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
2. **B8 (core) — CI + fuzzing.** ⏳ **In progress** — GitHub Actions added (CP build + CodeQL C#) and
   reproducer regression tests for the B1 defects; remaining: native server-build CI (cached deps +
   DB service running the 898 suite) and libFuzzer harnesses for the SMTP/IMAP/MIME parsers.
3. **Track A Ph 0–1 — drop classic from installer + Control-Panel functional parity.** CP becomes the sole shipped GUI.
4. **B3 — secrets & least-privilege** (DPAPI for INI/DB secrets; non-LocalSystem service).
5. **B2 — auth modernization** (OAuth2 XOAUTH2/OAUTHBEARER, SCRAM-SHA-256, Argon2id + hash policy).
6. **Track A Ph 2 — Control-Panel UX/UI polish.**
7. **B4 — deliverability & SMTP standards** (SMTPUTF8/EAI, SRS, PIPELINING/DSN, rate shaping).
8. **B5 — IMAP modern sync profile** (CONDSTORE/QRESYNC/UIDPLUS/ENABLE/ESEARCH/STATUS=SIZE).
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

Validated end-to-end: **IMAP 215/215** and **SMTP 175/175** regression tests pass
(SMTP re-run under strict line-endings and again with the AUTH cap — both 175/175).
Commits: `6f7e019` (defects), `53ec538` (line-ending default), `9f3a51e` (AUTH cap).

| Fix | File | What changed |
|---|---|---|
| ✅ IMAP literal overflow / unbounded buffer | `IMAP/IMAPConnection.cpp` | `GetLiteralSize_` validates digits, parses 64-bit, rejects overflow, caps command literals to 10 MB (prevents pre-auth memory pinning). |
| ✅ IMAP APPEND overflow + over-write | `IMAP/IMAPCommandAppend.cpp` | Validates octet count, hard 2 GB ceiling even when max size is unlimited, writes only the declared literal length (no message corruption / parser desync). |
| ✅ MIME header over-read | `Common/Mime/Mime.cpp` | `MimeHeader::Load` bounds every read by `nDataSize`; no read past the caller's buffer on an unterminated header. |
| ✅ AV scanner path hijack | `Common/AntiVirus/ClamWinVirusScanner.cpp`, `CustomVirusScanner.cpp` | Quotes the executable path so a spaced path can't be hijacked by `CreateProcess` (unquoted-path resolution). |
| ✅ Listener slow-loris | (REST/Web/Metrics) | Verified already mitigated — 64 KB / fixed-buffer request caps + 5–10 s read deadlines already present. |
| ✅ Secure default: strict SMTP line endings | `DBScripts/CreateTables{MYSQL,MSSQL,PGSQL}.sql` | `smtpallowincorrectlineendings` default 1→0 on fresh installs (SMTP-smuggling hardening). Validated: SMTP suite 175/175 under strict mode. Commit `53ec538`. |
| ✅ Per-connection SMTP AUTH cap | `SMTP/SMTPConnection.cpp/.h` | 10 failed AUTH attempts per connection → 535 + disconnect (defense-in-depth over per-IP auto-ban). Validated: SMTP 175/175. Commit `9f3a51e`. |

### ⏳ Next

- ⏳ **B8 — CI + fuzzing** *(in progress)*: added `.github/workflows/ci.yml` (Control Panel
  build, warnings-as-errors) and `codeql.yml` (C# static analysis), plus reproducer tests
  `TestAppendOversizedLiteralRejected` / `TestOversizedCommandLiteralRejected` (guard the B1
  IMAP fixes; pass 2/2). Remaining: native **server-build CI** (cached OpenSSL/Boost/libpq +
  DB service running the 898 suite) and **libFuzzer harnesses** for the SMTP/IMAP/MIME parsers.
- ⬜ Modern default TLS cipher list (drop RC4/legacy CBC) + MD5-hash-accept deprecation —
  deferred to run with the TLS/auth-modernization work (higher regression risk).
- ⬜ Then: Control-Panel parity track (Phases 0–4 below), then deeper hardening
  (OAuth2/SCRAM, DPAPI secrets, SMTPUTF8, IMAP sync profile).

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
| 3 | ✅ **Rules editor parity** (done, commit 139bd91) | `IInterfaceRule/RuleCriteria/RuleActions` | Selectable criteria/action grids; per-item **edit** dialogs (`RuleCriteriaDialog`, `RuleActionDialog`) with all 10 action types parameterized (delete, forward+abort-spam, reply, move-to-folder, run-script, stop, set-header, send-using-route, create-copy, bind-to-IP) and predefined-field/custom-header criteria; per-item **remove**; action **move up/down**; **AND/OR** match mode (`UseAND`). Live-validated end-to-end. *(Account-level Rules tab still TODO.)* |
| 4 | ✅ **Route Addresses tab** (done, commit 88ae5bf) | `Route.Addresses` | AllAddresses toggle (already present) + per-address list editor (Add/Remove, persists via `Addresses.Add()/Save()/DeleteByDBID`). Live-validated end-to-end. |
| 5 | **Status page** | `Application.Status`, `Application.Version` | New `StatusView`: Server / Status / Delivery-queue tabs (version, DB, uptime, session + spam/virus counters) — full `ucStatus` parity. |
| 6 | ✅ **TOTP 2FA login** (done, commit e9e5051) | `Services/Totp.cs`, `TotpSetupDialog`, `TotpPromptDialog` | RFC 6238 setup (secret + otpauth URI) + login prompt gate in `OnConnected`. Reads the same HKLM `AdminTotpSecret` (machine-scope DPAPI via crypt32) as Administrator, so existing 2FA carries over. Live-validated end-to-end (DPAPI round-trip, prompt, code acceptance → dashboard). |
| 7 | **Active Directory pickers + Import members** | port `formActiveDirectoryAccounts`, `formSelectUsers`, `formUserAccounts`, `formImportMembers` | Browse/import AD accounts into the Account Directory tab; import members for groups/dist-lists. |
| 8 | **Message viewer** | `formMessageViewer` parity | "Show message" → raw source/headers from queue/mailbox. |
| 9 | ✅ **DMARC failure score** (done, commit 9743096) | `AntiSpam.DMARCFailureScore` | Field added to the AntiSpam section. |
| 10 | ◑ **Admin actions** (greylisting + logon-failure clear done, commit 9743096; IP-range bulk SetDefault deferred — destructive) | `ClearGreyListingTriplets`, `ClearLogonFailureList`, IP-range `SetDefault` | Surface as buttons. |

*Parallelizable:* items 1, 2, 4, 9 are small and independent. Items 3, 5, 6, 7
are larger.

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
TLS cipher list (drop RC4/legacy CBC) and MD5-hash-accept deprecation; per-connection
AUTH cap for IMAP/POP3 (they already inherit the per-IP auto-ban).

## B2 — Authentication modernization
- OAuth2 SASL **XOAUTH2 + OAUTHBEARER** (IMAP / SMTP submission / POP3); token validation
  via JWKS/introspection. Today only AUTH LOGIN/PLAIN (`SMTPConnection`, `IMAPCommandAuthenticate`,
  outbound `SMTPClientConnection`).
- **SCRAM-SHA-256** (+ `-PLUS` channel binding).
- **Argon2id** KDF option (keep PBKDF2); hash-policy engine (min accepted type, rehash stale,
  phase out MD5/SHA256); optional pepper. `HashCreator`.
- POP3 SASL + UTF8 (RFC 6856).
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
- CONDSTORE/QRESYNC (7162), UIDPLUS (4315), ENABLE (5161), LIST-EXTENDED/ESEARCH/SEARCHRES,
  STATUS=SIZE; consider IMAP4rev2. (`IMAPCommandCapability` + command map.)
- Verify: fast resync in Thunderbird/Apple Mail.

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

## B8 — Quality gates & supply chain (no CI exists today)
- **GitHub Actions**: build+test matrix Windows × MySQL/MSSQL/PostgreSQL running the full suite.
- SAST (CodeQL + clang-tidy); ASAN/UBSAN build; **fuzz harnesses for SMTP/IMAP/MIME parsers**
  (the B1 defects prove the need). SBOM + dependency/CVE scanning + signed release artifacts.
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