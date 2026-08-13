Open findings
=============

Reproducers for defects the fuzzers have found and **that are not fixed yet**.

This directory exists because the two obvious places to put such a file are both
wrong. `fuzz\artifacts\` is in `.gitignore` and is wiped by anyone who cleans their
build, so a finding left there is lost the first time somebody tidies up - and the
CPU time that found it is spent again. `fuzz\regression\` is worse: `run-fuzz.ps1`
replays that directory on every run precisely so a *fixed* bug cannot come back, so
putting an unfixed reproducer there would make every future run fail on a defect
everybody already knows about, which is how a suite gets ignored.

So: findings live here until they are fixed, and `run-fuzz.ps1` does not touch this
directory. When one is fixed, **move** the file to
`fuzz\regression\<target>\<name-of-the-finding>` in the same commit as the fix, so
that from then on every run proves it stays fixed.

One directory per target. Name each file after the defect, not after the hash
libFuzzer gave it, and add an entry below saying what it does and what is known
about reachability - a reproducer with no explanation is a puzzle, not a bug report.

Files here are exact byte sequences: never edit one, never re-save one. The
`fuzz\.gitattributes` rules cover this subtree too, so git will not normalise line
endings the way it does elsewhere in the repository.

Current findings
----------------

### mime_decode_fuzzer/out-of-memory-mimeunicodeencoder-encodevalue

`libFuzzer: out-of-memory` reached through

    MIMEUnicodeEncoder::EncodeValue   Mime.cpp:539
      -> FieldCodeBase::GetOutput -> Encode -> MimeEncodedWord::QEncode

Found 13 August 2026, on the first run of this target, and reproduced twice with
different inputs (two independent `oom-*` artifacts), so it is not a one-off
allocation spike. A **3.4 KB** input drives the process past libFuzzer's default
2048 MB RSS limit, which is a memory amplification of roughly six hundred thousand
to one - the shape of a defect where an output buffer grows without a bound tied to
the input, rather than of a merely expensive operation.

What is proven: given a charset and a value, `EncodeValue` can be made to consume
unbounded memory in-process.

What is NOT proven, and must not be claimed until it is: that this is reachable from
the network. `EncodeValue` encodes a header value on the way OUT, and the reachable
question is whether any path lets a remote party control both the charset and a
value long enough to trigger it - re-encoding a subject on forward is the obvious
candidate to check first. Until somebody follows that through, this is a real defect
of unknown severity, not a vulnerability.

Do not "fix" this by raising the RSS limit in `run-fuzz.ps1`. The limit is what
found it.
