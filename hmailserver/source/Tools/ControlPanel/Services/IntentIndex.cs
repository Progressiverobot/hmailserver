// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>One thing an administrator came here to achieve, and where it is done.</summary>
   public sealed class IntentEntry
   {
      public IntentEntry(string phrase, string page, string answer)
      {
         Phrase = phrase;
         Page = page;
         Answer = answer;
      }

      /// <summary>What the administrator would type or say. Never a setting name.</summary>
      public string Phrase { get; }

      /// <summary>The navigation key of the page where it is done.</summary>
      public string Page { get; }

      /// <summary>
      /// What they will do when they get there. Displayed under the phrase in
      /// the palette, so that picking a result is an informed choice rather than
      /// a guess, and so that a wrong guess by us is visibly wrong before the
      /// user has navigated anywhere.
      /// </summary>
      public string Answer { get; }
   }

   /// <summary>
   /// Outcome phrases: the bridge between what an administrator wants and what
   /// we called it.
   ///
   /// The settings index answers "where is the setting called X". This answers
   /// the far more common question, "I need to stop spam / block an address /
   /// let the office printer send mail - where do I go", which name-based search
   /// cannot answer at all. Typing "stop spam" into the old palette returned
   /// nothing: no page and no setting contains the word "stop", so the one thing
   /// most people arrive wanting to do was unsearchable.
   ///
   /// Written as full phrases rather than as keyword tags on each page because
   /// the phrase is what gets shown: a result reading "stop spam - Anti-spam
   /// settings" teaches where the subject lives, whereas a page row that
   /// happened to match a hidden tag teaches nothing. The phrases are matched
   /// word-wise with synonym folding (see <see cref="SearchTerms"/>), so each
   /// one covers its obvious rewordings - "mail is stuck" reaches "why is mail
   /// queued" - and only genuinely different phrasings need their own row.
   ///
   /// Adding to this table is the cheapest possible fix for "I could not find
   /// X". Every entry is checked by PaletteSearchTests: the page must exist, the
   /// phrase must be unique, and typing the phrase must actually land on the
   /// page - so a well-meaning addition cannot quietly fail to work.
   /// </summary>
   public static class IntentIndex
   {
      /// <summary>Every outcome phrase the palette understands.</summary>
      public static readonly IReadOnlyList<IntentEntry> Entries = new List<IntentEntry>
      {
         // ---- Mail is not moving -------------------------------------------
         // The single most common reason anyone opens this application. Each of
         // these lands on the page that shows evidence, not on a settings page:
         // changing settings before reading the queue is how outages get longer.
         new IntentEntry(N("why is mail queued"), "queue",
            N("See every message still waiting, the last error the server got, and when it will be retried.")),
         new IntentEntry(N("mail is stuck"), "queue",
            N("The delivery queue lists what is waiting and why.")),
         new IntentEntry(N("mail is not being delivered"), "queue",
            N("Start at the delivery queue: if the message is there, the reason it has not left is there too.")),
         new IntentEntry(N("outgoing mail is not sending"), "queue",
            N("Check the queue for the failure the remote server reported.")),
         // The queue says that a message is waiting; when the reason is not on that
         // page, the diagnosis path is - which half has stalled and which log line
         // names the cause.
         new IntentEntry(N("mail is stalled"), "stalledmail",
            N("Work out whether accepting or delivering has stalled, turn on the logging that names the cause, and read the lines that do.")),
         new IntentEntry(N("sender times out after sending a message"), "stalledmail",
            N("That is the accept pipeline stalling after the 354: the stage timings in the debug log say which stage.")),
         new IntentEntry(N("accepted but never delivered"), "stalledmail",
            N("That is the delivery half: a wedged scanner, a slow remote server, a hung external tool or the database.")),
         new IntentEntry(N("diagnose slow mail"), "stalledmail",
            N("The guided path: which half, which log lines, and the setting that bounds each stage.")),
         new IntentEntry(N("messages are not arriving"), "logs",
            N("Watch the live log while the sender retries - an inbound message that never appears was refused or never arrived.")),
         new IntentEntry(N("find out why a message bounced"), "logs",
            N("The live log carries the SMTP conversation, including the text the far end sent back.")),
         new IntentEntry(N("see what the server is doing right now"), "logs",
            N("Stream the log as it is written.")),
         new IntentEntry(N("test whether port 25 is open"), "diagnostics",
            N("Run the built-in checks: outbound port 25, MX resolution, backup folder and IP configuration.")),
         new IntentEntry(N("check if the server is healthy"), "diagnostics",
            N("Run the built-in connectivity and configuration checks.")),
         new IntentEntry(N("where does mail for a domain go"), "mxquery",
            N("Look up the MX records for any domain.")),
         new IntentEntry(N("is anyone connected"), "status",
            N("Server status lists the live sessions for each protocol.")),

         // ---- People and addresses -----------------------------------------
         new IntentEntry(N("add a user"), "domains",
            N("Open the domain, then add the account - accounts live inside their domain.")),
         new IntentEntry(N("add a mailbox"), "domains",
            N("Accounts are added inside the domain that owns the address.")),
         new IntentEntry(N("create an e-mail address"), "domains",
            N("Add an account for a real mailbox, or an alias to point one address at another.")),
         new IntentEntry(N("add a domain"), "domains",
            N("Add the domain first; accounts, aliases and lists all hang off it.")),
         new IntentEntry(N("reset a user's password"), "domains",
            N("Open the domain, open the account, and set a new password.")),
         // "change a user password" and "change a mailbox password" are spelled out
         // rather than left to the partial matcher. Without them both queries scored a
         // tie with "change the administrator password" - two of three tokens each -
         // and lost the tie on alphabetical title order, so asking how to change a
         // mailbox password led with the page that changes the SERVER administration
         // password. Leading an administrator to the wrong password field is worse than
         // returning nothing.
         new IntentEntry(N("change a user password"), "domains",
            N("Open the domain, open the account, and set a new password. This is not the server administration password.")),
         new IntentEntry(N("change a mailbox password"), "domains",
            N("Mailbox passwords are set on the account, inside its domain.")),
         // The destructive half of the lifecycle. Every intent here was add/configure/
         // diagnose, so "add a user" was answerable and "delete a user" returned
         // nothing at all - and the person who cannot find how to remove an account is
         // more likely to leave a live mailbox for a leaver than the one who cannot
         // find how to add one.
         new IntentEntry(N("delete a user"), "domains",
            N("Accounts are deleted inside the domain that owns them; deleting one removes its mail.")),
         new IntentEntry(N("remove a mailbox"), "domains",
            N("Open the domain, select the account, and delete it. Consider an alias if the address must keep receiving.")),
         new IntentEntry(N("disable an account"), "domains",
            N("An account can be switched off without deleting it, which keeps the mail and refuses new logons.")),
         new IntentEntry(N("remove a domain"), "domains",
            N("Deleting a domain deletes the accounts inside it. Check for aliases and lists pointing at it first.")),
         new IntentEntry(N("remove an alias"), "domains",
            N("Aliases are listed under their domain and can be deleted without touching the mailbox behind them.")),
         new IntentEntry(N("delete a mailing list"), "domains",
            N("Distribution lists live in the domain that owns the list address.")),
         new IntentEntry(N("set up a mailing list"), "domains",
            N("Distribution lists are created inside the domain that owns the list address.")),
         new IntentEntry(N("forward one address to another"), "domains",
            N("An alias points one address at another without needing a second mailbox.")),
         new IntentEntry(N("set a mailbox size limit"), "domains",
            N("A quota can be set on the domain and overridden per account.")),
         new IntentEntry(N("collect mail from another provider"), "domains",
            N("External accounts fetch mail from a remote POP3 or IMAP mailbox into a local one.")),
         new IntentEntry(N("sign outgoing mail with dkim"), "domains",
            N("DKIM signing is configured per domain, with the key and selector you publish in DNS.")),
         new IntentEntry(N("share a folder between accounts"), "publicfolders",
            N("Create the public folder, then grant accounts or groups permission on it.")),

         // ---- Spam and viruses ---------------------------------------------
         // The overview first, because "is my spam filtering set up sensibly" is a
         // question the five editing pages could not answer between them: each one
         // shows its own switch and none shows the order, the scores or the two
         // thresholds that decide what the score means.
         new IntentEntry(N("what is my spam configuration"), "spamoverview",
            N("Every check in the order the server runs them, its score, and what the total does to the message.")),
         new IntentEntry(N("which spam checks are running"), "spamoverview",
            N("One list of the nine checks, which are on, and which of the two phases each one runs in.")),
         new IntentEntry(N("why is my spam filter not catching anything"), "spamoverview",
            N("The overview names the arrangements that silently do nothing - a zero threshold, a threshold no enabled check can reach, a check scoring zero.")),
         new IntentEntry(N("stop spam"), "antispam",
            N("Scores and thresholds, SPF, DKIM, DMARC, greylisting and SpamAssassin all live here.")),
         new IntentEntry(N("too much spam is getting through"), "antispam",
            N("Lower the mark and delete thresholds, or turn on more of the checks.")),
         new IntentEntry(N("turn on spamassassin"), "antispam",
            N("Point the server at a SpamAssassin host and choose how its score is merged.")),
         new IntentEntry(N("check spf dkim and dmarc"), "antispam",
            N("Each check can be enabled separately and given its own score.")),
         new IntentEntry(N("legitimate mail is being marked as spam"), "spamwhitelist",
            N("White-list the sender or their IP range so the message bypasses spam protection.")),
         new IntentEntry(N("let a sender through the spam filter"), "spamwhitelist",
            N("Add the address or IP range to the anti-spam white list.")),
         // Phrased around reputation rather than around "block an IP address":
         // that wording collides with the far more common request, which is to
         // refuse one specific address on the IP ranges page, and a query like
         // "ban an ip address" was landing here instead of there.
         new IntentEntry(N("block mail from a bad reputation ip"), "dnsbl",
            N("Add a DNS blacklist; connections from listed addresses gain score or are refused.")),
         new IntentEntry(N("block spam links in message bodies"), "surbl",
            N("SURBL servers are checked against the links inside the message.")),
         new IntentEntry(N("mail from a new sender is delayed"), "greylistwhitelist",
            N("That is greylisting. Exempt the sending address here, or turn greylisting off entirely.")),
         new IntentEntry(N("scan for viruses"), "antivirus",
            N("Enable ClamAV or an external scanner, and choose what happens to an infected message.")),
         new IntentEntry(N("block executable attachments"), "blockedattachments",
            N("Add file-name patterns; matching attachments are stripped whatever a scanner says.")),

         // ---- Who may connect ----------------------------------------------
         new IntentEntry(N("block an ip"), "ipranges",
            N("Add an IP range with everything denied - ranges are matched most specific first.")),
         new IntentEntry(N("let a device send mail"), "ipranges",
            N("Add an IP range for the device that allows SMTP delivery without requiring authentication.")),
         new IntentEntry(N("let the office printer send e-mail"), "ipranges",
            N("Give the printer's address its own IP range with authentication not required.")),
         new IntentEntry(N("require authentication to send mail"), "ipranges",
            N("Turn on the require-authentication options for the range that covers your clients.")),
         new IntentEntry(N("make sure i am not an open relay"), "ipranges",
            N("Check that the internet-wide range does not allow deliveries to external accounts.")),
         // These three used to land on one page called "Auto-ban & SSL/TLS", which
         // meant two of them showed the reader a cipher list they had not asked
         // for and the third showed them a lockout threshold. The page is now two
         // pages and the phrases point at the half each one is about.
         new IntentEntry(N("someone is trying to guess passwords"), "autoban",
            N("Auto-ban locks out an address after repeated logon failures.")),
         new IntentEntry(N("lock out repeated failed logons"), "autoban",
            N("Set the auto-ban threshold and how long the lockout lasts.")),
         // Deliberately phrased with "unban" rather than "unblock": "block" is
         // folded onto by ban/deny/reject and is the word the IP ranges page owns,
         // so an "unblock..." phrase here scored against "block an ip" and the two
         // could tie. "unban" is nobody else's word.
         new IntentEntry(N("unban an address that was locked out"), "autoban",
            N("Raise the threshold here and clear the counted failures. An address already locked out stays out until its \"Auto-ban:\" range expires, or you delete that range on the IP ranges page.")),
         new IntentEntry(N("which tls versions are allowed"), "tls",
            N("The accepted TLS versions and cipher suites are set here.")),
         new IntentEntry(N("change the administrator password"), "adminaccess",
            N("Set a new server administration password.")),
         new IntentEntry(N("use microsoft 365 or google sign-in"), "authentication",
            N("Configure OAuth2 / XOAUTH2 so clients authenticate with a token instead of a password.")),
         new IntentEntry(N("make stored passwords harder to crack"), "authentication",
            N("Choose the password hashing algorithm and its work factor.")),
         // DisableAUTHList is a comma-separated list of ports on the Authentication
         // page. Nothing in its label or its page title contains the word "port",
         // so the one query anybody would type for it found the TCP/IP ports page,
         // which is not where the setting is.
         new IntentEntry(N("do not offer authentication on port 25"), "authentication",
            N("List the local ports where AUTH should not be advertised - typically a port 25 that only takes inbound mail.")),

         // ---- Getting mail in and out --------------------------------------
         new IntentEntry(N("send all mail through my provider"), "delivery",
            N("Set the SMTP relayer, and its credentials if the provider needs them.")),
         new IntentEntry(N("send mail for one domain to a different server"), "routes",
            N("A route overrides MX lookup for named domains.")),
         new IntentEntry(N("retry failed deliveries sooner"), "delivery",
            N("The retry counts and intervals are here, along with what happens when they run out.")),
         new IntentEntry(N("change the bounce message wording"), "servermessages",
            N("The bounce and error texts the server sends are editable templates.")),
         new IntentEntry(N("act on messages as they arrive"), "rules",
            N("Global rules match on the message and take an action - move, tag, reply or refuse.")),
         new IntentEntry(N("tell everyone about maintenance"), "sendout",
            N("Server sendout mails every account, or every account matching a wildcard.")),
         new IntentEntry(N("my spam filter blames my own gateway"), "relays",
            N("Register the gateway as an incoming relay so its address is not treated as the client.")),

         // ---- Ports, clients and TLS ---------------------------------------
         new IntentEntry(N("open the submission port"), "ports",
            N("Add a listener - 587 with STARTTLS, or 465 for implicit TLS.")),
         new IntentEntry(N("make clients use an encrypted connection"), "ports",
            N("Set the connection security on each listener, then require it for authentication.")),
         new IntentEntry(N("install a certificate"), "certs",
            N("Add the certificate and key, then attach it to the ports that should present it.")),
         new IntentEntry(N("my certificate has expired"), "certs",
            N("Replace the certificate here; the expiry date of each one is listed.")),
         new IntentEntry(N("renew certificates automatically"), "acme",
            N("Point the server at an ACME authority such as Let's Encrypt and it will renew on its own.")),
         new IntentEntry(N("prove to other servers that tls is required"), "security",
            N("MTA-STS and DANE publish that commitment; TLS-RPT tells you who failed it.")),
         new IntentEntry(N("set up outlook automatically"), "webservices",
            N("Autodiscover and autoconfig are served by the built-in HTTP listener.")),
         new IntentEntry(N("configure a phone or tablet"), "webservices",
            N("Autoconfiguration hands clients the right host names, ports and security.")),
         new IntentEntry(N("use my own name servers"), "dns",
            N("Set the resolvers the server queries, and whether answers are DNSSEC validated.")),

         // ---- Housekeeping --------------------------------------------------
         new IntentEntry(N("back up the server"), "backup",
            N("Choose what to include and where to write it.")),
         new IntentEntry(N("restore from a backup"), "backup",
            N("Restore configuration, domains and messages from a backup file.")),
         new IntentEntry(N("move to a new server"), "backup",
            N("Back up here and restore on the new machine.")),
         new IntentEntry(N("the server is slow"), "performance",
            N("Thread counts, database connections and caching are the levers.")),
         new IntentEntry(N("logs are filling the disk"), "logging",
            N("Reduce the log level, or set how many days of logs are kept.")),
         new IntentEntry(N("turn on more detailed logging"), "logging",
            N("Raise the log level and choose which log files are written.")),
         // The three phrases below are for capabilities the server has and the
         // interface never advertised in words anybody would type. The log format
         // and the SQL log device were selectable for years while the server
         // ignored them; now that both work, "how do I get my logs into something
         // that reads them" is a question with an answer, and it should be
         // findable in the terms it is asked in.
         new IntentEntry(N("send my logs to a log analyser"), "logging",
            N("Choose the NCSA Common Log Format so an analyser can read the lines, or JSON for a log shipper.")),
         new IntentEntry(N("write the log to a database"), "logging",
            N("Set the log destination to the database; entries fall back to the log files if it is unreachable.")),
         new IntentEntry(N("make awstats work"), "logging",
            N("Turn on the AWStats-compatible log. That is a separate file - the log line format setting does not change it.")),
         new IntentEntry(N("run code when mail arrives"), "scripts",
            N("Event scripts run on delivery, logon and error events.")),
         new IntentEntry(N("keep a copy of every message"), "advanced",
            N("Archiving writes a copy of every message to a folder you choose.")),
         new IntentEntry(N("change a setting with no page"), "hardening",
            N("Server-wide ceilings, durability and abuse controls that belong to no single feature are edited here.")),
         // The six settings below moved to the page that owns their feature, which
         // is where an administrator will now find them by browsing. These phrases
         // cover the other half of the problem: knowing the symptom but not the
         // subsystem. "IMAP search" is not a phrase anyone types when a search
         // stops working - "searching my mailbox does not work" is.
         new IntentEntry(N("searching a mailbox does not work"), "protocols",
            N("Raise the IMAP search time and size limits; a search that hits either one is reported to the client as a failure.")),
         new IntentEntry(N("stop forwarded mail failing spf"), "security",
            N("Turn on SRS so a forwarded message keeps a return path this server can vouch for.")),
         new IntentEntry(N("reject forged bounce messages"), "security",
            N("Turn on BATV so a bounce for a message this server never sent can be told apart from a real one.")),
         new IntentEntry(N("limit how long a tls session can be resumed"), "tls",
            N("Set the resumption lifetime and ticket-key rotation, which bound how long a captured resumption secret stays useful.")),
         new IntentEntry(N("disconnect idle clients sooner"), "protocols",
            N("The per-protocol idle timeouts decide how long a connection may sit doing nothing before it is closed.")),
         // MaxSubmissionsPerIPPerMinute. An abused account sending thousands of
         // messages an hour is one of the commonest reasons an administrator opens
         // this application in a hurry, and the only setting that helps was
         // reachable by knowing its name.
         new IntentEntry(N("limit how many messages an account can send per minute"), "hardening",
            N("Set the per-IP submission rate limit; authenticated senders above it are refused.")),
         new IntentEntry(N("monitor the server from outside"), "api",
            N("Prometheus metrics, health endpoints and the REST API are enabled here.")),
         new IntentEntry(N("what version am i running"), "about",
            N("Version, build and licence information."))
      };
   }
}
