# Roadmap 2: the programme after 6.3.2

**Written 14 September 2026.** [Roadmap.md](Roadmap.md) is the record: 864 rows done, a handful open, three dozen deferred with their reasons. It has grown into the history of the project, and it is kept because every row in it is a claim that was checked. This file is the plan: what still stands between this server and the best mail server there is, held against what the people who run Gmail, Outlook.com, Exchange, Dovecot and Stalwart offer, and worked through in the order below. A row moves out of here into Roadmap.md when it is done, with the measure that says so.

The legend is Roadmap.md's: ✅ done and verified, 🔄 underway, ⬜ not started, ⏸️ deferred with the reason written down.

## 1. Landing what was built on 13 and 14 September

| | Item | Where it stands |
|---|---|---|
| ✅ | **Linux suite waves A to F** (PR #251) | Merged 14 September, 02:46. Windows 2,233 / 2,225 passed / 0 failed / 8 skipped; Linux 1,457 / 1,185 passed / 0 failed / 272 skipped. Schema 6039: a domain's relay password could not be saved on any Windows installation, found by the first honest gate of the branch. |
| 🔄 | **One header on every source file** (#256) | Gated green; pushed to master by the direct-push flow as soon as its checks finish. |
| 🔄 | **The tidy of the two Copilot drafts, and the two defects its own gates found** (#255) | Gated green at the third attempt. The first gate died in `generate-com-wrapper.ps1` under Windows PowerShell (MIDL's stderr as a terminating error); the second on a recordset walk that a COM error could end with a minidump - both fixed on the branch. |
| 🔄 | **The live update from an open Control Panel** (#258) | In its gate. The first real update, 6.3.1 to 6.3.2, failed twice with Inno's exit code 5 because the Control Panel that started it kept its own files open. The helper now ends what runs from under the installation, the Control Panel closes itself, and a failed run quotes the installer's log. |
| ⬜ | **Linux suite waves G and H** (#254), **CardDAV** (#253), **the webmail rebuilt** (#257) | Gated one after another, then pushed in order. |
| ⬜ | **6.3.3** | The release that carries all of the above, with notes and the wiki. It has to say that an installation on 6.3.1 or 6.3.2 must be updated by hand once, because the helper that runs a live update is the one already installed. |

## 2. The Linux control panel: the browser Control Deck to parity

The desktop Control Panel is WPF and runs on Windows only; that will not change, and it should not - it is the best-in-class Windows administration program this server has. On Linux the administration surface is the browser Control Deck served at `/WebAdmin`, the REST API (81 routes, 40 of them administrative), and `hmailserver --set-admin-password`, `--create-database`, `--upgrade-database`. The Deck today has ten views - dashboard, domains, delivery queue, logs, rules, routes, certificates, ports, TLSA, settings - which is far more than Roadmap.md's row on it still says, and far less than the desktop program. Parity is the item.

| | Item | Detail |
|---|---|---|
| ⬜ | **A census: the desktop Control Panel's every page and field against the REST API and the Deck** | A script under `build/`, run in CI like the settings-index check, that lists each COM property and Control Panel control and whether a REST route writes it and a Deck view shows it. The number it prints is this section's measure; it reaches zero when the section is done. |
| ⬜ | **The REST write routes the census finds missing** | Each validated by the code that owns the value, as the settings, rules, routes, aliases, accounts, certificates and listeners already are. Likely: distribution-list members and moderation, per-account rules and vacation, DKIM keys per domain, IP ranges' every field, backup and restore, archive holds, quarantine release, API-key scopes, the anti-spam and anti-virus settings in full. |
| ⬜ | **The Deck rebuilt as one page the way the webmail was** | The same design tokens, dark theme and keyboard as the webmail; a harness that runs its script like `build/portal-script-test.js`; served from the binary so the file is the file checked. Views for everything the desktop program has: accounts in full (forwarding, vacation, signature, rules, quota, two-factor and application passwords, directory link), aliases and lists, per-domain DKIM/DMARC/MTA-STS/TLS-RPT, IP ranges, auto-ban and lockout, spam and virus filtering, backup and restore, archive and legal holds, quarantine, API keys, metrics history, the update page, diagnostics (MX, SRV, DANE, DNSSEC, TLSA probes), live logs, external setup. |
| ⬜ | **The Deck in the seventeen languages** | The same catalogues and checks the Control Panel and the webmail have. |
| ⬜ | **The Linux package's administration story written down** | An administrator who installs the .deb, .rpm or AppImage should reach a configured server without reading source: the README's Linux section walks it, and the Deck is the tool it names. |

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

| | Item | Detail |
|---|---|---|
| ⬜ | **The .deb pins the builder's Boost** | It depends on `libboost-regex1.83.0-icu74` exactly and will not install on Ubuntu 26.04; the AppImage is portable. Static Boost, or a per-distribution build matrix. |
| ⬜ | **The service host: systemd, and a live update on Linux** | The update helper is a Windows program; on Linux the apply step is a package upgrade or an AppImage swap under a systemd unit that restarts the server, with the same outcome file and the same rollback. |
| ⬜ | **The Linux suite's remaining skips** | The message object, distribution lists, the anti-virus settings, application passwords and folder ACLs over REST, each with the shim taught to use it and the skip count re-measured. |

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
| ⬜ | **Contacts in and out** | vCard and CSV import and export in the address book, and a Google and Outlook contacts export walked through in the wiki. |
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
