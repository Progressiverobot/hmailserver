# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

Param(
	[string]$Configuration = 'Debug',
	[switch]$Clean,
	# Keep the server's assertions in a Release build, reported through the ERROR log
	# (HM6364) rather than compiled out. The dynamic-analysis build: run the full suite
	# on it before a release and expect no HM6364 line. Never the binary that ships.
	[switch]$Asserts
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$solutionRelative = "..\hmailserver\source\Server\hMailServer\hMailServer.sln"
try {
	$solution = Resolve-Path (Join-Path $scriptRoot $solutionRelative) -ErrorAction Stop
} catch {
	Write-Error "Solution not found: $solutionRelative (resolved from $scriptRoot)"
	exit 1
}

$logsDir = Join-Path $scriptRoot "..\logs"
if (-not (Test-Path $logsDir)) { New-Item -Path $logsDir -ItemType Directory -Force | Out-Null }

# Use shared msbuild locator
. (Join-Path $scriptRoot "Find-MsBuild.ps1")
$msbuild = Find-MsBuild
if (-not $msbuild) {
	Write-Error "MSBuild not found. Install Visual Studio 2026 (Build Tools) or ensure msbuild is on PATH."
	exit 2
}

Write-Host "Using MSBuild: $msbuild"
Write-Host "Building solution: $solution"
Write-Host "Configuration: $Configuration"

if ($Clean) {
	Write-Host "Cleaning..."
	& "$msbuild" $solution '/t:Clean' "/p:Configuration=$Configuration" '/p:Platform=x64' *>&1
	if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$msbuildArgs = @(
	$solution
	'/m'
	"/p:Configuration=$Configuration"
	'/p:Platform=x64'
	'/p:PreBuildEventUseInBuild=false'
	'/p:PostBuildEventUseInBuild=false'
)

$logSuffix = ''
if ($Asserts) {
	if ($Configuration -ne 'Release') {
		Write-Error "-Asserts applies to the Release configuration (Debug keeps its assertions already)."
		exit 3
	}
	$msbuildArgs += '/p:KeepAssertions=true'
	$logSuffix = '-asserts'
	Write-Host 'Assertions are KEPT in this Release build (-Asserts): a violated HM_ASSERT is reported as HM6364 in the ERROR log. This is the dynamic-analysis build, not the one that ships.' -ForegroundColor Yellow
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()

# $logsDir was created above and nothing was ever written to it. A server build
# emits warnings worth grepping after the fact, and a failure worth reading once
# the console has scrolled, so the output goes to a file as well as the terminal.
# $LASTEXITCODE still carries MSBuild's own exit code through the pipeline.
$msbuildLog = Join-Path $logsDir "build-$Configuration$logSuffix.log"

& "$msbuild" @msbuildArgs *>&1 | Tee-Object -FilePath $msbuildLog

$exitCode = $LASTEXITCODE
$sw.Stop()
Write-Host ("Build completed in {0:F1} seconds." -f $sw.Elapsed.TotalSeconds)
if ($exitCode -ne 0) {
	Write-Error "Build failed with exit code $exitCode. Build log: $msbuildLog"
	exit $exitCode
}

Write-Host "Build succeeded. Build log: $msbuildLog"

