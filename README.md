hMailServer 6.0
===============

hMailServer is an open source email server for Microsoft Windows, implementing SMTP, IMAP and POP3.

This repository is a modernized fork of the original project (which is no longer maintained upstream). It has been brought up to date with a current toolchain, current cryptography, and the transport-security standards expected of a mail server in 2026.

What's new in 6.0
=================

**Toolchain and platform**

   * Visual Studio 2026 build tools (platform toolset v145), 64-bit only
   * OpenSSL 4.0.x, Boost 1.91, PostgreSQL 18 (libpq), .NET Framework 4.8.1 for the tools
   * PBKDF2-HMAC-SHA256 password hashing (transparent upgrade on login), TLS 1.2/1.3 defaults
   * Database version 6001; upgrade scripts from 5.7 included (MySQL, MS SQL, PostgreSQL)

**Outbound transport security**

   * MTA-STS (RFC 8461) policy discovery and enforcement
   * DANE (RFC 7672) with full in-process DNSSEC validation (RFC 4033-4035) — bogus chains block delivery to that host
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
   * InnoSetup 5.5.4a (non-unicode version) — only needed to build the installer
   * Perl 5 (https://strawberryperl.com/) — only needed to build OpenSSL
   * Python 3 (https://www.python.org/) — only needed to build libpq
   
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
   MetricsServerPort=0           ; Prometheus metrics endpoint (/metrics)
   MetricsServerBindAddress=127.0.0.1
   JsonLogging=0                 ; write logs as JSON lines
   </pre>

   REST endpoints: `/api/v1/status`, `/api/v1/domains`, `/api/v1/domains/<name>/accounts` (GET/POST), `/api/v1/accounts/<address>` (DELETE), `/api/v1/queue` (GET), `/api/v1/queue/<id>/retry` (POST), `/api/v1/queue/<id>` (DELETE), `/api/v1/tlsa` (GET, publish-ready DANE TLSA records).

Running in Debug
----------------

If you want to run hMailServer in debug mode in Visual Studio, add the command argument /debug. You find this setting in the Project properties, under Configuration Properties -> Debugging.

Running tests
-------------

hMailServer source code contains a number of automated tests which excercises the basic functionality. When adding new features or fixing bugs, corresponding tests should be added. hMailServer tests are implemented using NUnit. To run them in Visual Studio, follow these steps:

NOTE: When running tests, your local hMailServer installation will be updated with test accounts. Existing domains and accounts are deleted. Each tests prepares the server configuration in different ways. In other words, do not run the automated tests in an environment where you need to preserve hMailServer data.

1. Make sure hMailServer.exe is built and can be run. The tests will launch the service.
2. Open the test solution, `\hmailserver\test\hMailServer Tests.sln`
3. In Visual Studio, select Test Explorer from the View-menu. 
4. Locate a test to run under "RegressionTests"
5. Right-click on a test or test category and select "Run".

You can also navigate to the source code for a test, right-click anywhere and select "Run Test(s)" to run it.

Releasing hMailServer
=====================

Without finding any serious issues:

1. Run all integration tests on supported versions of Windows and the different supported databases. 
2. Run all server stress tests
3. Enable Gflags (gflags /p /enable hmailserver.exe) and run all integration tests to check for memory issues
4. Run for at least 1 week in production for hMailServer.com
5. Wait for at least 500 downloads of the beta version
