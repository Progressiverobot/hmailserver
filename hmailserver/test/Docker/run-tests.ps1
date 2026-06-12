param(
    [string]$InstallerName = "hMailServer.exe",
    [ValidateSet('Debug','Release')][string]$BuildConfig = 'Debug',
    [int]$InstallWaitSeconds = 300
)

$ErrorActionPreference = 'Stop'

$testsPath = Join-Path -Path 'C:\tests' -ChildPath $BuildConfig
if (-not (Test-Path $testsPath)) {
    Write-Host "Tests path $testsPath not found."
    exit 4
}

$nunitRunner = 'C:\nunit\nunit3-console.exe'
if (-not (Test-Path $nunitRunner)) {
    Write-Host "NUnit runner not found at $nunitRunner"
    exit 5
}

$dlls = Get-ChildItem -Path $testsPath -Filter '*.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
if (-not $dlls) {
    Write-Host "No test assemblies found under $testsPath"
    exit 6
}

Write-Host "Running NUnit for assemblies:`n$($dlls -join "`n")"

& $nunitRunner $dlls --result='C:\TestResult.xml'
$exitCode = $LASTEXITCODE

Write-Host "`n=== NUnit XML Result (C:\TestResult.xml) ==="
if (Test-Path 'C:\TestResult.xml') {
    Get-Content 'C:\TestResult.xml' -Raw | Write-Host
} else {
    Write-Host "Result file not produced."
}

exit $exitCode
# Run NUnit tests found under C:\tests and output the XML result to C:\TestResult.xml
$ErrorActionPreference = 'Stop'

$testsPath = 'C:\tests'
$nunitRunner = 'C:\nunit\nunit3-console.exe'

if (-not (Test-Path $nunitRunner)) {
    Write-Host "NUnit runner not found at $nunitRunner"
    exit 2
}

$dlls = Get-ChildItem -Path $testsPath -Filter '*.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName
if (-not $dlls) {
    Write-Host "No test assemblies found under $testsPath"
    exit 3
}

Write-Host "Running NUnit for assemblies:`n$($dlls -join "`n")"

& $nunitRunner $dlls --result='C:\TestResult.xml'
$exitCode = $LASTEXITCODE

Write-Host "`n=== NUnit XML Result (C:\TestResult.xml) ==="
if (Test-Path 'C:\TestResult.xml') {
    Get-Content 'C:\TestResult.xml' -Raw | Write-Host
} else {
    Write-Host "Result file not produced."
}

exit $exitCode
