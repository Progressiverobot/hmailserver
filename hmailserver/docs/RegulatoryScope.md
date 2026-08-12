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

* SPDX and CycloneDX SBOMs attached to every release.
* A documented security policy and private disclosure route
  ([SECURITY.md](../../.github/SECURITY.md)).
* Static analysis and CodeQL in CI; a full regression suite gating every release.
* Release notes that name unfixed known issues rather than omitting them.

Still outstanding, and tracked in [Roadmap.md](../../Roadmap.md): signing of
release artefacts, and a machine-readable `security.txt` (RFC 9116) — the latter
now served by the web services listener.
