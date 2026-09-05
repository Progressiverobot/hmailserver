# Copyright (c) 2026 Martin Knafve / hMailServer.com.
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
.SYNOPSIS
    Builds a specific OpenSSL 4.x version for hMailServer.

.DESCRIPTION
    Downloads the OpenSSL source for the requested version into
    %hMailServerLibs%\openssl-<Version>, verifies it against the SHA-256 pinned below,
    then configures and builds it with the Visual Studio 2026 x64 toolchain into an
    "out64" install prefix, matching the layout hMailServer and libpq link against
    (out64\include, out64\lib, out64\bin with libcrypto-4-x64.dll / libssl-4-x64.dll).

    This is the README's recipe, unchanged: Configure for VC-WIN64A without assembler
    (so no NASM is needed), targeting Windows 10 (_WIN32_WINNT=0x0A00), then build and
    install only the libraries. The command-line openssl application is not built - it
    is not needed, and it has failed to compile in some 4.0.x source drops.

    Prerequisites (must be on PATH / installed):
      - The environment variable hMailServerLibs, pointing at your library folder.
      - Perl (e.g. Strawberry Perl) - required by OpenSSL's Configure.
      - Visual Studio 2026 with the x64 build tools (vcvars64.bat is located
        automatically via vswhere).

.PARAMETER Version
    The OpenSSL version to build, e.g. 4.0.2. Must match 4.x.y.

.PARAMETER Sha256
    The SHA-256 of openssl-<Version>.tar.gz, for a version the digest table below does
    not know yet. Take it from the .sha256 file published beside the release.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File libraries\build-openssl.ps1 -Version 4.0.2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^4\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$Sha256
)

$ErrorActionPreference = "Stop"

# Handle native-command exit codes explicitly (checked after each step) rather than
# letting a nonzero exit or stderr text abort the pipeline on its own.
$PSNativeCommandUseErrorActionPreference = $false

# The SHA-256 of each release archive, from the openssl-<version>.tar.gz.sha256 file
# published on https://github.com/openssl/openssl/releases. See Get-PinnedDigest.
$Digests = @{
    '4.0.1' = '2db3f3a0d6ea4b59e1f094ace2c8cd536dffb87cdc39084c5afa1e6f7f37dd09'
    '4.0.2' = '736b467530f916737b7031310ccb21d8218c6229e61e8e160cd1d3458cd543a8'
}

# --- Set up a build log ---------------------------------------------------------

# OpenSSL's Configure/nmake output is verbose and the host console may buffer it, so
# mirror every step to build-openssl.log next to this script. This gives a file you can
# watch live from another shell to confirm the build is progressing:
#
#     Get-Content libraries\build-openssl.log -Wait
#
# and a full transcript to inspect if a step fails.
. (Join-Path -Path $PSScriptRoot -ChildPath "build-common.ps1")

$logPath = Join-Path -Path $PSScriptRoot -ChildPath "build-openssl.log"
Start-BuildLog -LogPath $logPath -Title "OpenSSL $Version build log"

# --- Resolve the library folder -------------------------------------------------

$libsPath = Resolve-HMailServerLibs

$srcDir = Join-Path -Path $libsPath -ChildPath "openssl-$Version"
$outDir = Join-Path -Path $srcDir -ChildPath "out64"

# --- Locate the Visual Studio build environment via vswhere --------------------

$vsInstall = Resolve-VcVars64

# --- Verify a Windows-native Perl is available ----------------------------------

Resolve-NativePerl

# --- Download, verify and extract the source (always a clean tree) --------------

$digest = Get-PinnedDigest -Digests $Digests -Version $Version -Library 'OpenSSL' -Override $Sha256
$tarUrl = "https://github.com/openssl/openssl/releases/download/openssl-$Version/openssl-$Version.tar.gz"
Get-SourceArchive -Url $tarUrl -SrcDir $srcDir -LibsPath $libsPath -Sha256 $digest

# --- Import the VS x64 build environment ---------------------------------------

Import-VsEnvironment -VsInstall $vsInstall

# --- Configure and build (each step checked individually) ----------------------

Write-Log "Building OpenSSL $Version (this takes ten to twenty minutes; nmake is single-threaded)"
Write-Log "Progress is being logged to $logPath (tail it with: Get-Content `"$logPath`" -Wait)"

Push-Location $srcDir
try
{
    Invoke-BuildStep "Configuring OpenSSL $Version for target VC-WIN64A" {
        perl Configure no-asm VC-WIN64A "--prefix=$outDir" "--openssldir=$outDir" '-D_WIN32_WINNT=0x0A00'
    }
    if ($LastExitCode -ne 0)
    {
        Throw "OpenSSL 'perl Configure' failed with exit code $LastExitCode. See $logPath for details."
    }

    Invoke-BuildStep "Compiling the libraries (nmake build_libs)" {
        nmake build_libs
    }
    if ($LastExitCode -ne 0)
    {
        Throw "OpenSSL 'nmake build_libs' failed with exit code $LastExitCode. See $logPath for details."
    }

    Invoke-BuildStep "Installing the headers, import libraries and DLLs (nmake install_dev install_runtime_libs)" {
        nmake install_dev install_runtime_libs
    }
    if ($LastExitCode -ne 0)
    {
        Throw "OpenSSL 'nmake install_dev install_runtime_libs' failed with exit code $LastExitCode. See $logPath for details."
    }
}
finally
{
    Pop-Location
}

# --- Verify the expected output -------------------------------------------------

# The DLL names carry the major version: libcrypto-4-x64.dll for 4.x.
$major = $Version.Split('.')[0]

$expected = @(
    "bin\libcrypto-$major-x64.dll",
    "bin\libssl-$major-x64.dll",
    "lib\libcrypto.lib",
    "lib\libssl.lib",
    "include\openssl\ssl.h"
) | ForEach-Object { Join-Path -Path $outDir -ChildPath $_ }

foreach ($item in $expected)
{
    if (!(Test-Path $item))
    {
        Throw "Build completed but expected output was missing: $item"
    }
}

Write-Log "OpenSSL $Version built successfully into $outDir"
