# Relocating an hMailServer installation

Moving a configured installation from one directory to another - `C:\Program
Files (x86)\hMailServer` to `C:\Program Files\hMailServer`, or onto another
drive - is not one change. The installation path is recorded in the registry, in
the service, in the COM registration, in `hMailServer.ini`, and in a handful of
database columns that hold absolute file names an administrator typed. This is
the ordered list of every one of them, what repairs each, and the two places
where the honest answer is "there is no clean fix".

It is written for 6.3.0. Every claim below was read out of the code; the file
and line references are there so you can check any of them.

**Installing somewhere new is a different, solved problem.** `/DIR="<path>"` on
the installer puts a *fresh* installation wherever you want. This document is
about an installation that already has mail in it.

**The good news, first.** The mail store itself is already relocatable. Message
rows hold a bare `{guid}.eml` with no directory (`Message::GenerateFileName`,
`Common/BO/Message.cpp:261`), and the path is rebuilt at every read against the
data directory (`PersistentMessage::GetFileName`,
`Common/Persistence/PersistentMessage.cpp:1502`). Moving the data directory and
pointing `DataFolder` at its new home moves every message with it. The exception
is a database that predates hMailServer 5.4, where the column can hold a full
path; step 0 tells you whether yours does.

---

## 0. Before you touch anything

1. **Take a backup and verify it restores.** Everything below is reversible only
   while the old tree and a good backup both exist.

2. **Ask the server whether its message rows are relative.** In the Control
   Panel, *Monitoring & troubleshooting → Diagnostics → Test message file locations*
   (`Common/Diagnostics/TestDataDirectory.cpp:31`). If it reports
   `ERROR: Full paths are stored in the database`, the move is still possible but
   step 8 becomes mandatory rather than optional.

3. **Write down what you are about to strand.** The inventory is in step 1. Doing
   it afterwards means finding out from a failure.

---

## 1. The inventory

### In `Bin\hMailServer.ini`

| Section | Key | What breaks if it is left pointing at the old tree |
|---|---|---|
| `[Directories]` | `ProgramFolder` | Language files, the database scripts and 7-Zip for backups are looked for under it (`IniFileSettings.cpp:154`, `:1072`, `Compression.cpp:84`) |
| `[Directories]` | `DataFolder` | Every message path is rebuilt from it. The store appears empty |
| `[Directories]` | `DatabaseFolder` | The SQL Server Compact database file, if you use that backend |
| `[Directories]` | `LogFolder` | Logs are written to the old tree, silently |
| `[Directories]` | `TempFolder` | Message assembly and backup staging |
| `[Directories]` | `EventFolder` | Event scripts are not found; events stop running |
| `[Settings]` | `ArchiveDir` | Archiving stops; existing archived copies are not found |
| `[Settings]` | `AcmeCertificateDirectory` | ACME state and issued certificates |
| `[Settings]` | `OAuth2PublicKeyFile`, `UpdateTrustRootsFile`, `UpdateLogPublicKeyFile` | Token validation and update verification refuse |
| `[Settings]` | `RestApiCertificateFile` / `PrivateKeyFile`, `MetricsServerCertificateFile` / `PrivateKeyFile`, `WebServicesCertificateFile` / `PrivateKeyFile` | The listener starts without TLS, or does not start |
| `[Database]` | `PostgreSQLSslRootCert` | PostgreSQL TLS verification fails |

Leave the `[Database]` connection keys alone. They name a server, not a path.

### In the registry

| Value | Where | Repaired by |
|---|---|---|
| `InstallLocation` | `HKLM\SOFTWARE\hMailServer` **in the 32-bit view** | Step 5, by hand |
| Service `ImagePath` | `HKLM\SYSTEM\CurrentControlSet\Services\hMailServer` | Step 6, `/Register` |
| `LocalServer32` (79 keys) and `InprocServer32` (15) | `HKCR\CLSID\...` | Step 6, `/Register` |
| TypeLib `win64` and `HELPDIR` | `HKCR\TypeLib\...` | Step 6, `/Register` |
| `InstallLocation`, `UninstallString`, `DisplayIcon` | `HKLM\...\Uninstall\hMailServer_is1` | Nothing. See step 11 |

The 32-bit view is not a mistake and not optional. The server reads that value
through `KEY_WOW64_32KEY` (`Common/Util/Registry.cpp:40`), and the installer
writes it there for the same reason
(`hmailserver/installation/section_registry.iss`). **Writing the 64-bit view
changes nothing and looks like it worked.**

### In the database

Nine places hold a path an administrator typed. None of them is rewritten by
anything, and there is no function that enumerates them.

| Table and column | What it is |
|---|---|
| `hm_sslcertificates.sslcertificatefile`, `.sslprivatekeyfile` | Certificate and key files |
| `hm_domains.domaindkimprivatekeyfile`, `.domaindkimsecondaryprivatekeyfile` | DKIM signing keys |
| `hm_tcpipports.portclientcertificatecafile` | The CA bundle for inbound client certificates |
| `hm_settings` row `backupdestination` | Where backups are written |
| `hm_settings` rows `avclamwinexec`, `avclamwindb` | ClamWin's scanner and database |
| `hm_settings` row `customvirusscannerexecutable` | An external scanner command line |
| `hm_inisettings` | The whole `[Settings]` section, mirrored since schema 6011 |
| `hm_archiveindex.archivepath` | One row per archived copy |
| `hm_quarantine.quarantinefilename` | Held messages |
| `hm_rule_actions.actionfilename` | A rule that runs a program or writes a file |
| `hm_messages.messagefilename` | Relative since 5.4; see step 8 |

---

## 2. Stop the service

Nothing in this runbook may run against a live server. Stop it, and confirm it is
stopped, before anything is copied.

---

## 3. Copy the tree

Copy, do not move. The old tree is the rollback.

Include `Bin\hMailServerApiKeys.ini` if it exists: the REST API key store lives
there (`Common/Util/RestApiServer.cpp:2414`), and no installer manifest mentions
it.

Grant the service account access to the new tree. The installer grants it to
`NT SERVICE\hMailServer` on the data, log and temp directories; a copy made by an
administrator will usually inherit different rights, and a server that cannot
write its log directory starts and then says nothing.

---

## 4. Rewrite `hMailServer.ini`

Edit `<new root>\Bin\hMailServer.ini` and change every key from step 1 whose
value was under the old root. Nothing else.

The file is read from the directory the running `hMailServer.exe` sits in when
one is there, and otherwise from the registry (`Common/Util/Utilities.cpp:102`),
so the copy you edit is the one the moved server will read.

---

## 5. Repoint the registry root

From an elevated prompt:

```
reg add "HKLM\SOFTWARE\hMailServer" /v InstallLocation /t REG_SZ /d "<new root>" /reg:32 /f
```

`/reg:32` is the whole of the point. See step 1.

---

## 6. Re-register the server

From an elevated prompt, from the **new** `Bin`:

```
hMailServer.exe /Register
```

One command repairs the service's `ImagePath`
(`ServiceManager.cpp:45` → `ChangeServiceConfig`), all 79 `LocalServer32`
registrations, the 15 `InprocServer32` ones, and the type library's `win64` and
`HELPDIR` values. It must run elevated, and it must run from the new tree - it
registers the path of the executable that runs it.

---

## 7. Start it, and check what it read

Start the service and read the application log.

**A server whose anchors are stale reports `Running` and serves an empty
configuration.** That failure is silent, and it is the one this whole document
exists to prevent: `build/preflight-tests.ps1` carries a check for it precisely
because it has happened. Confirm over COM or in the Control Panel that
*Settings → Directories* shows the new paths, that your domains are listed, and
that the log file you are reading is under the new log directory.

---

## 8. Convert full message paths, if step 0 found any

Only if *Test message file locations* reported full paths.

Run the **Data Directory Synchronizer** against the new data directory. It walks
the data directory and converts each absolute row to a partial name
(`Tools/DataDirectorySynchronizer`, `MailImporter::ReplaceMessagePath_`). It is
the supported converter; a `UPDATE hm_messages SET ...` is not, because the rows
are not uniform and the file layout is derived, not stored.

---

## 9. Fix the database-borne paths

For each row from step 1's database table that pointed under the old root:

- **SSL certificates.** The Control Panel's certificate grid is read-only except
  for the passphrase, so each certificate must be deleted and re-added with its
  new file names, or set over COM.
- **DKIM keys.** *Domain → DKIM*, both the primary and, if used, the secondary.
- **Client-certificate CA bundle.** *Advanced → TCP/IP ports*, per port. A port
  set to *require* a client certificate refuses every client when its bundle
  cannot be loaded.
- **Backup destination, ClamWin, custom scanner, rule actions.** Each in its own
  page.
- **Archive index.** `hm_archiveindex.archivepath` holds one row per archived
  copy and is matched on the exact string by retention, legal hold and the
  address eraser. Do not bulk-rewrite it. If the archive directory moved, move
  the archive back or accept that pre-move rows point at the old location; there
  is no supported converter for this column.

---

## 10. Verify before deleting anything

- Send and receive a message.
- Complete a TLS handshake on every port that has a certificate.
- Connect on any port whose client-certificate policy is *require*.
- Run a backup to completion. It refuses outright when its destination is
  unwritable, which is the check you want.
- Run an event script, if you use them.
- Check the log directory is being written.

---

## 11. What stays broken, and why

- **Add/Remove Programs** still points at the old `unins000.exe`. Do not edit
  `unins000.dat`; it is a compiled record, and a hand edit produces an
  uninstaller that removes the wrong tree. The supported repair is to re-run the
  installer at the new root, which rewrites the uninstall metadata.
- **Start-menu shortcuts** hold absolute targets and nothing repairs them.
  Re-create them, or re-run the installer.
- **Message content.** The old path appears inside messages the server itself
  wrote - a delivery report, for example, names the file it could not find.
  Those are historical records of what happened. **Never rewrite message
  content**, and be careful with any "search and replace across the
  installation" approach for exactly this reason.

---

## 12. Keep the old tree

Keep it, unreachable by the service, for at least one full backup cycle. Once it
is deleted, the only rollback is the step 0 backup.

---

## Seeing where the paths are, without doing any of this

The Control Panel's *Monitoring & troubleshooting → Diagnostics* page has an **Installation paths**
section that lists the registry root, every `[Directories]` key, the derived
directories, and every path-shaped setting the running server holds, with the
ones that do not exist marked. It changes nothing; it is there so that the
inventory in step 1 can be read off a running server rather than assembled by
hand.

## Why the paths are not one setting

This came up as [issue #158](https://github.com/Progressiverobot/hmailserver/issues/158).
The short answer is that `ProgramFolder` is not the root of anything: it is one
of six independent absolute strings, and the registry holds a seventh copy that
the server actually uses to find its own INI file. From 6.3.0 a *relative*
value in the five non-program directories is resolved against the program folder,
so a new installation can be written to be movable; an existing installation's
absolute values are left exactly as they are, because rewriting a configured
server's paths during an upgrade is not something an upgrade may do.
