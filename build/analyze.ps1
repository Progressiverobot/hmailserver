# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# The static-analysis build: the server, Release x64, rebuilt from scratch with MSVC's
# /analyze (PREfast) on, and the findings summarised by code and by file. This is how
# the static-analysis backlog in Roadmap.md is measured; run it, read the list, fix
# what is real, and write down why the rest is not.
#
# Two things to know before running it:
#   - It REPLACES the Release binaries with the /analyze build's output. The code is
#     the same, but rebuild normally (build\build.ps1 -Configuration Release) before
#     running the regression gate, so the gate proves the binary that ships.
#   - The findings under libraries\ (Boost, the SQL CE headers, the Windows SDK) are
#     listed separately from the project's own code; the own-code list is the one
#     that is worked through.
#
# Outputs: logs\analyze-Release.log (the full MSBuild log) and logs\analyze-warnings.txt
# (one line per distinct finding).
Param(
    [switch]$Quiet
)
$ErrorActionPreference = 'Continue'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')
. (Join-Path $scriptRoot 'Find-MsBuild.ps1')
$msbuild = Find-MsBuild
if (-not $msbuild) {
    Write-Error 'MSBuild not found. Install Visual Studio 2026 (Build Tools) or ensure msbuild is on PATH.'
    exit 2
}
$solution = Resolve-Path (Join-Path $repoRoot 'hmailserver\source\Server\hMailServer\hMailServer.sln')
$logsDir = Join-Path $repoRoot 'logs'
if (-not (Test-Path $logsDir)) { New-Item -Path $logsDir -ItemType Directory -Force | Out-Null }
$log = Join-Path $logsDir 'analyze-Release.log'
$list = Join-Path $logsDir 'analyze-warnings.txt'

Write-Host "== /analyze rebuild started $(Get-Date -Format s)"
Stop-Service hMailServer -ErrorAction SilentlyContinue
Get-Process hMailServer -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

& $msbuild $solution '/m' '/t:Rebuild' '/p:Configuration=Release' '/p:Platform=x64' `
    '/p:PreBuildEventUseInBuild=false' '/p:PostBuildEventUseInBuild=false' `
    '/p:EnablePREfast=true' '/p:RunCodeAnalysis=true' '/p:TreatWarningsAsErrors=false' `
    '/nologo' '/v:m' '/fl' "/flp:logfile=$log;verbosity=normal" *>&1 |
    Select-String -Pattern 'error C|Build succeeded|Build FAILED|Error\(s\)|Warning\(s\)' | Select-Object -Last 6
$exit = $LASTEXITCODE
Write-Host "MSBUILD EXIT=$exit finished $(Get-Date -Format s)"
if ($exit -ne 0) { exit $exit }

# One line per distinct finding, the MSBuild project suffix stripped.
$lines = Select-String -Path $log -Pattern 'warning (C6\d{3,4}|C26\d{3}|C28\d{3}|C33\d{3})' | ForEach-Object { $_.Line }
$unique = $lines | ForEach-Object { ($_ -replace '^\s*\d*>', '') -replace '\s+\[.*$', '' } | Sort-Object -Unique
$unique | Set-Content -Path $list -Encoding UTF8

$thirdParty = 'libraries\\|boost|win32_api|sqlce|XMLite|\.hpp\(|\.ipp\(|Program Files|Windows Kits'
$own = $unique | Where-Object { $_ -notmatch $thirdParty }
$defects = $own | Where-Object { $_ -match 'warning (C6\d{3,4}|C28182|C33\d{3})' }

Write-Host "== findings: $($unique.Count) distinct, $($own.Count) in the project's own code, $($defects.Count) of those defect-class (C6xxx, C28182, C33xxx)"
if (-not $Quiet) {
    Write-Host '== own code, defect-class:'
    $defects | ForEach-Object {
        if ($_ -match '\\([A-Za-z0-9_.]+)\((\d+)\): warning (C\d+): (.*)$') { "  {0}({1}): {2}: {3}" -f $Matches[1], $Matches[2], $Matches[3], $Matches[4] } else { "  $_" }
    }
    Write-Host '== own code, by code:'
    $own | ForEach-Object { if ($_ -match 'warning (C\d+)') { $Matches[1] } } | Group-Object | Sort-Object Count -Descending | ForEach-Object { "  {0,-8} {1}" -f $_.Name, $_.Count }
}
Write-Host "Full list: $list"
