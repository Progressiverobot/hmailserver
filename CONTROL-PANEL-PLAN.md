# hMailServer Control Panel — Build Plan

The working plan for replacing the WinForms Administrator with a world-class,
modern desktop program. Keep this file updated as phases complete — it is the
session-to-session memory for this effort.

---

## Vision

A new desktop app, **hMailServer Control Panel** (`hMailCP.exe`), that looks
and feels like a 2026 flagship Windows application: Fluent/Windows 11 design,
Mica window material, dark/light themes, animated charts, live log streaming,
and **complete coverage of the 6.0 feature settings that the old admin lacks**
(DANE, MTA-STS, ARC, DNSSEC, TLS-RPT, ACME, REST API, web services hosting,
Prometheus metrics, JSON logging).

The legacy WinForms `hMailAdmin.exe` stays in the installer as a fallback
until the Control Panel reaches full parity.

## Stack (decided)

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 8 (`net8.0-windows`), x64 | SDK 8.0.422 installed at `C:\Program Files\dotnet` (winget, 2026-06-12); WindowsDesktop runtime 8.0.28 present |
| UI framework | **WPF** | Mature, GPU-rendered, designer-friendly, COM interop trivial |
| Design system | **WPF-UI** (lepoco) NuGet `WPF-UI` | Fluent/Win11: `FluentWindow`, `NavigationView`, Mica/Acrylic, dark+light, animated transitions |
| Charts | **LiveChartsCore.SkiaSharpView.WPF** (v2) | Animated, smooth, MIT; line/area/gauge series for dashboard |
| MVVM | **CommunityToolkit.Mvvm** | `[ObservableProperty]`, `[RelayCommand]` source generators |
| Server API | COM via `dynamic` (IDispatch) | No interop DLL dependency; `Type.GetTypeFromProgID("hMailServer.Application", host)` — same pattern as `APICreator.cs` |
| INI features | `GetPrivateProfileString`/`WritePrivateProfileString` + registry `HKLM\SOFTWARE\hMailServer\InstallLocation` | Same mechanism as `formServerFeatures.cs` (works when running on the server box) |
| Logs | `FileSystemWatcher` + tail of `LogFolder` files | Folder from INI `[Directories] LogFolder`; colorize by log type |

## Project layout

```
hmailserver/source/Tools/ControlPanel/
  ControlPanel.csproj          # net8.0-windows, UseWPF, x64, nullable
  App.xaml / App.xaml.cs       # WPF-UI resources, theme bootstrap
  MainWindow.xaml(.cs)         # FluentWindow + NavigationView shell
  Services/
    ServerSession.cs           # COM connect/authenticate (dynamic), status polling
    IniFeatureStore.cs         # locate INI via registry; read/write feature keys
    LogTailService.cs          # live tail + parse of hMailServer logs
  ViewModels/                  # one VM per page (CommunityToolkit.Mvvm)
  Views/
    ConnectPage.xaml           # host/user/password sign-in (modern card)
    DashboardPage.xaml         # KPI cards + LiveCharts session/throughput charts
    DomainsPage.xaml           # domains + accounts CRUD (DataGrid + dialogs)
    QueuePage.xaml             # delivery queue list, retry/delete
    LogsPage.xaml              # live log viewer, severity colors, pause/filter
    SecurityPage.xaml          # DANE, DNSSEC, MTA-STS, ARC, TLS-RPT  ← missing in old admin tree
    AutomationPage.xaml        # ACME (Let's Encrypt) settings
    IntegrationPage.xaml       # REST API, web services, metrics, JSON logging
    SettingsPage.xaml          # theme toggle, about
```

## Feature settings inventory (Security/Automation/Integration pages)

INI `[Settings]` keys (defaults in parentheses):

- **Security page**: `DaneEnforcementEnabled` (1), `DnssecValidationEnabled` (1),
  `DnssecTrustAnchors` (empty), `MtaStsEnabled` (1), `ArcSealingEnabled` (0),
  `TlsRptFromAddress` (empty), `TlsRptOrganizationName` (hMailServer)
- **Automation page**: `AcmeEnabled` (0), `AcmeContactEmail`, `AcmeDomains`,
  `AcmeDirectoryUrl` (LE prod), `AcmeHttpPort` (80), `AcmeReuseKey` (1)
- **Integration page**: `RestApiPort` (0), `RestApiBindAddress` (127.0.0.1),
  `RestApiCertificateFile`, `RestApiPrivateKeyFile`, `MetricsServerPort` (0),
  `MetricsServerBindAddress` (127.0.0.1), `WebServicesHttpPort` (0),
  `WebServicesHttpsPort` (0), `WebServicesBindAddress` (0.0.0.0),
  `MtaStsHostingEnabled` (1), `MtaStsPolicyMode` (enforce),
  `MtaStsPolicyMaxAge` (604800), `MtaStsPolicyMx`, `AutoconfigEnabled` (1),
  `AutoconfigClientHost`, `JsonLogging` (0)

Each page: modern settings cards (WPF-UI `CardControl`/`CardExpander`), inline
explanations, restart-service prompt after save (service restart needed for
INI changes to take effect).

## COM surface used (via dynamic)

- `app.Authenticate(user, password)` — sign in
- `app.Status` — `ProcessedMessages`, `SpamMessages` (name check!), session counts via `Status.get_SessionCount(type)`; queue via `Status.UndeliveredMessages` (tab-separated rows)
- `app.Settings.Domains` — `Count`, `get_Item(i)`, `Add()`; domain: `Name`, `Active`, `Accounts`
- Account: `Address`, `Password`, `Active`, `MaxSize`, `Save()`, `Delete()`
- `app.Settings.ServerMessages`, etc. — later phases
- Queue actions: `app.GlobalObjects`/`Status` — verify exact members against the IDL in `source/Server/COM/` before use

## Phases

- [x] Phase 0 — environment: .NET 8 SDK 8.0.422 installed (2026-06-12)
- [x] Phase 1 — scaffold: FluentWindow shell, Mica, custom Fluent sidebar, Connect page with COM auth (+ `/connect host user pass` CLI auto-connect)
- [x] Phase 2 — Dashboard: KPI cards, LiveCharts animated throughput + session charts (2s poll), uptime/state
- [x] Phase 3 — feature settings pages (Security / Automation / Integration) reading+writing INI, with restart prompt
- [x] Phase 3.5 — **COM server settings pages**: Protocols, Delivery, Anti-spam (SPF/DKIM/DMARC/greylisting/SpamAssassin), Anti-virus (ClamAV/ClamWin), SSL/TLS + auto-ban, Logging — data-driven via dotted property paths over IDispatch (`ServerSettingsView`)
- [x] Phase 4 — Domains & accounts CRUD (note: COM path is `app.Domains`, NOT `app.Settings.Domains`)
- [x] Phase 5 — Delivery queue + Logs live tail with severity colors
- [x] Phase 6 (partial) — contrast guarantee (global TextBlock style), validated by screenshots in dark mode
- [x] Phase 7 — packaging: framework-dependent publish (7 MB) shipped in installer as component `controlpanel` → `{app}\ControlPanel\hMailCP.exe` + Start-menu icon (requires .NET 8 Desktop Runtime)
- [x] Phase 8 — validation: UIA-driven screenshots (`build/capture-cp.ps1 -Launch -Nav 'Anti-spam'`)
- [x] Phase 9 — release: v6.2.0 (server 6.2.0 build 6, gate 898/898)

**ALL FEATURES COMPLETE** (2026-06-13). 19 navigation sections.

Done post-6.2.0-initial: IP ranges page (+ full per-range permission
editor), SSL certificates manager, light/dark theme toggle (sidebar
footer, HKCU persisted), Ctrl+K command palette, branded app icon
(build/make-app-icon.ps1), Rules page with graphical IF/THEN editor
(create rules, add/remove criteria and actions; functionally verified
via UIA -> COM), Backup & restore page (BackupManager COM), aliases +
distribution lists panels (with Members recipients dialog), account Edit
dialog (quota/password/forwarding/vacation), Routes page, queue Deliver
now/Remove actions, About page.

## Environment facts (do not rediscover)

- dotnet CLI: `C:\Program Files\dotnet\dotnet.exe`, SDK **8.0.422**, Desktop runtime 8.0.28
- Dev server: localhost COM, admin password **testar** (test-suite canonical; `TestSetup.Authenticate` uses "testar" or blank)
- Dev INI: `hmailserver\source\Server\hMailServer\x64\Release\hMailServer.ini` (`RestApiPort=8045`, `AddXOriginalRcptTo=1`, MYSQL hmailtest2); `Bin\hMailServer.ini` is the SQLCE copy
- Registry install location used by INI locator: `HKLM\SOFTWARE\hMailServer\InstallLocation` — dev box has stale key removed earlier; the locator returns null on the dev box unless the key exists → IniFeatureStore must also accept an explicit path fallback (use the dev INI path when running from the repo)
- Web Control Deck: http://127.0.0.1:8045/ (Administrator/testar)
- Screenshot tooling: PrintWindow flag 2 works for WPF; `Cap3`/`Mouse` Add-Type snippets in session history; auto-connect config for legacy admin written to `%LOCALAPPDATA%\Halvar Information\hMailServer\hMailAdmin.exe.config`
- MSBuild (for the old solutions): `. build\Find-MsBuild.ps1; $msbuild = Find-MsBuild`
- Tools solution must NOT gain the new project (old-style sln, VS2026 BuildTools) — build ControlPanel separately with `dotnet build`/`publish`
- Release gate: server code untouched by this effort → full 898-test regression suite only needed if server C++ changes

## Release plan

Ship as **6.2.0**: "hMailServer Control Panel (preview)" alongside the legacy
admin. Installer: new component `controlpanel`, default on. Update README +
release notes. Tag `v6.2.0`, upload installer, mark latest.
