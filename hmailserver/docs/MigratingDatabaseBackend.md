Migrating to a different database backend
=========================================

How to move an existing installation from one of the four supported databases to
another — in practice, off **SQL Server Compact**, which the installer still
chooses by default and which Microsoft stopped maintaining a long time ago.

There is no migration tool, and that is not an omission. Backup and restore are
already backend-agnostic, so the migration is three operations you already have:
back up, repoint, restore. What was missing was this document.

Read [Upgrading.md](Upgrading.md) instead if you are moving to a newer *release*
of the server. That is a different operation and it keeps your backend.

The short version
-----------------

1. Stop the service.
2. Take a backup with **domains, settings and messages** selected.
3. Create an empty database on the new backend and point `hMailServer.ini` at it.
4. Start the service and restore the backup.

Your mail files never move. Nothing is exported to an intermediate format you
have to trust.

Why this works
--------------

Worth understanding before you run it, because it tells you what can and cannot
go wrong.

**Nothing in the archive is tied to a database.** Neither `BackupExecuter` nor
`BackupRestorer` looks at the database type — there is no branch on it anywhere in
either. The archive is XML plus, optionally, the message files.

**The XML carries no identity values.** `Account::XMLStore` writes `Name`,
`Password`, `MaxAccountSize` and the rest, and no account id; `XMLLoad` sets none.
On restore, `SaveObject` sees an object whose id is `0`, inserts it, and lets the
new database assign its own. Relationships are rebuilt through the object graph —
accounts under their domain, folders under their account, messages under their
folder — not by raw id. So the fact that MSSQL and PostgreSQL will hand out
completely different numbers than SQL CE did is not something the restore has to
cope with. It never sees the old ones.

**The message store on disk is addressed by name, not by id.** A message file
lives at `<DataFolder>\<domain>\<account>\<first two characters of the guid>\<guid>.eml`,
and a public-folder message at `<DataFolder>\.Public_Folder\<xx>\<guid>.eml`.
Those paths are built from the domain and account *names* and the file's own guid.
Change every id in the database and every file is still exactly where the server
will look for it.

That is the whole of it. The migration is possible because nothing
identity-shaped crosses the archive boundary, which is also why it can cross a
backend boundary.

Choosing a target
-----------------

| Target | Choose it when |
|---|---|
| **MS SQL Server** (including Express and LocalDB) | You already run SQL Server, or you want the strongest transaction support of the four. Note that the session runs at READ UNCOMMITTED. |
| **PostgreSQL** | You want the best-behaved backend in this codebase. It is the only one of the four where DDL is transactional, so a failed schema upgrade rolls back cleanly. |
| **MySQL / MariaDB** | You already run it. Make sure every table ends up InnoDB — the server only issues transactions if they all report it at connect time, and silently does not if any table does not. |
| **SQL Server Compact** | Only if you are migrating *to* a test rig. It is the default and it is the one real dependency liability in this tree; see the SQL Server Compact row in [Roadmap.md](../../Roadmap.md) for the three defects that are specific to it. |

The schema is created for you either way — you do not need to run the SQL scripts
by hand.

Before you start
----------------

**Take a filesystem-level backup of the whole installation as well.** Not because
the restore is unreliable, but because it is the only thing that will get you back
if you discover a problem after the old database has been retired. See
[Rolling back](#rolling-back).

**Check that your messages are all inside the data folder.** Backup refuses to run
if any message row points at a file outside `DataFolder` — it fails with "All
messages are not located in the data folder". This only happens on installations
where messages were imported with absolute paths. If it fires, fix those rows
before going any further; the migration is not the place to discover it.

**Note your current settings.** Open `hMailServer.ini` (normally
`C:\Program Files\hMailServer\Bin\hMailServer.INI`) and keep a copy. The
`[Database]` section is the part you are about to change:

```ini
[Database]
Type=MSSQLCE
Server=
Database=hMailServer
Username=
Password=
Port=0
Internal=1
```

The administrator password lives in this file too, not in the database, so it
carries over untouched.

The procedure
-------------

### 1. Stop the service

```powershell
Stop-Service hMailServer
```

Wait for it to actually stop before continuing. A backup taken while mail is being
delivered is a backup of a moving target.

Restart it for the backup itself — the backup runs through the running server —
but stop accepting new mail first if you can, either by stopping the SMTP TCP/IP
port in the administration tool or by holding the traffic upstream. Anything that
arrives after the backup and before the cutover is delivered into the *old*
database and will not be in the new one.

### 2. Take the backup

In the administration tool, under **Settings → Advanced → Backup**, select all
three of **domains**, **settings** and **messages**, set a destination with room
for it, and run it. Or from a script:

```powershell
$app = New-Object -ComObject hMailServer.Application
$app.Authenticate("Administrator", "<password>")
$b = $app.Settings.Backup
$b.BackupDomains  = $true
$b.BackupSettings = $true
$b.BackupMessages = $true
$b.Destination    = "D:\hmail-migration"
$app.BackupManager.StartBackup()
```

The archive lands in the destination folder as `HMBackup <local time>.7z`. That
one file is the whole backup — the `hMailServerBackup.xml` index you may see
appear next to it during the run is written into the archive and then deleted.

All three of domains, settings and messages are required. The restore refuses
combinations that would delete something and put nothing back — restoring domains from a backup that has none
would remove every domain, account and alias on the server, so it will not do it
and will tell you why.

**If your mail store is large, use the database-only mode.** Set

```ini
[Settings]
BackupMessagesDBOnly=1
```

in `hMailServer.ini` before both the backup and the restore. This stores and
restores the message *rows* — which is what you are actually migrating — while
leaving the `.eml` files alone on disk. On an installation with a few hundred
gigabytes of mail this is the difference between minutes and most of a day, and
since the data folder is not moving, copying it out and back achieves nothing.

The setting has to be identical for the backup and the restore. Remove it
afterwards so your ordinary scheduled backups go back to including the files.

### 3. Create the new database

Create an **empty** database on the target server, and a login with rights to
create tables in it. Do not create any tables — the next step does that.

Then edit `hMailServer.ini` to point at it:

```ini
[Database]
Type=MSSQL
Server=sql01.example.com
Database=hMailServer
Username=hmailserver
Password=<password>
Port=0
Internal=0
```

`Type` is one of `MSSQL`, `MYSQL`, `PostgreSQL` or `MSSQLCE`, matched
case-insensitively. Set `Internal=0` for anything other than the embedded engine.
Leave `Port=0` to use the backend's default.

Now create the schema by running the database setup tool from the installation's
`Bin` folder:

```powershell
& "C:\Program Files\hMailServer\Bin\DBSetupQuick.exe"
if ($LASTEXITCODE -ne 0) { throw "Database setup failed with exit code $LASTEXITCODE" }
```

It reads the connection details you just wrote, sees no database, and creates one
at the current schema version. It returns a non-zero exit code if it fails —
that is what the installer checks — so it is safe to run from a script.

`DBSetup.exe`, in the same folder, is the interactive wizard and does the same
job with prompts. Use it if you would rather enter the connection details in a
dialog than edit the ini file by hand; it writes the same `[Database]` section.

### 4. Restore

Start the service and restore the archive:

```powershell
Start-Service hMailServer

$app = New-Object -ComObject hMailServer.Application
$app.Authenticate("Administrator", "<password>")
$backup = $app.BackupManager.LoadBackup("D:\hmail-migration\HMBackup 2026-08-13 220511.7z")
$backup.RestoreDomains  = $true
$backup.RestoreMessages = $true
$backup.RestoreSettings = $true
$backup.StartRestore()
```

The server restarts itself at the end of a restore. That is expected.

Verifying it worked
-------------------

Do all of these. The failure mode worth catching is a *partial* restore, which
looks like a working server to anyone who only checks that it starts.

- **Counts match.** Domains, accounts, aliases and distribution lists, compared
  against what you wrote down before you started.
- **Mail is visible.** Log in over IMAP as a real account and check that the
  folder list and the message counts are what they were. This is the one that
  proves the message rows and the files found each other again.
- **A message opens.** Fetch a whole message body, not just a header — that
  proves the row's file path resolves on disk under the new ids.
- **Settings survived.** SSL certificates, TCP/IP ports, IP ranges, routes,
  global rules and the anti-spam lists. These are the part of the backup that is
  easiest not to notice is missing.
- **Send and receive.** One message in, one message out.
- **The error log is empty.** `hmailserver_ERROR_<date>.log` in the log folder.
  Anything in it that appeared during the restore is worth reading before you
  retire the old database.

Rolling back
------------

Until you delete the old database, rollback is exact and takes a minute: stop the
service, put the original `[Database]` section back in `hMailServer.ini`, start
the service. The old database is untouched by everything above — the migration
only ever read from it.

**Keep it until you are satisfied**, and remember that any mail that arrived after
the cutover exists only in the new database. That is the one thing rolling back
loses, and it is why the cutover is worth doing during a quiet window with
inbound traffic held.

There is no downgrade path for the *schema*, but that does not apply here: you are
not changing schema version, only which server holds it.

Known sharp edges
-----------------

**Any mail delivered between the backup and the cutover is lost to the new
database.** Nothing in the process merges the two. Hold inbound traffic, or accept
the gap knowingly.

**Ids change, and anything of yours that stored one will break.** Nothing inside
the server depends on id continuity, which is the entire basis of this
document — but if you have external scripts, reporting or an integration that
recorded `hm_accounts.accountid` or `hm_domains.domainid`, those values will not
mean the same thing afterwards. Key on the address or the domain name instead.

**The SQL log device's table is not part of the backup.** If you use
`SQLLogging`, historical log rows stay in the old database. Export them separately
if you need them; they are ordinary rows in `hm_log`.

**MySQL and SQL Server Compact commit DDL as it executes.** If the schema
creation in step 3 fails halfway on those two, the database is left partly built
rather than rolled back. Drop it and start step 3 again rather than re-running the
tool over the remains.

What this does not cover
------------------------

Moving the *data folder* to different storage, which is a separate operation and
is not coupled to the backend at all — the path is `[Directories] DataFolder`, and
the only requirement is that the service account can reach it.
