# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

# Emits the server's NATIVE dependencies as JSON, for merging into the SBOM.
#
# Why this has to exist. The SBOM workflow runs Syft over the repository, and Syft can
# only report what it can see. Boost, OpenSSL and libpq are not in the repository - they
# are built separately and referenced through $(hMailServerLibs) - so a directory scan
# finds no trace of them. The result was a shipped SBOM listing 54 packages, every one of
# them .NET, for a product whose TLS is OpenSSL and whose entire socket layer is Boost
# Asio. Anyone using that SBOM the way an SBOM is meant to be used - checking a release
# against a CVE feed - would have concluded this server does not link OpenSSL at all.
# An SBOM that is silently missing the parts most likely to have a CVE is worse than no
# SBOM, because it answers the question wrongly instead of not answering it.
#
# Why it parses the project file rather than carrying a list. A hand-maintained list of
# versions is a second copy of the truth, and second copies rot: the day someone upgrades
# OpenSSL and forgets this file, the SBOM starts lying with full confidence. The include
# paths in hMailServer.vcxproj are what the compiler is actually given, so they cannot be
# stale while the build works - if the version in the path is wrong, the build fails
# rather than the SBOM.
#
# The licences are declared here because a path cannot carry one. They are checked against
# the shipped licence text by build/check-native-dependencies.ps1.

[CmdletBinding()]
param(
   # Where to write the JSON. Defaults to stdout, which is what CI uses.
   [string] $OutputFile
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# Built up a segment at a time rather than as one backslash-separated string, because this
# runs on the Linux runner in the SBOM workflow as well as on Windows, and a literal
# 'a\b\c' is one filename containing backslashes there rather than a path.
$projectFile = $repoRoot
foreach ($segment in @('hmailserver', 'source', 'Server', 'hMailServer', 'hMailServer.vcxproj')) {
   $projectFile = Join-Path $projectFile $segment
}

if (-not (Test-Path $projectFile)) {
   throw "Cannot find the server project file at $projectFile"
}

$project = Get-Content $projectFile -Raw

# Each entry: the regex that finds the version in an include or library path, and the
# metadata a path cannot carry. Anchored on the directory-name conventions the upstream
# projects themselves use, so an upgrade that follows those conventions is picked up with
# no change here.
$dependencies = @(
   @{
      Name    = 'openssl'
      Pattern = 'openssl-(\d+\.\d+\.\d+[a-z]?)'
      Purl    = 'pkg:generic/openssl@{0}'
      Cpe     = 'cpe:2.3:a:openssl:openssl:{0}:*:*:*:*:*:*:*'
      License = 'Apache-2.0'
      Website = 'https://www.openssl.org/'
      Why     = 'TLS for SMTP, POP3, IMAP and the REST API, plus DKIM signing and hashing'
   },
   @{
      Name    = 'boost'
      # boost_1_92_0 -> 1.92.0
      Pattern = 'boost_(\d+)_(\d+)_(\d+)'
      Purl    = 'pkg:generic/boost@{0}'
      Cpe     = 'cpe:2.3:a:boost:boost:{0}:*:*:*:*:*:*:*'
      License = 'BSL-1.0'
      Website = 'https://www.boost.org/'
      Why     = 'Asio for all socket I/O, plus threading, filesystem and string algorithms'
   },
   @{
      Name    = 'postgresql'
      Pattern = 'postgresql-(\d+\.\d+)'
      Purl    = 'pkg:generic/postgresql@{0}'
      Cpe     = 'cpe:2.3:a:postgresql:postgresql:{0}:*:*:*:*:*:*:*'
      License = 'PostgreSQL'
      Website = 'https://www.postgresql.org/'
      Why     = 'libpq, the client library for the PostgreSQL database backend'
   }
)

$found = @()
$missing = @()

foreach ($dependency in $dependencies) {
   $match = [regex]::Match($project, $dependency.Pattern)

   if (-not $match.Success) {
      $missing += $dependency.Name
      continue
   }

   # Boost's directory convention is underscores; everything else already reads as a
   # version. Joining the numeric groups keeps this general rather than special-casing
   # the name.
   $groups = @()
   for ($i = 1; $i -lt $match.Groups.Count; $i++) { $groups += $match.Groups[$i].Value }
   $version = $groups -join '.'

   $found += [pscustomobject]@{
      name    = $dependency.Name
      version = $version
      purl    = $dependency.Purl -f $version
      cpe     = $dependency.Cpe -f $version
      license = $dependency.License
      website = $dependency.Website
      why     = $dependency.Why
      source  = 'hMailServer.vcxproj include path'
   }
}

if ($missing.Count -gt 0) {
   # A hard failure, not a warning. This script exists to stop the SBOM omitting things
   # silently, so it must not itself omit things silently: if a dependency's path
   # convention changed, that is a two-minute fix to a regex above, and it should block
   # the release rather than quietly shrink the SBOM back to being wrong.
   throw ("Could not determine the version of: {0}. The include paths in hMailServer.vcxproj " +
          "no longer match the expected pattern. Fix the pattern in this script - do not " +
          "remove the dependency, because it is still linked." -f ($missing -join ', '))
}

$json = $found | ConvertTo-Json -Depth 4

if ($OutputFile) {
   $json | Set-Content -Path $OutputFile -Encoding UTF8
   Write-Host "Wrote $($found.Count) native dependencies to $OutputFile"
   foreach ($dependency in $found) { Write-Host ("  {0} {1}" -f $dependency.name, $dependency.version) }
}
else {
   $json
}
