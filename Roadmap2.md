# Roadmap 2: the programme after 6.3.2

**Written 14 September 2026.** [Roadmap.md](Roadmap.md) is the record: 864 rows done, a handful open, three dozen deferred with their reasons. It has grown into the history of the project, and it is kept because every row in it is a claim that was checked. This file is the plan: what still stands between this server and the best mail server there is, held against what the people who run Gmail, Outlook.com, Exchange, Dovecot and Stalwart offer, and worked through in the order below. A row moves out of here into Roadmap.md when it is done, with the measure that says so.

The legend is Roadmap.md's: ✅ done and verified, 🔄 underway, ⬜ not started, ⏸️ deferred with the reason written down.

## 1. Landing what was built on 13 and 14 September

| | Item | Where it stands |
|---|---|---|
| ✅ | **Linux suite waves A to F** (PR #251) | Merged 14 September, 02:46. Windows 2,233 / 2,225 passed / 0 failed / 8 skipped; Linux 1,457 / 1,185 passed / 0 failed / 272 skipped. Schema 6039: a domain's relay password could not be saved on any Windows installation, found by the first honest gate of the branch. |
| ✅ | **One header on every source file** (#256) | On master 14 September, 05:02, by the direct push. |
| ✅ | **The tidy of the two Copilot drafts, and the two defects its own gates found** (#255) | On master 14 September, 05:02. Gated green at the third attempt. The first gate died in `generate-com-wrapper.ps1` under Windows PowerShell (MIDL's stderr as a terminating error); the second on a recordset walk that a COM error could end with a minidump - both fixed on the branch. |
| ✅ | **The live update from an open Control Panel** (#258) | On master 14 September with batch 2 (#259): its own gate was green at f124e5e09, and the batch's at a9261d7eb (2,267 tests, 2,259 passed, 0 failed, 8 skipped). It carries one more fix, from batch 1's Linux run: a message expunged between the indexer listing it and its terms being saved is no longer reported as an error (PostgreSQL's foreign key refuses the save; SQL Server Compact accepted it). The first real update, 6.3.1 to 6.3.2, failed twice with Inno's exit code 5 because the Control Panel that started it kept its own files open. The helper now ends what runs from under the installation, the Control Panel closes itself, and a failed run quotes the installer's log. |
| ✅ | **Linux suite waves G and H** (#254), **CardDAV** (#253), **the webmail rebuilt** (#257), **four more webmail features and twelve search operators** (#259) | On master 14 September as one batch, pushed whole after the full Windows suite on a9261d7eb: 2,267 tests, 2,259 passed, 0 failed, 8 skipped. The batch's first gate failed two CardDAV tests - a comma-bearing TYPE list quoted on the way out, so a card the webmail had edited came back with `TYPE="INTERNET,PREF"`, and a 4.0 card a test still expected served back as 3.0 - fixed in the batch's last commit, the collection now advertising both versions. |
| ✅ | **The Deck census, the REST write routes it found missing, and the Deck views** (#260) | On master 14 September with the fourth batch, whose gate covered it: its own full Windows suite on 1e549fe9f (2,279 tests, 2,268 passed, 3 failed, 8 skipped) failed three tests the next commits put right - two in the account update's new fields (two tests wanting two sentences for a bad expiry date, and an expiry of today expected to leave the message on, where the read has always switched it off), one a test's timing - and its hosted Linux run (1,493 tests, 1,246 passed, 2 failed, 245 skipped) two more - **on PostgreSQL every value with a backslash was stored with it doubled** (the escaper doubled it unconditionally, right only while `standard_conforming_strings` is off, and it has been on by default since 9.1; the connection now reads the server's answer and doubles only where the server escapes), and the pipelined 222 KB chunk test's timing. Its CI had found two Linux-only compile errors on the way (an include spelled `PersistentDNSBlackList.h` for a file named `PersistentDNSBlacklist.h`; the Linux suite's shims lacking the anti-virus group and nine domain properties), and it carries two fixes besides: the container image job of a tag run, which asked docker for `Progressiverobot/hmailserver` and was refused the capital letter at 6.3.2; and **issue #261** - an outbound BDAT chunk larger than one send buffer stalled after the first buffer, because the second was queued behind the reply's read in a queue that starts only its head, so every message over 60,000 bytes to a CHUNKING remote had timed out since outbound BDAT arrived (the queue runs once more after a read starts; two 222 KB tests). |
| ✅ | **The webmail's next six features, the Deck to parity, and the Linux suite's last route families** (#262) | On master 14 September as the fourth batch - three branches built in parallel by three agents off the third batch's tip and chained, the last rebase's conflicts all of the both-added kind - after the full Windows suite on its tip: the numbers are on the wiki's Changes page, section 4d, written once the batch had landed; the hosted Linux run on the same tree: the numbers are on the wiki's Changes page, section 4d, written once the batch had landed. |
| ✅ | **6.3.3** | Published 15 September 2026 as the latest release, build 42, schema 6040. Cut from master plus what the adversarial review of the day confirmed (the REST ini routes answering to the administrator password only, a domain-scoped key refused a domain's limits, the CardDAV entity scan and multiget bounded, MySQL's card column widened) and the fix for issue #263, which had stopped every upgrade from a schema older than 6038 in 6.3.2. The assertion build's suite first: 2,302 tests, 2,294 passed, 0 failed, 8 skipped; then the stamped shipping binary's: 2,302 tests, 2,294 passed, 0 failed, 8 skipped; three clean builds of the tree produced the same `hMailServer.exe`. Installer `hMailServer-6.3.3-x64.exe` SHA-256 `45A9D7F1A78BB31031DDE27849FA21828A60AFF178F32CBC2B0D9CD93F6B33F2` as built, Authenticode-signed on publication; the six Linux packages, two SBOMs and the cosign bundles attached by the release workflows, the smoke test green before it was published. The notes say that an installation on 6.3.1 or 6.3.2 must be updated by hand once, since the helper that runs a live update is the one already installed; the wiki's changes page carries the release as section 4d and the schema steps 6039 and 6040. Issues #261 and #263 closed. |

## 2. The Linux control panel: the browser Control Deck to parity

The desktop Control Panel is WPF and runs on Windows only; that will not change, and it should not - it is the best-in-class Windows administration program this server has. On Linux the administration surface is the browser Control Deck served at `/WebAdmin`, the REST API (81 routes, 40 of them administrative), and `hmailserver --set-admin-password`, `--create-database`, `--upgrade-database`. The Deck today has ten views - dashboard, domains, delivery queue, logs, rules, routes, certificates, ports, TLSA, settings - which is far more than Roadmap.md's row on it still says, and far less than the desktop program. Parity is the item.

| | Item | Detail |
|---|---|---|
| ✅ | **A census: the desktop Control Panel's every page and field against the REST API and the Deck** | **Landed 14 September (#260).** `build/check-deck-parity.py`, run in CI, and `hmailserver/docs/DeckParity.md`: 335 properties the desktop writes; 306 writable over REST, 248 reachable from a Deck view, 29 missing over REST and 58 over REST that no Deck view reaches - from 240, 153 and 95 at the start of the day. The row as written: A script under `build/`, run in CI like the settings-index check, that lists each COM property and Control Panel control and whether a REST route writes it and a Deck view shows it. The number it prints is this section's measure; it reaches zero when the section is done. |
| ✅ | **The REST write routes the census finds missing** | **Landed 14 September, in two batches (#260, #262):** the anti-virus settings group, sixteen more account fields, nine more domain fields, the distribution list's settings and a `PUT` for it, DNS blacklists, SURBL servers, white-list addresses, blocked senders, incoming relays, blocked attachments and the greylisting white list as resources, the cache group's eight ceilings and lives, an IP range's expiry, `abort_spam_flagged` on a rule's forward and reply, `user_interface_language`, application passwords and folder permissions administered under the account's address; 95 missing became 2 - groups and their members, which no Deck view needs yet - after the census was corrected for what it had misread (a diagnostic's inputs, a parent's id, a name that is a key). The row as written: Each validated by the code that owns the value, as the settings, rules, routes, aliases, accounts, certificates and listeners already are. Likely: distribution-list members and moderation, per-account rules and vacation, DKIM keys per domain, IP ranges' every field, backup and restore, archive holds, quarantine release, API-key scopes, the anti-spam and anti-virus settings in full. |
| 🔄 | **The Deck rebuilt as one page the way the webmail was** | **The reach half is done, 14 September (#260, #262):** a harness of its own (`build/check-deck-script.py` and `build/deck-script-test.js`, 292 checks, in CI), full domain editing, an IP-ranges view with expiry, fetch-account and backup views, the account editor in full (28 keys, grouped as the desktop groups them), distribution lists and aliases under their domain, the seven small collections from one description, server messages, the scripting, cache and indexing groups with their verbs, and the rule editor's abort flag - every property writable over REST that the Deck's views cover it reaches, 322 of 328, the six outside being the administered app passwords and folder permissions that landed last. **Left, the shape half:** the same design tokens, dark theme and keyboard as the webmail, and views for what has no REST resource yet (groups). The row as written: The same design tokens, dark theme and keyboard as the webmail; a harness that runs its script like `build/portal-script-test.js`; served from the binary so the file is the file checked. Views for everything the desktop program has: accounts in full (forwarding, vacation, signature, rules, quota, two-factor and application passwords, directory link), aliases and lists, per-domain DKIM/DMARC/MTA-STS/TLS-RPT, IP ranges, auto-ban and lockout, spam and virus filtering, backup and restore, archive and legal holds, quarantine, API keys, metrics history, the update page, diagnostics (MX, SRV, DANE, DNSSEC, TLSA probes), live logs, external setup. |
| ⬜ | **The Deck in the seventeen languages** | The same catalogues and checks the Control Panel and the webmail have. |
| ✅ | **The Linux package's administration story written down** | **Done 15 September 2026.** The wiki's *Installing on Linux* page had the walk - the database, the ini, the administrator password, the first start under systemd, the Deck - and the README and the page's own *Administering it* section still described the Linux server of 6.3.0, with DKIM, the domain limits, the signature and the relay host named as unreachable. Both say what 6.3.3 reaches: a domain whole over `PUT /api/v1/domains/{domain}` (DKIM signing included), accounts in full, the settings groups, IP ranges, fetch accounts, application passwords, folder permissions and the message store, and the Deck at parity with the desktop program by the census (322 of 328). What is still outside is stated instead of discovered: groups, `Account.ExportMessages`, a GSSAPI bind, a re-keying command. |

## 3. The webmail against Gmail and Outlook.com

What it has, measured against the two on 14 September: a reading pane (right, below, off), conversations, a list with sender, subject, snippet, time and hover actions, search with operators and an options panel, labels with colours, stars, pins, mute, block, sweep, archive, junk, snooze, scheduled send, undo send, templates, signatures and identities, vacation, forwarding, rules, drag-and-drop to folders, right-click menus, keyboard shortcuts and a palette, tabs (Primary, Social, Promotions, Updates, Forums) or Focused and Other, select-all across a folder, attachments with previews and large ones as links, S/MIME, read receipts, one-click unsubscribe, contacts with CardDAV, notifications, offline reading and an outbox, a phone layout that installs as an app, dark theme, twenty languages. What it lacks:

| | Item | Detail |
|---|---|---|
| ⬜ | **The README's screenshots against the real bench** | The tooling is ready (`readme-seed.ps1`, `readme-shots.py`); taken as soon as the webmail gate leaves the build running. |
| ⬜ | **A UX audit on the real bench, desktop and phone, light and dark** | Every view walked with a seeded mailbox and screenshots read, the way the tab bar's overflow was found; what it finds fixed on the same branch. Then the same by a screen reader (NVDA) and by keyboard alone, since accessibility is where Gmail is strongest and most webmails weakest. |
| ⬜ | **Calendar and tasks** | The largest gap; section 4. |
| ⬜ | **Contact groups** | A group in the address book, addressed by name in To, and served over CardDAV as a vCard KIND:group. |
| ⬜ | **Important, and sections of the inbox** | Gmail's importance marker (who you write to, what you open) as a local heuristic with no server learning; an inbox that can show Important, Starred and Everything else as sections. |
| ⬜ | **Delegation in the page** | The server has shared folders and the post right; the page should let an account open a mailbox it is delegated to and send as it, as Outlook's "open another mailbox" does. |
| ⬜ | **Emoji, images inline in compose, tables** | The rich editor takes text, links and formatting; pasting an image, an emoji picker and simple tables are what people reach for next. |
| ⬜ | **Performance at a hundred thousand messages** | Measured, not assumed: the list is chunked, the search bounded; a seeded folder of that size on the bench, timed for open, scroll, search and select-all. |
| ⬜ | **A second account in the same page** | Identities cover addresses of one mailbox; two mailboxes side by side is what Outlook does and Gmail does not. |
| ⏸️ | **Confidential mode, tracking, translation, AI writing, chat and video** | Declined with reasons: an expiring or unforwardable message cannot be enforced for a recipient elsewhere and would be a promise the server cannot keep; tracking readers is the thing this project refuses to do to them; translation and AI writing need a third-party service or a local model, and neither belongs in the server until one can run on the server's own machine; chat and video are not mail. |

## 4. Calendar and tasks (Phase 6 of the client programme)

| | Item | Detail |
|---|---|---|
| ⬜ | **CalDAV on the web-services listener** | Beside CardDAV, on the WebDAV layer it built: calendar collections, PUT/GET/DELETE with ETags, PROPFIND, REPORT calendar-query, calendar-multiget and sync-collection, free-busy. Schema: the calendars and their components. RFC 4791 fixtures, then an interoperability check with Thunderbird, iOS and DAVx5. |
| ⬜ | **iCalendar** | A parser and writer of VEVENT, VTODO and VALARM with recurrence (RRULE, EXDATE, RDATE) expanded correctly across time zones, because a calendar that gets recurrence wrong is worse than none. |
| ⬜ | **iMIP: invitations by mail** | REQUEST, REPLY, CANCEL and COUNTER as text/calendar parts; the page shows an invitation card with Accept, Tentative and Decline, replies by mail, and updates the calendar; the server files a REPLY into the organiser's event. The only standards path to Exchange and Microsoft 365. |
| ⬜ | **The calendar page** | Month, week, day and agenda views; create by click and drag, move and resize by drag, a recurrence editor, reminders by notification, time zones, several calendars with colours, shared calendars through CalDAV ACLs. |
| ⬜ | **Tasks** | VTODO lists with due dates and completion, in the page and over CalDAV, which is how Apple Reminders and Tasks.org sync. |

## 5. The Linux port beyond compiling

(The .deb's Boost pin, once a row here, was closed on 11 September: Boost is linked statically and the packages install on Ubuntu 26.04, proven in CI.)

| | Item | Detail |
|---|---|---|
| ⬜ | **The service host: systemd, and a live update on Linux** | The update helper is a Windows program; on Linux the apply step is a package upgrade or an AppImage swap under a systemd unit that restarts the server, with the same outcome file and the same rollback. |
| 🔄 | **The Linux suite's remaining skips** | The anti-virus settings and distribution lists over REST landed 14 September (#260; the hosted run on that tree: 1,493 tests, 1,246 passed, 2 failed, 245 skipped), and the message object, application passwords and folder permissions the same day (#262; the numbers are on the wiki's Changes page, section 4d, written once the batch had landed), each with the shim taught to use it. Left: `Account.ExportMessages`, groups, and the three fixture files that do not yet compile against the shims (`ACL.cs`, `API/Basics.cs`, `MessageUids.cs`). |

## 6. Measured quality

| | Item | Detail |
|---|---|---|
| ⬜ | **Native coverage, measured** | The two silver-badge rows in Roadmap.md: the C++ server has never been measured. A coverage build on the bench, the number published. |
| ⬜ | **The bench, doubled** | One Windows bench runs one gate at a time, about ninety minutes each, and the queue of 14 September was six deep. The Ubuntu VM as a second bench for the Linux suite, in parallel. |
| ⬜ | **A fuzzing job that never stops** | The release step fuzzes once; a nightly job over the parsers (MIME, IMAP, SMTP, Sieve, iCalendar when it exists) with the corpus kept. |

## 7. What the deep dive of 14 September found missing

Each of these was checked against Roadmap.md before it was written here: none has a row that ships it, and several have a row that says, in so many words, that it does not exist. Grouped by who feels the gap first.

### The administrator

| | Item | Detail |
|---|---|---|
| ⬜ | **Alerts** | Nothing tells an administrator anything unless they read a log or a metric. A queue that stalls, a disk that fills, a certificate a week from expiry, an auto-ban storm, a minidump, a backup that failed: each becomes a message to the administrator's address and a webhook, with a daily digest as the quiet default. The disk-space and work-queue tasks already know; they only log. |
| ⬜ | **Outbound webhooks** | The roadmap row says it plainly: no configurable webhook exists, only an event script an administrator writes by hand. A configured URL per event - message received, delivered, bounced, quarantined, account locked - with a signed body and retries. |
| ⬜ | **An audit trail** | Every administrative change - who, what, when, from where, over COM, REST or a page - in an append-only, hash-chained log the Deck shows. Exchange has it; a regulated customer asks for it first. |
| ⬜ | **Declarative configuration** | Export the whole configuration - settings, domains, accounts, rules, routes, certificates, listeners - as one file; apply one with a diff shown first; a GitOps loop that applies a repository on change. Stalwart's file and Postfix's main.cf are what people compare against, and the REST write surface is most of the work already done. |
| ⬜ | **The reports our own domains receive, read** | The server sends DMARC and TLS-RPT reports; it does not read the ones that come back for its own domains. A mailbox address per domain, the aggregate reports parsed, and a Deck page that charts who sends as the domain, with what result, the way dmarcian does for a fee. |
| ⬜ | **Own-reputation watch** | The server's outbound addresses checked against the major DNSBLs once a day, and an alert when one lists them - the first thing an administrator learns otherwise is a bounce. |
| ⬜ | **Compromised-account detection** | Per-account sending limits exist. What is missing is the shape of an account takeover: a sudden change in volume, recipients, hour or client address, which locks the account, keeps the mail in the queue for review and alerts. |
| ⬜ | **Outbound content policy (DLP)** | Rules on what leaves: card and account numbers by pattern, attachment types, size, an external recipient on a message marked internal - block, quarantine for review or warn the sender. Exchange has it; no open server does it well. |
| ⬜ | **Quarantine digests for users** | A daily message to each account whose quarantine holds something new, with one-click release from the webmail's held-mail page. |
| ⬜ | **PST import** | Every migration from Exchange and Outlook has a PST somewhere. A reader for the PST format in the import tool, so a migration is drag, wait, done. |
| ⬜ | **Event scripts on Linux** | The Windows build hosts VBScript and JScript through Active Scripting; the Linux build has no engine, and the roadmap's Linux row skips every scripting test for it. One cross-platform engine - JavaScript through an embeddable interpreter - for the same events on both, with the Windows engines kept. |
| ⬜ | **Passkeys and single sign-on** | Passkeys (WebAuthn) for the webmail and Deck sign-in with TOTP as the fallback; OIDC sign-in so an organisation's identity provider is the front door. The server already validates bearer tokens for SMTP and IMAP; the pages should take the same identity. |
| ⬜ | **A password checked against known breaches** | At every password change, the k-anonymity check against the breach corpus (five characters of the hash leave the server, nothing else), refused when found. |
| ⬜ | **Spam traps** | Addresses that were never real; a message to one auto-bans the sender and trains the score. |

### The organisation

| | Item | Detail |
|---|---|---|
| ⬜ | **Active-active nodes** | The warm-standby topology is documented and tested; what remains is two or more nodes serving the same domains at once - shared database, shared or replicated message store, queue ownership by node, sessions on any node - so a node can be taken down without a failover. |
| ⬜ | **Object storage for the message store** | Messages on S3-compatible or Azure Blob storage with the database as the index, which is how a store grows past one disk and how the active-active row shares a store. |
| ⬜ | **Mailbox encryption at rest** | A key per account, the message files encrypted with it, the key unlocked by the password at sign-in and by a recovery key held by the administrator - Dovecot's mail-crypt shape. The search index has to be built with the same care or it leaks what the files hide. |
| ⬜ | **Storage compression and attachment de-duplication** | Messages compressed on the way to disk and the same attachment stored once, measured on a real mailbox before it is claimed. |
| ⬜ | **A list manager** | Distribution lists with moderation exist. A list a person subscribes to by mail and by page, with an archive, digests, per-member delivery settings and the List-* headers filled in, is what replaces Mailman for a small organisation. |
| ⬜ | **Message recall within the server** | An unread message sent to accounts on this server can be withdrawn by its sender, which Outlook does within Exchange and nothing does across the internet. |

### The person reading mail

| | Item | Detail |
|---|---|---|
| ⬜ | **Web Push** | Notifications while the page is open exist. Web Push through the browser's push service, with the server holding the subscriptions, means new mail is announced when the installed app is closed, which is the difference between a page and an app. |
| ⬜ | **Saved searches as folders** | Outlook's search folders: a search saved with a name, shown in the folder list, always current. |
| ⬜ | **Manage subscriptions** | One page listing every sender that offers an unsubscribe, with how much it sends and one button per row - Gmail's, and the reason most people go looking for it. |
| ⬜ | **Brand logos** | BIMI on receipt: the logo a sender publishes, verified, as the avatar in the list, which Gmail and Outlook.com both show. |
| ✅ | **Contacts in and out** | **Done 15 September 2026.** The Contacts page exports the address book as vCard 3.0 (one card per contact: FN, N, EMAIL) or as CSV with the `Name,E-mail Address` header Outlook reads, and imports either: a vCard file of any number of cards (folded lines, escapes, several EMAILs on one card, quoted-printable cards skipped) or a CSV read by its own header row - Google Contacts' `Name` and `E-mail 1 - Value`, Outlook's `First Name`, `Last Name` and `E-mail Address`, or a plain `name,email` - adding what is not there yet by address and saying how many came in. The wiki's client page walks the Google and Outlook exports. Five harness checks. |
| ⬜ | **Snooze, scheduled send and reminders on the phone** | The same features exist; whether they are reachable and readable on a phone is what the UX audit in section 3 measures. |

### The project

| | Item | Detail |
|---|---|---|
| ⬜ | **Published numbers** | Messages per second in and out, IMAP sessions per core, memory per session, on named hardware, against Postfix and Dovecot on the same machine, with the load generator in the repository so anyone can repeat it. The claim in this file's first line is not one until this row is done. |
| ⬜ | **Windows on ARM** | The Linux port builds for AArch64; the Windows build does not. Visual Studio's ARM64 tools and the same installer. |
| ⬜ | **macOS** | The POSIX port should build on macOS with little work; a Homebrew formula makes it a developer's local server. |
| ⬜ | **A Helm chart and a compose file** | The container image exists; the two files people expect beside it, with the probes already served. |

## 8. Declined in the deep dive, with reasons

| | Item | Why not |
|---|---|---|
| ⏸️ | **Apple push for iOS Mail** | Dovecot's XAPPLEPUSHSERVICE needs a certificate only Apple's own server program is issued; without it the extension is a promise the server cannot keep. IMAP IDLE and the installed webmail's Web Push are the honest answers. |
| ⏸️ | **Publishing BIMI for our domains** | It is DNS and a Verified Mark Certificate that costs a sender money each year; the server can document the record but there is nothing to ship. |
| ⏸️ | **A Bayesian classifier of our own** | Deferred in Roadmap.md and still right: rspamd and SpamAssassin are integrated and learn; a second learner in the server would be worse than both. |

## 9. End-to-end encryption

Transport is done: TLS everywhere, DANE, MTA-STS, a post-quantum key exchange, and S/MIME in the page since 11 September. What is not done is mail that stays unreadable to the server itself, and to anyone who takes the server. Proton and Tuta built businesses on that; theirs is proprietary in the sense that only their client reads their format. This server's version is built on OpenPGP, so that any client with a key reads the mail and no one is locked in - the standard is the feature. Roadmap.md deferred OpenPGP in the browser on 11 September as "the S/MIME page's design on another format"; it is un-deferred here, as the first row of the four.

| | Item | Detail |
|---|---|---|
| ⬜ | **OpenPGP in the page** | The S/MIME module's shape - keys made or imported in the browser, kept encrypted under a passphrase, unlocked for a session - for OpenPGP: sign, encrypt, decrypt, verify, with the lock and the signature badge the S/MIME messages already show; a key page with fingerprints, expiry and revocation; Autocrypt headers on outgoing mail so a correspondent's client learns the key, and Autocrypt read on incoming; keys of correspondents looked up by WKD and, where a domain has none, by a keyserver. Interoperates with Thunderbird, GnuPG, Mailvelope, Proton and every client that speaks the standard. |
| ⬜ | **The server publishes its users' keys** | Web Key Directory for every domain the server hosts: `/.well-known/openpgpkey/` on the web-services listener, advanced method, so that any client on the internet finds a user's key from the address alone. The keys are the ones made in the page or uploaded by an administrator for a client that keeps its own. |
| ⬜ | **Zero-access mailboxes** | Opt-in per account, the Proton model: after the filters have run - spam, virus, rules, which need the text - a delivered message is encrypted to the account's public key and the plaintext never touches the disk; the private key is encrypted under a key derived from the password with Argon2id and unlocked in the browser, and a recovery key the user writes down is the only other way in. The administrator cannot read the mail, cannot search it, and cannot recover it. Written down as the trade-offs it is: search runs over the client's own index or the headers the server kept; IMAP and POP see ciphertext, so an account with this on is a webmail account until a local bridge exists; a forgotten password without the recovery key is the mail lost, and the page says so before it is switched on. |
| ⬜ | **A password-protected message to anyone** | For the correspondent with no key: the sender sets a password, the page encrypts the body and attachments (OpenPGP, symmetric) and sends a short message with a link to a reading page on this server; the recipient enters the password there and the browser decrypts; a reply written on that page goes back the same way. End to end, because the plaintext is never in the message that crosses the internet and never on this server; unlike "confidential mode", it promises only what it can keep. |
| ⬜ | **Trust made visible** | A correspondent's key is remembered the first time it is seen and a change is a warning in the reader, not a silent switch - the one thing a man in the middle needs is silence. Fingerprints shown, verifiable by voice or in person, and a green mark on a verified correspondent from then on. |

## 10. The second sweep: product by product, feature by feature

Gmail and Google Workspace, Outlook.com and Microsoft 365 with Exchange behind it, Proton Mail, Fastmail, Tuta, Zoho, Zimbra, Stalwart, mailcow and iRedMail, MDaemon, Kerio and Axigen, each walked through its own feature list and settings pages, and every item below checked against Roadmap.md and the page's own script before it was written down. Where a name is given in brackets it is the product that people know the feature from. Sorted by who feels the gap.

### Normal users: reading and writing mail

| | Item | Detail |
|---|---|---|
| ✅ | **Attachment reminder** (Gmail, Outlook) | **Landed 14 September (#259).** "attached", "see the attachment", "enclosed" in the body and nothing attached: one question before it sends. The compose form has the words in front of it already; it only has to read them. |
| ⬜ | **@mentions in the body** (Outlook) | Typing `@` opens the contact completion; the person chosen is added to To and their name is a link in the message; a message that mentions the reader shows an @ in the list. |
| ✅ | **Shift-click and Ctrl-click in the list** (every desktop client) | **Landed 14 September (#259).** The list has one way to select several rows - the boxes, one at a time. Shift-click for a range, Ctrl-click to add one, as the folder page and every file manager does. Checked: the script has no shift-key handling at all. |
| ✅ | **Nudges** (Gmail) | **Landed 14 September (#262):** "Received N days ago. Reply?" from the listing's own fields, and "Sent N days ago. Follow up?" on a sent message nothing answered, asked of the server once per Sent view through the new `in_reply_to:` operator; a `nudges` preference turns them off. The row as written: "Sent 3 days ago, no reply" on a message the reader sent that got none, and "received 3 days ago, not answered" on one that asked a question - a heuristic on dates and the answered flag, no learning. |
| ✅ | **Follow-up flags with a date, and a reminder** (Outlook) | **Landed 14 September (#262):** the star plus `$FollowUp` and a `$Due-YYYY-MM-DD` keyword on the message itself (the flags route takes any IMAP atom, so other clients still see a flag and a move keeps the date), today, tomorrow, next week or a date from the toolbar, a due badge on the row, the Starred view sorted soonest first, and a reminder once a day at sign-in and when the day turns. The row as written: A flag is on or off today. A flag with a due date, a Flagged view sorted by it, and a notification when it comes due; the date is the `$FollowUp` keyword plus a date the page keeps, so other clients still see a flag. |
| ✅ | **Quick steps** (Outlook) | **Landed 14 September (#262):** defined on a Settings card and kept in the preferences (`qs.<slug>`), as buttons over the list and on the digit keys 1 to 9; move, mark read, label, forward, in one press. The row as written: One button the reader defines: move to a folder, mark read, label, forward to an address, in one press, with a keyboard shortcut of its own. |
| ✅ | **Clean up conversation** (Outlook) | **Landed 14 September (#262):** from the row's menu and the message's, deleting the messages whose every line is quoted in a later message of the conversation - the newest, the unread, the starred and those with attachments kept - with Undo. The row as written: Delete the messages of a conversation whose whole text is quoted in a later one, so a thread of twenty is the three that said something. |
| ✅ | **Message pop-out** (Outlook, Gmail) | **Landed 14 September (#259).** Open a message, or the compose form, in its own window, so a person can read one while writing another. |
| ✅ | **Search operators the two have and this one lacks** | **All thirteen landed 14 September** - `cc:`, `bcc:`, `filename:`, `larger:`, `smaller:`, `older_than:`, `newer_than:`, `is:muted`, `is:pinned`, `category:`, `-word` and `OR` (#259), then `has:link` (#262: an `http` or `https` address in the text, or an `href` in the HTML, so a remote image alone is not a link) - each with a test; and `in_reply_to:<message-id>` besides, which the nudges ask. The row as written: `cc:`, `bcc:`, `filename:`, `larger:10M`, `smaller:`, `older_than:7d`, `newer_than:`, `is:muted`, `is:pinned`, `category:promotions`, `has:link`, `-word` to exclude, `OR`. The operators today: from, to, subject, has:attachment, before, after, in, is:unread/read/flagged/unflagged/answered, label. Each new one is a line in the server's search and a line in the options panel. |
| ✅ | **Search history and suggestions** (Gmail) | **Landed 14 September** - the history (#259), then a contact's name, or `from:`/`to:` and a name, completing to `from:<address>` under the box from the address book (#262). The row as written: The last searches offered under the box as it is focused, and a contact's name completing to `from:` as it is typed. |
| ⬜ | **Saved searches** | Section 7. |
| ⬜ | **Recover deleted items** (Outlook) | **The premise as first written was wrong, checked 14 September:** the IMAP expunge retention task caps `hm_imapexpunged` - folder, UID and mod-sequence, for QRESYNC - and an expunge deletes the file; nothing retains a message. So this is a store first: a retention window (a setting, default off) under which an expunged message's row and file move to a recoverable state instead of going, a sweep that ends them after the window, a `/api/v1/me` pair to list and restore, and then the Recover page. A schema change, and the IMAP expunge path; sized as a wave of its own. |
| ⬜ | **Auto-delete and auto-archive the reader chooses** (Proton, Outlook) | "Delete Trash and Junk after 30 days", "archive Inbox mail older than a year" as the reader's own settings, run by the server's retention task; today retention is the administrator's, per domain and account. |
| ⬜ | **Masked addresses** (Fastmail's Masked Email, Proton's hide-my-email, Apple's Hide My Email) | An alias the reader makes in a click for one shop or one sign-up, named for it, forwarding to the mailbox, with a reply from the mailbox rewritten to come from the alias, and a switch that turns it off the day it starts to receive spam. The server's aliases are the administrator's; these are the reader's own, bounded per account. |
| ⬜ | **Other accounts, fetched** (Gmail's "check mail from other accounts") | The server fetches external POP3 and IMAP accounts for an account; only an administrator can set one up. A page in the webmail where the reader adds their old provider's account and sees its mail arrive here. |
| ⬜ | **Send-as an address that is verified** (Gmail) | An identity is what an administrator gave. A reader should be able to add an address they own elsewhere, prove it with a code sent to it, and send as it from then on. |
| ⬜ | **Signatures with images and layout** (Outlook, Gmail) | The signature is text with formatting; a logo, a card layout and a per-identity signature with an image are what every business signature has. |
| ⬜ | **Trackers, counted** (Proton, Apple Mail) | Remote images are blocked; the page does not say how many, or which senders track. "3 trackers blocked" on the message, and a sender's habit shown on their avatar. |
| ⬜ | **Report phishing** (Gmail, Outlook) | Beside Junk: a report that files the message, sends its headers to the administrator's abuse address, blocks the sender, and adds the URL to the SURBL-style local list, so the next reader is warned. |
| ⬜ | **Safe senders** (Outlook) | A reader's own allow list - never junk from this address or domain - fed to the spam filter, beside the block list that exists. |
| ⬜ | **Conditional formatting of the list** (Outlook) | Rows coloured by a rule: mail from the boss in red, from a list in grey. The labels have colours; this is a rule on the row. |
| ⬜ | **Multiple stars** (Gmail) | The star is one. Gmail's set of star and flag icons, cycled by pressing the star again, kept as `$Star2`-style keywords other clients ignore. |
| ⬜ | **Snooze presets and custom swipe actions** (Gmail, Proton) | The snooze times and the two swipe actions as settings. |
| ⬜ | **A mailbox export** (Google Takeout, Proton's export) | The reader downloads their whole mailbox as mbox and their contacts as vCard, from Settings, with no administrator involved - data portability as the law and decency require. |
| ⬜ | **Import from the old provider** (Proton's Easy Switch, Fastmail's import) | The reader's side of the migration: sign in to Gmail or Outlook.com over IMAP (OAuth for the two), pick folders, and watch them arrive - the server's IMAP mirror, driven from the page for one account. |
| ⬜ | **Right-to-left languages** | Twenty catalogues, none right-to-left. Arabic, Hebrew and Persian, with the layout mirrored, which is a stylesheet's `dir` and a day of checking every view. |
| ⬜ | **The reader's own time zone** | Dates are shown in the browser's zone; a reader travelling, or reading from a server on another continent, sets the zone once and every date, rule and scheduled send uses it. |
| ⬜ | **Undo more** | Undo covers filing and sending. Undo for a label removed, a rule saved, a contact deleted, a sender blocked. |

### Normal users: the phone

| | Item | Detail |
|---|---|---|
| ✅ | **The app badge** (every mail app) | **Done 15 September 2026.** `navigator.setAppBadge` with the same total the title carries - the folders the reader asked to be told about - on every count the page reads, cleared at zero; nothing without the API. |
| ✅ | **Share to the webmail** (every mail app) | **Done 15 September 2026.** The manifest declares a share target (`POST /portal/share`, multipart: title, text, url, files - images, PDFs, text, calendars, `.eml`); the service worker answers the POST by putting what was shared into a cache of its own and sends the page to a new message, which reads the cache once, fills the subject and the text, attaches the files as if dropped, and deletes it. |
| ✅ | **The mailto: handler** | **Done 15 September 2026.** Settings offers *Open mailto: links here*, which calls `registerProtocolHandler` for `/portal#/compose?mailto=%s` (the manifest declares the same); the compose route parses the mailto: URL - addresses, subject, body, cc, bcc - into a new message. |
| ⬜ | **Offline for the last thirty days** (Gmail offline) | Offline holds the last listing and an outbox. The last thirty days of bodies and attachments kept in the browser's storage, searched offline, bounded by a setting. |
| ⬜ | **Web Push** | Section 7. |

### Normal users: contacts and the organisation

| | Item | Detail |
|---|---|---|
| ⬜ | **The organisation's directory** (Outlook's Global Address List, Google's directory) | Every account and list of the domain in the completion and on a directory page, with a photo, a title and a phone number the administrator or the person sets, and an opt-out per account. Today the completion knows only the reader's own contacts. |
| ⬜ | **Collected addresses** (Gmail's "Other contacts", Outlook's suggested recipients) | Everyone the reader has written to, offered in completion without being a contact, promoted to one in a click. |
| ⬜ | **Contact photos, birthdays, notes and custom fields** | The contact is name and addresses. A photo (shown as the avatar), a birthday (shown on the day), notes and the vCard's other fields, in the page and over CardDAV. |
| ⬜ | **Shared address books** (Zimbra, Kerio) | An address book shared by a team, read-only or writable, over CardDAV too. |

### Administrators: day to day

| | Item | Detail |
|---|---|---|
| ⬜ | **Accounts from a spreadsheet** (every hosted admin console) | A CSV of addresses, names and passwords imported into a domain, with a dry run that lists what would be made, and the same file exported. The import tool reads mbox and text; it does not read a list of accounts. |
| ⬜ | **Reports** (Google Workspace, Microsoft 365, Axigen) | Per domain and per day: messages in and out, spam and virus counts, top senders and recipients, largest mailboxes, storage growth, delivery failures by reason - the metrics history is kept; a page that reads it is not. |
| ⬜ | **Follow one connection live** (Stalwart's tracing) | A session id in the Deck, and every line the server logs for it as it happens - the debugging that a grep through a gigabyte of log stands in for today. |
| ⬜ | **syslog** (every Linux administrator's first question) | RFC 5424 to a UDP or TLS collector beside the JSON files and the OTLP exporter; on Linux, the journal. |
| ⬜ | **Country blocking** (MDaemon's location screening, every firewall) | Connections and sign-ins by country, from a GeoIP database the administrator supplies, allowed and denied per listener, and the country on every sign-in line and session. |
| ⬜ | **`winget install hMailServer`** (and Chocolatey) | The manifest in the winget repository, updated by the release workflow, so the installer is one command and the update is `winget upgrade`. |
| ⬜ | **A PowerShell module and a Linux command-line client** | `hmconfig.ps1`'s successor: `Get-HmDomain`, `New-HmAccount`, `Set-HmSetting` over the REST API, with completion; and `hmctl` on Linux, the same verbs. The API has the routes; the shells do not have the words. |
| ⬜ | **Restore one mailbox, or one message, from a backup** (every hosted service) | The backup restores the whole server. An administrator restoring one person's mailbox as it was last Tuesday, or one deleted message, from the Deck. |
| ⬜ | **Mailbox rename and move** | An address renamed with the old one kept as an alias and mail redirected; a mailbox moved between domains on the same server. |

### Enterprise: policy, compliance and identity

| | Item | Detail |
|---|---|---|
| ⬜ | **External-sender tagging and first-contact tips** (Microsoft 365, Google) | "[EXTERNAL]" in the subject or a banner in the reader on mail from outside the organisation; "you don't usually get mail from this sender" on a first contact; both per domain, both off by default. The page already warns about links; this is the sender. |
| ⬜ | **Impersonation protection** (Microsoft 365's anti-phishing policies) | A display name matching an account or a protected person on mail from outside; a sender domain one edit from ours or from a protected partner (homoglyphs included); quarantined or banner-flagged, per domain. |
| ⬜ | **Mail flow rules** (Exchange transport rules, Google's routing) | Conditions on sender, recipient, headers, size, attachment types, words, and actions: add a disclaimer, stamp a header, redirect, BCC to an address, reject with a text, require TLS, quarantine. The rules today act for an account; these act for the server and the domain, before delivery. |
| ⬜ | **A domain disclaimer** (every business) | The one mail flow rule everyone asks for first: a legal footer, text and HTML, appended to outbound mail per domain, once, not on every reply. |
| ⬜ | **Journaling and dual delivery** (Exchange journaling, Google's dual delivery) | A copy of every message of a domain, or of chosen accounts, to an external address or a second system - compliance archives and migrations both want it. The archive on disk exists; the copy to elsewhere does not. |
| ⬜ | **Remote-domain settings** (Exchange) | Per remote domain: whether automatic replies and forwards may go there, the message format, the TLS required. |
| ⬜ | **Restricted delivery** (Google Workspace's restrict delivery) | Accounts, or a whole domain, that may only exchange mail with named domains - schools and regulated desks. |
| ⬜ | **Allow and block lists for the server** (Microsoft 365's tenant allow/block list) | Senders, domains and URLs allowed or blocked for every account, with an expiry on each entry and the reason kept. |
| ⬜ | **eDiscovery** (Google Vault, Microsoft Purview) | Search across every mailbox and the archive by person, date, words and attachment, the results held or exported as EML or PST with a manifest, and every search logged - the legal-hold rows have the hold; this is the finding. |
| ⬜ | **Roles** (every admin console) | Beyond the one administrator and the domain-scoped API key: a help-desk role that resets passwords and nothing else, a read-only auditor, a domain administrator who signs in to the Deck for their domain. |
| ⬜ | **SCIM provisioning** (Entra ID, Okta, Google) | Accounts created, renamed, suspended and removed by the organisation's identity provider over SCIM 2.0, with the aliases and the lists. The LDAP backend authenticates; SCIM provisions. |
| ⬜ | **SAML, beside OIDC** | The identity providers that still speak only SAML, for the webmail and the Deck sign-in. |
| ⬜ | **Delegated mailboxes announced to Outlook** (Exchange automapping) | A mailbox an account is delegated to appears in Outlook by itself through Autodiscover, as it does with Exchange. |
| ⬜ | **An organisation CA for S/MIME** (Exchange with AD CS) | Certificates issued to every account by the server's own CA and published for lookup, so S/MIME works inside the organisation without anyone buying a certificate. |
| ⬜ | **Gateway encryption to partner domains** | Mail to a named partner domain encrypted at the server with the partner's S/MIME or OpenPGP key, whatever the sender's client - the enterprise shape of the end-to-end section. |
| ⬜ | **A user's data, exported** (GDPR article 20) | Everything the server holds on one person - mail, contacts, settings, logs that name them - as one archive, from the Deck, with the erasure that follows it. |
| ✅ | **`security.txt`** (RFC 9116) | **Done 15 September 2026.** The server half already existed: the web-services listener serves `/.well-known/security.txt` (and the legacy `/security.txt`) for every hosted domain with a postmaster, naming that domain's own contact - the right file for an operator's site, since a report about their server goes to them, not to this project. The repository half is `.well-known/security.txt` at the root: the GitHub Security Advisories address as the contact, the policy `.github/SECURITY.md`, a canonical URL and a one-year expiry, which is what the badge and the scanners look for. |
| ⬜ | **Session policies** | Idle timeout, absolute lifetime, one session per device, and a session bound to the network it began on, as settings; a new sign-in from a new device announced to the recovery address. |

### Deliverability and the mail path

| | Item | Detail |
|---|---|---|
| ⬜ | **IP warm-up** (every sending service) | A schedule for a new outbound address: so many messages a day to each large provider, rising over weeks, enforced by the queue, with the counts on the Deck. |
| ⬜ | **Mandatory TLS per domain** (mailcow's TLS policy map, Exchange connectors) | Outbound mail to a named domain refused rather than sent in the clear when TLS cannot be had or the certificate does not verify; inbound from a named domain likewise - the row that makes REQUIRETLS an administrator's policy rather than a sender's request. |
| ⬜ | **Recipient callout for backup MX** | A backup MX that asks the primary whether an address exists before accepting, so it does not become a backscatter source when the primary is down. |
| ⬜ | **Bounce handling for lists** | Section 7's list manager: VERP addresses, hard bounces unsubscribing, soft bounces counted. |

### Recovery and account safety, for the person

| | Item | Detail |
|---|---|---|
| ⬜ | **Recovery address and codes** (every provider) | A recovery email address and a set of one-time recovery codes, set in the webmail, so a forgotten password or a lost authenticator is a self-service reset and not a ticket. Today only an administrator can reset either. |
| ⬜ | **A breach check on the password** | Section 7. |
| ⬜ | **Passkeys** | Section 7. |
