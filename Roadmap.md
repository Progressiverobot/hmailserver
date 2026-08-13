Roadmap
=======

What this fork has, what is planned, what is deliberately not, and why. It is
kept honest rather than aspirational: work appears here when there is a concrete
reason for it, and anything already shipped moves to the
[Releases page](https://github.com/Progressiverobot/hmailserver/releases).

No dates are promised for the work. This is a maintained fork, not a funded
product, and the order below is the order things will be looked at rather than a
schedule. Dates that *are* given belong to the outside world — runtime
end-of-life, regulatory deadlines, a provider switching something off — and
those are not negotiable.

How to read this
----------------

Every item carries a status:

| | Meaning |
|:-:|---|
| ✅ | **Shipped** — working in the current release |
| 🔄 | **Underway** — being worked on now |
| ⬜ | **Not started** — identified, nothing done |
| ⏸️ | **Deferred** — consciously postponed, reason given |

A ✅ with a caveat in the Detail column is the most useful row type here, and
there are a lot of them: the capability is real and works, but it has a limit
worth knowing before you rely on it. Those caveats are the honest content of
this document.

The capability matrix below was built by auditing the source tree in August 2026
and then having every "shipped" claim adversarially re-checked against the code.
That second pass changed 69 rows and caught 15 outright overclaims, several of
which turned out to be defects rather than wording problems. They are listed
under [Defects found by the audit](#defects-found-by-the-audit) and are counted
as ⬜, not ✅.

### Contents and totals

750 items. The counts are the point of this table — they say where the fork is
strong and where it is thin far more honestly than any prose summary.

| Section | ✅ | 🔄 | ⬜ | ⏸️ |
|---|--:|--:|--:|--:|
| [Dated items — the forcing functions](#dated-items--the-forcing-functions) | 1 | – | 5 | 1 |
| [Defects found by the audit](#defects-found-by-the-audit) | 15 | 1 | – | – |
| **The next generation** | | | | |
| [The phases](#the-phases) | – | 1 | 6 | 1 |
| [Structural prerequisites](#structural-prerequisites) | 6 | 1 | 2 | – |
| **Control Panel findability and accessibility** | | | | |
| [What is concretely wrong](#what-is-concretely-wrong) | 3 | – | 4 | – |
| [What to do about it](#what-to-do-about-it) | 2 | 2 | 2 | – |
| [Accessibility, which is not optional](#accessibility-which-is-not-optional) | 4 | 2 | 2 | – |
| **The capability matrix** | | | | |
| [SMTP and ESMTP](#smtp-and-esmtp) | 23 | – | 4 | – |
| [Transport security and deliverability](#transport-security-and-deliverability) | 28 | – | 16 | 1 |
| [IMAP](#imap) | 55 | – | 19 | 3 |
| [POP3](#pop3) | 17 | – | 9 | – |
| [Sieve, ManageSieve and rules](#sieve-managesieve-and-rules) | 38 | – | 21 | – |
| [Authentication and cryptography](#authentication-and-cryptography) | 55 | – | 19 | – |
| [Anti-spam, anti-virus and content control](#anti-spam-anti-virus-and-content-control) | 52 | – | 13 | – |
| [Storage, accounts and data model](#storage-accounts-and-data-model) | 81 | – | 9 | – |
| [Routing, queue and delivery](#routing-queue-and-delivery) | 19 | – | 4 | 1 |
| [Administration, API and Control Panel](#administration-api-and-control-panel) | 52 | – | 8 | – |
| [Observability and diagnostics](#observability-and-diagnostics) | 26 | – | 12 | 1 |
| [Extensibility and scripting](#extensibility-and-scripting) | 37 | – | 3 | – |
| [Build, testing and supply chain](#build-testing-and-supply-chain) | 4 | – | 1 | – |
| [Cross-cutting and platform](#cross-cutting-and-platform) | 9 | 1 | 2 | – |
| **Forward-looking** | | | | |
| [Planned work](#planned-work) | 1 | 2 | 15 | 2 |
| [Future-proofing: standards and protocols](#future-proofing-standards-and-protocols) | 1 | – | 5 | 2 |
| [Future-proofing: platform and supply chain](#future-proofing-platform-and-supply-chain) | 4 | 1 | 2 | 2 |
| [Future-proofing: deployment and operations](#future-proofing-deployment-and-operations) | 3 | – | 6 | – |
| **Total** | **536** | **11** | **189** | **14** |

Three things stand out and are worth naming rather than leaving to be inferred.
**Storage and the administration surface are the best-covered areas**, and the
core protocol layer is in good shape. **Sieve is the thinnest** — 21 not-started
against 38 shipped, and most of what is missing is one standard extension set.
And **transport security has the widest gap between reputation and reality**:
it is the area this fork is best known for, yet 16 items are outstanding and
four of the audit defects live there. And the forward-looking sections are no longer all ⬜: post-quantum key exchange,
the .NET 10 migration and the supply-chain work shipped this month, which is what
progress on a roadmap is supposed to look like.

Dated items — the forcing functions
-----------------------------------

Nothing else on this page has a deadline. These do, and none of them were on the
previous version of this roadmap. Ordered by when they bite.

| | Date | Item | What happens |
|:-:|---|---|---|
| ✅ | ~~10 Nov 2026~~ | **.NET 8 end of support — done, 12 Aug 2026** | Migrated to **.NET 10 LTS** (EOL 14 Nov 2028) with three months to spare. All nine projects retargeted to `net10.0-windows`, SDK pinned in `global.json` (`rollForward: latestFeature`, so a newer SDK on a build machine cannot silently change the toolchain), the bundled Desktop Runtime and the installer's version probe moved to 10.x, and packages taken to current. Everything builds with **0 warnings** and the Control Panel starts clean. |
| ⬜ | **End Dec 2026** | **Microsoft 365 turns off Basic auth for SMTP AUTH** | The outbound client offers exactly one mechanism — `AUTH LOGIN`. Anyone relaying through `smtp.office365.com` stops working. Needs a token cache plus XOAUTH2 encoding; note Exchange Online implements **XOAUTH2 only**, not RFC 7628 OAUTHBEARER. Collecting *from* M365 over POP/IMAP has already been broken since 2022. |
| ⬜ | **9 Dec 2026** | **EU Product Liability Directive (2024/2853)** | Software is unambiguously a "product"; a product can be defective *because of a vulnerability or a failure to ship security updates*, and liability **cannot be disclaimed by licence** — AGPLv3's warranty disclaimer does not help. FOSS supplied outside a commercial activity is exempt. Documentation, not code, but it should drive a business decision. |
| ⬜ | **12 Jan 2027** | **Windows Server 2016 end of support** | The moment to declare a supported floor. Recommendation: **Server 2019 / Windows 10 21H2**, which costs zero code — Server 2019 is still `_WIN32_WINNT=0x0A00`. Do not raise the macro past that; self-hosted mail skews old and Server 2019 has support into 2029. |
| ⬜ | **10 Feb 2027** | **Let's Encrypt default lifetime drops to 64 days** | With 10-day authorisation reuse. The ACME client must handle renewal and reload without human intervention, and ARI (renewal-info) becomes worth implementing. The CA/Browser Forum ceiling then falls to 100 days (15 Mar 2027) and **47 days with 10-day DCV reuse (15 Mar 2029)**. Manual renewal is over. |
| ⬜ | **14 May 2027** | **OpenSSL 4.0.x end of life** | 4.0.x is not an LTS branch. The LTS options are 3.5 (EOL 8 Apr 2030) and whatever the next LTS is. This needs a deliberate decision by Q1 2027, not a surprise. |
| ⏸️ | **11 Dec 2027** | **EU Cyber Resilience Act, full application** | Currently **out of scope**: AGPLv3, no paid tier, an individual maintainer — "making available in the course of a commercial activity" is the test, and donations, sponsorship and paid consulting do not by themselves trigger it. Deferred rather than ignored because *the moment a paid tier, hosted edition or commercial licence exception exists, the whole product is in scope* and, being a substantial modification of upstream, this fork is the manufacturer of record. Reporting obligations start 11 Sep 2026. |

**Done, 12 August 2026:** the dated scope determination is at
[`hmailserver/docs/RegulatoryScope.md`](hmailserver/docs/RegulatoryScope.md). It
records the position (out of scope as manufacturer, ineligible as steward, on the
commercial-activity test rather than the price), the five triggers that would
change it, and the fact that a paid tier would make *this fork* the manufacturer
of record rather than upstream. It settles two of the rows above and cost nothing
but an afternoon.

How work is prioritised
-----------------------

In this order, and the order matters:

1. **Anything that loses, corrupts or silently drops mail.** A mail server that
   accepts a message has taken responsibility for it.
2. **Anything that stops the server serving** — a hang, a thread pool that can be
   exhausted, a crash reachable from the network.
3. **Security**, weighted by whether an unauthenticated remote party can reach it.
4. **Operability** — being able to tell *why* something went wrong without
   attaching a debugger. Most of the 2026 work has been here, because the hardest
   bug of the year ([#18](https://github.com/Progressiverobot/hmailserver/discussions/18))
   was hard entirely because the server was silent about it.
5. **Features.**

A fix does not ship without a regression test that fails against the build
before it, and the full suite must pass against the exact binary being released.
The process is in [RELEASE.md](RELEASE.md).

Where this fork stands
----------------------

The matrix below is mostly a list of limits, and a list of limits misleads on its
own. Measured against Postfix + Dovecot, iRedMail, Mail-in-a-Box and Stalwart,
this fork is **ahead on transport security, authentication and observability**,
and behind on mailbox features and the administrative surface.

Genuinely ahead:

* **MTA-STS is implemented in-process.** Postfix still does not implement
  RFC 8461 natively; it needs an external resolver feeding
  `smtp_tls_policy_maps`.
* **DANE with in-process DNSSEC validation** of the TLSA lookup, plus TLS-RPT,
  ACME, SRS, BATV and Ed25519 DKIM.
* **IMAP4rev2 (RFC 9051) advertised by default.** In Dovecot this is still
  compile-time experimental, off by default, and only partially implemented.
* **SCRAM-SHA-256-PLUS with channel binding**, Argon2id, OAuth2/OIDC bearer.
* **Prometheus, OpenTelemetry, JSON logs and health probes in the open tier.**
  Stalwart gates metric alerts, live telemetry, the dashboard *and historical
  retention* behind its Enterprise edition.
* **Four SQL backends behind one portable abstraction**, plus the COM API and
  event scripting, which nothing else in this class offers.

**ARC sealing — corrected, then fixed.** Earlier release notes described it as a
differentiator over verify-only implementations. That was overstated: `Arc::Seal`
had a single caller sitting after every early return in `DKIMSigner::Sign`, so a
message was sealed only when the sender's domain was hosted here *and* had DKIM
enabled *and* signing succeeded — meaning relayed third-party mail, the case ARC
exists for, was never sealed at all. Sealing is now reachable independently of
author-domain signing. The correction is recorded rather than quietly dropped,
because the claim went out in a release.

And what the fork is behind on has now been *measured* rather than guessed. The
capability matrix below is the result: 720 items, built by auditing the source and
then having every "shipped" claim adversarially re-checked against the code. That
second pass changed 69 rows and caught 15 overclaims — the failure shape, almost
every time, was not "the feature is missing" but **"the feature exists, is
advertised, and is inert in the configuration people actually run"**. Two features
enabled by default were unreachable by default; one startup warning had never
executed in any release; one auto-ban was registered and never enforced.

That is the honest baseline the next generation is built from, and it is why the
[structural prerequisites](#structural-prerequisites) come before any of it.

Defects found by the audit
--------------------------

The adversarial verification pass over the capability matrix found sixteen
defects, none of them known before August 2026. **All sixteen are now fixed**,
each with a regression test that fails against the build before it. The suite
went from 1049 to 1144 passing.

Three took two attempts. The first attempt at each was written, reviewed, and
**rejected** -- the password-hash upgrade would have broken SCRAM authentication
with the correct password; the Prometheus rework added throwing database calls to
a thread with no exception barrier, where an escaped exception is
`std::terminate` and a dead mail server; and the `List-*` header work treated a
failed message parse as success, and would have written a stub with no From,
Subject or body on a default configuration for every list posting. Each is
recorded below with what went wrong, because the reasoning is worth more than the
outcome.

Three are not fixed, and it is worth saying why, because in each case a fix was
written and then rejected on review rather than never attempted. That is the
process working: all three would have been worse than the defect.

| | Defect | Status |
|:-:|---|---|
| ✅ | **POP3 credential logging** | `PasswordRemover` masked only lines beginning `PASS`, so a SASL initial response (`AUTH PLAIN <base64>`) went to the log verbatim — and SCRAM continuation lines were never covered by the connection's own masking either. Now a real per-protocol scrubber across POP3, IMAP and SMTP, covering initial responses, `APOP`, quoted and literal `LOGIN` forms, and bare base64 continuations. Usernames stay readable deliberately: a protocol log with no identity in it cannot tell you which account is being attacked. |
| ✅ | **ManageSieve brute force was unbounded** | The 3-attempt cap and `RegisterFailedLogin` both worked, but the auto-ban they create is enforced at accept time by the shared listener — which ManageSieve did not consult, so reconnecting reset everything. The ban is now checked in the accept loop, before the greeting. |
| ✅ | **ETRN was unauthenticated and ungated** | No security-range check and no STARTTLS-required guard, so on a STARTTLS-required port a remote party could trigger a queue flush before TLS. Now gated on the same relay-permission pair `RCPT TO` uses. Loopback keeps working; anonymous remote gets `530`. |
| ✅ | **ARC sealing never applied to relayed mail** | `Arc::Seal` had one caller, after every early return in `DKIMSigner::Sign`, so only mail we were already DKIM-signing as the author domain was sealed — never the forwarding case ARC exists for. Sealing is now reachable independently, still gated on `ArcSealingEnabled`, and an unsealable message is still delivered. |
| ✅ | **Script reload was not fail-closed** | A hot reload assigned the new file text *before* the syntax check and never cleared the handler flags, so a script that failed to compile left the previous handlers advertised against broken contents. Contents, language and flags now commit together or not at all, and the last known-good script stays in force. |
| ✅ | **POP3 `LIST n` and `UIDL n` returned deleted messages** | The scan-listing forms skipped flagged messages; the single-message forms did not. RFC 1939 requires both to treat them as absent. |
| ✅ | **POP3 `AUTH PLAIN =` was mishandled** | A bare `=` is an empty initial response; it was treated as *no* initial response, so the server issued a continuation the client was not expecting. |
| ✅ | **SCRAM failures on POP3 fired no `OnClientLogon`** | The password and bearer paths fired it on success and failure; SCRAM failure did not, so script-based lockout and auditing missed SCRAM brute force entirely. |
| ✅ | **Virus-scanner concurrency counter leaked** | On the give-up paths the slot was never taken but the matching decrement still ran, so the counter drifted negative and the cap stopped meaning anything. Now RAII: the decrement cannot happen unless the increment did. The give-up policy is unchanged — mail still flows. |
| ✅ | **REST queue endpoints did not validate the id** | `retry` and `DELETE` reported success for an id that never existed; both now 404. The same change also *removed* a mail-destruction path: the old `DELETE` could delete a mailbox message rather than a queued one. |
| ✅ | **MTA-STS and ACME hosting were silently inert** | Both are served by `WebServicesServer`, whose ports default to 0 while `MtaStsHostingEnabled` defaults to 1 — features enabled by default and unreachable by default. Now stated at startup, naming the setting to change. Reported to the *application* log, not as an error: this is the shipped default configuration, and putting a Medium entry in every stock install's ERROR log would be its own defect. |
| ✅ | **`ENHANCEDSTATUSCODES` was advertised but mostly unused** | The code table was consulted from one function while ~36 reply sites wrote their status line directly. Most now route through it. No numeric status code or reply text changed. |
| ✅ | **The settings-index generator was not wired into the build** | `build/generate-settings-index.ps1` was referenced by nothing, so the committed Ctrl+K index was effectively hand-maintained. CI now regenerates it and fails if the tree moves. |
| ✅ | **Plaintext-stored passwords never upgrade** | **Corrected 12 Aug 2026 — the original framing of this defect was wrong and overstated.** Weak *hashed* schemes do upgrade correctly: `PasswordValidator.cpp` verifies the password, then re-hashes to the preferred KDF and **persists** it via `SaveObject`, upgrading only ever upward. So the README's "transparent upgrade on login" is accurate for MD5 and SHA256. The real gap is narrower: an account whose `accountpwencryption` is `0` takes the plaintext-comparison branch, which returns *before* reaching the upgrade block, so it stays plaintext forever. The only path that ever handled that case is the dead re-hash in `PersistentAccount::ReadObject`. Compounding it, the `MinimumAcceptedHashAlgorithm` check runs **before** any upgrade, so on a policy-enabled install a plaintext account is refused outright rather than upgraded — arguably deliberate (the log says "must be reset") but it means the policy can never heal an install. A first fix was **rejected** for removing the read-time re-hash before wiring a replacement, which would have broken SCRAM for those accounts *with the correct password*: SCRAM requires a PBKDF2 record, and the read-time re-hash was accidentally what satisfied that gate. |
| 🔄 | **DANE validates the TLSA record but not the MX RRset** | The TLSA lookup is DNSSEC-validated; the MX lookup that chose the host is not, so an attacker able to forge the MX response can redirect delivery to a host whose own TLSA record then validates. Not attempted yet — it needs resolver-level changes. |
| ✅ | **TLS-RPT reporting is gated on an unset value** | The reporter returns immediately when `TlsRptFromAddress` is empty, which it is by default, so reports are aggregated and never sent. Should get the same startup warning treatment as MTA-STS above. |


The capability matrix
---------------------

Everything the server does today, and the notable things it does not. Built from
the source, not from documentation.

### SMTP and ESMTP

23 shipped · 0 underway · 4 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | 8BITMIME (RFC 6152) | Advertised unconditionally; BODY=7BIT and BODY=8BITMIME on MAIL FROM are accepted as no-ops because the transmission path is 8-bit clean. No 8BITMIME->7bit downgrade on outbound relay. |
| ✅ | AUTH LOGIN and PLAIN | LOGIN always offered when AUTH is enabled; PLAIN only when AuthAllowPlainText is set. AUTH is suppressed entirely on a STARTTLSRequired port before TLS. |
| ✅ | Bare LF / bare CR message rejection | After DATA the whole spool file is scanned chunk-wise; any \r not followed by \n, any \n not preceded by \r, or a trailing \r causes "554 Rejected - Message containing bare LF's." Disableable via AllowIncorrectLineEndings… |
| ✅ | BDAT desynchronisation hardening | A rejected BDAT still consumes exactly the announced octet count (StartBdatDiscard_) so chunk payload can never be re-parsed as SMTP commands; a BDAT whose size cannot be parsed closes the connection rather than guessing. |
| ✅ | CHUNKING / BDAT (RFC 3030) | Full BDAT implementation: "BDAT <n> [LAST]", byte-transparent spooling in 40 KB pieces, zero-length chunk handling, spool reused across chunks, DATA-after-BDAT rejected 503, unparseable chunk-size closes the session. |
| ✅ | CRLF.CRLF-only end-of-data | End-of-data is detected only as \r\n.\r\n (or a lone .\r\n at buffer start). A bare LF.LF or CR.CR sequence is never treated as terminator, which is the CVE-2023-51764 smuggling primitive. |
| ✅ | DSN (RFC 3461) | Advertised. MAIL FROM RET= (FULL/HDRS, validated) and ENVID= (xtext, <=100 chars) accepted; RCPT TO NOTIFY= (NEVER/SUCCESS/FAILURE/DELAY) and ORCPT= (addr-type;xtext) validated. NOTIFY is persisted per recipient… |
| ✅ | EHLO response builder | Single builder emits, in order: <hostname>, SIZE, 8BITMIME, PIPELINING, CHUNKING, SMTPUTF8, ENHANCEDSTATUSCODES, DSN, STARTTLS (conditional), AUTH (conditional), HELP. ETRN is implemented but deliberately not advertised. |
| ✅ | EHLO/HELO negotiation and capability sniffing | Sends EHLO, falls back to HELO on a negative reply unless STARTTLS-required or SMTP AUTH is in use; the only capabilities parsed from the remote EHLO banner are STARTTLS and SMTPUTF8 (substring match on the whole response… |
| ✅ | ENHANCEDSTATUSCODES (RFC 2034 / 3463) | Advertised; enhanced codes are emitted only when the client greeted with EHLO (esmtp_session_). A per-code table maps 235/250/251/252/421/450/451/452/454/500/501/502/503/504/530/535/550/551/552/553/554, with class-based x.0.0 fallback. **Caveat: the table is consulted from one function only; 36 reply sites write their status line directly and emit no enhanced code.** |
| ⬜ | ETRN (RFC 1985) | Implemented for route domains — releases held messages by flipping messagetype/nexttrytime for the route ID, with 250/458/500 replies — but **unauthenticated and ungated**: no security-range check and no STARTTLS-required guard, so it is reachable pre-TLS. Also not advertised in EHLO. [Listed as a defect](#defects-found-by-the-audit). |
| ✅ | Outbound AUTH mechanism support | The client offers exactly one mechanism: AUTH LOGIN. There is no PLAIN, no SCRAM and no OAuth2/XOAUTH2 outbound, which matters for the Microsoft 365 basic-auth cutover called out in the roadmap. |
| ✅ | Oversized-line and invalid-command limits | A line over MAX_LINE_LENGTH with no newline aborts the transmission ("Too long line was received"); more than MaximumIncorrectCommands 5xx replies disconnects the client when DisconnectInvalidClients is set. |
| ✅ | Per-port AUTH disable | [Settings] DisableAUTHList is a comma-separated port list; AUTH is neither advertised nor accepted on those local ports (typical use: port 25 submission lock-down). |
| ✅ | PIPELINING (RFC 2920) | Advertised; the command reader processes batched command lines and enqueues replies in order so clients need not wait per command. |
| ✅ | Received header content | Emits "Received: from <HELO> (<PTR> [<ip>]) by <host> with ESMTP[S][A] id <session>" plus a (version= cipher= bits=) line for TLS sessions and an RFC-format date. Optional X-AuthUser, X-AuthUserIP, X-Original-Rcpt-To… |
| ✅ | Return-Path and Delivered-To on local delivery | Return-Path is always written at local delivery; Delivered-To is written with the original recipient address when AddDeliveredToHeader is enabled. |
| ✅ | SCRAM-SHA-256 (RFC 7677) and SCRAM-SHA-256-PLUS | SCRAM-SHA-256 offered whenever AUTH is enabled (independent of the plain-text setting); the -PLUS channel-binding variant (tls-server-end-point, RFC 5929) only on a TLS connection. Full 334-continuation state machine on the connection. |
| ✅ | SIZE (RFC 1870) | Advertises "250-SIZE <bytes>" from MaxMessageSize*1024, or bare "250-SIZE" when unlimited; MAIL FROM SIZE= is parsed and oversized transactions get 552 before DATA, with a second hard check after DATA (554). |
| ✅ | SMTPUTF8 (RFC 6531) | Advertised; parameter detected before sender validation so UTF-8 envelope addresses pass IsValidEmailAddress; propagated outbound only when the remote EHLO advertises SMTPUTF8 and the envelope actually contains non-ASCII… |
| ✅ | STARTTLS (RFC 3207) | Advertised only on non-TLS sockets when the port is STARTTLSOptional/Required; rejects any parameter with 501; on Required, every command other than NOOP/EHLO/STARTTLS/QUIT gets 530. |
| ✅ | STARTTLS plaintext-injection defence | On handshake completion the receive buffer is discarded and SMTP state is reset: HELO name cleared, credentials cleared, in-progress message dropped. This is the CVE-2011-0411 class of bug. |
| ✅ | VRFY / TURN deliberately refused | Both commands are recognised and answered 502 rather than left unimplemented, so address enumeration via VRFY is closed. The HELP text still lists SAML/TURN/VRFY. |
| ✅ | XOAUTH2 / OAUTHBEARER (RFC 7628) | Both advertised when OAuth2TokenValidator is enabled, and by default only over TLS (OAuth2RequireTLS=1). Bearer response handling and account lookup are implemented. |
| ⬜ | BINARYMIME (RFC 3030) | Not advertised and not accepted. Only BODY=7BIT / BODY=8BITMIME are recognised; BODY=BINARYMIME falls through to the unsupported-extension path and is rejected 550. |
| ⬜ | CRAM-MD5 / DIGEST-MD5 / GSSAPI-NTLM | No implementation anywhere in the server; a repo-wide grep finds these names only in an unrelated MySQL connector comment. SCRAM is the intended replacement. |
| ⬜ | Outbound SIZE / PIPELINING / CHUNKING use | The client never advertises or uses SIZE on MAIL FROM, does not pipeline, and always uses DATA rather than BDAT even when the remote advertises CHUNKING. |

### Transport security and deliverability

28 shipped · 0 underway · 16 not started · 1 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | ACME / Let's Encrypt certificate automation (RFC 8555) | Full ACME v2 client: account key create/load, JWS RS256, newOrder, http-01 challenge served either by a transient listener or the always-on WebServicesServer, finalize, fullchain.pem/privkey.pem output, scheduled renewal task… |
| ✅ | ARC chain validation (cv=) | Existing sets are parsed, completeness and contiguity from i=1 enforced, a previous cv=fail is honoured as sticky, and the most recent ARC-Seal is cryptographically verified against its DNS key to produce cv=pass/fail/none… |
| ⬜ | ARC sealing (RFC 8617) | **Only reachable via the DKIM signing path** — `Arc::Seal` has one caller, after every early return in `DKIMSigner::Sign`, so relayed third-party mail is never sealed. [See the defects list](#defects-found-by-the-audit). Otherwise: Adds a full ARC set (ARC-Authentication-Results, ARC-Message-Signature over relaxed/relaxed with a t= timestamp, ARC-Seal covering the whole chain) using the domain's DKIM key, with instance numbering and a max-instance cap… |
| ✅ | BATV (prvs) backscatter protection | Signs the wire envelope MAIL FROM of locally-originated outbound mail as prvs=<K><DDD><SSSSSS>=local@domain (HMAC-SHA256, day-number expiry), leaving the stored message untouched and the envelope domain intact for SPF… |
| ✅ | Configurable cipher suite list | Admin-settable `SslCipherList` pushed through `SSL_CTX_set_cipher_list`, applied to both server and client contexts, with OpenSSL errors logged. Caveat: this API governs TLS ≤1.2 only… |
| ✅ | DANE TLSA (RFC 6698 / 7672) | TLSA lookup per MX host and port; DANE-EE(3) usage only, selectors 0/1, matching types 0/1/2, max 8 records. Bogus DNSSEC skips the host entirely; if every host is bogus the message is deferred rather than delivered insecurely. **Caveat: the TLSA lookup is DNSSEC-validated but the MX RRset that selects the host is not**… |
| ✅ | Diffie-Hellman parameters | 2048-bit DH loaded from `Bin\dh2048.pem` with `SSL_OP_SINGLE_DH_USE`; a missing file is a logged Critical error (5603) rather than a startup failure, and the server then runs without finite-field DH. |
| ✅ | DKIM canonicalization - simple and relaxed | Both simple and relaxed are implemented for header and body independently, including the upstream PR #530 fix that hashes the header name with the exact case emitted ("DKIM-Signature") under simple canonicalization. |
| ✅ | DKIM Ed25519 (RFC 8463) - sign and verify | Key type is auto-detected from the PEM; ed25519-sha256 signing goes through EVP_DigestSign one-shot, and verification loads a 32-byte raw public key from the DNS p= tag. Both directions implemented. |
| ✅ | DKIM signing - RSA (rsa-sha256 / rsa-sha1) | Per-domain signing keyed off the RFC 5322 From: domain (not MAIL FROM) so d= aligns for DMARC, with optional alias signing, 50 MB size ceiling, and a duplicate-signature guard. Algorithm… |
| ✅ | DKIM verification - multi-signature, l=, DNS key flags | Verifies every DKIM-Signature in the header, honours the l= body-length tag, checks v/a/q/h/d/b/bh presence, i= vs d= subdomain relationship, the DNS record g= and h= restrictions and t= key flags… |
| ✅ | DMARC organizational domain lookup | Works, but from a hard-coded 60-odd entry subset of multi-label public suffixes rather than the real Public Suffix List, so relaxed alignment is wrong for any registry outside that list (e.g. .co.th, .com.pe… |
| ✅ | DMARC verification (RFC 7489) | Policy discovery at _dmarc.<from-domain> with organizational-domain fallback, aspf/adkim strict-or-relaxed alignment against SPF and every passing DKIM d=, sp= for subdomain policies… |
| ✅ | ECDH curve list (inbound) | Hard-coded to `secp384r1:x25519:secp256r1` on the server context — not configurable, and it is applied only in `InitServer`, not `InitClient`, so outbound connections keep OpenSSL's default group list. |
| ✅ | In-process DNSSEC validation | Own validating resolver: RRSIG verification for algorithms 8 (RSA/SHA-256), 10 (RSA/SHA-512), 13 (ECDSA P-256), 14 (ECDSA P-384) and 15 (Ed25519); chain walked to IANA root KSK-2017/KSK-2024 anchors… |
| ✅ | MTA-STS consumption (RFC 8461) | _mta-sts TXT lookup, HTTPS policy fetch from mta-sts.<domain>/.well-known/mta-sts.txt with certificate validation, full policy parse, wildcard mx matching (one leftmost label)… |
| ✅ | MTA-STS policy hosting for own domains | The built-in WebServicesServer serves /.well-known/mta-sts.txt for mta-sts.<hosted-domain>, deriving mx: lines from live MX records (or MtaStsPolicyMx override), with mode/max_age from ini and max_age clamped to 1 day..1 year. |
| ✅ | Negotiated cipher/version logging and metrics | Every completed handshake logs session id, remote IP, protocol version, cipher name and bit count, and increments a Prometheus TLS-handshake counter. |
| ✅ | Opportunistic STARTTLS with downgrade retry | Outbound STARTTLS is attempted when configured; a failed optional handshake marks recipients ResultOptionalHandshakeFailed and the delivery is retried in cleartext - but only when neither MTA-STS enforce nor DANE set RequireTls… |
| ✅ | Outbound peer certificate verification + SNI | Client connections verify the peer with `verify_peer \| verify_fail_if_no_peer_cert` when globally enabled (DB default on), MTA-STS requires it, or DANE TLSA records are present… |
| ✅ | Per-IP-range "require TLS for authentication" | `IPRANGE_REQUIRE_TLS_FOR_AUTH` (131072) is enforced on IMAP LOGIN, IMAP AUTHENTICATE, POP3 USER and POP3 AUTH, and SMTP AUTH — a cleartext auth attempt from such a range is refused. |
| ✅ | Per-route / per-domain TLS policy | Each Route carries its own ConnectionSecurity (none / STARTTLS optional / STARTTLS required / implicit SSL), as does the global SMTP relayer; the resolver hands it to the client connection… |
| ✅ | Server cipher preference and ChaCha prioritisation | `TlsOptions` bitmask maps to `SSL_OP_CIPHER_SERVER_PREFERENCE` and `SSL_OP_PRIORITIZE_CHACHA` (the latter only takes effect together with server preference and TLS 1.2/1.3). Ships as 0, i.e. both off. |
| ✅ | SPF checking | libspf2-derived in-process evaluator (RMSPF.cpp, ~113 KB) covering IPv4 and IPv6, invoked with IP, envelope-from and HELO. Caveat: only Pass and Fail are distinguished - SoftFail, Neutral, None… |
| ✅ | SRS (Sender Rewriting Scheme) - SRS0 | HMAC-signed SRS0=<hash>=<tt>=<domain>=<local>@forwarder rewriting when forwarding external mail onward, plus reverse-at-RCPT so signed bounces are decoded and relayed to the original sender without opening a relay… |
| ✅ | TLS 1.2 and TLS 1.3 enabled by default | `SslVersions` bitmask ships as 24 (= TlsVersion12 8 \| TlsVersion13 16), so TLS 1.0 and 1.1 are off unless explicitly re-enabled. SSLv2 and SSLv3 are hard-disabled in code and cannot be turned on. |
| ✅ | TLS on the auxiliary HTTP listeners | REST API and web-services listeners build their own `SSL_CTX` with `TLS_server_method` and a TLS 1.2 floor, separate from the Asio stack — so they do not honour `SslVersions`, `SslCipherList`, `TlsOptions` or the curve list. |
| ✅ | TLS version, cipher and curve control | SSLv2/SSLv3 always off; TLS 1.0/1.1/1.2/1.3 individually disableable via SSL_OP_NO_TLSv1*; administrator-supplied cipher list applied with SSL_CTX_set_cipher_list; curve list pinned to secp384r1:x25519:secp256r1. |
| ⬜ | TLS-RPT (RFC 8460) report generation and submission | **Inert by default**: the reporter task returns immediately when `TlsRptFromAddress` is empty, which it is out of the box, so reports are aggregated and never sent. Otherwise: Per-UTC-day, per-domain success/failure aggregation; scheduled task reads _smtp._tls TXT, extracts rua= mailto: targets, builds the RFC 8460 JSON report and mails it as multipart/report with application/tlsrpt+json… |
| ✅ | TLSA record generation for own certificate | AcmeClient::GetCertificateTlsa computes the DANE "3 1 1" payload (SHA-256 over SubjectPublicKeyInfo) from the issued PEM, so the operator can publish a matching TLSA record. Publishing itself is manual. |
| ⏸️ | OCSP stapling | Not implemented — no OCSP callback, no `SSL_CTX_set_tlsext_status_cb`, no must-staple handling. Consciously postponed to the documented-but-unscheduled tier. |
| ⬜ | AEAD-only cipher policy | Achievable today by hand-editing `SslCipherList` (the mechanism exists), but not shipped as a default or as a preset, and explicitly listed as future work pending a compatibility measurement. |
| ⬜ | ARC results used for inbound filtering | ARC is only produced (sealing) and validated as an input to the cv= tag of a new seal. There is no ARC spam test, no ARC result in the score set, and no way for a trusted ARC chain to rescue a message that fails DMARC. |
| ⬜ | Authentication-Results / Received-SPF header (RFC 8601) | No RFC 8601 Authentication-Results header and no Received-SPF header are written on inbound mail. Results only surface as X-hMailServer-Reason-* scoring headers, or inside ARC-Authentication-Results when ARC sealing is on. |
| ⬜ | Client certificates (mutual TLS) for inbound sessions | Never requested. Peer verification is gated on `IsClient()`, so inbound SMTP/IMAP/POP3 always use `verify_none`; there is no per-port or per-IP-range client-certificate requirement. |
| ⬜ | DKIM dual-selector rotation | Domain holds a single selector and a single private key file, so key rotation requires an edit-and-cutover rather than publishing two selectors. Called out as a gap in the roadmap. |
| ⬜ | DKIM oversigning | No oversigning. A fixed 30-entry recommended-header list is used and each header name appears at most once in h= (found headers are erased from the pool after matching)… |
| ⬜ | DKIM t= / x= signature timestamps | The emitted DKIM-Signature contains only v, a, d, s, c, q, h, bh, b. No signing timestamp (t=) and no expiry (x=), and no x= expiry check on the verify side either. |
| ⬜ | DMARC aggregate (rua) and forensic (ruf) reporting | The server consumes DMARC policy but never generates reports for the domains it receives mail from - no rua/ruf parsing, no per-day DMARC aggregation, no report mail. (TLS-RPT reporting exists; DMARC reporting does not.) |
| ⬜ | DNSSEC authenticated denial of existence (NSEC/NSEC3) | A missing DS record is treated as an unsigned (Insecure) delegation without validating the NSEC/NSEC3 proof, so a stripped-DS downgrade is not detected. No NSEC or NSEC3 handling exists in the resolver. |
| ⬜ | Encrypted private keys for TLS certificates | Explicitly unsupported: the password callback logs error 5143 ("The private key file has a password. hMailServer does not support this.") and returns an empty string. |
| ⬜ | Per-account outbound sending limits | RateLimiter is per-IP and per-destination-domain, per minute only. There is no per-account message quota, no daily cap and no recipient-count cap… |
| ⬜ | Post-quantum key exchange (X25519MLKEM768) | Not available on inbound TLS: the hard-coded three-curve list overrides whatever hybrid groups OpenSSL 4.0.1 would offer, and no group list is configurable. Outbound client contexts do not override the group list… |
| ⬜ | Session resumption / ticket management | No configuration or code at all: no session-ID context, no cache sizing, no ticket-key rotation, no `SSL_OP_NO_TICKET`. Behaviour is whatever Boost.Asio/OpenSSL default to… |
| ⬜ | TLS 1.3 ciphersuite configuration | Not exposed. `SSL_CTX_set_ciphersuites` is never called, so the TLS 1.3 suite set is whatever OpenSSL defaults to and the admin's cipher list cannot restrict it. |

### IMAP

55 shipped · 0 underway · 19 not started · 3 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | ACL (RFC 4314) commands | Config-gated. SETACL, DELETEACL, GETACL, LISTRIGHTS and MYRIGHTS are all registered and implemented, and permission checks are enforced across SELECT, STATUS, LIST, APPEND, COPY, MOVE, STORE, EXPUNGE and CREATE. |
| ✅ | ACL is public-folder-only | SETACL refuses any folder whose AccountID is non-zero with "It is not possible to set permission for account folders", so sharing is confined to the #Public tree. Cross-account mailbox sharing is the roadmap's top mailbox feature. |
| ✅ | ACL rights letters | The permission model implements the full RFC 4314 set l r s w i p k x t e a as bit flags, and parses each letter with +/- delta support. RIGHTS=texk is advertised, matching the four rights that RFC 4314 split out of the legacy c/d. |
| ✅ | APPEND | Single message per command, with optional flag list and internaldate. Literal size is digit-validated, must be > 0, and is hard-capped at 2 GB independent of the configured max message size… |
| ✅ | AUTH=PLAIN (RFC 4616) with SASLprep | Advertised only when SASL is enabled and the connection is either TLS or not STARTTLS-required. Username is SASLprep-normalised (RFC 4013) before lookup, and the default domain is applied to bare usernames. |
| ✅ | AUTH=SCRAM-SHA-256 (RFC 7677) | Full challenge/response exchange with per-connection state; only servable for accounts stored with a PBKDF2 hash, and honours the MinimumAcceptedHashAlgorithm policy. Failures feed the same per-IP auto-ban accounting as LOGIN. |
| ✅ | AUTH=SCRAM-SHA-256-PLUS (RFC 5802/5929) channel binding | Advertised only on TLS connections and refused off-TLS; binds the exchange to the TLS channel via the server certificate. |
| ✅ | AUTH=XOAUTH2 and AUTH=OAUTHBEARER (RFC 7628) | Advertised only when the OAuth2 token validator is enabled and, by default, only over TLS. |
| ✅ | CAPABILITY base string | Unconditional prefix is "* CAPABILITY IMAP4 IMAP4rev1 IMAP4rev2 CHILDREN"; the unconditional trailer adds NAMESPACE RIGHTS=texk MOVE ID SPECIAL-USE UNSELECT UIDPLUS ENABLE STATUS=SIZE ESEARCH CONDSTORE QRESYNC LIST-EXTENDED SEARCHRES U… |
| ✅ | CHILDREN (RFC 3348) | Advertised unconditionally, and every LIST/LSUB line carries \HasChildren or \HasNoChildren computed from the folder's subfolder count. |
| ✅ | CLOSE with silent expunge | Expunges \Deleted messages without sending EXPUNGE to the closing client, but does raise a ChangeNotification so other sessions on the same mailbox do not silently drift out of sequence. |
| ✅ | Command dispatch table | 39 command types are recognised: CAPABILITY, LOGIN, LIST, LSUB, SELECT, FETCH, UID, LOGOUT, NOOP, SUBSCRIBE, CREATE, EXPUNGE, DELETE, UNSUBSCRIBE, STATUS, CLOSE, APPEND, STORE, RENAME, COPY, EXAMINE, SEARCH, AUTHENTICATE, CHECK… |
| ✅ | Command-buffer and literal DoS caps | An 11 MB cap on the accumulating command buffer disconnects clients that never complete a command (reachable pre-auth via LOGIN literals, and quadratic without the cap)… |
| ✅ | CONDSTORE (RFC 7162) | Advertised unconditionally. HIGHESTMODSEQ reported on SELECT and in STATUS, MODSEQ available as a FETCH data item and SEARCH key, STORE (UNCHANGEDSINCE n) implemented with a [MODIFIED set] tagged response code… |
| ✅ | Configurable hierarchy delimiter | Stored as a setting; changing it is refused if any existing folder or rule action contains the new character, and existing rule actions are rewritten atomically. |
| ✅ | ENABLE (RFC 5161) | Recognises exactly four capability names: QRESYNC (implies CONDSTORE), CONDSTORE, UTF8=ACCEPT, IMAP4rev2 (implies UTF8=ACCEPT). Emits the untagged * ENABLED only when at least one was recognised… |
| ✅ | ENVELOPE and recursive BODYSTRUCTURE | Generated from the parsed MIME tree, including nested message/rfc822 encapsulation which recurses to build a child ENVELOPE. |
| ✅ | ESEARCH (RFC 4731) | SEARCH RETURN (...) is parsed for MIN, MAX, COUNT, ALL and SAVE; an empty option list defaults to ALL. Response is "* ESEARCH (TAG \"tag\") [UID] ..."… |
| ✅ | EXPUNGE suppression during unsafe commands | Untagged EXPUNGE is withheld while responding to FETCH, STORE, SEARCH or SORT, per RFC 3501 and RFC 2177. |
| ✅ | FETCH data items | Supports BODY, BODY.PEEK, BODYSTRUCTURE, ENVELOPE, RFC822, RFC822.SIZE, RFC822.HEADER, RFC822.TEXT, UID, FLAGS, MODSEQ, INTERNALDATE, and the ALL/FAST/FULL macros. Section specifiers cover HEADER, HEADER.FIELDS, HEADER.FIELDS.NOT… |
| ✅ | IDLE (RFC 2177) | Config-gated. Enters idle with "+ idling" and pushes untagged EXISTS, RECENT, EXPUNGE and FLAGS as changes arrive. Caveat: idle is terminated by ANY subsequent client line, not specifically the literal "DONE" token. |
| ✅ | IMAP master user (SASL authzid) | A configured master user may authenticate as itself and act as another account by supplying an authzid in the PLAIN token; mismatched authcid is rejected. No other-users namespace is exposed… |
| ✅ | IMAP regression suite | 14 NUnit fixtures covering ACL, Append, Basics, CommandSequences, ConcurrentConnections, Examine, Fetch, Folders, HierarchyDelimiter, MessageIndexing, MessageUids, Search, SequenceSets and Sort… |
| ✅ | IMAP4rev2 (RFC 9051) advertised — behavioural deltas implemented | IMAP4rev2 is advertised unconditionally and ENABLE IMAP4rev2 switches four documented behaviours: RECENT is suppressed in SELECT and EXAMINE, the [UNSEEN] response code is suppressed, RECENT is dropped as a STATUS item… |
| ✅ | IMAP4rev2 gaps — the advertisement overstates the implementation | RFC 9051 folds several extensions into the base protocol that this fork does not implement: LIST-STATUS (RETURN (STATUS ...)) is absent, LITERAL- non-synchronising literals are absent, the BINARY FETCH items are absent… |
| ✅ | LIST-EXTENDED (RFC 5258) | Partial. Supports the (SUBSCRIBED) selection option, a parenthesised multi-pattern mailbox list with de-duplication, and RETURN (SUBSCRIBED) annotation. REMOTE and RECURSIVEMATCH are parsed and ignored… |
| ✅ | LISTRIGHTS returns a fixed list | Real but degenerate: LISTRIGHTS always replies "l r s w i k x t e a" regardless of folder or identifier, omits 'p' even though the model supports it… |
| ✅ | LSUB | Separate code path emitting "* LSUB" lines filtered to subscribed folders, sharing the same wildcard matcher and attribute generator as LIST. |
| ✅ | Mailbox-name encoding: modified UTF-7 (RFC 3501 §5.1.3) | Folder names are stored and carried on the wire as modified UTF-7 and decoded only at the COM/Control Panel boundary. With UTF8=ACCEPT inert there is no UTF-8 mailbox-name path… |
| ✅ | Message flags: system flags only, no keywords | Only \Deleted \Seen \Draft \Answered \Flagged are stored or matched. STORE detects them by case-insensitive substring scan of the raw command; custom keywords ($Forwarded, $MDNSent, user keywords) are silently dropped… |
| ✅ | MOVE / UID MOVE (RFC 6851) | Both implemented, gated on Expunge permission in the source folder and Insert in the destination, with a quota check. Caveat: the COPYUID response code is placed in the TAGGED OK after the EXPUNGE lines… |
| ✅ | NAMESPACE (RFC 2342) | Returns personal namespace ("" + delimiter) and one shared namespace (the configured public-folder name). The other-users namespace is hard-coded to NIL… |
| ✅ | Partial fetch <origin.size> | BODY[...]<p.n> is parsed and the range is clamped defensively; the response echoes only the origin octet as RFC 3501 requires. |
| ✅ | Password masking in logs | LOGIN arguments are masked even when the password arrives as literal data, tracked by a dedicated IsReceivingLiteralDataForLoginCommand_ check. |
| ✅ | Per-message and per-folder MODSEQ persistence | Mod-sequences are stored, not synthesised: Message carries message_modseq_, folders carry a current mod-seq, and expunged UIDs are queryable by mod-sequence for VANISHED replay. |
| ✅ | Public folders (#Public) | A single shared folder tree exposed under the configured public folder name, listed alongside personal folders in LIST/LSUB and gated by ACL lookup per folder. |
| ✅ | QRESYNC (RFC 7162) | SELECT mailbox (QRESYNC (uidvalidity modseq ...)) replays "* VANISHED (EARLIER)" plus changed-flag FETCHes; EXPUNGE and UID EXPUNGE emit a single compacted "* VANISHED" set when enabled; UID FETCH… |
| ✅ | QUOTA (RFC 2087) — read-only STORAGE | Config-gated. GETQUOTA and GETQUOTAROOT return a single unnamed quota root with a STORAGE resource in kilobytes derived from the account max size; an account with no limit gets an empty "()" resource list… |
| ✅ | RENAME / DELETE / CREATE / SUBSCRIBE / UNSUBSCRIBE | All present. INBOX is protected from both RENAME and DELETE; RENAME rejects renaming a folder into its own subtree using the configured hierarchy delimiter (not a hard-coded "."). CREATE creates intermediate path components. |
| ✅ | SASL-IR (RFC 4959) | Advertised behind its own config toggle; AUTHENTICATE accepts an initial response as the second parameter. Caveat: the initial-response path is accepted whether or not the advertising toggle is on. |
| ✅ | SEARCH CHARSET handling | Only UTF-8, US-ASCII and ISO-8859-1 are accepted; anything else is rejected with a "NO [BADCHARSET]" response. |
| ✅ | SEARCH criteria set | Supports CHARSET, ALL, ON, HEADER, TEXT, BODY, SUBJECT, FROM, CC, TO, SENTON, SENTBEFORE, SENTSINCE, SINCE, BEFORE, DELETED, UNDELETED, RECENT, SEEN, UNSEEN, ANSWERED, UNANSWERED, DRAFT, UNDRAFT, FLAGGED, UNFLAGGED, NEW, OLD, LARGER… |
| ✅ | SEARCH is a linear full-message scan | BODY and TEXT are resolved by loading each message from disk and substring-scanning it; there is no full-text index. The roadmap calls this the largest single quality gap. |
| ✅ | SEARCHRES (RFC 5182) "$" marker | SEARCH RETURN (SAVE) stores matched UIDs on the connection; "$" is then expanded in FETCH, STORE, COPY, MOVE, UID FETCH/STORE/COPY/MOVE and UID EXPUNGE… |
| ✅ | SELECT / EXAMINE | SELECT emits EXISTS, RECENT, FLAGS, [UIDVALIDITY], [UNSEEN], [UIDNEXT], [PERMANENTFLAGS] and READ-WRITE/READ-ONLY per ACL. EXAMINE is the same path forced read-only. [UNSEEN] correctly carries a sequence number, not a UID. |
| ✅ | Session timeout and excessive-data handling | Idle-session timeout is load-scaled between 5 and 30 minutes via TimeoutCalculator, with a "* BYE" on both timeout and excessive data. |
| ✅ | SORT (RFC 5256) | Config-gated. Sort keys: ARRIVAL, CC, DATE, FROM, SIZE, SUBJECT, TO, with REVERSE. Available as both SORT and UID SORT. DISPLAYFROM/DISPLAYTO (RFC 5957) are absent. |
| ✅ | SPECIAL-USE (RFC 6154) — attribute annotation only | Advertised and emitted: \Sent, \Drafts, \Trash, \Junk, \Archive are attached by matching well-known top-level folder names (Sent/Sent Items/Sent Messages, Drafts, Trash/Deleted Items/Deleted Messages, Junk/Junk E-mail/Junk Email/Spam… |
| ✅ | STARTTLS (RFC 2595) and implicit TLS | STARTTLS is advertised whenever connection security is STARTTLS-optional or -required, and the handshake is driven from the command. Implicit-TLS (IMAPS) sends its banner only after the handshake completes. With STARTTLS required… |
| ✅ | STATUS data items | MESSAGES, UNSEEN, RECENT, UIDNEXT, UIDVALIDITY, SIZE and HIGHESTMODSEQ. RECENT is correctly counted per queried folder rather than reusing the selected folder's count. Items are matched by case-insensitive substring, not tokenised… |
| ✅ | STATUS=SIZE (RFC 8438) | Advertised and implemented: SIZE sums RFC822.SIZE across the mailbox. Computed by iterating every message on each call, so it is O(n) per STATUS with no cached total. |
| ✅ | UID sequence-set parsing | Handles comma lists, colon ranges, "*" on either side of a colon, and reversed ranges (swapped). For the QRESYNC VANISHED path "*" is deliberately left unbounded (0xFFFFFFFF) so expunged UIDs above the surviving maximum are still repor… |
| ✅ | UIDPLUS (RFC 4315) | All three parts present: [APPENDUID validity uid] on APPEND, [COPYUID validity src dst] on COPY and MOVE, and UID EXPUNGE restricted to \Deleted messages inside the supplied set… |
| ✅ | UNSELECT (RFC 3691) | Closes the selected mailbox without the implicit EXPUNGE that CLOSE performs, so \Deleted messages survive. |
| ✅ | UTF8=ACCEPT (RFC 6855) — advertised but inert | Advertised unconditionally and settable via ENABLE UTF8=ACCEPT (and implicitly by ENABLE IMAP4rev2), but the resulting utf8_accept_enabled_ flag has a getter and setter and no reader anywhere in the codebase… |
| ⏸️ | ESORT (RFC 5267) — SORT RETURN (...) | Explicitly not handled: the RETURN result-option parser is gated on !is_sort_, with an in-code comment stating ESORT is intentionally out of scope. |
| ⏸️ | NOTIFY (RFC 5465) | Not implemented. Explicitly placed under the roadmap's "Not planned" heading, with the reasoning that iOS Mail does not do push for generic IMAP at all and IDLE is the portable answer. |
| ⏸️ | SEARCH=FUZZY (RFC 6203) | Not implemented, not advertised, and explicitly declined in the roadmap on the grounds that Dovecot does not have it either. |
| ⬜ | APPENDLIMIT (RFC 7889) | Not advertised, though the limit exists: APPEND enforces the SMTP max message size, the per-domain max size and a hard 2 GB ceiling. Clients cannot discover any of it, so oversized uploads fail only after the data is sent. |
| ⬜ | BINARY (RFC 3516) | Not implemented. No BINARY[], BINARY.PEEK[] or BINARY.SIZE[] FETCH items, no APPEND with a binary literal, and BINARY is not advertised. Listed in the roadmap's IMAP-extension backlog. |
| ⬜ | CATENATE (RFC 4469) | Not implemented and not advertised; APPEND accepts only a literal, with no CATENATE (TEXT/URL ...) part list. Requires URLAUTH-style URL resolution, which is also absent. |
| ⬜ | COMPRESS=DEFLATE (RFC 4978) | Not implemented and not advertised; no COMPRESS command and no deflate stream layer in the IMAP connection. Listed in the roadmap's IMAP-extension backlog. |
| ⬜ | I18NLEVEL (RFC 5255) | Not implemented and not advertised; no LANGUAGE command, no COMPARATOR support, no translated response text. |
| ⬜ | LIST-STATUS (RFC 5819) | Not implemented and not advertised. The LIST RETURN parser handles only SUBSCRIBED, so clients must issue one STATUS per mailbox at startup. Roadmap ranks it second by value and notes it is expected alongside IMAP4rev2. |
| ⬜ | LITERAL+ / LITERAL- (RFC 7888) | Neither is advertised. A trailing '+' in a literal count is tolerated and stripped in both the connection-level and APPEND parsers, but the server still sends a "+ Ready for literal data" continuation… |
| ⬜ | LOGINDISABLED (RFC 3501) not advertised | When connection security is CSSTARTTLSRequired the LOGIN command is refused with "STARTTLS is required", but LOGINDISABLED never appears in the capability string, so a conformant client cannot tell in advance… |
| ⬜ | METADATA (RFC 5464) / ANNOTATE | Not implemented and not advertised; no GETMETADATA or SETMETADATA commands and no annotation store. Roadmap flags it as a prerequisite for some Sieve extensions. |
| ⬜ | MULTIAPPEND (RFC 3502) | Not implemented and not advertised. APPEND handles exactly one message literal per command and finishes the command as soon as that literal is complete. |
| ⬜ | OBJECTID (RFC 8474) — EMAILID / THREADID / MAILBOXID | Not implemented and not advertised; no EMAILID or THREADID FETCH item and no MAILBOXID in the LIST/STATUS paths. Listed in the roadmap's IMAP-extension backlog. |
| ⬜ | PREVIEW (RFC 8970) | Not implemented and not advertised; no PREVIEW FETCH item and no snippet generation. Listed in the roadmap's IMAP-extension backlog. |
| ⬜ | REPLACE (RFC 8508) | Not implemented and not advertised; there is no REPLACE or UID REPLACE command, so clients must emulate draft updates with APPEND + STORE \Deleted + EXPUNGE. |
| ⬜ | RFC 9208 QUOTA (QUOTA=RES-STORAGE / RES-MESSAGE, SETQUOTA) | Not implemented. The bare "QUOTA" capability atom is advertised rather than the RFC 9208 QUOTA=RES-* form, there is no SETQUOTA, no per-mailbox quota roots and no OVERQUOTA response code. Listed in the roadmap's IMAP-extension backlog. |
| ⬜ | SAVEDATE (RFC 8514) | Not implemented. No SAVEDATE FETCH item and no SAVEDATE/SAVEDBEFORE/SAVEDSINCE/SAVEDATESUPPORTED search keys. Listed in the roadmap's IMAP-extension backlog. |
| ⬜ | THREAD (RFC 5256) | Not implemented and not advertised. No THREAD command in the dispatch table and no reference anywhere in the source. SORT ships from the same RFC but THREAD does not… |
| ⬜ | UNAUTHENTICATE (RFC 8437) | Not implemented and not advertised; there is no way to return an authenticated session to the not-authenticated state for connection reuse. |
| ⬜ | URLAUTH (RFC 4467) / BURL | Not implemented and not advertised; no GENURLAUTH, URLFETCH or RESETKEY commands and no IMAP URL parser. |
| ⬜ | WITHIN (RFC 5032) OLDER / YOUNGER search keys | Not implemented; the search keyword table has no OLDER or YOUNGER entry, so relative-age searches must be expressed as absolute BEFORE/SINCE dates. |

### POP3

17 shipped · 0 underway · 9 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | AUTH command with mechanism listing (RFC 5034) | Bare AUTH returns a dot-terminated mechanism list; the list is TLS-conditional (SCRAM-PLUS only on TLS) and OAuth-conditional |
| ✅ | Brute-force containment | Ten failed attempts on one connection force a disconnect regardless of the auto-ban setting, and every failure path (PASS, SCRAM, bearer) also calls AccountLogon::RegisterFailedLogin to feed the per-IP auto-ban |
| ✅ | CAPA (RFC 2449) | Advertises exactly: UIDL, TOP, USER, SASL <mechs>, STLS (only in STARTTLS modes), UTF8. USER/SASL are suppressed when STARTTLS is required and TLS is not yet active |
| ✅ | Core command set | USER, PASS, QUIT, STAT, LIST (both forms), RETR, DELE, NOOP, RSET all implemented; STAT/LIST use __int64 totals so mailboxes over 2 GB report correctly |
| ⬜ | Credential hygiene | Partial, and the gap is a defect. A pending SASL PLAIN/bearer *continuation* line is masked, and `RequireTLSForAuth` per security range refuses cleartext authentication — but `PasswordRemover` is not a general scrubber: its POP3 arm redacts only lines beginning `PASS`, so a SASL initial response (`AUTH PLAIN <base64>`) is logged verbatim. See [Defects found by the audit](#defects-found-by-the-audit). |
| ⬜ | Deletion semantics and RSET | DELE only flags. **The scan-listing forms skip flagged messages but `LIST n` and `UIDL n` do not**, which RFC 1939 requires — [see the defects list](#defects-found-by-the-audit). Otherwise: STAT/RETR/TOP skip flagged messages; deletion is committed at QUIT via DeleteInboxMessages with an autologout-timer callback; RSET reloads the inbox and clears all flags |
| ✅ | Exclusive mailbox lock | Per-account lock set held for the session; a session refused the lock drops its account reference so it cannot release another session's lock on disconnect, and the lock is released on idle timeout |
| ✅ | OnClientLogon script event | Fires for every POP3 logon attempt (success and failure) with username, IP, port, session id and, on TLS, cipher version/name/bits |
| ✅ | Resource limits | 500-byte command line cap, load-scaled idle autologout between POP3DMinTimeout(10s) and POP3DMaxTimeout(600s), excessive-data guard, and a MaxPOP3Connections session cap |
| ✅ | SASL PLAIN (RFC 4616) with SASLprep | Both the initial-response and continuation forms; authcid is SASLprep-normalised (RFC 4013) and domain aliases are applied for parity with USER |
| ✅ | SASL-IR (RFC 4959) | An initial response may be supplied on the AUTH line; a bare "=" is correctly treated as an empty initial response, and "*" cancels an in-progress exchange |
| ✅ | SCRAM-SHA-256 (RFC 5802 / RFC 7677) | Full three-leg exchange with server-final acknowledgement. Only PBKDF2-hashed, non-AD accounts are eligible; ineligible or unknown accounts run a forced-failure exchange so existence is not disclosed… |
| ✅ | SCRAM-SHA-256-PLUS with channel binding | tls-server-end-point channel binding (RFC 5929) offered only on TLS; the non-PLUS mechanism sets SetServerSupportsChannelBinding on TLS so a stripped-PLUS gs2 'y' downgrade is rejected (RFC 5802 §6) |
| ✅ | STLS (RFC 2595) and implicit POP3S | STLS accepted only in CSSTARTTLSOptional/Required and refused once TLS is active; implicit TLS handled by banner-on-handshake-complete. Per-range RequireTLSForAuth blocks USER/AUTH on cleartext |
| ✅ | Three-state session machine | AUTHORIZATION / TRANSACTION / UPDATE enforced per command; NOOP, HELP, QUIT and CAPA are allowed in any state, UPDATE rejects everything. HELP is a non-standard extra returning +OK |
| ✅ | TOP | TOP <msg> <n> streams full headers plus n body lines with dot-stuffing; recreates a missing message file via EnsureFileExistance and answers -ERR if the file cannot be opened rather than sending an empty body |
| ✅ | UIDL | Both the scan-listing and single-message forms; the UID is the persistent numeric message UID. Deleted-flagged messages are omitted from the scan listing but **not** from the single-message form |
| ✅ | UTF8 command (RFC 6856) | Partial: the UTF8 command is accepted in AUTHORIZATION state and sets a session flag, and UTF8 is advertised in CAPA — but the flag changes no behaviour (message bytes were already passed through verbatim)… |
| ✅ | XOAUTH2 / OAUTHBEARER (RFC 7628) | Bearer-token login gated on OAuth2TokenValidator::IsEnabled() and, by default, TLS; the token's username claim is the login identity and a client-asserted user= must match it. Feeds the same failure accounting as password login |
| ⬜ | APOP | Not implemented; the string APOP does not occur anywhere in the server source. Deliberate in effect (it requires a cleartext-equivalent stored secret), but no comment says so |
| ⬜ | AUTH-RESP-CODE (RFC 3206) | No [AUTH] / [SYS/TEMP] / [SYS/PERM] response codes on authentication failures; all failures are plain -ERR strings |
| ⬜ | CRAM-MD5, DIGEST-MD5, EXTERNAL, GSSAPI, NTLM | None offered; any other mechanism gets "-ERR Unsupported authentication mechanism." CRAM-MD5/DIGEST-MD5 are omitted for the same reason as APOP (they need reversible secrets)… |
| ⬜ | EXPIRE and LOGIN-DELAY (RFC 2449) | Neither capability is advertised and neither policy exists: no server-declared message retention period for POP3 clients and no minimum interval between logins |
| ⬜ | IMPLEMENTATION (RFC 2449) and LANG (RFC 6856) | Neither advertised. Server identity is only carried in the freeform greeting banner (configurable welcome message), and there is no response-language negotiation |
| ⬜ | PIPELINING (RFC 2449) | Not advertised. The connection issues one EnqueueRead per response, so batched commands are not a declared capability even if buffering sometimes tolerates them |
| ⬜ | RESP-CODES (RFC 2449) | No extended response codes; failures are bare -ERR text. Notably a locked mailbox returns "-ERR Your mailbox is already locked" with no [IN-USE] code, so clients cannot distinguish it from a bad password |

### Sieve, ManageSieve and rules

38 shipped · 0 underway · 21 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | Account forwarding | Per-account forward address with keep-original and abort-if-spam-flagged switches; the target is validated through CheckDeliveryPossibility, self-forward is rejected, and the rule-loop counter guards against forward loops |
| ✅ | Account vacation / out-of-office message | Per-account on/off, custom subject and body, with a %SUBJECT% macro substituted from the original message and an automatic "Re: <original subject>" when no subject is configured. Body is sent as text/plain; charset=utf-8 |
| ✅ | Actions implemented | Exactly five: keep, discard, fileinto, redirect, stop — plus the implicit keep when a script produced no action. Result is a semicolon-joined action summary string, so an action carrying a colon in its argument is ambiguous |
| ✅ | AUTHENTICATE (SASL PLAIN only) | Partial: PLAIN is the only mechanism, and only with an initial response on the command line — the challenge/continuation form is refused with NO "PLAIN requires an initial response." authcid is SASLprep-normalised and validated against… |
| ✅ | Auto-reply dedupe durability | Caveat on the above: the sent-to set is an in-process multimap with no time window and no persistence. It is cleared only when vacation is switched off… |
| ✅ | Auto-reply loop suppression | Three guards: never reply to yourself, never reply to a spam-flagged message when the per-account switch is set, and reply at most once per (account, recipient) pair… |
| ✅ | Bounded authentication retries | Fixed: three failed AUTHENTICATE attempts on one connection drop it, and every failure also calls AccountLogon::RegisterFailedLogin so the per-IP auto-ban applies. Before this the listener allowed unlimited password guessing |
| ✅ | Capability advertisement | Sent on connect and on CAPABILITY. Literally four lines: "IMPLEMENTATION" "hMailServer ManageSieve", "SIEVE" "fileinto", "SASL" "PLAIN", "VERSION" "1.0". The SIEVE line lists only fileinto… |
| ✅ | COM and Control Panel surface | Account.SieveScript reads/writes the active script directly from storage (no Save() needed); Utilities.CheckSieveSyntax and Utilities.EvaluateSieveScript expose the parser and evaluator for testing… |
| ✅ | Command set | Implemented: CAPABILITY, NOOP, LOGOUT, AUTHENTICATE, LISTSCRIPTS (with ACTIVE marker), PUTSCRIPT, GETSCRIPT, SETACTIVE (including deactivate with an empty name), DELETESCRIPT, CHECKSCRIPT, HAVESPACE… |
| ✅ | Comparators | Partial: :comparator is parsed and only i;octet is honoured (as case-sensitive); anything else including an explicit i;ascii-numeric silently falls back to the case-insensitive i;ascii-casemap default |
| ✅ | Connection concurrency | Real limitation: the accept loop calls HandleClient_ inline on the single worker thread, so exactly one ManageSieve client is served at a time and a slow client blocks all others until its 30-second socket timeout expires |
| ✅ | Control flow | if / elsif / else chains with correct first-match-wins consumption of the whole chain, arbitrary nesting, and stop halting execution mid-block |
| ✅ | Control Panel forwarding and auto-reply tabs | Account editor exposes forwarding (address, keep original, abort on spam) and auto-reply (on, subject, body, expiry checkbox, expiry date picker, abort on spam) as first-class tabs alongside the Sieve tab |
| ✅ | Control Panel rules UI | Dedicated rules view with separate criteria and action editors, so the whole rule model is administrable without COM scripting |
| ✅ | Criteria combination and chaining | Per-rule AND/OR over criteria (GetUseAND). Note the chaining semantics: ApplyRule_ always returns false, so a matching rule does NOT stop the chain — every active rule is evaluated unless a rule explicitly fires the StopRuleProcessing… |
| ✅ | Criteria fields and match types | Predefined fields From, To, CC, Subject, Body (plain+HTML concatenated), MessageSize, RecipientList (semicolon-joined envelope recipients) and DeliveryAttempts; or any arbitrary header by name. Match types Equals, NotEquals, Contains… |
| ✅ | Delivery integration | Runs per recipient during local delivery, after account rules and account forwarding. fileinto overrides the rule-selected IMAP folder; each redirect queues a copy through SMTPForwarding::RedirectToAddress (loop-counter guarded… |
| ✅ | End-to-end regression coverage | One fixture drives the real listener over TCP: greeting, pre-auth refusal, bad/good AUTHENTICATE, CHECKSCRIPT accept and reject, PUTSCRIPT, LISTSCRIPTS ACTIVE marker, GETSCRIPT byte round-trip, active-script delete refusal… |
| ✅ | Evaluation-cost and fidelity caveats | The whole raw message is read into a String for every script evaluation (ReadCompleteTextFile), and the size test compares that in-memory character length rather than the on-disk octet count… |
| ✅ | Fail-open on script error | A script that fails to lex/parse is logged and skipped, and the message is delivered normally — a broken filter can never break delivery. Only one folder can be selected (last fileinto wins); there is no :copy semantics |
| ✅ | HAVESPACE and quotas | HAVESPACE is a stub that always answers OK — there is no per-account script-size or script-count quota to check it against, and no MAXSCRIPTS/QUOTA capability. Comment says so explicitly |
| ✅ | Input hardening | 1 MB cap on an unterminated line, 10 MB cap on literal accumulation, 30-second send/receive timeouts, and quoted-string output escaping for script names |
| ✅ | Lexer | Hash and bracketed comments, quoted strings with backslash escaping, multi-line text: strings with dot-unstuffing, tags, numbers with K/M/G quantifiers, and all bracket/brace/paren/comma/semicolon punctuation |
| ✅ | Literal handling | Both synchronising {NNN} and non-synchronising {NNN+} literals are accepted for PUTSCRIPT/CHECKSCRIPT; the size is taken from the last brace on the line and the trailing CRLF is consumed… |
| ✅ | Loop and abuse guards | Forward, Reply and CreateCopy all check IsGeneratedResponseAllowed: a per-message rule-loop counter against SMTPConfiguration RuleLoopLimit, plus (for Reply only) suppression when the source carries an Auto-Submitted header… |
| ✅ | Match types and address parts | :is (default), :contains and :matches (wildcard, case-insensitive) are supported. Address parts :all, :localpart and :domain are honoured with angle-bracket extraction and comma splitting… |
| ✅ | Named-script store semantics | Script names limited to 128 chars of [A-Za-z0-9.-_+ ] with "."/".." rejected; PUTSCRIPT over the active script refreshes the live copy; the active script cannot be deleted (RFC 5804)… |
| ✅ | Optional listener | Raw-socket + std::thread service outside the Boost.Asio stack, started from Application::StartServers only when [Settings] ManageSieveServerPort is non-zero (default 0 = disabled); bind address defaults to 127.0.0.1… |
| ✅ | Parser and AST | Commands with tagged/numeric/string-list arguments, nested blocks, test lists in parentheses (flattened into the parent test), and the RFC rule that require must precede any other command |
| ✅ | Per-account script storage | File-backed under {DataDir}\Sieve\{domain}\{localpart}\ — active.sieve is the live copy, scripts\{name}.sieve the named set, active.name the pointer… |
| ✅ | Rule actions | Ten types: Delete, Forward, Reply, MoveToIMAPFolder, ScriptFunction, StopRuleProcessing, SetHeaderValue, SendUsingRoute, CreateCopy, BindToAddress. SetHeaderValue supports a %MACRO_ORIGINAL_HEADER% substitution… |
| ✅ | Sieve (RFC 5228) as a second scripting surface | A real interpreter runs each account's active script during delivery, with ManageSieve (RFC 5804) for client management. It is a token subset - actions keep/discard/fileinto/redirect/stop and tests true/false/not/allof/anyof/header/add… |
| ✅ | SRS on forwarded mail | When SRSEnabled is set, forwarding an externally-originated message rewrites MAIL FROM to a signed reversible address at the forwarding domain so SPF stays aligned; local senders are left alone… |
| ✅ | Tests implemented | Exactly nine: true, false, not, allof, anyof, header, address, exists, size. exists requires ALL named headers to be present; size supports :over/:under against the raw message length |
| ✅ | Two-level rule application | Global rules run once in SMTPDeliverer before recipient split; account rules run per recipient in LocalDelivery after the message file is in the account's folder. Results carry move-to-folder, delete… |
| ✅ | Unimplemented constructs fail silently | Real hazard: the parser accepts reject, ereject, vacation, setflag, addflag, removeflag, notify, error, return, include, global and set as valid commands, and envelope, body, hasflag, string, date, currentdate… |
| ✅ | Vacation expiry with auto-disable | Optional expiry date; on the first delivery after it passes the flag is switched off in the database rather than merely being ignored. An unparsable date fails open (vacation stays on) |
| ⬜ | copy (RFC 3894) | Not implemented. There is no :copy tag on fileinto or redirect, so a redirect always cancels the implicit keep unless an explicit keep is also written |
| ⬜ | date and index (RFC 5260) | Not implemented. date/currentdate parse as known tests and evaluate false; there is no :index/:last tagged argument for selecting among repeated header fields |
| ⬜ | duplicate (RFC 7352) | Not implemented; no duplicate test and no tracking store for :handle/:uniqueid seen-values |
| ⬜ | editheader (RFC 5293) | Not implemented. addheader/deleteheader are not even in the known-command list, so a script using them fails CHECKSCRIPT with "unknown command" |
| ⬜ | enotify (RFC 5435) | Not implemented. `notify` parses as a known command and no-ops; there are no notification methods, no valid_notify_method test and no NOTIFY capability advertised over ManageSieve |
| ⬜ | envelope (RFC 5228 §5.4) and body (RFC 5173) | Not implemented and this is the most user-visible gap: the SMTP envelope is never passed to the evaluator (SieveMessage is built from the raw file only)… |
| ⬜ | ihave (RFC 5463) and environment (RFC 5183) | Not implemented. Both parse as known tests and evaluate false — which is actively wrong for ihave, whose whole purpose is capability probing, and means an ihave-guarded fallback script silently takes the wrong branch |
| ⬜ | imap4flags (RFC 5232) | Not implemented. setflag/addflag/removeflag parse and no-op; the hasflag test parses and evaluates false; there is no :flags tagged argument on keep/fileinto |
| ⬜ | include (RFC 6609) | Not implemented. include, return and global parse as known commands and no-op; there is no personal/global script namespace in SieveStorage to include from |
| ⬜ | mailbox / mboxmetadata (RFC 5490) | Not implemented. No :create tag on fileinto and no mailboxexists test — a fileinto naming a folder that does not exist relies on whatever MoveToIMAPFolder does rather than declared Sieve semantics |
| ⬜ | Out-of-office scheduling and scope | Only an end date exists — there is no start date, so a future absence cannot be scheduled and must be switched on manually. There is also no domain-level or server-level auto-reply, no separate internal/external message… |
| ⬜ | regex (draft-ietf-sieve-regex) | Not implemented as a Sieve match type, even though the server already carries a regex engine used by the legacy rules engine (RuleCriteria::MatchesRegEx). Wiring it into MatchValue_ would be small |
| ⬜ | reject / ereject (RFC 5429) | Not implemented. Both parse as known commands and silently no-op, which is the worst case for these two specifically: the author believes mail is being refused while it is in fact being kept |
| ⬜ | relational (RFC 5231) | Not implemented. No :count or :value match types, and no i;ascii-numeric comparator to make them meaningful — SplitArguments recognises only is/contains/matches |
| ⬜ | RENAMESCRIPT and UNAUTHENTICATE | Neither implemented; both fall through to NO "Unknown command." A client renaming a script must GETSCRIPT/PUTSCRIPT/DELETESCRIPT by hand |
| ⬜ | spamtest / spamtestplus / virustest (RFC 5235) | Not implemented, and again the underlying data exists: messages already carry a spam flag and SpamAssassin/AV scores from the antispam pipeline, but no Sieve test can read them |
| ⬜ | Structured response codes | Not implemented. Responses are bare OK / NO with a quoted human string — no (WARNINGS), (QUOTA/maxsize), (QUOTA/maxscripts), (NONEXISTENT), (ALREADYEXISTS), (TAG), (REFERRAL) or BYE codes… |
| ⬜ | subaddress (RFC 5233) | Not implemented. Address parts stop at :all/:localpart/:domain; :user and :detail are not recognised, so plus-addressing cannot be filtered on |
| ⬜ | TLS | None. STARTTLS is recognised but always answered NO "STARTTLS is not supported on this listener.", it is not advertised as a capability, and there is no implicit-TLS variant — so SASL PLAIN credentials cross the wire in the clear… |
| ⬜ | vacation (RFC 5230) | Not implemented. The `vacation` keyword parses as a known command and is then discarded. The irony is that a full native auto-reply engine already exists (subject, body, expiry, spam guard… |
| ⬜ | variables (RFC 5229) | Not implemented. `set` parses and no-ops; there is no ${name} expansion anywhere in the lexer or evaluator, and no match-variable capture from :matches |

### Authentication and cryptography

55 shipped · 0 underway · 19 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | Active Directory account authentication (SSPI) | Accounts flagged as AD bypass all local hashing and are validated by `LogonUser` with LOGON32_LOGON_NETWORK against the account's AD domain/username. They are exempt from the minimum-hash policy and ineligible for SCRAM. |
| ✅ | Administrator password hashed with PBKDF2 | `SetAdministratorPassword` always writes a `$h1$` PBKDF2 hash into `[Security] AdministratorPassword` in hMailServer.INI; validation sniffs the hash type so older MD5/SHA-256 admin hashes keep working. |
| ✅ | alg allow-list and alg:none rejection | `alg` is upper-cased, an empty or `none` value is rejected before the allow-list is even consulted, and the allow-list (`OAuth2AllowedAlgorithms`, default `RS256`) must contain the algorithm… |
| ✅ | Argon2id (OpenSSL EVP_KDF) | Available and selectable (`PreferredHashAlgorithm=5`) but NOT the default. OWASP-minimum parameters: 19456 KiB memory, t=2, p=1 lane, 16-byte salt, 32-byte tag, format `$a2$m$t$p$salt$key`. Verification clamps memory ≤1 GiB, t ≤100… |
| ✅ | AUTH LOGIN | Advertised on every SMTP port where AUTH is enabled, and NOT gated by the plain-text toggle — so a cleartext-equivalent mechanism is always offered when AUTH is on (subject only to STARTTLS-required / per-IP-range RequireTLSForAuth gat… |
| ✅ | AUTH PLAIN (RFC 4616) | Advertised and accepted only when the `authallowplaintext` setting is on; DB default is 0 (off). Supports both the inline initial-response form and the 334-continuation form. |
| ✅ | AUTH PLAIN / SCRAM-SHA-256 / SCRAM-SHA-256-PLUS (RFC 5034) | Advertised via `SASL PLAIN SCRAM-SHA-256` in CAPA and listed by a bare `AUTH`. Unlike IMAP there is no enable/disable setting — POP3 SASL is always on. PLUS only on TLS. |
| ✅ | AUTH SCRAM-SHA-256 (RFC 7677) | Always advertised when SMTP AUTH is enabled, independent of the plain-text setting. Full server-side exchange with RFC 4954 SASL-IR handling (`=` treated as no initial response). |
| ✅ | AUTH SCRAM-SHA-256-PLUS | Advertised only on a TLS connection; rejects with 504 if attempted in cleartext or if the channel-binding value cannot be derived. |
| ✅ | AUTHENTICATE PLAIN | Advertised as `AUTH=PLAIN` and accepted, but the whole IMAP AUTHENTICATE command is gated behind `EnableImapSASLPlain`, whose shipped DB default is 0 — so IMAP SASL is OFF out of the box. |
| ✅ | AUTHENTICATE SCRAM-SHA-256 / -PLUS | Advertised as `AUTH=SCRAM-SHA-256` and (TLS only) `AUTH=SCRAM-SHA-256-PLUS`. Caveat: both share the same `EnableImapSASLPlain` gate as PLAIN, so turning SASL PLAIN off also turns SCRAM off on IMAP. |
| ✅ | Authentication outcome metrics | `hmailserver_auth_success_total` and `hmailserver_auth_failures_total` counters exposed on the Prometheus listener, incremented centrally in `AccountLogon::Logon`. |
| ✅ | Auto-ban on repeated authentication failure | AutoBanLogonEnabled + MaxInvalidLogonAttempts + AutoBanMinutes create a temporary blocking IP range from every failed-login path (POP3 PASS, SCRAM, bearer, IMAP LOGIN, SMTP AUTH)… |
| ✅ | Blowfish reversible account passwords accepted | Scheme 1 decrypts and compares case-insensitively. Reversible storage, retained only for legacy rows; not offered as a preferred choice in the Control Panel except as "Blowfish (legacy)". |
| ✅ | Claim validation | `exp` is required and checked with a clock-skew allowance; `nbf` checked when present; `iss` and `aud` checked only when configured (aud supports array form); the login identity comes from `OAuth2UsernameClaim` (default `email`)… |
| ✅ | Constant-time hash comparison throughout | PBKDF2, Argon2id, legacy SHA-256/MD5 and the SCRAM proof all compare with OpenSSL `CRYPTO_memcmp`, and derived keys are wiped with `OPENSSL_cleanse`. |
| ✅ | Credential masking in protocol logs | IMAP `LOGIN`, POP3 `PASS` and SMTP `AUTH PLAIN` lines are rewritten before logging (SMTP logs the base64 username but never the password); the POP3 SASL continuation line is explicitly never logged. |
| ✅ | DPAPI protection for reversible secrets | On by default (`ProtectStoredSecretsWithDPAPI=1`): the INI database password and DB-stored route/fetch/relayer passwords are written as a self-describing machine-scoped `DPAPI:<base64>` envelope. Machine-bound… |
| ✅ | Empty administrator password means anonymous admin access | Upstream behaviour retained: if `AdministratorPassword` is blank, `AttempAnonymousAuthentication` hands out a ServerAdmin account with no credential at all. The REST API refuses to start in that state, but COM does not. |
| ✅ | Failed-SCRAM and failed-bearer attempts feed auto-ban | The newer mechanisms were wired into the same `RegisterFailedLogin` accounting as the legacy paths, including ManageSieve, which previously allowed unlimited guessing (noted in the source comment). |
| ✅ | Inbound bearer-token validation (offline) | JWT is verified entirely locally against an administrator-configured key: HS256 against `OAuth2HmacSecret`, RS256 against the PEM in `OAuth2PublicKeyFile`… |
| ✅ | IP ranges with per-range protocol and relay policy | Priority-ordered ranges gate SMTP/POP3/IMAP access, the four relay quadrants, the four require-SMTP-auth quadrants, spam and virus protection, and require-TLS-for-auth. Auto-ban writes into the same table with an expiry timestamp. |
| ✅ | Legacy salted SHA-256 accepted | Scheme 3: 6-hex-character salt prefix + SHA-256(salt+password), 70 chars total, detected by length. Accepted for login. The intended upgrade-on-login does not persist (see defects)… |
| ✅ | Legacy unsalted MD5 accepted | Scheme 2: bare 32-char hex MD5, no salt, identified purely by string length. Still a valid login path unless MinimumAcceptedHashAlgorithm is raised. |
| ✅ | ManageSieve AUTHENTICATE PLAIN (RFC 5804) | Only PLAIN is advertised and accepted; STARTTLS is explicitly refused on this listener, so credentials cross in cleartext unless the port is fronted or bound to loopback (the documented default bind is 127.0.0.1). |
| ✅ | Master-user impersonation via SASL PLAIN authzid | An authzid in the PLAIN response is honoured only when `ImapMasterUser` is configured and the authcid equals that master user; otherwise BAD. RFC 4616 two-identity form. |
| ✅ | Minimum-accepted-hash policy | `MinimumAcceptedHashAlgorithm` refuses a login whose stored hash is weaker than the configured floor even with the correct password, and logs why. Default 0 = policy disabled. AD accounts exempt. Also gates SCRAM eligibility. |
| ✅ | OAuth2 provider configuration in the Control Panel | A dedicated card exposes enable, require-TLS, issuer, audience, allowed algorithms, username claim, public-key file picker and the HMAC secret as a masked field. |
| ✅ | OAUTHBEARER (RFC 7628) | Advertised and handled alongside XOAUTH2 on all three protocols; both share one parser and one validator. When the client asserts an identity it must match the token's username claim. |
| ✅ | Password hashing: Argon2id and PBKDF2 with pepper | Argon2id via OpenSSL EVP_KDF and PBKDF2-HMAC-SHA256 ($h1$iter$salt$key, 210k iterations), transparent rehash on login to PreferredHashAlgorithm, a MinimumAcceptedHashAlgorithm floor… |
| ✅ | PBKDF2-HMAC-SHA256 — the default scheme | `PreferredHashAlgorithm` defaults to 4 (= ETPBKDF2). 16-byte random salt, 32-byte key, 210,000 iterations, self-describing `$h1$<iter>$<salt-hex>$<key-hex>`. Verification bounded at 10,000,000 iterations and constant-time compared. |
| ✅ | Per-connection authentication-failure cap | Defence in depth that works even with auto-ban disabled: 10 failures on one connection forces a disconnect on IMAP, SMTP and POP3; ManageSieve uses a tighter cap of 3. All SASL paths (PLAIN, SCRAM, bearer) feed the same counters. |
| ✅ | Per-IP auto-ban on repeated logon failure | Enabled by default; failures counted in the `hm_logon_failures` table, and on reaching `MaxInvalidLogonAttempts` (default 3) an expiring IP range named "Auto-ban: <user>" is created for `AutoBanMinutes` (default 60) and the connection… |
| ✅ | Plain LOGIN command | Always available (no capability gate), blocked only when STARTTLS is required or the IP range sets RequireTLSForAuth. The CAPABILITY response never emits `LOGINDISABLED`, so a client cannot tell in advance that LOGIN will be refused. |
| ✅ | Plaintext account passwords still accepted | Scheme 0 compares case-INSENSITIVELY (an explicit upstream backward-compatibility decision, documented in a comment). Rows are re-hashed to the preferred scheme when the account record is loaded… |
| ✅ | REST API authentication | HTTP Basic only, username must literally be `administrator`, password checked against the INI hash with the normal Crypt dispatch; rejected credentials are logged. No tokens, no scoping, no expiry… |
| ✅ | REST API transport safety rail | Refuses to start unless either bound to 127.0.0.1/localhost or given a certificate and key; the TLS context enforces a TLS 1.2 minimum. |
| ✅ | SASL-IR (RFC 4959) | `SASL-IR` capability advertised only when `EnableImapSASLInitialResponse` is on (DB default 0), but the AUTHENTICATE handler accepts a second parameter (initial response) regardless of that setting. |
| ✅ | SASLprep (RFC 4013) on the authentication identity | Full four-step SASLprep (map → NFKC → prohibit → bidi) applied to the authcid on SMTP PLAIN, IMAP PLAIN, POP3 PLAIN and ManageSieve PLAIN. Caveat: the SCRAM path only applies default-domain canonicalisation… |
| ✅ | SCRAM anti-enumeration (forced-failure exchange) | Unknown or non-PBKDF2 accounts get a full-looking exchange with a deterministic per-installation fake salt derived by HMAC-SHA256 over the admin password + DB credentials, so probing cannot distinguish a real account… |
| ✅ | SCRAM proof verification uses constant-time compare | Client proof is checked with `CRYPTO_memcmp`; nonces are 18 bytes from `RAND_bytes` base64-encoded to 24 chars. |
| ✅ | SCRAM restricted to PBKDF2-stored accounts | Real limitation: SCRAM is served from the stored `$h1$` PBKDF2 key (which is the SaltedPassword). Accounts stored as Argon2id, SHA-256, MD5, Blowfish, plaintext… |
| ✅ | SCRAM stripped-PLUS downgrade rejection | On a TLS connection where PLUS is advertised, a non-PLUS client sending the gs2 `y` flag is rejected per RFC 5802 §6. |
| ✅ | SCRAM-SHA-256 channel binding (RFC 5929 tls-server-end-point) | Channel-binding data is the digest of the server's own end-entity certificate, using the hash from the cert's signature algorithm with MD5/SHA-1 substituted by SHA-256. Only `p=tls-server-end-point` is accepted… |
| ✅ | Script-overridable password validation | `OnClientValidatePassword` fires before any hash comparison and can return 0 (accept) or 1 (reject), letting an operator plug in an external auth source. This bypasses the hash-policy checks when it returns 0. |
| ✅ | Server-wide password pepper | Optional `PasswordPepper` INI secret; the password is HMAC-SHA256'd under it before Argon2id. Deliberately applied to Argon2id ONLY — PBKDF2 must stay un-peppered because it doubles as the SCRAM SaltedPassword… |
| ✅ | Sliding failure window with background purge | `RemoveExpiredRecords` deletes expired security ranges and logon-failure rows older than `MaxLogonAttemptsWithin` minutes, so the counter is a rolling window rather than cumulative. |
| ✅ | Startup crypto self-tests | HashCreator (SHA-256/PBKDF2/Argon2id round-trips, salt-uniqueness, cross-scheme confusion), the Crypt login-dispatch path, the HMAC-SHA256 pepper helper against a known-answer vector… |
| ✅ | Stored-secret protection (DPAPI) and least-privilege service account | ProtectStoredSecretsWithDPAPI=1 wraps the INI database password and the DB-stored route/fetch-account/relayer passwords in machine-scoped DPAPI envelopes instead of reversible Blowfish (with Blowfish still read… |
| ✅ | TOTP for the Control Panel logon | RFC 6238, HMAC-SHA1, 6 digits, 30-second period, ±1 step tolerance, fixed-time code comparison, 160-bit secret from `RandomNumberGenerator`, QR/otpauth enrolment. Applies to the admin GUI logon only. |
| ✅ | TOTP secret storage | Stored as `AdminTotpSecret` under HKLM\SOFTWARE\hMailServer, machine-scope DPAPI via direct crypt32 P/Invoke so the .NET 8 Control Panel and the legacy .NET Framework Administrator share one blob format… |
| ✅ | Transparent legacy Blowfish fallback for stored secrets | Any non-`DPAPI:`-prefixed value is decrypted with the legacy Blowfish key, and a DPAPI failure falls back to Blowfish on write so a secret is never lost. Set the INI key to 0 for portable (Blowfish) storage. |
| ✅ | Transparent upgrade-on-login | On a successful login, if the stored scheme is weaker than the preferred one and the preferred one is PBKDF2 or Argon2id, the account is re-hashed and saved. Only ever upgrades (enum-ordered)… |
| ✅ | USER / PASS | Advertised in CAPA and available whenever the connection is not STARTTLS-required-but-cleartext. |
| ✅ | XOAUTH2 | Non-standard Google/Microsoft bearer mechanism, offered on SMTP, IMAP and POP3 when OAuth2 is enabled and (by default) only over TLS. Parsed from the 0x01-separated SASL blob. |
| ⬜ | ANONYMOUS (RFC 4505) | Not implemented, and deliberately so — empty passwords are rejected outright by the password validator. |
| ⬜ | App passwords and per-account 2FA | TOTP covers only the Control Panel logon. There is no app-password mechanism, so account-level 2FA is structurally impossible for IMAP/POP3/SMTP clients that cannot present a code — and app passwords are the stated prerequisite for per… |
| ⬜ | bcrypt / scrypt | Not implemented; the strong-KDF menu is PBKDF2 and Argon2id only. |
| ⬜ | Client-side OAuth2 for outbound relay | Not implemented. The outbound SMTP client only ever issues `AUTH LOGIN` — no PLAIN, no XOAUTH2, no token cache. Roadmap flags this as the most time-sensitive gap given Microsoft's Basic-auth deprecation for SMTP AUTH at end of 2026. |
| ⬜ | Client-side OAuth2 for the external account fetcher | Not implemented. The POP3 fetcher authenticates with `USER`/`PASS` and can only upgrade the channel with `STLS`; there is no IMAP fetcher and no bearer path, so Microsoft 365 / Gmail mailboxes cannot be collected. |
| ⬜ | CRAM-MD5 (RFC 2195) | Not implemented anywhere — not advertised, not accepted. Would in any case be impossible against the PBKDF2/Argon2id stores since it needs a reversible or MD5-equivalent secret. |
| ⬜ | DIGEST-MD5 (RFC 2831) | Not implemented. Obsoleted by RFC 6331; SCRAM-SHA-256 is the replacement that is shipped. |
| ⬜ | ES256 tokens | Recognised but explicitly refused with a clear diagnostic, because JWS carries ECDSA as raw R\|\|S while OpenSSL expects X9.62 DER and the transcode was never written. The Control Panel still offers "RS256… |
| ⬜ | EXTERNAL (client-certificate SASL) | Not implemented, and could not work today: the inbound TLS contexts never request a client certificate (verify_none on the server side). |
| ⬜ | GSSAPI / Kerberos | Not implemented as a SASL mechanism. Active Directory accounts authenticate by replaying the plaintext password to `LogonUser`, which is password-based, not Kerberos SSO. |
| ⬜ | JWKS fetch with key rotation | Not implemented. The validator never contacts the identity provider; a static PEM or shared secret is the only key source, so provider key rotation requires manual re-configuration. |
| ⬜ | LDAP / directory account backend | Not implemented. There is per-account AD linking but no LDAP account source, no directory sync and no bind-based authentication against a directory. |
| ⬜ | NTLM | Not implemented on any protocol. |
| ⬜ | Password complexity / expiry / history policy | No server-enforced policy. Only an advisory COM helper `Utilities.IsStrongPassword` with a hard-coded 7-entry deny-list and a >4-character rule; nothing calls it during account creation, and there is no expiry… |
| ⬜ | Per-account 2FA and app passwords | Neither exists for mailbox accounts. Roadmap records the structural reason: IMAP/POP clients cannot present a TOTP code, so app passwords are the prerequisite before per-account TOTP is feasible. |
| ⬜ | Per-account lockout and login tarpitting | Neither exists. Banning is keyed purely on source IP, so a distributed attack against one mailbox is never throttled per-account, and there is no progressive delay on failed authentication. |
| ⬜ | SCRAM-SHA-1 / SCRAM-SHA-512 | Neither variant exists; SHA-256 is the only SCRAM family member. Some older clients that only speak SCRAM-SHA-1 will therefore fall back to PLAIN/LOGIN. |
| ⬜ | Second factor on the COM API and REST API | Not enforced. TOTP is checked by the Control Panel client after it authenticates; the COM authentication path and the REST API's HTTP Basic check know nothing about it… |
| ⬜ | Token introspection (RFC 7662) | Not implemented — no revocation awareness at all; a stolen token is valid until `exp`. |

### Anti-spam, anti-virus and content control

52 shipped · 0 underway · 13 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | Action on detection and notifications | Two actions only - delete the message, or strip all attachments and continue delivery (VIRUS_ATTACHMENT_REMOVED server message); optional notification to sender and/or to every recipient. No quarantine-and-release option |
| ✅ | Anti-spam scope: inbound, unauthenticated only | All spam protection is skipped for authenticated sessions, for IP ranges with spam protection disabled, and for white-listed senders - so there is no outbound/authenticated-submission spam filtering at all |
| ✅ | Attachment blocking by file-name wildcard | Independent of virus scanning and of the scan flag: every delivered message is checked when enabled, matching attachment file names against wildcard patterns… |
| ✅ | Attachment blocking by name/extension | A configurable blocked-attachment list is applied after scanning; matching parts are stripped from the message rather than the message rejected. Independent of the virus scanner and available with scanning off. |
| ✅ | Blocked-attachment list management | DB-backed list of wildcard + description rows (hm_blocked_attachments), editable through the COM API and the Control Panel "Blocked attachments" collection page. Matching is file-name only - no MIME type… |
| ✅ | Bounded SpamAssassin session | Idle timeout scaled between SAMinTimeout (30s) and SAMaxTimeout (90s) plus an absolute session ceiling of SAMaxTimeout+30s, so a trickling spamd cannot pin the thread that acknowledges the message (discussion #18) |
| ✅ | Built-in scanner test harness (EICAR + negative control) | TestClamAVConnect / TestCustomVirusScanner / TestClamWinVirusScanner each scan a plain file first (must not alarm) then an EICAR file (must alarm); exposed on the COM AntiVirus interface and wired to the Control Panel buttons |
| ✅ | Built-in weighted test pipeline | Nine tests, each independently enabled and independently scored: DNSBL, HELO-host match, PTR, sender MX records, SPF, SURBL, DKIM, DMARC and SpamAssassin… |
| ✅ | ClamAV connection timeout, load-scaled | Timeout computed by TimeoutCalculator between ClamMinTimeout (15s) and ClamMaxTimeout (90s), both settable in hMailServer.ini and surfaced on the Control Panel ClamAV tab |
| ✅ | ClamAV integration (clamd TCP + clamscan) | Native clamd client (host/port, INI ClamMinTimeout/ClamMaxTimeout bounds) plus a clamscan/ClamWin command-line path; the whole spool file is scanned first, then every MIME attachment is written to a temp file and scanned individually… |
| ✅ | ClamAV via clamd INSTREAM | TCP host+port only. Sends "nINSTREAM\n", then 4-byte big-endian length-prefixed 4096-byte chunks, then a zero-length terminator, and matches the reply against ^stream.*: (.*) FOUND$ |
| ✅ | ClamWin (local clamscan.exe) scanner | Launches clamscan with --database and --tempdir; exit code 1 means infected, and the virus name is reported as "Unknown" because the CLI output is not parsed. Executable path is force-quoted against unquoted-path hijack |
| ✅ | Concurrency cap of 10 parallel scans | Interlocked counter caps simultaneous scanners at MaxRunningScanners=10. Caveat: after waiting 60s (or 10 failed CAS attempts) it proceeds without reserving a slot — and because the matching decrement still runs, the counter drifts negative and the cap stops meaning anything — [see defects](#defects-found-by-the-audit) |
| ✅ | Custom command-line virus scanner | Any external scanner can be wired in by executable path plus a 'virus found' return code, launched through ProcessLauncher and bounded by [Settings] ExternalProcessTimeout. |
| ✅ | Custom external command-line scanner | Runs any executable; %FILE% is substituted anywhere in the command line, otherwise the quoted file path is appended. A configurable exit code means "infected"; virus name is again "Unknown" |
| ✅ | Custom-scanner presets in the Control Panel | One-click command lines and exit codes for Microsoft Defender (MpCmdRun), Sophos savscan, ESET ecls, Bitdefender bdscan and Kaspersky avp.com, plus Test buttons for all three scanner types and ClamWin auto-detect |
| ✅ | DKIM and DMARC as scored tests | DKIM permfail and DMARC reject/quarantine each contribute their configured failure score; a DMARC p=quarantine verdict is scored exactly like p=reject (there is no quarantine store to honour it), and p=none is logged only |
| ✅ | DNSBL / RBL checking | Multiple configurable lists, each with its own DNS host, expected-result expression, score and rejection text. Expected results support pipe-separated alternatives, last-octet ranges (127.0.0.1-5) and wildcards… |
| ✅ | DNSBL check timing | [Settings] DNSBLChecksAfterMailFrom (default 1) moves the pre-transmission checks from connect time to after MAIL FROM; hosts listed as incoming relays are switched to post-transmission scoring instead of pre-transmission rejection. |
| ✅ | DNSBL check timing is configurable | DNSBLChecksAfterMailFrom (default 1) moves blacklist lookups from connect time to after MAIL FROM, so a blacklisted client is not paying DNS cost before it has identified itself |
| ✅ | DNSBL lists with per-list score and reject text | Each configured blacklist has host, expected result, score and reject message, and is checked against the originating IP; list managed via COM/Control Panel collection editor |
| ✅ | External scanner processes are bounded and killed | Scanner launches set a 20s "slow" warning and are then hard-bounded by ExternalProcessTimeout (default 300s), after which the child is TerminateProcess'd with a 5s grace so the delivery thread is never lost… |
| ✅ | Fail-open on scanner failure | A scanner error (cannot connect, cannot launch, unparseable reply) is reported as HM5406 and then treated as NoVirusFound, so the message is delivered unscanned… |
| ✅ | Fixed HTTP endpoint surface (no user-defined routes) | The built-in web-services listener serves only a fixed set of paths - ACME challenges, /.well-known/mta-sts.txt, Thunderbird autoconfig and Outlook autodiscover; there is no way to register a script- or plugin-backed route |
| ✅ | Greylisting | Classic sender/recipient/IP triplet store with configurable initial delay, initial-delete and final-delete windows, per-domain opt-out, IP whitelist, bypass on SPF pass… |
| ✅ | Greylisting with bypasses and per-domain opt-out | Triplet-based greylisting with initial delay and two expiry windows, a dedicated IP white list, bypass on SPF pass, bypass when the connecting IP is the sender domain's A or MX record, per-recipient-domain enable flag… |
| ✅ | HELO host check | Resolves the HELO name and requires the connecting IP to appear among its A/AAAA records; accepts a bracketed literal [addr] or [IPv6:addr] that matches the peer; skips loopback; treats DNS failure as not-spam. Scored… |
| ✅ | Mark / delete thresholds and message tagging | Score >= delete threshold rejects with 550/554 and the failing test's message; score >= mark threshold sets the spam flag and adds X-hMailServer-Spam, X-hMailServer-Reason-N per failed test, X-hMailServer-Reason-Score… |
| ✅ | Maximum message size to scan | Messages larger than AntiVirus.MaximumMessageSize (KB) are skipped entirely and delivered unscanned; 0 means unlimited |
| ✅ | Multiple engines chained per scan | ClamWin, then custom scanner, then ClamAV are each run if enabled, in that fixed order; the first VirusFound short-circuits the rest. Order is not configurable |
| ✅ | Outbound webhooks | No configurable webhook feature; webhook delivery exists only as an admin-written event-script handler using MSXML2.ServerXMLHTTP, shipped as a Control Panel template. Synchronous by default… |
| ✅ | Per-destination outbound rate shaping | [Settings] MaxOutboundPerDestinationPerMinute caps messages sent to one recipient domain per minute; over the cap the delivery is deferred non-fatally and retried. 0 = unlimited (default). |
| ✅ | Per-fetch-account scanning of externally downloaded mail | The POP3 external fetcher sets the scan flag from the fetch account's UseAntiVirus property, so downloaded mail can be scanned independently of any IP range |
| ✅ | Per-IP submission rate limiting | [Settings] MaxSubmissionsPerIPPerMinute caps transactions started per source IP per minute, answering 421 when exceeded. 0 = unlimited (default). |
| ✅ | Per-test timing instrumentation | Each spam test is timed and logged by name with its score contribution; anything taking 10s or more is escalated from debug to the application log, so a sick DNS resolver or stalled spamd is identified by name (discussion #18) |
| ✅ | Pre- vs post-transmission test split | DNSBL, HELO, PTR, MX and SPF run before the body is transferred (rejectable at RCPT/MAIL with 550); SURBL, DKIM, DMARC and SpamAssassin run after the body arrives and reject with 554 |
| ✅ | PTR / reverse-DNS check | Requires a PTR record for the peer whose forward A/AAAA resolution includes the peer IP (full FCrDNS round trip); DNS failure is treated as not-spam. Separately… |
| ✅ | Return-Path injected for SA then removed | A Return-Path built from the envelope sender is written as the topmost header before handing the file to spamd (so SA's SPF and stock rules work) and deleted afterwards; the rewrite is skipped if the message cannot be reloaded… |
| ✅ | Safe degradation when spamd misbehaves | Truncated, short-read, EOF-early or zero-length responses abort the rewrite and keep the original message; the test-incomplete case is reported as HM5508 ("message was accepted without a SpamAssassin verdict"), i.e. fail-open by design |
| ✅ | Scan applies to both directions, gated per IP range | Virus scanning is not inbound-only: the per-message scan flag is set when the connecting IP range has Virus protection enabled, and the scan itself runs at delivery time… |
| ✅ | Scan concurrency cap and size ceiling | Messages over VirusScanMaxSize KB are skipped entirely, and a global running-scanner counter makes new scans wait (10 retries) before giving up, so a slow scanner cannot fan out across the thread pool. |
| ✅ | Scan scope: whole message plus each attachment | The complete spool file is scanned first, then every MIME attachment is extracted to a GUID-named temp file and scanned individually; first detection wins and temp files are deleted either way… |
| ✅ | Score merging or fixed score | If X-Spam-Status starts with YES the message scores either SpamAssassin's own score merged into the hMailServer total, or a flat configured score. Caveat: the merge parser takes only the integer part before the first '.' of score= |
| ✅ | Scored test pipeline with early exit and per-test timing | Nine tests run in a fixed order (DNSBL, HELO, PTR, MX, SPF, SURBL, DKIM, DMARC, SpamAssassin), split pre/post transmission, aborting as soon as the mark/delete threshold is reached… |
| ✅ | Sender-domain MX check | Scores mail whose envelope-from domain publishes no MX records; a failed DNS query is treated as having records. |
| ✅ | Sender/IP white list with wildcard matching | Cached list of (IP range, sender-address wildcard) entries; a match bypasses all spam protection including greylisting. Cache is refreshed lazily under a shared mutex |
| ✅ | Size ceiling on post-transmission scanning | Post-transmission tests are skipped above AntiSpam.MaximumMessageSize, hard-capped at the MIME parser's 80MB limit because scanning above it could not load the message and the post-scan rewrite would destroy it |
| ✅ | SpamAssassin integration | Native spamd client speaking PROCESS SPAMC/1.2, with configurable host/port, score, merge-score mode and its own min/max timeouts. Runs as the last (post-transmission) test. |
| ✅ | spamd integration over SPAMC/1.2 PROCESS | Connects to the configured host/port and sends "PROCESS SPAMC/1.2" with a Content-length header, streams the whole message in 20KB chunks, then replaces the spool file with spamd's rewritten message (move or copy, per SAMoveVsCopy) |
| ✅ | SURBL (URI blacklists) | Extracts URLs from plain and HTML bodies with a regex, strips soft-line-break artefacts, de-duplicates, trims to the registrable domain via the TLD table, skips known boilerplate hosts (w3.org, schemas.microsoft.com… |
| ✅ | SURBL URL-reputation checks | Extracts URLs from the plain and HTML bodies by regex, caps at 15 URLs per message, skips well-known boilerplate hosts (w3.org, schemas.microsoft.com, fonts.googleapis/gstatic)… |
| ✅ | Version-tolerant SPAMD response parsing | Accepts any SPAMD/<version> banner as long as the status is EX_OK (upstream required 1.1 exactly), then requires a well-formed non-negative Content-length; malformed… |
| ⬜ | Admin-reviewable quarantine | Messages are marked (X-hMailServer-Spam, subject prefix, reason headers) or deleted at threshold; nothing is held in a reviewable store for release. Explicitly identified as a gap in the roadmap. |
| ⬜ | Admin-reviewable quarantine (hold and release) | Nothing is ever held for review: spam is scored, tagged or deleted, and virus hits are deleted or stripped. No quarantine store, no release workflow, no per-user or per-admin review queue. Explicitly identified as a gap |
| ⬜ | Bayesian / statistical / ML classifier | No built-in learning filter, no per-user training, no autolearn feedback path; statistical filtering is only available by delegating to SpamAssassin |
| ⬜ | Binary plugin / module loading API | No plugin loader, no DLL extension point, no registered filter chain. The only extension surfaces are the COM API and the Active Scripting event file |
| ⬜ | Configurable fail-closed / defer on scanner outage | No setting exists to hold, defer (4xx) or reject a message when the scanner is unreachable - fail-open is hard-coded. No AVFailAction / fail-closed key anywhere in the source or INI |
| ⬜ | Milter protocol support | Not implemented and explicitly ruled out in favour of an HTTP hook: "an HTTP filter hook modelled on Stalwart's MTA Hooks is much more idiomatic here than milter" |
| ⬜ | Native HTTP filter hook (rspamd / MTA-hook style) | No way to put an external engine in the SMTP path natively; the roadmap names this as the intended mechanism and notes the HTTP client and listeners it would build on already exist |
| ⬜ | Other clamd transports and commands | No Unix/named-socket support, no TLS to clamd, and no use of PING, VERSION, STATS, MULTISCAN or zINSTREAM - INSTREAM is the only command the codebase ever sends |
| ⬜ | Per-account spam settings | Spam thresholds, scores and test enablement are global. The only sub-global controls are the per-domain greylisting toggle, the per-IP-range spam-protection switch… |
| ⬜ | Per-user spamd preferences / other spamc features | No User: header is sent, so all mail is scanned under spamd's global preferences; no SYMBOLS/REPORT/CHECK commands, no TELL learning, no Unix-socket or TLS transport, no per-recipient SA profile |
| ⬜ | Sender/domain blacklist object | There is a white list business object but no matching sender or domain blacklist; blocking a sender means an IP-range deny, a rule, or a DNSBL. No named blocklist collection in the BO layer or COM API |
| ⬜ | Tarpitting | Not implemented. The COM properties TarpitDelay and TarpitCount survive only as stubs marked "OBSOLETE: To be removed in v6" that return 0 and ignore writes; there is no delay logic anywhere in the SMTP or Common code. |
| ⬜ | Virus-scanner failure policy is fail-open and undocumented | A scan that errors, times out or cannot reach the scanner returns 'no virus' and the message is delivered unscanned. There is no configurable fail-closed/defer option and no admin-visible statement of the posture… |

### Storage, accounts and data model

81 shipped · 0 underway · 9 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | Account address validation is filesystem-constrained | Beyond normal address validation the local part additionally forbids \ / ? * \| and spaces/quotes, and the whole address is capped at 254 characters, because the address becomes a directory name in the message store… |
| ✅ | Account and domain signatures with four combination modes | Plain-text and HTML signatures on both account and domain; the domain method is Set-if-not-specified / Overwrite-account / Append-to-account / none… |
| ✅ | Account delete cascade | Deletes all folders and their messages, force-deletes the Inbox, deletes rules, fetch accounts, group memberships and owned ACL grants, deletes the hm_accounts row… |
| ✅ | Account forwarding | Per-account forward address with keep-original and abort-if-spam-flagged flags, integrated with SRS rewriting and the rule loop counter. |
| ✅ | Account rename rewrites dependent data | NameChanger renames the on-disk account/domain folder and rewrites account addresses, forward addresses, alias names and values, distribution-list addresses and list member addresses; a domain rename cascades through all of them. |
| ✅ | Account size tracked by an in-memory delta cache | AccountSizeCache seeds from SUM(messagesize) on first read then applies +/- deltas on every save and delete; it is per-process and is reset on account save/delete… |
| ✅ | Aliases, catch-all and plus-addressing | Per-domain aliases and domain aliases, a domain catch-all address, and per-domain plus-addressing with a configurable separator character. None of the address-resolution surface appears in the inventory. |
| ✅ | Automatic reconnect and statement retry | Every Execute retries up to 6 times, reconnecting on attempts 2 and 4 and after any DALConnectionProblem result, with a 1-second pause between tries. |
| ✅ | Background indexer thread with quick/full modes | Dedicated worker wakes every minute; a "quick" pass indexes only the newest IndexerQuickLimit (default 1000) messages, a "full" pass runs every IndexerFullMinutes (default 720) up to IndexerFullLimit (default 25000) rows. |
| ✅ | Backup and restore | BackupManager/BackupExecuter with a scheduled BackupTask, selectable components (settings, domains, messages), 7za compression, an optional messages-database-only mode (BackupMessagesDBOnly), and COM/Control Panel surfaces… |
| ✅ | Backup/restore of settings, domains and messages | Backup writes an XML document of business objects plus (optionally) the whole data directory, 7z-compressed; BackupMessagesDBOnly skips the files and keeps only the rows. Requires all message files to be inside the data folder. |
| ✅ | Bound parameters on MSSQL/SQL CE only | ADO and SQL CE report GetSupportsCommandParameters()==true; MySQL and PostgreSQL report false, so their statements are rebuilt by literal interpolation with SQLStatement::Escape rather than server-side binding. |
| ✅ | Bulk delete by account bypasses path reconstruction | DeleteByAccountID selects messagefilename and passes it straight to DeleteFile — which works only for rows still holding a full path, so partial-filename rows leave orphan files behind (logged as error 5024). |
| ✅ | Catch-all is the domain postmaster field | There is one catch-all per domain (hm_domains.domainpostmaster). If no account, alias, list or route matches, delivery is redirected to it; if it is blank the recipient is rejected with "Unknown user". No server-wide catch-all exists. |
| ✅ | Continuous upgrade chain 0 → 6005 | 57 registered upgrade steps from hMailServer 1.0 to 6.2; from step 5001 onward every step ships in four dialects (MSSQL, MSSQLCE, MySQL, PGSQL). Run by DBUpdater.exe, not by the service. |
| ✅ | Data Directory Synchronizer (two-way reconcile tool) | Import mode adds .eml/.hma files found on disk into the database via Utilities.ImportMessageFromFile; Delete mode removes files that have no matching row. Domain-scoped or whole-directory. |
| ✅ | Database-outage behaviour | A recipient lookup that fails because the database did not answer returns 451 rather than 550, so a database locked by a backup defers mail instead of bouncing it… |
| ✅ | DBSetup wizard | Wizard flow (Welcome → select database type → connection info → action → service dependency → perform task → completed) supporting Microsoft SQL Server, MySQL/MariaDB and PostgreSQL… |
| ✅ | DBSetupQuick — unattended installer path | Headless entry point used by the installer: creates the database if absent, otherwise shells DBUpdater.exe /SilentIfOk (forwarding /silent) and propagates the child exit code… |
| ✅ | DBUpdater — chained schema upgrades | Registers a chain of upgrade steps from schema 0 through 6005 and applies them in sequence. Scripts exist for four backends (MSSQL, MySQL, PGSQL and MSSQLCE from 5001 onward); the older pre-5001 steps ship MSSQL and MySQL only. |
| ✅ | Dead legacy tables left behind on upgraded databases | hm_serverlog, hm_filters, hm_deliverylog and hm_deliverylog_recipients are created by the 1.2→1.4 and 3.301→3.4 steps and never dropped on MySQL/PGSQL; no current code references them, and fresh installs never get them. |
| ✅ | Delivery-queue poll query has no supporting index | The queue scan filters messagetype/messagelocked/messagenexttrytime and orders by messagesize, messagecurnooftries, messageid; the only relevant index is idx_hm_messages_type on messagetype alone… |
| ✅ | Distribution lists | List modes (public/membership/announcement), require-SMTP-authentication, a require-from address restriction, and AD bulk-import of members from the Control Panel. No moderation, no self-subscribe… |
| ✅ | Domain aliases | hm_domain_aliases rewrites the domain part of both sender and recipient addresses before any account/alias/list lookup, and is applied again when comparing list owners and members. |
| ✅ | Domain delete cascade | Deletes all accounts, aliases, distribution lists and domain aliases, then the hm_domains row, then the {DataDir}\{domain} directory tree. |
| ✅ | Domain limits enforced only at object save | Max accounts / aliases / distribution lists (individually toggleable), max per-account size and total domain size are all checked in PreSaveLimitationsCheck when the object is created or edited — never during mail flow… |
| ✅ | Embedded SQL CE as the zero-config default | The installer's default choice is "Use the built-in database engine", which registers SQL Server Compact 4.0; the connection string caps the file at 4000 MB, and the create script used is CreateTablesMSSQL.sql. |
| ✅ | Envelope recipient de-duplication | After alias, list and catch-all expansion, AddRecipient_ drops any address already present (case-insensitive), so overlapping lists do not produce duplicate copies. |
| ✅ | Fixed-size connection pool with bounded acquisition | Pool size from [Database] NumberOfConnections; waiters block on a condition variable with a DBConnectionAcquireTimeout deadline, and a timeout marks the database unavailable so callers return 451 instead of 550. |
| ✅ | Folder depth cap | Hard limit of 25 nesting levels enforced when validating folder paths. |
| ✅ | Four SQL backends behind one abstraction, with a continuous upgrade chain | MySQL/MariaDB (vendored MariaDB Connector/C, caching_sha2/ed25519 capable), MS SQL via ADO, PostgreSQL via libpq, and embedded SQL CE for zero-configuration installs, all behind DALConnection/DALRecordset with parameterised queries… |
| ✅ | Four SQL backends behind one DAL | MS SQL Server (ADO/OLE DB), MySQL/MariaDB (vendored MariaDB Connector/C as libmysql.dll), PostgreSQL (libpq) and SQL Server Compact Edition, selected by an enum and built by a factory; every persistence class is backend-agnostic. |
| ✅ | Groups as ACL principals | hm_groups / hm_group_members give named account groups usable as an ACL principal type, with their own cache TTL setting. |
| ✅ | GUID-named .eml files, hashed into 256 sub-folders | Filename is `{GUID}.eml`; account mail lives at {DataDir}\{domain}\{localpart}\{first 2 GUID chars}\{filename}. The 2-character level bounds directory width per account. |
| ✅ | hm_imapfolders with per-folder UID and MODSEQ counters | Unique index on (accountid, parentid, name); foldercurrentuid and foldercurrentmodseq are monotonic counters bumped with UPDATE ... = x + 1 then re-read, mirrored into the in-memory folder container. |
| ✅ | hm_message_metadata header index | Indexes exactly five fields per delivered message — date (UTC), from, subject, to, cc — keyed by account+folder+message with a unique index. Values are truncated to 100 characters on write even though the columns are varchar(255). |
| ✅ | IMAP QUOTA extension | QUOTA is advertised and GETQUOTA/GETQUOTAROOT report a single unnamed root with STORAGE used/limit in KB; when the account has no quota the resource list is returned empty. Gated by the enableimapquota setting. |
| ✅ | Index is consumed by IMAP SORT only | IMAPSort loads the metadata map for From/Subject/To/CC/Date; IMAP SEARCH does not touch it at all and calls PersistentMessage::LoadHeader per message, so header search is unindexed even when indexing is on. |
| ✅ | Indexing is off by default | The MessageIndexing setting ships as 0; with it off nothing populates hm_message_metadata and IMAP SORT falls back to reading each message header from disk. |
| ✅ | Max message size, global and per-domain | The effective limit is min(global maxmessagesize, domain maxmessagesize where non-zero) in KB. Enforced three times: against the MAIL FROM SIZE= estimate (552), against the actual buffer during DATA, and at IMAP APPEND… |
| ✅ | Message archiving | [Settings] ArchiveDir writes a raw copy of every message into {ArchiveDir}\{senderDomain}\{senderUser}\ trees, with an optional hardlink mode. No retention, no per-domain scope, no index, no search, no immutability or legal hold. |
| ✅ | Message delete order of operations | DELETE the hm_messages row → write a QRESYNC tombstone (if it was in a folder) → delete hm_messagerecipients rows (queue messages only) → adjust the account size cache → delete hm_message_metadata → zero the in-memory id → delete the f… |
| ✅ | Message flags are a fixed 8-bit bitmask | messageflags is tinyint unsigned holding Seen/Deleted/Flagged/Answered/Draft/Recent/VirusScan/Spam. SELECT advertises those five system flags and no \* in PERMANENTFLAGS, so client-defined IMAP keywords cannot be stored. |
| ✅ | Message metadata indexer | A background worker populates hm_message_metadata (date, from, subject, to, cc), which is what makes IMAP header SEARCH and SORT indexed rather than linear. Tunable via IndexerFullMinutes/IndexerFullLimit/IndexerQuickLimit… |
| ✅ | Message rows carry DSN NOTIFY per recipient | hm_messagerecipients.recipientdsnnotify (added at DB version 6005) records the RFC 3461 NOTIFY setting, and LocalDelivery suppresses the failure DSN when the sender opted out. |
| ✅ | Message-store consistency scan | Hourly task walks every hm_messages row, resolves the on-disk path and reports rows whose file is missing — as a Prometheus gauge and as a tab-separated recovery report in the log directory… |
| ✅ | Message-store integrity: fsync and consistency check | MessageStoreFsync=1 forces each received message to physical storage (_commit/FlushFileBuffers) before it is acknowledged; MessageStoreConsistencyCheck=1 runs a read-only periodic cross-check of message rows against files on disk… |
| ✅ | Missing-file placeholder generation | If a file referenced by a row is gone at retrieval time, the server synthesises a MESSAGE_FILE_MISSING notice in its place, resizes the row and logs error 5026, so the client gets a readable message instead of an error. |
| ✅ | MSSQL failover partner and provider override | [Database] Provider and DatabaseServerFailoverPartner are honoured; with a failover partner set the provider defaults to SQLNCLI, otherwise MSOLEDBSQL, and FailoverPartner= is appended to the connection string. |
| ✅ | Native vacation / auto-reply | Per-account vacation subject, body, on/off and an expiry date, with a spam-flag guard and per-sender dedupe. Cited in the roadmap as the irony behind the missing Sieve vacation extension… |
| ✅ | Object caches with per-type TTLs | Domain, account, alias, distribution-list and group caches each have their own TTL setting (default 60) plus a master usecache flag; there is also an inbox-id cache and a short-lived message cache used to hand a just-accepted message t… |
| ✅ | OpenTelemetry spans for database queries | Each statement emits a `db.query` client span with `db.system` (mysql/mssql/postgresql/sqlce) and a redacted `db.statement`, parented to the active protocol-command span; no-op unless OtelEndpoint is set. |
| ✅ | Optional fsync before acknowledgement | MessageStoreFsync=1 flushes the message file to disk before the transmission buffer completes, trading throughput for durability across a power loss. Off by default. |
| ✅ | Orphaned-metadata cleanup | DeleteOrphanedItems runs once, when the indexer thread starts; there is no scheduled sweep, so metadata for rows deleted while indexing was disabled lingers until the next service restart. |
| ✅ | Partial filenames in the database | hm_messages.messagefilename normally stores only `{guid}.eml` and the path is reconstructed from account address + location; full absolute paths are still tolerated for pre-5.4 rows… |
| ✅ | Password hash upgrade on successful login | `accountpwencryption` records the algorithm (0 none, 1 Blowfish, 2 MD5, 3 SHA256, 4 PBKDF2, 5 Argon2id, 6 DPAPI). After a password verifies, `PasswordValidator` re-hashes to `PreferredHashAlgorithm` and persists it, upgrading only upward and only to a strong KDF. **Caveat:** this is reached only from the hashed-comparison branch, so an account stored as plaintext (`0`) is never upgraded — [see defects](#defects-found-by-the-audit). Separately, the re-hash in `PersistentAccount::ReadObject` writes to the in-memory object only and is dead code… |
| ✅ | Per-account forwarding | hm_accounts carries forwardenabled / forwardaddress / forwardkeeporiginal / forwardabortspamflagged; optional envelope-from rewriting on forward is controlled by RewriteEnvelopeFromWhenForwarding. |
| ✅ | Per-account mailbox quota | accountmaxsize in MB; Account::SpaceAvailable compares AccountSizeCache + incoming size against it. Zero means unlimited. |
| ✅ | Per-backend connection + recordset pair | ADOConnection/ADORecordset, MySQLConnection/MySQLRecordset, PGConnection/PGRecordset, SQLCEConnection/SQLCERecordset all implement DALConnection/DALRecordset; MySQL client is loaded dynamically through MySQLInterface. |
| ✅ | Per-backend DDL macro expanders | Upgrade scripts embed `@@@macro@@@` directives (drop column, rename column, add index...) that each backend expands into its own dialect, so one logical upgrade step is written four ways only where it must be. |
| ✅ | Per-domain aliases | hm_aliases maps a full address to any target address (local or external) with an active flag; resolution loops with a 25-level recursion cap and an inactive alias produces "Alias is not active." |
| ✅ | Plus addressing (subaddressing) | Enabled per domain with a configurable separator character (validated as non-empty when enabled); everything from the separator to @ is stripped before account lookup. The tag is not preserved anywhere in the data model. |
| ✅ | Pre-upgrade data prerequisites | A prerequisite registry runs data fixes before the step that needs them — currently PreReqNoDuplicateFolders, which renames duplicate IMAP folders before the 5200 unique index is applied. Only one prerequisite is registered. |
| ✅ | Public folders are exempt from quota | APPEND checks account quota only when the destination is not a public folder, and public-folder messages carry accountid 0 so they never contribute to any account's size — public folder growth is entirely unbounded. |
| ✅ | Public-folder message path | Public folder mail lives at {DataDir}\{PublicFolderDiskName}\{2 chars}\{filename}; the public folder disk name comes from the imappublicfoldername setting (default `#Public`). |
| ✅ | QRESYNC expunge tombstones (hm_imapexpunged) | Every expunge writes account+folder+UID+modseq so VANISHED (EARLIER) can be replayed. Added at DB version 6003. |
| ✅ | Queue files are flat in the data-directory root | Messages in the Delivering state get no fan-out folder at all — the file sits directly in {DataDir}. A large backed-up queue therefore produces one very wide directory. |
| ✅ | Quota is enforced at delivery, not at RCPT TO | CheckAccountQuotas_ runs inside LocalDelivery, after the message has been accepted, so an over-quota recipient produces a bounce/DSN ("Inbox is full") rather than an SMTP-time 4xx/5xx — i.e. backscatter to a possibly forged sender… |
| ✅ | Quota model: per-account and per-domain size limits only | Account and domain maximum sizes are enforced, but only during local delivery, so an over-quota recipient generates a DSN (backscatter) rather than a 5xx at RCPT TO. No warning threshold… |
| ✅ | RFC 4314 ACLs with inheritance | hm_acl stores per-folder rights (l r s w i p k t e x a as a bitmask) for a user, a group, or "anyone"; ACLManager walks up to the nearest ancestor that has an ACL, and the folder owner always gets full rights. |
| ✅ | Server configuration lives in hm_settings as a name/value property set | Single table of settingname / settingstring (varchar 4000) / settinginteger, loaded into a PropertySet; secret-valued properties are stored through Crypt::ProtectSecret. Everything not in hm_settings is in hMailServer.INI (paths… |
| ✅ | Signatures only append to existing body parts | The signature is appended to the text/plain part and/or the text/html part if they already exist; no part is created, so a message with neither (or an unusual MIME structure) silently gets no signature… |
| ✅ | Single #Public shared namespace | A folder is a public folder purely by having accountid 0; the namespace name is configurable via imappublicfoldername (default `#Public`) and is reported as the one shared namespace. |
| ✅ | Single-recipient file reuse | When a queue message has exactly one local recipient the file is moved into the account folder and the same hm_messages row is repurposed (accountid/folderid set, state → Delivered) rather than copied. |
| ✅ | Slow-query log with SQL literal redaction | Queries over SlowQueryLogMilliseconds are logged with every single-quoted literal collapsed to '?' (handles '' and backslash escapes) so credentials never reach the log; also feeds ServerStatus query counters. |
| ✅ | SRS and BATV reverse resolution in the recipient parser | An SRS0 address at a local domain is HMAC-verified and rewritten to the original sender; a prvs= address is verified and stripped to the original local recipient. Both run before plus-addressing… |
| ✅ | Transactions | BeginTransaction/Commit/Rollback exist on the connection manager but are only reachable from the COM API; the internal message-save path is not transactional — it inserts hm_messages with messagelocked=1, inserts recipients… |
| ✅ | UID assignment serialised in-process only | FolderManipulationLock is a static std::set guarded by a process-wide mutex, so strictly-ascending UID assignment holds for one server instance but is not enforced by the database — a second node against the same schema could interleav… |
| ✅ | Version pin enforced at startup | REQUIRED_DB_VERSION 6005 is compiled in; the service refuses to start and logs error 5011 if hm_dbversion is lower ("run DBUpdater") or higher ("upgrade hMailServer"). |
| ✅ | Zero-byte and folderless messages are refused at save | AddObject aborts if the message size is 0, and reports error 5213 if a Delivered message has no folder id — the two ways a row could otherwise point at nothing. |
| ⬜ | Cross-account (other-users) mailbox sharing | The other-users namespace is hard-coded to NIL, so no user can open another user's mailbox and there is no delegation or Send-As; sharing exists only through #Public. |
| ⬜ | Deduplication / hardlinking of delivered mail | A message to N local recipients is written N times: CopyFromQueueToInbox does a full FileUtilities::Copy per recipient. Hardlinking exists only in the archiver, never in the live store, and there is no content-hash dedup. |
| ⬜ | Disk-full behaviour | Nothing owns it. A repo-wide search finds no GetDiskFreeSpace/ENOSPC/ERROR_DISK_FULL handling anywhere, so there is no free-space precondition before accepting a message, no low-space alarm… |
| ⬜ | Full-text / body index | BODY and TEXT search load and substring-scan each message body and HTML body in turn — a linear scan of the mailbox, with no attachment text extraction and no posting-list table. |
| ⬜ | Message retention / auto-expiry policy | There is no "delete mail older than N days" anywhere — no such setting, no scheduled task. The scheduled-task list is greylisting cleanup, expired IP ranges, TLS-RPT, log retention, message-store consistency… |
| ⬜ | Quota warning thresholds and per-account send quotas | No warning percentage, no notification to the user or admin as a mailbox fills, and no per-account outbound message/recipient cap — rate limiting is per-IP and per-destination-domain per minute only. |
| ⬜ | Referential integrity in the schema | No FOREIGN KEY or ON DELETE CASCADE anywhere in any CreateTables script; all cascades are application-level loops in the Persistent* classes, so a crash mid-delete can leave orphans. |
| ⬜ | TLS to the database server | The PostgreSQL conninfo is built from host/port/user/password/dbname only — no sslmode — and MySQLConnection sets no mysql_options TLS parameters. Only the MSSQL path can get encryption… |
| ⬜ | Tombstone table is never pruned by age | hm_imapexpunged rows are only removed when the whole folder is deleted; there is no retention window and no scheduled cleanup, so the table grows for the life of a busy mailbox. It also has no primary key. |
| ✅ | Upgrade/migration documentation | Done: the docs index at [`hmailserver/docs/README.md`](hmailserver/docs/README.md), the unattended-install contract in the README, and [`hmailserver/docs/Upgrading.md`](hmailserver/docs/Upgrading.md) covering the 56-step schema chain from version 0 to 6005, what to back up and why, and the one genuine sharp edge — there is no downgrade path, the server refuses to start in *either* direction on a version mismatch, and rollback is therefore only as good as the snapshot taken first. |

### Routing, queue and delivery

19 shipped · 0 underway · 4 not started · 1 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | AWStats journal as the only delivery event stream | A tab-separated file journal writing time, sender, recipient, sender IP, recipient IP, SMTP code and byte count, called from the SMTP rejection paths, LocalDelivery, ExternalDelivery and SMTPDeliverer… |
| ✅ | Bounce / NDR generation | Generates a mailer-daemon message from the SEND_FAILED_NOTIFICATION template with %MACRO_SENT%/%MACRO_SUBJECT%/%MACRO_RECIPIENTS%/%MACRO_ORIGINAL_HEADER% substitution, marks it auto-generated, counts it in ServerStatus… |
| ✅ | Bounce-loop and backscatter suppression | Never bounces a mailer-daemon sender or a null sender, honours the Auto-Submitted header and a rule-loop counter, and increments the loop count on the generated NDR. |
| ✅ | Delivery worker pool and outbound source IP | Configurable delivery thread count, and SMTPDeliveryBindToIP pins the outbound socket to a chosen local address (needed for PTR/SPF-correct multi-IP hosts). Recipients per outbound transaction are capped by MaxSMTPRecipientsInBatch. |
| ✅ | End-of-data finalization deadline | If the accept/save/queue work after the final dot (or last BDAT chunk) exceeds [Settings] FinalizationTimeout, the message is discarded and a temporary 451 is returned rather than letting the relaying MTA time out and duplicate - the f… |
| ✅ | External POP3 account fetcher | Scheduled download from remote POP3 accounts on behalf of local accounts, with per-account UID tracking, a delete-after-days policy, MaxNumberOfExternalFetchThreads, and an OnExternalAccountDownload script hook… |
| ✅ | Four list modes | Public (anyone may post), Membership (members only), Announcement (one nominated owner address only) and DomainMembers (any sender in the list's own domain). |
| ✅ | Global SMTP relayer (smart host) | Fallback relayer with port, optional AUTH credentials and its own connection-security setting; deliveries to identical target/port/credentials are merged into a single connection. |
| ✅ | Incoming relays (trusted forwarders) | Configured relay IPs get spam scoring deferred to post-transmission rather than pre-transmission rejection, so a trusted forwarder's connection is not judged on its own IP. |
| ✅ | MX resolution and host-attempt limits | Manual MX resolution with preference ordering; MaxNumberOfMXHosts truncates the candidate list, and MXTriesFactor limits hosts attempted per retry to (retries+1)*factor. A fixed relay host may carry several pipe-separated hostnames. |
| ✅ | Per-route address allow-list | A route either applies to all addresses in the domain (routealladdresses) or only to the explicit addresses in hm_routeaddresses; a non-listed address at a routed external domain is rejected with "Recipient not in route list." |
| ✅ | Recursive member permission and expansion checks | Before accepting, the server recursively checks the sender may reach every member (lists inside lists included); expansion and permission recursion are both capped at 25 levels. |
| ✅ | Relay controls by IP range | Per-range option bits for allow-SMTP, the four relay permissions (local/remote to local/remote), the four require-SMTP-AUTH permissions, spam-protection and virus-protection opt-out, require-TLS-for-AUTH… |
| ✅ | Retry schedule | Fixed retry count and fixed minutes-between-tries (global, or per-route override taking the maximum across matching routes), plus optional QuickRetries/QuickRetriesMinutes for the first N attempts (greylisting recovery) and QueueRandom… |
| ✅ | Route and fetch-account passwords stored DPAPI-protected | routeauthenticationpassword and fapassword are written through Crypt::ProtectSecret, producing a machine-bound `DPAPI:` envelope by default with a Blowfish fallback; legacy Blowfish values are still read transparently… |
| ✅ | Route target selection order | A rule-forced route id wins outright; otherwise recipients are grouped per domain and each domain is resolved to a route, then to the global SMTP relayer, then to MX… |
| ✅ | Routes (per-domain smart host) | Wildcard-matched per-domain route with target host, port, credentials, connection security, retry count, retry interval, to-all-addresses or an explicit address list, and treat-sender/recipient-as-local flags… |
| ✅ | Routes with wildcard domain matching | hm_routes stores domain pattern, target host/port, retry count and interval, connection security, optional relay authentication, and two "treat as local" flags (recipient and sender)… |
| ✅ | Sender restrictions enforced at RCPT TO | UserCanSendToList_ applies the require-SMTP-authentication flag first, then the mode check, with distinct rejection texts ("SMTP authentication required.", "Not authorized owner.", "Not authorized domain.", "Not authorized sender.")… |
| ⏸️ | hm_message_events message-trace table | Designed in detail in the roadmap — append-only table with trace id plus RFC Message-ID, event type, sender, recipient, remote IP, subject prefix, size, SMTP code, duration… |
| ⬜ | Moderation, self-subscribe, per-list bounce handling | hm_distributionlists stores only address, enabled, requireauth, requireaddress and mode; there is no moderator queue, no subscribe/unsubscribe workflow and no per-list VERP or bounce processing. |
| ⬜ | No post-delivery history table at all | On successful delivery the queue row is simply deleted (or, for a single local recipient, repurposed into the mailbox row); nothing records that the delivery happened… |
| ⬜ | RFC 2369 List-* headers and RFC 8058 one-click unsubscribe | No List-Id, List-Unsubscribe, List-Post, List-Help, List-Owner, List-Archive or List-Unsubscribe-Post is ever emitted. Those header names occur in exactly one place in the tree: the DKIM oversigning "recommended headers" list. |
| ⬜ | RFC 3464 machine-readable DSN format | Bounces are human-readable text only. No message/delivery-status part, no Final-Recipient / Action / Status / Diagnostic-Code fields, no message/rfc822 attachment of the original honouring the RET=FULL\|HDRS the server accepts at MAIL… |

### Administration, API and Control Panel

52 shipped · 0 underway · 8 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | ACME http-01 challenge serving (/.well-known/acme-challenge/) | Serves key authorizations from the in-process AcmeChallengeStore; rejects tokens containing a slash or longer than 256 characters. Always enabled on the web-services listener (not gated behind a setting). |
| ✅ | Admin helper services | Active Directory account picker, DKIM RSA key-pair generator producing PEM + the DNS TXT p= value, password generator and strength meter, protected-secret storage, message-store consistency report parser. |
| ✅ | Advertised authentication method | Autoconfig hard-codes <authentication>password-cleartext</authentication> for every server block; there is no OAuth2 authentication element even though the server has an OAuth2 token validator… |
| ✅ | Authentication: HTTP Basic, single account | Only username "administrator" against the hMailServer.ini administrator password hash. No bearer tokens, no API keys, no scopes, no expiry, no per-key IP restriction — the admin password is replayed on every request. |
| ✅ | Browser admin SPA served from the REST listener | Single self-contained HTML/CSS/JS page served unauthenticated at / and /index.html (it is only the login shell); every data call underneath uses the authenticated /api/v1 routes. Installed to {app}\WebAdmin by the installer. |
| ✅ | COM / IDispatch administration API | 87 Interface* COM classes covering every configuration object (accounts, domains, aliases, lists, rules, routes, IP ranges, ports, certificates, backup, status, utilities). It is the seam every tool and third-party script uses… |
| ✅ | COM automation surface size | 86 dual/IDispatch interfaces with 86 matching coclasses (80 forward declarations at the head of the file). Entry point is IInterfaceApplication with 20 members including Start/Stop, Settings, Domains, Database, Utilities, Status… |
| ✅ | Control Deck views | Four views only: Dashboard (animated KPI tiles + live session counts, 3 s poll), Domains (with account drill-down, create and delete), Delivery queue (retry / delete per message), DANE/TLSA (copy-all record block). |
| ✅ | Control Panel (WPF, .NET 8) | The sole GUI since the classic Administrator was removed in 6.2.10: ~30 views covering domains, accounts, routes, rules, IP ranges, ports, SSL certificates, server and feature settings, queue, logs, status, backup… |
| ✅ | Ctrl+K command palette over pages and individual settings | Fuzzy palette searches both navigation leaves and individual settings; picking a setting opens the page hosting it. The settings index is generated by `build/generate-settings-index.ps1`, and CI now regenerates it and fails if the working tree moves, so it can no longer drift from the settings it indexes. **But see [findability](#control-panel-findability-and-the-ctrlk-problem): the palette exists because the navigation does not make things findable, and that is the actual defect.** |
| ✅ | Data-directory diagnostic | A built-in diagnostic verifies the data directory is reachable and writable, alongside the backup-directory test, in the Control Panel "Run diagnostics" set. |
| ✅ | DataDirectorySynchronizer | Wizard (Welcome → synchronisation mode → select domain → progress) that reconciles the on-disk data directory with the database, per-domain or wholesale. |
| ✅ | DELETE /api/v1/accounts/<address> | Deletes an account by address; 404 when absent. No bulk delete, no soft delete. |
| ✅ | DELETE /api/v1/queue/&lt;id&gt; | Removes a message from the delivery queue. Does not validate that the id exists — an unknown id still reports success — [see defects](#defects-found-by-the-audit). |
| ✅ | Delivery queue management | Grid of id/created/from/recipients/next try/tries/file with view-raw-message, retry-now (ResetDeliveryTime + StartDelivery, 64-bit ids) and remove. |
| ✅ | Endpoint selection from real configuration | Both autoconfig and Autodiscover pick the best advertised port per protocol from the configured TCPIPPorts by ranking implicit TLS above STARTTLS-required (with 587 preferred) above STARTTLS-optional above plain — so clients are told w… |
| ✅ | Failed-auth visibility, but no lockout | A rejected credential writes "REST API: administrator authentication failed." to the application log. There is no rate limiter, no auto-ban and no delay on the REST path (RateLimiter is not referenced by this file)… |
| ✅ | Folder UID recalculation maintenance operation | A single maintenance operation, RecalculateFolderUID, resets each folder's foldercurrentuid to MAX(messageuid) where it has fallen behind — the repair for the "message has no UID" condition the reader reports as error 5025. |
| ✅ | GET /api/v1/domains | Array of {name, active} only — no id, quota, alias or account counts. |
| ✅ | GET /api/v1/domains/<name>/accounts | Array of {address, active}. No paging, no quota/size, no forwarding data. |
| ✅ | GET /api/v1/queue | Delivery-queue listing {id, created, from, recipients, next_try, locked, tries} reusing the same query behind COM Status.UndeliveredMessages. No filtering or paging; whole queue on every call. |
| ✅ | GET /api/v1/status | JSON: version, numeric server state, processedMessages, spamMessages, virusesRemoved, and SMTP/IMAP/POP3 session counts. No uptime, no queue depth. |
| ✅ | GET /api/v1/tlsa | Emits publish-ready DANE TLSA 3 1 1 (DANE-EE / SPKI / SHA-256) records for every configured SSL certificate, falling back to the ACME fullchain.pem. Hard-codes the _25._tcp prefix, so submission/IMAPS records are not generated. |
| ✅ | Graceful fallback when the page is not installed | If WebAdmin\index.html is missing the listener serves a small built-in HTML notice pointing at /api/v1/ rather than 404ing. |
| ✅ | HTTPS REST listener | Self-contained OpenSSL listener; refuses to start unless the administrator password is set, and refuses plaintext unless bound to 127.0.0.1; TLS 1.2 floor; falls back to the ACME cert when none configured… |
| ✅ | IInterfaceCache — cache observability | Per-collection (domain / account / alias / distribution list) hit rate, TTL, max size and current size in KB, plus Clear(). This is the only per-subsystem statistics surface in COM. |
| ✅ | IInterfaceDiagnostics / DiagnosticResults / DiagnosticResult | PerformTests() plus LocalDomainName/TestDomainName inputs; results expose Name, Description, ExecutionDetails and a boolean Result. Every accessor is gated on GetIsServerAdmin(). |
| ✅ | IInterfaceLogging | 23 members: per-category toggles (SMTP/IMAP/POP3/TCPIP/application/debug), live-log enable + LiveLog read, log directory, resolved paths for the current event/error/awstats/default logs, AWStats toggle, KeepFilesOpen… |
| ✅ | IInterfaceMessageIndexing | TotalMessageCount, TotalIndexedCount, Enabled, Clear() and Index() — lets an operator drive and observe the message index from script. |
| ✅ | ImportTool — mbox and text import | Chooser offering two importers: an mbox importer with a streaming parser that handles classic and Thunderbird "From - <date>" envelopes, LF and CRLF, mboxrd >From unquoting and CRLF normalisation without dot-stuffing… |
| ✅ | INI-backed feature settings pages | Seven FeatureSettingsView sections (Security, Automation/ACME, Integration/API & monitoring, Hardening/Advanced INI, Authentication, DNS, Web services) edit hMailServer.ini keys directly through IniFeatureStore, including RestApi*… |
| ✅ | IStatus exposes seven members | UndeliveredMessages (tab-separated blob), StartTime, ProcessedMessages, RemovedViruses, RemovedSpamMessages, SessionCount(eSessionType) and ThreadID. The delivered/deferred/bounced, auth-success/failure, TLS-handshake… |
| ✅ | Live dashboard with charts | Throughput (msgs/min, derived from the processed counter delta) and per-protocol session lines, plus KPI tiles for processed, queue length, spam, viruses, uptime. History is 90 samples at a 2 s poll — about three minutes, in RAM… |
| ✅ | Live log viewer | Tails the current log file from disk on a 750 ms timer with per-category colouring (SMTP/IMAP/POP3/error/application), a 2000-line ring buffer and a pause control. Reads files directly rather than using the COM live-log stream. |
| ✅ | MTA-STS policy hosting (/.well-known/mta-sts.txt) | Serves STSv1 policies only from a mta-sts.<domain> Host for domains hosted and active here. mx: lines come from an explicit MtaStsPolicyMx override or from the domain's live MX records (1 h cache, 512-entry cap)… |
| ✅ | Navigation tree / feature areas | Welcome, Dashboard; Status group (Server status, Delivery queue, Live logs); Domains; Rules; Settings group with Protocols, Delivery, Routes, Public folders, Anti-spam (settings + SURBL + DNSBL + white list + greylist white list)… |
| ✅ | Outlook Autodiscover — POX only | /autodiscover/autodiscover.xml answered for POST and GET, parsing <EMailAddress> out of the POX request and returning the outlook/responseschema/2006a Account block with IMAP/POP3/SMTP Protocol entries (SSL on/off, Encryption TLS/SSL… |
| ✅ | POST /api/v1/domains/<name>/accounts | Creates an account from {address, password}; validates the address belongs to the domain, 409 on duplicate, hashes with PreferredHashAlgorithm, returns 201. No way to set quota, display name or active flag. |
| ✅ | POST /api/v1/queue/<id>/retry | Resets the delivery time and kicks the delivery thread; ids parsed strictly numeric, max 18 digits. |
| ✅ | Public web-services listener | Separate HTTP and HTTPS listeners (own threads) hosting ACME challenges, MTA-STS policy and autoconfiguration. Falls back to the ACME fullchain.pem/privkey.pem when no certificate is configured… |
| ✅ | Remote (DCOM) administration | The Control Panel activates hMailServer.Application by ProgID against a named host, so a remote server can be administered over DCOM; authentication is Application.Authenticate(user, password). |
| ✅ | Request handling limits | Single accept thread handling clients serially; HTTP/1.0 with Connection: close; 64 KB max request; 10 s per-socket and 30 s total read deadline to stop a slow-dribble client pinning the one worker. |
| ✅ | REST admin API covers domains, accounts and the queue only | GET /api/v1/domains, GET\|POST /api/v1/domains/<name>/accounts, DELETE /api/v1/accounts/<address>, GET /api/v1/queue, POST /api/v1/queue/<id>/retry, DELETE /api/v1/queue/<id>, plus /status and /tlsa. No aliases, distribution lists… |
| ✅ | REST administration API | Raw-socket HTTP(S) listener serving /api/v1/status, /domains, /domains/<name>/accounts, /accounts/<address>, /queue (+retry/delete) and /tlsa, with a body-size cap and receive deadline… |
| ✅ | Self-healing COM session | Probes ServerState (not Version) to decide whether the link is alive, checks the Windows service state for local hosts, reconnects with bounded retries and toasts "Reconnected …" then re-enters the current page. |
| ✅ | Session handling and theming | Basic credential kept in sessionStorage, 401 forces re-login, dark/light toggle persisted in localStorage. Server-supplied domain and account names are interpolated into innerHTML and inline onclick handlers… |
| ✅ | Setup and migration tooling | DBSetup (interactive DB creation), DBSetupQuick (headless), DBUpdater (schema migration), DataDirectorySynchronizer (reconcile files against the database) and ImportTool (accounts from text files, messages from mbox)… |
| ✅ | Thunderbird autoconfig (Mozilla clientConfig 1.1) | Served at both /mail/config-v1.1.xml and /.well-known/autoconfig/mail/config-v1.1.xml; derives the mail domain from an autoconfig.<domain> Host header, then the emailaddress query parameter (handles %40)… |
| ✅ | TOTP two-factor on the admin logon | RFC 6238 HMAC-SHA1, 30 s, 6 digits, with QR enrolment; secret stored DPAPI machine-scope in HKLM\SOFTWARE\hMailServer\AdminTotpSecret so it is shared with the classic Administrator. Three attempts then the session is dropped… |
| ✅ | Unattended launch and window/theme persistence | hMailCP.exe /connect <host> <user> <password> auto-connects; theme follows the OS until an explicit toggle, and window bounds/maximised state are persisted under HKCU\Software\hMailServer\ControlPanel with an off-screen guard. |
| ✅ | Unit tests for Control Panel services | xUnit project covering the consistency-report parser (including mid-rewrite and comment-line cases), numeric field validation, password generation and strength. |
| ✅ | WPF .NET 8 Control Panel (hMailCP) | 41 registered pages behind a cached page factory, WPF-UI/Fluent chrome, LiveCharts + SkiaSharp charting, QRCoder for TOTP enrolment. Replaces the classic Administrator (no MFC/Delphi admin remains in the tree). |
| ⬜ | /.well-known/caldav and /.well-known/carddav redirects | Not served. ProcessRequest_ handles only acme-challenge, mta-sts.txt, autoconfig and autodiscover; a paired calendar/contacts server is therefore undiscoverable. |
| ⬜ | Admin UI accessibility | Nobody owns it. The dashboard charts render unlabelled series distinguishable only by colour (no legend; Success/Warning separate by ΔE 5.1 under protanopia against a target of 8)… |
| ⬜ | API coverage beyond status/domains/accounts/queue/TLSA | ProcessRequest_ routes exactly nine API operations. No endpoints for settings, aliases, distribution lists, rules, certificates, DKIM, logs, IP ranges or backup — everything else is COM-only. |
| ⬜ | Apple .mobileconfig configuration profile | No profile generator anywhere in the tree. Named in the roadmap as one of the "smaller items" to sit alongside the existing Thunderbird autoconfig and Outlook Autodiscover. |
| ⬜ | IPv6 for the management/observability listeners | REST, metrics and web-services listeners all create AF_INET sockets and parse the bind address with inet_pton(AF_INET), so none of them can bind an IPv6 address. |
| ⬜ | OpenAPI / Swagger description | No machine-readable API description anywhere in the repo; the endpoint list exists only as prose in README.md:312. |
| ⬜ | Settings, logs, certificates and rules in the browser | The Control Deck cannot edit any server setting, view logs, manage certificates or edit rules — those exist only in the desktop Control Panel and COM. |
| ⬜ | SRV record generation or advice (_imaps/_submission/_autodiscover) | Nothing generates, checks or documents client-discovery SRV records — unlike DANE TLSA, which has a dedicated /api/v1/tlsa generator. Administrators must author SRV by hand. |

### Observability and diagnostics

26 shipped · 0 underway · 12 not started · 1 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | Archiving runs synchronously inside message acceptance | The archive copies happen between the anti-spam/modification stages and PersistentMessage::SaveObject, i.e. before the 250 is sent, so archive I/O latency is added to every accepted message and a slow archive volume slows acceptance. |
| ✅ | AWStats delivery journal | Per-recipient tab-separated stream (time, sender, recipient, sender IP, recipient IP, SMTP, ?, code, bytes) written to hmailserver_awstats.log on delivery success and failure; toggled by Logging.AWStatsEnabled… |
| ✅ | Backup and restore over COM | BackupManager.StartBackup() and LoadBackup(xmlFile) → Backup.StartRestore(); BackupSettings selects destination, whether to include settings/domains/messages and whether to compress… |
| ✅ | Backup log | Backup progress is written to hmailserver_backup.log (UTF-16 with BOM) and the path is exposed through BackupSettings.LogFile. |
| ✅ | Built-in diagnostic test suite (9 tests) | TestInformationGatherer, TestIPv6, TestOutboundPort (needs a test domain), TestBackupDirectory, TestMXRecords, TestConnectToSelf (both need a local domain), TestDataDirectory, TestIPRanges, TestErrorLogs — each returning name… |
| ✅ | Delivery-queue depth caching | hmailserver_delivery_queue_messages is backed by a COUNT(*) against hm_messages, cached for 10 s so a fast scrape interval cannot hammer the database; safe without locking because clients are handled serially. |
| ✅ | Filesystem archive on receipt | With ArchiveDir set, every accepted message is copied to {ArchiveDir}\{senderDomain}\{senderUser}\Sent-<file> for local senders, to {ArchiveDir}\Inbound\ for external senders… |
| ✅ | Instrumented span points | Three: one server-kind span per client protocol command line (named after the verb, attributed with hmailserver.session.id, one trace per session), one client-kind db.query span at the database chokepoint… |
| ✅ | JSON-lines structured logging | JsonLogging=1 switches every category to one JSON object per line with ts, category, thread, optional session, optional remoteip and message, with correct escaping including \u00XX for control characters. Off by default… |
| ✅ | Kubernetes-style probes | Three probes on the metrics listener: /livez always 200 if the thread answers (deliberately no dependency checks, so a database outage does not get a healthy process killed)… |
| ✅ | Live log streaming over COM | Logging.EnableLiveLogging + Logging.LiveLog drain an in-memory buffer; the buffer self-disables past LiveLogMaxSize so a listener that walks away cannot grow it without bound. |
| ✅ | Log categories and files | Nine destinations: dated hmailserver_<date>.log, dated ERROR_hmailserver_<date>.log, hmailserver_awstats.log, hmailserver_backup.log, hmailserver_events.log… |
| ✅ | Log retention | LogDeleteDays prunes by last-write time, run once shortly after start-up and then every six hours. Deliberately narrow: only files named hmailserver_*.log or error_hmailserver_*.log are ever deleted. Disabled by default (0). |
| ✅ | Log rotation | Daily only, and implicit — rotation happens because the filename embeds the date and the writer reopens when the name changes. No size-based rollover, no numbered generations, no compression… |
| ✅ | Log volume controls | LogLevel (>=3 full detail, <=2 quieter) plus MaxLogLineLen truncation that keeps the head and last 25 characters with " ... " between; debug logging overrides truncation. |
| ✅ | Message-store consistency check | Background task cross-checks hm_messages against files on disk at start-up and hourly, publishes hmailserver_messagestore_missing_files, and rewrites hMailServer_messagestore_consistency.report in the log folder… |
| ✅ | Metric catalogue (20 families) | Counters: hmailserver_processed_messages_total, _spam_messages_total, _viruses_removed_total, _tls_handshakes_total, _tls_handshake_failures_total, _auth_success_total, _auth_failures_total, _messages_delivered_total… |
| ✅ | Metric labels | Only two label dimensions exist: hmailserver_sessions{protocol="smtp"\|"imap"\|"pop3"} and hmailserver_db_connections{state="busy"\|"available"}. Every other series is a single unlabelled global value — no per-domain… |
| ✅ | OpenTelemetry trace export (OTLP/HTTP JSON) | Dependency-free exporter: batches completed spans onto a background thread and POSTs OTLP JSON to <OtelEndpoint>/v1/traces (default port 4318), tagged with a service.name resource attribute from OtelServiceName… |
| ✅ | OpenTelemetry trace export and JSON logging | OtelTracer exports spans in batches to an OTLP/HTTP collector (OtelEndpoint/OtelServiceName; a cheap no-op when unset) with message-to-session correlation IDs; JsonLogging=1 switches the logger to JSON lines… |
| ✅ | Operator runbooks | Two operator-facing documents ship in-tree: DiagnosingStalledMail.md and HighAvailabilityRunbook.md. |
| ✅ | Optional hardlinking of per-recipient archive copies | ArchiveHardLinks=1 uses Win32 CreateHardLink for the per-recipient copy and falls back to a full copy (logging "HardLink failed.. Falling back to Copy.") when the link cannot be made — e.g. across volumes or on non-NTFS. |
| ✅ | Prometheus /metrics endpoint | Text exposition format version 0.0.4 over plain HTTP/1.0, single accept thread, 5 s socket timeouts. Off by default (MetricsServerPort=0), default bind 127.0.0.1. |
| ✅ | Prometheus metrics and Kubernetes-style health probes | /metrics plus /livez (liveness), /readyz (200 only when StateRunning and the DB pool is connected, 503 while draining) and /healthz (JSON status/state/database), covering processed/spam/virus counts, TLS handshakes, auth outcomes… |
| ✅ | Slow-query log | SlowQueryLogMilliseconds logs database statements above the threshold and feeds the hmailserver_db_slow_queries_total counter; 0 disables (the default). |
| ✅ | Work-queue stall reporting | A once-a-minute scheduled task names the tasks holding the message-acknowledgement threads when they are all busy with work queued behind them, running on a different queue than the one it measures… |
| ⏸️ | OTLP over TLS / gRPC transport | Endpoint must be an http:// URL; https:// is rejected at startup with "OtelEndpoint must be an http:// URL; tracing disabled." Consciously postponed in a source comment: "Only plain HTTP is supported … TLS export is future work." |
| ⬜ | Archive retention, index, search, immutability, per-domain scope | None of these exist. The archive is a raw directory tree with no database record, no retention sweep, no WORM/legal hold and no way to scope it to particular domains; only ArchiveDir and ArchiveHardLinks are configurable. |
| ⬜ | Authentication on /metrics | The metrics listener performs no authentication or authorisation of any kind — protection is entirely the bind address (default 127.0.0.1). Exposing it on 0.0.0.0 publishes queue depth, session counts and auth-failure counts to anyone. |
| ⬜ | TLS on /metrics | The metrics listener has no `SSL_CTX` and serves plain HTTP; it touches OpenSSL only to read certificate files and export their expiry as a metric. On the loopback default that is a reasonable choice and not a defect, which is why it is listed here rather than with the [TLS unification work](#structural-prerequisites) — but it means `MetricsServerBindAddress` cannot be moved off 127.0.0.1 without publishing the scrape in clear, and the same is then true of `/livez`, `/readyz` and `/healthz`. Decide it deliberately: either TLS through `SslContextInitializer` like ManageSieve, or documented as loopback-only by design and validated as such at startup. |
| ⬜ | Latency percentiles | hmailserver_command_processing_seconds and hmailserver_db_query_seconds are emitted as Prometheus summaries carrying only _sum and _count, so only the mean is derivable. No histogram buckets, no quantiles, so p95/p99 are unavailable. |
| ⬜ | Metric history / persistence | /metrics is a stateless scrape and nothing stores samples server-side; the only history anywhere is the Control Panel's three-minute in-RAM buffer. No metrics-sample table, no retention setting, so no 24 h / 7 d / 30 d views. |
| ⬜ | OTLP metrics and logs signals | Only the traces signal is implemented — the exporter path is hard-coded to /v1/traces and there is no metric or log record builder. The Control Panel blurb advertising "OpenTelemetry traces/metrics export" overstates what ships. |
| ⬜ | Queryable message trace | No per-message event store. The AWStats journal is already a per-recipient delivery event stream called from every interesting site, but it has no correlation key and no home… |
| ⬜ | Queryable message trace and metric history | Two named holes with no implementation: there is no per-message event record, so 'what happened to the message Jane sent at 14:20' means grepping logs… |
| ⬜ | Scheduled / automatic backups | CreateScheduledTasks_ registers greylist cleaning, expired-record removal, TLS-RPT reporting, log retention, message-store consistency, work-queue health and ACME renewal — no backup task… |
| ⬜ | SQL log device and NCSA log format | eLogDevice hLogDeviceSQL and eLogOutputFormat hLogFormatCSA are declared in the IDL, editable through COM and offered in the Control Panel's Logging page… |
| ⬜ | W3C traceparent ingestion / context propagation | Trace ids are always minted locally; no inbound traceparent header is parsed and none is emitted on outbound SMTP or HTTP, so traces cannot be joined to an upstream caller. |
| ⬜ | Windows Event Log integration | None. hMailServer's "event log" is hmailserver_events.log plus a COM EventLog.Write for scripts; there is no ReportEvent/RegisterEventSource call anywhere, so nothing surfaces in Windows Event Viewer. |

### Extensibility and scripting

37 shipped · 0 underway · 3 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | Client object exposes TLS session detail | IPAddress, Port, Username, HELO, SessionID, Authenticated, EncryptedConnection plus CipherVersion, CipherName and CipherBits - so a script can gate on TLS version/cipher strength |
| ✅ | COM control of the script engine | hMailServer.Scripting exposes Enabled, Language, Directory, CurrentScriptFile, Reload() and CheckSyntax() - so an external tool can flip scripting on/off, swap language and hot-reload without restarting the service |
| ✅ | Compile/syntax check before activation | LoadScripts compiles the file first and reports error HM5016 without registering anything if compilation fails; also exposed on demand via the COM API and Control Panel |
| ✅ | Control Panel event-script editor | Dedicated Scripts page: loads the file named by Scripting.CurrentScriptFile, edits it, writes a .bak beside it on save, then calls Reload() and reports the CheckSyntax() result inline. Plain TextBox - no syntax highlighting… |
| ✅ | Custom script function as a rule action | Rule action type ScriptFunction (5) invokes any named function in the script file with HMAILSERVER_MESSAGE; the message is reloaded afterwards so script edits to the file are picked up. Available to both global and per-account rules |
| ✅ | Event invocation by source-text concatenation | The call is built as text and appended to the script before parsing; string arguments are escaped per language (VBScript doubles quotes, JScript escapes apostrophes) - except OnDeliveryFailed… |
| ✅ | EventLog object always present | EventLog is added to every container by the constructor, so EventLog.Write is callable from any handler and writes to the server event log |
| ✅ | Fail-closed on a hung script file | If the file's top-level code exceeds the timeout during load, loading is deliberately abandoned and no event handlers are registered at all (including OnClientLogon / OnClientValidatePassword) until the admin fixes it and reloads… |
| ✅ | Fresh engine and full re-parse on every event | Each event creates a new CScriptSiteBasic, re-parses the whole script text with the call appended, runs it and terminates; no state persists between events and parse cost is paid per message/connection |
| ✅ | Full COM API reachable from scripts | Scripts can CreateObject("hMailServer.Application") and authenticate to reach the whole administration object model (domains, accounts, rules, messages)… |
| ✅ | Handler auto-registration by function probing | On load the server probes 15 named handler functions and only fires the ones that exist; each probe spins up a fresh engine and re-executes the file's top-level code… |
| ✅ | Legacy rules engine (global and per-account) | Nine predefined criteria fields (From/To/CC/Subject/Body/Size/RecipientList/DeliveryAttempts) with eight match types including regex and wildcard, and ten action types (Delete, Forward, Reply, MoveToIMAPFolder, ScriptFunction… |
| ✅ | Objects injected into the script namespace | Container maps names to COM wrappers for six types: Result, Message (hMailServer.Message), Client, EventLog, FetchAccount and Account; all are added as global members so handlers reference them by name |
| ✅ | OnAcceptMessage(oClient, oMessage) | Fires after end-of-data, on the async accept task that holds the thread sending the 250, immediately before the message is saved and queued; script may rewrite the spool file. Same 554/453 reject mapping |
| ✅ | OnBackupCompleted() / OnBackupFailed(sReason) | Fired by the backup manager at the end of a backup run; notification only |
| ✅ | OnClientConnect(oClient) | Fires on every accepted TCP connection for all listeners (SMTP/IMAP/POP3) before the greeting; Result.Value=1 drops the socket immediately |
| ✅ | OnClientLogon(oClient) | Fires after a successful authentication on all three protocols (SMTP AUTH, POP3 and IMAP LOGIN); notification only, no Result object, so it cannot veto the session |
| ✅ | OnClientValidatePassword(oAccount, sPassword) | Can override password validation: Result 0 accepts the logon outright, 1 rejects it, any other value (default 2) falls through to normal validation… |
| ✅ | OnDeliverMessage(oMessage) | Fires per delivery attempt after global rules; Result.Value=1 deletes the message. Because it runs on every retry, a script must be idempotent |
| ✅ | OnDeliveryFailed(oMessage, sRecipient, sErrorMessage) | Fires once per failed recipient (so several times for one message); notification only - no Result object is added to the container |
| ✅ | OnDeliveryStart(oMessage) | Fires once per message when delivery begins; Result.Value=1 deletes the message and logs "Action triggered by script subscribing to OnDeliveryStart" |
| ✅ | OnError recursion guard | When the killed script is the OnError handler itself, the interruption is logged rather than reported, because reporting an error re-fires OnError and would recurse until the stack is exhausted |
| ✅ | OnError(iSeverity, iCode, sSource, sDescription) | Fires for every ErrorManager::ReportError in the server, guarded so it is skipped before settings are loaded; this is the hook that makes SIEM/alert forwarding possible |
| ✅ | OnExternalAccountDownload(oFetchAccount, oMessage, sRemoteUID) | Fires per message fetched by the POP3 external fetcher (with oMessage null/Nothing when the message was skipped); the Result overrides the remote delete policy - 1 deletes immediately, 2 keeps for Result.Parameter days |
| ✅ | OnHELO(oClient) | Fires on both HELO and EHLO after the domain argument parses; Result 1 -> "554 Rejected", 2 -> "554 <message>", 3 -> "453 <message>". Client object carries IP, port, session, HELO name and TLS cipher details |
| ✅ | OnRecipientUnknown(oClient, oMessage) | Fires when the server is about to answer 550 unknown user; notification only - the Result object is not passed and no return value is acted on. Suppressed once the invalid-command limit trips |
| ✅ | OnSMTPData(oClient, oMessage) | Fires on the DATA command, before any body bytes are read, so a message can be refused without transferring it; same 554/554+msg/453+msg result mapping |
| ✅ | OnTooManyInvalidCommands(oClient, oMessage) | Fires when DisconnectInvalidClients trips the MaxNumberOfInvalidCommands limit and the connection is being dropped; notification only, no Result object |
| ✅ | Result object (Value / Message / Parameter) | Three writable properties; semantics are per-hook (reject codes for SMTP hooks, delete for delivery hooks, auth verdict for password validation, retention days for external download) and are not documented in one place in the code |
| ✅ | Script error reporting with source position | Runtime and compile errors are captured through IActiveScriptSite::OnScriptError and logged as "Script Error: Source ... Description ... Line ... Column ... Code ..."; the last message is retrievable for the syntax-check API |
| ✅ | Script execution watchdog | Each script invocation and each compile runs under a TimerQueue watchdog bounded by [Settings] ScriptTimeout; a script that overruns is interrupted, logged with the file name… |
| ✅ | Script execution watchdog (bounded scripts) | Every script run is armed with a timer-queue watchdog that calls IActiveScript::InterruptScriptThread; ScriptTimeout in hMailServer.ini, default 60s, 0 disables the limit |
| ✅ | Server-side event scripting (VBScript/JScript) | In-process Active Scripting host firing OnClientConnect/OnHELO/OnAcceptMessage/OnDeliverMessage/OnDeliveryStart/OnDeliveryFailed/OnClientLogon/OnClientValidatePassword/OnExternalAccountDownload/OnError… |
| ✅ | Single global event-script file | Loads exactly one file, {EventFolder}\EventHandlers.vbs\|.js, set by the INI key Directories/EventFolder; no per-domain or per-account scripts, no include/import mechanism, no multi-file support |
| ✅ | Starter templates for external integrations | Three insertable VBScript OnAcceptMessage templates: external AV/DLP command-line scanner, fire-and-forget webhook POST (SIEM/Slack/Teams), and an external HTTP verdict API… |
| ✅ | VBScript and JScript event scripting | Windows Active Scripting (IActiveScript) engine created by CoCreateInstance on the language name; only the literal settings "VBScript" and "JScript" are recognised… |
| ✅ | Watchdog limitation: blocked COM calls | An interrupt aborts script execution but cannot release a handler blocked inside a COM call (e.g. a synchronous ServerXMLHTTP to a dead host) - the limit is documented in the error text the admin receives |
| ⬜ | External filter hook (rspamd / milter / MTA hooks) | In-process scripting events (OnHELO, OnAcceptMessage, OnDeliverMessage, OnDeliveryFailed) and the rules engine cover a lot, but there is no way to put an external scanner in the SMTP path… |
| ⬜ | Script sandboxing / capability restriction | None: scripts run in-process under the service account with unrestricted CreateObject (the shipped Control Panel templates use WScript.Shell and MSXML2.ServerXMLHTTP); the only bound is the wall-clock watchdog. No allow-list… |
| ⬜ | XCLIENT / PROXY protocol | No support for either, so a TCP load balancer or front-end proxy in front of the SMTP listener will hide the real client IP from DNSBL, SPF, greylisting and the Received header. |

### Build, testing and supply chain

4 shipped · 0 underway · 1 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | CI, supply-chain and installer verification | Seven GitHub workflows: server build, CodeQL, SBOM (SPDX + CycloneDX via Syft), dependency review, installer smoke test on a clean machine, general CI and a monthly upstream-comparison job with a checked-in baseline… |
| ✅ | Installer and in-place upgrade path | A single Inno Setup x64 installer that preserves configuration and mail on upgrade, bundles and silently installs the .NET 8 Desktop Runtime, ships SQL CE for zero-config installs and stages the MariaDB connector plus plugins… |
| ✅ | Regression coverage for hooks and scanners | 24 event tests covering every hook in both VBScript and JScript, 5 ClamAV tests including live EICAR detection plus unreachable-scanner fail-open, and 14 SpamAssassin tests including a deliberate spamd outage |
| ✅ | Regression coverage for the authentication surface | Dedicated NUnit suites exist for SCRAM-PLUS on IMAP/POP3/SMTP, TLS version and TLS option negotiation, OAuth2 bearer, hash policy, password pepper, secret protection, auto-ban, password masking and protocol fuzzing. |
| ⬜ | Architecture guide and docs index | The detailed codebase map is kept local and unpublished, so a first-time contributor has CONTRIBUTING.md and nothing else; hmailserver/docs/ has two guides and no index. Both are named as outstanding in the roadmap's near-term list… |

### Cross-cutting and platform

9 shipped · 1 underway · 2 not started · 0 deferred

| | Capability | Detail |
|:-:|---|---|
| ✅ | Active/passive HA topology | A documented, tested warm-standby runbook: exactly one node running, shared SQL plus shared message store, floating VIP driven by the /readyz probe, no clustering code and explicit split-brain avoidance… |
| ✅ | Built-in diagnostics suite | A 'run diagnostics' set exposed through COM and the Control Panel: outbound port connectivity, connect-to-self, MX record checks, IP range sanity, data and backup directory checks, error-log inspection and an explicit IPv6 test… |
| ✅ | Certificate lifecycle outside ACME | ACME issues, renews at <30 days, auto-assigns and hot-reloads without a restart. For manually provisioned certificates there is no expiry check, no warning… |
| ✅ | Graceful shutdown drain | [Settings] ShutdownDrainSeconds makes the service wait up to N seconds for active sessions to finish before stopping (0, the default, stops immediately), and /readyz returns 503 for the duration so a load balancer sheds the node first. |
| ✅ | IPv6 support end to end | Real but uneven, and never stated as a whole: IPAddress models IPV6, security ranges and DNSBL reverse-nibble lookups handle it, AAAA is used in HELO/PTR checks and there is a dedicated IPv6 diagnostic — but the ManageSieve listener ca… |
| ✅ | Localisation of server, Control Panel and installer | Server UI strings ship in exactly two languages (english.ini, swedish.ini) selected by UserInterfaceLanguage; the Inno Setup installer declares only English; the WPF Control Panel has no resx localisation at all… |
| ✅ | Scheduler and background task set | A minute-polled scheduler running ScheduledTask objects — TLS-RPT reporter, ACME renewal, backup, log retention, message-store consistency, greylist/expired-record cleanup, external fetch… |
| ✅ | Scripts and scanners cannot starve the accept path | Script and scanner work runs on the shared async queue with AsyncQueueReservedThreads held back for short work, per-stage timings logged around the script/save stage… |
| ✅ | Work queue saturation reporting and session caps | WorkQueue exposes queue depth, blocked-task count and per-task name/thread/wait/run time; WorkQueueHealthTask reports saturation and names the task holding each thread; AsyncQueueStallThreshold and AsyncQueueReservedThreads bound it… |
| 🔄 | Client-facing surfaces | **Reopened, August 2026.** Three of the five surfaces previously declined here are now in the next-generation programme above: a **webmail of our own**, **JMAP**, and **CalDAV/CardDAV**. Two remain declined and the reasons still hold: LMTP only matters behind another MTA, which is not a realistic Windows deployment; and EAS/EWS are proprietary, patent-encumbered, and being retired by their own vendor. |
| ⬜ | End-user self-service portal | There is no web surface for users at all — no password self-service, no vacation toggle, no quota view, no quarantine release. Every user-initiated change goes through an administrator… |
| ⬜ | LDAP / Active Directory as a directory backend | Accounts can be linked to an AD domain and the Control Panel has a read-only AD picker, but there is no LDAP account source — the account database is always the SQL store. On Windows this is the deployment case that matters most. |

The next generation: engine to service
--------------------------------------

Everything above this line is a mail *engine*, and a good one — the protocol and
transport-security work is ahead of the mainstream self-hosted field. What it has
never had is the surfaces people actually touch. You cannot read your mail with it
without installing something else, you cannot find a message without a linear scan,
and two people cannot share `info@` without sharing a password.

**The next generation is the same engine, with those surfaces built rather than
delegated.** That is the whole of it.

This section was designed in August 2026 by seven parallel designs against the real
tree — webmail, full-text search, the API, sharing, JMAP, groupware, and an
adversarial security review of all of it — then reconciled into one sequence. The
calls below are decisions, not options.

### The hard calls, and what they cost

**Webmail talks to the store, not to IMAP over a loopback socket.** An IMAP client
inside the server would reuse every existing access-control decision for free, which
is genuinely attractive. It is rejected because it would create a second instance of
the byte-fidelity defect already deferred in the 6.2.15 backlog, and because a
loopback protocol hop is a permanent tax on every request. **The mitigating clause
matters more than the decision:** each piece of logic extracted from `Server/IMAP/`
lands first as an *IMAP-only* refactor, proven by the existing IMAP tests, before any
web code is allowed to call it. That way the shared path is verified by the suite
that already exists rather than by the new code that needs it.

**REST first, JMAP-shaped, JMAP last — and droppable.** The JMAP-versus-REST framing
turned out to be wrong: roughly ninety of the API's endpoints are server
administration, which JMAP has nothing to say about, so a REST v2 is happening
regardless of what we decide about JMAP. For the contested mail-client half, REST
comes first but is deliberately built JMAP-shaped — the same entity names, opaque-id
semantics, and a per-id `/set` envelope — so that adding JMAP later is a translation
layer rather than a second server. JMAP itself goes last, and is the one phase that
can be dropped without the rest being a failure. Its own design conceded it must be
justified by what it does for our webmail; by the time we reach it the webmail
already has threading, keywords and push, so that justification has expired.

**A real HTTP server, on Boost.Asio.** Four of the seven designs proposed
hand-rolling HTTP/1.1. Boost 1.91 is already on the include path, so that is
rejected in favour of Beast: keep-alive, chunked encoding, `Range`, and multipart are
all things a mail client needs and none of them are things worth writing twice. It
also inherits TLS from `SslContextInitializer`, the security-range check at accept,
and the existing exception discipline — which is three of the prerequisites above
solved by construction rather than by remembering.

**No new compression dependency.** Precompressed sibling assets instead of linking
zlib. Cheaper, and it removes the BREACH question by absence rather than by
discipline — there is no dynamic compressor to leak a CSRF token through.

**Groupware before JMAP.** Contacts are what a webmail needs on day one for
compose auto-completion, and a calendar is what makes the product a plausible
Exchange alternative. Both serve users; JMAP serves an interoperability story that
nobody is currently asking for.

### Honest size

**Roughly 148 person-weeks of implementation — about three person-years.** For one
maintainer at realistic utilisation that is **four to five calendar years, and the
webmail alone is two of them.** That number is not padding and it is not
flinching; it is what the seven designs add up to when nobody is allowed to say
"and then the client part".

Two consequences, and they drive the ordering rather than being caveats to it:

1. **Every phase ends somewhere shippable.** No phase is a down payment that only
   pays off on completion, and each one can be abandoned at its boundary without
   what came before becoming dead code. A phase that fails that test is drawn
   wrong and gets redrawn.
2. **Something outranks all of it.** The Microsoft 365 Basic auth cutover is
   **four months away**, it is not part of this programme, and it breaks working
   installations. Dated external deadlines beat architecture, every time. See
   [Dated items](#dated-items--the-forcing-functions).

### The phases

| | Phase | What ships, and what a user can do afterwards that they cannot now |
|:-:|---|---|
| 🔄 | **0. Prerequisites** | The nine items in [Structural prerequisites](#structural-prerequisites), led by the crash oracle — because every gate after this assumes the suite can see a crash, and until 12 August 2026 it could not. Nothing user-visible. This is the phase that is tempting to skip and must not be. **Five of the nine are closed and one is part-done:** the crash oracle is armed and checked before and after every test, `ManageSieveServer` has a barrier, every TLS listener in the server now takes its configuration from `SslContextInitializer` (so `SSL_CTX_new` appears nowhere), the SQL substitution fallback no longer corrupts queries — and closed a live SQL-injection path through a mail header while it was being fixed — and `IMAP SEARCH` is bounded. Fuzzing closed too, on the same day and last deliberately — it was the one item genuinely worth waiting for the oracle, because a fuzzer that cannot tell when it has found something is just a load generator — and it repaid the wait immediately by finding undefined behaviour in the MIME parser that every message with a charset and a transfer encoding was reaching. **Six of the nine are closed.** Remaining: the single authorisation choke point, the web-shaped concurrency model (which is Phase 1's foundation), and the rest of the ignored return values and NUL truncations. |
| ⬜ | **1. HTTP foundation** | A real HTTP/1.1 server on Boost.Asio, with `RestApiServer` and `WebServicesServer` re-hosted on it and their raw accept loops deleted. User-visible outcome: the existing admin API and web services get keep-alive, correct caching, ACME certificate hot-reload and the same TLS hardening as the mail protocols. Modest on its own, and it is what makes everything after it possible. |
| ⬜ | **2. Full-text search** | A portable posting-list index maintained by the existing `MessageIndexer`, with a throttled resumable backfill for mail that already exists. Afterwards: IMAP `SEARCH BODY` over a large mailbox returns in a reasonable time instead of scanning every message — which is a real improvement for every existing client, with no webmail required. This is why it comes before the client and not with it. |
| ⬜ | **3. REST v2 and the self-service portal** | The full API — mailboxes, folders, messages, submission, attachments, search, settings, filters — plus browser sessions, and the smallest possible web surface on top: password change, vacation, quota. Afterwards: a user can change their own password without an administrator. That alone retires a permanent support burden. |
| ⬜ | **4. Shared and delegated mailboxes** | The IMAP other-users namespace, per-folder ACL evaluation through the single authorisation choke point, and Send-As. Afterwards: three people handle `info@` with their own credentials and their own 2FA, instead of sharing one password. The most-requested missing feature, and the one whose absence is actively insecure. |
| ⬜ | **5. Webmail** | The flagship, and two calendar years of the estimate. An SPA over the REST API, with the message body rendered in a unique opaque origin so that no HTML is ever generated in C++. Afterwards: read and send mail from a browser, with nothing else installed. |
| ⬜ | **6. Contacts, then calendaring** | CardDAV first because compose auto-completion needs it and users notice it immediately; then CalDAV with the WebDAV/ACL/sync-collection layer beneath it, and **iMIP**, which is the only standards-based bidirectional path to Exchange and Microsoft 365. |
| ⏸️ | **7. JMAP** | Core plus Mail. Deferred, deliberately last, and explicitly droppable — see the hard calls above. |

### What is still refused, under the higher bar

"A third party already does it" is no longer a reason. These survive that:

* **Exchange ActiveSync and EWS.** Proprietary, patent-encumbered, and being
  retired by their own vendor — phased disablement from October 2026, fully gone
  April 2027. Building toward something its owner is switching off is not a
  judgement call.
* **LMTP.** Only matters when another MTA fronts this one, which is not a
  realistic Windows deployment.
* **Active/active clustering.** Dovecot removed Director and its replicator
  outright and now documents its community edition as single-server; HA moved to
  the commercial product. Multi-node is no longer part of the open-source baseline,
  so its absence is not a gap. Warm standby, documented and tested, is the honest
  deliverable.
* **An embedded browser control in the Control Panel.** The WebView2 SDK licence
  prohibits distribution in a way that would subject it to copyleft, which is
  exactly what shipping it inside an AGPLv3 application does.

Structural prerequisites
-----------------------

**These are not features, and they are not optional.** Every client-facing item in
the next generation lands on top of the optional HTTP listeners and the SQL layer,
and neither is currently built to carry it. Shipping a web application on the
present foundation would be precisely the half-measure this programme exists to
avoid.

*Corrected 12 August 2026.* An earlier version of this section claimed all four
listeners lack an exception barrier. That was wrong, and checking it produced a
worse finding.

| | Prerequisite | Why it gates everything above it |
|:-:|---|---|
| ✅ | **The test suite cannot currently see a memory-safety bug** | This is the one that gates the gates. The project builds with `<ExceptionHandling>Async</ExceptionHandling>`, so `catch (...)` catches **structured** exceptions as well as C++ ones — an access violation inside a `try` is swallowed and reported as an ordinary failure. And there is no `set_terminate`, no `SetUnhandledExceptionFilter` and no `_set_se_translator` anywhere in the server. So a null dereference or a buffer overrun in a request handler can be caught, logged and shrugged off, and 1144 green tests will say nothing about it. Every other gate in this programme assumes the suite would notice a crash. Until a crash oracle exists — a filter that writes a minidump and fails loudly rather than letting a `catch (...)` absorb it — the suite is measuring the wrong thing. `hMailServer.Minidump.exe` already exists in the tree as a starting point. **Shipped 12 August 2026.** `CrashOracle` registers a vectored exception handler with `AddVectoredExceptionHandler(1, ...)`, so Windows calls it at *first chance* — before any frame-based `catch (...)` can absorb the fault — plus a `SetUnhandledExceptionFilter` for the terminal case. It writes one line per fault to `<LogDirectory>\crash-oracle.log` and reuses the existing out-of-process minidump writer. The line that makes it an oracle rather than another log nobody reads is in `TestFixtureBase.SetUp`: `CrashOracleAsserts.AssertNoMemorySafetyEvents()` runs before **every** test, so a fault provoked by one test fails the run even if the server carried on. Four tests cover it, including a negative control that the preflight can actually fail. |
| ✅ | **`ManageSieveServer` has no exception barrier at all** | Verified by count, as the tree stood on 12 August 2026 before this was fixed: `MetricsServer` had 9 `catch` statements, `WebServicesServer` 5, `RestApiServer` 3 — and `ManageSieveServer` **zero**. (The two HTTP listeners are each one higher now, from the TLS row below.) It is a raw `std::thread` accept loop, so an escaped exception is `std::terminate` and the whole mail server dies. It is off by default, which is the only reason this has not bitten. One listener, one barrier, and then the same discipline applied structurally rather than per-file. **Shipped 12 August 2026.** The accept loop and each session now have a barrier, so an escaped exception costs one ManageSieve connection instead of the mail server. TLS came with it — see the row below. |
| ✅ | **One TLS configuration path for the listeners** | The optional listeners each build their own `SSL_CTX` and never consult `SslContextInitializer`, so the hardening applied to SMTP/IMAP/POP3 does not reach them — including the post-quantum key-exchange groups shipped this month. A user who reads "post-quantum key exchange" in the release notes and then serves mail over a web listener is getting classical-only key exchange with no way to know. Either the listeners go through the shared initialiser, or the setting is honestly scoped in the documentation. The first is correct, and it also gets them ACME certificate hot-reload for free. **Shipped 12 August 2026.** `SSL_CTX_new` no longer appears anywhere in the server: ManageSieve, the REST API and the Web Services HTTPS listener all take their context from `SslContextInitializer::InitServer`, which is the same call `TCPServer` makes for SMTP, POP3 and IMAP. The bridge is thin by design — the initialiser wants a `boost::asio::ssl::context` and an `SSLCertificate`, so each listener builds both and then hands `native_handle()` to its own blocking sockets; nothing about the TLS configuration is restated locally, so nothing local can drift again. Two things are deliberately *not* inherited: each HTTP listener re-applies its TLS 1.2 floor after `InitServer`, because the shared option mask follows the `[Settings]` protocol toggles and an administrator may well have opened those up for a legacy *mail* client — applied afterwards, it can only tighten. `MetricsServer` turns out not to belong on this list at all: it has no `SSL_CTX` and serves **plain HTTP**, reading certificates only to export their expiry as a metric. It is off by default (`MetricsServerPort` 0) and binds `127.0.0.1`, so plaintext is defensible for a local scrape, and whether it should be able to serve TLS is now [its own item](#observability-and-diagnostics) rather than part of this one. Pinned twice over by `SSL/ListenerTlsConfiguration.cs`, because the two things worth proving are different. First, *which implementation configured the context*: each listener is pointed at a certificate that does not exist and the failure must arrive as **HM5113**, a code only `SslContextInitializer` reports — against the previous build both listeners logged that with `LOG_APPLICATION` and reported nothing, so those tests fail there for the right reason. Second, and more to the point, *the consequence*: each listener must actually negotiate `X25519MLKEM768`. `SslStream` cannot report a negotiated key-exchange group, so that is measured with a hand-built TLS 1.3 `ClientHello` carrying `supported_groups` but an **empty** `client_shares` — RFC 8446 leaves the server no option but a `HelloRetryRequest` naming the single group it wants, which arrives before any certificate or cipher is involved. Offer it the hybrid group and `X25519` and its answer says which it prefers. That probe was written for the ManageSieve STARTTLS tests and is now `Shared/TlsHandshakeProbe.cs`, shared by all three. |
| ⬜ | **A concurrency model that fits a web workload** | The listeners are serial accept loops: one request handled to completion before the next is read, HTTP/1.0 with `Connection: close`, no keep-alive, no streaming, the whole response built in memory. Adequate for a metrics scrape every 30 seconds; hopeless for a mail client issuing dozens of small requests per screen, and incapable of expressing either streaming shape a webmail needs (attachment download out, upload in). Needs a real HTTP server on the existing Boost.Asio stack — with a **bounded** worker pool, a connection cap, per-connection and per-request absolute ceilings rather than idle timeouts, and its own `io_context` so a webmail request storm cannot starve SMTP accept. |
| ✅ | **Fix the parameter-substitution fallback before anything writes a large query** | `SQLStatement::GenerateFromCommand` substitutes parameters by naive `String::Replace` in insertion order, and that path is taken whenever `GetSupportsCommandParameters()` is false — which is **MySQL and PostgreSQL**, while MSSQL and SQL CE use real parameters and never exercise it. So `@T1` replaced before `@T10` silently corrupts the query, on two of four backends, invisibly on the other two. Latent today: two real prefix collisions exist (`@UID` against `@UIDFAID` and `@UIDID`) but never appear in the same command. It stops being latent the moment anything uses ten-plus numbered parameters, which a full-text-index batch insert does by nature. Longest-name-first ordering, or word-boundary matching, and a test that pins it. **Shipped 12 August 2026,** and neither of those two fixes was sufficient. Investigating it turned up a worse case than the prefix collision: a parameter *value* containing the text of a name substituted later had that name replaced **inside the value**, and because a string replacement carries its own quotes, that ends the literal early and splices the other value into the statement as SQL. `hm_message_metadata` is written with four attacker-supplied header fields in one `INSERT`, so it was reachable by sending a mail whose `From` header contained `@metadata_cc_9`. Longest-first ordering does not fix that, and nor does a word boundary; only refusing to re-examine substituted text does. `GenerateFromCommand` is now a single left-to-right pass that matches longest-name-first at each token and never looks at its own output. Seven cases in `SQLStatementTester`, reachable from the suite through `Utilities.RunTestSuite`. Generated names also gained an underscore before the ordinal, because a column `x` numbered 12 and a column `x1` numbered 2 both produced `@x12` — one column's value written into another, silently; a duplicate name carrying a *different* value is now reported (HM5842) rather than resolved by first-one-wins. |
| ⬜ | **A single authorisation choke point** | Every existing access-control decision is made against "the logged-in account". Shared mailboxes and a REST API both introduce paths where the answer must instead be "the owner of the folder being touched". Scattering that across endpoints is how mail servers serve other people's mail. Note the `UseIMAPACL` bypass has **two** early-return sites, not one, which is exactly the kind of thing a single choke point prevents. |
| ✅ | **Bound `IMAP SEARCH` now, before any index exists** | `SEARCH BODY`/`TEXT` loads every message in the mailbox and substring-scans it. That is an authenticated CPU-exhaustion vector today, needs no new feature to exploit, and needs no index to fix — a time or bytes-examined ceiling that returns partial results or refuses is a small change. Full-text search removes the motive; a bound removes the vector. **Shipped 12 August 2026.** Two absolute ceilings on one search, both measured from the moment it starts and neither re-arming on progress — `IMAPSearchTimeout` (60s) bounds the resource actually under attack, which is thread-seconds, and `IMAPSearchMaxMegabytes` (2048) bounds it deterministically, so the same mailbox behaves the same way on fast and slow storage. An over-budget search returns **`NO` with RFC 9051 `LIMIT`** and no untagged response at all, which is the opposite of the obvious choice and the right one: RFC 3501 has no partial `SEARCH` result, so a truncated `* SEARCH` line is indistinguishable from a complete one, and a client acting on the set in bulk — Thunderbird's Search Messages window offering Delete, an archiver keeping what the search returned — would silently act on a list missing matches. `NO` is the only answer a client can detect, and its existing error path already handles it. `$` is cleared rather than left stale, which RFC 5182 leaves undefined after a failed search. |
| 🔄 | **Clear the ignored return values and NUL truncations** | Five write calls whose result is discarded and two places where a NUL truncates a value. Small, unglamorous, and exactly the class that turns into a silent-corruption bug once a web client is exercising these paths thousands of times a day rather than a protocol session doing it occasionally. **Underway,** and it turned out to be the most serious item on this list rather than the least glamorous. Everything in it that could lose mail is now closed. `FileUtilities::Move` deleted the destination and *then* renamed, so a failure between the two lost the message and left nothing behind; the rename does the overwrite atomically (`MoveFileExW` with `MOVEFILE_REPLACE_EXISTING`), and the `overwrite` parameter is gone rather than ignored — every value behaved identically while the call sites read as though they had chosen something, and one caller had already written a careful paragraph reasoning about which branch it wanted. Both SpamAssassin write-back results are checked (HM5860, HM5861); one was discarded on the delivery path. But the worst of them was `TransparentTransmissionBuffer::SaveToFile_`, which read `bool bResult = file_.Write(...)`, never looked at `bResult`, and returned `true` unconditionally — so a failed spool write during `DATA` was invisible twice over: dropped where it happened, and reported to every caller as success. Which of the two failure shapes actually lost mail was measured against the pre-fix binary rather than assumed, and it is not the obvious one. A spool file that received **nothing** was already refused with a 451 — not by any check that meant to catch it, but because the accept path fails downstream on a message with no content, and with nothing written to the error log, so an operator got a refusal with no reason. A spool file **truncated** mid-message is the shape a disk that fills up actually produces, and it has content, headers and a non-zero size, so it passed every downstream guard: that message was accepted with **250** and delivered short. The sender's copy is gone the moment it is told 250, so that is mail loss with an acknowledgement, which is the most serious class there is. A short write now counts as a failure too, the two `FlushToDisk` calls behind `MessageStoreFsync` are checked — an unchecked fsync buys nothing but the appearance of durability — and the receive path answers **451 4.3.0** through the handler that already existed for a spool file that could not be created, transient because a disk that is full now may not be in ten minutes and a `554` would bounce recoverable mail. MSVC does not warn about the unused variable at `/W3` (C4189 is a `/W4` warning), which is why it lasted. The same failure reached the **external POP3 fetcher** in a worse shape still: a partial write passed the existing zero-byte check, so the truncated message was delivered as though complete and the `DELE` that followed removed the only intact copy from the remote server — gone from both ends. It now abandons the fetch without a `DELE`, leaving the message to be collected again. Provoking a write failure needs fault injection, so the server carries `[Settings] SimulateSpoolWriteFailure` — deliberately ini-only and absent from the Control Panel — with mode 1 failing every write and mode 2 failing every write after the first, because those are the two shapes and only one of them was dangerous. `SMTP/SpoolWriteFailure.cs` covers both, and was checked against a purpose-built pre-fix binary: the truncated test fails there with "DeliveryFailedException was expected, but not thrown", which is the 250 being sent, and the empty test fails only on the missing HM5862. Still open, and the only reason this row is not ticked: the two NUL truncations, which are lower-consequence and whose specific sites need re-identifying. |
| ✅ | **Fuzz the parsers we already have, before adding more** | MIME today; HTML, iCalendar and vCard next. Every one is untrusted input in a process that must not crash, and a crash here is a mail outage rather than a bad request. libFuzzer needs a clang toolchain alongside MSVC, which is the real work — and it is worth noting that this item is nearly useless until the crash oracle above exists, because a fuzzer needs to be able to tell that it found something. **Shipped 12 August 2026, and it found a live defect in production code within seconds of first running.** Three libFuzzer targets under `fuzz/` (whole message, header block, transfer-encoding decoder) built with clang-cl and `-fsanitize=fuzzer,address`, seeded from the suite's own 59 `.eml` resources, with a MIME dictionary and a `regression/` directory whose reproducers are replayed on every run. Four things had to be measured rather than assumed: LLVM does not ship in the VS install here and had to be a portable user-local extraction; `_DISABLE_STRING_ANNOTATION` and `_DISABLE_VECTOR_ANNOTATION` are mandatory, because the prebuilt `clang_rt.fuzzer` library is built with MSVC STL's ASan container annotations off and `lld-link` otherwise refuses on `/failifmismatch: annotate_string`; `/MD` cannot link against that library at all (`/failifmismatch: RuntimeLibrary`), so the static CRT is the default; and the harness needs its own `stdafx.h` shim, because the real one `#import`s the ADO type library, which clang rejects outright. **The finding:** `AddressSanitizer: new-delete-type-mismatch` at `Mime.cpp:1044` in `MimeBody::GetUnicodeText`. `MimeCodeBase` has virtual `Encode`/`Decode` and **no virtual destructor**, while six sites in `Mime.cpp` `delete` a derived coder through a `MimeCodeBase*` — undefined behaviour, in which the derived destructor never runs and the deallocation uses the wrong size. The reproducer is not a mutated blob: it is an **unmodified seed**, an ordinary Outlook `multipart/alternative` message with a quoted-printable part, so this was reachable by every message with a charset and a transfer encoding that took that path. Exactly the class the 1168-test suite cannot see — nothing crashes, and the leak is small enough per message to look like ordinary growth. Fixed by making the destructor virtual, and the fix is proven by replaying the artifact that found it. **A second defect, also found and also fixed:** `libFuzzer: out-of-memory` through `MIMEUnicodeEncoder::EncodeValue` → `FieldCodeBase::Encode` → `MimeEncodedWord::QEncode`, where a 3.4 KB input drove the process past a 2 GB RSS limit. The cause was arithmetic rather than buffer handling: both encoders computed their budget as `MAX_ENCODEDWORD_LEN - charset_length - 7`, and since that constant is 75, a charset name of 69 characters or more makes the budget **zero or negative**. In `QEncode` the "line is full" test is then true on every byte, so each input byte emitted a complete `=?<charset>?Q?` header and the output grew as input × charset — the six-hundred-thousand-to-one amplification. `BEncode` was worse: its block size went non-positive, so it encoded nothing per iteration while still appending a header, and **did not terminate at all**; the only guard was an `ASSERT`, which the shipped Release build compiles out. Both now fall back to the raw encoder when no legal encoded word is possible, which is the honest answer rather than a workaround — no real charset name approaches 69 characters and no encoded word built from one would be decodable. The reproducer that took 2 GB now runs in 1 ms, and it has moved into `fuzz/regression/` so every future run proves it stays fixed. `fuzz/findings/` remains for the next unfixed one, with the rule written down: never in `artifacts/` (gitignored, wiped by a clean) and never in `regression/` (replayed, so it would fail every run). A longer session then found **two heap-buffer-overflow reads**, both on paths any received message reaches, which is a more serious class than the first two. In `MimeBody::Load` the content copy advanced `pszData` by `nSize` without reducing `nDataSize`, so the subsequent `pszEnd = pszData + nDataSize` sat `nSize` bytes past the end of the buffer and was then passed to `GetBoundaryEnd` as the limit to search up to — `FindString` respects its limit exactly, so it read a full boundary length (27 bytes) beyond the allocation. A pointer/length desync, not a missing check, and a first attempt that clamped two `pszData - 2` step-backs was necessary but not sufficient; the replay is what said so, which is the argument for keeping reproducers. In `MimeEncodedWord::Decode` a single `if` carried three unbounded reads: `pbData[1]` where the loop only guarantees `pbData < pbEnd`, `::strchr` on a length-delimited header field with **no NUL terminator**, and `pszHeaderEnd[2]` dereferenced *before* the bounds test meant to protect it, because `&&` evaluates left to right. Both fixed, both proven by replay, both now permanent regression seeds. The third target, `mime_header_fuzzer`, is clean over 18,978 inputs and its full seed corpus. **Four defects in the MIME parser in one day of fuzzing, none of which 1168 passing tests could see.** |


Control Panel: findability, and the Ctrl+K problem
--------------------------------------------------

**Ctrl+K is a symptom, not a cure.** A command palette over 244 individual
settings was added because settings were hard to find. It works, and it should
stay — but a search box is an accelerator for someone who already knows the name
of the thing they want. It does nothing for the much more common case: an
administrator who knows what they are trying to *achieve* and cannot work out
where the product put it. If the only reliable way to find a setting is to search
for it, the navigation is wrong.

The root cause is written in the code. `BuildNavTree` carries the comment
*"Mirrors the classic Administrator tree layout"* — the tree was inherited from a
2000s-era MFC application rather than designed around what people come here to do.
And the failure is already documented in the same file: the "Advanced hardening"
page was renamed and moved out of Security because, in the comment's own words,
*"admins were not finding them under a heading that told them not to touch
anything."* That fix was correct and it is also the tell — one page was noticed and
repaired by hand; the structural problem behind it was not.

### What is concretely wrong

Named specifically, because "improve the UX" is not actionable:

| | Problem | Why it costs the user |
|:-:|---|---|
| ✅ | **Almost everything lives under one "Settings" group, three levels deep** | Of 42 pages, the majority sit under `Settings → <group> → <page>`. "Settings" is not a category — it is where you put things when you have not decided what they are. Depth is not the issue in itself; an undifferentiated middle layer is. |
| ✅ | **TLS and certificates are spread across four pages in two groups** | "SSL certificates", "Transport security" and "Certificates (ACME)" all sit under Security, while "Web services & autoconfiguration" — which serves the MTA-STS policy and the ACME challenge — sits under Network. An administrator setting up TLS has to visit four pages in two branches and know which belongs to which. |
| ⬜ | **"Auto-ban & SSL/TLS" is two unrelated concerns in one page** | Brute-force lockout and transport encryption share nothing. The title is an admission that the page is a bucket. |
| ⬜ | **"Advanced INI settings" is an explicit catch-all** | A page whose organising principle is "the settings that live in the INI file" describes our storage layout, not the user's task. Its existence guarantees that some settings are findable only by search. |
| ⬜ | **"Advanced & scripting" and "Event scripts" are separate pages in the same group** | Two names for adjacent concepts, one of which is also a catch-all. |
| ⬜ | **Anti-spam is five pages, anti-virus two, with no overview** | SURBL servers, DNS blacklists, a white list and a greylisting white list are each their own leaf. There is no single page that answers "what is my spam configuration". |
| ✅ | **No task-oriented entry point beyond the Welcome tiles** | The Welcome page has quick-action tiles and `NavigateTo` exists to serve them, which is the right idea and is not carried through. Nothing answers "mail is not being delivered", "add a user", "set up TLS" as a starting point. |

### What to do about it

| | Work | Detail |
|:-:|---|---|
| ✅ | **Reorganise around tasks, not around our storage layout** | Group by what an administrator is doing — *Accounts & domains*, *Mail flow & delivery*, *Filtering*, *Security & access*, *TLS & certificates*, *Monitoring & diagnostics*, *Maintenance*. Dissolve "Settings" as a container and dissolve the catch-all pages by moving their contents to where they belong. This is the item that makes the rest unnecessary. |
| 🔄 | **Consolidate the TLS/certificate story into one place** | One page, or one group with an overview, that answers "is my transport security correct" — covering ports and connection security, certificates and their expiry, ACME, MTA-STS, DANE and TLS-RPT. These are one subject to the user even though they are five subsystems to us. |
| ⬜ | **Give every settings page a one-line purpose statement** | A page whose title needs the manual has failed. This also feeds the palette: a searchable description is far more useful than a searchable identifier. |
| 🔄 | **Expand the task-oriented entry point** | Grow the Welcome tiles into a real starting page keyed on intent, including a direct route into the stall-diagnosis path documented in [DiagnosingStalledMail.md](hmailserver/docs/DiagnosingStalledMail.md). The information exists; nothing points at it from the UI. |
| ✅ | **Keep Ctrl+K, and make it better** | It stays — it is genuinely the fastest route for an experienced administrator. Improvements worth having once the IA is fixed: match on the purpose statements above, show the navigation path of each result so the palette teaches the structure rather than bypassing it, and surface recently-visited pages. |
| ⬜ | **Settle the MVVM question** | `CONTROL-PANEL-PLAN.md` pre-decided `CommunityToolkit.Mvvm` and a `ViewModels/` folder. Neither exists; every view is imperative code-behind. That is not automatically wrong, but the plan and the code disagree, and a large reorganisation is the moment to decide rather than discover. |

### Accessibility, which is not optional

These are defects, not enhancements, and three of them are in the shipped
dashboard. They were found by validating the palette computationally rather than
by eye, which is the only way this kind of thing gets found.

*Reconciled against the code 13 August 2026, after the accessibility work landed.*
Two of these were ticked optimistically on the strength of a `ChartPalette` existing,
and reading it says otherwise: **`LineSmoothness` is `0` only on the High Contrast
branch and remains `0.8` for Light and Dark**, and the opaque `SurfaceArgb` is
likewise set only for High Contrast. So the spline interpolation that rounds off the
spikes being watched for, and the translucency that voids every contrast guarantee,
are both still live in the two themes essentially everyone uses. They stay ⬜, and
they are each a one-line change — the cheapest two accessibility fixes left on this
list, which is exactly why leaving them ticked would have been the expensive mistake.

| | Defect | Detail |
|:-:|---|---|
| ✅ | **The sessions chart is unreadable to a colour-blind administrator** | No `LegendPosition` is set and LiveCharts defaults to `Hidden`, so the chart draws three unlabelled lines whose only distinguishing feature is colour — and the dark-theme `Success #3FB950` against `Warning #D29922` separate by ΔE 5.1 under protanopia, against a target of 8. Two fixes, both needed: add a legend so identity is never colour alone, and split the **chart series palette** from the **status tokens**. Status colours belong on badges where they are paired with an icon and a label; they are the wrong basis for series identity. A validated six-slot series palette exists in the working notes and passes in both themes. |
| ✅ | **Charts ignore the theme toggle** | `DashboardView.xaml.cs` bakes `static readonly SKColor` values and never re-reads them, so the charts are the one part of the application that does not respond to `◐`. Needs a `ThemeTokens`→`SKColor` bridge subscribed to the theme-changed event. |
| ✅ | **`LineSmoothness = 0.8` on monitoring data** | Spline interpolation invents values between samples and rounds off spikes — precisely the events being watched for. Monitoring series must be `0`. |
| ✅ | **The X axis is hidden** | `IsVisible = false`, so the reader cannot tell whether the window is three minutes or three hours. It is three minutes. |
| ⬜ | **Chart cards are translucent over Mica** | Series land on a non-deterministic composited surface, so no contrast guarantee holds. Chart cards need an opaque background token. |
| 🔄 | **High Contrast is ignored by the charts** | `ThemeTokens` already has a High Contrast palette using `SystemColors`; the charts do not consult it. Every chart also needs a table view — which is both the High Contrast answer and the screen-reader answer. |
| 🔄 | **No screen-reader or keyboard audit has ever been done** | `AutomationProperties` are set on the navigation tree, which is a good start and is as far as it goes. A full pass needs: keyboard reachability for every control, a visible focus indicator, labels on every input, and announcement of validation errors. |
| ⬜ | **The Control Panel has no localisation at all** | No resx, so the admin UI is English-only while the server itself ships UI strings in two languages. |

Two things deliberately **not** planned here: radial gauges and speedometers (they
spend a large card on a single number that a stat tile renders in a line, and the
line in `CONTROL-PANEL-PLAN.md` sanctioning LiveCharts gauge series should be
removed), and animated KPI counters (an administrator watching a number tick up
learns nothing the final value did not tell them, and it costs a repaint per
frame on a machine usually being administered over RDP).

Planned work
------------

The gaps that cost users, ordered by value per unit of effort. Each notes what is
already in the tree that a fix would reuse, because that is what makes some of
these much cheaper than they look.

| | Item | Detail |
|:-:|---|---|
| ⬜ | **API keys for the REST API** | HTTP Basic only today — the admin password replayed on every request, no scoping, no expiry, no address restriction. Bearer tokens with labels and expiry. Small, and the cheapest security win available. |
| ⬜ | **Per-account outbound send limits** | The rate limiter is keyed by IP or destination domain, per minute. There is no per-*account* quota, so one compromised account can blacklist the IP with no ceiling. iRedMail ships this free. A security control, not a feature. |
| ⬜ | **Sieve extension set** | Only `keep`/`discard`/`fileinto`/`redirect`/`stop` and nine tests exist, and ManageSieve advertises exactly `"SIEVE" "fileinto"`, so any ManageSieve client sees an almost-empty capability set — and the filter UI in our own webmail will be built on it. `vacation` is the glaring one, and account-level auto-reply *already exists*; it is simply unreachable from Sieve. The lexer, parser and evaluator are already there. |
| ⬜ | **Shared and delegated mailboxes** | The other-users namespace is hard-coded to `NIL`, so no user can open another's mailbox. `info@`/`sales@`/`support@` is *the* small-business requirement and the current answer is to share a password. Full RFC 4314 ACL machinery, folder containers, public folders and a master user already exist — this is namespace plumbing plus cross-account ACL lookups. |
| ⬜ | **App passwords** | 2FA is unenforceable against IMAP/POP clients that cannot present a TOTP code. App passwords are what make account-level 2FA deployable, and the prerequisite for extending TOTP beyond the Control Panel. |
| ⬜ | **Message trace / delivery history** | No queryable per-message record — "what happened to Jane's 14:20 message" means grepping logs. `AWStats.cpp` is already a per-recipient delivery event stream called from every interesting site; it needs a correlation key and a home. Carry two ids like Exchange does: the RFC `Message-ID` and a per-*instance* id that survives forking. Off by default — it stores subjects and addresses. |
| ⬜ | **Full-text search** | No index at all: `BODY`/`TEXT` load each message and substring-scan, and attachment text is never searched. Also a cheap CPU-exhaustion vector for an authenticated user. Four-backend portability rules out native FTS; the route is a portable posting table on the existing `MessageIndexer` worker. The largest single quality gap and the largest single piece of work. |
| ⬜ | **RCPT-time over-quota rejection** | Quotas are checked during delivery, so an over-quota recipient produces a DSN — backscatter, often to a forged sender. Also missing: warning thresholds and notifications. |
| ⬜ | **`List-*` headers and one-click unsubscribe** | Distribution lists emit neither. Those header names appear in the source *only* in the DKIM oversigning list. Gmail and Yahoo bulk-sender rules require RFC 8058 one-click. Small: the headers plus an endpoint on the existing `WebServicesServer`. |
| ⬜ | **HTTP filter hook** | No way to put an external engine — above all rspamd — in the path. An HTTP/JSON hook modelled on Stalwart's MTA Hooks is far more idiomatic here than milter: HTTP listeners and clients already exist and no binary protocol is needed. |
| ⬜ | **Admin-reviewable quarantine** | Spam is scored, marked or deleted; nothing is held for review and release. |
| ⬜ | **End-user self-service portal** | No web surface for users at all. Password self-service alone is a permanent support burden. |
| ⬜ | **Retention and per-domain archiving** | Archiving is a raw filesystem copy — no retention, no per-domain scope, no index, no immutability, no hold. Searchable eDiscovery depends on full-text search. |
| ⬜ | **THREAD (RFC 5256)** | SORT is implemented from the same RFC but not THREAD, which is what drives conversation view in Thunderbird — and in our own webmail, where a threaded message list is table stakes rather than a nicety. |
| ⬜ | **LDAP/Active Directory as a directory backend** | AD-domain linking on accounts exists, but no directory as an account source. On Windows this is the case that matters. |
| ✅ | **Publish the architecture guide** | [ARCHITECTURE.md](ARCHITECTURE.md) — the module map, the patterns that matter (the COM seam, layered persistence, four backends behind one abstraction, INI-vs-database settings, and why the optional listeners deliberately sit outside Boost.Asio *and therefore have no exception barrier and their own `SSL_CTX`*), a "where does this change go" table, and the constraints that cost real releases: every wait on a pooled thread needs a ceiling; an idle timeout is not a ceiling because it re-arms on every byte; distinguish "the answer is no" from "there was no answer"; `shared_from_this()` throws in a constructor; a diagnostic must not fire on the shipped default. |
| 🔄 | **Static-analysis backlog** | First `/analyze` pass done — three buffer overruns, a data race, NULL dereferences and a log-corrupting shadowed variable fixed. The remainder needs triage rather than blanket suppression. |
| 🔄 | **Virus-scanner timeout policy** | A scanner killed for exceeding its bound currently fails open, consistent with a scanner that refuses a connection. That is a defensible posture but it should be a documented, configurable choice rather than an inherited default. |
| ⏸️ | **Backup scheduling in the GUI** | Frequently wanted. Needs a real scheduler with persistence and failure handling — more than a point release should absorb, which is exactly why it keeps being deferred. |
| ⏸️ | **IMAP FETCH byte fidelity** | `FETCH` reconstructs parts rather than returning original octets. Semantically correct, no client known to be affected; a rework would be a large change to a heavily used path, so it waits for a real reported problem. |

Future-proofing: standards and protocols
----------------------------------------

| | Item | Detail |
|:-:|---|---|
| ✅ | **Post-quantum key exchange — shipped 12 Aug 2026** | `SslContextInitializer` calls `SSL_CTX_set1_curves_list(ssl, "secp384r1:x25519:secp256r1")` unconditionally at context init. That **replaces** OpenSSL's default group list, which in 3.5+/4.x already contains `X25519MLKEM768`. So every SMTP, IMAP, POP3 and ManageSieve listener negotiated classical-only key exchange despite linking a PQC-capable library. Fixed: the list is now the setting `TlsKeyExchangeGroups`, defaulting to `X25519MLKEM768:SecP256r1MLKEM768:X25519:secp384r1:secp256r1`, with a fallback to the classical list if OpenSSL rejects the configured value — getting *that* wrong was the one way this change could have taken TLS down everywhere, so it is the part the regression tests cover hardest. **Scope note, updated 12 August 2026:** this now reaches every TLS listener in the server — SMTP, POP3, IMAP, ManageSieve, the REST API, the Web Services HTTPS listener and outbound delivery — because the two that built their own `SSL_CTX` were moved onto `SslContextInitializer`; see [Structural prerequisites](#structural-prerequisites). The metrics listener is not in the list because it serves plain HTTP and has no TLS at all. |
| ⬜ | **DMARCbis: replace PSL lookups with the tree walk** | DMARCbis changes organisational-domain discovery from the Public Suffix List to a DNS tree walk, and adds a 2.0 report namespace. A PSL-based evaluator produces *wrong policy decisions*, not soft failures, on domains relying on tree-walk semantics. No hard date, but receivers publish tree-walk-shaped records through 2026-27. The highest-value pure-protocol item here. |
| ⬜ | **ACME renewal robustness and ARI** | Certificate lifetimes fall to 100 days (Mar 2027) and 47 days with 10-day validation reuse (Mar 2029); Let's Encrypt defaults to 64 days from Feb 2027. Renewal and reload must be unattended and bulletproof, and ARI (renewal-info) becomes worth implementing. Treat 2027 as the real deadline. |
| ⬜ | **Legacy algorithm audit (RFC 9905)** | Audit for SHA-1, RSA-1024 and other deprecated primitives. No external deadline. TLS 1.2 has no sunset date and is not going anywhere soon — do not pre-emptively drop it. |
| ⬜ | **SPF void lookup limit** | Cheap correctness item, already biting in the field. |
| ⬜ | **Certificate expiry and queue-age metrics** | Both are things mail operators actually alert on, and the data is already there. Queue *depth* alone does not distinguish a burst from a stuck relay. |
| ⏸️ | **DKIM2** | Real momentum and the right backers, but the working group has already slipped its milestone by a year and there is no publication date. Track; hedge only by keeping the signing path abstracted. |
| ⏸️ | **PQC signatures (ML-DSA) and PQC for DNSSEC/DANE** | 2028 at the earliest, and they arrive through OpenSSL and the ACME client rather than as code written here. No allocated DNSSEC algorithm and an unsolved packet-size problem. Track only. |

Future-proofing: platform and supply chain
------------------------------------------

| | Item | Detail |
|:-:|---|---|
| ✅ | **Migrate to .NET 10 LTS** | Done 12 Aug 2026, ahead of the 10 Nov 2026 deadline. Nine projects to `net10.0-windows`; SDK 10.0.303 pinned in `global.json`; bundled Desktop Runtime, the installer's `Microsoft.WindowsDesktop.App\10.*` probe, the runtime-fetch script, the screenshot script and the docs all moved together. Two real findings on the way: .NET 9 added the **WFO1000** WinForms analyzer, which (correctly) rejected four properties on `ucText` whose designer-serialization intent was undeclared — the designer had been emitting meaningless `Number = 0` lines for years; and `System.DirectoryServices` is now in the Windows Desktop shared framework, so that `PackageReference` was redundant and is gone. **`MinVersion` stays at 10.0.14393** — .NET 10 still supports Windows 10 1607 and Server 2012, which was worth checking rather than assuming. |
| ✅ | **.NET target policy, written down** | Audited every project in the tree, not just the shipping ones. **All six shipped artefacts and the Control Panel publish as `net10.0`** — verified from their `runtimeconfig.json`, so nothing reaching a user is on .NET 8. The rest is deliberate, and now recorded so it is a decision rather than drift: the **regression suite and its three companions stay on .NET Framework 4.8.1**, which has *no end date* (Component Lifecycle Policy — supported as long as its host Windows) and drives the server over COM interop, so moving it to .NET 10 would be a large, risky change to the release gate for no benefit. **Six projects still on .NET Framework 4.5 — support ended 13 January 2016 — were bumped to 4.8.1**; they are dormant upstream dev tools referenced by no script, and since the 4.5 targeting pack is absent from a modern VS install they could not build at all before. **Seven projects remain older still**: five on .NET Framework 3.5 SP1 (supported to 10 January 2029), and two — `MemoryTests` and `TestInvalidConnections` — that declare no `TargetFramework` at all and carry Visual Studio 2005 markers (`ProductVersion 8.0.50727`, `ToolsVersion 3.5`, `OldToolsVersion 2.0`). All seven are dormant, referenced by no solution the build touches, and buildable by nobody today. Modernising code nothing exercises is a decision to take deliberately rather than blind; retiring them is the more honest option and is worth a separate look. |
| ⬜ | **Decide the OpenSSL branch** | 4.0.x is not LTS and dies 14 May 2027. Decide by Q1 2027 whether to follow to the next LTS or move back to 3.5. |
| ⬜ | **Declare a supported Windows floor** | Server 2019 / Windows 10 21H2, effective with the first release after 12 Jan 2027. Zero code cost — Server 2019 is still `_WIN32_WINNT=0x0A00`, and that macro should *not* be raised. |
| 🔄 | **Artifact signing** | **Sigstore half done.** [`sign-release.yml`](.github/workflows/sign-release.yml) signs every release asset with cosign, keyless via OIDC -- deliberately keyless, because for a single-maintainer project a long-lived signing key stored in Actions is itself the thing most worth stealing. The signature is verified in the same job, so a malformed bundle cannot reach a user. **Authenticode is still open, and the two are complementary rather than alternatives:** SmartScreen and the UAC prompt care about Authenticode and not about Sigstore, so cosign gives verifiability to anyone who checks while Windows still shows an unknown-publisher warning. Azure Trusted Signing is the realistic route. |
| ✅ | **security.txt (RFC 9116)** | Served at `/.well-known/security.txt` by the web services listener, with a derived (never hardcoded) `Expires`, a `Policy` pointing at `SECURITY.md`, and a deliberate refusal to serve anything at all when no contact is configured — a placeholder address is worse than no file. Note it inherits the listener caveat above: the web services ports default to 0, and the server now says so at startup. |
| ✅ | **OpenSSF Scorecard** | [`scorecard.yml`](.github/workflows/scorecard.yml), weekly and on push, publishing SARIF to code scanning so supply-chain posture sits beside the CodeQL findings. Deliberately **not** published to the public OpenSSF badge API -- there is no reason to send repository telemetry to a third party to obtain a number we can read ourselves. Expect Branch-Protection and Code-Review to fail by design on a single-maintainer repository; the actionable checks are Pinned-Dependencies, Token-Permissions and Dangerous-Workflow. OSPS Baseline remains open. |
| ⏸️ | **SQL Server Compact** | Long dead upstream and the one real dependency liability. Migration targets are SQLite or LocalDB. Deferred because it is a data-migration project for existing installs, not a swap, and no date forces it. |
| ⏸️ | **Boost and C++ standard upgrades** | No forcing function. Move when there is a reason. |

Future-proofing: deployment and operations
------------------------------------------

| | Item | Detail |
|:-:|---|---|
| ✅ | **Document the silent-install contract** | Done — see [README](README.md#unattended-install). Two limitations are now written down rather than discovered: the administrator password cannot be set on the command line at all, and a silent *upgrade* therefore cannot supply it either. Originally: The Inno script *already* branches on `WizardSilent()` and forwards credentials to `DBSetupQuick`, and already checks both `Exec` success and the child exit code so a failed schema upgrade cannot masquerade as success. None of that is documented, so an admin automating a hundred installs has to read the Pascal. Publish the switches and exit codes. Cheapest high-value win on this list. |
| ⬜ | **Migration *into* the server** | The biggest genuine opportunity here and currently unexploited: with Microsoft turning off Basic auth and EWS, people are moving. There is no documented import path. Competitors have imapsync, `doveadm import`, PST and Maildir/mbox routes. |
| ✅ | **Configuration as code** | [`build/hmconfig.ps1`](build/hmconfig.ps1) -- `export`, `diff` and `apply` the whole configuration as reviewable JSON, over the COM API so it can never disagree with the rules the server itself enforces. Two design decisions worth knowing: **`apply` is a dry run without `-Force`, and deletions additionally require `-AllowDelete`**, because deleting an account deletes its mail and a stale checkout, a partial export or a mistyped domain must not be able to do that -- additive and update operations are the default precisely because they cannot lose mail. And **passwords are never exported**: the server stores hashes, a hash is not a password, and a config file that silently reset every mailbox password on apply would be far worse than one that cannot set them at all. New accounts get a random password that must be reset. |
| ⬜ | **Prometheus naming fixes** | The bulk is idiomatic — `hmailserver_` prefix, `_total` on counters, `_seconds` base units, protocol as a label. Five cheap deviations: `hmailserver_state` is a numeric enum where OpenMetrics defines StateSet; `uptime_seconds` should be `start_time_seconds` as a Unix timestamp; no `build_info`; the two latency metrics are summaries with no quantiles, so only a mean is computable — histograms with buckets would give p95/p99; `database_up` collides conceptually with Prometheus's synthetic `up`. |
| ⬜ | **Per-domain and per-account metric labels** | Every counter is global, which is what stops metrics becoming reporting. |
| ✅ | **Ship Grafana dashboard JSON in the repo** | [`hmailserver/docs/grafana-dashboard.json`](hmailserver/docs/grafana-dashboard.json) — 16 panels, built against the metric names the exporter actually publishes rather than the ones it should. The panel descriptions state where the exporter is weak, so the dashboard does not quietly imply a tail-latency signal that is not there. |
| ⬜ | **GDPR-shaped features** | Not because the project is a controller or processor — it is neither — but because operators are. Per-account export, erasure that reaches the message store *and* the logs, and log retention limits are the concrete asks. |
| ⬜ | **Warm-standby topology, documented and tested** | State already lives in shared SQL, so this is largely achievable today and merely unwritten. Include the reasons *not* to put the message store on SMB/CSV. |
| ⬜ | **Backup verification** | The expectation has hardened from 3-2-1 to 3-2-1-1-0 — the trailing zero being "verified restores". Backup and restore exist; verification does not. |

Not planned
-----------

Saying no is part of a roadmap, and these are reasoned rather than reflexive.

| Item | Why not |
|---|---|
| **A rewrite**, in any language | This is upstream hMailServer with a current toolchain and a set of additions — 936 of 980 shared server source files are still byte-identical. That is the point of it. |
| **Removing the COM API** | It is how the Control Panel and every third-party script talk to the server. |
| **Matching upstream's dependency downgrades** | Deliberately ahead on OpenSSL, Boost and PostgreSQL. |
| **32-bit builds** | 64-bit only. |
| **Active/active clustering** | The ground moved here: Dovecot 2.4 **removed** Director and the replicator outright and now documents CE as single-server, with HA moved to the commercial product. Multi-node is no longer part of the open-source baseline, so its absence is not a gap. Warm standby is the right deliverable. |
| **Windows containers** | Base image sizes, the GUI tooling problem and thin adoption make this a poor fit, and no competitor does it on Windows. |
| **ActiveSync and EWS** | Both proprietary; EAS is patent-encumbered; EWS has had no feature work since 2018 and Microsoft has published its retirement — phased disablement from October 2026, fully retired April 2027. |
| **IMAP NOTIFY (RFC 5465)** | Dovecot *does* implement it, so this is a real gap rather than an imagined one — but a low-value one, because what people want it for does not work anyway. iOS Mail does no push for generic IMAP at all ("If Push isn't available as a setting, your account will default to Fetch"), and `XAPPLEPUSHSERVICE` needs an APNs certificate gated on owning discontinued macOS Server. IDLE is the portable answer and NOTIFY would not change what any mobile client does. |
| **BIMI / VMC** | Never became an RFC after seven years. A sender-brand feature rendered by webmail UIs; the server's role is header pass-through, which already works. |
| **REQUIRETLS (RFC 8689)** | Near-zero deployment, and it converts delivery failures into hard bounces. The `TLS-Required: No` header half is the only useful part. |
| **LMTP (RFC 2033)** | Only matters when another MTA fronts this one, which is not a realistic Windows deployment. |
| **Multi-tenancy** | The per-domain model already covers the realistic cases. |
| **Embedded browser UI** | The WebView2 SDK is under a proprietary EULA that prohibits distribution "in ways that would subject it to copyleft licensing" — incompatible with AGPLv3. The runtime shipping in Windows does not help; the redistributed SDK assemblies are the problem. |
| **"Quantum-safe email" as a claim** | With no PQ signature story for DKIM and no PQ DNSSEC, only the transport hop can be made quantum-safe. Claiming more than "PQ hybrid key exchange on all TLS listeners" would be dishonest. |

Relationship with upstream
--------------------------

The original project is largely dormant but not dead, and real fixes do still
land there. A scheduled job compares this fork against upstream monthly and opens
a tracking issue when anything new appears; the baseline it compares from is in
[`.github/upstream-sync`](.github/upstream-sync), along with the reasoning for
the two upstream changes this fork deliberately does not take.

As of the last review, nothing upstream is missing here.

Influencing this list
---------------------

A concrete report with a log beats a feature request. Bugs and enhancements go to
[Issues](https://github.com/Progressiverobot/hmailserver/issues), open-ended
questions to
[Discussions](https://github.com/Progressiverobot/hmailserver/discussions), and
anything security-sensitive privately via [SECURITY.md](.github/SECURITY.md).

Something being on this list does not mean it is being worked on now, and
something not being on it does not mean it will be refused — it usually means
nobody has asked.
