Roadmap
=======

What is planned for this fork, what is deliberately not, and why. It is kept
honest rather than aspirational: work appears here when there is a concrete
reason for it, and anything already shipped moves to the
[Releases page](https://github.com/Progressiverobot/hmailserver/releases).

No dates are promised. This is a maintained fork, not a funded product, and the
order below is the order things will be looked at rather than a schedule.

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

Worth stating plainly, because the rest of this document is a list of gaps and
that gives a misleading impression on its own. Measured against the mainstream
self-hosted alternatives — Postfix + Dovecot, iRedMail, Mail-in-a-Box, Stalwart —
this fork is **ahead of the field on transport security, authentication and
observability**, and behind on mailbox features and the administrative surface.

Specifically ahead:

* **MTA-STS is implemented in-process.** Postfix still does not implement RFC 8461
  natively; it needs an external resolver feeding `smtp_tls_policy_maps`.
* **DANE with in-process DNSSEC validation**, TLS-RPT, ACME, SRS, BATV, Ed25519
  DKIM, and **ARC sealing** — Stalwart's own comparison page describes its ARC
  support as inbound verification only.
* **IMAP4rev2 (RFC 9051) advertised by default.** In Dovecot this is still a
  compile-time experimental option that is off by default.
* **SCRAM-SHA-256-PLUS with channel binding**, Argon2id, and OAuth2/OIDC bearer.
* **Prometheus, OpenTelemetry, JSON logs and health probes in the open tier.**
  Stalwart gates metric alerts, live telemetry, the real-time dashboard and
  historical retention behind its Enterprise edition.
* **Four SQL backends behind one portable abstraction**, plus the COM API and
  event scripting, which nothing else in this class offers.

The gaps below are almost all *above* the protocol layer. That is the honest
shape of the thing.

Recently shipped
----------------

The 2026 work through 6.2.18 was overwhelmingly about bounding waits and making
the server say what it is doing. Every wait that could block a shared thread pool
now has a ceiling: `FinalizationTimeout`, `ClientSessionCeiling`,
`DNSQueryTimeout`, `ScriptTimeout`, `ExternalProcessTimeout`,
`DBConnectionAcquireTimeout`, `AsyncQueueStallThreshold`. Acceptance is timed per
stage, the work queue reports saturation and names the task holding each thread,
and a recipient lookup that fails because the database did not answer now
returns `451` rather than `550`, so a database locked by a backup defers mail
instead of bouncing it.

The troubleshooting guide that work was written for is
[DiagnosingStalledMail.md](hmailserver/docs/DiagnosingStalledMail.md).

Near term
---------

**Decide the virus-scanner timeout policy explicitly.** A scanner that is killed
for exceeding its bound currently fails open — the message is delivered unscanned,
which is what already happens when the scanner refuses a connection. That is
consistent, but it is a security posture that should be a deliberate, documented,
configurable choice rather than an inherited default.

**Finish the static-analysis backlog.** The first `/analyze` pass found a buffer
overrun in three path helpers, a data race on the virus-scanner counter, ignored
`ReadFile` results, several unchecked NULL dereferences and a shadowed variable
that silently corrupted log rotation. Those are fixed. The remainder needs triage
rather than blanket suppression.

**Publish the architecture guide.** There is a detailed map of the codebase, but
it is kept local and unpublished, so anyone arriving at the repository has
`CONTRIBUTING.md` and nothing else to orient them. A fork that invites
contributions should publish the map.

**Remaining documentation gaps**: an upgrade/migration note covering the database
upgrade chain, and an index for `hmailserver/docs/`.

Security
--------

Listed before features because of the priority order above, and ordered by
how cheap the fix is relative to what it prevents.

**API keys for the REST API.** `RestApiServer.cpp:407-415` accepts **HTTP Basic
only** — the administrator password, replayed on every request, with no tokens,
no scoping, no expiry and no IP restriction. Bearer tokens or API keys with
labels, expiry and an address restriction are a small change and should land
before the API surface grows. This is the cheapest security win available.

**Per-account outbound sending limits.** `RateLimiter` is a sliding window keyed
by IP or destination domain, per minute only. There is no per-*account* message
quota, no daily cap, no recipient-count cap. A single compromised account is the
most common route to a blacklisted IP, and the blast radius is currently
unbounded. iRedMail ships this in its free edition. Treat it as a security
control, not a feature.

**App passwords.** TOTP exists, but only for the Control Panel logon. The
structural problem is that per-account 2FA and IMAP/SMTP are incompatible without
app passwords — an IMAP client cannot present a TOTP code, so account-level 2FA
only works for clients that speak OAUTHBEARER or XOAUTH2. App passwords are the
mechanism that makes it deployable at all, and they are the prerequisite for
per-account TOTP later. Dovecot has no built-in equivalent; every control panel
that offers it built it themselves.

**Over-quota rejection at RCPT TO.** `LocalDelivery::CheckAccountQuotas_` runs
during delivery, so an over-quota recipient produces a DSN rather than an
SMTP-time rejection — which means generating backscatter, often to a forged
sender. Dovecot's `quota-status` service exists precisely to let Postfix reject
at RCPT. Also missing: quota warning thresholds and notifications.

Operability
-----------

**Message trace.** The logging is much better than it was, but there is still no
*queryable* per-message record: answering "what happened to the message Jane sent
at 14:20" means grepping logs. This is the single feature that would have turned
#18 from a three-release investigation into a ten-minute one, and it is the
natural continuation of the per-stage timing work.

The reference implementation is Exchange's Message Trace, and its design is worth
copying closely — including the part most people get wrong. It carries **two**
identifiers: the RFC `Message-ID:`, constant for the life of the message, and a
per-*instance* id that survives bifurcation and distribution-list expansion. You
need both, because one inbound message becomes many delivery attempts.

The shape here would be an append-only `hm_message_events` table — time, trace
id, RFC message id, direction, event type (receive / deliver / defer / fail /
expand / reject), sender, recipient, remote IP, first 256 characters of subject,
size, SMTP code, response text, duration. Notes on doing it properly:

* `AWStats.cpp` is **already** a per-recipient delivery event stream, already
  called from every interesting site — the SMTP rejection paths, `LocalDelivery`,
  `ExternalDelivery` and `SMTPDeliverer`. It is missing only a correlation key
  and a home. Those are the right seams.
* `session_id_` is process-lifetime monotonic and therefore **collides across
  restarts**; pair it with the server start epoch before using it as a key.
* Retention must be a first-class setting alongside `LogDeleteDays`, and the
  feature should be **off by default**: it stores subjects and addresses, which
  has privacy consequences that belong in front of the administrator, not in a
  release note.

A useful first step needs no server change at all: the Control Panel can parse
the AWStats journal client-side, the way `LogsView` already tails the main log.
That gets a searchable, filterable event table immediately. Its ceiling is
correlation and subject, which is the argument for the table afterwards.

**Metric history.** There is no history anywhere. `/metrics` is a stateless
scrape, and the Control Panel dashboard keeps 90 samples at a 2-second poll —
three minutes, in RAM, discarded on navigating away. Every graph ambition runs
into this before it runs into anything else. The fix is a periodic metrics-sample
table with a retention setting, which is exactly what Stalwart does — and
charges for. Once it exists, 24-hour, 7-day and 30-day views follow, and "is this
normal for a Tuesday" becomes answerable.

**Counters that exist but are not exposed.** `ServerStatus` already tracks
delivered/deferred/bounced, authentication success and failure, TLS handshakes
and failures, command latency and database query latency. `WorkQueue` already has
`GetQueueDepth()`, `GetWaitingBlockingTaskCount()` and `GetRunningTasks()`,
returning task name, thread, queue wait and running time. **None of it reaches
COM**, where `IStatus` exposes seven members. One additional COM property would
surface all of it in a single round trip. This is the smallest server change with
the largest payoff on this page.

**Missing counters.** SPF, DKIM, DMARC, ARC and DANE are all implemented and none
of them are counted. Stalwart has a metric subsystem per mechanism. Per-domain
and per-account labels are also absent — every counter is global, which is what
stops metrics becoming reporting.

**Latency percentiles.** `hmailserver_command_processing_seconds` and
`hmailserver_db_query_seconds` are Prometheus *summaries* carrying only `_sum`
and `_count`, so only the mean is available. p95 and p99 need histogram buckets
that do not exist.

Mailbox features
----------------

These are the gaps that cost actual users, in rough value-per-effort order.

**Shared and delegated mailboxes.** `IMAPCommandNamespace.cpp:37` hard-codes the
other-users namespace to `NIL`. Sharing exists only through the single `#Public`
namespace: there is no way for one user to open another's mailbox, and no
Send-As authorisation. `info@`, `sales@` and `support@` handled by three people is
*the* small-business mail requirement, and the current answer is to share a
password, which defeats per-user authentication and 2FA entirely.

This is the best value-per-effort feature on the list, because most of it is
already built — full RFC 4314 ACL machinery (`ACLPermission.h` implements
`l r s w i p k x t e a`, advertised as `RIGHTS=texk`), a folder container
abstraction, public folders, and an IMAP master user. The work is namespace
plumbing and cross-account ACL lookups in LIST and SELECT.

**Sieve is a token subset, and ManageSieve advertises it.** `SieveEvaluator.cpp`
implements the actions `keep`, `discard`, `fileinto`, `redirect`, `stop` and the
tests `true`, `false`, `not`, `allof`, `anyof`, `header`, `address`, `exists`,
`size`. `ManageSieveServer.cpp:306` advertises literally `"SIEVE" "fileinto"`.

That last line matters more than it looks: every ManageSieve client — above all
Roundcube's `managesieve` plugin, which is how most self-hosters expose filters —
reads it and renders an almost-empty UI. And note the irony: **vacation
auto-reply already exists** as a native account feature, complete with expiry
dates and a spam-flag guard. It simply is not reachable through Sieve, so no
standard client can see or set it.

Worth adding, in value order: `vacation` (RFC 5230), `imap4flags` (5232),
`envelope` and `body` (5173), `variables` (5229), `relational` (5231),
`subaddress` (5233), `copy` (3894), `mailbox` (5490), `reject`/`ereject` (5429),
`duplicate` (7352). The lexer, parser and evaluator structure is already there;
this is filling in rather than architecting, and `vacation` is largely a mapping
onto machinery that exists.

**Full-text search.** There is no index. `IMAPCommandSearch.cpp` resolves `BODY`
and `TEXT` by loading each message and substring-scanning it; `hm_message_metadata`
indexes date, from, subject, to and cc, so header search and SORT are indexed but
**body search is a linear scan of the whole mailbox**, and attachment text is
never searched. It is also a cheap CPU-exhaustion vector for an authenticated
user.

This is the largest single quality gap and the largest single piece of work. The
complication specific to this fork is the four-backend portability rule: native
FTS fragments four ways across PostgreSQL `tsvector`, MSSQL FTS and MySQL
`FULLTEXT`. The realistic route is a portable posting-list table populated by the
existing `MessageIndexer` worker thread, which already exists and already runs
asynchronously. Attachment text extraction is a later phase.

**Bulk-sender compliance.** Distribution lists emit no RFC 2369 `List-*` headers
and no RFC 8058 one-click unsubscribe. Those header names appear in the source
**only** in the DKIM oversigning list. Gmail and Yahoo bulk-sender rules now
require one-click unsubscribe. The headers plus an endpoint on the existing
`WebServicesServer` is a small change with disproportionate value. List
moderation and self-subscribe are larger and separate.

**Admin-reviewable quarantine.** Spam is scored, marked or deleted; nothing is
held for review and release. iRedMail quarantines to SQL with self-service
release; Exchange has per-user release. Mail-in-a-Box deliberately does not, so
this is not universal — but it is expected wherever an administrator is
accountable for false positives.

**End-user self-service portal.** There is no web surface for users at all.
Password self-service alone is a permanent support burden.

**Retention and archiving.** Archiving is a raw filesystem copy into
`{ArchiveDir}\{senderDomain}\{senderUser}\` trees. No retention, no per-domain
scope, no index, no search, no immutability, no legal hold. Retention and
per-domain scope are tractable; searchable eDiscovery depends on full-text search.

**External filter integration.** VBScript/JScript event hooks and the rules engine
cover a lot in-process, but there is no way to put an *external* engine in the
path — above all rspamd, which has effectively replaced the
amavisd + SpamAssassin + opendkim + opendmarc stack across the ecosystem. Of the
two mechanisms, an **HTTP filter hook** modelled on Stalwart's MTA Hooks is much
more idiomatic here than milter: this server already runs HTTP listeners and an
HTTP client, and it avoids implementing a binary protocol.

**Smaller items**, each small on its own: an Apple `.mobileconfig` profile to sit
alongside the existing Thunderbird autoconfig and Outlook Autodiscover; DKIM
dual-selector rotation (`Domain.h` holds a single selector, and rotation is
standard practice); `/.well-known/caldav` and `/.well-known/carddav` redirects so
a paired calendar server is discoverable.

**IMAP extensions** clients actually use, in value order: **THREAD** (RFC 5256 —
SORT is implemented from the same RFC but not THREAD, and it is what drives
conversation view in Roundcube and Thunderbird); **LIST-STATUS** (5819, a visible
startup-latency win, and expected alongside IMAP4rev2 — the rev2 advertisement is
worth auditing for completeness); **COMPRESS=DEFLATE** (4978); **MULTIAPPEND**
(3502); **METADATA** (5464, a prerequisite for some Sieve extensions); then
BINARY, SAVEDATE, PREVIEW, OBJECTID and RFC 9208 QUOTA. `SEARCH=FUZZY` is absent
from Dovecot too and is not a gap.

The Control Panel: graphs and visual data
-----------------------------------------

The dashboard currently answers "is the process alive", which the tray icon
already answered. Making it answer real questions is mostly not a charting
problem — see *Metric history* above, which is the actual blocker — but there is
a useful amount to do before that lands, and some defects to fix first.

### Defects in the current dashboard

These are bugs, not enhancements, and they should be fixed before the charting
surface is multiplied:

* **The sessions chart is unreadable to a colour-blind administrator.** No
  `LegendPosition` is set, and LiveCharts defaults to `Hidden`, so the chart
  renders three unlabelled lines whose only distinguishing feature is colour —
  and the dark-theme `Success #3FB950` and `Warning #D29922` separate by only
  ΔE 5.1 under protanopia, against a target of 8. Light mode is marginal. The fix
  is a legend plus a **chart series palette separate from the status tokens**:
  status colours belong on badges, where they are always paired with an icon and
  a label, and they are the wrong basis for series identity.
* **Charts do not follow the theme.** `DashboardView.xaml.cs` bakes
  `static readonly SKColor` values and never re-reads them, so the charts are the
  one part of the application that ignores the theme toggle.
* **`LineSmoothness = 0.8` on monitoring data.** Spline interpolation invents
  values between samples and rounds off spikes — precisely the events being
  watched for. Monitoring series should be `0`.
* **`AnimationsSpeed = 400ms` against a 2-second poll** means the chart is
  animating 20% of the time. Mail servers are administered over RDP, where WPF
  historically falls back to software rasterisation; this is pure waste there.
  (.NET 8 does add an opt-in
  `Switch.System.Windows.Media.EnableHardwareAccelerationInRdp`, but assume
  software rendering anyway.)
* **The X axis is hidden**, so the reader cannot tell whether the window is three
  minutes or three hours. It is three minutes.
* Chart cards use a translucent Fluent fill over a Mica backdrop, so series land
  on a non-deterministic composited surface and no contrast guarantee holds. Give
  chart cards an opaque background.

### The charting library question is already settled

`LiveChartsCore.SkiaSharpView.WPF` 2.0.5 is already a `PackageReference`, it is
MIT, it reached 2.0 stable in March 2026 and is the most actively maintained of
the candidates, and its `ObservableCollection` binding model is the right shape
for a polling dashboard. ScottPlot is a reasonable alternative; OxyPlot's last
release was September 2024 and is best avoided on cadence grounds.

**WebView2 plus a JavaScript charting library is ruled out on licensing.** The
WebView2 SDK is under a proprietary Microsoft EULA which prohibits distributing
the code in ways that would subject it to copyleft licensing — which is exactly
what shipping the assemblies inside an AGPLv3 application does. The runtime being
bundled with Windows does not rescue it; the redistributed SDK assemblies are the
problem. Note also that the Windows Community Toolkit has no chart control at
all — its only data-visualisation control is a radial gauge, and it targets
WinUI rather than WPF.

### What to build, and in what order

**Without any server change**, from data the COM API already returns:

* **Queue analytics.** `QueueView` already parses `Created`, `Recipients` and
  `Tries`. Three groupings give a queue-*age* histogram, a retry-count
  distribution and the top stuck recipient domains — which together answer "what
  is queued and why", the question the current dashboard cannot touch. This is
  the highest-value chart set available without writing any C++.
* **Storage and quota.** `Domain.Size`/`MaxSize` and `Account.Size`/`MaxSize` are
  all on COM today. Top accounts by size, and domains near quota — as **meters,
  not gauges**.
* **Stat tiles with sparklines** replacing the bare KPI numbers. When the number
  is the point, a `label · value · delta · sparkline` tile beats a chart and
  costs a fraction of the space.
* **Log-derived message search** over the AWStats journal, per *Message trace*
  above.

**Then**, in order: widen the COM pipe to expose the `ServerStatus` and
`WorkQueue` counters; add the metrics history table; build message trace; and
finally the authentication and reputation panel, whose shape should follow Google
Postmaster Tools — SPF/DKIM/DMARC pass rates against the ~95% benchmark, TLS
delivery percentage, and bounces broken down by reason.

### Design rules

* A dashboard answers a question. If a panel does not correspond to a question an
  administrator actually asks, it is decoration.
* **No dual-axis charts.** Two y-scales manufacture a correlation that is not in
  the data.
* **Every chart needs a table twin.** That is also the High Contrast answer;
  `ThemeTokens` already has a High Contrast palette that the charts ignore.
* Stack delivery *outcomes*, which genuinely sum to a total. Do not stack
  per-domain volumes.
* One filter row scoping the whole page, never per-chart ranges. Sensible
  defaults: 1 hour live, 24 hours everyday, then 7/30/90 days.
* Solid hairline gridlines one step off the surface, never dashed; no value label
  on every point.

### Deliberately not built

Geographic maps of connecting IPs — screenshot gold, actionable never; a ranked
table with a country column wins on every real task. Radial gauges and
speedometers, which spend a large card on one number (the line in
`CONTROL-PANEL-PLAN.md` sanctioning LiveCharts gauge series should be removed).
Pie charts of protocol mix. Sankey mail-flow diagrams. Animated KPI counters —
an administrator watching a number tick up learns nothing the final value did not
tell them.

Protocols and interoperability
------------------------------

**OAuth 2.0 as a *client*, for Microsoft 365 and Gmail.** This is the most
time-sensitive item in this document. The server supports OAuth2/OIDC for
*inbound* authentication, but the external account fetcher and the outbound
relayer can only present a username and password. Basic authentication for
IMAP and POP against Exchange Online has been off since 2022, and Microsoft's
published schedule turns off Basic for SMTP AUTH by default at the end of
December 2026. Anyone relaying outbound through `smtp.office365.com` or
collecting from a Microsoft 365 mailbox will stop working.

The work is a token cache plus the XOAUTH2 SASL encoding, which is a short piece
of code. One point of detail that matters: **Exchange Online implements XOAUTH2
only** — the non-standard Microsoft/Google mechanism — and not RFC 7628
OAUTHBEARER, so supporting the standard alone is not sufficient.

**Server-side OAuth2 live validation.** Token validation is currently offline
against a statically configured key. Live JWKS fetching with rotation, and token
introspection, are the missing halves. Interop verification against Microsoft 365
and Gmail XOAUTH2, and Thunderbird SCRAM, is blocked on access to those services
rather than on the code.

**Calendaring, if it is ever done, means iMIP and nothing else.** The research
here produced a clear answer: **iMIP over SMTP (RFC 6047, carrying RFC 5546 iTIP
and RFC 5545 iCalendar) is the only standards-based bidirectional interoperability
path with Exchange that exists.** Free/busy, sharing, delegation and sync in
Microsoft 365 are all proprietary and unreachable — Exchange asks EWS and nothing
else, will not consume a published VFREEBUSY, and does not support CalDAV in
either direction. Consuming a published `.ics` URL over HTTP is the one other
standards-based bridge, and it is cheap.

If iMIP is implemented, the constraints that will bite are documented in
MS-OXCICAL: the `method` MIME parameter must match the `METHOD` property; a
REQUEST, REPLY or CANCEL must contain **exactly one** event, so recurrence
overrides go one per message; REPLY and COUNTER must carry exactly one attendee;
and `ADD`, `REFRESH` and `DECLINECOUNTER` have no Exchange mapping at all, so a
full REQUEST with an incremented SEQUENCE is the portable substitute. And a
safety requirement, not an optional one: verify that the envelope sender matches
the `ORGANIZER` on REQUEST and CANCEL and the `ATTENDEE` on REPLY, or the server
will accept forged cancellations.

**Ideas worth stealing from Exchange**, none of them requiring a Microsoft
protocol:

* **Moderated transport on distribution lists** — hold in an arbitration store,
  send an approval request carrying the original as an attachment, three outcomes
  (approve / reject with comment / expire), a bypass list, and a nested-moderation
  flag. This is the most involved of these, and the most requested.
* **Sender restrictions on lists** — accept-only-from and reject-from lists that
  accept groups as well as individuals, and require-authentication as a default.
  Some of this already exists on distribution lists and is worth completing.
* **Two-stage quota enforcement** — separate prohibit-send and
  prohibit-send-receive thresholds, plus a warning threshold, rather than one
  cliff. Note Exchange's own rule that the warning is suppressed unless it is at
  least half the prohibit-send value.
* **`RemovePrivateProperty`-style hazards to avoid**: Exchange's resource
  mailboxes strip subject, body, attachments and the private flag by default.
  Worth knowing as a design anti-pattern if resource mailboxes are ever built.

**LDAP/Active Directory as a directory backend.** There is AD-domain linking on
accounts and an `ActiveDirectoryService` in the Control Panel, but no LDAP
account source. On Windows the AD case is the one that matters, and iRedMail
added exactly this as a headline paid feature — the demand is real.

Testing and CI
--------------

* **Abnormal-input coverage.** Aborted and malformed sessions are the class that
  produced the worst bugs of the year. SMTP is now covered; IMAP and POP3 need
  the same treatment.
* **Static analysis in CI.** Currently a manual pass. It should run automatically
  so new findings are caught where they are introduced. Note that it must not run
  concurrently with the regression suite — build events stop the service, and
  doing both at once has already cost one wasted run.
* **clang-tidy, ASAN and UBSAN**, and libFuzzer over the protocol parsers. All
  need a clang toolchain alongside MSVC, which is the gating work.
* **Broader database coverage.** The suite runs against one backend. MySQL,
  PostgreSQL and MS SQL are supported and should be exercised, at least
  periodically.
* **Installer verification on more Windows versions.** The smoke test installs
  each release on a clean machine and checks the service comes up; it runs on one
  image today.
* **An AEAD-only cipher default**, once the compatibility cost is measured.

Relationship with upstream
--------------------------

The original project is largely dormant but not dead, and real fixes do still
land there. A scheduled job compares this fork against upstream monthly and opens
a tracking issue when anything new appears; the baseline it compares from is in
[`.github/upstream-sync`](.github/upstream-sync), along with the reasoning for
the two upstream changes this fork deliberately does not take.

As of the last review, nothing upstream is missing here.

Not planned
-----------

Saying no is part of a roadmap, and these are reasoned rather than reflexive.

* **A rewrite**, in any language. This is upstream hMailServer with a current
  toolchain and a set of additions — 936 of the 980 shared server source files
  are still byte-identical. That is the point of it.
* **Removing the COM API.** It is how the Control Panel and every third-party
  script talk to the server. It is not going anywhere.
* **Matching upstream's dependency downgrades.** This fork is deliberately ahead
  on OpenSSL, Boost and PostgreSQL.
* **32-bit builds.** 64-bit only.
* **JMAP (RFC 8620/8621).** It is the better protocol, and Stalwart has the
  complete stack. It is also still without mainstream client support —
  Thunderbird, Outlook, Apple Mail and Roundcube are all IMAP, and Dovecot has no
  JMAP at all. Implementing it means a new HTTP API surface, a JSON object model
  with state strings and `/changes` semantics, blob handling and push
  infrastructure: effectively a second server. Genuinely the future, genuinely
  not yet required. Revisit when a mainstream client ships it.
* **A CalDAV/CardDAV server.** Note how the distributions solve this: Mail-in-a-Box
  bolts on Nextcloud, iRedMail bundles SOGo. Nobody in this position writes their
  own, because it means a WebDAV stack, an iCalendar parser, recurrence expansion,
  a scheduling state machine and a second object store — and it is not a mail
  server feature. The `.well-known` redirects listed above plus a tested pairing
  recipe capture most of the benefit for a day's work.
* **Webmail.** Even Stalwart does not ship one; it recommends Roundcube. A tested
  Roundcube-on-Windows recipe is the right deliverable — and is a second argument
  for finishing Sieve first, since `managesieve` is the UI those users would see.
* **Active/active clustering.** The ground moved here, in this fork's favour:
  Dovecot 2.4 **removed** Director and the replication plugin outright, and the
  documentation now states that Dovecot CE is designed for a single server. HA
  moved to Dovecot Pro. Multi-node HA is no longer part of the open-source
  baseline at all. Since this server already keeps state in shared SQL, the useful
  deliverable is a documented and tested **warm-standby topology**, which is
  largely achievable today and merely unwritten. True active/active needs
  distributed locking on the message store and folder state, and is not worth it.
* **Exchange ActiveSync and EWS.** Both proprietary; EAS is patent-encumbered; EWS
  has had no feature work since 2018 and Microsoft has published its retirement —
  phased disablement from October 2026, fully retired April 2027. Building toward
  a protocol its owner is switching off is a poor use of effort.
* **IMAP NOTIFY (RFC 5465) and mobile push.** Dovecot does implement 5465 — it
  has since 2.2 — so this is a real gap rather than an imagined one, but it is a
  low-value one, because the thing people want it for does not work anyway. The
  state of mobile push is worse than most assume: **iOS Mail does not do push for
  generic IMAP at all**, only iCloud and ActiveSync, and Apple's own current
  documentation says so plainly ("If Push isn't available as a setting, your
  account will default to Fetch"). The single exception is the undocumented
  `XAPPLEPUSHSERVICE` command, which needs an APNs certificate obtainable only
  through a process gated on owning macOS Server — a discontinued product.
  Android's IMAP clients hold an IDLE connection open. **IDLE, which is already
  implemented, is the real and portable answer**, and NOTIFY would not change
  what any mobile client does.
* **LMTP (RFC 2033).** It only matters when another MTA fronts this one, which is
  not a realistic Windows deployment.
* **Multi-tenancy.** The per-domain model already covers the realistic cases.
* **Embedded browser UI**, for the licensing reason given above.

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
