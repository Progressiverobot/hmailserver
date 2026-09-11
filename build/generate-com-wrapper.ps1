# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

<#
   Generates the COM wrapper the .NET tools compile against,
   hmailserver\source\Tools\Interop\Interop.hMailServer.dll, from this
   repository's own hMailServer.idl. The wrapper is TlbImp output and is not in
   git since 11 September 2026: every .NET build - build-tools.ps1, the CI
   workflows, a plain dotnet build after this script - makes it from source,
   the way the server makes the type library from the same IDL.

   WHERE THE TYPE LIBRARY COMES FROM. Two sources, and they are the same bytes
   (checked on 11 September 2026: a MIDL run over the IDL and the server's own
   Release build produced type libraries with one SHA-256):

     * the server build's hMailServer.tlb, when one exists for the requested
       configuration and the IDL has not changed since it was built - the
       staleness test is git's, not the file clock's, because a checkout
       touches every file;
     * otherwise MIDL over hMailServer.idl, with the options the project file
       gives MIDL, into a temporary directory. MIDL needs cl.exe as its
       preprocessor, so this runs inside vcvars64.bat, found through vswhere.
       This is the path CI takes: no server build, the IDL alone.

   IDEMPOTENT. The wrapper is written only when the type library it would be
   made from differs from the one recorded beside it
   (Interop.hMailServer.dll.source-sha256); TlbImp output carries a fresh
   module id on every run, so the wrapper's own hash cannot say whether it is
   current, and the source's can.

   -FromIdl ignores any built type library and compiles the IDL, which is what
   CI passes so a runner never depends on a build it did not do.
#>

[CmdletBinding()]
param(
   # Which server build's type library to prefer, when one is there.
   [string] $Configuration = 'Release',
   # Compile the IDL even if a built type library exists.
   [switch] $FromIdl,
   # Rewrite the wrapper even if its recorded source matches.
   [switch] $Force,
   # Overrides, for a machine that keeps its tools elsewhere.
   [string] $TlbImp = '',
   [string] $VcVars64 = ''
)

$ErrorActionPreference = 'Stop'

$root = (Get-Item (Split-Path -Parent $MyInvocation.MyCommand.Path)).Parent.FullName
$idl = Join-Path $root 'hmailserver\source\Server\hMailServer\hMailServer.idl'
$idlRelative = 'hmailserver/source/Server/hMailServer/hMailServer.idl'
$builtTypeLibrary = Join-Path $root "hmailserver\source\Server\hMailServer\x64\$Configuration\hMailServer.tlb"
$intermediateTypeLibrary = Join-Path $root "hmailserver\source\Server\hMailServer\hMailServer\x64\$Configuration\hMailServer.tlb"
$wrapper = Join-Path $root 'hmailserver\source\Tools\Interop\Interop.hMailServer.dll'
$stamp = "$wrapper.source-sha256"

function Find-TlbImp
{
   if ($TlbImp) { return $TlbImp }
   # The .NET Framework SDK's tools, newest first: NETFX 4.8.1 Tools, then 4.8.
   $candidates = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Microsoft SDKs\Windows\v10.0A\bin\NETFX * Tools\x64\TlbImp.exe" -ErrorAction SilentlyContinue |
      Sort-Object FullName -Descending
   if ($candidates) { return $candidates[0].FullName }
   $onPath = Get-Command TlbImp.exe -ErrorAction SilentlyContinue
   if ($onPath) { return $onPath.Source }
   throw 'TlbImp.exe was not found. It ships with the .NET Framework SDK (a Windows SDK component that Visual Studio installs).'
}

function Find-VcVars64
{
   if ($VcVars64) { return $VcVars64 }
   $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
   if (Test-Path -LiteralPath $vswhere)
   {
      $found = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find 'VC\Auxiliary\Build\vcvars64.bat' 2>$null | Select-Object -First 1
      if ($found) { return $found }
   }
   $glob = Get-ChildItem -Path "${env:ProgramFiles}\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat", "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\VC\Auxiliary\Build\vcvars64.bat" -ErrorAction SilentlyContinue |
      Sort-Object FullName -Descending | Select-Object -First 1
   if ($glob) { return $glob.FullName }
   throw 'vcvars64.bat was not found: MIDL needs the Visual C++ tools (cl.exe is its preprocessor). Install the "Desktop development with C++" workload, or build the server first so a type library exists.'
}

function Get-Sha256([string] $path)
{
   return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLower()
}

function Test-BuiltTypeLibraryCurrent([string] $path)
{
   # Stale if the IDL has uncommitted edits newer than the type library, or if
   # the IDL's last commit is newer than it.
   $libraryTime = (Get-Item -LiteralPath $path).LastWriteTime
   & git -C $root diff --quiet HEAD -- $idlRelative
   $idlDirty = ($LASTEXITCODE -ne 0)
   if ($idlDirty -and (Get-Item -LiteralPath $idl).LastWriteTime -gt $libraryTime) { return $false }
   $lastCommitEpoch = & git -C $root log -1 --format=%ct -- $idlRelative
   if ($lastCommitEpoch)
   {
      $lastCommitTime = [DateTimeOffset]::FromUnixTimeSeconds([long]$lastCommitEpoch).LocalDateTime
      if ($lastCommitTime -gt $libraryTime) { return $false }
   }
   return $true
}

function Invoke-Midl
{
   # The project file's MIDL settings for the x64 configurations: /Oicf
   # (stubless proxies), OPENSSL_NO_FILENAMES and NDEBUG defined, x64
   # environment. Everything but the .tlb is discarded.
   $vcVars = Find-VcVars64
   $work = Join-Path ([System.IO.Path]::GetTempPath()) ("hmailserver-midl-" + [System.IO.Path]::GetRandomFileName())
   New-Item -ItemType Directory -Path $work -Force | Out-Null
   $idlDir = Split-Path -Parent $idl
   $command = "call `"$vcVars`" >nul 2>&1 && cd /d `"$idlDir`" && midl /nologo /env x64 /Oicf /D OPENSSL_NO_FILENAMES /D NDEBUG " +
      "/tlb `"$work\hMailServer.tlb`" /h `"$work\hMailServer.h`" /iid `"$work\hMailServer_i.c`" " +
      "/proxy `"$work\hMailServer_p.c`" /dlldata `"$work\dlldata.c`" hMailServer.idl"
   Write-Host ("  MIDL over {0} (in {1})" -f $idlRelative, (Split-Path -Leaf (Split-Path -Parent $vcVars)))
   $output = & cmd.exe /d /c $command 2>&1
   if ($LASTEXITCODE -ne 0)
   {
      $output | ForEach-Object { Write-Host "    $_" }
      throw "MIDL returned $LASTEXITCODE."
   }
   $produced = Join-Path $work 'hMailServer.tlb'
   if (-not (Test-Path -LiteralPath $produced)) { throw "MIDL returned 0 but wrote no type library to $work." }
   return $produced
}

# 1. The type library.
$source = $null
$fromBuild = $false
if (-not $FromIdl)
{
   foreach ($candidate in @($builtTypeLibrary, $intermediateTypeLibrary))
   {
      if ((Test-Path -LiteralPath $candidate) -and (Test-BuiltTypeLibraryCurrent $candidate))
      {
         $source = $candidate
         $fromBuild = $true
         break
      }
   }
}
if (-not $source)
{
   $source = Invoke-Midl
}
$sourceHash = Get-Sha256 $source
if ($fromBuild) { Write-Host ("  type library: {0} (built {1})" -f $source, (Get-Item -LiteralPath $source).LastWriteTime) }
Write-Host ("  type library sha256 {0}" -f $sourceHash)

# 2. Already current?
if (-not $Force -and (Test-Path -LiteralPath $wrapper) -and (Test-Path -LiteralPath $stamp))
{
   $recorded = (Get-Content -LiteralPath $stamp -Raw).Trim().ToLower()
   if ($recorded -eq $sourceHash)
   {
      Write-Host ("Interop.hMailServer.dll is current: made from a type library with this hash. Nothing to do.") -ForegroundColor Green
      if (-not $fromBuild) { Remove-Item -LiteralPath (Split-Path -Parent $source) -Recurse -Force -ErrorAction SilentlyContinue }
      exit 0
   }
}

# 3. TlbImp.
$tlbImpExe = Find-TlbImp
New-Item -ItemType Directory -Path (Split-Path -Parent $wrapper) -Force | Out-Null
Write-Host ("  TlbImp: {0}" -f $tlbImpExe)
& $tlbImpExe $source /out:$wrapper /namespace:hMailServer /machine:X64 /silent
if ($LASTEXITCODE -ne 0) { throw "TlbImp returned $LASTEXITCODE." }
[System.IO.File]::WriteAllText($stamp, $sourceHash + "`n", (New-Object System.Text.UTF8Encoding($false)))
if (-not $fromBuild) { Remove-Item -LiteralPath (Split-Path -Parent $source) -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host ("Generated {0} ({1} bytes, sha256 {2}) from {3}." -f $wrapper, (Get-Item -LiteralPath $wrapper).Length, (Get-Sha256 $wrapper), $(if ($fromBuild) { 'the built type library' } else { 'the IDL' })) -ForegroundColor Green
