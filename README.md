hMailServer
===========

[![OpenSSF Best Practices](https://www.bestpractices.dev/projects/14187/badge)](https://www.bestpractices.dev/projects/14187)
[![OpenSSF Scorecard](https://api.scorecard.dev/projects/github.com/Progressiverobot/hmailserver/badge)](https://scorecard.dev/viewer/?uri=github.com/Progressiverobot/hmailserver)

hMailServer is a free, open source email server for Microsoft Windows, implementing SMTP, IMAP and POP3.

This repository is a maintained fork of the original project, which is no longer developed upstream. It has been brought up to date with a current toolchain, current cryptography, and the transport-security and authentication standards expected of a mail server in 2026 — while remaining a drop-in upgrade for existing hMailServer installations. It is maintained by Christopher Holloway / [Progressive Robot Ltd](https://www.progressiverobot.com).

**[Download the latest release](https://github.com/Progressiverobot/hmailserver/releases/latest)** — a single `hMailServer-x.y.z-x64.exe` installer. Upgrading in place preserves your configuration and mail; the database upgrade chain is continuous from every earlier hMailServer version.

Every release is validated by the full regression suite before it ships — the complete suite, run against the exact binary being released, with live SpamAssassin and ClamAV (real EICAR detection), DMARC evaluated against live DNS, and TLS 1.2/1.3 handshakes end to end. Nothing is skipped or mocked.

**What changed in each version** is on the [Releases page](https://github.com/Progressiverobot/hmailserver/releases). What is planned next, and what is deliberately not, is in [Roadmap.md](Roadmap.md). The release process itself is documented in [RELEASE.md](RELEASE.md), and the codebase map is in [ARCHITECTURE.md](ARCHITECTURE.md).

Contents
--------

* [Capabilities](#capabilities) — what the server does
* [Technology](#technology) — what it is built on
* [Administration](#administration) — the Control Panel and the APIs
* [Installing](#installing) — supported platforms and unattended install
* [Building hMailServer](#building-hmailserver)
* [Configuration reference](#configuration-reference)
* [Operator documentation](#operator-documentation) — the runbooks

Capabilities
============

Mail protocols
--------------

* **SMTP** with PIPELINING, ENHANCEDSTATUSCODES, 8BITMIME, SIZE, CHUNKING/BDAT (RFC 3030), DSN delivery status notifications (RFC 3461/3464) and SMTPUTF8/EAI for internationalised addresses.
* **IMAP4rev1**, plus **IMAP4rev2** (RFC 9051) advertised with its behavioural deltas implemented, including the extensions rev2 folds in (LIST-STATUS, non-synchronising literals, BINARY) — with IDLE, MOVE (RFC 6851), UIDPLUS (RFC 4315), CONDSTORE/QRESYNC (RFC 7162), SEARCHRES (RFC 5182), ESEARCH (RFC 4731), SORT and THREAD (RFC 5256, both ORDEREDSUBJECT and REFERENCES), ACL, NAMESPACE, ID (RFC 2971), SPECIAL-USE (RFC 6154, including explicit designation via `CREATE ... (USE (\Sent))`) and QUOTA.
* **POP3**, including retrieval from external POP3 accounts on a schedule.
* **Public folders**, shared across accounts with per-user ACLs.

Transport security
------------------

* **TLS 1.2 and 1.3** by default, on implicit-TLS and STARTTLS ports, with SNI, configurable cipher suites (separately for TLS ≤ 1.2 and TLS 1.3) and configurable key-exchange groups — **hybrid post-quantum key exchange** (X25519MLKEM768) is preferred by default, on every TLS context inbound and outbound.
* **MTA-STS** (RFC 8461) policy discovery and enforcement for outbound mail, and optional hosting of your own policy at `mta-sts.<domain>`.
* **DANE** (RFC 7672) with full in-process **DNSSEC validation** (RFC 4033–4035) — a bogus chain blocks delivery to that host rather than silently downgrading. The **MX RRset is validated too**, not just the TLSA record (RFC 7672 §2.2): DANE is applied only to a host the recipient domain provably published, so a forged MX answer cannot redirect delivery to a host whose own TLSA record then validates.
* Encrypted (passphrase-protected) TLS private keys, with the passphrase held per certificate and protected at rest.
* TLS session-ticket key **rotation** (OpenSSL's default key is generated once per process and never rotated, so every ticket it issues is sealed under the same key), plus session cache and timeout control and the ability to disable tickets entirely. All off by default.
* An `AEAD-ONLY` cipher preset, which excludes every CBC construction and the Lucky13 family; nothing below TLS 1.2 can connect under it.
* DNSSEC validation also protects SPF, DKIM and DMARC record lookups.
* **TLS-RPT** (RFC 8460) daily aggregate reports to recipient domains — off until `TlsRptFromAddress` is set (the server notes this in the application log while statistics are collected unsent).
* **ACME v2 (Let's Encrypt)** built in: certificates are issued, renewed, assigned to TLS ports and hot-reloaded without a restart. The private key is reused across renewals, so published DANE TLSA records stay valid.

Sender authentication and anti-abuse
------------------------------------

* **SPF**, **DKIM** signing and verification (including Ed25519, RFC 8463) and **DMARC** evaluation with alignment, with the organizational domain resolved from the real **Public Suffix List** (compiled in, wildcard and exception rules, ICANN and private sections) rather than a heuristic.
* **DKIM signature timestamps**: every signature carries `t=`; an `x=` expiry is added when `DKIMSignatureValiditySeconds` is set, and expired signatures are refused on verification (on by default, with a configurable clock-skew allowance). Optional **oversigning** (`DkimOversignHeaders`, off by default) stops a second `From:` being prepended to signed mail.
* **ARC** sealing (RFC 8617) so forwarded mail keeps a verifiable authentication chain — off by default (`ArcSealingEnabled`). Inbound ARC results can also recover the original authentication result so forwarding stops costing legitimate mail its DMARC pass — off until both `ASArcFilteringEnabled` and a list of trusted sealer domains are set, because anyone can seal a chain with their own key and a passing chain proves nothing on its own.
* Optional **SRS** sender rewriting for forwarded mail (`SRSEnabled`, off by default), and optional **BATV** (`prvs`) backscatter protection.
* **SpamAssassin** integration, **DNSBL** and **SURBL** lookups, greylisting, HELO/PTR/MX sanity checks and a weighted scoring pipeline.
* **Virus scanning** via ClamAV (clamd or clamscan) or any command-line scanner.
* Attachment blocking, IP ranges with per-range policy, and connection auto-banning after repeated authentication failures.

Account security and authentication
-----------------------------------

* **SCRAM-SHA-256** SASL across IMAP, SMTP submission and POP3, plus **SCRAM-SHA-256-PLUS** channel binding on all three, with deterministic anti-enumeration salts. SMTP and POP3 offer SCRAM whenever AUTH is available; on IMAP, SASL (PLAIN and SCRAM alike) sits behind one setting whose shipped default is off.
* **OAuth2 / OpenID Connect** bearer tokens — SASL XOAUTH2 and OAUTHBEARER (RFC 7628) — validated against an external identity provider's signing key.
* **LDAP directory authentication** against Active Directory or any LDAP directory, so accounts authenticate with their domain password. Simple bind and SASL Negotiate; LDAPS and StartTLS; certificate validation on by default, and a password is never sent over an unprotected connection unless that is explicitly permitted. Unlike the Windows-logon path it needs no domain-joined host, which is the usual situation for a mail server in a DMZ. Infrastructure failures are reported separately from wrong passwords, so a directory outage does not read as a hundred users mistyping. Off by default: the whole `[LDAP]` ini section is absent until you add it.
* **LDAP directory provisioning** — the directory as an account source, not only as a password check. Preview reports exactly which mailboxes the directory says should exist and changes nothing; apply then creates and updates them. The two share one decider, so a preview cannot describe an action the apply would not take. A domain takes part only if its Active Directory domain name is set, which makes provisioning opt-in per domain and keeps a search base pointed one level too high from provisioning into unrelated hosted domains. Nothing is ever deleted: the most it does is mark an account inactive, and only when asked — never on a truncated or empty enumeration, and never for a domain in which nothing was seen. An optional unattended schedule (`[LDAP] SyncScheduleMinutes`, off by default) creates and updates but never disables. Verified end to end against a live Windows Server 2025 domain controller.
* **Argon2id** and **PBKDF2-HMAC-SHA256** password hashing, with tunable work factors, transparent upgrade on login (of the scheme, and of a hash cheaper than the configured work factor), a minimum-accepted-hash policy, and an optional server-side pepper.
* Full RFC 4013 SASLprep of non-ASCII credentials on the PLAIN and LOGIN paths (SCRAM usernames are matched as sent).
* Optional **TOTP two-factor authentication** for administrative logon.

Mail filtering and routing
--------------------------

* **Sieve** (RFC 5228) — a standards-based interpreter runs each account's active script during delivery (`keep`, `fileinto`, `discard`, `redirect`, implicit keep, plus the `copy`, `relational`, `subaddress`, `imap4flags` and `vacation` extensions), with an optional **ManageSieve** (RFC 5804) listener so clients can manage scripts over TCP.
* The original rules engine, with global and per-account rules, regular-expression criteria and scripted actions.
* Server-side **event scripts** (VBScript/JScript) on connection, HELO, DATA, accept and delivery events.
* Routes, aliases, distribution lists, catch-all addresses and plus-addressing.
* Multiple smart hosts with automatic failover: separate several hosts with `|` in the relayer field and delivery moves to the next when one cannot be reached.

Operations and observability
----------------------------

* **Prometheus** `/metrics` (database pool, TLS handshakes, delivery queue, authentication outcomes, delivery outcomes, command and query latency) with Kubernetes-style `/livez`, `/readyz` and `/healthz` probes.
* **OpenTelemetry** export over OTLP/HTTP for all three signals - traces (`/v1/traces`), metrics (`/v1/metrics`) and logs (`/v1/logs`) - each with its own endpoint setting and each off until it is set. The exported metrics are the same counters the Prometheus `/metrics` endpoint serves, under the same names, rather than a second tally. Inbound W3C `traceparent` is honoured on HTTP and on SMTP (where it travels as a message header), and emitted onward, so a message keeps one trace across hops. Plus message-to-session correlation IDs.
* Optional **JSON-structured logs**, log retention, per-service log files, and a slow-query log with every SQL string literal redacted.
* Per-stage timing of message acceptance, so a slow scanner, DNS lookup or event script is identified by name in the log rather than appearing as an unexplained pause. Acceptance is also bounded: if it runs past its deadline the sender gets a temporary `451` and retries, instead of waiting for a reply that never comes. Every wait that can hold a pooled thread - scanners, DNS, event scripts, external processes, outbound sessions - has a ceiling, and the work queue reports which task is holding each thread when they are all busy. See [diagnosing slow or stalled mail](hmailserver/docs/DiagnosingStalledMail.md).
* Backup and restore, a read-only **message-store consistency check** with a recovery report, configurable message-store fsync, graceful-shutdown drain, and a documented active/passive HA runbook.

Technology
==========

| Component | Detail |
|---|---|
| Server core | C++, built with Visual Studio 2026 (platform toolset v145), 64-bit only |
| Cryptography | OpenSSL 4.0.x |
| Async I/O | Boost 1.91 (Asio) |
| Databases | MySQL, MariaDB, MS SQL Server, PostgreSQL 18 (libpq), and the embedded SQL CE for zero-configuration installs |
| MySQL/MariaDB client | MariaDB Connector/C, shipped as `libmysql.dll` with auth plugins — works with MySQL 8 `caching_sha2_password` and MariaDB `ed25519`/`gssapi` out of the box. It requires TLS from the server by default; `AllowUnencryptedConnection=1` under `[Database]` lets it fall back to plaintext for a server that has none |
| Administration GUI and tools | C# / .NET 10 (WPF, Fluent design) |
| Extensibility | COM/IDispatch API, plus a REST administration API |
| Schema | Database version 6026, upgradeable from every earlier hMailServer release |

**Quality gates.** Every release ships SPDX and CycloneDX SBOMs (Syft). The repository runs CodeQL analysis, Dependabot CVE alerts with grouped update pull requests, a dependency-review gate on pull requests, an installer smoke test that installs the built installer on a clean machine and verifies the service comes up, and a monthly comparison against the original upstream repository so nothing landing there is missed.

Administration
==============

**hMailServer Control Panel** (`hMailCP.exe`) is the bundled administration GUI: a .NET 10 WPF application that talks to the server purely through the COM API. It covers domains, accounts, aliases, distribution lists, routes, rules, IP ranges, TCP/IP ports and SSL bindings, server settings, the live dashboard, the delivery queue, logs, status, backup, SSL certificates, scripts, Sieve scripts and public folders.

* **Ctrl+K** searches every setting by label or INI key — type `delete logs`, `log level` or `LogDeleteDays` and it takes you to the page that owns it.
* **Active Directory pickers**: a read-only browser lists the forest's domains and searches their users, to link an account to an AD user or bulk-import addresses into a distribution list.
* Optional TOTP two-factor authentication on logon.
* Requires the .NET 10 Desktop Runtime, which the installer bundles and installs silently when missing.

**REST administration API** for domains, accounts, the delivery queue, server status and TLSA records, with authenticated access and bounded request handling (a size cap and a receive deadline, so a slow or oversized request cannot occupy a worker). Callers authenticate with the administrator password or with a **scoped API key** — a bearer token stored only as a SHA-256 digest, with a mandatory expiry, an optional source-address restriction, and a scope that is read-only unless you say otherwise and can be confined to named domains. No key of any scope can mint or revoke keys; that needs the administrator password, or a narrow key would escalate itself in one request.

**Client autoconfiguration**: Thunderbird autoconfig and Outlook autodiscover are served for every local domain, so clients configure themselves from an address and password.

Installing
==========

Run the installer from the [Releases page](https://github.com/Progressiverobot/hmailserver/releases). It creates the Windows service, initialises or upgrades the database, and installs the .NET 10 Desktop Runtime if it is missing.

Supported platforms
-------------------

64-bit Windows only, from **Windows 10 1607 / Windows Server 2016** (build 14393) upward — the installer enforces this. That floor is set by the .NET 10 Desktop Runtime the Control Panel and setup tools need, and .NET 10 still supports Server 2012, so the floor here is deliberately conservative rather than as low as it could be.

Planned, so it is not a surprise: the intention is to raise the *declared* floor to **Windows Server 2019 / Windows 10 21H2** with the first release after **12 January 2027**, when Server 2016 leaves support. That costs nothing technically — Server 2019 is the same Windows API level — and everything in support through 2029 stays covered. Anyone still on Server 2016 after that date should expect no testing rather than active removal.

Unattended install
------------------

The installer is Inno Setup, so it takes the standard switches, plus one of its own. Documented here because otherwise the only way to find out is to read the Pascal.

| Switch | Effect |
|---|---|
| `/SILENT` | No wizard; progress window only |
| `/VERYSILENT` | No wizard and no progress window |
| `/SUPPRESSMSGBOXES` | Suppress message boxes. Worth pairing with the silent switches — some failure paths still raise one |
| `/LOG="<path>"` | Write an install log. Use it; it is the only record of what happened |
| `/DIR="<path>"` | Installation directory |
| `/COMPONENTS="server,admintools,controlpanel"` | Which components to install. `server` is the service itself; `admintools` registers the COM type library so scripts can administer a *remote* instance; `controlpanel` installs the admin GUI. The Control Panel does not need `admintools` — it binds late through IDispatch |
| `/useinternaldbms=true\|false` | **Custom to this installer.** `true` (the default) uses the bundled SQL Server Compact database. Set `false` when pointing at an existing MySQL, MariaDB, PostgreSQL or MS SQL server |

Example:

```bat
hMailServer-x.y.z-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /LOG="C:\Temp\hmail-install.log" ^
   /COMPONENTS="server,controlpanel" /useinternaldbms=true
```

Two things to know, because they are limitations rather than choices:

* **The administrator password cannot be set on the command line.** It is only collected by the wizard page, so a silent install leaves it unset and the database tool runs without it. Set it afterwards — through the Control Panel, or via the COM API (`Application.Authenticate` then `Settings.SetAdministratorPassword`).
* **A silent install of an existing installation still needs the password** for the database upgrade, and cannot prompt for it. Upgrade interactively, or set the password non-interactively first.

The installer checks both that the database tool launched *and* its exit code, so a failed or cancelled database create/upgrade cannot report a successful install — the service would otherwise come up against a missing or outdated schema. Treat a non-zero installer exit code as a failed install and read the log.

Do not run the installer on a machine you are also using to build the server: it takes over the service path, the COM `LocalServer32` registration and the 32-bit `InstallLocation` registry value, which is exactly the state a development checkout needs to control.

Building hMailServer
====================

Branches
--------

   * The master branch contains the latest development version of hMailServer. This version is typically not yet released for production usage. If you want to add new features to hMailServer, use this branch.
   
   * The x.y.z (for example 5.6.2) contains the code for the version with the same name as the branch. For example, branch 5.6.1 contains hMailServer version 5.6.1. These branches are typically only used for bugfixes or minor features.

Environment set up
---------------------

**Required software**

   * An installed version of hMailServer 5.7 or later (configured with a database)
   * Visual Studio 2026 (Community edition or Build Tools)
   * Inno Setup 6  -  only needed to build the installer (`winget install JRSoftware.InnoSetup`)
   * Perl 5 (https://strawberryperl.com/)  -  only needed to build OpenSSL
   * Python 3 (https://www.python.org/)  -  only needed to build libpq
   
**NOTE**

You should not be compiling hMailServer on a computer which already runs a production version of hMailServer, unless you disable the build events (see _Building hMailServer_ below). The default pre/post build events stop any already running hMailServer service and register the compiled version as the hMailServer service on the machine. If this happens, the easiest path is to reinstall the production version.

Installing Visual Studio 2026
----------------------------------------------

1. Download [Visual Studio 2026](https://visualstudio.microsoft.com/vs/) (or the Build Tools edition) and launch the installation.
2. Select the following _Workloads_
  * .NET desktop development (or "Managed Desktop Build Tools" for the Build Tools edition)
  * Desktop development with C++
3. Select the following _Individual components_
  * C++ ATL for latest v145 build tools (x86 & x64)
  * A Windows 10/11 SDK

Using winget:

   <pre>
   winget install Microsoft.VisualStudio.BuildTools --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools;includeRecommended --add Microsoft.VisualStudio.Component.VC.ATL --add Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools"
   </pre>

3rd party libraries
-------------------

Some 3rd party libraries which hMailServer relies on are large and updated frequently. Rather than including these large libraries into the hMailServer git repository, they have to be downloaded and built, currently manually. When you build hMailServer, Visual Studio will use a system environment variable, named hMailServerLibs, to locate these libraries.

Create an environment variable named hMailServerLibs pointing at a folder where you will store hMailServer libraries, such as C:\Dev\hMailLibs.

Building OpenSSL
----------------
1. Download OpenSSL 4.0.x from http://www.openssl.org/source/ and put it into %hMailServerLibs%\<OpenSSL-Version>.
   You should now have a folder named %hMailServerLibs%\<OpenSSL-version>, for example C:\Dev\hMailLibs\openssl-4.0.1
2. Start a x64 Native Tools Command Prompt for VS2026.
3. Change dir to %hMailServerLibs%\<OpenSSL-version>.
3. Run the following commands:

   <pre>
   Perl Configure no-asm VC-WIN64A --prefix=%cd%\out64 --openssldir=%cd%\out64 -D_WIN32_WINNT=0x0A00
   nmake clean
   nmake build_libs
   nmake install_dev install_runtime_libs
   </pre>

**NOTE:** Use the `build_libs` / `install_dev install_runtime_libs` targets rather than `install_sw`. The command-line `openssl` application is not needed by hMailServer and may fail to compile in some 4.0.x source drops.

Building PostgreSQL
-------------------
1. Download PostgreSQL 18.3 source from https://www.postgresql.org/ftp/source/v18.3/ and put it into %hMailServerLibs%\postgresql-18.3.
   You should now have a folder named %hMailServerLibs%\postgresql-18.3, for example C:\Dev\hMailLibs\postgresql-18.3
2. Download winflexbison from https://github.com/lexxmark/winflexbison/releases, extract it, and add the folder to `%PATH%`.
3. Install Python dependencies: `py -m pip install meson ninja`
4. Start a x64 Native Tools Command Prompt for VS2026.
5. Change dir to %hMailServerLibs%
6. Run the following commands:

   <pre>
   set hMailServerLibs=%cd%
   set CC=cl
   cd postgresql-18.3
   meson setup builddir --buildtype=release -Dssl=openssl -Dextra_include_dirs=%hMailServerLibs%\openssl-4.0.1\out64\include -Dextra_lib_dirs=%hMailServerLibs%\openssl-4.0.1\out64\lib
   meson compile -C builddir src/interfaces/libpq/libpq:shared_library
   </pre>

**NOTE:** The `-Dextra_include_dirs` and `-Dextra_lib_dirs` flags ensure meson links against the specific OpenSSL version built above. Verify that no other OpenSSL installation appears earlier in `%PATH%` (e.g. from Git for Windows or other tools), as meson may pick up the wrong version.

**TIP:** You can use [Dependencies](https://github.com/lucasg/Dependencies/releases) to verify that the built `libpq.dll` links against the correct OpenSSL DLLs (`libcrypto-4-x64.dll` / `libssl-4-x64.dll`) and not some other version found elsewhere on the system.

Building Boost
--------------
1. Download Boost 1.91.0 from http://www.boost.org/ and put it into %hMailServerLibs%\<Boost-Version>.
   You should now have a folder named %hMailServerLibs%\<Boost-Version>, for example C:\Dev\hMailLibs\boost_1_91_0
2. Start a x64 Native Tools Command Prompt for VS2026.
3. Change dir to %hMailServerLibs%\<Boost-Version>.
4. Run the following commands:

   NOTE: Change the -j parameter from 4 to the number of cores on your computer. The parameter specifies the number of parallel compilations will be done.

   <pre>
   bootstrap
   b2 debug release threading=multi link=static --with-thread --with-filesystem --with-regex --with-chrono --with-atomic address-model=64 stage --build-dir=out64 -j 4 define=BOOST_USE_WINAPI_VERSION=0x0A00
   </pre>

   NOTE: Boost.System is header-only in recent Boost versions and no longer needs to be built.

Building hMailServer
--------------------

The repository contains build scripts which locate the prerequisites automatically. Run them with `powershell.exe`:

   <pre>
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build.ps1        # builds hMailServer.exe
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\post-build.ps1   # copies DLLs, registers the COM server (elevates via UAC)
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\build-tests.ps1  # builds the regression test solution
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build\run-tests.ps1    # runs the regression tests
   </pre>

Alternatively, build from Visual Studio (started with _Run as Administrator_) or directly with MSBuild:

1. Download the source code from this Git repository.
2. Compile the solution hmailserver\source\Server\hMailServer\hMailServer.sln.
   This will build the hMailServer server-part (hMailServer.exe)
3. Build and publish the .NET 10 setup tools with build\build-tools.ps1 (or
   "dotnet build" on hmailserver\source\Tools\hMailServer Tools.sln).
   This covers DB Setup, DB Setup Quick, DB Updater, the Data Directory
   Synchronizer and the Import Tool.
   The Control Panel is a separate .NET 10 solution, hmailserver\source\Tools\ControlPanel.sln.
4. Compile hmailserver\installation\hMailServer64.iss (using Inno Setup 6)
   This will build the hMailServer installation program.

**NOTE:** On a machine running a production hMailServer service, pass `/p:PreBuildEventUseInBuild=false /p:PostBuildEventUseInBuild=false` to MSBuild. The build events stop and re-register the Windows service, which would otherwise disrupt the production installation.

Configuration reference
=======================

Most of the settings above are configured in `Bin\hMailServer.INI` under `[Settings]`, or interactively in the Control Panel under **Settings** (which edits the same settings and offers to restart the service). All settings below show their default values.

This block lists the keys an operator is most likely to need and is not the whole file: about ninety further keys - the timeout family, the quarantine, message-trace, filter-hook, password-policy, account-lockout and OAuth2 groups, and the classic keys carried over from 6.2.10 - are documented one row each, with their defaults, bounds and the code that reads them, on the wiki's [Settings Reference](https://github.com/Progressiverobot/hmailserver/wiki/Settings-Reference). The Control Panel's search box finds every key on either list.

Transport security and authentication:

   <pre>
   MtaStsEnabled=1               ; honor recipient MTA-STS policies when sending
   DaneEnforcementEnabled=1      ; honor recipient DANE/TLSA records when sending
   DnssecValidationEnabled=1     ; validate DNSSEC for DANE and SPF/DKIM/DMARC lookups
   DnssecTrustAnchors=           ; override root trust anchors ("tag alg digesttype hex;...")
   TlsKeyExchangeGroups=X25519MLKEM768:SecP256r1MLKEM768:X25519:secp384r1:secp256r1
                                 ; TLS key-exchange groups, hybrid post-quantum first; reaches every
                                 ; TLS context in the server, inbound and outbound
   TlsCipherSuites13=            ; TLS 1.3 ciphersuites (empty = OpenSSL's defaults; the SslCipherList
                                 ; setting covers TLS 1.2 and below)
   TlsSessionTicketsEnabled=1    ; TLS session tickets (0 also sets SSL_CTX_set_num_tickets(0), because
                                 ; SSL_OP_NO_TICKET alone only makes TLS 1.3 tickets stateful)
   TlsSessionCacheSize=0         ; server-side session cache entries (0 = OpenSSL's, negative = off)
   TlsSessionTimeoutSeconds=0    ; session lifetime (0 = OpenSSL's)
   TlsTicketKeyRotationSeconds=0 ; rotate the ticket key (0 = OpenSSL's single never-rotated key, so
                                 ; every ticket the process issues is sealed under it)
   DkimOversignHeaders=          ; DKIM oversigning (RFC 6376 5.4): header names listed in h= once more
                                 ; than the message carries them (empty = off; From is always included
                                 ; when the feature is on)
   DKIMSignatureValiditySeconds=0 ; add an x= expiry to outgoing DKIM signatures (0 = no x= tag)
   DKIMEnforceSignatureExpiry=1  ; refuse expired DKIM signatures when verifying
   DKIMExpiryClockSkewSeconds=300 ; clock-drift allowance when enforcing x=
   ArcSealingEnabled=0           ; add ARC seals when forwarding (uses the domain's DKIM key)
   TlsRptFromAddress=            ; sender for daily TLS-RPT reports (empty = disabled)
   TlsRptOrganizationName=hMailServer
   </pre>

Automatic certificates (Let's Encrypt):

   <pre>
   AcmeEnabled=0                 ; issue and renew certificates automatically
   AcmeContactEmail=             ; expiry notices from the CA
   AcmeDomains=                  ; comma-separated host names for the certificate
   AcmeDirectoryUrl=https://acme-v02.api.letsencrypt.org/directory
   AcmeHttpPort=80               ; port for http-01 challenges
   AcmeReuseKey=1                ; keep the same key across renewals (keeps TLSA records valid)
   </pre>

   Issued certificates are stored in `Data\ACME`, registered as an SSL certificate, assigned to TLS ports without one, and loaded without a restart.

Web services (MTA-STS hosting, client autoconfiguration):

   <pre>
   WebServicesHttpPort=0         ; 80 to enable
   WebServicesHttpsPort=0        ; 443 to enable (uses the ACME certificate if none is set)
   WebServicesBindAddress=0.0.0.0
   MtaStsHostingEnabled=1        ; serve https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt
   MtaStsPolicyMode=enforce      ; enforce, testing or none
   MtaStsPolicyMaxAge=604800
   MtaStsPolicyMx=               ; override mx patterns (default: the domain's live MX records)
   AutoconfigEnabled=1           ; Thunderbird autoconfig + Outlook autodiscover
   AutoconfigClientHost=         ; host name clients connect to (default: the server's host name)
   </pre>

   DNS records required per domain: point `mta-sts.<domain>`, `autoconfig.<domain>` and `autodiscover.<domain>` at this server, and include them in `AcmeDomains` for HTTPS.

Administration and monitoring:

   <pre>
   RestApiPort=0                 ; REST admin API (Bearer API keys, or HTTP Basic with the administrator password)
   RestApiBindAddress=127.0.0.1  ; TLS is required unless bound to 127.0.0.1
   RestApiCertificateFile=       ; PEM; falls back to the ACME certificate
   RestApiPrivateKeyFile=
   MetricsServerPort=0           ; Prometheus metrics endpoint (/metrics) + health probes
   MetricsServerBindAddress=127.0.0.1
   MetricsServerAuthToken=       ; Bearer token for /metrics. REQUIRED on a non-loopback bind: without a
                                 ; credential /metrics answers 503 there. /livez, /readyz and /healthz are
                                 ; never authenticated, so a load balancer's health check is unaffected
   MetricsServerAuthUsername=    ; HTTP Basic alternative to the token (both must be set to use it)
   MetricsServerAuthPassword=
   MetricsServerCertificateFile= ; PEM; both must be set to serve the metrics port over HTTPS
   MetricsServerPrivateKeyFile=  ; without them the port stays plain HTTP and says so in the application log
   OtelEndpoint=                 ; OTLP/HTTP collector for TRACES (empty = off). Default path /v1/traces, default
                                 ; port 4318. One endpoint per signal; this one carries spans and nothing else
   OtelServiceName=hmailserver   ; service.name on every exported span, metric and log record
   OtelMetricsEndpoint=          ; OTLP/HTTP collector for METRICS (empty = off). Default path /v1/metrics. Pushes
                                 ; the same counters /metrics serves, under the same names - not a second tally.
                                 ; Queue depth, database probes and certificate expiry are NOT exported: they are
                                 ; computed by the metrics listener from database and file reads
   OtelLogsEndpoint=             ; OTLP/HTTP collector for LOGS (empty = off). Default path /v1/logs. The same lines,
                                 ; categories and mask the log files get, with trace and span ids attached where a
                                 ; span was active on the logging thread
   OtelMetricsInterval=60        ; seconds between metric pushes; clamped to 5-3600
   WindowsEventLogEnabled=1      ; write operational events (database down, listener failed to bind, crash,
                                 ; failed backup, disk floor) to the Windows Application log with stable event
                                 ; ids - the table lives in Server/Common/Application/WindowsEventLog.h. On by
                                 ; default because a HEALTHY server writes zero events, while the administrator
                                 ; who never finds this setting is the one whose only monitoring is Event Viewer.
                                 ; Throttled to 5 events per id per 10 minutes; the overflow stays in the ERROR log
   WindowsEventLogLevel=2        ; minimum severity that becomes an event: 1=Critical, 2=+High (default),
                                 ; 3=+Medium, 4=everything ErrorManager reports. Protocol chatter never goes here
   SMTPProxyProtocolEnabled=0    ; accept the HAProxy PROXY protocol (v1 and v2) on the SMTP listener. Off by default.
                                 ; A front-end proxy otherwise makes every connection appear to come from IT, which
                                 ; silently misdirects DNSBL, SPF, greylisting, auto-ban and the IP range rules
   SMTPProxyProtocolTrustedIPs=  ; comma-separated addresses or CIDR ranges allowed to send a PROXY header. EMPTY
                                 ; MEANS NOBODY, which is the safe default - a peer that can rewrite its own source
                                 ; address has defeated every IP-based control here. Matched against the real TCP
                                 ; peer, never an address a header supplied, so chained proxies cannot bootstrap
                                 ; trust. An entry that does not parse matches nothing and is logged once per run.
                                 ; NOTE: once an address is listed the header becomes REQUIRED from it - configure
                                 ; the proxy to send it (HAProxy: send-proxy or send-proxy-v2) or it will be dropped
   SMTPXClientEnabled=0          ; accept the Postfix XCLIENT command. Off by default
   SMTPXClientTrustedIPs=        ; comma-separated addresses or CIDR ranges allowed to use XCLIENT. Empty means
                                 ; nobody. XCLIENT is not advertised in EHLO to an upstream that is not listed, so
                                 ; asking reveals nothing about the deployment
   ScheduledBackupTime=          ; 24-hour local "HH:MM" for a daily backup (empty = none); wins over the interval
   ScheduledBackupIntervalMinutes=0   ; minutes between backups (0 = none). What is backed up and where comes
                                 ; from the existing backup settings; these only decide when
   ScheduledBackupKeepCount=0    ; keep at most N archives (0 = keep everything)
   ScheduledBackupMaxAgeDays=0   ; delete archives older than N days (0 = keep everything). Never applied to
                                 ; the two newest, and never before a new backup has completed successfully
   LogDeleteDays=0               ; prune hMailServer's own date-stamped logs older than N days (0 = keep all)
   ShutdownDrainSeconds=0        ; on stop, wait up to N seconds for active sessions to finish (0 = stop immediately)
   MessageStoreFsync=0           ; force each received message to physical disk before it is acknowledged (1 = on)
   MessageStoreConsistencyCheck=0; periodically cross-check message rows against files on disk (1 = on, read-only)
                                 ; writes hMailServer_messagestore_consistency.report listing any affected messages
   MinimumFreeDiskSpaceMB=100    ; free-space floor on the message-store volume, in megabytes. Below it the
                                 ; server REFUSES mail with a temporary error rather than accept a message it
                                 ; may not be able to write - a sending server retries for days, while a volume
                                 ; that reaches zero costs a database that will not open and a service that
                                 ; will not start, so the floor is biased upwards. 0 = off
   DiskSpaceWarningThresholdMB=1024 ; where the administrator is TOLD, in the application log and the Windows
                                 ; Application log, well before the floor is reached. Absolute rather than a
                                 ; percentage: what decides whether the next message fits is how many bytes
                                 ; are left, not what fraction of the volume they are. 0 = off
   DatabaseStatementTimeout=30   ; seconds a single SQL statement may run before the backend abandons it
                                 ; (0 = no limit). Honoured at connect time by MySQL/MariaDB and PostgreSQL;
                                 ; MS SQL and SQL CE start at ADO's own 30 s and pick this value up on any
                                 ; connection that has run an upgrade or maintenance script. Scripts
                                 ; themselves run under a 30-minute per-statement ceiling regardless
   IMAPExpungeRetentionRecords=5000 ; how many expunge records to keep for QRESYNC/CONDSTORE clients, which
                                 ; use them to learn what vanished while they were away. Pruned to the newest
                                 ; N twelve-hourly and once at service start; a client whose last sync is
                                 ; older than the retained history gets a full resync, never a wrong one.
                                 ; 0 = keep everything, which also defers the prune at the first start after
                                 ; an upgrade on a long-lived installation
   PasswordHashIterations=0      ; PBKDF2 iterations for NEW password hashes (0 = the built-in 210,000; 10,000 to
                                 ; 10,000,000). A stored hash carries its own count, so raising this never breaks
                                 ; a logon: a cheaper hash is re-derived on the next successful logon, a costlier
                                 ; one is left alone - lowering it affects new hashes only. Out of range is
                                 ; reported and read as 0
   PasswordHashMemoryKB=0        ; Argon2id memory for new hashes, in KiB (0 = 19,456; 4,096 to 1,048,576)
   PasswordHashTimeCost=0        ; Argon2id passes for new hashes (0 = 2; 1 to 20). Same re-derive rule as above
   DmarcRptSchemaVersion=1       ; which spelling of the DMARC aggregate report this server SENDS: 1 = RFC 7489
                                 ; Appendix C (what every report consumer deployed today parses), 2 = RFC 9990.
                                 ; Anything else is reported as an error and treated as 1 - a typo must not
                                 ; decide what goes on the wire
   IndexerFullText=0             ; build a term index behind IMAP SEARCH BODY/TEXT. Off by default: it costs a
                                 ; term table of a few kilobytes per message and a backfill pass over every
                                 ; message already delivered, which is an administrator's decision and never an
                                 ; upgrade's. Results are identical on and off - the index only narrows what the
                                 ; substring scan reads. ALSO requires message indexing to be enabled in the
                                 ; Control Panel ("Enable message indexing"); on its own this key indexes nothing
   IndexerFullTextBatchSize=250  ; messages one backfill pass reads and tokenises before the cursor is saved
                                 ; and the thread pauses; clamped to 1-100000
   IndexerFullTextMinTokenLength=3 ; shortest search string the index will answer for; the stored floor is 3,
                                 ; so lower values are clamped up and higher ones only send more searches to
                                 ; the scan; clamped to 3-64
   IndexerFullTextMaxTokensPerMessage=2048 ; distinct terms per message before it is marked always-scanned
                                 ; instead of indexed; clamped to 64-1000000
   IMAPSearchTimeout=60          ; seconds one IMAP SEARCH may run before returning the matches found so far (0 = no limit)
   IMAPSearchMaxMegabytes=2048   ; message content one IMAP SEARCH may read and parse (0 = no limit)
                                 ; SEARCH BODY/TEXT reads every message in the mailbox, so these bound what a single
                                 ; authenticated command can cost; raise them for mailboxes of several hundred thousand messages
   ManageSieveServerPort=0       ; ManageSieve (RFC 5804) script-management service (0 = disabled, standard port 4190)
   ManageSieveServerBindAddress=127.0.0.1  ; STARTTLS is offered when a TLS certificate is configured, and an
                                 ; IP range can require TLS before authentication; otherwise SASL PLAIN
                                 ; travels in the clear, so keep the bind on localhost
   JsonLogging=0                 ; write logs as JSON lines
   </pre>

   **Mail filtering (Sieve, RFC 5228).** Each account can have an active Sieve script that runs during local delivery, supporting `keep`, `fileinto`, `discard`, `redirect` and `stop` with the core tests (`header`, `address`, `envelope`, `exists`, `size`, `allof`/`anyof`/`not`), the `:is`/`:contains`/`:matches` match types, and the extensions **copy** (RFC 3894), **relational** (RFC 5231), **subaddress** (RFC 5233), **imap4flags** (RFC 5232) and **vacation** (RFC 5230, including `:seconds`). `setflag`/`addflag`/`removeflag` and the `:flags` tag are applied to the stored message, so a script can mark its own automated mail read or flag it for attention; only the five system flags (`\Seen` `\Answered` `\Flagged` `\Deleted` `\Draft`) can be stored, and a keyword the server cannot hold is reported in the application log rather than dropped silently. Scripts are edited from the Control Panel account **Sieve** tab (or the COM `Account.SieveScript` property) and stored as files under the data directory. With `ManageSieveServerPort` set, mail clients can upload and manage multiple named scripts over **ManageSieve (RFC 5804)** (`CAPABILITY`, `STARTTLS`, SASL `PLAIN` `AUTHENTICATE`, `PUTSCRIPT`/`CHECKSCRIPT`, `LISTSCRIPTS`, `GETSCRIPT`, `SETACTIVE`, `DELETESCRIPT`, `HAVESPACE`).

   The metrics listener also serves Kubernetes-style health probes: `/livez` (process liveness), `/readyz` (200 only when `StateRunning` and the database has answered a real round trip within the last 20 seconds, else 503 — and 503 while the server is draining/stopping) and `/healthz` (JSON: status, server state, database, uptime). `/metrics` exposes counters and gauges for processed/spam/virus messages, TLS handshakes (success/failure), authentication (success/failure), sessions per protocol, the start time (`hmailserver_start_time_seconds`), database connectivity (`hmailserver_database_connected`, proved by a round trip, plus pool gauges), the SMTP delivery-queue depth and oldest-message age, certificate expiry, work-queue depth, delivery outcomes (`hmailserver_messages_delivered_total`/`_deferred_total`/`_bounced_total`), the message-store consistency result (`hmailserver_messagestore_missing_files`), and per-command and per-query latency histograms (`hmailserver_command_processing_seconds`, `hmailserver_db_query_seconds`).

   REST endpoints: `/api/v1/status`, `/api/v1/domains`, `/api/v1/domains/<name>/accounts` (GET/POST), `/api/v1/accounts/<address>` (DELETE), `/api/v1/queue` (GET), `/api/v1/queue/<id>/retry` (POST), `/api/v1/queue/<id>` (DELETE), `/api/v1/apikeys` (GET/POST, administrator password only), `/api/v1/apikeys/<id>` (DELETE), `/api/v1/tlsa` (GET, publish-ready DANE TLSA records).

Secret protection and least-privilege:

   <pre>
   ProtectStoredSecretsWithDPAPI=1   ; protect reversible stored secrets with machine-scoped Windows DPAPI
   ServiceAccountName=               ; run the service under this account (empty = LocalSystem)
   ServiceAccountPassword=           ; leave empty for virtual/managed accounts
   </pre>

   With `ProtectStoredSecretsWithDPAPI=1` (the default) the database password in `hMailServer.INI` and the database-stored route, fetch-account and SMTP-relayer passwords are stored as machine-scoped DPAPI envelopes instead of the legacy reversible Blowfish encoding. Because the protection is machine-bound, these secrets cannot be decrypted on another machine, so a configuration/database backup restored elsewhere must have them re-entered; set the key to `0` to keep portable Blowfish. Existing Blowfish values are always still read, and the server never loses a secret (it falls back to Blowfish if DPAPI is unavailable).

   Set `ServiceAccountName` to run the Windows service under a least-privilege account instead of LocalSystem — the recommended choice is the password-less virtual account `NT SERVICE\hMailServer` (leave `ServiceAccountPassword` empty). The account must be granted *Log on as a service* and access to the hMailServer program, data and database directories. The setting is applied when the service is (re)registered.

Deliverability and SMTP standards:

   <pre>
   SRSEnabled=0                       ; Sender Rewriting Scheme for forwarded mail (SPF alignment)
   SRSSecret=                         ; HMAC signing secret for SRS addresses (required when SRSEnabled=1)
   MaxSubmissionsPerIPPerMinute=0     ; cap MAIL FROM transactions per source IP per minute (0 = unlimited)
   MaxOutboundPerDestinationPerMinute=0 ; cap outbound messages per destination domain per minute (0 = unlimited)
   </pre>

   The ESMTP extensions `PIPELINING`, `SMTPUTF8`/EAI (RFC 6531/6532), `ENHANCEDSTATUSCODES` (RFC 2034) and `DSN` (RFC 3461) are advertised automatically and need no configuration; legacy `HELO` sessions keep the classic non-enhanced replies.

   With `SRSEnabled=1` the envelope `MAIL FROM` of a message forwarded from an external sender is rewritten to a reversible, HMAC-signed `SRS0=` address at the local forwarding domain so the forwarder (not the original sender) owns the envelope domain and SPF stays aligned; a bounce sent back to that address is verified, decoded and relayed to the original sender. `SRSSecret` must be a stable, non-empty secret — changing it invalidates outstanding SRS addresses (they remain valid for 21 days). SRS replaces the older naive forwarding rewrite.

   `MaxSubmissionsPerIPPerMinute` throttles inbound submissions: when a single source IP starts more `MAIL FROM` transactions than the limit within a 60-second sliding window, further attempts are refused with a `421` until the window drains. `MaxOutboundPerDestinationPerMinute` throttles outbound delivery: when the server has sent the configured number of messages to one destination domain within the window, additional deliveries to that domain are deferred (and retried later) rather than bounced. Both default to `0` (unlimited).

Running in Debug
----------------

If you want to run hMailServer in debug mode in Visual Studio, add the command argument /debug. You find this setting in the Project properties, under Configuration Properties -> Debugging.

Running tests
-------------

hMailServer ships with a full NUnit regression suite which exercises the server end to end over SMTP, IMAP and POP3  -  including anti-spam, anti-virus, TLS, DKIM/DMARC, rules, backup and the COM API. Every release must pass it in full, with zero failures and zero inconclusive, against the exact binary being shipped (the per-release count is quoted on the [Releases page](https://github.com/Progressiverobot/hmailserver/releases)). Without live SpamAssassin and ClamAV installed, the tests that depend on them report *inconclusive* rather than failing; the setup that makes them run is below.

NOTE: When running tests, your local hMailServer installation will be updated with test accounts. Existing domains and accounts are deleted. Each tests prepares the server configuration in different ways. In other words, do not run the automated tests in an environment where you need to preserve hMailServer data.

0. **Run the tests elevated.** The suite starts and stops Windows services - hMailServer itself, and the SpamAssassin service that the outage-handling test takes down on purpose - which a standard user cannot do. Unelevated, those tests report *inconclusive*.
1. Make sure hMailServer.exe is built and can be run. The tests will launch the service.
2. Open the test solution, `\hmailserver\test\hMailServer Tests.sln`
3. In Visual Studio, select Test Explorer from the View-menu. 
4. Locate a test to run under "RegressionTests"
5. Right-click on a test or test category and select "Run".

You can also navigate to the source code for a test, right-click anywhere and select "Run Test(s)" to run it, or run the whole suite from the command line with the NUnit console runner (`hmailserver\test\packages\NUnit.ConsoleRunner.*\tools\nunit3-console.exe`).

For 100% coverage the suite expects three optional integrations (tests degrade to *inconclusive* without them):

   * **SpamAssassin**  -  the JAM Software Windows build the suite was originally written against
     is long gone, so build it from CPAN instead. Install Strawberry Perl, then
     `cpanm --notest Mail::SpamAssassin`. That skips `spamd` on Windows by default; rebuild it
     from the unpacked distribution with `perl Makefile.PL BUILD_SPAMD=yes BUILD_SPAMC=no` and
     `gmake install`, then fetch rules with `sa-update`.

     Two details matter. The suite looks for a *process* named `spamd` before it falls back to
     starting the service, and `spamd` is a Perl script - so copy `perl.exe` to `spamd.exe`
     **inside `C:\Strawberry\perl\bin`**: Perl derives `@INC` from the location of its
     executable, and a copy anywhere else finds no core modules at all. Run it as
     `spamd.exe -T <path-to>\spamd --port 783 --listen 127.0.0.1 --round-robin --nouser-config`
     (`-T` is required because it is on the script's `#!` line), wrapped as a Windows service
     named `SpamAssassinJAM` so the outage-handling test can stop and start it.
   * **ClamAV**  -  the official Windows build, extracted to `C:\clamav` (the suite launches
     `C:\clamav\clamd.exe` by that exact path), with a `clamd.conf` setting `TCPSocket 3310`.
     Bind both `127.0.0.1` and `::1`: hMailServer connects to it as `localhost`, which resolves
     to either. Run `freshclam` first and let the daemon finish loading signatures before the
     first run - it takes a while.

     Note the virus test sends EICAR as a base64 **attachment**, not as the message body.
     ClamAV's EICAR signatures match a whole file, so the string with anything around it is not
     detected; as an attachment ClamAV decodes it back to exactly the EICAR file and matches,
     which is also how a virus would really arrive.
   * **`AddXOriginalRcptTo=1`** in `hMailServer.INI` for the X-Original-Rcpt-To header tests.

The complete dev-tree provisioning recipe (directories, certificates, DB scripts, runtime files) is kept with the maintainer's internal notes; open an issue if you need it to reproduce a build.

Operator documentation
======================

Runbooks for running the server, rather than for changing it, live in
[hmailserver/docs](hmailserver/docs/README.md). The ones most often needed:

* [Diagnosing slow or stalled mail](hmailserver/docs/DiagnosingStalledMail.md) — mail is not moving and the server looks healthy.
* [Upgrading](hmailserver/docs/Upgrading.md) — moving to a new release, or from the original upstream project.
* [Migrating to a different database backend](hmailserver/docs/MigratingDatabaseBackend.md) — in practice, moving off SQL Server Compact, which the installer still picks by default. No tool is needed: back up, repoint, restore.
* [High availability](hmailserver/docs/HighAvailabilityRunbook.md) — warm standby and shared-database topologies, and what is not supported.

Releasing hMailServer
=====================

The release process — the order of operations, the gates that must pass, and why
each one exists — is documented in [RELEASE.md](RELEASE.md).

License
=======

hMailServer is free and open source software, licensed under the GNU Affero General Public License v3.0 (AGPLv3). See [LICENSE](LICENSE) for the full text. Third-party component licenses are listed in [hmailserver/docs/Licenses](hmailserver/docs/Licenses).

Security
========

Please report security vulnerabilities privately - see [SECURITY.md](.github/SECURITY.md).

Contributing
============

See [CONTRIBUTING.md](.github/CONTRIBUTING.md).
