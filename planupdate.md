# hMailServer Control Panel — Migration & Modernization Roadmap

*Generated 2026-06-13. Goal: make the WPF Control Panel (`hMailCP.exe`) the
single administration GUI, retire the classic WinForms Administrator from the
installer, add third-party AV/security extensibility, and complete a full
UX/UI pass.*

---

## Implementation progress

Execution follows the agreed master sequence (security defects first, locked by
tests, then Control-Panel parity, then deeper hardening). Status below is updated
as work lands.

### ✅ Done — Server security & correctness defects (Track B, phase B1)

Validated end-to-end: **IMAP 215/215** and **SMTP 175/175** regression tests pass
(the SMTP suite was re-run a second time with strict line-endings active, also
175/175). Committed as `6f7e019`.

| Fix | File | What changed |
|---|---|---|
| ✅ IMAP literal overflow / unbounded buffer | `IMAP/IMAPConnection.cpp` | `GetLiteralSize_` validates digits, parses 64-bit, rejects overflow, caps command literals to 10 MB (prevents pre-auth memory pinning). |
| ✅ IMAP APPEND overflow + over-write | `IMAP/IMAPCommandAppend.cpp` | Validates octet count, hard 2 GB ceiling even when max size is unlimited, writes only the declared literal length (no message corruption / parser desync). |
| ✅ MIME header over-read | `Common/Mime/Mime.cpp` | `MimeHeader::Load` bounds every read by `nDataSize`; no read past the caller's buffer on an unterminated header. |
| ✅ AV scanner path hijack | `Common/AntiVirus/ClamWinVirusScanner.cpp`, `CustomVirusScanner.cpp` | Quotes the executable path so a spaced path can't be hijacked by `CreateProcess` (unquoted-path resolution). |
| ✅ Listener slow-loris | (REST/Web/Metrics) | Verified already mitigated — 64 KB / fixed-buffer request caps + 5–10 s read deadlines already present. |

### ⏳ In progress / next

- ⏳ Secure-by-default: flip `smtpallowincorrectlineendings` to 0 in the install
  scripts (strict line-endings; SMTP-smuggling hardening). Validated: SMTP suite
  passes 175/175 under strict mode.
- ⬜ Per-connection AUTH attempt cap (complements per-IP auto-ban).
- ⬜ Then: Control-Panel parity track (Phases 0–4 below), then deeper hardening
  (OAuth2/SCRAM, DPAPI secrets, SMTPUTF8, IMAP sync profile, CI + fuzzing).

The full two-track roadmap (server world-class hardening + Control Panel) lives in
the session plan; the Control-Panel phases follow.

---

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
| 1 | **IP-range full policy editor** | `IInterfaceSecurityRange` | Add RequireSMTPAuth per direction (Local→Local/Local→External/External→Local/External→External), EnableSpamProtection, EnableAntiVirus, Expires+ExpiresTime, Priority. Move inline form → tabbed `IPRangeDialog`. *Parallel.* |
| 2 | **TCP/IP port → SSL certificate binding** | `IInterfaceTCPIPPort.SSLCertificateID` | Pick from `Settings.SSLCertificates`. **Security-critical**, currently missing. *Parallel.* |
| 3 | **Rules editor parity** | `IInterfaceRule/RuleCriteria/RuleActions` | Per-row criterion/action **edit** + **move up/down** + Test-matcher + AND/OR; all criteria fields and action types (delete, forward, move-to-folder, reply, run-script, set-header, stop, create-copy, bind-to-IP, send-using-route). Applies to global `RulesView` and the account Rules tab. *Largest item.* |
| 4 | **Route Addresses tab** | `Route.Addresses` | AllAddresses toggle + per-address list editor in `RouteDialog`. *Parallel.* |
| 5 | **Status page** | `Application.Status`, `Application.Version` | New `StatusView`: Server / Status / Delivery-queue tabs (version, DB, uptime, session + spam/virus counters) — full `ucStatus` parity. |
| 6 | **TOTP 2FA login** | port `TwoFactorAuth.cs`, `formTotpSetup`, `formTotpPrompt` | Setup dialog (generate secret, show secret/QR, verify) + prompt in `ConnectView`; reuse classic's registry key so existing 2FA carries over. |
| 7 | **Active Directory pickers + Import members** | port `formActiveDirectoryAccounts`, `formSelectUsers`, `formUserAccounts`, `formImportMembers` | Browse/import AD accounts into the Account Directory tab; import members for groups/dist-lists. |
| 8 | **Message viewer** | `formMessageViewer` parity | "Show message" → raw source/headers from queue/mailbox. |
| 9 | **DMARC failure score** | `AntiSpam.DMARCFailureScore` | Add field to the AntiSpam section. |
| 10 | **Admin actions** | `ClearGreyListingTriplets`, `ClearLogonFailureList`, IP-range `SetDefault` | Surface as buttons. |

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