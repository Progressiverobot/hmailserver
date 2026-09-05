# Copyright (c) 2026 Martin Knafve / hMailServer.com.
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
.SYNOPSIS
    Builds a specific Boost version for hMailServer.

.DESCRIPTION
    Downloads the Boost source for the requested version into
    %hMailServerLibs%\boost_<underscored-Version> (e.g. boost_1_91_0), verifies it
    against the SHA-256 pinned below, bootstraps b2, and builds the static,
    multithreaded x64 libraries hMailServer links against into stage\lib, matching the
    layout the project expects (boost_<ver>\stage\lib for libs, boost_<ver> itself for
    headers).

    Only the subset of Boost libraries hMailServer uses is built: thread, filesystem,
    regex, chrono and atomic. Everything else it uses is header-only (Boost.System
    included, since 1.69) and needs no compilation. Both the debug and the release
    variant are staged, so a Debug build of the server links as well as a Release one.

    Prerequisites (must be on PATH / installed):
      - The environment variable hMailServerLibs, pointing at your library folder.
      - Visual Studio 2026 with the x64 build tools (vcvars64.bat is located
        automatically via vswhere). b2 is driven with the msvc-14.5 toolset, the v145
        compiler hMailServer's own projects are built with.

.PARAMETER Version
    The Boost version to build, e.g. 1.91.0. Must match 1.x.y.

.PARAMETER Toolset
    The b2 toolset to build with. Defaults to msvc-14.5, the v145 toolset hMailServer's
    project files expect. The library names carry it (libboost_thread-vc145-...), and
    the auto-linking pragma in hMailServer's build looks for exactly that tag.

.PARAMETER Jobs
    Number of parallel compilations (b2 -j). Defaults to the number of logical
    processors.

.PARAMETER Sha256
    The SHA-256 of boost_<underscored-Version>.tar.gz, for a version the digest table
    below does not know yet. Take it from the .json file published beside the archive.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File libraries\build-boost.ps1 -Version 1.91.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^1\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory = $false)]
    [ValidatePattern('^msvc-\d+\.\d+$')]
    [string]$Toolset = "msvc-14.5",

    [Parameter(Mandatory = $false)]
    [int]$Jobs = [int]$env:NUMBER_OF_PROCESSORS,

    [Parameter(Mandatory = $false)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$Sha256
)

$ErrorActionPreference = "Stop"

# Handle native-command exit codes explicitly (checked after each step) rather than
# letting a nonzero exit or stderr text abort the pipeline on its own.
$PSNativeCommandUseErrorActionPreference = $false

# The SHA-256 of each release archive, from the boost_<version>.tar.gz.json file
# published beside it on https://archives.boost.io/release/. See Get-PinnedDigest.
$Digests = @{
    '1.91.0' = '5734305f40a76c30f951c9abd409a45a2a19fb546efe4162119250bbe4d3a463'
}

# --- Set up a build log ---------------------------------------------------------

# b2's compile output is verbose and the host console may buffer it, so mirror every
# step to build-boost.log next to this script. This gives a file you can watch live from
# another shell to confirm the build is progressing:
#
#     Get-Content libraries\build-boost.log -Wait
#
# and a full transcript to inspect if a step fails.
. (Join-Path -Path $PSScriptRoot -ChildPath "build-common.ps1")

$logPath = Join-Path -Path $PSScriptRoot -ChildPath "build-boost.log"
Start-BuildLog -LogPath $logPath -Title "Boost $Version build log"

# --- Resolve the library folder -------------------------------------------------

$libsPath = Resolve-HMailServerLibs

# Boost's source folder / tarball use underscores (boost_1_91_0), not dots.
$underscored = $Version -replace '\.', '_'
$srcDir = Join-Path -Path $libsPath -ChildPath "boost_$underscored"

if ($Jobs -lt 1)
{
    $Jobs = 4
}

# The tag b2 puts into the library names for this toolset: msvc-14.5 -> vc145. It is
# what the build below is checked against, because a library with any other tag is one
# hMailServer's auto-linking pragma will never find.
$libraryTag = 'vc' + (($Toolset -replace '^msvc-', '') -replace '\.', '')

# --- Locate the Visual Studio build environment via vswhere --------------------

# Boost must be compiled with the toolset hMailServer is compiled with (see
# Resolve-VcVars64 for why 'vswhere -latest' is the wrong question): its static libraries
# end up inside hMailServer.exe, and the auto-linking pragma encodes the toolset in the
# library name.
$vsInstall = Resolve-VcVars64

# --- Download, verify and extract the source (always a clean tree) --------------

$digest = Get-PinnedDigest -Digests $Digests -Version $Version -Library 'Boost' -Override $Sha256
$tarUrl = "https://archives.boost.io/release/$Version/source/boost_$underscored.tar.gz"
Get-SourceArchive -Url $tarUrl -SrcDir $srcDir -LibsPath $libsPath -Sha256 $digest

# --- Import the VS x64 build environment ---------------------------------------

Import-VsEnvironment -VsInstall $vsInstall

# --- Bootstrap and build (each step checked individually) -----------------------

Write-Log "Building Boost $Version with toolset $Toolset (this can take several minutes)"
Write-Log "Progress is being logged to $logPath (tail it with: Get-Content `"$logPath`" -Wait)"

# Boost's bootstrap.bat / b2 are invoked from the source directory and rely on cmd
# resolving batch files (bootstrap.bat, its internal guess_toolset.bat, .\b2) from the
# current directory. If NoDefaultCurrentDirectoryInExePath is set in the environment, cmd
# refuses to search the cwd and every such call fails with "is not recognized as an
# internal or external command". Clear it for this process (and the child cmd/b2
# processes that inherit it) so the build works regardless of the host's setting.
Remove-Item Env:\NoDefaultCurrentDirectoryInExePath -ErrorAction SilentlyContinue

Push-Location $srcDir
try
{
    Invoke-BuildStep "Bootstrapping b2" {
        cmd /c "bootstrap.bat"
    }
    if ($LastExitCode -ne 0)
    {
        Throw "Boost 'bootstrap.bat' failed with exit code $LastExitCode. See $logPath for details."
    }

    # Build only the compiled libraries hMailServer links against, as static,
    # multithreaded, x64, in both variants. Intermediate build output goes to out64; the
    # finished libraries are staged into stage\lib (what the project references).
    # BOOST_USE_WINAPI_VERSION matches the _WIN32_WINNT the server and OpenSSL are built
    # for, so Boost does not fall back to pre-Windows-10 code paths.
    Invoke-BuildStep "Compiling the Boost libraries (b2 stage)" {
        $b2Arguments = @(
            'debug', 'release', 'threading=multi', 'link=static',
            '--with-thread', '--with-filesystem', '--with-regex', '--with-chrono', '--with-atomic',
            "toolset=$Toolset", 'address-model=64', 'stage', '--build-dir=out64', '-j', $Jobs,
            'define=BOOST_USE_WINAPI_VERSION=0x0A00'
        )

        .\b2 @b2Arguments
    }
    if ($LastExitCode -ne 0)
    {
        Throw "Boost 'b2 stage' failed with exit code $LastExitCode. See $logPath for details."
    }
}
finally
{
    Pop-Location
}

# --- Verify the expected output -------------------------------------------------

$stageLib = Join-Path -Path $srcDir -ChildPath "stage\lib"
$boostHeaders = Join-Path -Path $srcDir -ChildPath "boost"

if (!(Test-Path $boostHeaders))
{
    Throw "Build completed but the Boost headers folder was missing: $boostHeaders"
}

if (!(Test-Path $stageLib))
{
    Throw "Build completed but the staged library folder was missing: $stageLib"
}

# The staged libs are named like libboost_thread-vc145-mt-x64-1_91.lib (release) and
# libboost_thread-vc145-mt-gd-x64-1_91.lib (debug). Confirm each requested library
# produced both, with the tag the server's auto-linking pragma looks for.
$expectedLibs = @("thread", "filesystem", "regex", "chrono", "atomic")
foreach ($lib in $expectedLibs)
{
    foreach ($variant in @("mt", "mt-gd"))
    {
        $pattern = "libboost_$lib-$libraryTag-$variant-x64-*.lib"
        $found = @(Get-ChildItem -Path $stageLib -Filter $pattern -ErrorAction SilentlyContinue)
        if ($found.Count -eq 0)
        {
            $present = (Get-ChildItem -Path $stageLib -Filter "libboost_$lib-*.lib" -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ', '
            Throw "Build completed but no staged library matched $pattern in $stageLib. Libraries staged for boost_${lib}: $present. Boost was built with a toolset other than $Toolset."
        }
    }
}

Write-Log "Boost $Version built successfully. Headers: $boostHeaders  Libs: $stageLib"
