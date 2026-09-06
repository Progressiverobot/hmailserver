Migrating mail into hMailServer
===============================

How to bring existing mailboxes into this server: from another IMAP server, from
mbox files, from a Maildir, from Outlook, and from the original upstream
hMailServer. What each route preserves, what it does not, and what to do about the
one format the server does not read.

If you are moving an *installation* of hMailServer rather than mail from somewhere
else, this is the wrong page: [Upgrading.md](Upgrading.md) covers a release-to-release
upgrade and the move from the upstream project, and
[MigratingDatabaseBackend.md](MigratingDatabaseBackend.md) covers a change of database.

The routes at a glance
----------------------

| From | Route | Keeps | Does not keep |
|---|---|---|---|
| Another IMAP server (Dovecot, Exchange, Gmail, Microsoft 365, the old hosting) | An external account with **Mirror every folder** on: the server itself collects every folder over IMAP | Folders and their hierarchy, every message byte for byte, `\Seen` `\Flagged` `\Answered` `\Draft` `\Deleted`, the internal date | Subscriptions, custom keywords, ACLs, what `\Noselect` nodes hold (nothing) |
| mbox files (Thunderbird's profile, Apple Mail export, a Unix spool) | The Import Tool, one IMAP folder per file | Every message; the date from the `Received` or `Date` header | Flags: mbox has no standard for them, and the Import Tool sets none |
| A Maildir (Dovecot, Courier, Postfix local delivery) | The Import Tool, the folders and flags read from the tree | The Maildir++ hierarchy, every message, the flags the file names carry, the date from the headers | Custom keywords; the arrival time in the file name (the header date is used instead) |
| Outlook (PST) | Outlook itself, through IMAP - see below | Whatever Outlook uploads: folders, messages, read and flagged state | Anything Outlook does not put on the wire |
| The upstream hMailServer, in place | The installer, over the existing installation | Everything | Nothing - see [Upgrading.md](Upgrading.md) |
| Accounts in bulk | The Import Tool's text-file import | Addresses, passwords, quotas | Mail - pair it with one of the routes above |

From another IMAP server
------------------------

The server has an IMAP client of its own - the one that collects external accounts -
and with the mirror switch on it does what `imapsync` does: every folder the remote
lists, every message not yet collected, filed into the local folder of the same
name exactly as the remote holds it.

1. Create the local account the mail is going to.
2. On it, add an external account: **server type IMAP**, the remote host, port and
   security (implicit TLS on 993, STARTTLS on 143, or a host on `FetchOAuth2Hosts`
   for XOAUTH2), the remote credentials, and **Mirror every folder** on. Over COM
   that is `FetchAccount.ServerType = 1` and `FetchAccount.MirrorFolders = true`.
3. Decide what happens on the far side. **Days to keep messages** is per collected
   message, as for any external account: 0 deletes each message from the remote
   folder once it is safely filed here - a move - and any other value leaves it
   there, so the two servers stay in step for as long as the account polls. A
   migration usually runs with the messages left in place until the cut-over, then
   one last poll and the account is disabled.
4. Poll. **Download now** starts a collection at once; otherwise the account polls
   at its interval. A large mailbox is collected over several polls if the remote
   server ends the session first; nothing is lost, because a message is recorded
   as collected only after it has been filed, and the next poll takes up where the
   last one stopped.

What it does, folder by folder: `LIST "" "*"`, then for each mailbox that can be
selected, `SELECT`, `UID SEARCH ALL`, and `UID FETCH (FLAGS INTERNALDATE BODY.PEEK[])`
for every UID not on record for that mailbox. The message is stored byte for byte -
no `X-hMailServer-ExternalAccount` header, no delivery, no rules, no anti-spam or
anti-virus, because it is a copy of mail the other server had already accepted, not
new mail arriving. The remote flags become the local flags and the remote internal
date becomes the local one, so a mailbox migrated today does not read as having
arrived today. The remote hierarchy delimiter is mapped to the local one:
`Archive/2025` on a server that separates with `/` becomes `Archive.2025` here, the
folder `2025` inside `Archive`. Mailbox names in modified UTF-7 pass through as they
are; both servers speak it.

Each mailbox keeps its own record of what has been collected (the record names the
mailbox, its UIDVALIDITY and the UID), so the same UID in two mailboxes is two
messages, a second poll asks each mailbox only for what is new, and a mailbox whose
UIDVALIDITY changes is collected afresh - the same rules the INBOX-only collection
has always followed, applied per folder.

Things to know before switching it on:

- **Use a new external account for the mirror.** An account that has been
  collecting the INBOX without the mirror keeps its records under a bare key; the
  mirror's keys name the mailbox. Switching the mirror on for that account
  collects the INBOX once more.
- A mailbox the remote refuses to `SELECT`, or lists as `\Noselect`, is skipped with
  a line in the application log, and the mailboxes after it are still collected.
  A mailbox name the remote sends as a literal (rare; a very long name) is skipped
  the same way.
- The `OnExternalAccountDownload` script event does not fire for a mirrored
  message, and the per-message override of *days to keep* it offers does not
  apply; the account's value does.
- Subscriptions are not copied. The local folders are created subscribed, so a
  client that shows subscribed folders sees them all.
- It is a mirror of messages, not of the account: ACLs, quotas, sieve or vacation
  settings on the far side are not read.

From mbox files
---------------

The Import Tool (`Start menu > hMailServer > Import Tool`, or
`Addons\ImportTool\ImportTool.exe` under the installation) imports a folder of mbox
files into one account, one IMAP folder per file, named after the file without its
extension; `Inbox` (any case) is the INBOX. Thunderbird's profile keeps its
folders this way (`Mail\<account>\Inbox`, `Sent`, `Archives.sbd\2025`), and its `.msf`
index files beside them are ignored. Each message is streamed out of the file - a
mailbox larger than memory is fine - its line endings made CRLF, and handed to the
server, which files it and takes the date from its `Received` or `Date` header.
mbox carries no flags the tool could read, so every imported message is unread.

From a Maildir
--------------

The same tool, the **Select Maildir** button. Point it at the Maildir itself - the
directory holding `cur`, `new` and `tmp` - and it lists the INBOX and every
Maildir++ folder beside them (`.Sent`, `.Archive.2025`: the names start with a dot
and use a dot as the separator, which is also this server's default, so
`.Archive.2025` becomes the folder `2025` inside `Archive`). Every file in `new` and
`cur` is a message; `tmp` is never read.

The flags a file's name carries after `:2,` are set on the copy once the folder is
imported: `S` seen, `F` flagged, `R` answered, `D` draft, `T` deleted. Line endings
are made CRLF, as with mbox. The date comes from the message's headers rather than
from the timestamp in the file name.

A colon is not allowed in a Windows file name, so a Maildir cannot be copied here
with its names intact: whatever brought it over either replaced the colon or dropped
the flags with it. The tool reads `;2,` and `!2,` as well as `:2,`, which covers the
copies that replaced it. If your copy shows names ending in `_2,FS` or without the
info part at all, the flags did not survive the copy - the messages still import,
unread, and the fix is to copy again with a tool that maps the character (WSL, or an
archive made on the Unix side and opened with 7-Zip, keep it).

If the server's IMAP hierarchy delimiter has been changed from `.`, a Maildir++
folder name is still split on `.`, because that is what the format means by it.

From Outlook
------------

The server does not read PST files, and this is deliberate. PST is a proprietary
format whose readable implementations are either commercial or GPL-licensed
libraries with a long record of fidelity problems on real archives, and a migration
tool that silently drops or damages a message is worse than none. Outlook itself,
which wrote the file, is the reliable reader of it:

1. Add the hMailServer account to Outlook as an IMAP account.
2. Drag the folders from the PST-backed account onto the IMAP account, or copy
   them. Outlook uploads each message with `APPEND`, flags and dates included, and
   creates the folders as it goes.
3. When the folder counts match, remove the PST account from Outlook.

The same works from any client that speaks IMAP, and it is also the way out of a
mail service that offers no IMAP access but has a desktop client that does.

From the upstream hMailServer
-----------------------------

Run this fork's installer over the existing installation. The database is upgraded
in place, the message store is untouched, and the settings carry over.
[Upgrading.md](Upgrading.md) has the detail, including what to back up first.

Accounts in bulk
----------------

The Import Tool's other button creates or updates the accounts of one domain from
a comma-separated text file - address, password, and optionally the quota - which is
the usual first step before any of the routes above, since each needs the local
account to exist.
