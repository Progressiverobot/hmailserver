Warm standby
============

How to run a second hMailServer machine that can take over when the first one
fails, what actually has to be replicated, and the three constraints that decide
the shape of the whole thing. Everything here was verified against the code on
21 August 2026; the *Verified against the code* section at the end says where.

The one-sentence version: **share the database, replicate the data directory,
keep the standby's service stopped, and know that DPAPI-protected passwords do
not travel.** Each of those four clauses is a section below.

Warm, not hot — and this is not a preference
--------------------------------------------

Exactly one hMailServer service may run against the database at a time. This is
not a licensing position or an untested caution; the code makes two servers
actively destructive to each other:

* **Startup unlocks the whole queue.** `PersistentMessage::UnlockAll` runs
  `update hm_messages set messagelocked = 0` for every queued message when the
  service starts — it has to, because locks held by a crashed server would
  otherwise stall those messages forever. Start a second server against the
  same database and that statement releases every message the first server is
  *currently delivering*. Both then deliver them. Every recipient gets
  duplicates, and nothing anywhere looks wrong.
* **Delivery selection is not coordinated.** The queue is driven by
  `select ... where messagelocked = 0 and messagenexttrytime <= now`, and the
  lock is taken after selection. Two servers polling the same table race that
  window even after both have started cleanly.
* **External fetch has the identical shape.** `PersistentFetchAccount::UnlockAll`
  runs at fetch-manager start, so a second server releases the fetch accounts
  the first is mid-download on — and a POP3 account fetched twice with
  delete-after-download is mail split arbitrarily between two stores.

So the standby is *warm*: installed, configured, patched along with the
primary — and its hMailServer service set to **Manual** start and left stopped.
Failover is starting a service, not booting a machine, which is minutes; what
warm standby does not give you is seconds, and pretending otherwise is how the
duplicate-delivery scenario above gets built by accident.

Set the service to Manual on the standby rather than Disabled: a disabled
service cannot be started by the failover runbook without an extra step that
will be forgotten at 3 a.m.

What is where
-------------

| State | Lives in | Reaches the standby by |
|---|---|---|
| Domains, accounts, aliases, lists, rules, routes, IP ranges | the database | sharing the database |
| Message metadata, folders, IMAP state (UIDs, flags, tombstones) | the database | sharing the database |
| The delivery queue | the database (`hm_messages` rows) + spool files on disk | database + data-directory replication |
| Greylisting triplets, auto-ban ranges, login-failure state | the database | sharing the database |
| **Message files (.eml)** | the **data directory** on disk | **replication — see below** |
| `[Settings]` section of hMailServer.INI | both — mirrored into `hm_inisettings` | the mirror, automatically (see below) |
| `[Directories]`, `[Database]` sections of the INI | each machine's own INI | configured per machine, deliberately |
| TLS certificates and private keys | **files on disk** — `hm_sslcertificates` stores *paths* (`sslcertificatefile`, `sslprivatekeyfile`) | replicate the files to the same paths |
| DKIM signing keys | **files on disk** — `hm_domains` stores *paths* (`domaindkimprivatekeyfile`) | replicate the files to the same paths |
| Route / external-fetch / per-domain-relay passwords | the database, **DPAPI-protected** | **they do not travel — see below** |

Two rows in that table do more work than the rest:

**The `[Settings]` mirror is what makes the standby's configuration stay
current.** Every `[Settings]` key is mirrored into `hm_inisettings` and
reconciled by a three-way merge at service start: a row changed while the local
file was not is written back into the file. A standby that shares the database
therefore *inherits the primary's server settings on its next start*, without
anything copying INI files around. What the mirror deliberately does not carry
is `[Directories]` and `[Database]` — the paths and the database connection are
per-machine facts, and they are exactly what you want to differ or verify on
the standby, not inherit.

**Certificate and key paths must resolve on both machines.** The database rows
carry file paths, so if the primary says `C:\certs\mail.pem`, the standby needs
that file at that path. Keep certificates and DKIM keys inside a replicated
directory and use the same drive letter and layout on both machines — a
standby whose paths differ from the primary's rows serves no TLS the moment it
matters most.

The DPAPI constraint — the one thing that genuinely does not travel
-------------------------------------------------------------------

Every password this server has to *present* to somebody else — route smart-host
credentials, external POP3 fetch accounts, the per-domain relay password, SSL
private-key passphrases — is stored in the database protected by Windows DPAPI
with `CRYPTPROTECT_LOCAL_MACHINE`. That is the right storage for a secret that
cannot be hashed, and it has a consequence that no replication strategy can
work around: **a DPAPI blob written by one machine cannot be decrypted by
another.** The protection is the machine.

On the standby, after failover, every such credential fails to decrypt. The
server keeps running — local delivery, IMAP, POP3 and unauthenticated relaying
are untouched — but a route that authenticates, an external account fetch, or
a per-domain relay with credentials will fail authentication until its password
is re-entered *on the standby*, which re-protects it with the standby's own
machine key.

Plan for it rather than discovering it:

* **Keep the list.** Maintain a short, current list of every credentialed
  route, fetch account and relay-configured domain, with the passwords in your
  password manager. Failover ends with walking that list once in the
  Control Panel. On a typical installation it is between zero and five entries.
* **Failback re-breaks them the other way.** A password re-entered on the
  standby is now the *standby's* blob in the shared database, and the primary
  cannot read it when it comes back. The runbooks below include the walk in
  both directions.
* The `[Database]` password in each machine's own INI is also DPAPI-protected,
  but per machine and never replicated — each machine's INI was written by its
  own installer. This one causes no failover work; it is listed so nobody
  "fixes" it by copying the primary's INI over the standby's, which breaks the
  standby's database connection outright.

The message store — and why not SMB or CSV
------------------------------------------

Message bodies are files under the data directory; the database rows point at
them. The two must move together: a database that is current against a message
store that is stale serves mailboxes with rows whose files are missing.

The tempting design is to avoid replication entirely by putting the data
directory on a share both machines can see — SMB, or a Cluster Shared Volume.
Do not. The reasons are specific, not superstition:

* **Durability is anchored to local semantics.** The accept path flushes the
  spool file to disk before answering 250 — that promise is what lets the
  sending server delete its copy. Over SMB the flush is a round trip whose
  guarantee depends on the file server honouring write-through, and the
  failure this topology exists for — the network between the machines
  breaking — now severs the mail store from the mail server *while sessions
  are mid-write*. Every accepted message becomes hostage to a second machine
  and the path to it.
* **The hot path multiplies the cost.** Local delivery copies the spool file
  once per local recipient, and the delivery loop reads and rewrites message
  files (headers, signatures) as a matter of course. Each of those becomes
  network I/O on every message, in the loop that decides how fast mail moves.
* **A dead primary can hold the store hostage.** SMB handles and oplocks held
  by a machine that has crashed take time to expire at the file server. The
  standby starting up can find the very files it needs still locked by a
  machine that no longer exists — the failover stalls on the storage layer's
  timeout, which is the kind of delay warm standby was supposed to bound.
* **CSV is built for the other workload.** Cluster Shared Volumes optimise
  large, block-oriented, coordinated I/O (VHDs, SQL data files). A mail store
  is millions of small files created and deleted constantly from one owner
  node; on CSV that pattern degrades into redirected I/O through the
  coordinator node, and you inherit a cluster's complexity to get storage
  slower than a local disk.

**Replicate instead**, and let the database be the arbiter of truth:

* A scheduled `robocopy /MIR` of the data directory (certificates and DKIM
  keys included) every few minutes is the honest baseline: simple, observable,
  restartable, and its lag is measurable.
* DFS-R or storage-level replication work too; what matters is that the
  standby has a *local* copy it owns outright the moment it starts.
* **Know what the lag costs.** Messages accepted after the last sync exist as
  database rows whose files are missing on the standby. The server treats a
  missing message file as an error on access, not a crash: those specific
  messages are unreadable until the primary's disk is recovered, and
  everything else serves normally. A five-minute replication interval bounds
  the loss window to five minutes of *bodies* — the metadata, folders and
  flags are all in the database and lose nothing.
* Replicate one way, primary to standby, and stop replication as part of
  failover — the first thing a failed-over standby must not receive is a
  stale mirror pass from a half-dead primary deleting its newly accepted mail.

The database itself
-------------------

* Use a real server backend — SQL Server, MySQL/MariaDB or PostgreSQL — with
  its own availability story (that ecosystem's replication, not this
  document's). **SQL Server Compact is excluded from everything here**: it is
  a single local file, and a standby cannot share it.
* Both machines must run the same hMailServer build. The schema version pin
  (`REQUIRED_DB_VERSION`, checked at every start) refuses a mismatch in both
  directions rather than corrupting anything — so a half-upgraded pair fails
  safe, loudly, at startup. Upgrade the standby's binaries in the same
  maintenance window as the primary's.
* The statement timeout (`DatabaseStatementTimeout`, default 30 s) applies on
  whichever machine is active; nothing about it is topology-specific.

Building the standby
--------------------

1. Install the same hMailServer build as the primary, with the same drive
   letter and directory layout.
2. Point its `[Database]` section at the same database server. Verify with the
   Control Panel that it connects — then **stop the service and set it to
   Manual**.
3. Set up data-directory replication (primary → standby), covering the message
   store, certificates and DKIM keys.
4. Record the DPAPI credential list (routes, fetch accounts, per-domain
   relays) somewhere that survives the primary.
5. Rehearse the failover below at least once before it is real. A standby that
   has never been started is a hope, not a topology.

Failover
--------

1. **Make sure the primary is actually stopped.** Powered off, service
   stopped, or network-isolated — one of these must be true before step 3,
   because of the queue-unlock behaviour described at the top. If the primary
   is unreachable rather than confirmed dead, isolate it (switch port, VM
   network) before proceeding.
2. Stop the replication job, in whichever direction it runs.
3. Start the hMailServer service on the standby. Its startup unlock now works
   *for* you — messages the dead primary held locked mid-delivery are
   released and retried. A message the primary had delivered but not yet
   deleted from the queue may be delivered again; that duplicate is the known,
   bounded cost of failing over mid-flight, and it errs on the side mail
   errs on.
4. Move the public endpoint — DNS, VIP, or port forward — to the standby.
   Keep MX TTLs modest (300–3600 s) as a standing decision, not a failover
   step; sending servers retry for days, so mail queued remotely during the
   DNS window is delayed, not lost.
5. Walk the DPAPI credential list: re-enter route, fetch and relay passwords
   in the Control Panel on the standby.
6. Verify (below).

Failback
--------

Failback is the same procedure with the roles swapped, plus one step at the
front: **resynchronise the data directory backwards first** — the standby has
been accepting mail, and the primary's store is now the stale one. Then stop
the standby's service, replicate standby → primary once more for the final
delta, start the primary, move the endpoint back, re-enter the DPAPI
credentials on the primary, and re-establish primary → standby replication.

Verify
------

After either direction:

* `GET /readyz` on the metrics listener answers 200 — that means the service
  is running *and* the database has answered a real round trip within 20 s.
* Send a message in from outside and read it back over IMAP or POP3.
* Send a message out through each credentialed route, if any — this is the
  DPAPI walk proving itself.
* Check the application log for `ERROR` entries since the failover timestamp.
* Confirm TLS on 25/143/110/587/993/995 presents the right certificate —
  this is the file-path row of the table above proving itself.

Verified against the code
-------------------------

Checked 21 August 2026. The startup queue unlock is
`PersistentMessage::UnlockAll` (`update ... set messagelocked = 0`), called from
`SMTPDeliveryManager::Start`; the fetch twin is `PersistentFetchAccount::
UnlockAll` in `ExternalFetchManager`. Queue selection is
`SMTPDeliveryManager::LoadPendingMessageList_`
(`where messagelocked = 0 and messagenexttrytime <= now`). DPAPI protection is
`DataProtector`/`Crypt::ProtectSecret` with `CRYPTPROTECT_LOCAL_MACHINE` and a
fixed description string, used for route, fetch-account, per-domain-relay and
SSL-key passwords. The `[Settings]` mirror and its three-way merge are
`IniSettingStore`, whose header documents the file-wins/row-wins rules quoted
here. Certificate and DKIM key *paths* (not blobs) are `hm_sslcertificates.
sslcertificatefile`/`sslprivatekeyfile` and `hm_domains.domaindkimprivatekeyfile`
in the create scripts. The schema pin is `REQUIRED_DB_VERSION` in `Constants.h`,
enforced by `Application::OnDatabaseConnected` in both directions. The
missing-message-file behaviour and the accept-path flush are
`PersistentMessage`/`TransparentTransmissionBuffer`. The SMB/CSV reasoning is
operational judgement built on those code facts, not itself a code fact — the
durability anchor, the per-recipient copy and the read-rewrite loop are in the
code; the handle-expiry and CSV redirected-I/O behaviour are Windows storage
semantics.
