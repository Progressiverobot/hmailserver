# Builds the seed corpora for the MIME fuzz targets out of the real test
# messages already in the repository.
#
# WHY THE SEEDS ARE GENERATED RATHER THAN COMMITTED
# -------------------------------------------------
# A libFuzzer corpus is only as good as its seeds: coverage-guided mutation
# starts from what you give it, and a run seeded with invented byte blobs spends
# its first hours rediscovering that a MIME message has a colon in it. The right
# seeds are real messages, and this repository already has ~50 of them - the
# DKIM test data, the multipart resources the MIME tests parse, and the two
# DKIM-signed .eml files.
#
# Those files are also the most line-ending-sensitive files in the tree. The
# repository's .gitattributes says it outright: the .eml resources carry DKIM
# signatures whose body hashes break if a line ending changes, and raw-message
# tests reject bare LFs with "554 Message containing bare LF's". So this script:
#
#   - reads and writes with [System.IO.File]::ReadAllBytes / WriteAllBytes only.
#     Never Get-Content, Set-Content, Out-File or Add-Content: every one of them
#     is encoding- and newline-aware and would silently rewrite CRLF, or add a
#     BOM, or append a trailing newline.
#   - never modifies a source file. It copies bytes out, one direction only.
#   - verifies every seed it writes by reading it back and comparing bytes,
#     because a corpus that quietly lost its CRLFs still looks like a corpus and
#     would just produce a run that never reaches the multipart code.
#
# The header seeds are a *prefix* of the source bytes (up to and including the
# first CRLFCRLF) and the decode seeds are one selector byte followed by those
# same bytes. Both are transformations a reader can verify by eye, which is the
# only reason they are allowed to be anything other than a straight copy.
#
# Output (all fully derived - safe to delete, never edit by hand):
#   fuzz\corpus\seeds\message\   whole messages, verbatim
#   fuzz\corpus\seeds\header\    header block only, verbatim prefix
#   fuzz\corpus\seeds\decode\    selector byte + header block
#
# The growing corpora that libFuzzer writes to live in fuzz\corpus\<target>\ and
# are never touched by this script - regenerating seeds must not throw away
# inputs a run spent hours discovering.

Param(
    [switch]$Quiet
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repoRoot = (Get-Item $scriptRoot).Parent.FullName

# Source roots, repo-relative. Read by RegressionTests\MIME\FuzzHarness.cs too,
# which fails if one of them stops existing - a renamed resources directory would
# otherwise leave the fuzzer running on an empty corpus, which looks identical to
# a fuzzer that is simply not finding anything.
$sourceRoots = @(
    'hmailserver\test\RegressionTests\Resources',
    'hmailserver\test\RegressionTests\Messages',
    'hmailserver\test\TestData\DKIM'
)

$sourceExtensions = @('.eml', '.txt')

$seedsRoot = Join-Path $scriptRoot 'corpus\seeds'
$messageDir = Join-Path $seedsRoot 'message'
$headerDir = Join-Path $seedsRoot 'header'
$decodeDir = Join-Path $seedsRoot 'decode'

# The first byte of a mime_decode_fuzzer input selects the codec and the field
# coder: index = byte % 7 for the Content-Transfer-Encoding name and byte % 4 for
# the field name. 0 selects "base64" and "Subject", which is the pair worth
# seeding - the encoded words in these messages are exactly what the field coder
# is for. Every other combination is one mutated byte away.
$decodeSelectorByte = [byte]0

function Write-SeedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    [System.IO.File]::WriteAllBytes($Path, $Bytes)

    # Read back and compare. This is not paranoia about the .NET API; it is a
    # guard against somebody later "tidying" this function into Set-Content,
    # which would corrupt every seed in a way nothing else in the pipeline
    # notices.
    $written = [System.IO.File]::ReadAllBytes($Path)
    if ($written.Length -ne $Bytes.Length) {
        throw "Seed $Path was written as $($written.Length) bytes but should be $($Bytes.Length)."
    }

    for ($index = 0; $index -lt $Bytes.Length; $index++) {
        if ($written[$index] -ne $Bytes[$index]) {
            throw "Seed $Path differs from its source at byte $index ($($written[$index]) vs $($Bytes[$index])). Line endings were probably rewritten."
        }
    }
}

# Returns the offset one past the first CRLFCRLF, or -1. Byte scan rather than a
# string search so that no encoding is ever guessed.
function Find-HeaderEnd {
    param(
        [Parameter(Mandatory = $true)][byte[]]$Bytes
    )

    for ($index = 0; $index -le $Bytes.Length - 4; $index++) {
        if ($Bytes[$index] -eq 13 -and $Bytes[$index + 1] -eq 10 -and
            $Bytes[$index + 2] -eq 13 -and $Bytes[$index + 3] -eq 10) {
            return $index + 4
        }
    }

    return -1
}

foreach ($directory in @($messageDir, $headerDir, $decodeDir)) {
    if (Test-Path $directory) { Remove-Item -Recurse -Force $directory }
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
}

$usedNames = @{}
$messageCount = 0
$headerCount = 0
$missingRoots = @()
$noHeaderTerminator = @()

foreach ($relativeRoot in $sourceRoots) {
    $root = Join-Path $repoRoot $relativeRoot
    if (-not (Test-Path $root)) {
        $missingRoots += $relativeRoot
        continue
    }

    # Filtered with Where-Object rather than -Include: -Include on a directory
    # path behaves differently depending on whether the path ends in \* and has
    # a habit of silently matching nothing, which here would mean an empty
    # corpus and a fuzz run that proves nothing.
    $files = Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $sourceExtensions -contains $_.Extension }

    foreach ($file in $files) {
        # Flatten the path below the repository root into one readable name.
        # Anything that is not safe in a file name or awkward on a command line
        # becomes an underscore; libFuzzer prints these names when it replays a
        # corpus, so they are worth keeping legible.
        $relative = $file.FullName.Substring($repoRoot.Length).TrimStart('\')
        $flattened = ($relative -replace '[^A-Za-z0-9._-]', '_')

        $name = "$flattened.seed"
        if ($usedNames.ContainsKey($name)) {
            $suffix = 2
            while ($usedNames.ContainsKey("$flattened._$suffix.seed")) { $suffix++ }
            $name = "$flattened._$suffix.seed"
        }
        $usedNames[$name] = $true

        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        if ($bytes.Length -eq 0) { continue }

        Write-SeedFile -Path (Join-Path $messageDir $name) -Bytes $bytes
        $messageCount++

        $headerEnd = Find-HeaderEnd -Bytes $bytes
        if ($headerEnd -lt 0) {
            # No blank line anywhere: a body-only or truncated fragment. Still a
            # perfectly good message seed - it exercises the "no header found"
            # path, which MimeHeader::Load reports by returning 0 - but there is
            # no header block to slice out of it. All 59 files currently qualify
            # for a header seed, so this branch is for the day somebody adds a
            # fragment, not for anything in the tree today.
            $noHeaderTerminator += $relative
            continue
        }

        $headerBytes = New-Object byte[] $headerEnd
        [System.Array]::Copy($bytes, 0, $headerBytes, 0, $headerEnd)

        Write-SeedFile -Path (Join-Path $headerDir $name) -Bytes $headerBytes

        $decodeBytes = New-Object byte[] ($headerEnd + 1)
        $decodeBytes[0] = $decodeSelectorByte
        [System.Array]::Copy($bytes, 0, $decodeBytes, 1, $headerEnd)

        Write-SeedFile -Path (Join-Path $decodeDir $name) -Bytes $decodeBytes
        $headerCount++
    }
}

if ($missingRoots.Count -gt 0) {
    Write-Error ("Seed source directories are missing: {0}. Fix the list in make-corpus.ps1." -f ($missingRoots -join ', '))
    exit 3
}

if ($messageCount -eq 0) {
    Write-Error 'No seed messages were found. A fuzz run with an empty corpus produces nothing and looks exactly like a fuzz run that found nothing.'
    exit 3
}

if (-not $Quiet) {
    Write-Host ("Seed corpus written under {0}" -f $seedsRoot) -ForegroundColor Cyan
    Write-Host ("  message : {0} files" -f $messageCount)
    Write-Host ("  header  : {0} files" -f $headerCount)
    Write-Host ("  decode  : {0} files" -f $headerCount)
    if ($noHeaderTerminator.Count -gt 0) {
        Write-Host ("  {0} source file(s) have no CRLFCRLF and contributed a message seed only: {1}" -f `
            $noHeaderTerminator.Count, ($noHeaderTerminator -join ', ')) -ForegroundColor DarkGray
    }
}

exit 0
