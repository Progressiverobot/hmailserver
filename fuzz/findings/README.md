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

**None.** Both findings so far were fixed in the commit that recorded them, so both
reproducers went straight to `fuzz\regression\`. The directory is kept because the
next one will not necessarily be that quick, and because the rule about where an
unfixed reproducer may and may not live is worth writing down once rather than
rediscovering under pressure.

Fixed, and now replayed on every run from `fuzz\regression\`:

* `mime_message_fuzzer/heap-buffer-overflow-findstring-boundary-scan` - a
  **pointer/length desync** in `MimeBody::Load`. After copying a part's content it
  did `pszData += nSize` without reducing `nDataSize`, so the later
  `pszEnd = pszData + nDataSize` landed `nSize` bytes past the end of the buffer -
  and that too-far pointer was handed to `GetBoundaryEnd` as the limit to search up
  to. `FindString` honours its limit exactly, so it read a whole boundary's length
  (27 bytes here) beyond the allocation. Fixing the arithmetic fixed it; a first
  attempt that clamped the two `pszData - 2` step-backs was necessary but not
  sufficient, and the replay said so.
* `mime_decode_fuzzer/heap-buffer-overflow-mimeencodedword-decode` - **three**
  unbounded reads in one `if` in `MimeEncodedWord::Decode`, from a 23-byte input:
  `pbData[1]` read when the loop only guarantees `pbData < pbEnd`; `::strchr` used on
  a length-delimited header field that has **no NUL terminator**; and
  `pszHeaderEnd[2]` dereferenced *before* the `pszHeaderEnd+3 < pbEnd` test meant to
  make it safe, because `&&` evaluates left to right. All three are reachable from
  any header on any received message.

* `mime_message_fuzzer/new-delete-type-mismatch-mimecodebase-nonvirtual-dtor` -
  `MimeCodeBase` had virtual `Encode`/`Decode` and no virtual destructor while six
  sites deleted derived coders through the base pointer. The reproducer is an
  unmodified seed, so it was live for ordinary mail.
* `mime_decode_fuzzer/out-of-memory-mimeunicodeencoder-encodevalue` - the entry
  below, kept for the record because the reachability question it raises is still
  worth reading.

### mime_decode_fuzzer/out-of-memory-mimeunicodeencoder-encodevalue  (FIXED)

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

**Fixed 13 August 2026, and the cause was not in the encoder's buffer handling at
all - it was arithmetic.** `MAX_ENCODEDWORD_LEN` is 75, and both encoders computed
their per-line budget as `75 - charset_length - 7`. A charset name of 69 characters
or more makes that zero or negative, and then in `QEncode` the "line is full" test
is true on every single byte, so each input byte emitted a complete `=?<charset>?Q?`
header - output growing as input x charset, which is the six-hundred-thousand-to-one
amplification. `BEncode` was worse: its block size went non-positive, so it encoded
nothing per iteration while still appending a header, and never terminated at all.
The only thing standing there was an `ASSERT`, which the shipped Release build
compiles out - the reason this harness has a separate `-Asserts` mode.

Both now fall back to the raw encoder when no legal encoded word is possible. That
is not a workaround: no real charset name approaches 69 characters, and no encoded
word built from one would be decodable by any client, so emitting the value as-is
conveys strictly more than emitting a stream nothing can read. The reproducer that
took 2 GB now runs in 1 ms.
