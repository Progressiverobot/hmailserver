Assurance case
==============

This document states what hMailServer claims about its own security, and the
argument and evidence for each claim. It is written for someone deciding
whether to run this on the public internet, and for anyone auditing that
decision.

It is deliberately *not* a marketing document. Where a claim is limited, the
limit is stated. Where a risk is accepted rather than eliminated, it is named
in [Residual risk](#residual-risk).

Contents:

1. [What the software is](#1-what-the-software-is)
2. [Security requirements — the claims](#2-security-requirements--the-claims)
3. [Threat model](#3-threat-model)
4. [Trust boundaries](#4-trust-boundaries)
5. [Argument: secure design principles are applied](#5-argument-secure-design-principles-are-applied)
6. [Argument: common implementation weaknesses are countered](#6-argument-common-implementation-weaknesses-are-countered)
7. [Evidence](#7-evidence)
8. [Residual risk](#8-residual-risk)

---

1. What the software is
-----------------------

hMailServer is a mail server for Windows: an SMTP, IMAP and POP3
implementation, with a management COM API, a Control Panel GUI, and optional
REST API, metrics and web-services listeners. It stores mail on the local
filesystem and configuration and accounts in a SQL database. It talks to the
public internet by design, on ports that are open to anyone.

That last point sets the security posture: **the primary interfaces are
exposed to unauthenticated strangers**, and most of them must accept input
before any authentication has happened.

---

2. Security requirements — the claims
-------------------------------------

These are the properties the project claims and argues for below.

| # | Claim |
|---|---|
| **C1** | An unauthenticated network peer cannot execute code, corrupt memory, or crash the service through the mail protocols. |
| **C2** | An unauthenticated peer cannot read, modify or delete mail belonging to an account, nor authenticate as one. |
| **C3** | Credentials at rest cannot be practically recovered from a stolen database. |
| **C4** | Credentials in transit are not exposed to a network attacker, and are never sent to an unverified peer. |
| **C5** | Untrusted message content cannot alter the meaning of a database query, a filesystem path, or a protocol exchange with a third party. |
| **C6** | An authenticated ordinary user cannot reach another user's mail, nor gain administrative capability. |
| **C7** | A user can verify that the software they downloaded is the software the project published. |

What is explicitly **not** claimed is listed in
[SECURITY.md](.github/SECURITY.md) under *Out of scope*, and repeated in
[Residual risk](#8-residual-risk).

---

3. Threat model
---------------

### Actors

| Actor | Position | Capability assumed |
|---|---|---|
| **A1 — Internet stranger** | Can open TCP connections to any listener | Arbitrary bytes, arbitrary volume, arbitrary timing, protocol non-compliance |
| **A2 — Sending MTA** | Delivers mail inbound | Everything A1 has, plus fully-controlled message content, headers and MIME structure |
| **A3 — Network attacker** | On-path between this server and a peer | Observe, modify, drop, replay; present a certificate; downgrade attempts |
| **A4 — Authenticated user** | Holds valid credentials for one account | Everything A2 has, plus authenticated IMAP/POP3/SMTP submission |
| **A5 — Malicious upstream** | Controls a DNS answer, a TLSA record, or a remote MTA | Poisoned resolution results, hostile TLS peer behaviour |
| **A6 — Local attacker** | Has a foothold on the host | Read files the service can read, observe process memory |

Administrators are **not** modelled as adversaries: an administrator can run
event scripts and change configuration by design, so administrative access is
equivalent to control of the service. This is stated in
[SECURITY.md](.github/SECURITY.md) and is a deliberate boundary, not an
oversight.

### Assets

Mail in transit and at rest; account credentials; TLS private keys; the
configuration database; the availability of the service itself.

### Principal attack surfaces

- **Protocol parsers** (SMTP, IMAP, POP3) — reachable pre-authentication by A1
  and A2. The largest and most dangerous surface.
- **MIME and header parsing** — reachable by anyone who can send mail.
- **TLS layer and certificate handling** — reachable by A1, A3 and A5.
- **Anti-spam and anti-virus paths**, including DNS lookups and handoff to
  external scanners — driven by attacker-supplied content.
- **DNS resolution**, including DNSSEC, DANE and MTA-STS — influenced by A5.
- **The persistence layer**, reached indirectly with attacker-influenced data.
- **REST API, metrics and web-services listeners**, where enabled.

---

4. Trust boundaries
-------------------

Each boundary below is a place where data changes trust level and must be
checked.

```
                    ┌──────────────── UNTRUSTED ────────────────┐
   A1/A2/A3  ──▶    │  TCP + TLS termination                    │   ◀── B1
                    │  Server/Common/TCPIP                      │
                    └───────────────────┬───────────────────────┘
                                        │  B2: protocol grammar
                    ┌───────────────────▼───────────────────────┐
                    │  SMTP / IMAP / POP3 command parsers       │
                    │  MIME + header parsing                    │
                    └───────────────────┬───────────────────────┘
             B3: authentication         │
                    ┌───────────────────▼───────────────────────┐
                    │  Business objects (BO)  — per-account      │
                    └──────┬─────────────────────┬───────────────┘
          B4: query         │                     │  B5: process + filesystem
                    ┌───────▼────────┐    ┌───────▼───────────────┐
                    │  Persistence   │    │  ClamAV / SpamAssassin│
                    │  → SQL         │    │  event scripts, DNS   │
                    └────────────────┘    └───────────────────────┘

              B6: management plane — COM API ◀── Control Panel, scripts
```

- **B1 — Network to process.** Everything arriving is hostile until parsed.
  TLS is terminated here; peer verification happens *before* application data
  moves (see C4).
- **B2 — Bytes to protocol grammar.** Each command is accepted only in the
  form its grammar defines; anything else is rejected rather than
  interpreted generously.
- **B3 — Anonymous to authenticated.** Crossing it requires a credential
  check against a stored key-stretched hash. Below this line, an identity
  exists and every mail access is scoped to it.
- **B4 — Program to database.** Crossed only through parameterised
  statements; content never becomes query structure.
- **B5 — Process to external programs and the network.** Content handed to
  ClamAV, SpamAssassin, event scripts or DNS is untrusted in both directions,
  and every such call is time-bounded.
- **B6 — Management plane.** The COM API is the single seam through which
  configuration and management happen; it sits inside the administrative
  trust boundary described above.

---

5. Argument: secure design principles are applied
-------------------------------------------------

**Economy of mechanism / single seam.** All configuration and management flows
through one interface — the COM API in `Server/COM/` — used by the Control
Panel, the regression suite and external scripts alike. There is no second,
lightly-tested management path to audit. This is the layering documented in
[ARCHITECTURE.md](ARCHITECTURE.md).

**Complete mediation.** Mail access is mediated per account below B3; the
persistence layer is reached only through the business objects, not directly
from protocol code.

**Fail-safe defaults.** The server refuses to store a *new* secret under a
scheme that is not a password hash: configuring `PreferredHashAlgorithm` to
None, Blowfish or MD5 is logged as a misconfiguration and silently upgraded to
PBKDF2 rather than honoured. TLS peer verification is on by default, and a
verification failure fails the handshake rather than downgrading the
connection.

**Least privilege, explicitly bounded.** The administrative boundary is
declared rather than assumed: administrators can run scripts and change
configuration by design, and reports predicated on already having
administrator access are out of scope. Drawing the line publicly is itself a
design decision — it tells operators exactly what administrative access is
worth to an attacker.

**Defence in depth.** No single control is relied on. Transport security is
layered (STARTTLS and implicit TLS, DANE, MTA-STS, per-route TLS policy);
content is filtered by more than one mechanism; and memory-safety risk is
attacked from several directions at once (see §6).

**Separation of secrets from code and configuration.** TLS private keys are
files referenced by path; password hashes live in the database; the optional
server-wide pepper is an INI setting. No credential is compiled in.

**Cryptographic agility.** Password hashing (SHA256 / PBKDF2 / Argon2id), the
TLS cipher list, and the TLS key-exchange groups are all configurable, so a
broken primitive can be replaced by configuration rather than by waiting for a
release.

---

6. Argument: common implementation weaknesses are countered
-----------------------------------------------------------

### Injection (CWE-89)

SQL is **exclusively parameterised**, as a stated project rule in
[CONTRIBUTING.md](.github/CONTRIBUTING.md): *"Use parameterised SQL
exclusively — never build SQL strings manually."* Message and header content
therefore never becomes query structure. The rule is enforced at review, and
the persistence layer offers parameterised interfaces as the path of least
resistance.

### Memory corruption (CWE-119, CWE-787, CWE-125)

This is C++, so this class cannot be argued away — it is attacked from four
directions:

- **Compiler-enforced hygiene.** The native build treats warnings as errors
  (`TreatWarningAsError`, MSVC `/WX`), so a new warning stops the build.
- **Platform hardening.** `/GS` buffer security checks, DEP (`/NXCOMPAT`) and
  ASLR (`/DYNAMICBASE`) are active on the shipped x64 binaries.
- **Static analysis.** CodeQL runs on every push and pull request.
- **Fuzzing.** A libFuzzer harness suite lives under `fuzz/`, with a corpus,
  dictionaries and a regression directory, aimed at exactly the
  pre-authentication parsers that A1 and A2 can reach.

### Protocol-level smuggling (CWE-444)

SMTP line-terminator handling is deliberately restrictive. Bare-LF spellings
of the terminator are accepted only when *Allow incorrect line endings* is
enabled, and even then are recognised only at the very end of the buffer with
anything behind one discarded — the CVE-2023-51764 SMTP-smuggling rule. This
behaviour is pinned by its own regression test, so it cannot be relaxed by
accident.

### Broken authentication and credential storage (CWE-287, CWE-916)

Passwords are stored as per-user salted, key-stretched hashes — PBKDF2 by
default, Argon2id available — with salts from OpenSSL `RAND_bytes` and an
optional HMAC pepper mixed into Argon2id hashes. A minimum acceptable scheme
can be configured so legacy weak hashes stop being accepted for
authentication. SCRAM-SHA-256 is supported, and TOTP is available as a second
factor.

### Weak randomness (CWE-338)

All security-relevant randomness comes from OpenSSL's CSPRNG (`RAND_bytes`),
with every call checked for failure: password-hash salts, SCRAM nonces, TLS
session-ticket keys and IVs, app passwords, TOTP secrets, REST API key
material and DNS/DNSSEC transaction IDs. Non-cryptographic generators appear
only in MIME part-boundary strings and OpenTelemetry trace identifiers,
neither of which is a security mechanism. A previous use of `rand()` for a
DNS-adjacent value was identified and replaced.

### Improper certificate validation (CWE-295)

Outbound TLS sets `verify_peer` with `verify_fail_if_no_peer_cert` and
installs a verification callback that checks the chain and the expected
hostname, with a DANE verifier where a TLSA record applies. Because this is
installed before the handshake completes, credentials cannot be sent to an
unverified peer — which is claim C4.

### Uncontrolled resource consumption (CWE-400)

Availability is treated as a security property, not a performance one. Several
unbounded waits were found and fixed as defects in this fork — database-pool
acquisition, event scripts, the custom virus scanner, the delivery-side ClamAV
read/write timeout, and spam-test DNS lookups, which previously had no
application-level timeout at all. Every crossing of boundary B5 is now
time-bounded.

### Supply-chain and integrity weaknesses (CWE-494)

Every release asset is signed with Sigstore cosign (keyless) and verified by
the workflow before upload, giving claim C7. Dependencies are enumerated in an
SBOM, monitored by Dependabot, and gated by dependency review. Every vendored
binary is inventoried with a SHA-256 and its provenance, and an unlisted or
changed binary fails the build — closing the "a DLL appeared and nobody
noticed" gap that a diff review cannot catch.

---

7. Evidence
-----------

| Claim | Where the evidence is |
|---|---|
| C1 | `fuzz/` harnesses; CodeQL workflow; `/WX` in `hMailServer.vcxproj`; the regression suite under `hmailserver/test/RegressionTests` |
| C2 | Authentication path and per-account scoping in `Server/Common/BO/`; regression tests for authentication |
| C3 | `Server/Common/Util/Hashing/HashCreator.cpp`; `IniFileSettings.cpp` hash-scheme handling |
| C4 | `Server/Common/TCPIP/TCPConnection.cpp` verify mode and callbacks; `SslContextInitializer.cpp` |
| C5 | Parameterised-SQL rule in `CONTRIBUTING.md`; persistence layer; SMTP terminator tests |
| C6 | Account scoping in the BO layer; administrative boundary in `SECURITY.md` |
| C7 | `.github/workflows/sign-release.yml`; `verify-binary-provenance.yml`; `sbom.yml` |

Release validation is not a smoke test: the full regression suite runs against
the exact binary being shipped, with live SpamAssassin and ClamAV (real EICAR
detection), DMARC evaluated against live DNS, and TLS 1.2 and 1.3 handshakes
end to end. Nothing is mocked or skipped.

---

8. Residual risk
----------------

Stated plainly, because an assurance case that claims no residual risk is not
credible.

- **Memory safety is mitigated, not guaranteed.** This is a large C++
  codebase with a pre-authentication attack surface. The controls in §6 raise
  the cost of exploitation; they do not make it impossible.
- **Statement coverage is not uniform.** The .NET Control Panel is measured
  for coverage in CI; the native server is not currently measured, so the
  regression suite's coverage of the C++ code is not quantified.
- **The build is not bit-for-bit reproducible.** A third party cannot
  currently rebuild a released binary and confirm it matches. Artifact
  signatures establish *who published* a binary, not that it corresponds to
  the source.
- **Control Flow Guard is not enabled.** `/guard:cf` is a candidate hardening
  addition and is not currently on.
- **Bus factor is 1.** A single maintainer performs security triage and
  releases. See [GOVERNANCE.md](GOVERNANCE.md) — a slow security response is a
  realistic failure mode.
- **Administrators are inside the boundary.** Anyone who can administer the
  server can run code on it. Operators must treat administrative credentials
  as equivalent to host credentials.
- **Operator-chosen weakening is possible.** The server permits plaintext
  listeners and legacy hash schemes for compatibility. It warns, but it obeys.

Reviewing this document
-----------------------

This assurance case should be revisited whenever the threat model changes — a
new listener, a new external integration, a new authentication mechanism — and
at minimum reviewed against the codebase once a year. Corrections are welcome
through the normal pull-request process described in
[CONTRIBUTING.md](.github/CONTRIBUTING.md).
