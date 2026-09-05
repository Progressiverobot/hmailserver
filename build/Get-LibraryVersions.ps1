# Copyright (c) 2026 Martin Knafve / hMailServer.com.
# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
.SYNOPSIS
    Reports the third-party library versions hMailServer is currently pinned to.

.DESCRIPTION
    The versions of OpenSSL, Boost and PostgreSQL (libpq) that hMailServer builds against
    are not recorded in one place: they are embedded in the include and library paths of
    hMailServer.vcxproj. This script reads them back out, so callers do not have to
    hard-code a version that then has to be remembered on every upgrade.

    The CI build uses it for the cache keys of the prebuilt libraries (so bumping a
    version invalidates exactly the caches that depend on it) and for the paths of the
    runtime DLLs that go into the build artifact. A version bump is therefore an edit to
    the project file and to the digest table of the matching libraries\build-*.ps1, and
    the workflow follows on its own.

    Output is one 'name=value' line per value, and, when running inside GitHub Actions
    ($env:GITHUB_OUTPUT set), the same lines are appended there as step outputs:

        openssl=4.0.2               openssl_dir=openssl-4.0.2
        boost=1.92.0                boost_dir=boost_1_92_0
        postgresql=18.3             postgresql_dir=postgresql-18.3

    The *_dir values are the folder names under %hMailServerLibs% that the build scripts
    create, which is what the workflow caches.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\Get-LibraryVersions.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName
$vcxproj = Join-Path -Path $repoRoot -ChildPath "hmailserver\source\Server\hMailServer\hMailServer.vcxproj"

if (!(Test-Path $vcxproj))
{
    Throw "Expected file not found: $vcxproj"
}

$vcxprojText = Get-Content -Path $vcxproj -Raw

# Pull the first match of $Pattern out of $Text, failing loudly rather than returning an
# empty version that would silently produce a wrong cache key or DLL path.
function Get-PinnedVersion
{
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][string]$Source
    )

    $match = [regex]::Match($Text, $Pattern)
    if (-not $match.Success)
    {
        Throw "Could not determine the $Description version from $Source (pattern: $Pattern)."
    }

    return $match.Groups[1].Value
}

# The same version strings the library build scripts and the project file use:
# openssl-4.0.2, boost_1_92_0 and postgresql-18.3 in the project's include and library
# paths.
$openssl = Get-PinnedVersion -Text $vcxprojText -Pattern 'openssl-(\d+\.\d+\.\d+)' -Description 'OpenSSL' -Source $vcxproj
$boostUnderscored = Get-PinnedVersion -Text $vcxprojText -Pattern 'boost_(\d+_\d+_\d+)' -Description 'Boost' -Source $vcxproj
$postgresql = Get-PinnedVersion -Text $vcxprojText -Pattern 'postgresql-(\d+\.\d+)' -Description 'PostgreSQL' -Source $vcxproj

# Boost is pinned as a folder name with underscores (boost_1_92_0); build-boost.ps1 takes
# the dotted form.
$boost = $boostUnderscored -replace '_', '.'

$values = [ordered]@{
    openssl        = $openssl
    openssl_dir    = "openssl-$openssl"
    boost          = $boost
    boost_dir      = "boost_$boostUnderscored"
    postgresql     = $postgresql
    postgresql_dir = "postgresql-$postgresql"
}

foreach ($name in $values.Keys)
{
    "$name=$($values[$name])"
}

if ($env:GITHUB_OUTPUT)
{
    foreach ($name in $values.Keys)
    {
        Add-Content -Path $env:GITHUB_OUTPUT -Value "$name=$($values[$name])"
    }
}
