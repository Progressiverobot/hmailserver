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
