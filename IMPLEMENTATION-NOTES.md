# hMailServer 6.0 — Implementation Notes

Internal log of the 6.0 modernization work. Read this before modifying any of the
6.0 features. Structural overview lives in [AGENTS.md](AGENTS.md); user-facing build
and configuration instructions live in [README.md](README.md).

## Moving to a new development machine — checklist

1. Copy the whole repository folder (git history is not present on the reference
   machine; the working tree IS the source of truth).
2. Copy the built third-party libraries folder (reference machine: `C:\Dev\hMailLibs`)
   containing `boost_1_91_0`, `openssl-4.0.1`, `postgresql-18.3` — or rebuild them
   per README.md. Copying is much faster.
3. Set the machine environment variable `hMailServerLibs` to that folder.
   New shells only: `$env:hMailServerLibs = '<path>'` per session if the machine
   variable was set after the shell opened.
4. Install VS 2026 Build Tools (workloads: VCTools incl. recommended, VC.ATL,
   ManagedDesktopBuildTools) — `winget install Microsoft.VisualStudio.BuildTools`
   with the overrides shown in README.md. Install the .NET Framework 4.8.1
   Developer Pack (`winget install Microsoft.DotNet.Framework.DeveloperPack_4`).
5. If files were downloaded/zipped in transit, strip Mark-of-the-Web before building
   the C# tools: `Get-ChildItem -Recurse | Unblock-File`. If MSBuild caches a MOTW
   failure, kill stale MSBuild nodes and build with `/nr:false`.
   ALSO verify line endings survived the transfer: an LF-normalized tree breaks
   `SQLScriptParser` (DB creation finds "no SQL commands"), DKIM-signed `.eml`
   test resources, and raw-message tests. Convert text files back to CRLF if
   `(Get-Content file -Raw) -notmatch "\r\n"`.
6. `build\Find-MsBuild.ps1` locates the newest VS automatically via vswhere.
   Reboot after installing Build Tools BEFORE attempting any MSI installs — a
   pending-reboot state wedges msiexec (elevated installers hang or exit 1602).
7. If the new machine does NOT run a production hMailServer service, the vcxproj
   pre/post build events are safe to re-enable (drop the
   `/p:PreBuildEventUseInBuild=false /p:PostBuildEventUseInBuild=false` flags) and
   the regression test suite becomes runnable — see "Test-environment recipe" at
   the end of this file for the full dev-tree setup (verified working). On any
   machine with a production install, keep the events disabled and do not run the
   tests (they wipe data and bind live ports).

## Toolchain facts (verified working)

- VS2026 BuildTools 18.6.x, MSVC 14.51 = PlatformToolset `v145`.
- Boost 1.91: `b2 --with-thread --with-filesystem --with-regex --with-chrono --with-atomic`
  (NO `--with-system` — header-only now; no toolset pin needed).
- OpenSSL 4.0.1: `nmake build_libs` then `nmake install_dev install_runtime_libs`.
  `nmake install_sw` FAILS (apps/openssl.c `internal/e_os.h` bug) — skip the apps.
  **MSVC 14.51 ICE:** `ssl\record\methods\ssl3_cbc.c` crashes the optimizer at
  `/O2` (fatal error C1001). Workaround: compile that file manually at `/O1`
  for BOTH objects — `libssl-shlib-ssl3_cbc.obj` (with `-DOPENSSL_PIC`) and
  `libssl-lib-ssl3_cbc.obj` (without) — using the same remaining flags nmake
  shows, then re-run `nmake build_libs` (it skips up-to-date objects).
- libpq 18.3 (MSVC/meson): in `cmd /c`, set PATH (winflexbison, Python) BEFORE
  calling vcvars64, then `set CC=cl&&` (no trailing space) →
  `meson setup builddir --buildtype=release -Dssl=openssl -Dextra_include_dirs=... -Dextra_lib_dirs=...`
  → `meson compile -C builddir src/interfaces/libpq/libpq:shared_library`.
  Artifacts: `builddir\src\interfaces\libpq\{libpq.lib,libpq.dll}`.
- Linked runtime DLLs: `libcrypto-4-x64.dll`, `libssl-4-x64.dll`, `libpq.dll` (Boost static).
- `stdafx.h`: WINVER/_WIN32_WINNT/_WIN32_WINDOWS and `BOOST_USE_WINAPI_VERSION`
  are 0x0601 (Boost 1.91 dropped XP).
- Compiler runs with `/WX` — zero warnings required.

## OpenSSL 4.0 API notes

- `X509_cmp_time` is DEPRECATED (C4996 + /WX = error) → use `ASN1_TIME_to_tm` + `_mkgmtime`.
- `SHA256()` one-shot is NOT deprecated. `EVP_Digest`/`EVP_DigestSign`/`EVP_DigestVerify`
  used for everything else. Keys built via `EVP_PKEY_fromdata` + `OSSL_PARAM_BLD`.
- `ERR_load_EVP_strings` removed (was deprecated; call deleted from DKIM::Initialize).

## CStdStr / codebase conventions

- `String`/`AnsiString` are CStdStr. `Format` specifiers: `%hs` narrow strings,
  `%d`, `%I64d`. Use `.c_str()` on const refs (`GetBuffer()` is non-const).
- **FormatV fix (test round):** `CStdStr::FormatV` used to count the required
  buffer with `_vsctprintf((const wchar_t*)szFormat, ...)` — i.e. ALWAYS the
  wide counter, even for `CStdStr<char>`. The garbage length made `vsprintf_s`
  fail-fast (c0000409 ucrtbase abort), killing the service on the first
  `AnsiString::Format` whose output exceeded the miscounted buffer (first hit:
  `HashCreator::GeneratePBKDF2` via `InterfaceAccount::put_Password`; COM
  clients saw "The remote procedure call failed"). Fixed with type-dispatched
  `ssvscprintf` overloads (`_vscprintf` narrow / `_vscwprintf` wide) in
  `StdString.h`. Latent in EVERY narrow Format call site — only surfaced when
  the regression tests first ran.
- `LOG_APPLICATION` (and friends) are if-style macros — surrounding `if`/`else`
  branches MUST use braces or you get C2181 "illegal else".
- `StringParser::SplitString("$h1$...","$")` yields `["","h1",iter,salt,key]` (5 parts).
- New persistent settings follow: getter + member default in
  `Common\Application\IniFileSettings.h`, `ReadIniSetting*_` call in the .cpp,
  control in `Tools\Administrator\Dialogs\formServerFeatures.cs`.
- New source files must be registered in `source\Server\hMailServer\hMailServer.vcxproj`.

## What was changed, per round

### Phase 3 — toolchain + core modernization
- All vcxprojs → v145; boost_1_91_0 / openssl-4.0.1 paths; `post-build.bat` and
  installer ship `libcrypto-4-x64.dll`/`libssl-4-x64.dll`.
- `HashCreator` rewritten on `EVP_Digest` (no deprecated SHA1_/MD5_Init),
  constant-time compare, PBKDF2-HMAC-SHA256 added (`$h1$iter$salt$key`, 210k iters).
- `Crypt`: ETPBKDF2=4; `GetHashType` detects `$h1$`; `PasswordValidator`
  transparently rehashes to PBKDF2 on login when `PreferredHashAlgorithm=4` (default).
- TLS: `SslVersions` default → 24 (TLS 1.2+1.3) in all CreateTables/upgrade scripts.
- DMARC: `Common\AntiSpam\DMARC\{DMARC,SpamTestDMARC}` ; settings
  `ASDMARCEnabled` (default 1) / `ASDMARCFailureScore` (5); COM IDL ids 39/40 on
  `IInterfaceAntiSpam`.
- IMAP: MOVE + UID MOVE (RFC 6851), ID (RFC 2971); enum IMAP_MOVE=136 / IMAP_ID=137.
- SMTP: 8BITMIME advertised and accepted.
- Version: DB 5709 → **6001**, `Upgrade5708to6001*.sql`, Version.h 6.0.0/B1,
  DBUpdater chain entry (5708,6001).
- C# tools: all csproj → .NET Framework 4.8.1.
- Administrator dashboard: `Controls\DashboardControls.cs` (ArcGauge/StatCard/Sparkline),
  `Main panes\ucDashboard.cs`, `Nodes\NodeDashboard.cs` (icon must exist in
  formMain imageList or it throws).

### Enterprise round 1
- **MTA-STS outbound** (RFC 8461): `SMTP\TlsPolicy.{h,cpp}` — policy TXT + HTTPS
  fetch (HTTP/1.0 + Connection: close to avoid chunked, 15s timeouts), static cache
  (mutex never held during network I/O; max_age cap 1y, revalidate 1h, negative 30m).
  Enforce mode filters MX by `mx:` patterns and forces TLS + peer verification.
  INI `MtaStsEnabled=1`.
- **DANE outbound** (RFC 7672): `TCPIP\DaneVerifier.{h,cpp}` (usage 3 only,
  selector 0/1, matching 0/1/2). TLSA present → RequireTls + pin.
  INI `DaneEnforcementEnabled=1`.
- Plumbing: `ServerInfo` += RequireTls/RequirePeerVerification/DaneRecords +
  `GetEffectiveConnectionSecurity`; `TCPConnection::AsyncHandshake` uses DaneVerifier
  callback when DANE records present; `ExternalDelivery` skips
  retry-without-STARTTLS when TLS required.
- **JSON logging**: Logger emits JSONL when INI `JsonLogging=1`.
- **Prometheus**: `Util\MetricsServer.{h,cpp}`, GET /metrics; INI `MetricsServerPort`
  (>0 enables), `MetricsServerBindAddress` (default 127.0.0.1).
- **Ed25519 DKIM** (RFC 8463): sign + verify branches in DKIM.cpp
  (`p=` in DNS is the RAW 32-byte key → `EVP_PKEY_new_raw_public_key`).
- **TOTP 2FA** for the Administrator GUI: `Utilities\TwoFactorAuth.cs`
  (RFC 6238, secret DPAPI LocalMachine → HKLM `SOFTWARE\hMailServer\AdminTotpSecret`).
- NOT implemented (deliberately deferred): OAUTHBEARER/XOAUTH2.

### Enterprise round 2
- **ARC sealing** (RFC 8617): `AntiSpam\DKIM\Arc.{h,cpp}` — called from DKIMSigner
  after DKIM sign when INI `ArcSealingEnabled=1` (default 0). cv= from live verify
  of latest seal; AAR authserv-id = computer name; AMS relaxed/relaxed;
  rsa-sha256 or ed25519-sha256 per key type. `DKIM::SignHash_/IsEd25519PrivateKey_/
  VerifyHeaderHash_` are public for Arc's use.
- **TLS-RPT** (RFC 8460): `Util\TlsRptStore.{h,cpp}` (singleton, per-UTC-day
  per-domain buckets) + `SMTP\TlsRptReporterTask.{h,cpp}` (hourly ScheduledTask;
  PopDay frees memory even when disabled; sends only when INI `TlsRptFromAddress`
  set; rua via TXT `_smtp._tls.<dom>`, mailto: only; JSON + multipart/report built
  manually → Message BO → SubmitPendingEmail).
- **REST admin API**: `Util\RestApiServer.{h,cpp}` — raw sockets + optional TLS
  (min TLS1.2). Refuses to start without cert unless bound to 127.0.0.1; refuses
  empty admin password. Basic auth "administrator" + `Crypt::Validate`.
  INI `RestApiPort` (>0 enables), `RestApiBindAddress`, `RestApiCertificateFile`,
  `RestApiPrivateKeyFile`.
- **ACME v2** (RFC 8555): `Util\AcmeClient.{h,cpp}` — RSA-2048 account JWK/RS256,
  http-01, CSR with SAN for all `AcmeDomains`, writes `fullchain.pem`+`privkey.pem`
  to `AcmeCertificateDirectory` (default `<DataDir>\ACME`); `AcmeRenewalTask`
  hourly, renews when cert missing/<30 days. badNonce retried once.
  INI: `AcmeEnabled=0`, `AcmeDirectoryUrl`, `AcmeContactEmail`, `AcmeDomains`,
  `AcmeCertificateDirectory`, `AcmeHttpPort=80`.

### Seamless UX round
- **ACME auto-apply**: `ApplyCertificate_` creates/updates SSLCertificate DB record
  "ACME (automatic)", auto-assigns to TLS ports with SSLCertificateID==0, then
  `Reinitializator::Instance()->ReInitialize()` → cert live with zero manual steps.
- **ACME startup check**: `CreateScheduledTasks_` schedules an extra RunOnce
  AcmeRenewalTask; Scheduler runs RunOnce tasks immediately via MaintenanceWorkQueue.
- **REST cert fallback**: empty `RestApiCertificateFile` → ACME fullchain/privkey
  used if both exist.
- **Server features dialog**: `Dialogs\formServerFeatures.cs` (code-built, no resx)
  edits hMailServer.INI via GetPrivateProfileString/WritePrivateProfileString;
  INI located via HKLM `SOFTWARE\hMailServer` InstallLocation (Registry64 then
  Registry32). Offers service restart via ServiceController. Menu item inserted in
  formMain ctor AFTER `Strings.Localize`.
- Scheduler facts: tasks run when next_run_time<=now, polled every minute;
  `SetMinutesBetweenRun` sets first run = now+interval; RunOnce → immediate,
  never added to the scheduled list.

### Inbound/client-facing round (#4–#9)
- **#9 DNSSEC for SPF/DKIM/DMARC**: `DnssecResolver` generalised —
  `QueryValidatedRrset_(name,type,rdatas&)` (≤3 validated CNAME hops;
  insecure link caps result at Insecure), `QueryTxt` (returns records on Secure AND
  Insecure; only Bogus withholds). `DNSResolver::GetTXTRecords`: Bogus → return
  false; Secure → validated records; Insecure → fall through to WinAPI resolver.
  SPF (vendored C-style `SPF\RMSPF.cpp`): bridge `HM::DnssecTxtLookupIsBogus(const char*)`
  declared locally; `dnsquery()` returns SPF_TempError on bogus TXT.
- **#7 IMAP SPECIAL-USE** (RFC 6154): capability token + folder attributes for
  selectable TOP-LEVEL folders only (`Find(delim)<0`); name matching
  (CompareNoCase): Sent/Sent Items/Sent Messages→`\Sent`, Drafts→`\Drafts`,
  Trash/Deleted Items|Messages→`\Trash`, Junk/Junk E-mail/Junk Email/Spam→`\Junk`,
  Archive(s)→`\Archive`. No LIST-EXTENDED.
- **#8 Queue REST**: GET `/api/v1/queue` (reuses
  `ServerStatus::GetUnsortedMessageStatus` tab columns), POST
  `/api/v1/queue/<id>/retry` (`DeliveryQueue::ResetDeliveryTime`+`StartDelivery`),
  DELETE `/api/v1/queue/<id>` (`DeliveryQueue::Remove`). ParseQueueId digits-only
  ≤18 chars, `_atoi64`.
- **#5 Inbound DANE helper**: static `AcmeClient::GetCertificateTlsa(certFile, spkiHex&)`
  (PEM_read_X509→i2d_X509_PUBKEY→SHA256→hex); GET `/api/v1/tlsa` lists `3 1 1`
  records for all SSLCertificates (fallback ACME fullchain). **ACME key reuse**:
  `FinalizeOrder_` reloads existing privkey.pem when `AcmeReuseKey=1` (default) so
  the TLSA 3-1-1 record stays stable across renewals; record logged after issuance.
- **#4+#6 WebServicesServer** (`Util\WebServicesServer.{h,cpp}`): dual HTTP/HTTPS
  listeners (RestApiServer socket/TLS pattern). Public, unauthenticated routes:
  - `/.well-known/acme-challenge/<token>` — served from `AcmeChallengeStore`
    (static map in AcmeClient.{h,cpp}); `CompleteAuthorization_` always Sets the
    token and skips the transient AcmeChallengeServer when
    `WebServicesServer::IsListeningOnPort(AcmeHttpPort)`.
  - `/.well-known/mta-sts.txt` — host must be `mta-sts.<domain>`, domain must
    exist+active; `mx:` lines from `MtaStsPolicyMx` CSV, else live MX lookup
    (1h cache), else hostname; mode enforce|testing|none; max_age clamped
    86400..31557600; CRLF line endings.
  - `/mail/config-v1.1.xml` (+ `/.well-known/autoconfig` variant) — Thunderbird XML.
  - `/autodiscover/autodiscover.xml` POST+GET — Outlook POX (parses `<EMailAddress>`).
  - `GetClientAccessSettings_`: clientHost = `AutoconfigClientHost` else hostname;
    best port per protocol ranked SSL(100) > STARTTLS-required(587:90/else 80) >
    STARTTLS-optional(70/60) > plain(10).
  - HTTPS cert: `WebServicesCertificateFile` else ACME fallback; missing → HTTPS
    disabled (logged), HTTP still runs. Bootstrap is self-healing: ACME issues over
    HTTP → ApplyCertificate_ → ReInitialize → HTTPS comes up.
- INI added: `AcmeReuseKey=1`, `WebServicesHttpPort=0`, `WebServicesHttpsPort=0`,
  `WebServicesBindAddress=0.0.0.0`, `WebServicesCertificateFile`/`KeyFile=""`,
  `MtaStsHostingEnabled=1`, `MtaStsPolicyMode=enforce`, `MtaStsPolicyMaxAge=604800`,
  `MtaStsPolicyMx=""`, `AutoconfigEnabled=1`, `AutoconfigClientHost=""`.

### DNSSEC-for-DANE round
- **`TCPIP\DnssecResolver.{h,cpp}`** (~1250 lines): in-process validating stub
  resolver (RFC 4033–4035). `ChainStatus{Secure,Insecure,Bogus}`. Follows ≤3
  validated CNAME hops, validates RRset sig, walks DS/DNSKEY chain to root trust
  anchors. Algorithms: RSA/SHA-256(8), RSA/SHA-512(10), ECDSA P-256(13)/P-384(14)
  (raw r||s → DER via `ECDSA_SIG_set0`+`i2d`), Ed25519(15). DS digests SHA-256(2)/
  SHA-384(4); SHA-1 rejected. KeyTag per RFC 4034 App B; wildcard expansion via
  labels field; RFC 1982 serial compare for validity. Transport: UDP EDNS0 1232 +
  DO bit, TC→TCP, CD flag set. Zone-key cache: static map, mutex, 512 entries,
  TTL 3600/300/60 (secure/insecure/bogus). **Failure policy: transport failure →
  Insecure (mail flows); crypto failure → Bogus.** Builtin root anchors
  KSK-2017(20326) + KSK-2024(38696); INI `DnssecTrustAnchors` override
  (`"tag alg digesttype hex;..."`).
- **TlsPolicy**: `GetTlsaRecords(host,port,TlsaLookupStatus&)` — enum
  `{DnssecValidated,Unvalidated,NoRecords,Bogus}`. `DnssecValidationEnabled=1`
  (default) → DnssecResolver (Secure→DnssecValidated, Insecure→NoRecords,
  Bogus→Bogus); =0 → legacy opportunistic path (`LookupTlsaOpportunistic_` +
  `FilterUsableTlsaRecords_`).
- **ExternalDelivery**: Bogus → log + `TlsRptStore::RecordFailure(...,"dnssec-invalid",mx)`
  + skip host (RFC 7672: bogus host MUST NOT be used); all MX bogus → defer
  (never deliver insecurely).
- INI: `DnssecValidationEnabled=1`, `DnssecTrustAnchors=""`.

## Build status / validation

- Both solutions build clean: Release|x64 server + Release tools, MSBuild EXIT 0,
  `/WX`, zero warnings.
- dumpbin-confirmed linkage: libcrypto-4-x64.dll, libssl-4-x64.dll, libpq.dll.
- **Regression suite GREEN (2026-06-12): 898 tests — 865 passed, 0 failed,
  33 inconclusive (environment-conditional), nunit exit 0.** First full
  validation of the 6.0 modernization; two real fixes came out of it
  (FormatV — see conventions above; test-infra TLS defaults — see below).

### Test-validation round (first machine able to run the suite)

Code fixes:
- `Common\Util\StdString.h`: FormatV narrow/wide counting bug (service-crashing,
  see CStdStr section).
- Test infra modernized for the TLS 1.2/1.3 server: `Shared\TcpConnection.cs` and
  `Shared\SMTPClientSimulator.cs` defaulted to `SslProtocols.Default` =
  **SSL3|TLS1.0 only** → every SslStream handshake failed with "A call to SSPI
  failed". New shared default `TcpConnection.ModernProtocols`
  (`Tls12 | (SslProtocols)12288 /*Tls13*/`); `WhenSSL3IsDisabledTLSShouldWork`
  switched Tls→Tls12 (Win11 disables TLS 1.0 client-side; its
  `version=TLSv1` assert still matches `TLSv1.2`).
- `RegressionTests.csproj` + `hMailServer.Test.Infrastructure.csproj` retargeted
  v4.8 → v4.8.1 (had been missed in the tools retarget round).
- `build\run-tests.ps1` updated to the restored NUnit.ConsoleRunner 3.16.3 path
  (`hmailserver\test\packages`). Running `nunit3-console` directly is more
  reliable than the script's output pump; `--labels=After` (3.16 dropped the
  old values).
- Test certificates regenerated (originals kept as `*.orig`): `SSL examples\
  example.crt/key` and `WithPassword\server.crt/key` were 1024-bit RSA from
  2007 — OpenSSL 4 refuses with "ee key too small"; now RSA-2048/10yr (same
  subjects, WithPassword key passphrase unchanged semantics: encrypted).
  `localhost.pfx` regenerated per its readme (CN=localhost, password Secret1).

Test-environment recipe (dev tree, no installer):
1. Provision next to the built exe (`x64\Release\`): `hMailServer.ini`
   ([Directories] + blank `AdministratorPassword`), folders `Logs/Temp/Events`,
   `DBScripts\` copy (**CRLF endings — SQLScriptParser splits on \r\n\r\n**),
   `Languages\` (missing dir = boost directory_iterator throw = startup crash),
   `dh2048.pem` + `tlds.txt` from `installation\Extras`, and `Bin\` containing
   `7za.exe` (backup tests) plus a copy of `hMailServer.ini`
   (XOriginalRcptHeaderTests read the installed-layout path).
2. **Short DataFolder** (e.g. `C:\HM\Data`) — long-account-name tests exceed
   MAX_PATH under a deeply nested repo path (error 206 cascades into hundreds
   of clean-log assertion failures).
3. SQL CE: repo MSIs in `installation\SQLCE` are **4.0** packages (matching
   `Microsoft.SQLSERVER.CE.OLEDB.4.0` in SQLCEConnection); only the x64 MSI is
   needed/installable on x64. Then COM: `Database.CreateInternalDatabase()` +
   `Reinitialize()`.
4. Remove any orphaned `HKLM\SOFTWARE\WOW6432Node\hMailServer` key from old
   installs — `Registry::GetStringValue` opens with KEY_WOW64_32KEY, so a stale
   32-bit `InstallLocation` silently redirects the INI lookup.
5. Strip Mark-of-the-Web on the whole tree and convert LF-normalized text files
   back to CRLF — LF endings break DB scripts, DKIM-signed `.eml` test
   resources, and raw-message tests (bare-LF 554 rejections).
6. Disable AV mail/SSL proxying for localhost (ESET rewrote the IMAP literal
   continuation to `+ just send it` and broke raw SMTP framing).
7. Before each run: clear the log folder (tests assert a clean error log; one
   early error cascades) and reset leftover `WelcomeIMAP/SMTP/POP3` if a
   previous run crashed mid-test.
8. Tests authenticate COM as Administrator with password `testar` → blank
   fallback; they bind live ports and wipe data — never run against a
   production install.

Optional integrations (turn inconclusive tests into real runs):
- `AddXOriginalRcptTo=1` under `[Settings]` in BOTH INI copies (exe dir and
  `Bin\`) enables the 6 XOriginalRcptHeaderTests.
- ClamAV (5 tests): install ClamAV (winget `Cisco.ClamAV`), copy the install
  to `C:\clamav` (CustomAsserts hardcodes `C:\clamav\clamd.exe`), minimal
  `clamd.conf` (`TCPSocket 3310`, `TCPAddr 127.0.0.1`, `DatabaseDirectory
  C:\clamav\database`), run freshclam, start clamd. **Warm-up gotcha:** clamd
  accepts TCP before signatures finish loading and hMailServer fails open —
  send one EICAR message and confirm "Virus detected" in the log before
  trusting AntiVirus fixture results.
- SpamAssassin (22 tests): **works on Windows** via the JAM Software x64 build —
  direct download `https://downloads.jam-software.de/spamassassin/
  SpamAssassinForWindows-x64.zip` (v4.0.1, rules bundled in `share\`). Extract
  to `C:\SpamAssassin`, run `spamd.exe -i 127.0.0.1 -A 127.0.0.1 -p 783`;
  a process literally named `spamd` satisfies the test gate. 21/22 then run
  for real. `TestSANotRunning` stays inconclusive (it stops/starts the
  `SpamAssassinJAM` service, i.e. JAM's "SpamAssassin in a Box" variant).
  The two `AntiSpam.Combinations` SURBL/MX tests additionally require a DNS
  resolver that answers `surbl-org-permanent-test-point.com` queries — many
  public resolvers filter these.
