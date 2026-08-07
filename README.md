hMailServer 6.2
===============

hMailServer is an open source email server for Microsoft Windows, implementing SMTP, IMAP and POP3.

This repository is a modernized fork of the original project (which is no longer maintained upstream). It has been brought up to date with a current toolchain, current cryptography, and the transport-security standards expected of a mail server in 2026. It is maintained by Christopher Holloway / [Progressive Robot Ltd](https://www.progressiverobot.com).

**Production status:** version **6.2.8** is released - [download the installer](https://github.com/Progressiverobot/hmailserver/releases/latest) (`hMailServer-6.2.8-x64.exe`). **6.2.8 is a Control Panel bug-fix release**: list editors were rendering every row blank, and the Control Panel stopped working entirely after an hMailServer service restart until it was closed and reopened - both are fixed, together with a refresh of the GUI's dependencies - see *6.2.8* below. The server core is unchanged since 6.2.6 (opt-in IMAP4rev2, the Control Panel redesign and complete settings coverage). It is validated by the full regression suite: **898 of 898 tests passing, zero failures, zero inconclusive**, including live SpamAssassin, ClamAV (real EICAR detection), DMARC evaluation against live DNS, and TLS 1.2/1.3 handshakes end to end. The bundled administration GUI is the modern .NET 8 **Control Panel** (the classic Administrator has been retired).

What's new in 6.0
=================

**Toolchain and platform**

   * Visual Studio 2026 build tools (platform toolset v145), 64-bit only
   * OpenSSL 4.0.x, Boost 1.91, PostgreSQL 18 (libpq), .NET Framework 4.8.1 for the tools
   * MySQL/MariaDB client: MariaDB Connector/C, bundled as `libmysql.dll` + auth plugins - works with both MySQL and MariaDB out of the box, including MySQL 8 `caching_sha2_password` and MariaDB `ed25519`/`gssapi`
   * PBKDF2-HMAC-SHA256 password hashing (transparent upgrade on login), TLS 1.2/1.3 defaults
   * Database version 6005; the upgrade chain is continuous from every earlier hMailServer release (MySQL, MS SQL, PostgreSQL, SQL CE)

**Outbound transport security**

   * MTA-STS (RFC 8461) policy discovery and enforcement
   * DANE (RFC 7672) with full in-process DNSSEC validation (RFC 4033-4035)  -  bogus chains block delivery to that host
   * DNSSEC validation also protects SPF/DKIM/DMARC TXT lookups
   * TLS-RPT (RFC 8460): daily aggregate reports sent to recipient domains

**Sender authentication**

   * DMARC evaluation as part of the anti-spam pipeline
   * ARC sealing (RFC 8617) for forwarded mail
   * Ed25519 DKIM signing and verification (RFC 8463)

**Automation and operations**

   * ACME v2 (Let's Encrypt) built in: certificates are issued, renewed, assigned to TLS ports and hot-reloaded automatically; the private key is reused across renewals so published DANE TLSA records stay valid
   * REST administration API (domains, accounts, delivery queue, server status, TLSA records)
   * Prometheus metrics endpoint and optional JSON-formatted logs
   * Web services server: hosts MTA-STS policies (`mta-sts.<domain>`), Thunderbird autoconfig and Outlook autodiscover for all local domains

**Protocol and client improvements**

   * IMAP MOVE (RFC 6851), ID (RFC 2971) and SPECIAL-USE (RFC 6154)
   * SMTP 8BITMIME
   * hMailServer Administrator: live dashboard, optional TOTP two-factor authentication, and a "Server features" dialog for all of the settings above

What's new in 6.2
=================

The 6.2.x line adds a modern administration experience plus a wave of
authentication, filtering, deliverability and observability work. Everything is
**additive and default-off** — an existing installation upgrades with no functional
change until the new settings are turned on.

**hMailServer Control Panel (the new GUI)**

   * A modern .NET 8 (WPF, Fluent design) administration application, `hMailCP.exe`, is now the **sole bundled GUI** — the classic Administrator has been retired. It talks to the server purely through the COM API and reaches functional parity with the classic tool (domains, accounts, aliases, distribution lists, routes, rules, IP ranges, TCP/IP ports + SSL bindings, server settings, live dashboard, queue, logs, status, backup, SSL certificates, scripts and public folders).
   * Optional TOTP two-factor authentication for the GUI logon (shares the same secret as the classic Administrator).
   * **Active Directory account pickers (new in 6.2.4):** a read-only AD browser lists the forest's domains and searches their users. **"Browse Active Directory…"** on an account's Directory tab fills the AD domain / user name and links the account; **"Add from AD…"** bulk-imports the selected accounts' e-mail addresses into a distribution list. Built on `System.DirectoryServices` and validated end-to-end against a live domain controller.
   * Requires the .NET 8 Desktop Runtime, which the installer bundles and installs silently when missing.

**Authentication modernization**

   * SCRAM-SHA-256 SASL across IMAP, SMTP submission and POP3, plus SCRAM-SHA-256-**PLUS** channel binding on all three; deterministic anti-enumeration salts.
   * Argon2id password KDF option, a hash-policy engine (`MinimumAcceptedHashAlgorithm`) with SCRAM minimum-hash enforcement, and an optional server-side password pepper.
   * OAuth2 bearer authentication — SASL XOAUTH2 + OAUTHBEARER (RFC 7628).
   * Full RFC 4013 SASLprep of non-ASCII credentials.

**Mail filtering — Sieve (RFC 5228) + ManageSieve (RFC 5804)**

   * A standards-based Sieve interpreter runs each account's active script during local delivery (`keep`/`fileinto`/`discard`/`redirect` + implicit keep), alongside the existing proprietary rules engine. Per-account scripts are stored on disk and exposed through the COM API; an optional ManageSieve listener manages named scripts over TCP. The Control Panel account dialog has a Sieve editor tab.

**Deliverability & SMTP standards**

   * SMTPUTF8/EAI, PIPELINING, ENHANCEDSTATUSCODES, DSN (RFC 3461/3464), SRS for forwarded mail, CHUNKING/BDAT (RFC 3030), and optional BATV (`prvs`) backscatter protection.

**Operability & observability**

   * Prometheus `/metrics` (database pool, TLS handshakes, delivery queue, auth success/failure, delivery outcomes, command + DB query latency) and Kubernetes-style `/livez` `/readyz` `/healthz` probes on the metrics listener.
   * Optional slow-query log (with every SQL string literal redacted), graceful-shutdown drain, configurable message-store fsync, a read-only message-store consistency check + recovery report, log retention, message-to-session correlation IDs, and a documented active/passive HA runbook.

**Supply chain & quality gates**

   * SPDX + CycloneDX SBOMs (Syft) attached to every release, Dependabot CVE alerts + grouped update PRs, and a dependency-review PR gate.

6.2.8
=====

A Control Panel bug-fix release. No server-core changes - the two defects below
were both in the GUI, and both made it look like the server was broken when it
was not.

   * **List editors showed blank rows** ([#6](https://github.com/Progressiverobot/hmailserver/issues/6)).
     Every data-driven list pane rendered the right *number* of rows with nothing
     in them: domain aliases, SURBL servers, DNS blacklists, the anti-spam and
     greylisting white lists, blocked attachments, groups, server messages,
     external POP3 accounts and account rules. Adding an entry appeared to create
     an empty row, and only the Edit dialog showed the value you had typed. The
     row model exposed its data as a *field*, and WPF data binding resolves
     properties only - so every generated column silently bound to nothing.
     Reported against domain aliases; the fix restores all of the affected panes.
   * **The Control Panel died after a service restart** ([#7](https://github.com/Progressiverobot/hmailserver/issues/7)).
     The COM server lives inside the hMailServer service process, so restarting
     the service invalidated every interface pointer the GUI held. Afterwards
     each page failed with *"The RPC server is unavailable"* - including restarts
     the Control Panel performed itself after saving a setting - and the only
     cure was to close and reopen it. The session now verifies the link before
     use and re-authenticates transparently when the service has gone.
   * **Restarting from the Control Panel reconnects immediately** and waits for
     the server to finish starting, rather than latching onto a service that has
     registered with Windows but is still opening its database - which produced a
     misleading "no connection to the database" error.
   * **A restart performed by anyone else is detected and healed** on the next
     thing you do, with a "Connection restored" notification and a refresh of the
     page on screen. No action is needed.
   * **A server that really is unreachable now reports it readably** instead of
     raising the unhandled-exception dialog, and the Control Panel no longer
     starts an hMailServer service that the administrator deliberately stopped.

   Both fixes were verified end to end against a live hMailServer: reproduced on
   the previous build, then confirmed fixed on this one.

**Dependencies**

   * Control Panel: WPF-UI 3.0.5 &rarr; 4.3.0, QRCoder 1.6.0 &rarr; 1.8.0 and the
     `System.Management` / `System.ServiceProcess.ServiceController` /
     `System.DirectoryServices` packages 8.0.0 &rarr; 10.0.10.
   * CI: `actions/checkout` v4 &rarr; v7, `actions/setup-dotnet` v4 &rarr; v5,
     `github/codeql-action` v3 &rarr; v4, `actions/dependency-review-action`
     v4 &rarr; v5, `actions/upload-artifact` v4 &rarr; v7.
   * LiveCharts is **held at 2.0.0-rc2**. The proposed 2.0.5 upgrade renders the
     dashboard chart area opaque white over the dark theme and hides the
     empty-state labels, so it is deferred until that is resolved upstream.

6.2.7
=====

A Control Panel usability release. No server-core changes; every improvement is
in the administration GUI, closing the remaining "standard desktop app" gaps so
common tasks no longer need hand-typed paths, external tools or guesswork.

   * **File/folder pickers everywhere.** Every field that holds a file-system path
     now has a `...` browse button: the backup destination and restore file, the
     archive folder, the ACME certificate folder, the OAuth2 public key, the REST
     API / Web Services TLS certificate and key files, the ClamWin executable and
     database folder, and the DKIM private key.
   * **One-click DKIM.** Domain &rarr; DKIM gains "Generate key pair", which creates an
     RSA-2048 key, saves the private key, fills the path, and shows the exact
     `selector._domainkey` **DNS TXT record** (`v=DKIM1; k=rsa; p=...`) with a Copy
     button - no more running OpenSSL by hand.
   * **Passwords.** All password boxes get a reveal (eye) toggle; the account editor
     and the quick-create form get a "Generate strong password" button (cryptographic
     RNG, copied to the clipboard); and the external POP3 fetch-account password is
     now masked instead of shown in clear text.
   * **Inputs.** The auto-reply expiry is a date picker; numeric server settings and
     the collection editors use up/down number boxes; every editor dialog now obeys
     **Enter** (save) and **Esc** (cancel); and MX query / Diagnostics output has a Copy
     button.
   * **Window state.** The main window remembers its size, position and maximized
     state between sessions, and a save confirmation toast appears after saving.

6.2.6
=====

A Control Panel and installer polish release. The server core is unchanged in
behaviour apart from one new opt-in protocol mode; everything else refines the
administration experience.

   * **IMAP4rev2 (RFC 9051)** as an opt-in session mode. The server advertises
     `IMAP4rev2` and `ENABLE IMAP4rev2` switches the connection to RFC 9051
     semantics (ESEARCH responses by default, `\Recent`/`RECENT` dropped from
     SELECT/EXAMINE/STATUS, the obsolete `[UNSEEN]` response code suppressed, and
     UTF-8 acceptance). IMAP4rev1 behaviour is unchanged until a client opts in.
   * **Control Panel visual redesign.** A central, theme-aware colour-token system
     replaces scattered hardcoded colours (success/warning/danger/info and the log
     palette now adapt to light/dark/high-contrast); the sidebar navigation gains a
     proper Fluent selection style and a brand keyboard-focus ring; live-log colours
     are legible on light theme; data grids get readable row dividers and balanced
     columns; settings forms use a readable column width with right-sized inputs;
     KPI colours encode state rather than category; destructive buttons are softened;
     dashboard charts show clear "no activity" placeholders; and the Welcome page is a
     grid of clickable quick-action tiles.
   * **Complete settings coverage.** Every configurable server setting now has a GUI
     control. New in the Control Panel: OAuth2 / external-token authentication, SRS
     and BATV, submission/outbound rate limits, OpenTelemetry + slow-query log,
     connection timeouts, delivery/queue tuning, search-indexing, message-archiving
     and other INI knobs, `Logging.Device`/`LogFormat`, the cache size caps, the
     domain-level Active Directory link, and a write-only editor for secrets. See the
     Control Panel's own pages for the full coverage map.
   * **Two-factor authentication setup** now renders a real scannable QR code (plus a
     grouped manual key with a copy button) and a larger, clearer verification field.
   * **Installer.** The custom database-type wizard page is DPI-scaled, the dead
     legacy dependency installers (MSI/IE6/MDAC/JET/.NET 2.0) are removed, the copy is
     modernised, and the wizard imagery is refreshed to the current brand.

6.2.5
=====

A critical fix release for default fresh installs. The 6.2.4 installer's default
configuration (built-in SQL Server Compact database with DPAPI secret protection,
both shipped defaults) could not connect to its own database after a clean install.
Two independent defects were responsible; both are fixed and the default install
is now validated end-to-end on a clean Windows Server 2025 machine:

   * **DPAPI database-password truncation.** Protected INI secrets (the database
     password, and likewise long OAuth2 HMAC secrets / the password pepper) are
     stored as a DPAPI base64 envelope that exceeds 255 characters. The INI reader
     used a fixed 255-character buffer, silently truncating the value; the truncated
     blob failed to decrypt and yielded an empty password, so the server reported
     SQL CE "Authentication failed" (error 25028). The buffer was enlarged to 4096.
   * **Fresh-install database version.** The create-table scripts still stamped the
     new database as schema 6004 while the server required 6005, so a clean install
     reported "database too old". The create scripts now stamp 6005 (the required
     column was already present).

Active Directory authentication was also validated end-to-end against a live domain
controller (COM `ValidatePassword` for DNS and NetBIOS domain forms, plus real IMAP
login), and the Control Panel reached full settings parity with the classic
Administrator (the Server-status page and the per-account rule criteria/action
editor were the last items confirmed).

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
   Perl Configure no-asm VC-WIN64A --prefix=%cd%\out64 --openssldir=%cd%\out64 -D_WIN32_WINNT=0x600
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
   b2 debug release threading=multi link=static --with-thread --with-filesystem --with-regex --with-chrono --with-atomic address-model=64 stage --build-dir=out64 -j 4
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
3. Compile the solution hmailserver\source\Tools\hMailServer Tools.sln.
   This will build hMailServer related tools, such as hMailServer Administrator and hMailServer DB Setup.
4. Compile hmailserver\installation\hMailServer.iss (using InnoSetup)
   This will build the hMailServer installation program.

**NOTE:** On a machine running a production hMailServer service, pass `/p:PreBuildEventUseInBuild=false /p:PostBuildEventUseInBuild=false` to MSBuild. The build events stop and re-register the Windows service, which would otherwise disrupt the production installation.

Configuring the 6.0 features
============================

Most new features are configured in `Bin\hMailServer.INI` under `[Settings]`, or interactively in hMailServer Administrator under **File -> Server features...** (which edits the same settings and offers to restart the service). All settings below show their default values.

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

hMailServer ships with a full regression suite (898 NUnit tests) which exercises the server end to end over SMTP, IMAP and POP3  -  including anti-spam, anti-virus, TLS, DKIM/DMARC, rules, backup and the COM API. Release 6.0.0 passes the complete suite with zero failures and zero inconclusive results.

NOTE: When running tests, your local hMailServer installation will be updated with test accounts. Existing domains and accounts are deleted. Each tests prepares the server configuration in different ways. In other words, do not run the automated tests in an environment where you need to preserve hMailServer data.

1. Make sure hMailServer.exe is built and can be run. The tests will launch the service.
2. Open the test solution, `\hmailserver\test\hMailServer Tests.sln`
3. In Visual Studio, select Test Explorer from the View-menu. 
4. Locate a test to run under "RegressionTests"
5. Right-click on a test or test category and select "Run".

You can also navigate to the source code for a test, right-click anywhere and select "Run Test(s)" to run it, or run the whole suite from the command line with the NUnit console runner (`hmailserver\test\packages\NUnit.ConsoleRunner.*\tools\nunit3-console.exe`).

For 100% coverage the suite expects three optional integrations (tests degrade to *inconclusive* without them):

   * **SpamAssassin**  -  the JAM Software Windows build (`https://downloads.jam-software.de/spamassassin/SpamAssassinForWindows-x64.zip`), extracted to `C:\SpamAssassin`, with `spamd.exe -i 127.0.0.1 -A 127.0.0.1 -p 783` running  -  ideally wrapped as a Windows service named `SpamAssassinJAM` so outage-handling tests can stop and start it.
   * **ClamAV**  -  installed to `C:\clamav` with `clamd` listening on TCP 3310 and current freshclam definitions. Let the daemon finish loading signatures before the first run.
   * **`AddXOriginalRcptTo=1`** in `hMailServer.INI` for the X-Original-Rcpt-To header tests.

The complete dev-tree provisioning recipe (directories, certificates, DB scripts, runtime files) is kept with the maintainer's internal notes; open an issue if you need it to reproduce a build.

Releasing hMailServer
=====================

Without finding any serious issues:

1. Run all integration tests on supported versions of Windows and the different supported databases. 
2. Run all server stress tests
3. Enable Gflags (gflags /p /enable hmailserver.exe) and run all integration tests to check for memory issues
4. Run for at least 1 week in production for hMailServer.com
5. Wait for at least 500 downloads of the beta version

License
=======

hMailServer is free and open source software, licensed under the GNU Affero General Public License v3.0 (AGPLv3). See [LICENSE](LICENSE) for the full text. Third-party component licenses are listed in [hmailserver/docs/Licenses](hmailserver/docs/Licenses).

Security
========

Please report security vulnerabilities privately - see [SECURITY.md](.github/SECURITY.md).

Contributing
============

See [CONTRIBUTING.md](.github/CONTRIBUTING.md).
