# Copyright (c) 2026 Martin Knafve / hMailServer.com.
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
.SYNOPSIS
    Builds libpq from a specific PostgreSQL version for hMailServer.

.DESCRIPTION
    Downloads the PostgreSQL source for the requested version into
    %hMailServerLibs%\postgresql-<Version>, verifies it against the SHA-256 pinned
    below, configures it with Meson against a previously built OpenSSL, and builds only
    libpq with the Visual Studio 2026 x64 toolchain. The result is the layout hMailServer
    links against: postgresql-<Version>\builddir\src\interfaces\libpq (libpq.dll /
    libpq.lib) plus the libpq-fe.h header under src\interfaces\libpq and postgres_ext.h
    under src\include.

    PostgreSQL 17 removed the src\tools\msvc build system in favour of Meson, so only
    Meson-era versions (17 and later) are supported.

    Every optional dependency is switched off (-Dauto_features=disabled) and OpenSSL is
    switched on by name. The point is that the libpq.dll this produces depends on
    libssl-4-x64.dll and libcrypto-4-x64.dll from the OpenSSL built by
    build-openssl.ps1, and on nothing else that would have to be shipped beside it.
    That is checked after the build, with dumpbin, rather than assumed.

    Prerequisites (must be on PATH / installed):
      - The environment variable hMailServerLibs, pointing at your library folder.
      - A previously built OpenSSL under %hMailServerLibs%\openssl-<OpenSSLVersion>\out64
        (build it with build-openssl.ps1). Without it libpq cannot make encrypted
        connections to PostgreSQL, so it is required rather than optional.
      - Perl (e.g. Strawberry Perl) - PostgreSQL's build generates sources with it.
      - Python with Meson and Ninja (py -m pip install meson ninja).
      - flex and bison, as win_flex.exe and win_bison.exe from winflexbison
        (https://github.com/lexxmark/winflexbison/releases) on PATH.
      - Visual Studio 2026 with the x64 build tools (vcvars64.bat is located
        automatically via vswhere).

.PARAMETER Version
    The PostgreSQL version to build, e.g. 18.3. Must be 17.x or later.

.PARAMETER OpenSSLVersion
    The OpenSSL version to link libpq against, e.g. 4.0.1. Must correspond to an existing
    %hMailServerLibs%\openssl-<OpenSSLVersion>\out64 build. If omitted, the script
    auto-detects it from hMailServer.vcxproj (the openssl-<ver> the project currently
    links against).

.PARAMETER Sha256
    The SHA-256 of postgresql-<Version>.tar.gz, for a version the digest table below
    does not know yet. Take it from the .sha256 file published beside the archive.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File libraries\build-pgsql.ps1 -Version 18.3 -OpenSSLVersion 4.0.1

.EXAMPLE
    # Auto-detect the OpenSSL version from hMailServer.vcxproj:
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File libraries\build-pgsql.ps1 -Version 18.3
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(1[7-9]|[2-9]\d)\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$OpenSSLVersion,

    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$Sha256
)

$ErrorActionPreference = "Stop"

# Handle native-command exit codes explicitly (checked after each step) rather than
# letting a nonzero exit or stderr text abort the pipeline on its own.
$PSNativeCommandUseErrorActionPreference = $false

# The SHA-256 of each release archive, from the postgresql-<version>.tar.gz.sha256 file
# published beside it on https://ftp.postgresql.org/pub/source/. See Get-PinnedDigest.
$Digests = @{
    '18.3' = '9e054ffd6e013da2c2c9a1bfd6e062c98875d340df080516551c96b9b0926a59'
}

# --- Set up a build log ---------------------------------------------------------

# Meson's and ninja's output is verbose and the host console may buffer it, so mirror
# every step to build-pgsql.log next to this script. This gives a file you can watch
# live from another shell to confirm the build is progressing:
#
#     Get-Content libraries\build-pgsql.log -Wait
#
# and a full transcript to inspect if a step fails.
. (Join-Path -Path $PSScriptRoot -ChildPath "build-common.ps1")

$logPath = Join-Path -Path $PSScriptRoot -ChildPath "build-pgsql.log"
Start-BuildLog -LogPath $logPath -Title "PostgreSQL $Version (libpq) build log"

# --- Resolve the library folder -------------------------------------------------

$libsPath = Resolve-HMailServerLibs

$srcDir = Join-Path -Path $libsPath -ChildPath "postgresql-$Version"
$buildDir = Join-Path -Path $srcDir -ChildPath "builddir"
$libpqDir = Join-Path -Path $buildDir -ChildPath "src\interfaces\libpq"

# --- Resolve the OpenSSL build to link against ----------------------------------

# If the caller did not pin an OpenSSL version, auto-detect the one the project currently
# links against from hMailServer.vcxproj (openssl-<ver>\out64). This keeps libpq's SSL
# backend in lockstep with the rest of hMailServer by default.
if ([string]::IsNullOrEmpty($OpenSSLVersion))
{
    $vcxproj = Join-Path -Path $PSScriptRoot -ChildPath "..\hmailserver\source\Server\hMailServer\hMailServer.vcxproj"
    if (!(Test-Path $vcxproj))
    {
        Throw "OpenSSLVersion was not supplied and hMailServer.vcxproj was not found at $vcxproj to auto-detect it. Pass -OpenSSLVersion explicitly."
    }
    $match = Select-String -Path $vcxproj -Pattern 'openssl-(\d+\.\d+\.\d+)' | Select-Object -First 1
    if ($null -eq $match)
    {
        Throw "Could not auto-detect the OpenSSL version from $vcxproj. Pass -OpenSSLVersion explicitly."
    }
    $OpenSSLVersion = $match.Matches[0].Groups[1].Value
    Write-Log "Auto-detected OpenSSL version $OpenSSLVersion from hMailServer.vcxproj"
}

$openSslOut = Join-Path -Path $libsPath -ChildPath "openssl-$OpenSSLVersion\out64"
$openSslMajor = $OpenSSLVersion.Split('.')[0]

if (!(Test-Path (Join-Path -Path $openSslOut -ChildPath "lib\libssl.lib")))
{
    Throw "The OpenSSL build to link libpq against was not found at $openSslOut. Build it first with build-openssl.ps1 -Version $OpenSSLVersion. Without it libpq would be built without SSL support."
}

# --- Locate the Visual Studio build environment via vswhere --------------------

$vsInstall = Resolve-VcVars64

# --- Verify the build tools are available ---------------------------------------

# Named here rather than discovered by Meson twenty minutes in: each missing tool fails
# now with what to install. The CI action installs the last three itself; a developer
# follows the README.
Resolve-NativePerl

$tools = @(
    @{ Names = @('meson');              Hint = "Meson - py -m pip install meson" },
    @{ Names = @('ninja');              Hint = "Ninja - py -m pip install ninja" },
    @{ Names = @('win_flex', 'flex');   Hint = "flex - win_flex.exe from https://github.com/lexxmark/winflexbison/releases on PATH" },
    @{ Names = @('win_bison', 'bison'); Hint = "bison - win_bison.exe from https://github.com/lexxmark/winflexbison/releases on PATH" }
)

foreach ($tool in $tools)
{
    $found = $tool.Names | Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
    if (-not $found)
    {
        Throw "$($tool.Names[0]) was not found on PATH. Install $($tool.Hint)"
    }
}

# --- Download, verify and extract the source (always a clean tree) --------------

$digest = Get-PinnedDigest -Digests $Digests -Version $Version -Library 'PostgreSQL' -Override $Sha256
$tarUrl = "https://ftp.postgresql.org/pub/source/v$Version/postgresql-$Version.tar.gz"
Get-SourceArchive -Url $tarUrl -SrcDir $srcDir -LibsPath $libsPath -Sha256 $digest

if (!(Test-Path (Join-Path -Path $srcDir -ChildPath "meson.build")))
{
    Throw "No meson.build in $srcDir. PostgreSQL $Version does not use the Meson build system; only 17.x and later are supported."
}

# --- Import the VS x64 build environment ---------------------------------------

# libpq is a C library consumed through an import library and a DLL, so its ABI does not
# depend on the toolset; the Visual Studio 2026 default is used.
Import-VsEnvironment -VsInstall $vsInstall

# Meson picks the first C compiler it finds. Strawberry Perl puts a gcc on PATH, and a
# libpq compiled by it would carry a MinGW runtime the server does not ship - so cl is
# named outright, as the README's recipe does.
$env:CC = 'cl'

# Meson looks OpenSSL up through pkg-config and CMake before it falls back to the extra
# include and library directories below. A machine with another OpenSSL installed - the
# GitHub runner images carry a 3.x under Program Files - would have libpq silently
# linked against that one, and the DLL would then fail to load beside hMailServer.exe.
# Both lookups are pointed at our build first; the dumpbin check at the end is the
# proof that it worked.
$env:OPENSSL_ROOT_DIR = $openSslOut
$env:PKG_CONFIG_PATH = Join-Path -Path $openSslOut -ChildPath "lib\pkgconfig"

# --- Configure and build libpq --------------------------------------------------

Write-Log "Building libpq from PostgreSQL $Version against OpenSSL $OpenSSLVersion (this takes a few minutes)"
Write-Log "Progress is being logged to $logPath (tail it with: Get-Content `"$logPath`" -Wait)"

Push-Location $srcDir
try
{
    Invoke-BuildStep "Configuring PostgreSQL $Version with Meson (release, OpenSSL only)" {
        meson setup builddir --buildtype=release -Dssl=openssl -Dauto_features=disabled "-Dextra_include_dirs=$openSslOut\include" "-Dextra_lib_dirs=$openSslOut\lib"
    }
    if ($LastExitCode -ne 0)
    {
        Throw "PostgreSQL 'meson setup' failed with exit code $LastExitCode. See $logPath for details."
    }

    Invoke-BuildStep "Compiling libpq (meson compile src/interfaces/libpq/libpq:shared_library)" {
        meson compile -C builddir src/interfaces/libpq/libpq:shared_library
    }
    if ($LastExitCode -ne 0)
    {
        Throw "PostgreSQL 'meson compile' failed with exit code $LastExitCode. See $logPath for details."
    }
}
finally
{
    Pop-Location
}

# --- Verify the expected output -------------------------------------------------

$libpqDll = Join-Path -Path $libpqDir -ChildPath "libpq.dll"

$expected = @(
    $libpqDll,
    (Join-Path -Path $libpqDir -ChildPath "libpq.lib"),
    (Join-Path -Path $srcDir -ChildPath "src\interfaces\libpq\libpq-fe.h"),
    (Join-Path -Path $srcDir -ChildPath "src\include\postgres_ext.h")
)

foreach ($item in $expected)
{
    if (!(Test-Path $item))
    {
        Throw "Build completed but expected output was missing: $item"
    }
}

# The DLL must import the OpenSSL that was just built, not another one Meson found.
$dependents = & dumpbin /nologo /dependents $libpqDll | Out-String
if ($LastExitCode -ne 0)
{
    Throw "dumpbin /dependents failed with exit code $LastExitCode on $libpqDll."
}

foreach ($dll in @("libssl-$openSslMajor-x64.dll", "libcrypto-$openSslMajor-x64.dll"))
{
    if ($dependents -notmatch [regex]::Escape($dll))
    {
        Throw "libpq.dll does not import $dll. Meson linked it against a different OpenSSL than $openSslOut. Its imports were:`n$dependents"
    }
}

Write-Log "libpq from PostgreSQL $Version built successfully into $libpqDir, linked against OpenSSL $OpenSSLVersion"
