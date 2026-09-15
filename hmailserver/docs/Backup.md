What the backup carries, and what it does not
=============================================

The built-in backup writes one 7-Zip archive per run: an XML index, and — when
**Back up messages** is on — the whole message store beside it. A restore is
destructive and has no transaction: it deletes every domain, every account and
every public folder before it puts the archive's back. So the only question that
matters about an archive is what is *in* it, because everything an account has
that is not in it is deleted by the restore and not replaced.

This document answers that question, item by item, and is checked rather than
remembered: `build/check-account-stores.py` reads the `CREATE TABLE` scripts
against the server's own declaration and fails the build when a table that holds
one account's data is in neither list. It runs on every pull request.

Before 15 September 2026 seven of the tables below were in neither, and
restoring an archive destroyed them. If you are running 6.3.3 or earlier, read
the last section.


In the archive
--------------

### The server

| What | Notes |
|---|---|
| Domains, and their aliases, distribution lists and signatures | |
| Accounts | Address, password hash, forwarding, vacation, signature, quota, admin level, TOTP secret, anti-spam thresholds |
| Aliases and distribution lists, with their members | |
| Routes, IP ranges, TCP/IP ports, SSL certificate *records*, incoming relays, groups | Only with **Back up settings** on |
| Anti-spam and anti-virus lists | DNS black lists, SURBL servers, white list, blocked senders, blocked attachments, greylisting white list |
| Public folders, and the permissions on them | |
| The `[Settings]` section of `hMailServer.ini` | Mirrored into the database since schema 6011 and carried with the settings |

### Everything an account has

| What | Table | Notes |
|---|---|---|
| Folders and messages | `hm_imapfolders`, `hm_messages` | Only with **Back up messages** on. A message keeps its UID, its flags **and its keywords** — the webmail's labels, the second and third stars, follow-up flags, mute and pin |
| Rules | `hm_rules`, and its criteria and actions | |
| Fetch accounts, and the UIDs already downloaded | `hm_fetchaccounts`, `hm_fetchaccounts_uids` | |
| Application passwords | `hm_apppasswords` | The hashes, so a restore does not silently revoke every client the account holder set up |
| The address book | `hm_contacts` | Including the vCard a CardDAV client stored, under the resource name it chose |
| The webmail's remembered choices | `hm_accountprefs` | Theme, density, reading pane, notifications, language, labels and their colours, saved searches, swipe actions — the server attaches no meaning to a key and carries each verbatim |
| Scheduled sends and snoozes | `hm_scheduled` | See *References*, below |
| Files sent as expiring links | `hm_files` | The bytes are already in the message store; this is the record that gives them a name, an owner and an expiry |
| S/MIME certificates and keys | `hm_smimekeys` | Including the wrapped private key. This server cannot open it, so if a restore lost it nothing would fail — the account would simply never read its own encrypted mail again |
| The password reuse history | `hm_passwordhistory` | So a restore does not let every account go back to a password it was made to change |
| Calendars and their events and tasks | `hm_calendars`, `hm_calendarobjects` | Tombstones included, so a CalDAV client's next sync is answered correctly rather than being told nothing was ever deleted |

The per-account stores are carried as rows under their account, in the archive's
own XML. A value is written as an attribute where every character of it is safe
in one, and as base64 of its UTF-8 under the same name with `.base64` appended
where it is not — so most of the index stays legible, and a vCard, an iCalendar
object or a PEM block cannot make the archive unreadable.


Not in the archive, deliberately
--------------------------------

| What | Why, and what to do |
|---|---|
| `[Database]` and `[Directories]` in `hMailServer.ini` | Per-machine. They are how this installation finds its database and its data folder, and they would be wrong on the machine you restore onto. Keep a copy of the file |
| The administrator password and its second-factor secret | In `hMailServer.ini`, for the same reason: they are what lets you in when there is no database |
| TLS certificate and DKIM key **files** | The database stores the *paths*, not the files. A restore onto a machine where those paths hold nothing gives you a server that starts, serves no TLS and signs nothing. Keep them inside the data directory (where the message-store backup carries them) or copy them yourself |
| Mail still in the delivery queue, and its recipients | A queued message is in flight rather than in a mailbox. Let the queue drain before a migration |
| The full-text index | Derived from the messages, and rebuilt by the indexer. Carrying it would roughly double the archive to save work a background task does anyway |
| Per-message and per-mailbox annotations (RFC 5257, RFC 5464) | Keyed on ids a restore reassigns. Recorded as a gap in `Roadmap2.md` rather than silently dropped |
| The expunge log a QRESYNC client is answered from | Restoring it would tell a client that messages the restore has just put back are gone |
| Quarantine, message trace, metric history, the archive index | Server-wide operational records rather than an account's data. Back the database up if you need them |


References: how a scheduled send survives a restore
---------------------------------------------------

A restore inserts every row afresh and the database hands out new identities, so
nothing in the archive can be an id. That is what makes the archive portable
between database backends at all — see
[MigratingDatabaseBackend.md](MigratingDatabaseBackend.md).

One row type names another: a scheduled send names the draft to send and the
folder to return a snoozed message to. Writing those numbers into the archive
would, after a restore, point a scheduled send at whatever message happened to be
given that id — so they are carried as what survives a restore instead: the
folder's path, segment by segment, and the message's UID. On the way back in the
path and the UID are resolved against the folders and messages this restore has
just put back.

A reference that cannot be resolved — the draft was deleted, or the archive was
restored without its messages — drops that one row, counts it, and says so in the
backup log by account. Nothing else in the account is affected. Restoring a row
that points at nothing is the one outcome worse than losing it.


What the verification proves
----------------------------

Every run reads its own archive back before it is called a backup, and before
retention is allowed to consider deleting an older one:

1. **The index opens**, through the same code a restore uses to open one, and
   describes what this run put in it.
2. **The per-account rows are all there.** What the run built is counted against
   what the archive reads back, per table. A difference means the archive does not
   contain what was backed up, so it is discarded and the backup fails.
3. **The message store extracts** to exactly the files and bytes that were staged
   for compression. Fewer of either is a truncated archive, whatever 7-Zip's exit
   code said.
4. **Every message row has a file.** This one is *reported*, never failed: the
   rows were read before the copy started and mail flow does not stop for a
   backup, so a message deleted in between is normal. A large number means the
   backup was taken during a bulk deletion and is worth taking again.

Step 3 costs disk: for the length of the check the message store exists twice, in
the temp directory. A temp volume that cannot hold it skips the step with an
explanation rather than failing a good backup. `BackupVerifyRestore=0` turns
steps 3 and 4 off and says so in the log.


Versions
--------

An archive from an **older** hMailServer restores. What that version did not
write is simply absent, and the restore does not complain about it: an archive
from before this change carries no per-account stores and no keywords, and
restores exactly as it always did.

An archive from a **newer** hMailServer is refused, with a sentence naming both
versions and telling you to upgrade first. There is deliberately no override: a
newer archive can carry properties this build has no column for, and those would
be dropped in silence while the restore reported success.

An archive that names a table this build does not have — which can only be a
hand-edited one, or one from a build whose version string cannot be parsed — has
those rows counted and named in the backup log, and the rest of the restore goes
ahead. Refusing a whole restore over rows nothing here could use would cost you
your mail to protect something you cannot have.


Adding a per-account table
--------------------------

For anyone working on the server. Everything that holds one account's data is
declared in
`hmailserver/source/Server/Common/Application/AccountStores.cpp`, in one of two
arrays:

* `stores_` and `columns_` — the table, and every one of its columns with the
  role it plays. The backup reads it through that declaration and the restore
  writes it back through the same one.
* `elsewhere_` — the table, and the reason it is not carried there: a business
  object already writes it, or it is derived, or it is in flight.

`build/check-account-stores.py` fails the build when a table that reaches
`hm_accounts` through `ON DELETE CASCADE`, or that has an account-id column, is
in neither — and when a column of a declared store is missing, when the three
`CREATE TABLE` scripts disagree about one, or when a store has no test in
`hmailserver/test/RegressionTests/Infrastructure/BackupAccountStores.cs`.

The test shape is the same for every store: make the data through the surface
that really writes it, take a backup, delete the whole domain the way a disaster
would, restore, and find the data exactly as it was.


If you are running 6.3.3 or earlier
-----------------------------------

The seven per-account tables above — the address book, the webmail's settings,
scheduled sends, file links, S/MIME keys, calendars and the password history —
and a message's keywords were in no archive those releases wrote, and a restore
deletes them. Until you are on a release that carries them:

1. **Back up the database as well**, with your database server's own tool:
   `pg_dump`, `mysqldump`, SQL Server's backup, or a copy of `hMailServer.sdf`
   with the service stopped.
2. **Restore from that** when what you are recovering is a whole server, and use
   the built-in restore for what it was good at: moving domains, accounts and the
   message store between installations.
3. If you restore from an archive those releases wrote, expect users to find
   their address book, labels and webmail settings gone, and tell them before
   they find out themselves. An archive written by an older release does not gain
   what it never held by being restored onto a newer server.
