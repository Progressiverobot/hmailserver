Fuzzing the parsers
===================

Every message the server accepts is untrusted input, and the code that reads it
runs inside the one process that holds every mailbox on the machine. A parser bug
here is not a bad request that gets a 500 — it is a mail outage, plus whatever
the write path had half-finished when the process went down. That asymmetry is
the whole reason this harness exists.

This document covers the MIME parser, which is done. HTML, iCalendar and vCard
are the same shape of problem and are **not** done; see [What is not covered
yet](#what-is-not-covered-yet) so nobody mistakes a clean MIME run for a clean
parser story.

The harness lives in [`fuzz\`](../../fuzz/) at the repository root.

Why this waited on the crash oracle
-----------------------------------

Read this section before anything else, because it is the reason the fuzzing item
sat at "not started" for so long while nine other prerequisites went in ahead of
it.

**A fuzzer is a machine for producing inputs. It is not a machine for noticing
that something went wrong.** Something else has to be the oracle — the thing that
turns "the program did something bad" into a signal. With no oracle, a fuzzer
runs millions of malformed messages through the parser, the parser corrupts
memory on one of them, the process carries on with corrupted state, the run
reports "no findings", and everyone concludes the parser is fine. That is worse
than not fuzzing, because it produces a false clean bill of health.

This codebase had a specific version of that problem, and it is worth being
precise about it:

* `MimeBody::LoadFromFile` wraps the entire parse in `catch (...)`, copies the
  offending message into `Problematic messages`, reports through `ErrorManager`
  and returns success.
* The server is compiled with `/EHa` (`ExceptionHandling=Async` in
  `hMailServer.vcxproj`). Under `/EHa`, `catch (...)` catches *structured*
  exceptions as well as C++ ones — which means it catches access violations.

Put those two together and an out-of-bounds write in the MIME parser presents, in
production, as a log line saying a message could not be parsed. The process
survives. The message is preserved for inspection. And the memory that got
scribbled on belongs to something else entirely, so the consequence appears
later, somewhere unrelated, as a corrupted mailbox or a crash with a stack that
points at innocent code.

So there were two things to build, in order:

1. **An oracle for the running server.** `Common\Util\CrashOracle.{h,cpp}`
   installs a vectored exception handler, which sees a first-chance exception
   *before* any `catch (...)` gets the chance to swallow it, and records it. The
   regression suite checks the oracle after every test, so a swallowed access
   violation now fails the test that provoked it instead of passing quietly. That
   is what makes the regression suite an oracle rather than just a lot of asserts
   (1,257 tests as of 13 August 2026 — a figure that only ever goes up, so treat it
   as a floor rather than a fact to maintain).
2. **An oracle for the fuzz build**, which is AddressSanitizer. ASan is stronger
   than a crash handler: it traps the *first* out-of-bounds byte rather than
   waiting for an access that happens to land on an unmapped page, and it reports
   reads as well as writes. Almost every finding this harness will produce is a
   read one byte past the end of a buffer — something that would never fault in
   production and would never be noticed without it.

The relationship between them is the point. ASan finds the bug in a target that
runs the parser in isolation, thousands of times a second. The crash oracle keeps
the same class of bug visible in the real server, where the parser runs inside a
`catch (...)` that would otherwise hide it. Neither replaces the other, and doing
the fuzzing first would have produced findings nobody could confirm against a
running server.

One consequence worth knowing: the fuzz build is compiled `/EHsc`, not `/EHa`, so
`catch (...)` in the parser does **not** swallow access violations there. ASan
traps before any handler runs anyway. The fuzz build is therefore strictly better
at surfacing memory errors than the shipped build is at surviving them — which is
the right direction for a bug hunt, and another reason a finding here needs a
regression test in the suite rather than only a corpus entry.

Building it
-----------

The one-time prerequisite is a clang toolchain alongside MSVC, with libFuzzer's and
AddressSanitizer's runtime libraries. Two routes; the second is the one in use on
this bench, and it is here first because it is the one that has actually been made
to work end to end.

**Portable LLVM, user-local, no elevation.** This is what the build was verified
against:

```powershell
# ~820 MB download, ~2.5 GB extracted. Windows' own tar handles .xz and .zst.
$rel = Invoke-RestMethod 'https://api.github.com/repos/llvm/llvm-project/releases/tags/llvmorg-22.1.8' `
   -Headers @{ 'User-Agent' = 'hmailserver-build' }
$asset = $rel.assets | Where-Object name -eq 'clang+llvm-22.1.8-x86_64-pc-windows-msvc.tar.xz'
Invoke-WebRequest $asset.browser_download_url -OutFile "$env:TEMP\llvm.tar.xz"

$target = "$env:LOCALAPPDATA\Programs\LLVM-22.1.8"
New-Item -ItemType Directory -Force $target | Out-Null
tar -xf "$env:TEMP\llvm.tar.xz" -C $target --strip-components=1

$env:PATH = "$target\bin;$env:PATH"      # before build-fuzz.ps1
```

Nothing goes into Program Files, nothing is written to the registry, and it is
removed by deleting one folder. That matters more than it sounds: the alternative
below needs administrator rights, and `winget install LLVM.LLVM` is not a substitute
because it has **no user-scope installer at all** (`--scope user` fails with "No
applicable installer found") and its machine scope needs elevation.

**Or the Visual Studio component,** if you have administrator rights on the machine:

> Visual Studio Installer → Modify → **Individual components** → **C++ Clang
> tools for Windows** (component id
> `Microsoft.VisualStudio.Component.VC.Llvm.Clang`)

```powershell
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe" modify `
    --installPath "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools" `
    --add Microsoft.VisualStudio.Component.VC.Llvm.Clang --passive
```

That component ships the sanitizer runtimes too, and `Find-ClangCl.ps1` prefers it
via `vswhere` when it is present. It is second in this list only because it was not
available here.

### Three things that are not optional, each of which costs a build to rediscover

All three are already handled by `build-fuzz.ps1`; they are written down because the
symptoms point somewhere unhelpful.

1. **`/D_DISABLE_STRING_ANNOTATION=1` and `/D_DISABLE_VECTOR_ANNOTATION=1`.** Without
   them the link fails with `lld-link: error: /failifmismatch: mismatch detected for
   'annotate_string'`. The prebuilt `clang_rt.fuzzer` library is compiled with MSVC
   STL's ASan container annotations **off**, and the STL stamps a `/failifmismatch`
   directive into every object recording which way it was built, so any translation
   unit that enables them refuses to link against libFuzzer. The cost is the
   container-overflow checks on `std::string` and `std::vector`; the only way to keep
   them is to rebuild compiler-rt from source to match.
2. **The static CRT (`/MT`) is the default, because `/MD` cannot link at all** —
   `/failifmismatch: mismatch detected for 'RuntimeLibrary'`, since that same library
   is built against the static CRT. Consequence worth knowing when reading a finding:
   the allocator under test is the static CRT's, not the DLL's that the shipped server
   uses.
3. **`clang_rt.asan_dynamic-x86_64.dll` must sit next to the executables even under
   `/MT`.** A `/MT` target still imports it and still dies with `0xc0000135`
   (`STATUS_DLL_NOT_FOUND`) before running a single input. `build-fuzz.ps1` copies it
   for both CRT choices; it used to copy only for `/MD`, on the reasonable-sounding
   but wrong assumption that a static CRT implies a static ASan.

### And why the harness has its own `stdafx.h`

`fuzz\harness\shim\stdafx.h` is first on the include path so that a server source
file's `#include "stdafx.h"` finds it instead of the real one. The real header
`#import`s the ADO type library, which clang rejects outright — *"#import of type
library is an unsupported Microsoft feature"* — and pulls in ATL; a parser needs
neither. The shim also has to include `Util/StdString.h`, because server sources use
`String` and `AnsiString` without including it themselves, relying on the
precompiled header.

**Watch out for a clang that is not this one.** `clang-cl` turns up on PATH from
all sorts of places — a Swift toolchain, an Android NDK, a Chocolatey LLVM — and
those builds compile this code perfectly well but do not ship
`clang_rt.fuzzer-x86_64.lib`. The symptom is `LNK1181` twenty minutes into a
build. `build-fuzz.ps1` prefers the Visual Studio toolchain and checks for the
runtime libraries before it compiles anything, so it fails immediately with the
install instructions instead.

```powershell
.\fuzz\build-fuzz.ps1
```

The build:

* finds clang-cl through `vswhere` and imports the MSVC environment itself, so it
  works from an ordinary PowerShell prompt (clang-cl links with `link.exe` and
  against the MSVC CRT, so that environment is not optional);
* compiles the five real `Common\Mime` sources (`Mime`, `MimeChar`, `MimeCode`,
  `MimeType`, `CodePages`) plus the five utility translation units they need
  (`Charset`, `ByteBuffer`, `Unicode`, `RegularExpression`, `StringParser`), with
  `-fsanitize=fuzzer-no-link,address`;
* links one executable per target with `-fsanitize=fuzzer,address` into
  `fuzz\bin\`;
* copies `clang_rt.asan_dynamic-x86_64.dll` next to the executables **for both CRT
  choices**, because a `/MT` target imports it too — see point 3 above, which is the
  bug this bullet used to repeat;
* regenerates the seed corpus from the real test messages.

It never builds `hMailServer.sln`, never runs its pre/post-build events and never
touches the installed service, so it cannot disturb a regression run in progress.

**Boost**: two of the compiled translation units use header-only Boost
(`boost::regex` in `RegularExpression.cpp`, `boost::tokenizer` and
`boost::lexical_cast` in `StringParser.cpp`). The script picks them up from
`%hMailServerLibs%\boost_1_91_0` like the main build does, or from
`-BoostInclude <dir>`. No Boost library is linked.

Running it
----------

```powershell
# a five-minute smoke test - do this first, and after any parser change
.\fuzz\run-fuzz.ps1 -Target mime_message_fuzzer -Minutes 5

# seconds: replay the seed corpus and everything previous runs kept, then exit
.\fuzz\run-fuzz.ps1 -Target mime_header_fuzzer -Replay

# overnight, four cores
.\fuzz\run-fuzz.ps1 -Target mime_message_fuzzer -Minutes 480 -Jobs 4
```

The three targets, and what each is for:

| Target | Input | Why it is separate |
|---|---|---|
| `mime_message_fuzzer` | a whole message | `MimeBody::Load` is what every spooled message goes through, and the only entry point that recurses (nested `multipart`, nested `message/rfc822`). Also round-trips through `Store()` and re-parses, because the serialiser runs on attacker-controlled structure every time a rule rewrites a header. |
| `mime_header_fuzzer` | a header block | Headers are parsed far more often than bodies — IMAP `FETCH ENVELOPE`, `BODY[HEADER]`, the rule engine and the DKIM signer all load headers alone. Smaller inputs mean a much higher execution rate and a deeper walk into folding and parameter parsing. Runs all three shapes production uses, including `Utilities::GetMimeHeader`'s truncated length. |
| `mime_decode_fuzzer` | selector byte + payload | Spends every execution inside the codecs (base64, quoted-printable, RFC 2047 encoded words, RFC 2231 parameter continuations) instead of first having to produce a plausible header. |

### How long

Fuzzing is not a pass/fail gate you run once; it is a background process with a
long tail. Useful budgets:

* **5 minutes per target** after any change to `Common\Mime` or to something it
  calls. This re-runs the whole corpus and mutates around it, and it is fast
  enough to be habitual. It belongs in the same reflex as running the suite.
* **A few hours** the first time a target is ever run, and after a real change to
  the parser's structure. Watch the `cov:` counter in libFuzzer's output: while it
  is still climbing, the run is still learning. When it has been flat for an hour,
  more of the same is unlikely to help.
* **Overnight with `-Jobs 4`** before a release that changed message handling.
* Between runs, keep the corpus. `fuzz\corpus\<target>\` accumulates every input
  libFuzzer found interesting, and it is the single most valuable thing the
  exercise produces — a later run starts from that coverage instead of from
  scratch. It is machine-local and not committed.

Once a corpus gets large, minimise it before a long run. `-merge=1` keeps only
the inputs needed to hold the same coverage, which makes every subsequent
execution cheaper:

```powershell
mkdir fuzz\corpus\merged
.\fuzz\bin\mime_message_fuzzer.exe -merge=1 fuzz\corpus\merged fuzz\corpus\mime_message_fuzzer
```

### Two practical notes

**Antivirus.** Exclude `fuzz\` from real-time scanning. A run writes corpus
entries and crash artifacts continuously — thousands of small files that each
look like a malformed email — and this project has already lost whole regression
runs to security software interfering with the test environment
(`build\preflight-tests.ps1` warns about the interference sources it can detect).
A scanner that quarantines an artifact mid-run destroys the one thing a finding is
worth: the exact bytes.

**Do not run it on the mail server.** The targets are ordinary user-mode
processes and do not touch the service, but a fuzz run will happily use every
core it is given.

What to do with a finding
-------------------------

libFuzzer stops on the first finding, prints the report, and writes the input
that produced it to `fuzz\artifacts\<target>\crash-<sha1>` (or `timeout-`,
`oom-`, `leak-`). `run-fuzz.ps1` prints the path and the reproduce command. The
exit code is `77` for a finding; anything else non-zero means the target could
not start or a flag was rejected.

**1. Reproduce it, deterministically.**

```powershell
.\fuzz\run-fuzz.ps1 -Target mime_header_fuzzer -Reproduce .\fuzz\artifacts\mime_header_fuzzer\crash-<sha1>
```

One input, one execution, no mutation, no corpus. If it does not reproduce, the
finding depended on state that leaked between executions — which is itself a bug,
in the harness or in the parser's static state, and worth chasing rather than
shrugging at.

**2. Work out what kind of finding it is**, because three of the four kinds are
routinely misread:

* **`heap-buffer-overflow READ` of one or two bytes** — the most common finding in
  this parser and the easiest to dismiss, because in production the byte being
  read usually exists (it is inside the same allocation's slack, or it is the NUL
  terminator's neighbour). Do not dismiss it. It is a real out-of-bounds read;
  whether it faults depends on the allocator's mood, and the value read steers
  the parse.
* **`stack-overflow`** — read the stack trace before filing. If the repeated
  frames are `MimeBody::Load`, that is a genuine finding: the parser recurses once
  per nesting level with no depth limit, so a small message can build a very deep
  tree, and a deep enough one exhausts the stack. If the repeated frames are
  `hm_fuzz::ExerciseBody`, the *harness* ran out of stack on a tree the parser
  survived; raise nothing and lower `kMaxTraversalDepth` instead.
* **`timeout` / `oom`** — a parser that stops making progress or allocates without
  bound is a remote denial of service, which for a mail server is a real
  vulnerability rather than a performance note. `MimeEncodedWord::BEncode` **had** a
  loop of exactly this shape, and it is the example to keep in mind because the
  cause was arithmetic rather than buffer handling: its block size was
  `(MAX_ENCODEDWORD_LEN - charset_length - 7) / 4 * 3`, and since that constant is
  75, a charset name of 69 characters or more drove it to zero or below — a
  zero-size block means the loop makes no progress while appending an encoded-word
  header to its output every iteration, so it never terminated at all. The only
  guard was an `ASSERT`, compiled out of Release. Both `BEncode` and `QEncode` now
  fall back to the raw encoder when no legal encoded word can be built
  (`if (nMaxBlockSize < 3)`), and the reproducer lives in
  `fuzz\regression\mime_decode_fuzzer\out-of-memory-mimeunicodeencoder-encodevalue`,
  so every run re-proves it. Read the shape, not the location: the same "budget goes
  non-positive, loop still appends" pattern is what to look for in the next one.
* **An assertion abort** — only possible if the target was built with
  `-Asserts`. The shipped Release build compiles `ASSERT` to `((void)0)`, so an
  assertion failure is a violated internal invariant, not a crash. Interesting,
  but a different investigation, and not something to fix by "adding a check" at
  the assert.

**3. Minimise the input.** libFuzzer will shrink it for you, which usually turns
4 KB of mutated mail into a dozen bytes and makes the actual defect obvious:

```powershell
.\fuzz\bin\mime_header_fuzzer.exe -minimize_crash=1 -runs=100000 `
    -exact_artifact_path=fuzz\artifacts\minimised .\fuzz\artifacts\mime_header_fuzzer\crash-<sha1>
```

**4. Fix it in `Common\Mime`, and write a suite test if the bug is reachable over
a protocol.** Most of them are: a header value reaches the RFC 2047 decoder
through IMAP `FETCH ENVELOPE`, a `Content-Disposition` filename reaches the
parameter decoder through the attachment API, a nested multipart reaches the
recursion through plain SMTP delivery. A corpus entry only proves the process no
longer dies, only runs when somebody runs the fuzzer, and says nothing about the
parser now doing the *right* thing. The regression suite runs on every release;
the fuzzer does not.

**5. Keep the input forever.** Move the artifact into
`fuzz\regression\<target>\` with a name that says what it was, and commit it.
Every run — including the seconds-long `-Replay` — re-executes it from then on.
The rules are in [`fuzz\regression\README.md`](../../fuzz/regression/README.md);
the important one is that these files are byte-exact and marked binary in
`fuzz\.gitattributes`, because a reproducer whose line endings got normalised
still looks like a reproducer and no longer reproduces anything.

How the harness stays honest
----------------------------

Three decisions in the harness exist to stop it reporting things that are not
bugs. They are worth knowing, because each one looks like a mistake until you
know why:

* **The input is copied and NUL-terminated** before being handed to the parser.
  That is the production contract — `File::ReadTextFile` appends a NUL and
  `LoadFromFile` passes a length that excludes it — and `MimeField::Load` relies
  on it, reading one past the counted length in its folding loop. Handing
  libFuzzer's raw buffer straight in would report a heap overflow on nearly every
  input, all of them false.
* **`ASSERT` is a no-op**, matching Release. `MimeBody::Load` asserts that a
  multipart part has a non-empty boundary, which is one malformed message away;
  with asserts live, that would be the only finding you ever saw. The server's
  own `HM_ASSERT` (the lowercase `assert` sites were renamed to it on 5 September
  2026) maps onto `ASSERT` in the shim, so `-Asserts` governs both spellings.
  The regression-suite counterpart is `build\build.ps1 -Configuration Release
  -Asserts`, where a violated assertion is reported as HM6364 rather than
  aborting, and the test that provoked it is the one that fails.
* **The harness catches what production catches** and nothing more. `catch (...)`
  in `LoadFromFile` means an escaping `std::logic_error` or `std::bad_alloc` is
  not a production crash, and letting it escape the target would call `abort()`
  and be reported as one. Memory errors are traps, not exceptions, so ASan still
  sees them first.

If a change to the harness starts producing large numbers of findings at once,
suspect one of these three before believing the parser suddenly got worse.

What is not covered yet
-----------------------

* **HTML**, reached through the HTML-to-text conversion and message rewriting.
* **iCalendar and vCard**, reached through attachment handling.
* **`MimeBody::LoadFromFile`'s byte bookkeeping.** The in-memory targets do not
  cover the `body_byte_offset_` / `body_byte_end_` arithmetic or the
  trailing-CRLF trim, which decide which bytes `SaveAllToFile` copies verbatim.
  Getting that wrong does not crash — it invalidates a DKIM body hash, which is
  arguably worse because nothing local notices. It needs `File` to serve bytes
  from memory; see the comment in `fuzz\harness\fuzz_environment.cpp`.
* **Sieve, ManageSieve and the SMTP command parser.** All three are untrusted
  input; none are wired to a target. The SMTP one is the most exposed.

Each of those is a new target next to the three that exist, not new
infrastructure: the shim, the environment stubs and the build script are the part
that was hard, and they are done.

Verified against the code
-------------------------

Checked 13 August 2026. Confirmed present and as described: `/EHa`
(`<ExceptionHandling>Async</ExceptionHandling>`, both configurations in
`hMailServer.vcxproj`) against the fuzz build's `/EHsc`;
`Common\Util\CrashOracle.{h,cpp}`; `MimeBody::LoadFromFile`'s `catch (...)` and its
copy into `Problematic messages`; the three targets and the ten translation units
`build-fuzz.ps1` compiles; `/MT` as the default `-RuntimeLibrary` and the ASan DLL
copied for both; `_DISABLE_STRING_ANNOTATION` / `_DISABLE_VECTOR_ANNOTATION`;
Boost from `%hMailServerLibs%\boost_1_91_0` or `-BoostInclude`; `run-fuzz.ps1`'s
`-Target` / `-Minutes` / `-Jobs` / `-Replay` / `-Reproduce` and `-error_exitcode=77`;
`kMaxTraversalDepth` and `hm_fuzz::ExerciseBody` in `fuzz_mime_common`;
`mime_header_fuzzer`'s three shapes including `Utilities::GetMimeHeader`'s truncated
length; `fuzz\.gitattributes` marking `regression/**`, `corpus/**` and
`artifacts/**` as `-text -diff` with `regression/README.md` put back to text.

One thing was stale and is now corrected in place: the `BEncode` example above was
written in the present tense after the defect had been fixed. If you are updating
this page, that is the failure mode to watch for — a document about finding bugs is
the one most likely to keep describing them after they are gone.
