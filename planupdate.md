# hMailServer Modernization — Master Plan & Roadmap

*Generated 2026-06-13. This is the single authoritative plan. It covers two
tracks run as one ordered program:*

- **Track A — Control Panel becomes the sole admin GUI** (parity with the classic
  WinForms Administrator, drop the classic from the installer, AV/security
  extensibility, full UX/UI pass).
- **Track B — Server world-class hardening** (security defects, auth/secrets
  modernization, standards & deliverability, operability, CI/fuzzing).

*Scope: Tier 1 defects + Tier 2 harden-in-place now; Tier 3 platform expansion
(Linux/JMAP/HA) is a documented future track only. Every new server capability is
surfaced in the Control Panel.*

---

## Master execution sequence (the chosen order)

Risk/value-ordered. Security defects first, locked by tests/CI, then finish the
Control Panel as the sole GUI, then deepen hardening. Each step ends with: clean
build, run the regression suite, (CP steps) screenshot-validate, then commit/push
+ move tag `v6.2.0` + clobber the release asset.

1. **B1 — security & correctness defects + secure defaults.** ✅ **Done** (see Progress).
2. **B8 (core) — CI + fuzzing.** ✅ **Done.** GitHub Actions: `ci.yml` (Control Panel build,
   warnings-as-errors) + `codeql.yml` (C# static analysis) on hosted runners; `server-build.yml`
   (native C++ build on a self-hosted VS 2026/v145 runner + opt-in regression-suite run). Plus
   reproducer regression tests for the B1 defects and an over-the-wire protocol fuzz suite for the
   SMTP/IMAP/MIME parsers. (Coverage-guided libFuzzer is impractical in this environment — MSVC/ATL-
   coupled parsers, no fuzzer runtime — so the live over-the-wire fuzzer is the validated substitute.)
3. **Track A Ph 0–1 — drop classic from installer + Control-Panel functional parity.** ⏳ **In progress** — Phase 1 is 9/10 (item 7 AD pickers deferred to a domain-joined runner); Phase 0 (drop the classic) follows once item 7 lands. CP becomes the sole shipped GUI.
4. **B3 — secrets & least-privilege** (DPAPI for INI/DB secrets; non-LocalSystem service).
5. **B2 — auth modernization** (OAuth2 XOAUTH2/OAUTHBEARER, SCRAM-SHA-256, Argon2id + hash policy).
6. **Track A Ph 2 — Control-Panel UX/UI polish.**
7. **B4 — deliverability & SMTP standards** (SMTPUTF8/EAI, SRS, PIPELINING/DSN, rate shaping).
8. **B5 — IMAP modern sync profile** (CONDSTORE/QRESYNC/UIDPLUS/ENABLE/ESEARCH/STATUS=SIZE). ✅ **Done** — UNSELECT, UIDPLUS, ENABLE, STATUS=SIZE, ESEARCH, CONDSTORE/QRESYNC (Stages 1–3b), LIST-EXTENDED and SEARCHRES all shipped in `v6.2.0`. Full IMAP regression suite green (242/242). IMAP4rev2 (RFC 9051) assessed and deferred to its own milestone (see B5).
9. **Track A Ph 3 — AV/security extensibility + INI hardening knobs.**
10. **B7 — operability & observability** (OpenTelemetry, health probes, DB pool/executor, durability, HA runbook).
11. **B6 — Sieve + ManageSieve.**
12. **Track A Ph 4 / ongoing** — finalize gap doc, release cadence. Then the future track (Tier 3).

---

## Implementation progress

Execution follows the agreed master sequence (security defects first, locked by
tests, then Control-Panel parity, then deeper hardening). Status below is updated
as work lands.

### ✅ Done — Server security & correctness defects (Track B, phase B1)

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

### ⏳ Next

- ✅ **B8 — CI + fuzzing** *(done)*: `.github/workflows/ci.yml` (Control Panel build,
  warnings-as-errors) and `codeql.yml` (C# static analysis) on hosted runners; reproducer tests
  `TestAppendOversizedLiteralRejected` / `TestOversizedCommandLiteralRejected` (guard the B1
  IMAP fixes; pass 2/2); an **over-the-wire protocol fuzz suite** (`Security/ProtocolFuzz.cs`,
  commit `fc6d1da`): seeded malformed-input barrage against the live SMTP/IMAP command parsers and
  the inbound MIME parser, asserting the server never crashes/hangs/logs a fault (layered detection:
  liveness check + `ServiceRestartDetector` + `AssertNoReportedError`, per-test `[Timeout]`); 3/3 pass
  (~231 s); and a native **server-build workflow** (`server-build.yml`, commit `6b692a8`) for a
  self-hosted VS 2026/v145 runner that compiles the C++ server (build step locally proven via
  `build/build.ps1`) with an opt-in regression-suite run (reuses `build-tests.ps1`/`post-build.ps1`/
  `run-tests.ps1`). Coverage-guided libFuzzer was assessed and is impractical here (no fuzzer runtime
  in the available clang; parsers are MSVC/ATL/Windows-coupled) — the live fuzzer is the validated
  substitute.
- ✅ Modern default TLS + password hashing — **assessed: already delivered in the 6.0 modernization.**
  TLS defaults to 1.2+1.3 only (`SslVersions=24`, SSLv2/3 always off, modern EC curves
  `secp384r1:x25519:secp256r1`) — no RC4/legacy-protocol exposure; passwords default to PBKDF2
  (`PreferredHashAlgorithm=4`), COM `put_Password` and the REST API both hash new passwords with
  PBKDF2, and logins transparently re-hash MD5/SHA256 → PBKDF2. Remaining *optional* hardening
  (lower priority): an explicit AEAD-only cipher-list default (client-interop trade-off) and upgrading
  the management/admin INI password from MD5.
- ⬜ **Next phase:** finish Track A Phase 1 item 7 (AD pickers, needs a domain-joined runner) →
  Phase 0 (drop the classic from the installer) → Track B **B3** (DPAPI for INI/DB secrets +
  non-LocalSystem service), then **B2** (OAuth2/SCRAM/Argon2) and the rest of the master sequence.

The full two-track roadmap is below: **Track A** (Control Panel) then **Track B**
(server hardening B2–B8) then the future track.

---

# TRACK A — Control Panel becomes the sole admin GUI

## Guiding decisions

- **Classic removal = installer only.** `source/Tools/Administrator` and the
  Tools solution stay; only the installer stops shipping the Administrator.
  DBSetup, DBUpdater and DataDirectorySynchronizer are retained.
- **AV/security extensibility = Control-Panel only.** Configure external
  scanners with presets + Test buttons and surface the event-script
  integration hooks. No server-side (C++) plugin API in this pass.
- **TOTP 2FA is ported** from the classic to the Control Panel.
- **Order:** parity-to-retire-classic → UX/UI polish → extensibility/hardening
  → release.

---

## Phase 0 — Drop the classic from the installer

1. Remove the Administrator executable/DLLs from `section_files_64.iss` and
   `section_files_common.iss`.
2. Remove its Start-menu shortcut from `section_icons.iss`; replace the
   end-of-setup "run Administrator" with "run Control Panel" in
   `section_run.iss`; clean `section_uninstallrun.iss` / `section_components.iss`
   if referenced.
3. Keep DBSetup / DBUpdater / DataDirectorySynchronizer.
4. Verify ISCC builds and the post-install database step still runs.

---

## Phase 1 — Functional parity (so the classic can be retired)

| # | Item | COM / source | Notes |
|---|---|---|---|
| 1 | ✅ **IP-range full policy editor** (done, commit 9743096) | `IInterfaceSecurityRange` | Tabbed `IPRangeDialog` (General/Connections/Relaying/Require auth/Protection): RequireSMTPAuth per direction, EnableSpamProtection, EnableAntiVirus, Expires+ExpiresTime, Priority. Wired via Properties button + double-click. Live-validated. |
| 2 | ✅ **TCP/IP port → SSL certificate binding** (done, commit 9743096) | `IInterfaceTCPIPPort.SSLCertificateID` | `TcpIpPortDialog` picks from `Settings.SSLCertificates`; Certificate column added to grid. Live-validated. |
| 3 | ✅ **Rules editor parity** (done, commits 139bd91 + fd3a27b account-level) | `IInterfaceRule/RuleCriteria/RuleActions` | Selectable criteria/action grids; per-item **edit** dialogs (`RuleCriteriaDialog`, `RuleActionDialog`) with all 10 action types parameterized (delete, forward+abort-spam, reply, move-to-folder, run-script, stop, set-header, send-using-route, create-copy, bind-to-IP) and predefined-field/custom-header criteria; per-item **remove**; action **move up/down**; **AND/OR** match mode (`UseAND`). Reusable `RulesView` (rule-source provider) now also powers the **account Rules tab** (hides server-only route/bind actions). Live-validated end-to-end (global + account). |
| 4 | ✅ **Route Addresses tab** (done, commit 88ae5bf) | `Route.Addresses` | AllAddresses toggle (already present) + per-address list editor (Add/Remove, persists via `Addresses.Add()/Save()/DeleteByDBID`). Live-validated end-to-end. |
| 5 | ✅ **Status page** (done, commit 9aff58c) | `Application.Status/Version/ServerState/Database` | New `StatusView` (nav: Status → Server status): server (version+arch, state, started, uptime), database (type/host/name/schema), statistics (processed/spam/virus + SMTP/IMAP/POP3 sessions), and the ucStatus configuration **warnings** (no host name, deny-null sender, external→external relay without auth, localhost banned, auto-ban count). Live-validated incl. warning badges. |
| 6 | ✅ **TOTP 2FA login** (done, commit e9e5051) | `Services/Totp.cs`, `TotpSetupDialog`, `TotpPromptDialog` | RFC 6238 setup (secret + otpauth URI) + login prompt gate in `OnConnected`. Reads the same HKLM `AdminTotpSecret` (machine-scope DPAPI via crypt32) as Administrator, so existing 2FA carries over. Live-validated end-to-end (DPAPI round-trip, prompt, code acceptance → dashboard). |
| 7 | ⏸ **Active Directory pickers + Import members** (deferred — needs a domain-joined runner) | port `formActiveDirectoryAccounts`, `formSelectUsers`, `formUserAccounts`, `formImportMembers` | Browse/import AD accounts into the Account Directory tab; import members for groups/dist-lists. **Deferred:** the dev/test machine is in a WORKGROUP and the `System.DirectoryServices`/`AccountManagement` packages are not available offline, so the AD-query path cannot be built or validated here. The Directory tab already supports manual AD linkage (ADDomain/ADUsername); only the *browse* picker convenience is outstanding. |
| 8 | ✅ **Message viewer** (done, commit 4cf4bac) | `MessageViewerDialog` | "View source" / double-click on a queued message shows the raw `.eml` (headers + body) read from disk (`Status.UndeliveredMessages` file column), with Copy. Friendly message if the file is gone/inaccessible. Live-validated. |
| 9 | ✅ **DMARC failure score** (done, commit 9743096) | `AntiSpam.DMARCFailureScore` | Field added to the AntiSpam section. |
| 10 | ◑ **Admin actions** (greylisting + logon-failure clear done, commit 9743096; IP-range bulk SetDefault deferred — destructive) | `ClearGreyListingTriplets`, `ClearLogonFailureList`, IP-range `SetDefault` | Surface as buttons. |

*Parallelizable:* items 1, 2, 4, 9 are small and independent. Items 3, 5, 6, 7
are larger.

**Status: 9 of 10 items done** (1–6, 8, 9 complete and released; 10 partial — the
greylisting + logon-failure clears shipped, the destructive IP-range bulk
`SetDefault` is intentionally left out; 7 deferred for AD-environment reasons).
Every completed item was build-clean (`-warnaserror`), live-validated via
screenshots/COM round-trips, and shipped in the `v6.2.0` installer. Once item 7
lands (on a domain-joined runner), **Phase 0** can drop the classic Administrator
from the installer.

---

## Phase 2 — UX/UI polish (after parity)

1. **Reload-on-enter** for cached pages (`FeatureSettingsView.OnEnter` is empty;
   ensure every settings page refreshes on navigation).
2. **No silent `catch{}`** — `DomainsView` alias/dist-list loads must show real
   empty/error states.
3. **Search/filter** on long lists: Domains, Accounts, Rules, Queue, Ports,
   Certs, IP ranges, Routes.
4. **Consistent destructive-action confirmations** (aliases, dist-list
   recipients, incoming relays currently skip them).
5. **Input validation + inline feedback** in Domain/Account/Route dialogs
   (bad numeric input is silently ignored today).
6. **Standardized loading/empty/error states** (shared status-line pattern).
7. **Accessibility** — AutomationProperties.Name + access keys + tab order on
   all dialogs/pages.
8. **Theme** — follow the OS theme by default (currently hard dark); keep the
   manual toggle.
9. **Navigation restructure** — split the overloaded **Advanced** group into
   Security / Network / Maintenance; add a top-level **Status** node.
10. **Style unification** — reconcile code-built pages (PublicFoldersView,
    UtilityViews, dialogs) with the XAML card pages; prefer Wpf.Ui controls and
    shared card/list styles.
11. **Global exception handler** — log to file and offer restart instead of
    silently marking handled.
12. **Responsiveness** — relax fixed widths (MainWindow 980×640 min, 248 px nav,
    ConnectView) for small/zoomed/RDP sessions.

---

## Phase 3 — AV + security extensibility & hardening (CP-only)

1. **Scanner presets + Test buttons** — `TestClamAVScanner`,
   `TestClamWinScanner`, `TestCustomScanner` plus a Custom-scanner **preset
   picker** that fills Executable / ReturnValue / args for common engines
   (Windows Defender `MpCmdRun`, Sophos, ESET, Bitdefender, Kaspersky CLI) and
   a ClamWin path auto-detect.
2. **Event-script integration hooks** — script templates in `ScriptsView`
   (OnAcceptMessage → AV/DLP/SIEM/webhook/external-API), with a pointer from the
   AntiVirus page for non-CLI engines.
3. **Advanced hardening card** (INI-backed) — surface the unexposed
   `IniFileSettings` knobs: `GreylistingEnabledDuringRecordExpiration`,
   `GreylistingRecordExpirationInterval`, `PreferredHashAlgorithm`,
   `DNSBLChecksAfterMailFrom`, `SAMinTimeout`/`SAMaxTimeout`,
   `ClamMinTimeout`/`ClamMaxTimeout`, `BlockedIPHoldSeconds`, `UseDNSCache`,
   `DNSServer`, `AuthUserReplacementIP`, `RewriteEnvelopeFromWhenForwarding`,
   `DisableAUTHList`, `AddXAuthUserHeader`/`AddXAuthUserIP`, `AddXOriginalRcptTo`.
4. **Account password-strength validation** before save.

---

## Phase 4 — Finalize

1. Build (save-all to disk first), then screenshot-validate every new/changed
   page and dialog with `build/capture-cp.ps1` against `hmailtest2`.
2. Rewrite `CONTROL-PANEL-GAP-ANALYSIS.md` as the authoritative
   parity + UX + security matrix (classic node → CP page → status).
3. Publish the CP, rebuild the installer (no Administrator), commit/push, move
   tag `v6.2.0`, clobber the release asset.

---

## Key files

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

---

## Verification

- Per phase: `dotnet build` clean (0/0); launch via `build/capture-cp.ps1 -Launch`;
  screenshot each changed page/dialog; confirm load+save round-trips against
  `hmailtest2` (MariaDB root `tester`; CP `/connect localhost Administrator testar`).
- Installer: ISCC builds; a clean install shows no Administrator shortcut, the
  Control Panel present, and DB tools intact.
- 2FA: setup → reconnect requires TOTP; an incorrect code is rejected.

---

## Open considerations

1. **Localization** — the classic supports translations; the CP is English-only.
   Recommended as a later roadmap item, not this pass.
2. **Active-session management** — Status can show live session *counts* now;
   *disconnecting* sessions likely needs server support. Recommend view-only.
3. **Removing Administrator from the Tools `.sln`** — defer until CP parity is
   proven in production.

---

# TRACK B — Server world-class hardening (Tiers 1–2; harden-in-place)

*Evidence from four code deep-dives (auth/crypto, protocol standards,
architecture/operability, correctness bug-hunt). Already verified strong (do not
redo): PBKDF2-HMAC-SHA256 (210k iters, transparent rehash-on-login), TLS 1.2/1.3
defaults, DANE+DNSSEC outbound, ARC, Ed25519 DKIM, MTA-STS, TLS-RPT, auto-ban,
correct dot-stuffing, parameterized SQL.*

## B1 — Protocol correctness & DoS hardening ✅ DONE
Delivered and validated — see **Implementation progress** above (commits `6f7e019`,
`53ec538`, `9f3a51e`; IMAP 215/215 + SMTP 175/175).
Deferred from B1 (do with the TLS/auth work, higher regression risk): modern default
TLS cipher list (drop RC4/legacy CBC) and MD5-hash-accept deprecation.

**Update (2026-06-13):** the per-connection IMAP/POP3 AUTH cap is done (commit
`b8a3829`) with regression coverage (commit `4dae4b1`; IMAP+POP3 267/267). Review of the
remaining two found them already satisfied by the 6.0 modernization: **TLS** defaults to
1.2+1.3 only (`SslVersions=24`) with SSLv2/3 always off and modern EC curves
(`secp384r1:x25519:secp256r1`) — no RC4/legacy-protocol exposure; **passwords** default to
PBKDF2 (`PreferredHashAlgorithm=4`), COM `put_Password` and the REST API both hash new
passwords with PBKDF2, and logins transparently re-hash MD5/SHA256 → PBKDF2. Remaining
optional hardening: an explicit AEAD-only cipher list default (interop trade-off) and
upgrading the management/admin INI password from MD5.

## B2 — Authentication modernization
- OAuth2 SASL **XOAUTH2 + OAUTHBEARER** (IMAP / SMTP submission / POP3); token validation
  via JWKS/introspection. Today only AUTH LOGIN/PLAIN (`SMTPConnection`, `IMAPCommandAuthenticate`,
  outbound `SMTPClientConnection`).
- **SCRAM-SHA-256** (+ `-PLUS` channel binding).
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
  plus the full IMAP regression suite. Follow-ups: SCRAM-SHA-256-**PLUS** (TLS channel binding),
  full SASLprep of non-ASCII credentials, and deterministic anti-enumeration salts.
- ✅ **SCRAM-SHA-256 SASL mechanism (SMTP submission) — delivered in v6.2.0.** Extended the same
  mechanism to SMTP `AUTH` (RFC 4954 SASL framing), reusing the `Common/Util/Hashing/ScramSha256`
  helper. Per-connection SASL state lives on `SMTPConnection` (`scram_session_`); the multi-step
  exchange is routed by three new connection states (`SMTPSCRAMFIRST`/`SMTPSCRAMFINAL`/`SMTPSCRAMACK`)
  with each base64 SASL message carried over `334` continuations and completion signalled with `235`
  (server-final `v=...` sent as a `334`, acknowledged by an empty client line). Honours SASL-IR
  (`AUTH SCRAM-SHA-256 <base64>`), `*` cancellation, the per-IP auto-ban
  (`AccountLogon::RegisterFailedLogin`, shared with LOGIN/PLAIN) and the per-connection brute-force
  cap; unknown / non-PBKDF2 accounts run
  the same forced-failure exchange. Advertised in EHLO (`AUTH ... SCRAM-SHA-256`) whenever AUTH is
  enabled, independent of the plain-text setting. The `OnClientLogon` script event was refactored into
  a shared `FireOnClientLogon_` so the SCRAM success path fires it exactly like the password path.
  Validated with an over-the-wire C# SMTP SCRAM client (`TestScramSha256Authenticates`,
  `TestScramSha256WrongPasswordFails`, `TestScramSha256Advertised`) plus the full SMTP regression
  suite. Follow-ups: SCRAM over POP3 (POP3 has no AUTH/SASL command today) and SCRAM-SHA-256-**PLUS**.
- ✅ **SASL AUTH for POP3 — PLAIN + SCRAM-SHA-256 — delivered in v6.2.0.** POP3 previously had only the
  legacy `USER`/`PASS` login; added the RFC 5034 `AUTH` command supporting `PLAIN` and
  `SCRAM-SHA-256` (RFC 5802 / RFC 7677), reusing the `Common/Util/Hashing/ScramSha256` helper. POP3 is
  command-dispatched (not a per-line auth state machine), so SASL continuation lines are routed at the
  top of `InternalParseData` before command parsing: a `scram_session_` (per-connection `ScramSha256`)
  or a `sasl_plain_pending_` flag consumes the next line(s) as base64 SASL data, exchanged over `+ `
  continuations; the SCRAM server-final (`v=...`) is sent as a `+ ` challenge acknowledged by an empty
  client line, then completion locks the mailbox and enters TRANSACTION exactly like `PASS`. Honours
  SASL-IR and `*` cancellation. `AUTH` with no argument lists the mechanisms; `CAPA` advertises
  `SASL PLAIN SCRAM-SHA-256` (gated on TLS like `USER`). Unknown / non-PBKDF2 accounts run the same
  forced-failure SCRAM exchange (anti-enumeration); SCRAM failures feed `AccountLogon::RegisterFailedLogin`
  (auto-ban) and the per-connection brute-force cap. The post-login success tail and the `OnClientLogon`
  script event were refactored into shared `HandleSuccessfulLogin_` / `FinishPasswordLogin_` /
  `FireOnClientLogon_` used by both `PASS` and the SASL paths; the PLAIN response is masked in the POP3
  log. Validated with an over-the-wire C# POP3 SASL client (`TestSaslAdvertised`,
  `TestAuthPlainAuthenticates`, `TestScramSha256Authenticates`, `TestScramSha256WrongPasswordFails`)
  plus the full POP3 regression suite. This completes SCRAM-SHA-256 across IMAP, SMTP and POP3.
- ✅ **Argon2id KDF option — delivered in v6.2.0.** Added the OWASP-recommended memory-hard KDF as
  password-hash algorithm **5** (`Crypt::ETArgon2id`), implemented in `HashCreator`
  (`GenerateArgon2id`/`ValidateArgon2id`/`IsArgon2idHash`) over OpenSSL's `EVP_KDF` `ARGON2ID`
  (no new dependency; default params m=19456 KiB, t=2, p=1; self-describing
  `$a2$<m>$<t>$<p>$<salt-hex>$<key-hex>` hash with defense-in-depth bounds on verify). `Crypt`
  (`EnCrypt`/`Validate`/`GetHashType`) dispatches it, and `PasswordValidator`'s transparent
  rehash-on-login was generalised to upgrade to whichever strong KDF is configured in
  `PreferredHashAlgorithm` **without ever downgrading** (PBKDF2 `<` Argon2id by enum value; both
  outrank legacy MD5/SHA256). PBKDF2 remains the default; Argon2id is opt-in via
  `PreferredHashAlgorithm=5`. Validated end-to-end inside the live server by the `RunTestSuite`
  self-tests (`HashCreatorTester` Argon2id round-trip/negative/salt-uniqueness/cross-scheme checks +
  a `Crypt` `EnCrypt`→`GetHashType`→`Validate` dispatch check for Argon2id and PBKDF2), with the
  full auth regression (default PBKDF2 path) green.
- Remaining B2: SCRAM-SHA-256-`PLUS` (channel binding); a hash-policy engine (min accepted type,
  phase out MD5/SHA256) and optional pepper building on the Argon2id work; OAuth2 XOAUTH2/OAUTHBEARER;
  POP3/IMAP UTF8 (RFC 6856 / UTF8=ACCEPT) and full SASLprep of non-ASCII credentials.
- Verify: O365/Gmail XOAUTH2 + Thunderbird SCRAM interop.

## B3 — Secrets & least-privilege
- **DPAPI** envelope encryption (machine/service-SID) for the DB password in `hMailServer.INI`
  (`IniFileSettings`) and the DB-stored reversible Blowfish secrets — route (`PersistentRoute`),
  fetch (`PersistentFetchAccount`), relayer (`Property`). External-secret-provider abstraction.
- Run the service as a **least-privileged virtual account** (not LocalSystem; `ServiceManager`),
  with explicit ACLs + privilege drop.
- Verify: secrets not reversible off-box; service runs non-SYSTEM.

## B4 — Deliverability & SMTP standards
- **SMTPUTF8 / EAI** (RFC 6531/6532) end-to-end (parser, validator currently ASCII-only, storage, delivery).
- **SRS** for forwarding (SPF alignment) replacing the naive envelope rewrite (`SMTPForwarding`); optional BATV.
- PIPELINING, ENHANCEDSTATUSCODES, DSN (RFC 3461; RCPT rejects ext params today); optional CHUNKING/BDAT.
  Per-IP / per-destination submission + outbound rate shaping.
- Verify: EAI roundtrip; SPF passes on forwarded mail; DSN interop.

## B5 — IMAP modern sync profile
- ✅ **UNSELECT (RFC 3691)** — delivered in v6.2.0. Closes the selected mailbox without the
  implicit EXPUNGE that CLOSE performs (\Deleted retained). Advertised in CAPABILITY. Covered by
  `RegressionTests.IMAP.Basics.TestUnselectKeepsDeletedMessages`.
- ✅ **UIDPLUS (RFC 4315)** — delivered in v6.2.0. APPEND/COPY/MOVE return `[APPENDUID]`/`[COPYUID]`
  response codes (destination UIDVALIDITY + assigned UIDs) and `UID EXPUNGE` removes only \Deleted
  messages whose UID is in the supplied set. Advertised in CAPABILITY. Covered by
  `TestAppendReturnsAppendUid`, `TestCopyReturnsCopyUid`, `TestUidExpungeOnlyRemovesMatchingUids`.
- ✅ **ENABLE (RFC 5161)** — delivered in v6.2.0. Negotiates opt-in extensions; returns a tagged OK
  (no enable-able extensions yet, so the untagged `ENABLED` line is omitted). Advertised in
  CAPABILITY. Covered by `TestEnableReturnsOk`.
- ✅ **STATUS=SIZE (RFC 8438)** — delivered in v6.2.0. `STATUS` answers the `SIZE` attribute (total
  mailbox size in octets). Advertised in CAPABILITY. Covered by `TestStatusReturnsMailboxSize`.
- ✅ **ESEARCH (RFC 4731)** — delivered in v6.2.0. `SEARCH RETURN (MIN MAX ALL COUNT)` emits the
  `* ESEARCH` response. Advertised in CAPABILITY. Covered by `TestEsearchReturnsExtendedResponse`.
- ◑ **CONDSTORE/QRESYNC (RFC 7162) — Stage 1 (CONDSTORE foundation) DELIVERED in v6.2.0.** This is
  the one B5 item that requires a **database schema migration**, so it is shipped in supervised
  stages.
  - **Stage 1 (done):** persistent mod-sequence storage + read surface. Bumped
    `REQUIRED_DB_VERSION` 6001→6002 (`Common/Application/Constants.h`); added a per-message
    `messagemodseq` column to `hm_messages` and a per-folder monotonic `foldercurrentmodseq`
    counter to `hm_imapfolders` across all four `CreateTables*` scripts; wrote
    `Upgrade6001to6002{MSSQL,MSSQLCE,MySQL,PGSQL}.sql` (each ALTERs both columns `DEFAULT 1` and
    sets `hm_dbversion=6002`). The mod-sequence is a per-mailbox monotonic counter (mirrors the UID
    counter): assigned on message arrival and bumped on every flag change (`SaveFlags`), so it only
    ever increases. Command surface: `CAPABILITY` advertises `CONDSTORE QRESYNC`; `ENABLE
    CONDSTORE`/`ENABLE QRESYNC` echo `* ENABLED …`; `SELECT`/`EXAMINE (CONDSTORE)` and
    `SELECT`/`EXAMINE` after `ENABLE CONDSTORE` emit `* OK [HIGHESTMODSEQ n]`; `STATUS (HIGHESTMODSEQ)`
    answers the attribute; `FETCH … (MODSEQ)` returns the per-message `MODSEQ (n)`. Covered by
    `TestEnableCondstoreEchoesEnabled`, `TestSelectAndStatusReportHighestModSeq`,
    `TestFetchModSeqIncrementsOnFlagChange`. MySQL/MariaDB migration validated end-to-end against the
    live regression DB (316/316 IMAP+persistence tests green); MSSQL/PGSQL/SQLCE scripts authored to
    match but await cross-backend upgrade testing.
  - **Stage 2 (done in v6.2.0):** conditional-store semantics. `FETCH … (CHANGEDSINCE n)` returns
    only messages whose mod-sequence exceeds `n` and implicitly includes `MODSEQ`; `STORE …
    (UNCHANGEDSINCE n) …` leaves any message changed since `n` untouched and lists it in a tagged
    `[MODIFIED <set>]` response code; a satisfied/CONDSTORE `STORE` returns the new `MODSEQ` in its
    untagged `FETCH` (even when `.SILENT`); and `SEARCH MODSEQ n` matches messages with
    mod-sequence ≥ `n`, appending the highest matched value as `(MODSEQ n)` (and `MODSEQ n` in an
    `ESEARCH` reply). Covered by `TestFetchChangedSinceFiltersByModSeq`,
    `TestStoreUnchangedSinceRejectsModified`, `TestStoreUnchangedSinceSucceedsAndReturnsModSeq` and
    `TestSearchModSeqReportsHighest`.
  - **Stage 3a (done in v6.2.0, QRESYNC in-session):** `SELECT`/`EXAMINE (QRESYNC (uidvalidity
    modseq …))` is parsed, enabling QRESYNC+CONDSTORE for the session and replaying flag/`MODSEQ`
    changes since the supplied mod-sequence as untagged `FETCH (UID … FLAGS … MODSEQ …)` responses;
    `EXPUNGE` and `UID EXPUNGE` emit a single `* VANISHED <uid-set>` (compressed sequence-set) when
    QRESYNC is enabled instead of per-message `* n EXPUNGE` lines. Covered by
    `TestExpungeWithQResyncReturnsVanished` and `TestSelectQResyncReplaysChanges`.
  - **Stage 3b (done in v6.2.0, QRESYNC offline tracking):** persistent expunged-UID tombstones
    (new `hm_imapexpunged` table, DB 6002→6003) recorded at the universal
    `PersistentMessage::DeleteObject` chokepoint (covers IMAP `EXPUNGE`/`UID EXPUNGE`, `CLOSE`,
    `MOVE` source-delete and POP3 delete; guarded to account/folder-scoped messages), each bumping
    the folder mod-sequence. `SELECT`/`EXAMINE (QRESYNC (uidvalidity modseq …))` now emits
    `* VANISHED (EARLIER) <uid-set>` for UIDs expunged since the supplied mod-sequence, and
    `UID FETCH <set> (CHANGEDSINCE n VANISHED)` emits `* VANISHED (EARLIER)` for the requested UIDs
    expunged since `n`. Tombstones are pruned when a folder is deleted. Covered by
    `TestSelectQResyncReportsVanishedEarlier` and `TestUidFetchVanishedReportsEarlier`. Migration
    scripts shipped for MySQL/MSSQL/PGSQL/SQLCE; only the MySQL path is validated in CI here.
    Follow-up: tombstone pruning/retention beyond folder deletion is not yet implemented (RFC 7162
    permits the server to fall back to a full resync, which the client handles).
- ✅ **LIST-EXTENDED (RFC 5258)** — delivered in v6.2.0. `LIST` accepts an optional leading
  selection-option list (`(SUBSCRIBED)` returns only subscribed mailboxes as `* LIST`; `REMOTE`/
  `RECURSIVEMATCH` are accepted as no-ops), an optional trailing `RETURN (SUBSCRIBED CHILDREN)`
  (annotates the `\Subscribed` attribute; `\HasChildren`/`\HasNoChildren` are always reported), and a
  parenthesised list of mailbox patterns (each mailbox listed once). Advertised in CAPABILITY as
  `LIST-EXTENDED`. Covered by `TestListExtendedReturnSubscribed`, `TestListExtendedSelectSubscribed`
  and `TestListExtendedMultiplePatterns`.
- ✅ **SEARCHRES (RFC 5182)** — delivered in v6.2.0. `SEARCH RETURN (SAVE)` (UID and sequence
  variants) saves the matched messages for the session; the `$` marker then references that result in
  a subsequent `FETCH`/`STORE`/`COPY`/`MOVE`/`UID EXPUNGE`. The result is stored as UIDs on the
  connection so it stays stable across expunges; `$` is expanded centrally in
  `IMAPCommandRangeAction::DoForMails` (mapped to sequence numbers for non-UID commands) and in
  `UID EXPUNGE`. When `SAVE` is combined only with `MIN`/`MAX`, just those extremes are saved.
  Advertised in CAPABILITY as `SEARCHRES`. Covered by `TestSearchResSaveAndFetch`,
  `TestSearchResSaveAndStore` and `TestSearchResCapability`. Follow-up: `$` inside `SEARCH` criteria
  (set intersection) is not yet supported.
- ⏸ **IMAP4rev2 (RFC 9051) — assessed and deferred to its own milestone.** hMailServer already
  implements the individual extensions IMAP4rev2 folds into the base (UIDPLUS, ENABLE, IDLE,
  NAMESPACE, MOVE, SPECIAL-USE, UNSELECT, ESEARCH, SEARCHRES, STATUS=SIZE, LIST-EXTENDED, SASL-IR,
  CONDSTORE), so the building blocks are in place. Full RFC 9051 conformance is **not** a single safe
  increment, however: advertising `IMAP4rev2` obliges the server to (1) use **UTF-8 mailbox names**
  instead of modified UTF-7 once the session enables rev2 — a session-scoped encode/decode switch
  threaded through every command that carries a mailbox name (`LIST`/`LSUB`/`SELECT`/`EXAMINE`/
  `CREATE`/`RENAME`/`DELETE`/`SUBSCRIBE`/`STATUS`/`APPEND`/`COPY`/`MOVE`, currently all via
  `ModifiedUTF7`); (2) return `SEARCH` results in the **ESEARCH** form by default; (3) drop the
  `\Recent` flag and `RECENT` responses; (4) treat `LSUB` as deprecated in favour of
  `LIST (SUBSCRIBED)`; and (5) audit the changed/added response codes. Shipping a partial rev2 under
  the `IMAP4rev2` capability would be non-conformant and risk client interop, so it is tracked as a
  dedicated future milestone rather than bundled into the `v6.2.0` profile. The `ENABLE` handler and
  `CAPABILITY` are the entry points when that milestone is scheduled.
- ✅ **B5 modern-sync profile complete for v6.2.0.** All targeted extensions delivered and validated
  (full IMAP suite 242/242). Verify in the field: fast resync in Thunderbird/Apple Mail.

## B6 — Standards-based filtering
- **Sieve** (RFC 5228) interpreter + **ManageSieve** (RFC 5804) service alongside the
  proprietary rules engine (`RuleApplier`). Verify with Sieve test vectors + a ManageSieve client.

## B7 — Operability & observability
- **OpenTelemetry** tracing (SMTP/IMAP/POP/DB spans + correlation IDs); unauthenticated local
  health/readiness/liveness probes; richer metrics (queue depth, per-command latency, DB pool
  saturation, TLS handshake failures); log retention/rotation.
- Async/DB isolation: dedicated DB executor; replace the connection-pool `Sleep` polling with
  condition variables (`DatabaseConnectionManager`); prepared-statement caches (MySQL/PG).
- Message-store durability: configurable fsync + consistency checker + recovery tooling.
  Graceful shutdown: readiness/drain + connection draining.
- HA: a documented, tested active/passive (shared DB + storage + VIP) runbook + readiness gating
  (no clustering code in this track).

## B8 — Quality gates & supply chain
**Delivered (core):** GitHub Actions \u2014 `ci.yml` (Control Panel build, warnings-as-errors) and
`codeql.yml` (CodeQL C# SAST) on hosted runners, plus `server-build.yml` (self-hosted native C++
build on a VS 2026/v145 runner + opt-in regression-suite run). B1 reproducer tests and an
over-the-wire SMTP/IMAP/MIME protocol fuzz suite (`Security/ProtocolFuzz.cs`, 3/3) lock in the
parser hardening.
**Remaining (future):**
- build+test matrix Windows \u00d7 MySQL/MSSQL/PostgreSQL running the full suite (today the self-hosted
  workflow runs one DB at a time).
- clang-tidy; ASAN/UBSAN build; coverage-guided **libFuzzer** harnesses (need a clang+fuzzer
  toolchain and decoupled parsers \u2014 impractical in the current MSVC/ATL environment, where the live
  over-the-wire fuzzer is the substitute).
- SBOM + dependency/CVE scanning + signed release artifacts.
- Verify: green-gates-required-to-merge; nightly fuzz.

## Cross-cutting — surface new server capabilities in the Control Panel
OAuth2 provider config, SCRAM/Argon2 policy, SMTPUTF8/SRS/rate-limit toggles, IMAP profile,
Sieve/ManageSieve editor, secrets/least-priv status, health/trace endpoints, AV scanner
presets + tests (Track A Phase 3), a security-diagnostics report.

## Future track (Tier 3 — documented, not scheduled)
Linux/container port (OS-abstraction layer first; today hard-wired to Win32/ATL/registry/service),
JMAP (RFC 8620/8621), CalDAV/CardDAV, native webmail, true clustering/HA, rspamd integration,
BIMI + VMC, OCSP stapling, ARF feedback-loop processing.

## Track B verification
Each phase: clean build; run the regression suite (`build/run-tests.ps1`) plus new
negative/fuzz tests; no `/WX` warnings. Security phases: a reproducer test proves the defect
is closed. Interop phases: test against real clients.