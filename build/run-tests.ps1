# Runs the NUnit regression suite against the live service.
#
# -StopOnError passes nunit3-console's /stoponerror, which abandons the run at the
# first failure. That used to be unconditional, and it contradicts the thing the suite
# is for: RELEASE.md step 8 requires "every test, nothing skipped", and a run that
# stopped at test 40 of 1361 produces a report that looks like a completed run with one
# failure. It cost two full runs in one afternoon to learn that twice - a single early
# failure reported "Test Count: 240" and said nothing whatsoever about the other
# thousand tests. It is the right flag while chasing one defect that poisons everything
# after it, and the wrong one for a release, so it is now something you ask for.
Param(
    [switch]$StopOnError,
    # Passed to --where, so a subset can be run without hand-building an nunit3-console
    # command line: -Where "class =~ /RegressionTests.IMAP/".
    [string]$Where
)

# Determine repository root relative to this script (script is in the `build` folder)
$repoRoot = (Get-Item $PSScriptRoot).Parent.FullName

# Path to the NUnit console runner installed under the test packages folder
$nunitExe = Join-Path $repoRoot 'hmailserver\test\packages\NUnit.ConsoleRunner.3.22.0\tools\nunit3-console.exe'

# Path to the test assembly to run (Debug x64 as requested)
$testAssembly = Join-Path $repoRoot 'hmailserver\test\RegressionTests\bin\x64\Debug\RegressionTests.dll'

if (-not (Test-Path $nunitExe)) {
    Write-Error "NUnit console runner not found: $nunitExe"
    exit 1
}

if (-not (Test-Path $testAssembly)) {
    Write-Error "Test assembly not found: $testAssembly"
    exit 1
}

Write-Host "Running tests:" -ForegroundColor Cyan
Write-Host "  Runner: $nunitExe"
Write-Host "  Assembly: $testAssembly"

# Execute the console runner and stream output in real time
# Add helpful NUnit arguments to show test names as they run
$nunitArgs = @(
    $testAssembly,
    '--labels=All'
)
if ($StopOnError) {
    $nunitArgs += '/stoponerror'
    Write-Host "  /stoponerror: the run will abandon at the first failure - not a release run." -ForegroundColor Yellow
}
if ($Where) {
    $nunitArgs += "--where=$Where"
    Write-Host "  --where=$Where : a subset - also not a release run." -ForegroundColor Yellow
}

Write-Host "Starting NUnit runner (streaming output)..." -ForegroundColor Cyan

$logsDir = Join-Path $repoRoot 'logs'
if (-not (Test-Path $logsDir)) { New-Item -Path $logsDir -ItemType Directory -Force | Out-Null }
$runLog = Join-Path $logsDir 'run-tests.log'

# Invoke the runner directly. PowerShell streams stdout/stderr natively;
# manual pipe reading with StreamReader.Peek() deadlocks when one stream
# has no data while the other pipe buffer fills. $LASTEXITCODE still carries
# the runner's exit code through the pipeline.
& $nunitExe @nunitArgs 2>&1 | Tee-Object -FilePath $runLog | ForEach-Object { $_.ToString() }

$lastExit = $LASTEXITCODE
Write-Host "Run log: $runLog"
if ($lastExit -ne 0) {
    Write-Error "Tests failed with exit code $lastExit"
}
exit $lastExit
