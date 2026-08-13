Param(
	# NUnit's /stoponerror was hard-coded here, and it abandons the whole run at the first
	# error - so a single failure in an early fixture reported "Test Count: 240" out of
	# ~1275 and said nothing whatsoever about the other thousand tests. That is the wrong
	# default for a regression gate: after a large integration you need every failure in
	# one run, not the alphabetically first one, and it cost two full runs to learn that
	# twice in one afternoon. It stays available for the case it is actually good for -
	# bisecting a crash that poisons everything after it.
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
if ($StopOnError) { $nunitArgs += '/stoponerror' }
if ($Where)       { $nunitArgs += "--where=$Where" }

Write-Host "Starting NUnit runner (streaming output)..." -ForegroundColor Cyan

# Invoke the runner directly. PowerShell streams stdout/stderr natively;
# manual pipe reading with StreamReader.Peek() deadlocks when one stream
# has no data while the other pipe buffer fills.
& $nunitExe @nunitArgs 2>&1 | ForEach-Object { $_.ToString() }

$lastExit = $LASTEXITCODE
if ($lastExit -ne 0) {
    Write-Error "Tests failed with exit code $lastExit"
}
exit $lastExit
