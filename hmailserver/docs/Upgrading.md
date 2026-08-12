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

**Any earlier hMailServer version.** The upgrade chain is continuous: 56 registered
steps, from schema version `0` through to the current **6005**, applied in sequence.
A database at any intermediate version is brought forward one step at a time, so
there is no "you must first upgrade to 5.x" hop to plan around.

That includes databases created by the *original* upstream project. This fork did
not branch the schema; it extended it. Version 6005 is a superset, reached by the
same mechanism upstream used.

What the upgrade touches
------------------------

* **The schema only.** Messages live on disk, not in the database — the database
  holds accounts, domains, settings and message *metadata*. No upgrade step rewrites
  message files.
* **Every supported backend.** Separate script sets exist for MySQL/MariaDB, MS SQL
  Server, PostgreSQL and the embedded SQL CE. The installer picks the set matching
  your configured backend.
* **Nothing is deleted that carries data.** On some backends a few long-dead tables
  are left in place rather than dropped, so a schema comparison between an upgraded
  database and a freshly created one can show extra tables. That is expected and
  harmless; it is not a sign of a partial upgrade.

Doing it
--------

1. **Back up.** Both halves, and they are separate things:
   * the **database** — with your backend's own tools (`mysqldump`, `pg_dump`,
     SQL Server backup). The built-in backup covers this too, but a native dump is
     what you want if you need to restore into a different server.
   * the **data directory** — the message files. This is the part that cannot be
     reconstructed from anything else.
2. **Note your current versions.** The server version is in the Control Panel, and
   the schema version is the `dbversion` value in the settings table. Write both down
   — if you need support, they are the first two questions.
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
failed or cancelled schema upgrade will not report a successful install. That
matters: the alternative is a service running against a schema it does not
understand.

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
notes. One limitation matters here: the administrator password cannot be supplied on
the command line, and a silent upgrade of an existing installation cannot prompt for
it. Upgrade interactively, or set the password non-interactively beforehand.

**The .NET 10 runtime.** From the first release after 6.2.18, the Control Panel and
the setup tools require the .NET 10 Desktop Runtime, which the installer bundles and
installs when missing; 6.2.18 and earlier used .NET 8. Nothing about the server
itself changed — it is native code with no .NET dependency, so an upgrade that
fails to install the runtime still leaves you with a working mail server, just
without the Control Panel.
