# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

# Locates the clang-cl toolchain and, if necessary, imports the Visual Studio
# build environment into the current PowerShell session.
#
# Both halves are needed because of how clang-cl works on Windows:
#
#   - clang-cl is a *driver*, not a complete toolchain. It links with the MSVC
#     linker (link.exe) and against the MSVC CRT and Windows SDK import
#     libraries. Those come from the environment (PATH/INCLUDE/LIB) that
#     vcvars64.bat sets up. Running clang-cl from a bare PowerShell prompt
#     usually compiles and then fails at link with LNK1104 on libcmt or
#     kernel32, which reads like a code problem and is not one.
#   - clang-cl ships inside the Visual Studio installation ("C++ Clang tools for
#     Windows" in the installer, component VC.Llvm.Clang), not on PATH. That
#     component is also what provides the sanitizer runtimes - the ASan and
#     libFuzzer .lib/.dll files under lib\clang\<version>\lib\windows. A
#     standalone LLVM install from llvm.org works too, which is why PATH is
#     still checked as a fallback.
#
# Mirrors the shape of build\Find-MsBuild.ps1 deliberately: same vswhere lookup,
# same "return $null and let the caller report" contract.

function Find-ClangCl {
    param(
        # Empty = latest installed Visual Studio / Build Tools.
        [string]$VsWhereMinVersion = ''
    )

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

    $clang = $null
    if (Test-Path $vswhere) {
        try {
            $vswhereArgs = @('-latest', '-products', '*',
                             '-requires', 'Microsoft.VisualStudio.Component.VC.Llvm.Clang',
                             '-find', 'VC\Tools\Llvm\x64\bin\clang-cl.exe')
            if ($VsWhereMinVersion) { $vswhereArgs = @('-version', $VsWhereMinVersion) + $vswhereArgs }

            $clang = & $vswhere @vswhereArgs | Select-Object -First 1
        } catch {
            $clang = $null
        }
    } else {
        Write-Verbose "vswhere not found at $vswhere"
    }

    if (-not $clang) {
        $clangCmd = Get-Command clang-cl.exe -ErrorAction SilentlyContinue
        if ($clangCmd) { $clang = $clangCmd.Source }
    }

    return $clang
}

# Returns the directory holding the sanitizer runtime libraries for a given
# clang-cl, or $null. Layout is <toolchain root>\lib\clang\<version>\lib\windows.
# The version component moves with every VS update, so it is globbed rather than
# hard-coded.
function Find-ClangRuntimeDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$ClangClPath
    )

    # ...\VC\Tools\Llvm\x64\bin\clang-cl.exe -> ...\VC\Tools\Llvm\x64
    $toolchainRoot = Split-Path -Parent (Split-Path -Parent $ClangClPath)
    $libClang = Join-Path $toolchainRoot 'lib\clang'
    if (-not (Test-Path $libClang)) { return $null }

    $candidates = Get-ChildItem -Path $libClang -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending

    foreach ($candidate in $candidates) {
        $windowsDir = Join-Path $candidate.FullName 'lib\windows'
        if (Test-Path $windowsDir) { return $windowsDir }
    }

    return $null
}

# True when this clang-cl actually ships the libFuzzer and AddressSanitizer
# runtime libraries for x64.
#
# This check exists because of a real trap. Find-ClangCl falls back to PATH when
# the Visual Studio Clang component is absent, and on a developer machine PATH
# very often has a clang-cl from something else entirely - a Swift toolchain, a
# Chocolatey LLVM, an Android NDK. Those compile the code perfectly well and then
# fail at link with
#
#     LINK : fatal error LNK1181: cannot open input file 'clang_rt.fuzzer-x86_64.lib'
#
# which reads like a broken build script rather than "you installed the wrong
# clang". Better to say so before compiling anything.
function Test-ClangSanitizerRuntime {
    param(
        [Parameter(Mandatory = $true)][string]$ClangClPath
    )

    $runtimeDir = Find-ClangRuntimeDirectory -ClangClPath $ClangClPath
    if (-not $runtimeDir) { return $false }

    $fuzzer = @(Get-ChildItem -Path $runtimeDir -Filter 'clang_rt.fuzzer*x86_64.lib' -ErrorAction SilentlyContinue)
    $asan = @(Get-ChildItem -Path $runtimeDir -Filter 'clang_rt.asan*x86_64.lib' -ErrorAction SilentlyContinue)

    return ($fuzzer.Count -gt 0 -and $asan.Count -gt 0)
}

# True when the current process already has the MSVC build environment.
function Test-VsDevEnvironment {
    # VCToolsInstallDir is what vcvars64.bat sets, and the only reliable sign.
    # This used to accept "link.exe is on PATH" as well, and an LLVM or Swift
    # toolchain puts a link.exe on PATH without any of the MSVC library paths -
    # so the import was skipped, clang-cl found the CRT by its own detection,
    # and the link failed on stl_asan.lib, which lives only in the MSVC lib dir.
    return [bool]$env:VCToolsInstallDir
}

# Runs vcvars64.bat in a child cmd and copies the resulting environment into this
# session. This is the standard trick and it is used here rather than telling the
# operator "open a developer prompt first", because a fuzz build that only works
# from one particular shell is a fuzz build nobody runs.
function Import-VsDevEnvironment {
    if (Test-VsDevEnvironment) {
        Write-Verbose 'MSVC environment already present.'
        return $true
    }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        Write-Warning "vswhere not found at $vswhere - cannot import the MSVC environment."
        return $false
    }

    $vcvars = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
                         -find 'VC\Auxiliary\Build\vcvars64.bat' | Select-Object -First 1
    if (-not $vcvars) {
        Write-Warning 'vcvars64.bat not found - install the MSVC x64 build tools.'
        return $false
    }

    Write-Host "Importing MSVC environment from: $vcvars"

    # "call ... && set" prints the post-vcvars environment; anything that is not
    # NAME=VALUE is vcvars' own banner and is skipped.
    $output = & cmd.exe /c "call `"$vcvars`" >nul 2>&1 && set"
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "vcvars64.bat failed with exit code $LASTEXITCODE."
        return $false
    }

    foreach ($line in $output) {
        $separator = $line.IndexOf('=')
        if ($separator -lt 1) { continue }

        $name = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)

        # SetEnvironmentVariable rather than Set-Item Env:\<name>: several of the
        # variables vcvars emits have parentheses in their names
        # (ProgramFiles(x86), CommonProgramFiles(x86)), and those are provider
        # path syntax to Set-Item.
        [System.Environment]::SetEnvironmentVariable($name, $value)
    }

    return $true
}
