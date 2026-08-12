Architecture
============

A map of the codebase, for someone about to change it. It answers "where does this
change go" rather than "how does mail work", and it records the constraints that are
not obvious from reading any single file — the ones that have actually caused bugs
here.

Pair it with [CONTRIBUTING.md](CONTRIBUTING.md) for process and
[RELEASE.md](RELEASE.md) for the release gates.

Shape of the repository
-----------------------

```
hmailserver/
  source/
    Server/          C++ mail server - almost all feature work happens here
    Tools/           C# administration tools and the Control Panel (.NET 10)
    Addons/          Standalone sample add-ons
    DBScripts/       Schema creation and the upgrade chain, per backend
  test/
    RegressionTests/ NUnit suite driving a real running server
  installation/      Inno Setup installer
  docs/              Operator documentation
libraries/           Vendored third-party code
build/               Build, publish, preflight and test scripts
```

The server
----------

Entry point: `source/Server/hMailServer/hMailServer.sln`.

```
Server/
  COM/               COM/IDispatch public API - the management seam
  Common/            Shared infrastructure used by every protocol
  ExternalFetcher/   POP3 *client*: fetch mail from remote accounts
  hMailServer/       Windows service shell (WinMain, service control)
  IMAP/              IMAP
  POP3/              POP3
  SMTP/              SMTP, delivery queue, outbound transport security
```

`Server/hMailServer/` is the service shell only — no protocol or business logic.

### `Server/Common/`

| Sub-folder | Purpose |
|---|---|
| `AntiSpam/` | SPF, SURBL, DNS blacklists, greylisting, score-based filtering, `DMARC/`, and `DKIM/` (RSA and Ed25519 signing/verification, ARC sealing in `Arc.{h,cpp}`) |
| `AntiVirus/` | ClamAV (clamd and clamscan) and arbitrary command-line scanners |
| `Application/` | Startup, configuration, scheduling, logging. `IniFileSettings` holds every `hMailServer.ini` setting; `Application::StartServers` starts the optional listeners |
| `BO/` | Business objects — the domain model: domains, accounts, aliases, distribution lists, rules |
| `Cache/` | In-memory caches in front of the BO layer, to keep hot paths off the database |
| `Diagnostics/` | The checks behind the Control Panel's "Run diagnostics" |
| `Mime/` | MIME parsing and construction |
| `Persistence/` | One class per business object, mapping it to database columns |
| `Scripting/` | VBScript/JScript event hooks |
| `Sieve/` | `SieveLexer`/`SieveParser` (AST), `SieveEvaluator`, `SieveStorage` (per-account scripts under `{DataDir}\Sieve\`), and the optional RFC 5804 `ManageSieveServer`. Evaluated during local delivery |
| `SQL/` | Database abstraction: connections, pooling, parameterised queries |
| `TCPIP/` | Boost.Asio networking, TLS, DNS. Also `DnssecResolver` (validating stub resolver) and `DaneVerifier` (TLSA matching) |
| `Threading/` | Thread pools and task queues |
| `Tracking/` | Publish/subscribe bus between components |
| `Util/` | Utilities, plus the optional listeners: `MetricsServer`, `RestApiServer`, `WebServicesServer`, `AcmeClient`, `TlsRptStore` |

### `Server/SMTP/`

The most complex module: reception, relay decisions, the disk-backed delivery queue,
bounces, DKIM signing, and outbound delivery. Outbound transport security lives here
too — `TlsPolicy` implements MTA-STS discovery/caching and DANE TLSA lookups,
`ExternalDelivery` applies the per-host requirements, `TlsRptReporterTask` sends the
daily RFC 8460 reports. The vendored SPF implementation is `SPF/RMSPF.cpp`.

### `Server/IMAP/` and `Server/POP3/`

One command-handler class per IMAP command. Folder hierarchy and message flags are in
the database via `Persistence/`; message bodies are on disk. POP3 is much simpler and
reads the same storage.

Patterns that matter
--------------------

**The COM API is the seam.** All configuration and management goes through
`Server/COM/`. The Control Panel, the regression suite and every third-party script
use it. A new configurable feature normally needs a COM property or method — and
because the test suite drives COM, that is also how the feature becomes testable.

**Persistence is layered.** `BO/` → `Persistence/` → `SQL/`, with `Cache/` in front
for frequently-read objects. A feature that persists new data touches all of the
first three, and `Cache/` if it is on a hot path.

**Four database backends, one abstraction.** MySQL/MariaDB, MS SQL Server,
PostgreSQL and the embedded SQL CE. **Use parameterised queries exclusively** — never
build SQL by string concatenation. Backend-specific DDL goes through the macro
expanders in `SQL/Macros/`, which recognise a deliberately small vocabulary; if you
need something they do not express, that is a design conversation, not a place to
special-case.

**Server-wide optional features are INI settings, not database settings.** MTA-STS,
DANE, ARC, TLS-RPT, ACME, the REST API, web services, metrics and JSON logging are
all `hMailServer.ini` `[Settings]` keys read by `IniFileSettings`. The pattern for a
new one: a getter in `IniFileSettings.h`, the default on the member declaration, a
`ReadIniSetting*_` call in `IniFileSettings.cpp`, and a control in the Control
Panel's `FeatureSettingsView`. Per-account and per-domain settings go in the database
instead.

**The optional listeners are deliberately not Boost.Asio.** `MetricsServer`,
`RestApiServer`, `WebServicesServer` and `ManageSieveServer` use raw sockets and
`std::thread`, outside the `TCPIP/` stack, started from `Application::StartServers`
only when their port is non-zero. Two consequences that have both bitten:

* **They have no exception barrier by default.** An exception escaping the top of one
  of those threads is `std::terminate` — the whole mail server dies. Anything that
  can throw, including any database call, needs a `try`/`catch` inside the thread.
* **They build their own `SSL_CTX`.** They do not go through
  `SslContextInitializer`, so TLS settings applied there — cipher lists, key-exchange
  groups — do not reach them. Check both when changing TLS behaviour.

**Scheduled work** uses `BO/ScheduledTask` and the `Scheduler`. `RunOnce` tasks go
through the maintenance work queue immediately; recurring tasks are polled once a
minute.

Constraints learned the hard way
--------------------------------

These are the ones that cost real releases. They are not stylistic.

**Every wait on a pooled thread needs a ceiling.** The server runs work on bounded
pools — a 15-thread async queue that also sends the SMTP `250`, a 10-thread delivery
queue. A dependency that stops responding consumes threads until none are left, and
then the server accepts mail and never replies. That was
[discussion #18](https://github.com/Progressiverobot/hmailserver/discussions/18), and
once found the same shape turned up in DNS, virus scanning, event scripts, external
processes and outbound delivery. Every one of those now has a bound, and a new one
must arrive with one.

**An idle timeout is not a ceiling.** Idle timeouts here re-arm on every byte
received, so a peer that dribbles one byte at a time is never *idle* and holds the
connection — and anything waiting on it — indefinitely. Absolute session ceilings are
a separate mechanism (`ClientSessionCeiling`) for exactly this reason.

**Distinguish "the answer is no" from "there was no answer."** A recipient lookup
that fails because the database did not respond used to be indistinguishable from one
that found nothing, so a database locked by a backup told the sender a valid mailbox
did not exist and the mail was *bounced*. The fix is the thread-local
`DatabaseUnavailableMarker` with its RAII `Scope`, read at both `RCPT TO` decision
points to answer `451` instead of `550`. Two non-obvious constraints if you extend
it: set the marker **after** releasing the pool lock and **before** `ReportError`,
because the error path can run an `OnError` script that re-enters the pool on the
same thread.

**`shared_from_this()` is invalid in a constructor.** It throws `bad_weak_ptr`. Timers
and anything else needing a `shared_ptr` to the object must be armed from `Start()`,
not the constructor.

**A new diagnostic must not fire on the shipped default configuration.** Reporting a
default as an `ErrorManager` error puts a Medium entry in every stock install's ERROR
log — and fails the regression fixtures, which assert a clean log. If it describes a
default, it is `LOG_APPLICATION`.

**Prefer deferral to bouncing, always.** A temporary failure costs a retry. A
permanent one costs someone their mail.

Tests
-----

`test/RegressionTests/` is NUnit driving a **real running server** over real sockets,
with live SpamAssassin and ClamAV, DMARC against live DNS, and real TLS handshakes.
Nothing is mocked. Two things to know before adding a test:

* **`RegressionTests.csproj` lists every source file explicitly — there is no glob.**
  A test file nobody adds to it is not merely unrun, it is invisible, and a green
  suite says nothing about it. `build/preflight-tests.ps1` now fails on orphans; that
  check exists because a committed test file went uncompiled for months.
* **A test that deliberately provokes a reported error must clear the ERROR log**, via
  `CustomAsserts.AssertReportedError(...)` which asserts and deletes. `PerformBasicSetup`
  calls `AssertNoReportedError`, so a provoked error left behind fails whichever
  unrelated fixture runs next.

A fix does not ship without a test that **fails against the build before it**. A test
that passes both ways proves nothing — build the pre-fix binary and check.

Building
--------

| Artifact | How |
|---|---|
| Server | `build/build.ps1 -Configuration Release` (VS 2026, toolset v145, x64) |
| Admin tools | `build/build-tools.ps1` (`source/Tools/hMailServer Tools.sln`) |
| Control Panel | `source/Tools/ControlPanel.sln`, published separately into its `publish/` folder |
| Tests | `build/build-tests.ps1` |

**.NET targets.** Everything shipped is `net10.0-windows`, pinned via `global.json`.
The regression suite and its companions are **.NET Framework 4.8.1** and stay there:
4.8.1 has no end-of-support date, and the suite drives the server over COM interop.
Dormant upstream dev tools under `tools/` and the unused performance/stress projects
target 4.8.1 or 3.5 and are built by nothing — do not assume they compile.

The SDK is pinned in `global.json`. There is one build tree and the service holds the
output binary, so **stop the service before linking** — and never build while a
regression run is in progress.

Where to start
--------------

| Change | Start at |
|---|---|
| A new SMTP behaviour | `SMTP/SMTPConnection.cpp` (reception) or `SMTP/ExternalDelivery.cpp` (sending) |
| A new IMAP command | a new `IMAP/IMAPCommand*.{h,cpp}` plus the dispatch table and `IMAPCommandCapability.cpp` |
| A server-wide setting | `Common/Application/IniFileSettings.{h,cpp}` and the Control Panel's feature settings |
| A per-account or per-domain setting | `Common/BO/`, `Common/Persistence/`, the COM interface, the schema, and the upgrade chain |
| A new anti-spam test | `Common/AntiSpam/` and the score pipeline |
| Something exposed to scripts | `Common/Scripting/` and the COM layer |

If a change spans more than about three of those rows, it is worth discussing in an
issue before writing it.
