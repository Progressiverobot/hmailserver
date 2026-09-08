Upgrading
=========

How to move an existing hMailServer installation to this fork, or from one release
of it to the next, and what the database upgrade actually does. Written because the
upgrade is the one operation where an unpleasant surprise costs you mail rather than
time.

The short version
-----------------

Run the new installer over the existing installation. It preserves your
configuration and your mail, stops the service, upgrades the database schema,
and starts the service again. There is no separate migration step and no export
and re-import.

**Take a backup first anyway.** Not because the upgrade is unreliable — the chain
below has run continuously since hMailServer 4 — but because the one thing an
upgrade cannot do is undo itself. See [Rolling back](#rolling-back).

Where you can upgrade from
--------------------------

The upgrade chain is continuous, from schema version `0` through to whatever
version this build requires, applied one step at a time. A database at any
intermediate version is brought forward step by step, so there is no "you must
first upgrade to 5.x" hop to plan around.

**The step count and the target version are deliberately not written here.**
They have gone stale on this page four times now, and a version number is
exactly the sort of fact a reader trusts without checking. Read them from the
tree instead: `REQUIRED_DB_VERSION` in
`hmailserver/source/Server/Common/Application/Constants.h` is the version this
build demands, and `build\check-schema-versions.ps1` prints both numbers and
proves the chain reaches one in exactly the other.

That includes databases created by the *original* upstream project. This fork did
not branch the schema; it extended it. The current version is a superset,
reached by the same mechanism upstream used.

**How far back the chain reaches depends on your backend, and this is the one
qualification worth knowing before you start.** The steps are registered once, for
every backend, but the SQL scripts they run are per-dialect and they do not all
exist:

| Backend | Steps that ship | Reaches back to |
|---|---|---|
| MySQL / MariaDB | every step | schema `0` (hMailServer 1.0) |
| Microsoft SQL Server | every step | schema `0` (hMailServer 1.0) |
| PostgreSQL | every step from `5001` onwards | schema `5001` |
| SQL Server Compact (internal) | every step from `5001` onwards | schema `5001` |

In practice that is not a gap, because neither PostgreSQL nor the internal SQL CE
database was a supported backend before hMailServer 5, so no database older than
`5001` exists on either. But it is why the answer is "any version of *your*
backend" rather than "any version". If a step's script is missing, DBUpdater says
so by name and stops before touching anything — it checks that every file on the
path exists before it runs the first one.

What the upgrade touches
------------------------

* **The schema only.** Messages live on disk, not in the database — the database
  holds accounts, domains, settings and message *metadata*. No upgrade step rewrites
  message files.
* **Every supported backend.** Separate script sets exist for MySQL/MariaDB, MS SQL
  Server, PostgreSQL and the embedded SQL CE. The installer picks the set matching
  your configured backend.
* **Nothing is deleted that carries live data.** One consequence is worth knowing
  because it looks alarming and is not: a schema comparison between an upgraded
  database and a freshly created one can show an extra table. The concrete case is
  `hm_adsynchronization`, a long-dead table that step 5004 → 5005 drops on MS SQL
  Server, MySQL and SQL CE but not on PostgreSQL, and which no `CreateTables` script
  creates on any backend. So an upgraded PostgreSQL database keeps it and a fresh one
  never had it. It is unused either way, and its presence is not a sign of a partial
  upgrade.

Doing it
--------

1. **Back up.** Both halves, and they are separate things:
   * the **database** — with your backend's own tools (`mysqldump`, `pg_dump`,
     SQL Server backup). The built-in backup covers this too, but a native dump is
     what you want if you need to restore into a different server.
   * the **data directory** — the message files. This is the part that cannot be
     reconstructed from anything else.
2. **Note your current versions.** The server version is in the Control Panel. The
   schema version is in a table of its own — `select value from hm_dbversion`, a
   single-column, single-row table, *not* a row in the settings table. (The Control
   Panel shows it on the **Server status** page and DBUpdater reads it too; the SQL
   is here for when the server will not start and neither of them will connect.)
   Write both down — if you need support, they are the first two questions.
3. **Run the new installer.** It stops the service, installs, upgrades the schema,
   and restarts.
4. **Check it came up.** The service should be running and listening on your
   configured ports. Then check the ERROR log for the window around the upgrade —
   an empty ERROR log is the signal you want.
5. **Send a message to yourself**, end to end, and read it back over IMAP or POP3.
   A service that starts is not the same as a server that delivers.

If the upgrade fails
--------------------

The installer checks both that the database tool launched *and* its exit code, so a
failed or cancelled schema upgrade is reported instead of passing silently: on a
non-zero exit code the installer shows an error dialog naming the exit code and
telling you to re-run `DBSetupQuick.exe`, and then **fails the install** — it
raises, so setup ends with a non-zero exit code rather than finishing as though
nothing had happened (it used to carry on and exit 0, which a scripted deployment
could not tell from success). If you script installs, treat a non-zero exit code,
or a `hm_dbversion` that is still on the old value afterwards, as the failure; do
not treat "the installer finished" as success.

If it does fail, the service may be installed but the schema only partly upgraded.
The server does not paper over that: on startup it compares the database's version
against the version it requires and **refuses to run** if they differ, in either
direction, with one of two messages worth knowing because they are what you will
search for:

* `The database is too old for this version of hMailServer. Please run hMailServer
  Database updater (DBUpdater.exe) to upgrade it.`
* `The database is too new for this version of hMailServer. Please upgrade
  hMailServer.`

For the first, run **`DBUpdater.exe`** from the installation's `Bin` folder — that is
the standalone updater, and it is what the error message names. (The installer drives
`DBSetupQuick.exe` for the same job during an install; either will bring the schema
forward.) It picks up from whichever version the database actually reports, so a
retry after a partial upgrade resumes rather than restarting. If it fails again, the
log names the step it stopped on, and that step number is the useful thing to put in
an issue.

The second message means the database has already been upgraded past this binary —
you are running an older server against a newer schema. Install the matching or newer
server rather than trying to downgrade the database.

Rolling back
------------

There is no downgrade path, and this is the one genuine sharp edge in the process.
The schema upgrade is one-way: an older server will refuse to run against a newer
`dbversion` rather than misinterpret it, which is the correct behaviour but means
"just reinstall the old version" does not work on its own.

To roll back you need the pre-upgrade database backup. Restore it, then reinstall the
older server. The data directory does not normally need restoring — message files are
not rewritten by an upgrade — but if you restore an older database against a newer
data directory, any mail that arrived after the backup will exist on disk with no
metadata row, and will be invisible.

Which is the real argument for the backup: not fear of the upgrade, but the fact that
rollback is only as good as the snapshot you took first.

Special cases
-------------

**Moving to a different database backend** is not an upgrade and the chain does not
do it. There is no supported in-place conversion between backends. The route is a new
installation against the new backend and a migration of accounts and mail — and there
is no first-class tool for that yet, which is honestly recorded as a gap in
[Roadmap.md](../../Roadmap.md).

**Moving to a new machine**: install the same version on the new machine, restore the
database and the data directory, and confirm the data-directory path in the settings
matches where you actually put it. Upgrade afterwards, as a separate step, so that if
something goes wrong you know which of the two changes caused it.

**Upgrading silently** — see the [unattended install](../../README.md#unattended-install)
notes. Until 6.2.23 a silent upgrade of an installation with an administrator password
set hung: `DBSetupQuick.exe` forwarded only `/SilentIfOk` and `/silent` to
`DBUpdater.exe`, not the password, and DBUpdater then opened a modal password dialog
nobody was there to answer. That is fixed. DBSetupQuick forwards the password on the
upgrade path as well as the create path, the installer accepts it as
`/adminpassword=<password>` and passes it through, and under `/silent` the shared
authenticator fails with an exit code instead of prompting. So:

* administrator password empty → a silent upgrade works;
* administrator password set → pass `/adminpassword=<password>`; without it the database
  upgrade fails and the installer reports the non-zero exit code rather than waiting.

*(In the tree after 6.2.27, not yet in a published release: the live update's helper
passes a single-use `/upgradetoken=<hex>` in place of the password, so an unattended
update never handles the administrator password at all.)*

**The .NET 10 runtime.** From the first release after 6.2.18, the Control Panel and
the setup tools require the .NET 10 Desktop Runtime, which the installer bundles and
installs when missing; 6.2.18 and earlier used .NET 8. Nothing about the server
itself changed — it is native code with no .NET dependency, so an upgrade that
fails to install the runtime still leaves you with a working mail server, just
without the Control Panel. The runtime is bundled as
`windowsdesktop-runtime-10.0-win-x64.exe` and installed `/quiet /norestart`, only
when it is missing, and the setup tools (`DBSetup`, `DBSetupQuick`, `DBUpdater`)
need it as well as the Control Panel does.

Verified against the code
-------------------------

Re-checked 21 August 2026, and this time the argument was acted on rather than
restated. The two numbers had gone stale a FOURTH time - the page said 73 steps
to 6022 while the tree had moved on - after a previous revision had already
written down that deriving them beats transcribing them, and then transcribed
them anyway. They are gone from the prose now; the sources below are the answer.
Against: `formMain.LoadScripts` in `DBUpdater` (the registered steps and the
version at each end), `Constants.h`'s `REQUIRED_DB_VERSION`,
`hmailserver/source/DBScripts` (which dialects ship which steps, and the
`hm_adsynchronization` asymmetry), `DatabaseConnectionManager::GetCurrentDatabaseVersion`
(`select … from hm_dbversion`), `Application::OnDatabaseConnected` (the two refusal
messages, verbatim), `hMailServerInnoExtension.iss` (the exit-code check and what
follows it, and the .NET 10 bundling), `DBSetupQuick`'s `UpgradeDatabase` (which
arguments are forwarded) and `Authenticator.AuthenticateUser` (fails instead of
prompting under `/silent`).
