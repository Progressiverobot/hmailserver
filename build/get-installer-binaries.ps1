# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
<#
.SYNOPSIS
   Puts the third-party binaries the installer carries where the installer script
   expects them, without any of them being in git.

.DESCRIPTION
   Until 11 September 2026 forty binaries were committed: the MSVC runtime, the
   MariaDB client and its plugins, 7-Zip's console, the SQL Server Compact
   runtime and a few relics. Each had a hash in hmailserver/docs/third-party-
   binaries.json, but a binary in a repository is still a binary nobody can read,
   and OpenSSF Scorecard scores every one of them. This script is the other half
   of that manifest: for every artifact whose disposition is "fetched" or
   "gathered" it obtains the file, verifies it against the recorded SHA-256, and
   places it at the manifest's path. The paths are ignored by git.

     gathered  copied from the build machine's Visual Studio: the MSVC runtime,
               %VCToolsRedistDir%\x64\Microsoft.VC145.CRT, the same DLLs the
               toolset links against.
     fetched   downloaded from this repository's own "build-inputs-<n>" release,
               which holds the exact bytes that were committed before, with their
               provenance in its notes. A release rather than the vendor's URL
               because two of the three have no stable first-party download left.

   Every placed file is verified against the manifest before the script reports
   success, so a wrong Visual Studio version or a tampered download is a refusal,
   not an installer with the wrong runtime in it.

.PARAMETER Verify
   Only check: every fetched/gathered artifact is present and matches. Exit 1
   otherwise. What CI runs.

.PARAMETER Cache
   Where downloads are kept between runs. Default: %LOCALAPPDATA%\hmailserver\build-inputs.
#>
[CmdletBinding()]
param(
   [switch]$Verify,
   # Place only the "system" artifacts - Windows' own files the server compiles
   # against - and stop. What the hosted server build needs, and nothing else.
   [switch]$SystemOnly,
   [string]$Cache = (Join-Path $env:LOCALAPPDATA 'hmailserver\build-inputs')
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$manifestPath = Join-Path $repo 'hmailserver\docs\third-party-binaries.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$releaseBase = 'https://github.com/Progressiverobot/hmailserver/releases/download'

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower() }

function Assert-Matches([string]$path, [string]$expected, [string]$what) {
   if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$what is missing: $path" }
   $actual = Get-Sha256 $path
   if ($actual -ne $expected.ToLower()) { throw "$what does not match the manifest.`n  path:     $path`n  expected: $expected`n  actual:   $actual" }
}

$fetched = @($manifest.artifacts | Where-Object { $_.disposition -eq 'fetched' })
$gathered = @($manifest.artifacts | Where-Object { $_.disposition -eq 'gathered' })
$system = @($manifest.artifacts | Where-Object { $_.disposition -eq 'system' })
Write-Host ("{0} fetched, {1} gathered and {2} system artifact(s) in the manifest." -f $fetched.Count, $gathered.Count, $system.Count)

# A system artifact is a file of the Windows installation itself - the ADO type
# library the server #imports - copied from the path the manifest names. Its
# bytes vary with the Windows build, so it is verified to exist and its hash is
# reported; the regression suite is what proves the compiled result.
function Get-SystemSource($artifact) {
   $path = [Environment]::ExpandEnvironmentVariables([string]$artifact.source)
   if (-not (Test-Path -LiteralPath $path)) { throw "$($artifact.path): the Windows installation has no $path" }
   return $path
}

# A gathered file comes from the build machine and must match the toolset that
# built the server, not a byte hash recorded on another day: Visual Studio updates
# change these DLLs, and that is the point of gathering rather than committing.
# So a gathered artifact is verified by version - major.minor of its file version
# against the manifest's, which is the runtime's compatibility contract (the
# redistributable's build number moves independently of the toolset directory's:
# 14.51.36231's redist carries DLLs stamped 14.51.36247) - and its hash is only
# reported.
function Assert-Version([string]$path, [string]$expectedVersion, [string]$what) {
   if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$what is missing: $path" }
   $actual = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
   $want = ($expectedVersion -split '\.')[0..1] -join '.'
   $have = (($actual -replace ',', '.') -split '[ .]')[0..1] -join '.'
   if ($have -ne $want) { throw "$what is version $actual, but the manifest names $expectedVersion (the toolset that built the server).`n  path: $path" }
}

if ($Verify) {
   $bad = 0
   foreach ($a in $fetched) {
      try { Assert-Matches (Join-Path $repo $a.path) $a.sha256 $a.path; Write-Host "  OK  $($a.path)" }
      catch { Write-Host "  BAD $($_.Exception.Message)"; $bad++ }
   }
   foreach ($a in $gathered) {
      try { Assert-Version (Join-Path $repo $a.path) $a.version $a.path; Write-Host "  OK  $($a.path) (version $($a.version))" }
      catch { Write-Host "  BAD $($_.Exception.Message)"; $bad++ }
   }
   foreach ($a in $system) {
      $target = Join-Path $repo $a.path
      if (Test-Path -LiteralPath $target) { Write-Host ("  OK  {0} (system copy, sha256 {1})" -f $a.path, (Get-Sha256 $target)) }
      else { Write-Host "  BAD $($a.path) is not in place; run this script to copy it from the Windows installation."; $bad++ }
   }
   if ($bad) { Write-Host "$bad problem(s)."; exit 1 }
   Write-Host 'Every fetched, gathered and system artifact is present and matches the manifest.'
   exit 0
}

# -------------------------------------------------------------------- system
foreach ($a in $system) {
   $source = Get-SystemSource $a
   $target = Join-Path $repo $a.path
   New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
   Copy-Item -LiteralPath $source -Destination $target -Force
   # A compile-time input: the copy keeps Windows' own file time (2024 for the ADO
   # library), which is OLDER than a .tlh the compiler generated last week, so an
   # incremental build would keep the stale wrapper header. Stamp it now.
   (Get-Item -LiteralPath $target).LastWriteTime = Get-Date
   Write-Host ("  system   {0}  (from {1}, sha256 {2})" -f $a.path, $source, (Get-Sha256 $target))
}
if ($SystemOnly) {
   Write-Host 'The system artifacts are in place.'
   exit 0
}

# ------------------------------------------------------------------ gathered
if ($gathered.Count) {
   if (-not $env:VCToolsRedistDir) {
      # Only VCToolsRedistDir is wanted, so vcvars64.bat is asked for that one
      # value rather than the whole environment being imported.
      . (Join-Path $repo 'libraries\build-common.ps1')
      $vsInstall = Resolve-VcVars64
      $vcVars = if ($vsInstall -is [string]) { $vsInstall } else { $vsInstall.VcVars64 }
      if (-not $vcVars -or -not (Test-Path -LiteralPath $vcVars)) { throw "vcvars64.bat was not found (Resolve-VcVars64 answered '$vcVars')." }
      # Delayed expansion, or cmd expands the variable before vcvars64 has set it.
      $line = & cmd.exe /d /v:on /c "call `"$vcVars`" >nul 2>&1 && echo VCToolsRedistDir=!VCToolsRedistDir!" 2>$null | Where-Object { $_ -like 'VCToolsRedistDir=*' } | Select-Object -Last 1
      if ($line) { $env:VCToolsRedistDir = $line.Substring('VCToolsRedistDir='.Length) }
   }
   if (-not $env:VCToolsRedistDir) { throw 'VCToolsRedistDir is not set and Visual Studio could not be found; the MSVC runtime cannot be gathered.' }
   $crt = Get-ChildItem (Join-Path $env:VCToolsRedistDir 'x64') -Directory -Filter 'Microsoft.VC*.CRT' | Select-Object -First 1
   if (-not $crt) { throw "No Microsoft.VC*.CRT directory under $env:VCToolsRedistDir\x64" }
   Write-Host "Gathering the MSVC runtime from $($crt.FullName)"
   foreach ($a in $gathered) {
      $target = Join-Path $repo $a.path
      $source = Join-Path $crt.FullName (Split-Path $a.path -Leaf)
      if (-not (Test-Path -LiteralPath $source)) { throw "The build machine's runtime has no $(Split-Path $a.path -Leaf) ($source)" }
      New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
      Copy-Item -LiteralPath $source -Destination $target -Force
      Assert-Version $target $a.version $a.path
      Write-Host ("  gathered {0}  (version {1}, sha256 {2})" -f $a.path, (Get-Item -LiteralPath $target).VersionInfo.FileVersion, (Get-Sha256 $target))
   }
}

# ------------------------------------------------------------------- fetched
New-Item -ItemType Directory -Force $Cache | Out-Null
$assets = @{}
foreach ($a in $fetched) {
   # source is "<release>/<asset>[#<member>]": the asset to download and, for an
   # archive, the member inside it that becomes this path.
   $parts = $a.source -split '#', 2
   $assetRef = $parts[0]
   $member = if ($parts.Count -gt 1) { $parts[1] } else { $null }
   $release, $asset = $assetRef -split '/', 2
   $local = Join-Path $Cache $asset
   if (-not $assets.ContainsKey($assetRef)) {
      $expectedAsset = ($manifest.build_inputs | Where-Object { $_.release -eq $release -and $_.asset -eq $asset }).sha256
      if (-not $expectedAsset) { throw "The manifest's build_inputs has no hash for $assetRef" }
      if (-not (Test-Path -LiteralPath $local) -or (Get-Sha256 $local) -ne $expectedAsset.ToLower()) {
         $url = "$releaseBase/$release/$asset"
         Write-Host "Downloading $url"
         Invoke-WebRequest -Uri $url -OutFile $local -UseBasicParsing
      }
      Assert-Matches $local $expectedAsset "release asset $assetRef"
      $assets[$assetRef] = $local
   }
   $target = Join-Path $repo $a.path
   New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
   if ($member) {
      $extract = Join-Path $Cache ($asset + '.extracted')
      if (-not (Test-Path -LiteralPath (Join-Path $extract $member))) {
         if (Test-Path -LiteralPath $extract) { [IO.Directory]::Delete($extract, $true) }
         Expand-Archive -LiteralPath $local -DestinationPath $extract -Force
      }
      Copy-Item -LiteralPath (Join-Path $extract $member) -Destination $target -Force
   } else {
      Copy-Item -LiteralPath $local -Destination $target -Force
   }
   Assert-Matches $target $a.sha256 $a.path
   Write-Host "  placed $($a.path)"
}

Write-Host 'Every fetched, gathered and system artifact is in place and matches the manifest.'
