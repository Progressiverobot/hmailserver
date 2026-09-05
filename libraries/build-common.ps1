# Copyright (c) 2026 Martin Knafve / hMailServer.com.
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
.SYNOPSIS
    Shared helpers for the hMailServer library build scripts.

.DESCRIPTION
    Dot-sourced by build-openssl.ps1, build-boost.ps1 and build-pgsql.ps1. Provides:

      - One build log per script, mirrored to a file next to the script and to the
        console, plus a helper that runs a native build step with its stdout and stderr
        captured to that log: Start-BuildLog, Write-Log, Invoke-BuildStep.
      - The build-environment plumbing every script shares: Resolve-HMailServerLibs,
        Resolve-VcVars64, Import-VsEnvironment, Get-PinnedDigest and Get-SourceArchive.

    Usage from a build script:

        . (Join-Path -Path $PSScriptRoot -ChildPath "build-common.ps1")
        Start-BuildLog -LogPath (Join-Path $PSScriptRoot "build-openssl.log") -Title "OpenSSL 4.0.1 build log"
        $libsPath = Resolve-HMailServerLibs
        $vsInstall = Resolve-VcVars64
        Get-SourceArchive -Url $tarUrl -SrcDir $srcDir -LibsPath $libsPath -Sha256 $digest
        Import-VsEnvironment -VsInstall $vsInstall
        Invoke-BuildStep "Compiling" { nmake build_libs }
        if ($LastExitCode -ne 0) { Throw "..." }

    The log path and encoding are held in script scope. Because this file is dot-sourced,
    those variables and the functions live in the caller's script scope, so Start-BuildLog
    and the helpers all share the same state.

    Ported from upstream hMailServer (hmailserver/hmailserver, pull request 592), whose
    scripts build with the v142 toolset out of Visual Studio 2019, 2022 or 2026. This
    fork's projects are pinned to v145, the compiler Visual Studio 2026 ships by default,
    so the toolset-pinning half of the upstream helpers is not here: the Visual Studio
    these helpers resolve is the one whose default compiler the projects ask for. What is
    here that upstream does not have is the digest check in Get-SourceArchive.
#>

# All log writes use this one encoding. Under Windows PowerShell 5.1 the various file
# cmdlets default to *different* encodings (Set-Content/Add-Content -> ANSI, Tee-Object
# -FilePath -> UTF-16LE), so mixing them produces a log where some lines render with a
# NUL between every character. Pin everything to UTF-8.
$script:BuildLogEncoding = "UTF8"
$script:BuildLogPath = $null

# Initialize the build log: record its path and write the header line. Call once before
# Write-Log / Invoke-BuildStep.
function Start-BuildLog
{
    param(
        [Parameter(Mandatory = $true)][string]$LogPath,
        [Parameter(Mandatory = $true)][string]$Title
    )
    $script:BuildLogPath = $LogPath
    Set-Content -Path $LogPath -Value "$Title - started $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding $script:BuildLogEncoding
}

# Write a message to both the console and the log file.
function Write-Log
{
    param([string]$Message)
    Write-Host $Message
    Add-Content -Path $script:BuildLogPath -Value $Message -Encoding $script:BuildLogEncoding
}

# Run a build step, mirroring its stdout and stderr to the console and the log file. The
# step's native exit code is left in $LastExitCode for the caller to check.
function Invoke-BuildStep
{
    param(
        [string]$Description,
        [scriptblock]$Command
    )
    Write-Log $Description
    Add-Content -Path $script:BuildLogPath -Value "----- $Description -----" -Encoding $script:BuildLogEncoding
    # Merge the step's stderr into the output stream so it is logged too. Native tools
    # (nmake, the compiler invoked by b2 and ninja) legitimately write progress and
    # warnings to stderr; under $ErrorActionPreference='Stop' a 2>&1-redirected stderr
    # line is otherwise turned into a terminating NativeCommandError before we can inspect
    # the exit code. Force Continue for just this pipeline; the caller still gates on
    # $LastExitCode.
    #
    # Deliberately NOT 'Tee-Object -FilePath': on Windows PowerShell 5.1 it has no
    # -Encoding switch and always writes UTF-16LE, which corrupts a log the rest of the
    # script writes as UTF-8. Instead echo each line to the console and append it to the
    # log with the shared encoding.
    #
    # The 2>&1 stream carries stdout lines as plain strings but stderr lines as
    # ErrorRecords (native tools such as cl.exe write the current source file name to
    # stderr). Casting such a record to [string] yields the useless text
    # "System.Management.Automation.RemoteException"; the real stderr text is in its
    # .Exception.Message, so pull that out explicitly.
    #
    # Write through a single StreamWriter held open for the whole step rather than an
    # Add-Content call per line: these builds emit many thousands of lines and a per-line
    # open/seek/close is needless disk churn. AutoFlush keeps the log tailable live
    # (Get-Content -Wait). UTF8Encoding($false) => no BOM, matching the UTF-8 the rest of
    # the script writes.
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $writer = New-Object System.IO.StreamWriter($script:BuildLogPath, $true, (New-Object System.Text.UTF8Encoding($false)))
    $writer.AutoFlush = $true
    try
    {
        & $Command 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord])
            {
                $line = $_.Exception.Message
            }
            else
            {
                $line = [string]$_
            }
            Write-Host $line
            $writer.WriteLine($line)
        }
    }
    finally
    {
        $writer.Close()
        $ErrorActionPreference = $prevEAP
    }
}

# Resolve and validate the hMailServerLibs library folder (where sources are built).
# Returns the path; throws with an actionable message if the variable is unset or the
# folder is missing.
function Resolve-HMailServerLibs
{
    $libsPath = $env:hMailServerLibs

    if ([string]::IsNullOrEmpty($libsPath))
    {
        Throw "The environment variable hMailServerLibs was not found. Please create it."
    }

    if (!(Test-Path $libsPath))
    {
        Throw "The environment variable hMailServerLibs was found, but the folder it was pointing at ($libsPath) was not. Please create it."
    }

    return $libsPath
}

# Locate a Visual Studio installation with the x64 C++ toolchain and return what the
# callers need from it: the vcvars64.bat path, the installation path and its version.
#
# We do NOT simply take 'vswhere -latest'. The Visual Studio in use decides which STL and
# CRT the libraries are compiled against, and Boost in particular has to match
# hMailServer's own toolset: its static libraries end up inside hMailServer.exe, and the
# auto-linking pragma encodes the toolset in the library name (libboost_thread-vc145-...).
# hMailServer.vcxproj is pinned to PlatformToolset v145, the default compiler of Visual
# Studio 2026 (18.x), so that is the only version accepted by default. It is also the only
# Visual Studio on the GitHub Actions windows-2025-vs2026 image.
#
# Pass $null or an empty array to fall back to 'vswhere -latest'.
function Resolve-VcVars64
{
    param(
        [Parameter(Mandatory = $false)]
        [string[]]$VersionRanges = @('[18.0,19.0)')
    )

    $vsWhere = Join-Path -Path ${env:ProgramFiles(x86)} -ChildPath "Microsoft Visual Studio\Installer\vswhere.exe"

    if (!(Test-Path $vsWhere))
    {
        Throw "vswhere.exe was not found at $vsWhere. Please install Visual Studio 2026 (or the Visual Studio Installer)."
    }

    $rangesToTry = if ($VersionRanges) { $VersionRanges } else { @($null) }

    $instance = $null
    foreach ($range in $rangesToTry)
    {
        $vsWhereArgs = @('-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-format', 'json')
        if ($range)
        {
            $vsWhereArgs += @('-version', $range)
        }
        else
        {
            $vsWhereArgs = @('-latest') + $vsWhereArgs
        }

        # -format json returns installationPath and installationVersion from a single
        # query, so the reported version can never describe a different install than the
        # path. vswhere prints '[]' when nothing matches, but guard against empty output
        # too: under Windows PowerShell 5.1, ConvertFrom-Json on an empty string is a
        # terminating error.
        $json = (& $vsWhere @vsWhereArgs | Out-String)
        if ([string]::IsNullOrWhiteSpace($json))
        {
            continue
        }

        $found = ($json | ConvertFrom-Json) | Select-Object -First 1
        if ($found)
        {
            $instance = $found
            break
        }
    }

    if (-not $instance)
    {
        $wanted = if ($VersionRanges) { " in version " + ($VersionRanges -join ' or ') } else { "" }
        Throw "No Visual Studio installation with the x64 C++ toolchain (VC.Tools.x86.x64$wanted) was found. hMailServer's projects need Visual Studio 2026 (PlatformToolset v145)."
    }

    $vcvars64 = Join-Path -Path $instance.installationPath -ChildPath "VC\Auxiliary\Build\vcvars64.bat"

    if (!(Test-Path $vcvars64))
    {
        Throw "vcvars64.bat was not found at $vcvars64."
    }

    return [PSCustomObject]@{
        VcVars64     = $vcvars64
        InstallPath  = $instance.installationPath
        Version      = $instance.installationVersion
        MajorVersion = [int]($instance.installationVersion -split '\.')[0]
    }
}

# Import the VS x64 build environment (PATH, INCLUDE, LIB, ...) from vcvars64.bat into
# this session so cl.exe, nmake, perl's Configure, b2, meson and ninja find the toolchain
# and the Windows SDK. Rather than chaining every build step into one 'cmd /c' (which
# collapses all failures into a single opaque exit code), the variables are imported once
# here and each build step is then run separately with its own exit-code check. vcvars'
# own stdout is discarded so only 'set' output is parsed; the '&&' ensures 'set' runs
# only if vcvars succeeded.
#
# No -vcvars_ver: the projects want the compiler Visual Studio 2026 selects by default
# (v145), so the default environment is the right one.
function Import-VsEnvironment
{
    param(
        [Parameter(Mandatory = $true)]
        [object]$VsInstall
    )

    # Accept either the object Resolve-VcVars64 returns or a bare vcvars64.bat path.
    $vcVars64 = if ($VsInstall -is [string]) { $VsInstall } else { $VsInstall.VcVars64 }

    Write-Log "Importing the VS x64 build environment from $vcVars64"

    $vcVarsOutput = cmd /c "call `"$vcVars64`" >nul 2>&1 && set"
    if ($LastExitCode -ne 0)
    {
        Throw "Failed to initialize the VS x64 build environment via $vcVars64 (exit code $LastExitCode)."
    }

    foreach ($line in $vcVarsOutput)
    {
        if ($line -match '^([^=]+)=(.*)$')
        {
            Set-Item -Path "Env:\$($matches[1])" -Value $matches[2]
        }
    }

    if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue))
    {
        Throw "cl.exe is not on PATH after importing $vcVars64. The Visual Studio installation has no x64 C++ compiler."
    }
}

# OpenSSL's Configure and PostgreSQL's source generation both run Perl, and both need a
# Windows-native one. Git for Windows carries an MSYS perl in usr\bin which, when it is
# the first perl on PATH, fails in the first minute: it writes paths as /c/..., and it
# lacks core modules the OpenSSL configuration loads (Locale::Maketext::Simple, for one).
# If the perl on PATH is that one, a Strawberry Perl installation is put ahead of it;
# without one the caller is told what to install rather than left to read Perl's
# @INC dump. Import-VsEnvironment inherits the adjusted PATH, so the order survives it.
function Resolve-NativePerl
{
    $perl = Get-Command perl.exe -ErrorAction SilentlyContinue
    $isMsys = $perl -and ($perl.Source -match '\\usr\\bin\\perl\.exe$')

    if ($perl -and -not $isMsys)
    {
        Write-Log "Perl: $($perl.Source)"
        return
    }

    $candidates = @(
        (Join-Path -Path $env:SystemDrive -ChildPath "Strawberry\perl\bin\perl.exe"),
        (Join-Path -Path $env:ProgramFiles -ChildPath "Strawberry\perl\bin\perl.exe"),
        (Join-Path -Path $env:SystemDrive -ChildPath "Perl64\bin\perl.exe"),
        (Join-Path -Path $env:SystemDrive -ChildPath "Perl\bin\perl.exe")
    )
    $native = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $native)
    {
        if ($perl)
        {
            Throw "The perl on PATH ($($perl.Source)) is the MSYS build that ships with Git for Windows, which cannot run OpenSSL's or PostgreSQL's build scripts. Install Strawberry Perl (https://strawberryperl.com/) or put a Windows-native Perl ahead of it on PATH."
        }
        Throw "Perl was not found on PATH. Install Strawberry Perl (https://strawberryperl.com/); OpenSSL's Configure and PostgreSQL's build both need it."
    }

    $env:PATH = (Split-Path -Path $native -Parent) + ";" + $env:PATH
    Write-Log "Perl: $native (put ahead of the MSYS perl at $($perl.Source))"
}

# The SHA-256 a build script pins for the archive of the version it is asked to build.
#
# A download that does not hash to the pinned value is deleted and the build stops: the
# archives come from the projects' own release servers over TLS, but a digest that lives
# in this repository is what makes the build reproducible, and it is what a review of a
# version bump actually checks. Bumping a version means adding its digest to the table in
# the script - taken from the project's published .sha256 file, not from the download.
# -Override lets a caller build a version the table does not know yet.
function Get-PinnedDigest
{
    param(
        [Parameter(Mandatory = $true)][hashtable]$Digests,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$Library,
        [Parameter(Mandatory = $false)][string]$Override
    )

    if ($Override)
    {
        return $Override.ToLowerInvariant()
    }

    if ($Digests.ContainsKey($Version))
    {
        return $Digests[$Version].ToLowerInvariant()
    }

    Throw "No SHA-256 digest is pinned for $Library $Version. Add it to the digest table in this script, taken from the project's published .sha256 file, or pass it with -Sha256."
}

# Fetch a source tarball, verify it against its pinned SHA-256 and extract it under
# $LibsPath, leaving the tree in $SrcDir.
#
# Every run starts from a clean tree: any existing $SrcDir is deleted first, then the
# archive is downloaded, verified, extracted and removed. This makes each build a full
# delete/download/verify/unzip/build with no reuse of stale, possibly cross-toolset output.
function Get-SourceArchive
{
    param(
        [Parameter(Mandatory = $true)]
        [string]$Url,

        [Parameter(Mandatory = $true)]
        [string]$SrcDir,

        [Parameter(Mandatory = $true)]
        [string]$LibsPath,

        [Parameter(Mandatory = $true)]
        [ValidatePattern('^[0-9a-fA-F]{64}$')]
        [string]$Sha256
    )

    if (Test-Path $SrcDir)
    {
        Write-Log "Removing existing source folder $SrcDir for a clean build"
        Remove-Item -LiteralPath $SrcDir -Recurse -Force
    }

    # Name the local tarball after the URL's file (e.g. boost_1_91_0.tar.gz).
    $tarPath = Join-Path -Path $LibsPath -ChildPath (Split-Path -Leaf $Url)

    Write-Log "Downloading $Url"
    # Windows PowerShell 5.1 redraws a progress bar for every chunk it receives, which
    # turns the 150 MB Boost download from seconds into minutes. Off for the download only.
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try
    {
        Invoke-WebRequest -Uri $Url -OutFile $tarPath -UseBasicParsing
    }
    finally
    {
        $ProgressPreference = $previousProgress
    }

    $actual = (Get-FileHash -Path $tarPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $Sha256.ToLowerInvariant())
    {
        Remove-Item $tarPath -Force
        Throw "The download of $Url does not match its pinned SHA-256 and has been deleted. Expected $($Sha256.ToLowerInvariant()), got $actual. If the project re-issued the archive, take the new digest from its published .sha256 file and update the pin."
    }
    Write-Log "SHA-256 verified: $actual"

    Write-Log "Extracting to $LibsPath"
    # Use the Windows-bundled bsdtar (System32\tar.exe) explicitly rather than a 'tar'
    # resolved from PATH: a GNU tar (e.g. from Git/MSYS) treats the "C:" in a "C:\..."
    # path as a remote rmt host ("Cannot connect to C: resolve failed"), whereas bsdtar
    # handles drive letters. The tarball extracts to $SrcDir.
    $tarExe = Join-Path -Path $env:SystemRoot -ChildPath "System32\tar.exe"
    if (!(Test-Path $tarExe))
    {
        Throw "The Windows-bundled tar.exe was not found at $tarExe. Windows 10/11 ships it; please install it or extract $tarPath manually."
    }
    & $tarExe -xzf $tarPath -C $LibsPath
    if ($LastExitCode -ne 0)
    {
        Throw "Extraction of $tarPath failed with error code $LastExitCode."
    }

    Remove-Item $tarPath -Force

    if (!(Test-Path $SrcDir))
    {
        Throw "Expected source folder $SrcDir was not found after extraction."
    }
}
