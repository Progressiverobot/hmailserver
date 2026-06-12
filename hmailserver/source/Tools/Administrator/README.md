# hMailServer Administrator  -  Codebase Guide

hMailServer Administrator (`hMailAdmin.exe`) is the Windows desktop GUI used to configure a running hMailServer instance. It is written in C# / WinForms and lives entirely under `source/Tools/Administrator/`. It communicates with the server exclusively through the COM API (`Interop.hMailServer.dll`)  -  it does not touch the database or file system directly.

The project is part of the Visual Studio solution `source/Tools/hMailServer Tools.sln`.

---

## How the UI Is Structured

The application is a classic two-panel WinForms application:

- A **left-side tree view** (`treeNodes`) lists every configurable area of the server: domains, accounts, settings, protocols, anti-spam, logging, backup, etc.
- A **right-side content area** displays a `UserControl` (a "pane") that matches the currently selected tree node.

When the user clicks a node, `formMain.ShowNodeRepresentation(INode node)` is called. This resolves the correct pane for that node, loads it into the content area, and calls `LoadData()` on it to populate the UI with current values from the COM API. When the user clicks Save, `SaveData()` is called on the active pane, which writes changed values back through the COM API.

---

## Folder Structure

```
Administrator/
  Dialogs/            # Modal dialogs
    formMain.cs         # Main form: tree view, navigation, pane hosting
    formConnect.cs      # Connection/login dialog shown at startup
  Main panes/         # The UserControl for each tree node / settings area
  Nodes/              # Tree node model classes (INode and implementations)
  Properties/         # AssemblyInfo, application settings
  Utilities/          # Shared helpers: localization, tab ordering, etc.
  Wizards/            # Multi-step wizard dialogs
```

---

## `formMain.cs`  -  The Shell

The entry point for the application UI. Responsibilities:

- Builds the tree view hierarchy on startup by instantiating `INode` objects.
- Handles `treeNodes_AfterSelect`  -  calls `ShowNodeRepresentation()` to swap the active pane.
- Manages the connection to hMailServer (calls `formConnect` if not yet connected).
- Holds a reference to the `hMailServer.Application` COM object that is passed down to all panes.

When adding a new top-level settings area, you must: add a new `INode` implementation in `Nodes/`, register it in the tree in `formMain`, and create a corresponding pane in `Main panes/`.

---

## `Nodes/`  -  Tree Node Model

Each item in the left-side tree is represented by a class implementing `INode`. The node class knows:

- The display name and icon for the tree entry.
- Which pane (`UserControl`) to instantiate when the node is selected.
- Any child nodes to add beneath it.

Node classes are lightweight  -  they don't hold state. They exist purely to drive tree construction and pane resolution. Examples from the namespace include nodes for the server root, domains collection, an individual domain, accounts, an individual account, settings, protocol sub-nodes (SMTP, IMAP, POP3), anti-spam, etc.

---

## `Main panes/`  -  Settings Panes

This is where most feature work happens. Each `.cs` file in this folder is a WinForms `UserControl` that implements `ISettingsControl`, which requires two methods:

- **`LoadData()`**  -  reads current values from the COM API and populates the form controls.
- **`SaveData()`**  -  reads form controls and writes changed values back through the COM API.

Notable panes (derived from class names visible in stack traces and the codebase):

| Pane class | Area |
|---|---|
| `ucStatus` | Dashboard  -  shows server state, delivery queue, live log, and configuration warnings |
| `ucProtocols` | Protocol on/off toggles (SMTP, IMAP, POP3 enabled/disabled) |
| `ucProtocolSMTP` | Detailed SMTP settings (ports, relay, delivery, authentication) |
| `ucProtocolIMAP` | IMAP settings (public folders, ACL, IDLE) |
| `ucProtocolPOP3` | POP3 settings |
| `ucDomains` | Domain list view |
| `ucDomain` | Individual domain settings (name, enabled, catch-all) |
| `ucAccounts` | Account list for a domain |
| `ucAccount` | Individual account settings (address, password, quota, Active Directory) |
| `ucAliases` | Alias list and editing |
| `ucDistributionLists` | Distribution list management |
| `ucRules` | Message rules  -  criteria and actions |
| `ucAntiSpam` | Anti-spam settings (SPF, DKIM, SURBL, DNS blacklists, score thresholds) |
| `ucAntiVirus` | Anti-virus integration settings |
| `ucLogging` | Log level and log file configuration |
| `ucBackup` | Backup settings and trigger |
| `ucDiagnostics` | Runs server diagnostics and displays results |
| `ucIPRanges` | IP range access control |
| `ucSSLCertificates` | TLS/SSL certificate management |

When adding a new feature that is exposed through the COM API, the pattern is: create a new `ucXxx.cs` pane (or add to an existing one), implement `LoadData()` / `SaveData()` against the relevant COM interface, and wire it to a node in `Nodes/`.

---

## `Dialogs/`  -  Modal Dialogs

Stand-alone modal dialog forms used for focused tasks that don't warrant a full pane: picking a certificate, confirming a destructive action, entering a password, showing a test-connection result, etc. These are invoked from panes or from `formMain` using `ShowDialog()`.

---

## `Utilities/`  -  Shared Helpers

Small utility classes shared across panes and dialogs. Key pieces:

- **Localization / `Strings`**  -  `Strings.Localize("key")` looks up the display string for the current language. All user-visible text in panes must go through this. String keys are defined in `source/Translations/english.ini` (and other language files). When adding new UI text, add a key to the translation file and reference it via `Strings.Localize()`.
- **`EnumStrings`**  -  defines the string variants for various enum values.

---

## `formConnect.cs`  -  Connection Dialog

Shown at startup (and on reconnect). Lets the user enter the hMailServer host and username.

---

## Key Patterns for Feature Work

**Every COM property needs a corresponding UI round-trip.** `LoadData()` reads from COM -> populates controls. `SaveData()` reads controls -> writes to COM. Keep these symmetric; do not mix them.

**All user-visible strings go through `Strings.Localize()`.** Hard-coded English strings in the UI are a bug. Add the key to `english.ini` first.

**Panes are instantiated fresh each time a node is selected** (or kept in a cache  -  confirm by checking `formMain.ShowNodeRepresentation`). Do not rely on pane-level state persisting across navigation.

**No direct database access.** The Administrator never queries the database. All reads and writes go through `Interop.hMailServer` COM calls. If a value isn't exposed by the COM API, it cannot be surfaced in the Administrator without first adding it to the COM layer in `source/Server/COM/`.

**COM errors surface as `COMException`.** Panes should catch `COMException` around `LoadData()` and `SaveData()` calls and display friendly error messages rather than letting them bubble to `formMain`.

---

## Relationship to the Rest of the Codebase

| Concern | Where it lives |
|---|---|
| COM API definition | `source/Server/COM/` (C++, `.idl`) |
| COM interop assembly | `Interop.hMailServer.dll` (generated from the IDL, referenced by Administrator) |
| Shared C# utilities | `source/Tools/Common/` |
| Translation strings | `source/Translations/english.ini` |
| Build output | `source/Tools/Administrator/bin/x64/Release/hMailAdmin.exe` |

Any new feature that requires a new COM property must be implemented in `source/Server/COM/` first, then the interop DLL regenerated, and only then can the Administrator pane be written to use it.