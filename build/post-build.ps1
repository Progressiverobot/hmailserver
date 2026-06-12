Param(
	[string]$Configuration = 'Debug'
)

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
	Start-Process powershell -Verb runAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Configuration $Configuration"
	exit $LASTEXITCODE
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = Resolve-Path (Join-Path $scriptRoot '..')

$outDir = Join-Path $repoRoot "hmailserver\source\Server\hMailServer\x64\$Configuration\"
$targetPath = Join-Path $outDir "hMailServer.exe"

$hmsLibs = $env:hMailServerLibs
if (-not $hmsLibs) {
	Write-Error "hMailServerLibs environment variable is not set."
	exit 1
}

$batchScript = Join-Path $repoRoot "hmailserver\source\Server\hMailServer\post-build.bat"

Write-Host "Running post-build for configuration: $Configuration"
Write-Host "Output directory: $outDir"

& $batchScript $hmsLibs $outDir $targetPath

$exitCode = $LASTEXITCODE
if ($exitCode -ne 0) {
	Write-Error "Post-build failed with exit code $exitCode"
	exit $exitCode
}

Write-Host "Post-build completed successfully."
