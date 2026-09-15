<!--
  https://www.progressiverobot.com
  Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
  SPDX-License-Identifier: AGPL-3.0-or-later
-->

# SPF

An implementation of RFC 7208. `SPF::Test` is the entry point; everything else is
reachable only from there and from the tests.

| File | What it does |
|---|---|
| `SPFSyntax` | The ABNF of section 12 and the macro rules of section 7.1 |
| `SPFRecord` | Parses a record into directives and modifiers |
| `SPFRecordLocator` | Finds and parses the record a domain publishes, sections 4.3-4.5 |
| `SPFMacroExpander` | Macro expansion, section 7 |
| `SPFAddress` | An IP address in the forms SPF needs |
| `SPFEvaluator` | `check_host()`, section 4 |
| `SPFDnsResolver` | The DNS seam, over `DNSResolver` |
| `SPFResult.h` | The seven results of section 2.6 |

`SPFDnsLookup` is the seam the evaluator asks DNS through. `SPFDnsResolver`
implements it for the server; `SPFTestLookup` and `Conformance/SPFConformanceLookup`
implement it for tests.

## Where it came from, and what it replaced

Ported from hMailServer upstream, commit `1beaeab9` ("Replace SPF evaluator", their
pull request 645) of 13 September 2026. Every file carries its provenance in its
own header. Both trees are AGPL-3.0-or-later.

What it replaced was `RMSPF.cpp`: RMSPF 1.10 by Roger Moser, 4,828 lines of C
written against RFC 4408, vendored here since the beginning. It reported three
results where the RFC has seven, and — the reason this port matters more than a
conformance number — **it did its own DNS**. It resolved through `DnsQuery_A` from
DNSAPI.DLL, not through this server's `DNSResolver`, and that had three
consequences:

* **On Linux SPF could not work at all.** The POSIX build had a shim that refused
  every query and reported HM6406 once per process, so every SPF check returned
  TempError — and, because `SpamTestDMARC` evaluates SPF, every DMARC decision on
  that platform lost its SPF half.
* **On Windows SPF ignored `DNSServer`**, the configured resolver every other
  lookup in the server honours.
* **No test could put a zone in front of it.** The suite's `SuiteDns` zone was
  invisible to it, so no fixture could assert an SPF verdict, and the void-lookup
  limit had to be pinned by feeding the library a policy by hand.

All three are gone: `SPFDnsResolver` asks `DNSResolver::GetRecordsOfType`, which
is the same query path as every other name this server looks up.

One more thing went with it, and it is worth knowing because it changes a verdict:
RMSPF returned a pass for the whole of `127.0.0.0/8` and for `::1` **before reading
any policy**. RFC 7208 has no such exemption, and this evaluator has none. It only
ever mattered to an unauthenticated sender connecting over loopback in an IP range
with spam protection on — an authenticated one is not spam-checked at all — but
inside the regression suite that is every message, and two DMARC fixtures had come
to depend on it without saying so.

## Why some things are their own

**`SPFAddress` rather than `IPAddress`.** SPF needs three things `IPAddress` does
not answer: an IPv6 address as 32 dotted nibbles for `%{i}`, the shortened form for
`%{c}`, and the address literals of section 12, which are spelled more strictly
than a resolver insists on.

**`SPFResult` in its own header.** `SPF` is the service entry point and brings the
singleton and the wide string type with it. The evaluator, the record locator and
the conformance suite are all built by the portable build and need to name a
result.

**`DNSResolver::GetRecordsOfType`.** The typed methods beside it each add
behaviour SPF must not have: `GetIpAddresses` merges A and AAAA and gates AAAA on
this server's IPv6 configuration, where sections 5.3 and 5.4 select by the family
the client connected over; `GetPTRRecords` builds a reverse-mapping name from an
address, which an evaluation has already built; `GetMXRecords` reports the null MX
of RFC 7505 the way it reports a failed query, where section 5.4 reads it as a
domain with no exchangers. It answers in bytes rather than in `String`, because a
TXT record may hold a byte the wide conversion has no business interpreting and
section 3.1 needs the evaluator to see that byte in order to reject the record.

**DNSSEC.** A TXT lookup made through `GetRecordsOfType` goes through the same
DNSSEC gate `GetTXTRecords` applies — a bogus chain fails the lookup, which is a
temperror — so an SPF policy is trusted exactly as far as a DKIM key or a DMARC
policy is. The old library reached the same behaviour through a one-function
bridge, `DnssecTxtLookupIsBogus`, because it could not consume `DnssecResolver`;
that bridge is still there for nothing else and is now only called from the
resolver's own code path.

## How the pieces fit

A record is parsed whole before any of it is used, which section 4.6 requires: a
mistake after a mechanism that would have matched is still a permerror.

A term's target is stored as written. Expansion depends on the message, and
section 7.1 does not re-parse what an expansion produces - a name that comes out
unusable is a name that does not exist, so the mechanism does not match.

Section 7.3's transformers rejoin the parts with dots whatever delimiter they were
split at, which is the point of naming one: the section's own example has `%{l-}`
turn `strong-bad` into `strong.bad`.

Section 4.6.4's limits are per check, not per record, so one counter sees every
query an evaluation makes including the macro expander's. It counts *terms* whose
queries answer nothing, not queries: one `mx` over five exchangers with no address
of the client's family is one such term.

The void-term limit is the RFC's two unless this server says otherwise.
`SpfVoidLookupLimit` in `hMailServer.ini` raises it, or switches it off with 0;
that setting has been here since 20 August 2026, the Control Panel offers it, and
the port kept it rather than hard-coding the number over the top of it. Only
`SPF::Test` reads it — every tester in this directory measures against the RFC's
own number, because a bench whose answer depended on a local setting would not be
measuring the RFC.

## Testing

`Conformance/` holds the openspf.org suite for RFC 7208: 203 cases, all decided
and all agreeing. It runs in two places, and the testers beside it run in both as
well:

* **Inside the shipping server.** `SPFTester::Test` is called from
  `ClassTester::DoTests`, which the Windows regression suite reaches through
  `Utilities.RunTestSuite`. Under Visual Studio, set the hMailServer project's
  debug command line to `/Test` and every case can be stepped through.
* **As a program of its own on Linux.** The CMake build makes `spf-tests` from
  `platform/spf_tests_main.cpp`, and `.github/workflows/linux-build.yml` runs it on
  x86-64 and AArch64, under clang and under GCC. That route exists because the
  first one does not here: `RunTestSuite` is a COM method and the Linux suite's
  shim skips it.

Neither reaches a network. `SPFConformanceLookup` answers an evaluation's queries
out of one section of the vendored table, and `SPFTestLookup` out of a table the
test writes, so a whole run costs milliseconds and its answer depends on nothing
outside this repository.

The testers cover what the suite does not. Each was found by breaking a behaviour
and checking whether any case noticed:

* A lookup that fails. Sections 5.3 and 5.4 make a failed A, AAAA or MX query a
  temperror, but the suite only has a case for `exists`.
* Section 4.6.4's term counting. The suite's `mech-over-limit` spends its budget
  on names that do not exist, so it trips the void limit first, and
  `redirect-loop` recurses rather than counting.
* What the resolver hands back. Every address in the suite's zones is one.
* That section 6.2 explains a fail and only a fail. All 22 cases that assert an
  explanation are fails.
* The grammar rule by rule, which `SPFRecordTester` does: the suite has no case
  for a zero-length prefix on `ip6`, or for `a/24//64` against `a/24/64`.

Failures are returned from a tester rather than reported, because where they
belong differs between the two hosts, and a run should list all of them.

**End to end**, with the server actually running:
`RegressionTests/AntiSpam/SpfEvaluation.cs` publishes a policy in the suite's own
DNS zone and asserts the verdict in both trace headers for a pass, a fail, a
softfail and a none. That fixture could not be written before this port — the old
library never saw the suite's zone — and it is what proves the evaluator is wired
to this server's resolver rather than merely correct in isolation. It is in the
Linux test project too.

## Open questions

Two places where the grammar is deliberately lenient and the suite tests neither:
a zero digit transformer (`%{d0}`), which is accepted, and an unterminated `%{`,
which is rejected. A corpus of real records is what would settle them.

## Traps

A record may hold a NUL or a byte above 0x7f, so records cross `SPFDnsLookup` as
counted bytes. Two of the suite's policies end in a NUL. The Windows DNS client
hands a TXT record over as a C string and so cannot deliver a NUL to begin with;
the seam is counted anyway, because the conformance lookup and the POSIX resolver
both can.

`MimeCode` folds a header value at 76 characters on the last space, and
`Application` turns folding on. A test asserting on `Authentication-Results` must
unfold it first, or it will pass for one sender and fail for another depending on
how long the fields ahead of the one it wants happen to be.

`DOMAIN` is a macro in MSVC's `math.h`. The portable build cannot see that.
