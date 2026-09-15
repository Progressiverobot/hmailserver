<!--
  https://www.progressiverobot.com
  Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
  SPDX-License-Identifier: AGPL-3.0-or-later
-->

# The RFC 7208 conformance suite

This directory holds the openspf.org test suite for RFC 7208, and what is needed
to run hMailServer's SPF evaluator against it. It came here with the evaluator, as
part of the port of upstream's `1beaeab9`; the runner and the generator carry
their provenance in their own headers, and the three vendored files below are not
upstream's to begin with.

## The vendored suite

| File | |
|---|---|
| `rfc7208-tests.yml` | The suite. Release 2014.04, 203 cases in 16 sections. |
| `rfc7208-tests.LICENSE` | Its licence: three-clause BSD, © Stuart D Gathman, Julian Mehnle, Scott Kitterman. |
| `rfc7208-tests.CHANGES` | Its changelog, which says what each release of the suite changed. |

The suite was published at <http://www.openspf.org/Test_Suite>. That site no
longer serves it; these copies were taken from pyspf, which carries the suite
and its own driver for it:

```
https://raw.githubusercontent.com/sdgathman/pyspf/master/test/rfc7208-tests.yml
https://raw.githubusercontent.com/sdgathman/pyspf/master/test/rfc7208-tests.LICENSE
https://raw.githubusercontent.com/sdgathman/pyspf/master/test/rfc7208-tests.CHANGES
```

The three files are vendored unchanged and carry no header of this project's:
they are not this project's files. The licence asks that the copyright notice be
retained, which is what `rfc7208-tests.LICENSE` is for; keep it alongside the
suite, and do not edit `rfc7208-tests.yml` — a local fix to a case would quietly
stop this from being the suite everyone else runs. It is listed in
`hmailserver/docs/Licenses/` with the other third-party notices.

## The generated table

Nothing in hMailServer reads YAML, and adding a parser to the server build in
order to read a test fixture would be the wrong trade. So the suite is
transcribed into a C++ table instead:

```
python3 Generate-SPFConformanceSuite.py
```

That rewrites `SPFConformanceSuite.cpp`, which is **committed**. Committing it is
what keeps Python and PyYAML out of the MSVC build: the generator only has to
run again when the vendored suite is updated. It is deterministic - the same
input produces the same file - so a regeneration that changes nothing produces
no diff.

Two parts of the transcription are decisions rather than copying, and the
generator's own comments give them in full:

- **Records of type SPF.** The suite predates RFC 7208 and publishes most of its
  policies as records of the deprecated type SPF (RR type 99), expecting the
  driver to present them as the TXT records that an evaluator following RFC 7208
  would find. It does not expect that everywhere: the "Selecting records"
  section exists to check that type SPF is ignored. The rule that satisfies
  both, and the one the suite's own driver uses, is per host - a host's
  type-SPF records stand in for a TXT record only where that host publishes no
  TXT record of its own. The generator applies it, so the emitted table holds no
  record of type SPF at all.
- **Timeouts.** A zone can be marked as not answering. In the suite's driver the
  effect depends on where the marker sits among the zone's records; in this file
  it is always last, which the generator asserts, so the table can carry it as a
  flag on the zone.

## Running the suite

`SPFConformanceTester` is built by both the Windows project and the CMake build,
and run from both. The cases are the same either way, and none of them reaches a
network - `SPFConformanceLookup` answers an evaluation's DNS queries out of one
section of the table, through the `SPFDnsLookup` interface the evaluator takes -
so a whole run costs milliseconds.

**Under Visual Studio**, where a case can be stepped through: build
`hMailServer.sln`, set the debug command line of the hMailServer project to
`/Test`, and run. `ClassTester::DoTests()` runs every C++ self-test in the
server, this one among them; breakpoints in `SPFConformanceTester.cpp` and
`SPFConformanceLookup.cpp` are hit like any other. Failures go to the Output
window, one line each, before the run is failed. The Windows regression suite
reaches the same place through `Utilities.RunTestSuite`.

**On Linux**, as the binary the CMake build makes for it:

```
cmake -S hmailserver/source/Server -B build/linux -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build build/linux --target spf-tests
./build/linux/spf-tests
```

`.github/workflows/linux-build.yml` runs exactly that on x86-64 and on AArch64,
under clang and under GCC. The binary prints one line per failure and the number
of cases decided either way, and exits non-zero if the count has fallen below the
203 the table holds - a case that stops being recognised would otherwise look
exactly like a suite with nothing wrong with it.

Note that inside `/Test` a failure fails the whole run, which stops the
self-tests that would have come after it. The failure lines are written first, so
the Output window still says which cases failed. The Linux binary has no such
problem: it runs all four testers and reports everything.

A failing case is named by the key it is written under in `rfc7208-tests.yml`.
Its description, and the commentary on what it is for and why RFC 7208 says what
it does, are there; they are deliberately not duplicated into the table.
