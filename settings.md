# hMailServer Settings — Coverage Map (server config vs Control Panel GUI)

**Date:** 2026-06-15 · **Control Panel:** `hMailCP.exe` (.NET 8 WPF) · **Server:** 6.2.5

This document maps **every configurable setting surface** in hMailServer to the control that
exposes it in the Control Panel, and records the gap analysis that drove the
recently-added settings. It is the reference for "does the GUI cover everything an
administrator configures?".

There are **four** settings surfaces:

| Surface | Source of truth | GUI |
|---|---|---|
| A. Server COM settings | `app.Settings` (+ sub-objects) — [hMailServer.idl](hmailserver/source/Server/hMailServer/hMailServer.idl) | [ServerSettingsView.xaml.cs](hmailserver/source/Tools/ControlPanel/Views/ServerSettingsView.xaml.cs) |
| B. INI `[Settings]` feature switches | [IniFileSettings.cpp](hmailserver/source/Server/Common/Application/IniFileSettings.cpp) | [FeatureSettingsView.xaml.cs](hmailserver/source/Tools/ControlPanel/Views/FeatureSettingsView.xaml.cs) |
| C. Per-object settings | object COM interfaces (Domain, Account, Route, …) | the editor dialogs in [Views/](hmailserver/source/Tools/ControlPanel/Views) |
| D. List/collection settings | COM collections | the collection-editor pages ([CollectionSpecs.cs](hmailserver/source/Tools/ControlPanel/Views/CollectionSpecs.cs)) |

---

## A. Server COM settings (`app.Settings`)

Exposed by `ServerSettingsView`, organised into pages that mirror the classic Administrator:

| Page (`Section`) | COM area | Notes |
|---|---|---|
| Protocols | SMTP/IMAP/POP3 service toggles & basics | ✅ |
| Delivery of e-mail | host name, delivery security, smart host (`SMTPRelayer*`), bounce, retries | ✅ |
| Anti-spam | `AntiSpam.*` thresholds, SPF/DKIM/DMARC/SURBL/DNSBL/greylisting switches | ✅ (lists via D) |
| Anti-virus | `AntiVirus.*` (ClamAV, custom scanner, action) | ✅ |
| Logging | `Logging.*` categories, **Device (file/SQL)**, **LogFormat (default/NCSA)** | ✅ |
| Auto-ban & SSL/TLS | auto-ban, min TLS version, ciphers | ✅ |
| Performance | threads, `Cache.*` TTLs **+ `Cache.*MaxSizeKb` caps**, message indexing | ✅ |
| Advanced & scripting | default domain, IPv6, mirror, admin password, scripting, **UI language** | ✅ |

**Sub-object coverage:** `Settings.AntiSpam`, `Settings.AntiVirus`, `Settings.Logging`,
`Settings.Cache`, `Settings.MessageIndexing` — all writable admin properties are exposed
(the audit found only legacy/obsolete/internal ones missing — see *Excluded* below).

---

## B. INI `[Settings]` feature switches (`hMailServer.INI`)

Exposed by `FeatureSettingsView` on four nav pages. **All 104 INI keys** read by
`IniFileSettings.cpp` are now either exposed here or intentionally excluded.

| Page (`Section`) | Cards |
|---|---|
| Transport security | DANE & DNSSEC, MTA-STS, ARC sealing, TLS reporting |
| Certificates (ACME) | Let's Encrypt issuance/renewal |
| API & monitoring | REST API + Web Control Deck, Web services (MTA-STS hosting/autoconfig), Monitoring (Prometheus + **OpenTelemetry** + **slow-query log** + JSON logging), **OAuth2 / external token auth**, ManageSieve, Operability |
| Advanced hardening | Greylisting, scanner timeouts, DNS, auth/received headers, message-store durability, **connection timeouts**, **delivery & queue tuning**, **logging detail**, **search indexing**, **message archiving**, **SRS/BATV**, **rate limits**, **low-level tuning**, **stored-secret protection** |

Secrets (OAuth2 HMAC, SRS, BATV, password pepper) use a **write-only editor** that never
displays the stored value (it may be a DPAPI blob) and only writes when a new value is typed.

---

## C. Per-object settings (editor dialogs)

| Object | Dialog | COM interface | Status |
|---|---|---|---|
| Domain | `DomainDialog` | `IInterfaceDomain` | ✅ (general, **AD domain link**, limits, signature, DKIM, names) |
| Account | `AccountDialog` | `IInterfaceAccount` | ✅ complete |
| Distribution list | `DistributionListDialog` | `IInterfaceDistributionList` | ✅ complete |
| Route | `RouteDialog` | `IInterfaceRoute` | ✅ (uses the modern connection-security enum) |
| IP / security range | `IPRangeDialog` | `IInterfaceSecurityRange` | ✅ (per-direction auth flags) |
| TCP/IP port | `TcpIpPortDialog` | `IInterfaceTCPIPPort` | ✅ (connection-security enum) |
| Rule criteria | `RuleCriteriaDialog` | `IInterfaceRuleCriteria` | ✅ complete |
| Rule action | `RuleActionDialog` | `IInterfaceRuleAction` | ✅ (all 10 action types) |

---

## D. List / collection settings

Managed by dedicated collection-editor pages, not single dialogs:

SURBL servers · DNS blacklists · spam white list · greylisting white list · blocked
attachments · groups · server messages · domain aliases · incoming relays · SSL certificates
· TCP/IP ports · IP ranges · routes · rules · public folders · event scripts.

---

## Gap analysis & remediation (2026-06-15)

A full diff of every INI key (`IniFileSettings.cpp`, 104 keys) and the COM `Settings`
object + sub-objects + per-object interfaces (`hMailServer.idl`) against the GUI found and
closed these gaps:

### Added — server features that had no GUI
- **OAuth2 / external token auth (XOAUTH2)** — enabled, require-TLS, issuer, audience,
  allowed algorithms, username claim, public-key file, HMAC secret.
- **OpenTelemetry** (`OtelEndpoint`, `OtelServiceName`) and **slow-query log**.
- **SRS** and **BATV** (sender rewriting + bounce tagging) with secrets.
- **Submission/outbound rate limits**.
- **Minimum accepted hash algorithm**, **DPAPI secret protection**, **password pepper**.

### Added — operational INI knobs (Advanced hardening)
- **Connection timeouts** — SMTP/POP3 server & client min/max (8).
- **Delivery & queue tuning** — quick retries, queue jitter, MX-tries factor, external-fetch threads.
- **Logging detail** — log level, max log line length, separate service logs.
- **Search indexing** — full/quick cadence and batch limits.
- **Message archiving** — archive folder, hard-link mode.
- **Low-level tuning** — daemon address domain, oversized-drop, SA move-vs-copy, DB-only backup,
  header/body read sizes.

### Added — COM settings
- **`Logging.Device`** (files / SQL) and **`Logging.LogFormat`** (default / NCSA).
- **`Cache.{Domain,Account,Alias,DistributionList}CacheMaxSizeKb`** memory caps.
- **`Settings.UserInterfaceLanguage`** (Advanced).

### Added — per-object
- **`Domain.ADDomainName`** — link a mail domain to an Active Directory domain (DomainDialog
  General tab, with an AD-domain picker).

### Intentionally excluded (legacy / obsolete / internal)
These are writable in COM/INI but are superseded, obsolete, or internal test knobs, so they
are deliberately **not** surfaced:

- `Settings.SMTPRelayerUseSSL`, `Route.UseSSL`, `TCPIPPort.UseSSL` — legacy booleans
  superseded by the `*ConnectionSecurity` enums already in the GUI.
- `SecurityRange.RequireAuthForDeliveryToLocal/Remote`, `IsForwardingRelay`,
  `Route.TreatSecurityAsLocalDomain`, `RuleAction.Filename` — obsolete, replaced by
  per-direction auth flags / the Incoming relays collection / the typed action fields.
- `Settings.CrashSimulationMode`, `AntiSpam.TarpitDelay/TarpitCount`,
  `Logging.MaskPasswordsInLog` — internal/obsolete (no-ops).
- `Settings.ServiceAccountName/Password` — install-time only; editing the INI does not
  re-register the Windows service, so changing them here would be misleading.

---

*Verification: every added control was confirmed against the live dev server via UI
automation (controls instantiate, settings pages report no read errors) and, for
`Domain.ADDomainName`, a direct COM read/write round-trip.*
