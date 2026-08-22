# Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
# SPDX-License-Identifier: AGPL-3.0-or-later

# Runs one of the MIME fuzz targets, or replays a corpus, or reproduces a crash.
#
# The flags below are not decoration - each one is the difference between a run
# that finds something and a run that wastes a night:
#
#   -dict            libFuzzer's mutators are byte-level. Without the MIME token
#                    dictionary whole decoders are never reached, because the
#                    mutator will not invent "Content-Transfer-Encoding:" or a
#                    40-character boundary that matches the header.
#   -max_len         Caps input size. Real mail is much larger, but every byte of
#                    input costs execution rate, and the structural bugs in a MIME
#                    parser (nesting, boundary arithmetic, encoded words) all
#                    reproduce in a few kilobytes. Raise it for a soak run.
#   -rss_limit_mb    A parser that allocates without bound is a finding, not a
#                    reason to swap out the machine. This makes it a report.
#   -timeout         Same for a parser that stops making progress: 25 seconds on
#                    one input is a hang, and MimeEncodedWord::BEncode has a loop
#                    that can stop advancing.
#   -error_exitcode  Distinguishes "found a bug" (77) from "could not start"
#                    (anything else), which matters when a script or a test is
#                    reading the exit code rather than a human reading the log.
#
# Usage:
#   .\fuzz\run-fuzz.ps1 -Target mime_message_fuzzer -Minutes 10
#   .\fuzz\run-fuzz.ps1 -Target mime_header_fuzzer -Replay
#   .\fuzz\run-fuzz.ps1 -Target mime_header_fuzzer -Reproduce .\fuzz\artifacts\mime_header_fuzzer\crash-abc123
#   .\fuzz\run-fuzz.ps1 -Target mime_message_fuzzer -Minutes 480 -Jobs 4    # overnight
#
# See hmailserver\docs\Fuzzing.md for how long to run, what to do with a finding,
# and why this is only useful alongside the crash oracle.

Param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('mime_message_fuzzer', 'mime_header_fuzzer', 'mime_decode_fuzzer')]
    [string]$Target,

    # Wall-clock budget. 0 = run until stopped with Ctrl-C.
    [int]$Minutes = 5,

    # Replay the seed corpus and the accumulated corpus once, then exit. This is
    # the regression mode: it proves the parser still survives every input a
    # previous run found interesting, and it finishes in seconds.
    [switch]$Replay,

    # Run one input file and exit. This is how a crash artifact is reproduced.
    [string]$Reproduce = '',

    [int]$MaxLen = 16384,

    # Parallel processes. libFuzzer forks workers; they share the corpus
    # directory, so more workers means more coverage per wall-clock hour on a
    # machine with cores to spare.
    [int]$Jobs = 0,

    # Extra libFuzzer flags, passed through verbatim.
    [string[]]$ExtraArgs = @()
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition

$executable = Join-Path $scriptRoot "bin\$Target.exe"
if (-not (Test-Path $executable)) {
    Write-Error "Target not built: $executable`nRun .\fuzz\build-fuzz.ps1 first."
    exit 2
}

# Which seed corpus each target starts from. The message target wants whole
# messages; the header target wants header blocks but also benefits from whole
# messages, because its third shape (Utilities::GetMimeHeader) searches for the
# blank line itself and so behaves differently when a body follows.
$seedDirectories = switch ($Target) {
    'mime_message_fuzzer' { @('corpus\seeds\message') }
    'mime_header_fuzzer'  { @('corpus\seeds\header', 'corpus\seeds\message') }
    'mime_decode_fuzzer'  { @('corpus\seeds\decode') }
}

$missingSeeds = @()
$seedArguments = @()
foreach ($relative in $seedDirectories) {
    $directory = Join-Path $scriptRoot $relative
    if (Test-Path $directory) { $seedArguments += $directory } else { $missingSeeds += $relative }
}

if ($missingSeeds.Count -gt 0) {
    Write-Error ("Seed corpus missing: {0}`nRun .\fuzz\make-corpus.ps1 (build-fuzz.ps1 does it for you)." -f ($missingSeeds -join ', '))
    exit 2
}

# Committed reproducers for findings that have been fixed. These are the closest
# thing this harness has to a regression test: every run, including the fast
# -Replay one, re-executes the exact input that used to crash. See
# fuzz\regression\README.md for the rule about not editing them.
$regressionDir = Join-Path $scriptRoot "regression\$Target"
if (Test-Path $regressionDir) { $seedArguments += $regressionDir }

# The growing corpus. Separate from the seeds on purpose: regenerating the seeds
# must never delete an input that a long run spent hours finding, and the seeds
# directory is deleted and rebuilt wholesale by make-corpus.ps1.
$corpusDir = Join-Path $scriptRoot "corpus\$Target"
$artifactDir = Join-Path $scriptRoot "artifacts\$Target"
foreach ($directory in @($corpusDir, $artifactDir)) {
    if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
}

$dictionary = Join-Path $scriptRoot 'dict\mime.dict'
if (-not (Test-Path $dictionary)) {
    Write-Error "Dictionary not found: $dictionary"
    exit 2
}

# Forward slashes and an explicit trailing slash. libFuzzer treats
# -artifact_prefix as a literal prefix, so without the trailing separator the
# artifacts land next to the directory with its name glued to theirs. Forward
# slashes avoid the other Windows trap: an argument that ends in a backslash and
# contains a space gets its closing quote escaped on the way to the process.
$artifactPrefix = ($artifactDir -replace '\\', '/') + '/'

if ($Reproduce) {
    if (-not (Test-Path $Reproduce)) {
        Write-Error "Input file not found: $Reproduce"
        exit 2
    }

    Write-Host "Reproducing: $Reproduce" -ForegroundColor Cyan
    Write-Host "  $executable $Reproduce"
    Write-Host ''

    # One input, one execution, full report. No corpus, no dictionary, no
    # mutation - so the run is deterministic and the stack trace is the only
    # output that matters.
    & $executable $Reproduce
    $exitCode = $LASTEXITCODE

    Write-Host ''
    if ($exitCode -eq 0) {
        Write-Host 'Input did not crash. If it used to, the fix worked - keep the file as a regression input in the corpus.' -ForegroundColor Green
    } else {
        Write-Host ("Reproduced (exit code {0}). The report above is the finding." -f $exitCode) -ForegroundColor Yellow
    }

    exit $exitCode
}

$arguments = @(
    "-dict=$dictionary",
    "-artifact_prefix=$artifactPrefix",
    "-max_len=$MaxLen",
    '-rss_limit_mb=2048',
    '-timeout=25',
    '-error_exitcode=77',
    '-print_final_stats=1'
)

if ($Replay) {
    # -runs=0 executes every corpus file once and exits. Adding the growing
    # corpus first means a replay covers both the seeds and everything previous
    # runs kept.
    $arguments += '-runs=0'
} else {
    if ($Minutes -gt 0) { $arguments += "-max_total_time=$($Minutes * 60)" }
    if ($Jobs -gt 0) { $arguments += @("-jobs=$Jobs", "-workers=$Jobs") }
}

$arguments += $ExtraArgs

# First positional argument is the corpus libFuzzer writes new inputs into; the
# rest are read-only seed directories.
$arguments += $corpusDir
$arguments += $seedArguments

Write-Host ("Target    : {0}" -f $executable)
Write-Host ("Corpus    : {0}" -f $corpusDir)
Write-Host ("Seeds     : {0}" -f ($seedArguments -join ', '))
Write-Host ("Artifacts : {0}" -f $artifactDir)
if ($Replay) {
    Write-Host 'Mode      : replay (-runs=0)'
} else {
    Write-Host ("Mode      : fuzz ({0})" -f ($(if ($Minutes -gt 0) { "$Minutes minute(s)" } else { 'until Ctrl-C' })))
}
Write-Host ''

& $executable @arguments
$exitCode = $LASTEXITCODE

Write-Host ''
if ($exitCode -eq 0) {
    Write-Host 'No findings.' -ForegroundColor Green
} elseif ($exitCode -eq 77) {
    Write-Host 'FINDING. libFuzzer wrote the input that did it into:' -ForegroundColor Red
    Get-ChildItem -Path $artifactDir -File | Sort-Object LastWriteTime -Descending | Select-Object -First 5 |
        ForEach-Object { Write-Host ("  {0}" -f $_.FullName) }
    Write-Host ''
    Write-Host 'Reproduce it (deterministic, one execution):' -ForegroundColor Yellow
    Write-Host ("  .\fuzz\run-fuzz.ps1 -Target {0} -Reproduce <path above>" -f $Target)
    Write-Host ''
    Write-Host 'Then read hmailserver\docs\Fuzzing.md, section "What to do with a finding".'
} else {
    Write-Host ("Target exited with {0}, which is neither a clean run nor a finding - it usually means the process could not start (missing AddressSanitizer runtime DLL) or a flag was rejected. Read the output above." -f $exitCode) -ForegroundColor Yellow
}

exit $exitCode
