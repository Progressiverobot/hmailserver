# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
   Regenerates the checked-in COM wrapper, Interop.hMailServer.dll, from the
   Release type library - AND updates its entry in the binary-provenance
   manifest, which is the half that kept getting forgotten.

   WHY ONE SCRIPT. The wrapper is TlbImp output, committed so the .NET tools
   build with plain `dotnet build` on any machine. Every tracked binary is
   listed in hmailserver/docs/third-party-binaries.json with its SHA-256, and
   .github/workflows/verify-binary-provenance.yml fails the build when the
   file on disk and the manifest disagree. Regenerating the wrapper by hand
   and not touching the manifest failed that check twice on 21 August 2026
   alone - after two different people had each done the first half
   correctly. Two steps that must always happen together are one step.

   WHEN. After any change to hMailServer.idl, and after building the server
   in Release - the wrapper is read from the Release type library, and a
   Debug-only build leaves it describing whatever Release last contained.
   Regenerate AFTER any interface-ordering fix, never before: the wrapper
   freezes the vtable layout it was generated from.

   A stale wrapper still compiles, which is what makes this easy to skip.
   The tools use a small, stable subset of the API, so nothing fails; the
   members added since the last regeneration are simply invisible to them.
#>

[CmdletBinding()]
param(
   [string] $TlbImp = 'C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8.1 Tools\x64\TlbImp.exe'
)

$ErrorActionPreference = 'Stop'

$root = (Get-Item (Split-Path -Parent $MyInvocation.MyCommand.Path)).Parent.FullName
$typeLibrary = Join-Path $root 'hmailserver\source\Server\hMailServer\x64\Release\hMailServer.tlb'
$intermediate = Join-Path $root 'hmailserver\source\Server\hMailServer\hMailServer\x64\Release\hMailServer.tlb'
$wrapper = Join-Path $root 'hmailserver\source\Tools\Interop\Interop.hMailServer.dll'
$manifestPath = Join-Path $root 'hmailserver\docs\third-party-binaries.json'
$manifestKey = 'hmailserver/source/Tools/Interop/Interop.hMailServer.dll'

if (-not (Test-Path $TlbImp))
{
   throw "TlbImp.exe not found at $TlbImp. It ships with the .NET Framework SDK (Windows SDK component)."
}

# MIDL writes the type library into the intermediate directory; post-build.bat
# stages it into the output directory, but only when the post-build event
# runs, and build.ps1 suppresses build events. Take the newer of the two so a
# build that skipped staging cannot regenerate the wrapper from yesterday.
$source = $typeLibrary
if (Test-Path $intermediate)
{
   if (-not (Test-Path $typeLibrary) -or (Get-Item $intermediate).LastWriteTime -gt (Get-Item $typeLibrary).LastWriteTime)
   {
      $source = $intermediate
   }
}

if (-not (Test-Path $source))
{
   throw "No Release type library found. Run build\build.ps1 -Configuration Release first - and if the IDL changed, delete the stale .tlb before building, because MSBuild has been seen to skip MIDL for one configuration while running it for the other."
}

# Staleness guard. A file's mtime is not evidence on its own - git bumps it on
# every checkout and merge, which is how the first version of this guard
# refused to run against a type library that was in fact current. So the
# question is asked two ways, and either answer of "stale" is final:
#   * the IDL has UNCOMMITTED edits newer than the type library, or
#   * the IDL's last COMMIT is newer than the type library.
$idl = Join-Path $root 'hmailserver\source\Server\hMailServer\hMailServer.idl'
$idlRelative = 'hmailserver/source/Server/hMailServer/hMailServer.idl'
$libraryTime = (Get-Item $source).LastWriteTime

& git -C $root diff --quiet HEAD -- $idlRelative
$idlDirty = ($LASTEXITCODE -ne 0)

if ($idlDirty -and (Get-Item $idl).LastWriteTime -gt $libraryTime)
{
   throw "hMailServer.idl has uncommitted edits newer than the type library ($source). Rebuild the server in Release first."
}

$lastCommitEpoch = [long](& git -C $root log -1 --format=%ct -- $idlRelative)
$lastCommitTime = [DateTimeOffset]::FromUnixTimeSeconds($lastCommitEpoch).LocalDateTime

if ($lastCommitTime -gt $libraryTime)
{
   throw "hMailServer.idl was last committed at $lastCommitTime, after the type library was built ($libraryTime). Rebuild the server in Release first."
}

Write-Host ("Generating {0}" -f $wrapper)
Write-Host ("  from {0} (built {1})" -f $source, (Get-Item $source).LastWriteTime)

& $TlbImp $source /out:$wrapper /namespace:hMailServer /machine:X64 /silent
if ($LASTEXITCODE -ne 0)
{
   throw "TlbImp returned $LASTEXITCODE."
}

$hash = (Get-FileHash -LiteralPath $wrapper -Algorithm SHA256).Hash.ToLower()
$size = (Get-Item $wrapper).Length

# Update the manifest in place with a text edit rather than ConvertTo-Json, so
# every other entry, the key order and the formatting survive byte-for-byte.
$manifest = Get-Content -LiteralPath $manifestPath -Raw
$pattern = '(?s)("path":\s*"' + [regex]::Escape($manifestKey) + '".*?"sha256":\s*")([0-9a-f]{64})(".*?"size":\s*)(\d+)'
$match = [regex]::Match($manifest, $pattern)

if (-not $match.Success)
{
   throw "No manifest entry for $manifestKey in $manifestPath - add one before regenerating, so the provenance check can see it."
}

$oldHash = $match.Groups[2].Value
$oldSize = $match.Groups[4].Value

if ($oldHash -eq $hash)
{
   Write-Host ("  unchanged: the manifest already records {0} ({1} bytes)" -f $hash.Substring(0, 12), $size) -ForegroundColor Green
   exit 0
}

$updated = $manifest.Substring(0, $match.Groups[2].Index) + $hash +
           $manifest.Substring($match.Groups[2].Index + $oldHash.Length, $match.Groups[4].Index - ($match.Groups[2].Index + $oldHash.Length)) +
           $size +
           $manifest.Substring($match.Groups[4].Index + $oldSize.Length)

# Preserve the file's own line endings and the absence of a BOM.
[System.IO.File]::WriteAllText($manifestPath, $updated, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("  manifest: sha256 {0}... -> {1}..., size {2} -> {3}" -f $oldHash.Substring(0, 12), $hash.Substring(0, 12), $oldSize, $size)
Write-Host ''
Write-Host 'Regenerated and recorded. Commit the wrapper and the manifest together, then rebuild the tools:' -ForegroundColor Green
Write-Host '  build\build-tools.ps1'
