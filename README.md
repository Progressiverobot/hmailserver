hMailServer
===========

hMailServer is a free, open source email server for Microsoft Windows, implementing SMTP, IMAP and POP3.

This repository is a maintained fork of the original project, which is no longer developed upstream. It has been brought up to date with a current toolchain, current cryptography, and the transport-security and authentication standards expected of a mail server in 2026 — while remaining a drop-in upgrade for existing hMailServer installations. It is maintained by Christopher Holloway / [Progressive Robot Ltd](https://www.progressiverobot.com).

**[Download the latest release](https://github.com/Progressiverobot/hmailserver/releases/latest)** — a single `hMailServer-x.y.z-x64.exe` installer. Upgrading in place preserves your configuration and mail; the database upgrade chain is continuous from every earlier hMailServer version.

Every release is validated by the full regression suite before it ships — the complete suite, run against the exact binary being released, with live SpamAssassin and ClamAV (real EICAR detection), DMARC evaluated against live DNS, and TLS 1.2/1.3 handshakes end to end. Nothing is skipped or mocked.

**What changed in each version** is on the [Releases page](https://github.com/Progressiverobot/hmailserver/releases). What is planned next, and what is deliberately not, is in [Roadmap.md](Roadmap.md). The release process itself is documented in [RELEASE.md](RELEASE.md).

Contents
--------

* [Capabilities](#capabilities) — what the server does
* [Technology](#technology) — what it is built on
* [Administration](#administration) — the Control Panel and the APIs
* [Building hMailServer](#building-hmailserver)
* [Configuration reference](#configuration-reference)

Capabilities
============

Mail protocols
--------------

* **SMTP** with PIPELINING, ENHANCEDSTATUSCODES, 8BITMIME, SIZE, CHUNKING/BDAT (RFC 3030), DSN delivery status notifications (RFC 3461/3464) and SMTPUTF8/EAI for internationalised addresses.
* **IMAP4rev1 and IMAP4rev2**, with IDLE, MOVE (RFC 6851), UIDPLUS (RFC 4315), CONDSTORE/QRESYNC (RFC 7162), SEARCHRES (RFC 5182), ESEARCH (RFC 4731), SORT, ACL, NAMESPACE, ID (RFC 2971), SPECIAL-USE (RFC 6154) and QUOTA.
* **POP3**, including retrieval from external POP3 accounts on a schedule.
* **Public folders**, shared across accounts with per-user ACLs.

Transport security
------------------

* **TLS 1.2 and 1.3** by default, on implicit-TLS and STARTTLS ports, with SNI and configurable cipher suites.
* **MTA-STS** (RFC 8461) policy discovery and enforcement for outbound mail, and optional hosting of your own policy at `mta-sts.<domain>`.
* **DANE** (RFC 7672) with full in-process **DNSSEC validation** (RFC 4033–4035) — a bogus chain blocks delivery to that host rather than silently downgrading.
* DNSSEC validation also protects SPF, DKIM and DMARC record lookups.
* **TLS-RPT** (RFC 8460) daily aggregate reports to recipient domains.
* **ACME v2 (Let's Encrypt)** built in: certificates are issued, renewed, assigned to TLS ports and hot-reloaded without a restart. The private key is reused across renewals, so published DANE TLSA records stay valid.

Sender authentication and anti-abuse
------------------------------------

* **SPF**, **DKIM** signing and verification (including Ed25519, RFC 8463) and **DMARC** evaluation with alignment.
* **ARC** sealing (RFC 8617) so forwarded mail keeps a verifiable authentication chain.
* **SRS** sender rewriting for forwarded mail, and optional **BATV** (`prvs`) backscatter protection.
* **SpamAssassin** integration, **DNSBL** and **SURBL** lookups, greylisting, HELO/PTR/MX sanity checks and a weighted scoring pipeline.
* **Virus scanning** via ClamAV (clamd or clamscan) or any command-line scanner.
* Attachment blocking, IP ranges with per-range policy, and connection auto-banning after repeated authentication failures.

Account security and authentication
-----------------------------------

* **SCRAM-SHA-256** SASL across IMAP, SMTP submission and POP3, plus **SCRAM-SHA-256-PLUS** channel binding on all three, with deterministic anti-enumeration salts.
* **OAuth2 / OpenID Connect** bearer tokens — SASL XOAUTH2 and OAUTHBEARER (RFC 7628) — validated against an external identity provider's signing key.
* **Argon2id** and **PBKDF2-HMAC-SHA256** password hashing, with transparent upgrade on login, a minimum-accepted-hash policy, and an optional server-side pepper.
* Full RFC 4013 SASLprep of non-ASCII credentials.
* Optional **TOTP two-factor authentication** for administrative logon.

Mail filtering and routing
--------------------------

* **Sieve** (RFC 5228) — a standards-based interpreter runs each account's active script during delivery (`keep`, `fileinto`, `discard`, `redirect`, implicit keep), with an optional **ManageSieve** (RFC 5804) listener so clients can manage scripts over TCP.
* The original rules engine, with global and per-account rules, regular-expression criteria and scripted actions.
* Server-side **event scripts** (VBScript/JScript) on connection, HELO, DATA, accept and delivery events.
* Routes, aliases, distribution lists, catch-all addresses and plus-addressing.
* Multiple smart hosts with automatic failover: separate several hosts with `|` in the relayer field and delivery moves to the next when one cannot be reached.

Operations and observability
----------------------------

* **Prometheus** `/metrics` (database pool, TLS handshakes, delivery queue, authentication outcomes, delivery outcomes, command and query latency) with Kubernetes-style `/livez`, `/readyz` and `/healthz` probes.
* **OpenTelemetry** traces and metrics export, and message-to-session correlation IDs.
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
| MySQL/MariaDB client | MariaDB Connector/C, shipped as `libmysql.dll` with auth plugins — works with MySQL 8 `caching_sha2_password` and MariaDB `ed25519`/`gssapi` out of the box |
| Administration GUI and tools | C# / .NET 10 (WPF, Fluent design) |
| Extensibility | COM/IDispatch API, plus a REST administration API |
| Schema | Database version 6005, upgradeable from every earlier hMailServer release |

**Quality gates.** Every release ships SPDX and CycloneDX SBOMs (Syft). The repository runs CodeQL analysis, Dependabot CVE alerts with grouped update pull requests, a dependency-review gate on pull requests, an installer smoke test that installs the built installer on a clean machine and verifies the service comes up, and a monthly comparison against the original upstream repository so nothing landing there is missed.

Administration
==============

**hMailServer Control Panel** (`hMailCP.exe`) is the bundled administration GUI: a .NET 10 WPF application that talks to the server purely through the COM API. It covers domains, accounts, aliases, distribution lists, routes, rules, IP ranges, TCP/IP ports and SSL bindings, server settings, the live dashboard, the delivery queue, logs, status, backup, SSL certificates, scripts, Sieve scripts and public folders.

* **Ctrl+K** searches every setting by label or INI key — type `delete logs`, `log level` or `LogDeleteDays` and it takes you to the page that owns it.
* **Active Directory pickers**: a read-only browser lists the forest's domains and searches their users, to link an account to an AD user or bulk-import addresses into a distribution list.
* Optional TOTP two-factor authentication on logon.
* Requires the .NET 10 Desktop Runtime, which the installer bundles and installs silently when missing.

**REST administration API** for domains, accounts, the delivery queue, server status and TLSA records, with authenticated access and bounded request handling (a size cap and a receive deadline, so a slow or oversized request cannot occupy a worker).

**Client autoconfiguration**: Thunderbird autoconfig and Outlook autodiscover are served for every local domain, so clients configure themselves from an address and password.

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

Transport security and authentication:

   <pre>
   MtaStsEnabled=1               ; honor recipient MTA-STS policies when sending
   DaneEnforcementEnabled=1      ; honor recipient DANE/TLSA records when sending
   DnssecValidationEnabled=1     ; validate DNSSEC for DANE and SPF/DKIM/DMARC lookups
   DnssecTrustAnchors=           ; override root trust anchors ("tag alg digesttype hex;...")
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
   RestApiPort=0                 ; REST admin API (HTTP Basic auth, administrator password)
   RestApiBindAddress=127.0.0.1  ; TLS is required unless bound to 127.0.0.1
   RestApiCertificateFile=       ; PEM; falls back to the ACME certificate
   RestApiPrivateKeyFile=
   MetricsServerPort=0           ; Prometheus metrics endpoint (/metrics) + health probes
   MetricsServerBindAddress=127.0.0.1
   LogDeleteDays=0               ; prune hMailServer's own date-stamped logs older than N days (0 = keep all)
   ShutdownDrainSeconds=0        ; on stop, wait up to N seconds for active sessions to finish (0 = stop immediately)
   MessageStoreFsync=0           ; force each received message to physical disk before it is acknowledged (1 = on)
   MessageStoreConsistencyCheck=0; periodically cross-check message rows against files on disk (1 = on, read-only)
                                 ; writes hMailServer_messagestore_consistency.report listing any affected messages
   ManageSieveServerPort=0       ; ManageSieve (RFC 5804) script-management service (0 = disabled, standard port 4190)
   ManageSieveServerBindAddress=127.0.0.1  ; SASL PLAIN over plaintext; bind to localhost unless TLS-fronted
   JsonLogging=0                 ; write logs as JSON lines
   </pre>

   **Mail filtering (Sieve, RFC 5228).** Each account can have an active Sieve script that runs during local delivery, supporting `keep`, `fileinto`, `discard` and `redirect` with the core tests (`header`, `address`, `exists`, `size`, `allof`/`anyof`/`not`) and `:is`/`:contains`/`:matches` match types. Scripts are edited from the Control Panel account **Sieve** tab (or the COM `Account.SieveScript` property) and stored as files under the data directory. With `ManageSieveServerPort` set, mail clients can upload and manage multiple named scripts over **ManageSieve (RFC 5804)** (`CAPABILITY`, SASL `PLAIN` `AUTHENTICATE`, `PUTSCRIPT`/`CHECKSCRIPT`, `LISTSCRIPTS`, `GETSCRIPT`, `SETACTIVE`, `DELETESCRIPT`).

   The metrics listener also serves Kubernetes-style health probes: `/livez` (process liveness), `/readyz` (200 when `StateRunning` and the database pool is connected, else 503 — and 503 while the server is draining/stopping) and `/healthz` (JSON: status, server state, database). `/metrics` exposes counters and gauges for processed/spam/virus messages, TLS handshakes (success/failure), authentication (success/failure), sessions per protocol, uptime, database up/pool, the SMTP delivery-queue depth, delivery outcomes (`hmailserver_messages_delivered_total`/`_deferred_total`/`_bounced_total`), the message-store consistency result (`hmailserver_messagestore_missing_files`), and aggregate per-command processing latency (`hmailserver_command_processing_seconds` summary).

   REST endpoints: `/api/v1/status`, `/api/v1/domains`, `/api/v1/domains/<name>/accounts` (GET/POST), `/api/v1/accounts/<address>` (DELETE), `/api/v1/queue` (GET), `/api/v1/queue/<id>/retry` (POST), `/api/v1/queue/<id>` (DELETE), `/api/v1/tlsa` (GET, publish-ready DANE TLSA records).

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
