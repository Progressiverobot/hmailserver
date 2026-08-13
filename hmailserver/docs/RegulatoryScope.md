Regulatory scope determination
==============================

**Status: out of scope as a manufacturer. Reviewed 12 August 2026.**

This is a dated, reviewable position on whether two pieces of EU legislation —
the Cyber Resilience Act and the revised Product Liability Directive — place
obligations on this project. It exists because both turn on *how the software is
supplied* rather than what it does, so the answer can change without a line of
code changing, and because the answer is much cheaper to write down now than to
reconstruct under pressure later.

It is a self-assessment by the maintainer, not legal advice. Anyone relying on it
commercially should take their own.

Who is asking
-------------

This fork is distributed as follows, and every conclusion below depends on these
facts staying true:

* AGPLv3, source public, no paid edition, no paid support, no hosted service.
* Released as an installer and source on GitHub. No charge, no licence key, no
  mandatory registration, no telemetry.
* Maintained by one individual, not a company or foundation.
* Donations, sponsorship and paid consulting: **none currently**, and see the
  triggers below before that changes.

Cyber Resilience Act — Regulation (EU) 2024/2847
------------------------------------------------

**In force** 10 December 2024. **Article 14 reporting obligations apply from 11
September 2026.** **Everything else — Annex I essential requirements, conformity
assessment, CE marking, technical documentation, declaration of conformity —
applies from 11 December 2027.**

Two independent questions decide whether it reaches this project.

**1. Is it made available in the course of a commercial activity?** That is the
test, and *free of charge is not the test*. Recital 18 says how development is
financed is not to be taken into account. The Commission's guidance under
Article 26 (document C(2026) 5252, approved 27 July 2026) confirms that freely
available open-source software is generally outside scope, and that **voluntary
donations, sponsorship, public funding, and paid consulting or training do not by
themselves make a project commercial**.

Commerciality would arrive with: selling the software; shipping a paid "pro"
edition; monetising a service built on it; requiring personal data beyond what
security or interoperability needs; or making donations effectively mandatory for
access or updates.

**2. Could it be an "open-source software steward" under Article 24?** No — a
steward must be a **legal person**. A sole individual maintainer is neither a
manufacturer (while non-commercial) nor eligible to be a steward.

**Conclusion: out of scope as manufacturer, ineligible as steward.**

**What changes it, and how much.** The moment there is a paid tier, a hosted
edition or a commercial licence exception, the whole product is in scope — and
because this fork substantially modifies upstream hMailServer, **this fork is the
manufacturer of record, not the upstream project.** Two consequences worth
knowing before that decision rather than after:

* A UK-based commercial manufacturer also needs an **EU authorised
  representative** (Article 18) — a real, recurring cost.
* Annex I would then require, in code as well as paper: secure-by-default
  configuration, no known exploitable vulnerabilities at release, security update
  distribution with integrity verification, an SBOM, coordinated vulnerability
  disclosure, and a defined support period.

Product Liability Directive — (EU) 2024/2853
--------------------------------------------

**Transposition deadline 9 December 2026**, applying to products placed on the
market from that date.

Software is now unambiguously a "product", including a standalone download. A
product can be **defective because of a cybersecurity vulnerability, or because
the manufacturer failed to supply security updates** — and liability under the
PLD **cannot be excluded or limited by contract.** The AGPLv3 warranty disclaimer
does not help here; that is the point of the provision.

The exemption is the same shape as the CRA's: **software supplied outside a
commercial activity.**

**Conclusion: out of scope, on the same basis, and it raises the stakes on the
CRA answer considerably.** Monetising would attach strict liability for security
defects across the EU from December 2026 — a full year *before* CRA conformity
obligations bite. That ordering is worth remembering: the liability arrives
first.

Things that do not reach this project
-------------------------------------

Recorded so the question does not get re-opened each time it comes up.

* **NIS2** — addresses operators of essential and important entities, not
  software vendors. It shapes what customers will *ask for* (logging, incident
  detection, message trace), which is a product argument, not a compliance one.
* **GDPR / UK GDPR** — this project is neither controller nor processor; the
  operator of a given installation is. That is precisely why per-account export,
  erasure reaching both the message store and the logs, and log retention limits
  are worth building: to let operators meet *their* obligations. They are
  features, not compliance for us.
* **DORA** — reaches financial entities and their critical ICT providers by
  contract. Not applicable to unpaid distribution.
* **eIDAS 2 / qualified electronic registered delivery** — a different service
  category entirely. Not relevant, and not planned.

Review triggers
---------------

Re-do this determination, with a new date, when **any** of the following happens:

1. Any paid offering appears — licence, support, hosting, a "pro" edition, or a
   commercial licence exception.
2. Donations or sponsorship become a condition of access or updates.
3. The maintainer incorporates, or maintenance moves to a company or foundation
   (which would make steward status possible, and change the analysis).
4. Distribution starts collecting personal data beyond security and
   interoperability purposes.
5. The Commission issues further Article 26 guidance, or harmonised standards
   under the CRA are published, that change the open-source treatment.

What we do anyway
-----------------

Not because either instrument compels it, but because they are the right shape
for a security-relevant product and they would be prerequisites if scope ever
changed:

* SPDX and CycloneDX SBOMs attached to every release
  ([`sbom.yml`](../../.github/workflows/sbom.yml)), including the native
  dependencies — OpenSSL, Boost and libpq — that a source-tree scan does not see.
  That matters for exactly the purpose an SBOM has: without them, anyone checking a
  release against a CVE feed would have concluded this server does not link OpenSSL.
* **Every release asset signed**, with Sigstore cosign, keyless via OIDC, the
  signature recorded in the public Rekor transparency log and verified in the same
  job so a malformed bundle cannot reach a user
  ([`sign-release.yml`](../../.github/workflows/sign-release.yml)). Keyless
  deliberately: for a single-maintainer project a long-lived signing key held in CI
  is itself the thing most worth stealing.
* A documented security policy and private disclosure route
  ([SECURITY.md](../../.github/SECURITY.md)), plus a machine-readable `security.txt`
  (RFC 9116) served at `/.well-known/security.txt` by the web services listener.
* Static analysis, CodeQL and OpenSSF Scorecard in CI; a full regression suite
  gating every release.
* Release notes that name unfixed known issues rather than omitting them.

Still outstanding, and tracked in [Roadmap.md](../../Roadmap.md): **Authenticode**
signing of the installer. Cosign and Authenticode are complementary rather than
alternatives — SmartScreen and the UAC prompt care about Authenticode and know
nothing about Sigstore, so a downloaded installer is verifiable by anyone who checks
and Windows still shows an unknown-publisher warning. Azure Trusted Signing is the
realistic route.

What in this document is checkable, and what is not
--------------------------------------------------

Worth separating, because the two halves have very different half-lives.

The **determination** — the scope analysis, the commerciality test, the steward
question, the PLD reasoning — is a dated legal self-assessment. Nothing in the
repository can confirm or refute it; it stands or falls on the legislation and on the
distribution facts listed under "Who is asking", and it is re-done when one of the
review triggers fires. Reviewed 12 August 2026.

The **"What we do anyway" list** is different: every item is a claim about this
repository, and each one is verifiable. Re-checked 13 August 2026 against
`.github/workflows/sbom.yml` (SPDX and CycloneDX, both attached on a `release`
event, with `build/merge-native-dependencies-into-sbom.ps1` adding OpenSSL, Boost
and libpq), `.github/workflows/sign-release.yml` (cosign `sign-blob` over every
asset, then `verify-blob` in the same job), `.github/workflows/codeql.yml`,
`.github/workflows/scorecard.yml`, `.github/SECURITY.md`, and
`WebServicesServer`'s `/.well-known/security.txt` handler. This half is the one to
re-read before quoting the page: the signing line was wrong for a while precisely
because a capability shipped and the paragraph saying it had not was never revisited.
