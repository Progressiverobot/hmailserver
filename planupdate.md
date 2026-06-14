# hMailServer Modernization — Master Plan & Roadmap

*Generated 2026-06-13, reorganized 2026-06-14. This is the single authoritative
plan. It covers two tracks run as one ordered program:*

- **Track A — Control Panel becomes the sole admin GUI** (parity with the classic
  WinForms Administrator, drop the classic from the installer, AV/security
  extensibility, full UX/UI pass).
- **Track B — Server world-class hardening** (security defects, auth/secrets
  modernization, standards & deliverability, operability, CI/fuzzing).

*Scope: Tier 1 defects + Tier 2 harden-in-place now; Tier 3 platform expansion
(Linux/JMAP/HA) is a documented future track only. Every new server capability is
surfaced in the Control Panel.*

**How this document is organised:**

- **Part 1 — Remaining work**, in the order it will be done.
- **Part 2 — Completed work**, a record of everything already delivered.

---

# PART 1 — REMAINING WORK (planned order)

Risk/value-ordered. Each step ends with: clean build, run the regression suite,
(CP steps) screenshot-validate, then commit/push + move the release tag + clobber
the release asset.

## Execution order (what's left)

1. **Track A Phase 1 — item 7: Active Directory pickers** (deferred; needs a
   domain-joined runner) → then **Track A Phase 0** (drop the classic from the
   installer). CP becomes the sole shipped GUI.
2. **B2 — authentication modernization follow-ups** (live JWKS/introspection +
   O365/Gmail XOAUTH2 + Thunderbird SCRAM interop). *RS256 auto-test coverage and
   full RFC 4013 SASLprep are done (v6.2.2).*
3. **Track A Phase 2 — Control-Panel UX/UI polish.**
4. **Track A Phase 3 — AV/security extensibility + INI hardening knobs.**
5. **B7 — operability & observability** (OpenTelemetry, health probes, DB pool/
   executor, durability, HA runbook).
6. **B6 — Sieve + ManageSieve.**
7. **Track A Phase 4 — finalize** (gap doc, release cadence).
8. **B8 — quality gates remaining** (CI DB matrix, clang-tidy/ASAN/UBSAN,
   libFuzzer, SBOM/CVE scanning, signed artifacts).
9. **Cross-cutting** — surface every new server capability in the Control Panel.
10. **Future track (Tier 3)** — documented, not scheduled.

---

## Track A — finish the Control Panel as the sole GUI

### Phase 1 (remaining) — item 7: Active Directory pickers + Import members

⏸ **Deferred — needs a domain-joined runner.** Port `formActiveDirectoryAccounts`,
`formSelectUsers`, `formUserAccounts`, `formImportMembers`: browse/import AD
accounts into the Account Directory tab; import members for groups/dist-lists. The
dev/test machine is in a WORKGROUP and the `System.DirectoryServices`/
`AccountManagement` packages are not available offline, so the AD-query path cannot
be built or validated here. The Directory tab already supports manual AD linkage
(ADDomain/ADUsername); only the *browse* picker convenience is outstanding.

*Also intentionally left out of Phase 1:* the destructive IP-range bulk
`SetDefault` admin action (item 10) — deferred by design.

Once item 7 lands, **Phase 0** can drop the classic Administrator from the
installer.

### Phase 0 — Drop the classic from the installer

1. Remove the Administrator executable/DLLs from `section_files_64.iss` and
   `section_files_common.iss`.
2. Remove its Start-menu shortcut from `section_icons.iss`; replace the
   end-of-setup "run Administrator" with "run Control Panel" in
   `section_run.iss`; clean `section_uninstallrun.iss` / `section_components.iss`
   if referenced.
3. Keep DBSetup / DBUpdater / DataDirectorySynchronizer.
4. Verify ISCC builds and the post-install database step still runs.

### Phase 2 — UX/UI polish (after parity)

1. ✅ **Reload-on-enter** for cached pages (`FeatureSettingsView.OnEnter` now re-reads
   the INI on navigation, matching `ServerSettingsView`; every settings page refreshes).
2. ✅ **No silent `catch{}`** — `DomainsView` alias/dist-list loads now surface real
   empty/error states (centered placeholder over the list; the swallowed exception
   message is shown instead of an empty list).
3. ✅ **Search/filter** on long lists: Domains, Accounts, Rules, Queue, Ports,
   Certs, IP ranges, Routes (shared `ListSearch` reflection-based substring filter
   over each list's `ICollectionView`; per-page search box, re-applied on reload).
4. ✅ **Consistent destructive-action confirmations** — aliases, distribution
   lists, distribution-list recipients and incoming relays now prompt Yes/No
   before deleting (matching accounts, IP ranges, queue and public folders).
5. ✅ **Input validation + inline feedback** in Domain/Account/Route dialogs —
   non-empty numeric fields (sizes, limits, port, retries) are validated against a
   range via the shared `NumericField` helper and bad input is reported in an
   inline status line instead of being silently dropped on save.
6. ✅ **Standardized loading/empty/error states** (shared `StatusText` helper;
   centered empty/error placeholders on Domains aliases/lists and the Routes, IP
   ranges, SSL certificate and TCP/IP port grids; Queue/Rules keep their count
   subtitles).
7. ✅ **Accessibility** — `AutomationProperties.Name` on the icon-only controls
   (theme toggle, ✕ delete buttons), every page search box, the nav tree and the
   content host so screen readers announce them. (Wpf.Ui buttons render the `_`
   mnemonic literally, so access keys are left to the standard menu/dialog defaults;
   tab order follows the logical visual tree.) **Automation-quality pass (WPF Buddy
   MCP):** every navigation node (`nav-*`/`navgroup-*`) and every data-driven
   settings editor (`ServerSettingsView`/`FeatureSettingsView`, keyed by COM path /
   INI key) now carries a stable `AutomationId` — the MCP audit went from 20/F
   (12/58 actionable controls identified) to 100/A (58/58). The only residual
   "duplicate" is the WPF default `TreeViewItem` expander toggle (`Expander`, one
   per nav group), a framework template part left untouched to avoid retemplating.
8. ✅ **Theme** — follows the OS theme by default (and tracks live OS changes via
   `SystemThemeWatcher`) until the manual toggle sets an explicit Light/Dark
   preference, which then persists and stops OS tracking.
9. ✅ **Navigation restructure** — the overloaded 13-item **Advanced** group is
   split into **Security** (auto-ban/TLS, IP ranges, SSL certs, transport security,
   ACME), **Network** (ports, incoming relays, API & monitoring) and **Maintenance**
   (performance, scripting, event scripts, server messages, groups); the top-level
   **Status** node was already present.
10. ✅ **Style unification** — app-wide `ui:ControlsDictionary` already themes the
    standard controls and all code-built pages use the shared `Card`/`PageTitle`/
    `PageSubtitle` styles and `Wpf.Ui` buttons; the Domain/Account/Route dialog text
    inputs were converted from plain `TextBox` to `Wpf.Ui.Controls.TextBox` so they
    match the card pages exactly.
11. ✅ **Global exception handler** — unhandled UI-thread exceptions are appended
    (with full stack) to `%LOCALAPPDATA%\hMailServer\ControlPanel\control-panel-errors.log`
    and the user is offered a restart (preserving the original `/connect` args);
    background-thread failures are logged too, instead of being silently handled.
12. ✅ **Responsiveness** — lowered the window minimum to 760×520, made the sidebar
    a bounded proportional column (200–260 px, 24% of width) and the Connect card
    adaptive (300–400 px) so the UI works on small/zoomed/RDP sessions.

### Phase 3 — AV + security extensibility & hardening (CP-only)

1. ✅ **Scanner presets + Test buttons** — "Test ClamAV connection" and "Test
   ClamWin scanner" call the server COM `TestClamAVScanner`/`TestClamWinScanner`
   (live values, result line); "Test custom scanner" validates the `%FILE%`
   command's executable client-side; a Custom-scanner **preset picker** fills the
   09-0command line + infected return value for common engines (Microsoft Defender
   `MpCmdRun`, Sophos `savscan`, ESET `ecls`, Bitdefender `bdscan`, Kaspersky
   `avp.com`); plus a **ClamWin auto-detect** that locates `clamscan.exe` and the
   database folder.
2. ✅ **Event-script integration hooks** — an "Insert template" picker on the Event
   scripts page appends ready-made `OnAcceptMessage` VBScript handlers (run an
   external AV/DLP scanner via `WScript.Shell`, fire a webhook to a SIEM/Slack/Teams
   endpoint, or call an external HTTP API and act on its verdict); the AntiVirus
   custom-scanner card points admins here for engines without a CLI.
3. ✅ **Advanced hardening card** (INI-backed) — a new "Advanced hardening" page
   (under Security) surfaces the previously unexposed `IniFileSettings` knobs,
   grouped into Greylisting (`GreylistingEnabledDuringRecordExpiration`,
   `GreylistingRecordExpirationInterval`), Scanner timeouts
   (`SAMinTimeout`/`SAMaxTimeout`, `ClamMinTimeout`/`ClamMaxTimeout`), DNS
   (`UseDNSCache`, `DNSServer`, `DNSBLChecksAfterMailFrom`), Authentication &
   headers (`AuthUserReplacementIP`, `DisableAUTHList`,
   `AddXAuthUserHeader`/`AddXAuthUserIP`, `AddXOriginalRcptTo`) and Other
   (`BlockedIPHoldSeconds`, `RewriteEnvelopeFromWhenForwarding`, and a
   `PreferredHashAlgorithm` picker: Argon2id/PBKDF2/SHA-256/MD5/Blowfish). Defaults
   mirror the server's; a new `ChoiceSetting` (combo) was added to the INI editor.
4. ✅ **Account password-strength validation** — the account dialog shows a live
   strength indicator (Weak/Fair/Strong, colour-coded, with what's missing) under
   the password field as it is typed, and saving a weak password (under 8 chars or
   a single character class) prompts a confirmation the admin can override. Backed
   by a shared offline `PasswordStrength` heuristic (length + character-class
   variety).

### Phase 4 — Finalize

1. Build (save-all to disk first), then screenshot-validate every new/changed
   page and dialog with `build/capture-cp.ps1` against `hmailtest2`.
2. Rewrite `CONTROL-PANEL-GAP-ANALYSIS.md` as the authoritative
   parity + UX + security matrix (classic node → CP page → status).
3. Publish the CP, rebuild the installer (no Administrator), commit/push, move
   the release tag, clobber the release asset.

---

## Track B — remaining hardening

### B2 — Authentication modernization (remaining follow-ups)

*Delivered already (see Part 2): SCRAM-SHA-256 across IMAP/SMTP/POP3,
SCRAM-SHA-256-PLUS channel binding across all three, deterministic
anti-enumeration salts, Argon2id KDF, the hash-policy engine
(`MinimumAcceptedHashAlgorithm`) + SCRAM min-hash enforcement, optional server-side
pepper, POP3/IMAP UTF8 + full RFC 4013 SASLprep, and offline OAuth2 XOAUTH2/OAUTHBEARER
(incl. automated RS256 public-key coverage).*

Remaining:

- **OAuth2 live validation** — JWKS fetch / token introspection (today validation
  is offline/local only). RS256 public-key tokens are now covered by automated
  regression tests; live-IdP JWKS/introspection still needs a running provider.
- **Interop verification** — O365/Gmail XOAUTH2 + Thunderbird SCRAM.

### B4 — Deliverability & SMTP standards

*Delivered already (see Part 2): SMTPUTF8/EAI, PIPELINING, ENHANCEDSTATUSCODES, DSN (RFC 3461/3464),
SRS for forwarding (SPF alignment), and per-IP / per-destination rate shaping.*

- Optional: **BATV**, **CHUNKING/BDAT** (RFC 3030).
- Verify: SPF passes on forwarded mail (SRS); DSN interop.

### B6 — Standards-based filtering

- **Sieve** (RFC 5228) interpreter + **ManageSieve** (RFC 5804) service alongside
  the proprietary rules engine (`RuleApplier`). Verify with Sieve test vectors +
  a ManageSieve client.
  - **In progress — Sieve parser foundation.** A new `Common/Sieve/` module
    implements an RFC 5228 lexer (`SieveLexer`: comments, quoted + multi-line
    `text:` strings with dot-stuffing, K/M/G numbers, tags, punctuation) and a
    recursive-descent parser (`SieveParser`) that builds an AST
    (`SieveCommand`/`SieveTest`/`SieveArgument`) while validating the grammar, the
    supported command/test set, and `require` placement. Surfaced through the COM
    API as `Utilities.CheckSieveSyntax` (returns an empty string when valid, else a
    line-numbered error), mirroring the event-script `CheckSyntax` precedent.
    Validated by the `SieveSyntax` regression test (valid + invalid scripts).
    Next: evaluate the AST against a delivered message and apply
    keep/fileinto/discard/redirect, then per-account script storage and the
    ManageSieve service.
  - **In progress — Sieve evaluator.** `SieveEvaluator` executes the AST against a
    `SieveMessage` (an unfolded-header + octet-size read model) and produces an
    action summary: RFC 5228 control flow (`if`/`elsif`/`else`/`stop`), the core
    tests (`true`/`false`/`not`/`allof`/`anyof`/`header`/`address`/`exists`/`size`
    with `:is`/`:contains`/`:matches`, address parts `:all`/`:localpart`/`:domain`,
    and the default + `i;octet` comparators) and the core actions
    (`keep`/`fileinto`/`discard`/`redirect` plus implicit keep). Surfaced through
    COM as `Utilities.EvaluateSieveScript(script, rawMessage)` returning the
    `;`-joined action summary (or `error: …`). Validated by the `SieveEvaluation`
    regression test. Next: per-account script storage + wiring into `LocalDelivery`,
    then the ManageSieve service.
  - **In progress — per-account script storage.** `SieveStorage` persists each
    account's active Sieve script as a file under
    `{DataDirectory}\Sieve\{domain}\{localpart}\active.sieve` (filesystem-safe path
    sanitization; no DB schema change — the named-script model for ManageSieve
    layers on top of this directory). Surfaced as the COM `Account.SieveScript`
    get/put property (file-backed, written immediately). Validated by the
    `SieveAccountScript` round-trip test. Next: evaluate the stored script in
    `LocalDelivery` and apply fileinto/redirect/discard, then ManageSieve.
  - **Done — Sieve filtering live in local delivery.** `LocalDelivery` now loads
    the recipient account's active Sieve script and evaluates it against the
    message during delivery (`EvaluateSieveScript_`): a `fileinto` routes the
    message into the named IMAP folder (overriding the rule-selected folder), a
    `discard` silently drops it, and the implicit `keep` delivers to INBOX as
    normal. A no-script account is unaffected (zero overhead), and an unparseable
    script never breaks delivery (logged, falls through to keep). Validated by the
    `SieveDelivery` test (matching message filed into a folder; matching message
    discarded while a normal message is still delivered). Remaining: `redirect`
    (needs forwarding plumbing) and the **ManageSieve (RFC 5804)** service for
    multi-script management.

### B7 — Operability & observability

- **Done — health/readiness/liveness probes.** The local unauthenticated metrics
  listener (`MetricsServer`, enabled via `[Settings] MetricsServerPort`) now also
  serves Kubernetes-style probes alongside `/metrics`: `/livez` (always 200 once
  the listener is up), `/readyz` (200 when the server is `StateRunning` and the DB
  pool reports connected, else 503), and `/healthz` (JSON: status, server state,
  database up/down, per-protocol session counts, uptime). Covered by the
  `HealthProbes` regression test.
- **Done — database observability metrics.** `/metrics` now also exposes
  `hmailserver_database_up` (1/0) and `hmailserver_db_connections{state="busy|available"}`
  gauges sourced from the `DatabaseConnectionManager` pool, giving DB connectivity
  and pool-saturation visibility. Asserted by the `HealthProbes` test.
- **Done — log retention/rotation.** A scheduled `LogRetentionTask` (runs once at
  startup, then every 6h) deletes hMailServer's own date-stamped log files older
  than `[Settings] LogDeleteDays` (0 = disabled, the default, so historical
  behaviour is unchanged). Only files named `hmailserver_*.log` /
  `ERROR_hmailserver_*.log` are ever touched. Covered by the `LogRetention` test.
- **Done — TLS handshake metrics.** `ServerStatus` now counts completed and failed
  TLS/SSL handshakes (incremented in `TCPConnection`), exposed on `/metrics` as
  `hmailserver_tls_handshakes_total` and `hmailserver_tls_handshake_failures_total`
  counters for TLS health alerting. Asserted by the `HealthProbes` test.
- **Done — delivery-queue depth metric.** `/metrics` exposes
  `hmailserver_delivery_queue_messages` (count of messages in the SMTP delivery
  queue, i.e. in the `Delivering` state). The count is queried via
  `PersistentMessage::GetDeliveryQueueCount()` and cached for 10s inside the
  metrics listener so frequent scrapes never issue a `COUNT(*)` per request.
  Asserted by the `HealthProbes` test.
- **Done — authentication metrics.** `/metrics` exposes
  `hmailserver_auth_success_total` and `hmailserver_auth_failures_total` counters,
  incremented from the central `AccountLogon::Logon` path (covers every protocol's
  authentication). Enables alerting on credential-stuffing / brute-force spikes.
  The `HealthProbes` test performs a real failed POP3 login and asserts the failure
  counter increments (wiring, not just presence).
- **OpenTelemetry** tracing (SMTP/IMAP/POP/DB spans + correlation IDs).
- **Done — delivery-outcome metrics.** `/metrics` exposes
  `hmailserver_messages_delivered_total`, `hmailserver_messages_deferred_total`
  and `hmailserver_messages_bounced_total` counters, incremented from the SMTP
  delivery threads (`SMTPDeliverer`): delivered/deferred at the terminal delivery
  outcome and bounced at the point an NDR is actually generated. Enables
  delivery-success-rate, retry-pressure and bounce-rate alerting. Asserted by the
  `DeliveryMetrics` test (the delivered counter advances after a successful local
  delivery).
- **Done — per-command processing-latency metric.** `/metrics` now exposes the
  Prometheus summary `hmailserver_command_processing_seconds` (`_sum` + `_count`),
  accumulated centrally in `TCPConnection`'s line-command dispatch (covers every
  SMTP/IMAP/POP3 command line) via `ServerStatus::OnCommandProcessed`. Average
  command latency is derivable without per-command histograms. Asserted by the
  `HealthProbes` test (the metric is present and its count advances after protocol
  activity).
- **Done — connection-pool condition variable.** `DatabaseConnectionManager`'s
  `GetConnection_` no longer busy-polls with `Sleep(10)` while waiting for a free
  connection; it blocks on a `condition_variable_any` signalled by
  `ReleaseConnection_` (with a 100ms backstop timeout). Removes scrape/latency
  jitter under pool exhaustion. Validated by 64 DB-intensive regression tests.
- Async/DB isolation: dedicated DB executor; prepared-statement caches
  (MySQL/PG).
- **Done — graceful shutdown drain.** On shutdown the server moves to
  `StateStopping` (so `/readyz` returns 503 and load balancers stop routing) and,
  if `[Settings] ShutdownDrainSeconds > 0`, waits up to that window for active
  SMTP/IMAP/POP sessions (`SessionManager::GetNumberOfConnections()`) to finish
  before tearing the listeners down — keeping the metrics listener up during the
  drain. Default 0 preserves the previous immediate-stop behaviour. Validated by
  the `ShutdownDrain` test (Stop() returns promptly when idle, waits ~the window
  while a session is held open).
- **Done — configurable message-store fsync.** With `[Settings] MessageStoreFsync = 1`
  a received message is forced to physical disk (`fflush` + `_commit` →
  `FlushFileBuffers`, via the new `File::FlushToDisk()`) before the spool file is
  closed at the SMTP accept point — so the message is durable before the server
  acknowledges it to the sender. Default 0 keeps the previous OS-buffered behaviour
  (no per-message fsync cost). Validated by the `MessageStoreDurability` test
  (delivery still succeeds end-to-end with the barrier on). The broader consistency
  checker / recovery tooling remains future work.
- **Done — message-store consistency check.** Opt-in via
  `[Settings] MessageStoreConsistencyCheck = 1`: a scheduled, read-only task
  (`MessageStoreConsistencyTask`, run once at startup then hourly) cross-checks
  every message row against its backing file on disk
  (`PersistentMessage::GetMissingFileCount()` reconstructs each on-disk path with
  the same logic the server uses) and publishes the count of missing files as the
  `hmailserver_messagestore_missing_files` gauge, logging a warning when divergence
  is found. The check never deletes or repairs anything. Default 0 keeps the
  (potentially expensive) store walk off. Validated by the
  `MessageStoreConsistency` test (delete a delivered message's file → gauge reports
  the missing file).
- **Done — message-store recovery report.** When the consistency check finds
  missing files it writes `hMailServer_messagestore_consistency.report` to the log
  directory: a timestamped, tab-separated list of every affected message
  (`PersistentMessage::GetMissingFileDetails()` → `messageid`, account, expected
  on-disk path) so an administrator has an actionable artifact to drive recovery
  (restore-from-backup, user notification, orphan cleanup). Read-only; the report
  is regenerated on each scan. Asserted by the `MessageStoreConsistency` test (the
  report lists the missing message's path and account). Active automated
  repair/re-fetch remains future work.
- **Done — HA active/passive runbook.** A documented, validated active/passive
  topology (shared external database + shared message store + floating VIP) with
  readiness gating on `/readyz` and graceful-drain failover, in
  [hmailserver/docs/HighAvailabilityRunbook.md](hmailserver/docs/HighAvailabilityRunbook.md).
  No clustering code — failover is driven by external infrastructure plus the
  readiness/drain primitives already shipped in this track.

### B8 — Quality gates & supply chain (remaining)

*Delivered already (see Part 2): `ci.yml`, `codeql.yml`, `server-build.yml`, B1
reproducer tests and the over-the-wire SMTP/IMAP/MIME protocol fuzz suite.*

Remaining:

- build+test matrix Windows × MySQL/MSSQL/PostgreSQL running the full suite (today
  the self-hosted workflow runs one DB at a time).
- clang-tidy; ASAN/UBSAN build; coverage-guided **libFuzzer** harnesses (need a
  clang+fuzzer toolchain and decoupled parsers — impractical in the current
  MSVC/ATL environment, where the live over-the-wire fuzzer is the substitute).
- SBOM + dependency/CVE scanning + signed release artifacts.
- Verify: green-gates-required-to-merge; nightly fuzz.

---

## Cross-cutting — surface new server capabilities in the Control Panel

OAuth2 provider config, SCRAM/Argon2 policy, SMTPUTF8/SRS/rate-limit toggles, IMAP
profile, Sieve/ManageSieve editor, secrets/least-priv status, health/trace
endpoints, AV scanner presets + tests (Track A Phase 3), a security-diagnostics
report.

## Future track (Tier 3 — documented, not scheduled)

Linux/container port (OS-abstraction layer first; today hard-wired to
Win32/ATL/registry/service), JMAP (RFC 8620/8621), CalDAV/CardDAV, native webmail,
true clustering/HA, rspamd integration, BIMI + VMC, OCSP stapling, ARF feedback-loop
processing. Also: **IMAP4rev2 (RFC 9051)** as its own milestone (assessed and
deferred — see Part 2, B5).

## Verification (per phase)

- **Track A:** `dotnet build` clean (0/0); launch via `build/capture-cp.ps1
  -Launch`; screenshot each changed page/dialog; confirm load+save round-trips
  against `hmailtest2` (MariaDB root `tester`; CP `/connect localhost Administrator
  testar`). Installer: ISCC builds; a clean install shows no Administrator
  shortcut, the Control Panel present, and DB tools intact. 2FA: setup → reconnect
  requires TOTP; an incorrect code is rejected.
- **Track B:** clean build; run the regression suite (`build/run-tests.ps1`) plus
  new negative/fuzz tests; no `/WX` warnings. Security phases: a reproducer test
  proves the defect is closed. Interop phases: test against real clients.

## Open considerations (Track A)

1. **Localization** — the classic supports translations; the CP is English-only.
   Recommended as a later roadmap item, not this pass.
2. **Active-session management** — Status can show live session *counts* now;
   *disconnecting* sessions likely needs server support. Recommend view-only.
3. **Removing Administrator from the Tools `.sln`** — defer until CP parity is
   proven in production.

## Guiding decisions (Track A)

- **Classic removal = installer only.** `source/Tools/Administrator` and the
  Tools solution stay; only the installer stops shipping the Administrator.
  DBSetup, DBUpdater and DataDirectorySynchronizer are retained.
- **AV/security extensibility = Control-Panel only.** Configure external
  scanners with presets + Test buttons and surface the event-script
  integration hooks. No server-side (C++) plugin API in this pass.
- **TOTP 2FA is ported** from the classic to the Control Panel.
- **Order:** parity-to-retire-classic → UX/UI polish → extensibility/hardening
  → release.

## Key files (Track A)

**Installer:** `hmailserver/installation/section_files_64.iss`,
`section_files_common.iss`, `section_icons.iss`, `section_run.iss`,
`section_components.iss`.

**Control Panel:** `Views/IPRangesView.*`, `Views/TcpIpPortsView.*`,
`Views/RulesView.*`, `Views/RouteDialog.cs`, `Views/AccountDialog.cs`,
`Views/ServerSettingsView.xaml.cs`, `Views/FeatureSettingsView.xaml.cs`,
`Views/ConnectView.*`, `Views/ScriptsView.cs`, `Views/DomainsView.xaml.cs`,
`MainWindow.xaml.cs`, `App.xaml`/`App.xaml.cs`.

**Port-from-classic:** `Administrator/Utilities/TwoFactorAuth.cs`,
`Dialogs/formTotpSetup.cs`, `formTotpPrompt.cs`, `formRule.cs`,
`formRuleCriteria.cs`, `formRuleAction.cs`, `formActiveDirectoryAccounts.cs`,
`formSelectUsers.cs`, `formUserAccounts.cs`, `formImportMembers.cs`,
`formMessageViewer.cs`.

**Server (read-only reference):**
`source/Server/hMailServer/hMailServer.idl`,
`source/Server/Common/Application/IniFileSettings.cpp`.

*Verified strong (do not redo): PBKDF2-HMAC-SHA256 (210k iters, transparent
rehash-on-login), TLS 1.2/1.3 defaults, DANE+DNSSEC outbound, ARC, Ed25519 DKIM,
MTA-STS, TLS-RPT, auto-ban, correct dot-stuffing, parameterized SQL.*

---

90p09op# PART 2 — COMPLETED WORK (record)

## Completed master-sequence steps

1. **B1 — security & correctness defects + secure defaults.** ✅ Done.
2. **B8 (core) — CI + fuzzing.** ✅ Done.
3. **B3 — secrets & least-privilege.** ✅ Done (v6.2.0).
4. **B5 — IMAP modern sync profile.** ✅ Done (v6.2.0) — full IMAP suite 242/242.
5. **B2 (large parts) — auth modernization.** ✅ SCRAM (IMAP/SMTP/POP3),
   SCRAM-PLUS (all three), Argon2id, hash policy, pepper, UTF8/SASLprep, offline
   OAuth2 — all v6.2.0. (Remaining follow-ups are in Part 1.)
6. **Track A Phase 1 — functional parity.** ✅ 9/10 (item 7 deferred — Part 1).
7. **B4 — deliverability & SMTP standards.** ✅ Done (v6.2.1) — SMTPUTF8/EAI,
   PIPELINING, ENHANCEDSTATUSCODES, DSN, SRS, per-IP/per-destination rate shaping.

---

## Track B — delivered

### B1 — Protocol correctness & DoS hardening ✅ DONE

Validated end-to-end: the IMAP and SMTP regression suites pass (originally **IMAP
215/215** + **SMTP 175/175**; SMTP re-run under strict line-endings and again with
the AUTH cap — both 175/175). The suite has since grown and stays green with the B1
reproducer tests, the IMAP/POP3 per-connection auth-cap tests (**IMAP+POP3 267/267**)
and the over-the-wire protocol fuzz suite (**3/3**). Commits: `6f7e019` (defects),
`53ec538` (line-ending default), `9f3a51e` (AUTH cap).

| Fix | File | What changed |
|---|---|---|
| ✅ IMAP literal overflow / unbounded buffer | `IMAP/IMAPConnection.cpp` | `GetLiteralSize_` validates digits, parses 64-bit, rejects overflow, caps command literals to 10 MB (prevents pre-auth memory pinning). |
| ✅ IMAP APPEND overflow + over-write | `IMAP/IMAPCommandAppend.cpp` | Validates octet count, hard 2 GB ceiling even when max size is unlimited, writes only the declared literal length (no message corruption / parser desync). |
| ✅ MIME header over-read | `Common/Mime/Mime.cpp` | `MimeHeader::Load` bounds every read by `nDataSize`; no read past the caller's buffer on an unterminated header. |
| ✅ AV scanner path hijack | `Common/AntiVirus/ClamWinVirusScanner.cpp`, `CustomVirusScanner.cpp` | Quotes the executable path so a spaced path can't be hijacked by `CreateProcess` (unquoted-path resolution). |
| ✅ Listener slow-loris | (REST/Web/Metrics) | Verified already mitigated — 64 KB / fixed-buffer request caps + 5–10 s read deadlines already present. |
| ✅ Secure default: strict SMTP line endings | `DBScripts/CreateTables{MYSQL,MSSQL,PGSQL}.sql` | `smtpallowincorrectlineendings` default 1→0 on fresh installs (SMTP-smuggling hardening). Validated: SMTP suite 175/175 under strict mode. Commit `53ec538`. |
| ✅ Per-connection SMTP AUTH cap | `SMTP/SMTPConnection.cpp/.h` | 10 failed AUTH attempts per connection → 535 + disconnect (defense-in-depth over per-IP auto-ban). Validated: SMTP 175/175. Commit `9f3a51e`. |
| ✅ Per-connection IMAP/POP3 auth cap | `IMAP/IMAPConnection.*`, `IMAP/IMAPCommandLogin.cpp`, `IMAP/IMAPCommandAuthenticate.cpp`, `POP3/POP3Connection.*` | 10 failed logins per connection → disconnect, even when the per-IP auto-ban is disabled (parity with SMTP). Validated: IMAP 217/217, POP3 48/48. Regression tests added (commit `4dae4b1`, IMAP+POP3 267/267). Commit `b8a3829`. |

Deferred from B1 (folded into the TLS/auth work, higher regression risk): review found
both already satisfied by the 6.0 modernization — **TLS** defaults to 1.2+1.3 only
(`SslVersions=24`) with SSLv2/3 always off and modern EC curves
(`secp384r1:x25519:secp256r1`) — no RC4/legacy-protocol exposure; **passwords** default
to PBKDF2 (`PreferredHashAlgorithm=4`), COM `put_Password` and the REST API both hash new
passwords with PBKDF2, and logins transparently re-hash MD5/SHA256 → PBKDF2. Remaining
*optional* hardening: an explicit AEAD-only cipher-list default (client-interop trade-off)
and upgrading the management/admin INI password from MD5.

### B2 — Authentication modernization (delivered parts) ✅

- ✅ **SCRAM-SHA-256 SASL mechanism (IMAP) — delivered in v6.2.0.** Added the
  `AUTHENTICATE SCRAM-SHA-256` mechanism (RFC 5802 / RFC 7677) so the password is never sent over
  the wire. The stored PBKDF2-HMAC-SHA256 key is, by construction, exactly the SCRAM SaltedPassword
  for the same salt and iteration count, so SCRAM is served straight from the existing account hash
  with no re-hash or password prompt — any PBKDF2-hashed account (the default) can use it. New
  crypto/message helper `Common/Util/Hashing/ScramSha256` (binary-safe base64, HMAC/SHA-256, nonce
  generation, client-first/-final parsing, server-first/-final construction and constant-time proof
  verification); per-connection SASL state on `IMAPConnection`; the multi-step exchange is driven in
  `IMAPCommandAuthenticate` by re-seeding the command buffer (the same technique the PLAIN path
  uses), so no new connection-state machine was needed. Unknown / non-PBKDF2 accounts run a
  forced-failure exchange (random salt) so the protocol does not reveal whether an account exists.
  The auto-ban accounting was refactored into `AccountLogon::RegisterFailedLogin` and is shared with
  the LOGIN/PLAIN path, and the per-connection brute-force cap also applies. Advertised in CAPABILITY
  as `AUTH=SCRAM-SHA-256`. Validated end-to-end with an over-the-wire C# SCRAM client
  (`TestScramSha256Authenticates`, `TestScramSha256WrongPasswordFails`, `TestScramSha256Capability`)
  plus the full IMAP regression suite.
- ✅ **SCRAM-SHA-256 SASL mechanism (SMTP submission) — delivered in v6.2.0.** Extended the same
  mechanism to SMTP `AUTH` (RFC 4954 SASL framing), reusing the `Common/Util/Hashing/ScramSha256`
  helper. Per-connection SASL state lives on `SMTPConnection` (`scram_session_`); the multi-step
  exchange is routed by three new connection states (`SMTPSCRAMFIRST`/`SMTPSCRAMFINAL`/`SMTPSCRAMACK`)
  with each base64 SASL message carried over `334` continuations and completion signalled with `235`.
  Honours SASL-IR (`AUTH SCRAM-SHA-256 <base64>`), `*` cancellation, the per-IP auto-ban and the
  per-connection brute-force cap; unknown / non-PBKDF2 accounts run the same forced-failure exchange.
  Advertised in EHLO. Validated with an over-the-wire C# SMTP SCRAM client
  (`TestScramSha256Authenticates`, `TestScramSha256WrongPasswordFails`, `TestScramSha256Advertised`)
  plus the full SMTP regression suite.
- ✅ **SASL AUTH for POP3 — PLAIN + SCRAM-SHA-256 — delivered in v6.2.0.** POP3 previously had only the
  legacy `USER`/`PASS` login; added the RFC 5034 `AUTH` command supporting `PLAIN` and
  `SCRAM-SHA-256` (RFC 5802 / RFC 7677), reusing the `Common/Util/Hashing/ScramSha256` helper. SASL
  continuation lines are routed at the top of `InternalParseData` before command parsing; `CAPA`
  advertises `SASL PLAIN SCRAM-SHA-256` (gated on TLS like `USER`). Unknown / non-PBKDF2 accounts run
  the same forced-failure SCRAM exchange (anti-enumeration). Validated with an over-the-wire C# POP3
  SASL client (`TestSaslAdvertised`, `TestAuthPlainAuthenticates`, `TestScramSha256Authenticates`,
  `TestScramSha256WrongPasswordFails`) plus the full POP3 regression suite. This completes
  SCRAM-SHA-256 across IMAP, SMTP and POP3.
- ✅ **SCRAM deterministic anti-enumeration salts — delivered in v6.2.0.** Closed a user-enumeration
  side-channel in the SCRAM forced-failure path shared by IMAP, SMTP and POP3. The fabricated salt for
  an unknown/non-PBKDF2 account is now **deterministic per identity**
  (`ScramSha256::DeriveAntiEnumerationSalt_`): `HMAC-SHA256(key, "scram-anti-enum-salt:" +
  lowercased-identity)` truncated to 16 bytes, keyed from server-side secrets a mail client never sees
  (admin password hash + DB credentials/name/server, domain-separated), so the salt is stable per
  installation yet cannot be precomputed off-box. Validated over the wire
  (`TestScramSha256UnknownAccountSaltIsStable`) plus the full IMAP regression suite.
- ✅ **SCRAM-SHA-256-PLUS channel binding (IMAP) — delivered in v6.2.0.** Added
  `AUTH=SCRAM-SHA-256-PLUS` (RFC 5802 + RFC 5929 `tls-server-end-point`) so authentication is
  cryptographically bound to the TLS channel, defeating a MITM who relays an otherwise-valid SCRAM
  exchange over a different TLS connection. `TCPConnection::GetTlsServerEndPoint` derives the binding
  data; the `ScramSha256` helper gained a PLUS mode (`SetChannelBinding`). Advertised in CAPABILITY and
  accepted **only** on TLS; the non-PLUS mechanism now rejects a `y` gs2 flag (stripped-PLUS downgrade,
  RFC 5802 §6). Validated over real TLS (`RegressionTests.SSL.ScramPlus`): `TestScramPlusAuthenticates`,
  `TestScramPlusWrongBindingFails`, `TestScramPlusAdvertisedOnTlsOnly`, `TestScramPlusRejectedWithoutTls`.
  Full IMAP suite 246/246 and the SCRAM set 13/13 green.
- ✅ **SCRAM-SHA-256-PLUS channel binding (SMTP submission) — delivered in v6.2.0.** Extended the same
  mechanism to SMTP `AUTH` (RFC 4954), reusing `GetTlsServerEndPoint` and the PLUS mode unchanged. EHLO
  advertises `SCRAM-SHA-256-PLUS` only on TLS. Validated over real TLS
  (`RegressionTests.SSL.ScramPlusSmtp`). SCRAM set 17/17 and the full SMTP suite 178/178 green.
- ✅ **SCRAM-SHA-256-PLUS channel binding (POP3) — delivered in v6.2.0.** Completed the channel-binding
  rollout to POP3 `AUTH` (RFC 5034). `CAPA` and the bare-`AUTH` list advertise `SCRAM-SHA-256-PLUS` only
  over TLS. Validated over real TLS (`RegressionTests.SSL.ScramPlusPop3`). SCRAM set 21/21 and the full
  POP3 suite 53/53 green.
- ✅ **Argon2id KDF option — delivered in v6.2.0.** Added the OWASP-recommended memory-hard KDF as
  password-hash algorithm **5** (`Crypt::ETArgon2id`), implemented in `HashCreator` over OpenSSL's
  `EVP_KDF` `ARGON2ID` (no new dependency; default m=19456 KiB, t=2, p=1; self-describing
  `$a2$<m>$<t>$<p>$<salt-hex>$<key-hex>` hash). Transparent rehash-on-login was generalised to upgrade
  to whichever strong KDF is configured **without ever downgrading**. PBKDF2 remains the default;
  Argon2id is opt-in via `PreferredHashAlgorithm=5`. Validated by the `RunTestSuite` self-tests.
- ✅ **Password hash-policy engine (MinimumAcceptedHashAlgorithm) — delivered in v6.2.0.** New
  `[Settings] MinimumAcceptedHashAlgorithm` INI key (default `0` = disabled) compared against the
  account's stored `Crypt::EncryptionType` (weak→strong: None=0, BlowFish=1, MD5=2, SHA256=3, PBKDF2=4,
  Argon2id=5). `PasswordValidator::ValidatePassword` refuses any cleartext login whose stored hash type
  is below the configured minimum *before* verifying the password. AD accounts are exempt. Validated by
  `RegressionTests.Security.HashPolicy`; combined Security/SCRAM/POP3 109/109 green.
- ✅ **SCRAM minimum-hash enforcement — delivered in v6.2.0.** When the admin raises
  `MinimumAcceptedHashAlgorithm` above PBKDF2, the `LookupPbkdf2Account_` helper on all three protocols
  returns no account, turning every SCRAM exchange into the anti-enumeration forced-failure. Validated
  by `RegressionTests.Security.HashPolicy`; Security 39/39 and SCRAM+POP3 73/73 green.
- ✅ **Optional server-side password pepper — delivered in v6.2.0.** New `[Settings] PasswordPepper` INI
  key (default empty). When set, applied as an HMAC-SHA-256 keyed transform of the password before the
  **Argon2id** hash (Argon2id only — peppering PBKDF2 would break SCRAM). Validated by
  `RegressionTests.Security.PasswordPepper`; Security 39/39 green.
- ✅ **POP3/IMAP UTF8 and SASLprep of non-ASCII SASL credentials — delivered in v6.2.0; SASLprep
  completed to full RFC 4013 in v6.2.2.** POP3 advertises
  `UTF8` in `CAPA` and accepts `UTF8` (RFC 6856); IMAP advertises `UTF8=ACCEPT` and honours
  `ENABLE UTF8=ACCEPT` (RFC 6855). SASL `PLAIN` tokens decoded as raw UTF-8 via
  `StringParser::DecodeSaslPlain`, and the decoded authcid passed through a full RFC 4013
  `StringParser::SaslPrep` on the POP3/IMAP/SMTP paths: RFC 3454 mapping (B.1 → nothing,
  C.1.2 → space), **Unicode NFKC normalization** (Win32 `NormalizeString`), the complete
  prohibited-output tables (C.2.1, C.2.2, C.3–C.9 — controls, private-use, non-characters,
  surrogates, etc.), and the RFC 3454 §6 bidirectional check (a RandALCat string must contain
  no LCat character and must start and end RandALCat). NFKC is a no-op on ASCII, so existing
  credentials are unaffected; the A.1 (unassigned) and exhaustive D.2 (LCat) tables are out of
  scope (require the full UCD; StringPrep is superseded by PRECIS/RFC 7613). Validated by
  `TestUtf8CapabilityAndCommand`, `TestAuthPlainSaslPrepsUsername`, `TestAuthPlainSaslPrepNfkcUsername`,
  the in-server `StringParserTester` self-test (NFKC/prohibition/bidi vectors), and
  `TestEnableUtf8AcceptEchoesEnabled`; POP3/Security/Infrastructure green.
- ✅ **OAuth2 bearer authentication — SASL XOAUTH2 + OAUTHBEARER (RFC 7628) — delivered in v6.2.0.**
  POP3, IMAP and SMTP submission accept bearer-token logins. The new `OAuth2TokenValidator`
  (`Common/Util`) validates the JWT **locally**: enforces an algorithm allow-list
  (`OAuth2AllowedAlgorithms`) — `HS256` verified (constant-time) against `OAuth2HmacSecret`,
  `RS256`/`ES256` against the PEM public key in `OAuth2PublicKeyFile` (OpenSSL `EVP_DigestVerify`).
  `none`/empty `alg` rejected (algorithm-confusion defence); requires `exp` (60s skew), honours `nbf`,
  checks `iss`/`aud` when configured; the configurable username claim (`OAuth2UsernameClaim`, default
  `email`) maps to a local account. Off by default (`OAuth2Enabled=0`); TLS-only unless
  `OAuth2RequireTLS=0`. Validated by `RegressionTests.Security.OAuth2Bearer` (13 cases, incl. RS256
  public-key tokens over POP3+SMTP with a tamper-rejection check) + self-test;
  POP3/Security/IMAP/SMTP green. **Limitation:** validation is offline/local only — no JWKS
  fetch / introspection / live-IdP interop yet. *(See Part 1
  for the remaining live-validation + interop follow-ups.)*

### B3 — Secrets & least-privilege ✅ DELIVERED (v6.2.0)

- ✅ **DPAPI envelope encryption** (machine-scoped, `CRYPTPROTECT_LOCAL_MACHINE`) for all reversible
  stored secrets, gated by the new `[Settings] ProtectStoredSecretsWithDPAPI` key (default **on**):
  - New `DataProtector` primitive (`Common/Util/DataProtector.{h,cpp}`) wrapping `CryptProtectData`/
    `CryptUnprotectData`, plus a `Crypt::ETDPAPI` type and self-describing `Crypt::ProtectSecret`/
    `UnprotectSecret` helpers that emit a `DPAPI:<base64>` envelope and transparently fall back to
    (and keep reading) legacy Blowfish values.
  - The **DB password** in `hMailServer.INI` (`IniFileSettings::SetPassword`, via the typed
    `PasswordEncryption` column so plaintext/Blowfish/DPAPI are all distinguishable).
  - The DB-stored route (`PersistentRoute`), fetch-account (`PersistentFetchAccount`) and
    SMTP-relayer/crypted-property (`Property`/`PropertySet`) passwords.
  - DPAPI blobs are larger than the old Blowfish hex, so `routeauthenticationpassword` and
    `fapassword` were widened `255→1024` (DB version `6003→6004`: create scripts + new
    `Upgrade6003to6004{MySQL,PGSQL,MSSQL,MSSQLCE}.sql`, DBUpdater path completed).
  - Trade-off (documented): machine-scoped DPAPI means secrets are **not reversible off-box**, so a
    backup restored onto a different machine must re-enter them; set `ProtectStoredSecretsWithDPAPI=0`
    to keep portable Blowfish. Secrets are never lost (Blowfish fallback if DPAPI fails).
- ✅ **Least-privileged service account** (opt-in): `ServiceManager` passes the new `[Settings]`
  `ServiceAccountName`/`ServiceAccountPassword` to `CreateService`/`ChangeServiceConfig`. Default
  empty = LocalSystem (unchanged); recommended value is the password-less virtual account
  `NT SERVICE\hMailServer`. Explicit ACL/privilege-drop automation left to the administrator.
- Validated: in-server `DataProtector` self-test (round-trip + tamper + machine-binding) and
  `RegressionTests.Security.SecretProtection`; 287/287 Security+SMTP+POP3 green, server builds 0/0.

### B4 — Deliverability & SMTP standards ✅ DELIVERED (v6.2.1)

All over-the-wire, validated against the live server (regression suite green, builds 0/0). Default-off
toggles preserve backwards compatibility.

- ✅ **PIPELINING (RFC 2920)** — advertised in EHLO; the async command reader already pipelines, so this
  is a capability announcement that lets clients batch commands. Covered by `SMTP/Deliverability.cs`.
- ✅ **SMTPUTF8 / EAI (RFC 6531/6532)** — `SMTPUTF8` advertised in EHLO and accepted as a `MAIL FROM`
  parameter; a relaxed UTF-8 e-mail validator (the previous one was ASCII-only) accepts internationalized
  local-parts/domains; outbound the client appends `SMTPUTF8` when the envelope has non-ASCII and the
  remote advertises it. Covered by `SMTP/Deliverability.cs`.
- ✅ **ENHANCEDSTATUSCODES (RFC 2034)** — advertised in EHLO; replies carry `x.y.z` enhanced codes via a
  central helper, gated on `esmtp_session_` (EHLO on / HELO off) so legacy `250 OK`-exact expectations are
  unaffected. Server-to-server safe (clients parse the leading numeric code). Covered by
  `SMTP/Deliverability.cs`. *(Features 1–3 committed together as `0d26824`.)*
- ✅ **DSN (RFC 3461/3464)** — `DSN` advertised in EHLO; `MAIL FROM` accepts+validates `RET=FULL|HDRS`
  and `ENVID=<xtext>`; `RCPT TO` accepts+validates `NOTIFY` (`NEVER` | combination of
  `SUCCESS,FAILURE,DELAY`) and `ORCPT=<addr-type>;<xtext>`. The per-recipient `NOTIFY` bitmask is
  persisted (schema `6004→6005`, new `hm_messagerecipients.recipientdsnnotify`, upgrade scripts for all
  four backends) and honored: `NOTIFY=NEVER` (and any notify set without `FAILURE`) suppresses the failure
  DSN in both `ExternalDelivery` and `LocalDelivery`. Covered by the DSN tests in `SMTP/Deliverability.cs`.
  *(Documented limitation: `RET`/`ENVID`/`ORCPT` are validated but not echoed into the generated report;
  the existing NDR template is retained rather than rewritten into RFC 3464 multipart/report.)*
- ✅ **SRS — Sender Rewriting Scheme** (replaces the naive forwarding envelope rewrite): a new
  `Common/Util/SRS.{h,cpp}` primitive produces and reverses HMAC-SHA256-signed `SRS0=` envelope
  addresses (base32 day-slot timestamp, 21-day validity + 1-day skew, first-8-hex signature). On
  forwarding to an external destination `SMTPForwarding` rewrites `MAIL FROM` to a signed local SRS0
  address so forwarded mail keeps SPF alignment; on the inbound bounce path the signed address is
  verified and reversed back to the original sender in **both** the RCPT-time check
  (`RecipientParser::CheckDeliveryPossibility`, which treats a valid reverse as authorized local delivery
  so the bounce relays without SMTP auth) and the spool-time recipient build
  (`CreateMessageRecipientList_`). Gated by new `[Settings] SRSEnabled` (default **off**) + `SRSSecret`;
  the HMAC signature prevents the reverse path from becoming an open relay. Covered by an in-server SRS
  self-test and the over-the-wire `SMTP/Srs.cs`.
  - En route, two latent self-test defects in already-committed code were fixed: the `StringParser`
    SASLprep test used greedy `\x` escapes (`\x00ADer` consumed the following `e` → U+0ADE), corrected to
    fixed-width `\u`; and `Unicode::WideToMultiByte` returned an `AnsiString` whose length included a
    trailing NUL padding byte (it sized the buffer to `bytes+1` via `GetBuffer` but never `ReleaseBuffer`),
    which corrupted DPAPI-protected secrets with a trailing NUL on round-trip — now trimmed to the exact
    byte count written. Full internals self-test + 597/597 SMTP/POP3/IMAP/Security/SSL green.
- ✅ **Per-IP / per-destination rate shaping** — a new thread-safe, in-memory sliding-window limiter
  (`Common/Util/RateLimiter.{h,cpp}`, 60-second window keyed by an arbitrary string). Two new
  default-off `[Settings]` keys: `MaxSubmissionsPerIPPerMinute` caps how many `MAIL FROM` transactions a
  single source IP may start per minute (enforced in `SMTPConnection::ProtocolMAIL_`, over-budget
  submissions get `421`), and `MaxOutboundPerDestinationPerMinute` caps how many messages this server
  sends to one destination domain per minute (enforced in `ExternalDelivery::DeliverToSingleDomain_`,
  over-budget deliveries are deferred non-fatally and retried later rather than bounced). Both are 0 by
  default = unlimited, so behaviour is unchanged until configured. Covered by an in-server `RateLimiter`
  self-test and the over-the-wire `SMTP/RateShaping.cs` (per-IP throttle is per source IP, not per
  connection; disabled-by-default verified). 194/194 SMTP + internals green, builds 0/0.

### B5 — IMAP modern sync profile ✅ DELIVERED (v6.2.0)

All targeted extensions delivered and validated (full IMAP suite 242/242). Verify in the field: fast
resync in Thunderbird/Apple Mail.

- ✅ **UNSELECT (RFC 3691)** — closes the selected mailbox without the implicit EXPUNGE that CLOSE
  performs (\Deleted retained). Covered by `TestUnselectKeepsDeletedMessages`.
- ✅ **UIDPLUS (RFC 4315)** — APPEND/COPY/MOVE return `[APPENDUID]`/`[COPYUID]`; `UID EXPUNGE` removes
  only \Deleted messages whose UID is in the set. Covered by `TestAppendReturnsAppendUid`,
  `TestCopyReturnsCopyUid`, `TestUidExpungeOnlyRemovesMatchingUids`.
- ✅ **ENABLE (RFC 5161)** — negotiates opt-in extensions; tagged OK. Covered by `TestEnableReturnsOk`.
- ✅ **STATUS=SIZE (RFC 8438)** — `STATUS` answers `SIZE`. Covered by `TestStatusReturnsMailboxSize`.
- ✅ **ESEARCH (RFC 4731)** — `SEARCH RETURN (MIN MAX ALL COUNT)` emits `* ESEARCH`. Covered by
  `TestEsearchReturnsExtendedResponse`.
- ◑ **CONDSTORE/QRESYNC (RFC 7162)** — the one B5 item requiring a **database schema migration**,
  shipped in supervised stages:
  - **Stage 1:** persistent mod-sequence storage + read surface. `REQUIRED_DB_VERSION` 6001→6002; added
    `messagemodseq` (hm_messages) + `foldercurrentmodseq` (hm_imapfolders) across all four
    `CreateTables*` scripts; `Upgrade6001to6002{MSSQL,MSSQLCE,MySQL,PGSQL}.sql`. `CAPABILITY` advertises
    `CONDSTORE QRESYNC`; `ENABLE CONDSTORE`/`QRESYNC` echo `* ENABLED`; `SELECT (CONDSTORE)` emits
    `* OK [HIGHESTMODSEQ n]`; `STATUS (HIGHESTMODSEQ)` + `FETCH (MODSEQ)`. MySQL/MariaDB validated
    (316/316); other backends authored.
  - **Stage 2:** `FETCH (CHANGEDSINCE n)`, `STORE (UNCHANGEDSINCE n)` with `[MODIFIED <set>]`, and
    `SEARCH MODSEQ n`. Covered by `TestFetchChangedSinceFiltersByModSeq`,
    `TestStoreUnchangedSinceRejectsModified`, `TestStoreUnchangedSinceSucceedsAndReturnsModSeq`,
    `TestSearchModSeqReportsHighest`.
  - **Stage 3a (QRESYNC in-session):** `SELECT (QRESYNC (...))` replays flag/MODSEQ changes; `EXPUNGE`
    emits `* VANISHED`. Covered by `TestExpungeWithQResyncReturnsVanished`,
    `TestSelectQResyncReplaysChanges`.
  - **Stage 3b (QRESYNC offline tracking):** persistent expunged-UID tombstones (`hm_imapexpunged`, DB
    6002→6003) at the `PersistentMessage::DeleteObject` chokepoint; `* VANISHED (EARLIER)` on resync and
    `UID FETCH (CHANGEDSINCE n VANISHED)`. Covered by `TestSelectQResyncReportsVanishedEarlier`,
    `TestUidFetchVanishedReportsEarlier`. Migration scripts for all backends; MySQL validated in CI.
    Follow-up: tombstone pruning beyond folder deletion not yet implemented (RFC 7162 allows full-resync
    fallback).
- ✅ **LIST-EXTENDED (RFC 5258)** — optional leading selection-options (`(SUBSCRIBED)`, `REMOTE`/
  `RECURSIVEMATCH` no-ops), trailing `RETURN (SUBSCRIBED CHILDREN)`, and parenthesised pattern lists.
  Covered by `TestListExtendedReturnSubscribed`, `TestListExtendedSelectSubscribed`,
  `TestListExtendedMultiplePatterns`.
- ✅ **SEARCHRES (RFC 5182)** — `SEARCH RETURN (SAVE)` + the `$` marker in
  `FETCH`/`STORE`/`COPY`/`MOVE`/`UID EXPUNGE`. Covered by `TestSearchResSaveAndFetch`,
  `TestSearchResSaveAndStore`, `TestSearchResCapability`. Follow-up: `$` inside `SEARCH` criteria not yet
  supported.
- ⏸ **IMAP4rev2 (RFC 9051) — assessed and deferred to its own milestone** (see Part 1, Future track).
  hMailServer already implements the individual extensions IMAP4rev2 folds in (UIDPLUS, ENABLE, IDLE,
  NAMESPACE, MOVE, SPECIAL-USE, UNSELECT, ESEARCH, SEARCHRES, STATUS=SIZE, LIST-EXTENDED, SASL-IR,
  CONDSTORE). Full conformance is not a single safe increment: advertising `IMAP4rev2` obliges UTF-8
  mailbox names (session-scoped encode/decode switch across every mailbox-name command, currently all via
  `ModifiedUTF7`), ESEARCH-by-default, dropping `\Recent`/`RECENT`, deprecating `LSUB`, and a
  response-code audit. The `ENABLE` handler and `CAPABILITY` are the entry points when scheduled.

### B8 — Quality gates & supply chain (core) ✅ DELIVERED

GitHub Actions: `ci.yml` (Control Panel build, warnings-as-errors) and `codeql.yml` (CodeQL C# SAST)
on hosted runners, plus `server-build.yml` (self-hosted native C++ build on a VS 2026/v145 runner +
opt-in regression-suite run, commit `6b692a8`). B1 reproducer tests
(`TestAppendOversizedLiteralRejected` / `TestOversizedCommandLiteralRejected`, 2/2) and an
**over-the-wire protocol fuzz suite** (`Security/ProtocolFuzz.cs`, commit `fc6d1da`): a seeded
malformed-input barrage against the live SMTP/IMAP command parsers and the inbound MIME parser, asserting
the server never crashes/hangs/logs a fault (liveness check + `ServiceRestartDetector` +
`AssertNoReportedError`, per-test `[Timeout]`); 3/3 pass (~231 s). Coverage-guided libFuzzer assessed and
impractical here (no fuzzer runtime in the available clang; parsers MSVC/ATL/Windows-coupled) — the live
fuzzer is the validated substitute. *(Remaining B8 items in Part 1.)*

---

## Track A — delivered

### Phase 1 — Functional parity (so the classic can be retired) ✅ 9/10

| # | Item | COM / source | Notes |
|---|---|---|---|
| 1 | ✅ **IP-range full policy editor** (commit 9743096) | `IInterfaceSecurityRange` | Tabbed `IPRangeDialog` (General/Connections/Relaying/Require auth/Protection): RequireSMTPAuth per direction, EnableSpamProtection, EnableAntiVirus, Expires+ExpiresTime, Priority. Wired via Properties button + double-click. Live-validated. |
| 2 | ✅ **TCP/IP port → SSL certificate binding** (commit 9743096) | `IInterfaceTCPIPPort.SSLCertificateID` | `TcpIpPortDialog` picks from `Settings.SSLCertificates`; Certificate column added to grid. Live-validated. |
| 3 | ✅ **Rules editor parity** (commits 139bd91 + fd3a27b account-level) | `IInterfaceRule/RuleCriteria/RuleActions` | Selectable criteria/action grids; per-item **edit** dialogs (`RuleCriteriaDialog`, `RuleActionDialog`) with all 10 action types parameterized and predefined-field/custom-header criteria; per-item **remove**; action **move up/down**; **AND/OR** match mode (`UseAND`). Reusable `RulesView` also powers the **account Rules tab**. Live-validated end-to-end. |
| 4 | ✅ **Route Addresses tab** (commit 88ae5bf) | `Route.Addresses` | AllAddresses toggle + per-address list editor (Add/Remove, persists via `Addresses.Add()/Save()/DeleteByDBID`). Live-validated. |
| 5 | ✅ **Status page** (commit 9aff58c) | `Application.Status/Version/ServerState/Database` | New `StatusView`: server (version+arch, state, started, uptime), database, statistics (processed/spam/virus + SMTP/IMAP/POP3 sessions), and the ucStatus configuration **warnings**. Live-validated incl. warning badges. |
| 6 | ✅ **TOTP 2FA login** (commit e9e5051) | `Services/Totp.cs`, `TotpSetupDialog`, `TotpPromptDialog` | RFC 6238 setup + login prompt gate in `OnConnected`. Reads the same HKLM `AdminTotpSecret` (machine-scope DPAPI via crypt32) as Administrator, so existing 2FA carries over. Live-validated end-to-end. |
| 7 | ⏸ **Active Directory pickers + Import members** (deferred — see Part 1) | port `formActiveDirectoryAccounts`, `formSelectUsers`, `formUserAccounts`, `formImportMembers` | Deferred: the dev/test machine is in a WORKGROUP and the AD packages are unavailable offline. Manual AD linkage (ADDomain/ADUsername) already works; only the *browse* picker is outstanding. |
| 8 | ✅ **Message viewer** (commit 4cf4bac) | `MessageViewerDialog` | "View source" / double-click on a queued message shows the raw `.eml` read from disk, with Copy. Friendly message if the file is gone. Live-validated. |
| 9 | ✅ **DMARC failure score** (commit 9743096) | `AntiSpam.DMARCFailureScore` | Field added to the AntiSpam section. |
| 10 | ◑ **Admin actions** (greylisting + logon-failure clear done, commit 9743096; IP-range bulk SetDefault deferred — destructive) | `ClearGreyListingTriplets`, `ClearLogonFailureList`, IP-range `SetDefault` | Surfaced as buttons. |

**Status: 9 of 10 items done** (1–6, 8, 9 complete and released; 10 partial — the greylisting +
logon-failure clears shipped, the destructive IP-range bulk `SetDefault` is intentionally left out; 7
deferred for AD-environment reasons). Every completed item was build-clean (`-warnaserror`),
live-validated via screenshots/COM round-trips, and shipped in the `v6.2.0` installer.
