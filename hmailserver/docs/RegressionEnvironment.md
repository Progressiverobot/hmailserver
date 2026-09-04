Setting up the regression environment
======================================

The regression suite (`hmailserver/test/RegressionTests`, roughly 1,800 tests)
does not mock the server. It runs against a real hMailServer service on the
same machine, over SMTP, IMAP and POP3, and through the COM API, and it
creates and deletes domains, accounts and mail as it goes. That makes it worth
far more than a unit suite, and it makes the machine it runs on part of the
test. This document is how to build that machine.

`build/preflight-tests.ps1` is the machine-readable version of everything
below. Every check in it corresponds to a failure that has actually cost a run,
and when a step here is missing the pre-flight names it. Run it before every
run; `Pre-flight passed - safe to run the suite` is the state this document
gets you to.

**Never run the suite against a production installation.** It authenticates as
`Administrator`, binds the live ports, and wipes data.

The pieces
----------

| Piece | What it is for |
|---|---|
| The service, running from the repository's Release build | The code under test. Not an installed copy. |
| SQL Server Compact 4.0 and the bench database | The default backend; the suite upgrades it in place, never recreates it. |
| A short data folder | Long account names overrun `MAX_PATH` under a deep checkout. |
| The test domain and the four standard ports | What every fixture assumes exists at start. |
| ClamAV (`clamd` on 3310) | The live-scanner tests. |
| SpamAssassin as a Windows service named `SpamAssassinJAM` | The SpamAssassin tests, including the one that stops and restarts it. |
| The NUnit console runner and the built test assembly | `build/build-tests.ps1` produces both. |

1. The service runs from the repository build
---------------------------------------------

Build the server (`build/build.ps1 -Configuration Release`), then run
`build/post-build.ps1 -Configuration Release`. It needs elevation and will ask
for it. It copies the runtime DLLs beside `hMailServer.exe`, registers the COM
server and the Windows service, and points the service at
`hmailserver\source\Server\hMailServer\x64\Release\hMailServer.exe` in your
checkout.

The pre-flight's first check reads the service's `PathName` and refuses anything
else. The failure it guards against is a stray installer: running the installer
on the development machine re-points the service at `C:\Program Files\...`, and
from then on every "green" run tests the installed binary, not the one you just
built. If that has happened, `post-build.ps1` again, then re-apply the
permission grant in step 7, then delete `HKLM\SOFTWARE\hMailServer` in **both**
registry views (`reg delete "HKLM\SOFTWARE\hMailServer" /reg:32 /f` and the
same with `/reg:64`) - a leftover `InstallLocation` value redirects the server's
INI lookup and it comes up with empty configuration while still reporting
"Running". `build/make-hmailserver-writable.ps1` deals with the file
permissions the build needs on the output directory.

Provision the output directory as the installed layout expects
(`IMPLEMENTATION-NOTES.md`, "Test-environment recipe", has the full list):
`hMailServer.ini` with a `[Directories]` section and an empty
`AdministratorPassword`, the `Logs`, `Temp` and `Events` folders, a copy of
`DBScripts\` with CRLF line endings, `Languages\`, `dh2048.pem` and `tlds.txt`
from `installation\Extras`, and a `Bin\` folder holding `7za.exe` and a second
copy of `hMailServer.ini`.

2. SQL Server Compact and the bench database
--------------------------------------------

Install the 4.0 x64 package from `hmailserver\installation\SQLCE` (it is the
version `SQLCEConnection` binds to; the x86 package is neither needed nor
installable on x64). Create the database once, through COM:
`Database.CreateInternalDatabase()` followed by `Reinitialize()` on an
`hMailServer.Application` object authenticated as `Administrator`.

From then on the database is **upgraded, never recreated**. `REQUIRED_DB_VERSION`
in `Server/Common/Application/Constants.h` moves whenever a release adds a
column, and a bench that nobody upgraded fails in a way that points somewhere
else entirely: the service refuses the database connection, the first pre-flight
check says "could not start", the ERROR-log check finds a stale error, and
neither says the word "database". The pre-flight's fourth check exists to say it.
Move the bench forward with `build/upgrade-test-database.ps1` **before**
building a server whose required version has moved past it.

Because the bench is always upgraded, no test here takes the fresh-install path
that `CreateTablesMSSQL.sql` provides. `build/check-db-scripts.ps1` covers that
separately by creating a throwaway database from the script; RELEASE.md makes
it a release step for exactly that reason.

3. A short data folder
----------------------

Point the data directory at something short, `C:\HMTest\Data` is the
convention the pre-flight knows. The suite creates accounts with deliberately
long names, and under a deeply nested checkout path the resulting file names
exceed `MAX_PATH`. The symptom is not a clear error but Windows error 206
cascading into hundreds of clean-log assertion failures in tests that never
mention paths.

4. The test domain and the standard ports
-----------------------------------------

The suite expects exactly one domain and exactly four TCP/IP ports at start
(SMTP 25, POP3 110, IMAP 143 and the SMTP submission port). The pre-flight
reads both counts from the running server. A port count of sixteen is a
signature, not a mystery: the TLS fixtures register twelve extra ports and remove
them in teardown, so a run killed while one of them was in flight leaves them
behind. Remove the non-standard ones in the Control Panel, or re-add the four
if there are fewer.

The suite authenticates over COM as `Administrator` with the password
`testar`, falling back to a blank password. Set it to `testar`. It is a bench
credential on a machine that must never hold real mail, which is why it appears
in the scripts in plain text and why it is not a secret.

5. ClamAV
---------

Install ClamAV (`winget install Cisco.ClamAV` works) and copy the installation
to `C:\clamav` - the suite's `CustomAsserts` hard-codes `C:\clamav\clamd.exe`.
A minimal `clamd.conf` is enough: `TCPSocket 3310`, `TCPAddr 127.0.0.1`,
`DatabaseDirectory C:\clamav\database`. Run `freshclam` once, then start
`clamd.exe`. It runs as a bare process, not a Windows service, so the pre-flight
checks for a listener on 3310 rather than for a service.

Warm-up matters: `clamd` accepts connections before its signatures have
finished loading, and hMailServer fails open when the scanner gives no verdict.
Before trusting the anti-virus fixtures on a freshly started `clamd`, send one
EICAR message and confirm "Virus detected" in the log.

6. SpamAssassin
---------------

The JAM Software x64 build works
(`https://downloads.jam-software.de/spamassassin/SpamAssassinForWindows-x64.zip`,
rules bundled under `share\`). Extract it to `C:\SpamAssassin` and wrap
`spamd.exe -i 127.0.0.1 -A 127.0.0.1 -p 783` as a Windows service named
**`SpamAssassinJAM`** - WinSW does this well, and the child process keeps the
name `spamd`, which both the process-gate check and the fixture that stops and
restarts the service rely on.

7. Let the unelevated test runner control the services
-------------------------------------------------------

One fixture stops and restarts `SpamAssassinJAM`; rebuilding the server stops
and starts `hMailServer`. Neither should need an elevated console. Grant
Authenticated Users start/stop on both services with `sc sdset` (the exact
descriptor is in `IMPLEMENTATION-NOTES.md`). The grant is silently dropped when
a service is re-registered, which is why "Access denied" starting the service
after a `post-build.ps1` is the pre-flight's own diagnosis for that failure.

8. Build the tests
------------------

`build/build-tests.ps1` restores the NuGet packages into `hmailserver\test\packages`
- where the project's hint paths point, which a bare `dotnet restore` on the
project does not do - and builds `RegressionTests.dll` for x64. Always x64: an
AnyCPU build of the same project also runs, against stale assemblies, and
passes tests it never actually executed.

`RegressionTests.csproj` is a legacy project that lists every source file
explicitly. A new test file that is not added to it is not merely unrun, it is
invisible, and a green suite says nothing about it. The pre-flight compares the
directory against the project and names any orphan.

9. Run it
---------

`build/preflight-tests.ps1`, then `build/run-tests.ps1`. The runner is
`nunit3-console`; subsets take `-Where "class =~ /RegressionTests.IMAP.Binary/"`.
NUnit writes its failure details only at the end of the run, so a run that
looks quiet for fifty minutes is normal.

Do not `dotnet test` this project. It exits 0 having run nothing.

Never build, run static analysis, or start other heavy work while the suite is
running. A rebuild restarts the service under the tests, the fixtures' restart
detector then fails every test that follows, and the run can neither convict nor
exonerate the code - thirty minutes are simply thrown away.

When a run is interrupted
-------------------------

A run that is stopped part-way leaves state behind, and the next run fails in
ways that point at the wrong thing. RELEASE.md step 4 says never abort a run for
this reason; when it happens anyway, `build/preflight-tests.ps1 -Clean` removes
what it can and names the rest:

- **A stale ERROR log.** One fixture writes a deliberate scanner error and
  removes it in teardown. Left behind, it fails every fixture's setup in the
  next run - the observed shape is 100% of tests failing before doing anything.
- **Test-only settings left in `hMailServer.ini`.** Fixtures that point the
  resolver at a fake DNS server on 127.0.0.1, enable the quarantine, set a POP3
  login delay, a password policy, PROXY-protocol trust, or an OTLP endpoint all
  put the key back in teardown. Any of them surviving fails tests that never
  mention the setting: every DNS lookup waits for the query timeout; every
  anti-spam refusal becomes an acceptance; every connection from the trusted
  address is dropped before the banner. The server caches the INI at start, so
  after `-Clean` restart the service.
- **Orphan domain directories in the data folder.** Several persistence
  fixtures rename the test domain, and the rename moves its directory. Killed
  mid-rename, the directory survives with no domain row, and every later run of
  that fixture fails with "already exists" about a directory it never named.
- **The twelve extra TLS ports** described in step 4.

The environment traps
---------------------

Each of these has produced a red run that looked like a code regression.

- **Anti-virus products that proxy localhost mail traffic in-process.** ESET,
  for one, answers an IMAP literal continuation itself with `+ just send it`
  and rewrites raw SMTP framing. A failure whose text contains that string is
  the proxy, not the server, and it returns after a reboot. Disable mail and
  SSL protocol filtering for localhost.
- **VPN clients that rewrite loopback.** ProtonVPN and WireGuard adapters have
  broken address selection in fixtures that bind a specific local address. The
  pre-flight warns when one is up.
- **Line endings.** The repository checks out CRLF on purpose (`.gitattributes`):
  the database scripts are split on `\r\n\r\n`, the DKIM-signed `.eml`
  resources have body hashes that change with the line ending, and raw-message
  tests send file content verbatim and are refused for bare LF. A tool that
  "normalises" the tree to LF breaks all three at once.
- **Mark-of-the-Web.** Files downloaded rather than cloned carry a zone
  identifier that the service refuses to load. Strip it from the whole tree.
- **"Passes alone, fails in a group."** This points at shared state between
  fixtures - an account, a setting, a port left behind by the fixture that ran
  before - and not at the network, however network-shaped the failure message
  is. Look at what the preceding fixture in the run order changes.
- **A build number that names a tree that no longer exists.** Not an
  environment trap exactly, but the same lesson: the suite's verdict is about
  the binary the service is running, which is why the pre-flight checks the
  path and why RELEASE.md voids a gate the moment anything changes after it.
