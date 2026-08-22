# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

Param(
    [string]$Configuration = 'Debug'
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Load shared msbuild locator
. (Join-Path $scriptRoot "Find-MsBuild.ps1")
$msbuild = Find-MsBuild
if (-not $msbuild) {
    Write-Error "MSBuild not found. Install Visual Studio 2026 (Build Tools) or ensure msbuild is on PATH."
    exit 2
}

$solutionRelative = "..\hmailserver\test\RegressionTests\RegressionTests.sln"
try {
    $solution = Resolve-Path (Join-Path $scriptRoot $solutionRelative) -ErrorAction Stop
} catch {
    Write-Error "Solution not found: $solutionRelative (resolved from $scriptRoot)"
    exit 1
}

$logsDir = Join-Path $scriptRoot "..\logs"
if (-not (Test-Path $logsDir)) { New-Item -Path $logsDir -ItemType Directory -Force | Out-Null }

Write-Host "Using MSBuild: $msbuild"
Write-Host "Building solution: $solution"
Write-Host "Configuration: $Configuration"

# Restore NuGet packages first. The test projects use packages.config with hint
# paths into hmailserver\test\packages, which is where a restore of the parent
# "hMailServer Tests.sln" places them (its solution dir is hmailserver\test).
$parentSolution = Resolve-Path (Join-Path $scriptRoot "..\hmailserver\test\hMailServer Tests.sln")
& "$msbuild" $parentSolution /t:Restore /p:RestorePackagesConfig=true "/p:Configuration=$Configuration" /p:Platform=x64 /v:minimal
if ($LASTEXITCODE -ne 0) {
    Write-Error "NuGet restore failed."
    exit $LASTEXITCODE
}

$msbuildArgs = @(
    $solution
    '/m'
    "/p:Configuration=$Configuration"
    '/p:Platform=x64'
)

# The log is written, not just announced. This script created $logsDir and then
# never wrote anything into it, while the success message below quoted a
# $msbuildLog variable that was never assigned - so it printed "Build log: " and
# a release checklist that says to check the build log had nothing to check.
$msbuildLog = Join-Path $logsDir 'build-tests.log'

& "$msbuild" @msbuildArgs *>&1 | Tee-Object -FilePath $msbuildLog

$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
    Write-Error "Test build failed with exit code $exitCode. Build log: $msbuildLog"
    exit $exitCode
}

Write-Host "Build succeeded. Build log: $msbuildLog"
