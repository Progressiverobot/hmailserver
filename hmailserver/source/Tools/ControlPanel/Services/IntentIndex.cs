using System;
using System.Collections.Generic;

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
         new IntentEntry("why is mail queued", "queue",
            "See every message still waiting, the last error the server got, and when it will be retried."),
         new IntentEntry("mail is stuck", "queue",
            "The delivery queue lists what is waiting and why."),
         new IntentEntry("mail is not being delivered", "queue",
            "Start at the delivery queue: if the message is there, the reason it has not left is there too."),
         new IntentEntry("outgoing mail is not sending", "queue",
            "Check the queue for the failure the remote server reported."),
         new IntentEntry("messages are not arriving", "logs",
            "Watch the live log while the sender retries - an inbound message that never appears was refused or never arrived."),
         new IntentEntry("find out why a message bounced", "logs",
            "The live log carries the SMTP conversation, including the text the far end sent back."),
         new IntentEntry("see what the server is doing right now", "logs",
            "Stream the log as it is written."),
         new IntentEntry("test whether port 25 is open", "diagnostics",
            "Run the built-in checks: outbound port 25, MX resolution, backup folder and IP configuration."),
         new IntentEntry("check if the server is healthy", "diagnostics",
            "Run the built-in connectivity and configuration checks."),
         new IntentEntry("where does mail for a domain go", "mxquery",
            "Look up the MX records for any domain."),
         new IntentEntry("is anyone connected", "status",
            "Server status lists the live sessions for each protocol."),

         // ---- People and addresses -----------------------------------------
         new IntentEntry("add a user", "domains",
            "Open the domain, then add the account - accounts live inside their domain."),
         new IntentEntry("add a mailbox", "domains",
            "Accounts are added inside the domain that owns the address."),
         new IntentEntry("create an e-mail address", "domains",
            "Add an account for a real mailbox, or an alias to point one address at another."),
         new IntentEntry("add a domain", "domains",
            "Add the domain first; accounts, aliases and lists all hang off it."),
         new IntentEntry("reset a user's password", "domains",
            "Open the domain, open the account, and set a new password."),
         // "change a user password" and "change a mailbox password" are spelled out
         // rather than left to the partial matcher. Without them both queries scored a
         // tie with "change the administrator password" - two of three tokens each -
         // and lost the tie on alphabetical title order, so asking how to change a
         // mailbox password led with the page that changes the SERVER administration
         // password. Leading an administrator to the wrong password field is worse than
         // returning nothing.
         new IntentEntry("change a user password", "domains",
            "Open the domain, open the account, and set a new password. This is not the server administration password."),
         new IntentEntry("change a mailbox password", "domains",
            "Mailbox passwords are set on the account, inside its domain."),
         // The destructive half of the lifecycle. Every intent here was add/configure/
         // diagnose, so "add a user" was answerable and "delete a user" returned
         // nothing at all - and the person who cannot find how to remove an account is
         // more likely to leave a live mailbox for a leaver than the one who cannot
         // find how to add one.
         new IntentEntry("delete a user", "domains",
            "Accounts are deleted inside the domain that owns them; deleting one removes its mail."),
         new IntentEntry("remove a mailbox", "domains",
            "Open the domain, select the account, and delete it. Consider an alias if the address must keep receiving."),
         new IntentEntry("disable an account", "domains",
            "An account can be switched off without deleting it, which keeps the mail and refuses new logons."),
         new IntentEntry("remove a domain", "domains",
            "Deleting a domain deletes the accounts inside it. Check for aliases and lists pointing at it first."),
         new IntentEntry("remove an alias", "domains",
            "Aliases are listed under their domain and can be deleted without touching the mailbox behind them."),
         new IntentEntry("delete a mailing list", "domains",
            "Distribution lists live in the domain that owns the list address."),
         new IntentEntry("set up a mailing list", "domains",
            "Distribution lists are created inside the domain that owns the list address."),
         new IntentEntry("forward one address to another", "domains",
            "An alias points one address at another without needing a second mailbox."),
         new IntentEntry("set a mailbox size limit", "domains",
            "A quota can be set on the domain and overridden per account."),
         new IntentEntry("collect mail from another provider", "domains",
            "External accounts fetch mail from a remote POP3 or IMAP mailbox into a local one."),
         new IntentEntry("sign outgoing mail with dkim", "domains",
            "DKIM signing is configured per domain, with the key and selector you publish in DNS."),
         new IntentEntry("share a folder between accounts", "publicfolders",
            "Create the public folder, then grant accounts or groups permission on it."),

         // ---- Spam and viruses ---------------------------------------------
         // The overview first, because "is my spam filtering set up sensibly" is a
         // question the five editing pages could not answer between them: each one
         // shows its own switch and none shows the order, the scores or the two
         // thresholds that decide what the score means.
         new IntentEntry("what is my spam configuration", "spamoverview",
            "Every check in the order the server runs them, its score, and what the total does to the message."),
         new IntentEntry("which spam checks are running", "spamoverview",
            "One list of the nine checks, which are on, and which of the two phases each one runs in."),
         new IntentEntry("why is my spam filter not catching anything", "spamoverview",
            "The overview names the arrangements that silently do nothing - a zero threshold, a threshold no "
            + "enabled check can reach, a check scoring zero."),
         new IntentEntry("stop spam", "antispam",
            "Scores and thresholds, SPF, DKIM, DMARC, greylisting and SpamAssassin all live here."),
         new IntentEntry("too much spam is getting through", "antispam",
            "Lower the mark and delete thresholds, or turn on more of the checks."),
         new IntentEntry("turn on spamassassin", "antispam",
            "Point the server at a SpamAssassin host and choose how its score is merged."),
         new IntentEntry("check spf dkim and dmarc", "antispam",
            "Each check can be enabled separately and given its own score."),
         new IntentEntry("legitimate mail is being marked as spam", "spamwhitelist",
            "White-list the sender or their IP range so the message bypasses spam protection."),
         new IntentEntry("let a sender through the spam filter", "spamwhitelist",
            "Add the address or IP range to the anti-spam white list."),
         // Phrased around reputation rather than around "block an IP address":
         // that wording collides with the far more common request, which is to
         // refuse one specific address on the IP ranges page, and a query like
         // "ban an ip address" was landing here instead of there.
         new IntentEntry("block mail from a bad reputation ip", "dnsbl",
            "Add a DNS blacklist; connections from listed addresses gain score or are refused."),
         new IntentEntry("block spam links in message bodies", "surbl",
            "SURBL servers are checked against the links inside the message."),
         new IntentEntry("mail from a new sender is delayed", "greylistwhitelist",
            "That is greylisting. Exempt the sending address here, or turn greylisting off entirely."),
         new IntentEntry("scan for viruses", "antivirus",
            "Enable ClamAV or an external scanner, and choose what happens to an infected message."),
         new IntentEntry("block executable attachments", "blockedattachments",
            "Add file-name patterns; matching attachments are stripped whatever a scanner says."),

         // ---- Who may connect ----------------------------------------------
         new IntentEntry("block an ip", "ipranges",
            "Add an IP range with everything denied - ranges are matched most specific first."),
         new IntentEntry("let a device send mail", "ipranges",
            "Add an IP range for the device that allows SMTP delivery without requiring authentication."),
         new IntentEntry("let the office printer send e-mail", "ipranges",
            "Give the printer's address its own IP range with authentication not required."),
         new IntentEntry("require authentication to send mail", "ipranges",
            "Turn on the require-authentication options for the range that covers your clients."),
         new IntentEntry("make sure i am not an open relay", "ipranges",
            "Check that the internet-wide range does not allow deliveries to external accounts."),
         // These three used to land on one page called "Auto-ban & SSL/TLS", which
         // meant two of them showed the reader a cipher list they had not asked
         // for and the third showed them a lockout threshold. The page is now two
         // pages and the phrases point at the half each one is about.
         new IntentEntry("someone is trying to guess passwords", "autoban",
            "Auto-ban locks out an address after repeated logon failures."),
         new IntentEntry("lock out repeated failed logons", "autoban",
            "Set the auto-ban threshold and how long the lockout lasts."),
         // Deliberately phrased with "unban" rather than "unblock": "block" is
         // folded onto by ban/deny/reject and is the word the IP ranges page owns,
         // so an "unblock..." phrase here scored against "block an ip" and the two
         // could tie. "unban" is nobody else's word.
         new IntentEntry("unban an address that was locked out", "autoban",
            "Raise the threshold here and clear the counted failures. An address already locked out stays out "
            + "until its \"Auto-ban:\" range expires, or you delete that range on the IP ranges page."),
         new IntentEntry("which tls versions are allowed", "tls",
            "The accepted TLS versions and cipher suites are set here."),
         new IntentEntry("change the administrator password", "adminaccess",
            "Set a new server administration password."),
         new IntentEntry("use microsoft 365 or google sign-in", "authentication",
            "Configure OAuth2 / XOAUTH2 so clients authenticate with a token instead of a password."),
         new IntentEntry("make stored passwords harder to crack", "authentication",
            "Choose the password hashing algorithm and its work factor."),
         // DisableAUTHList is a comma-separated list of ports on the Authentication
         // page. Nothing in its label or its page title contains the word "port",
         // so the one query anybody would type for it found the TCP/IP ports page,
         // which is not where the setting is.
         new IntentEntry("do not offer authentication on port 25", "authentication",
            "List the local ports where AUTH should not be advertised - typically a port 25 that only takes inbound mail."),

         // ---- Getting mail in and out --------------------------------------
         new IntentEntry("send all mail through my provider", "delivery",
            "Set the SMTP relayer, and its credentials if the provider needs them."),
         new IntentEntry("send mail for one domain to a different server", "routes",
            "A route overrides MX lookup for named domains."),
         new IntentEntry("retry failed deliveries sooner", "delivery",
            "The retry counts and intervals are here, along with what happens when they run out."),
         new IntentEntry("change the bounce message wording", "servermessages",
            "The bounce and error texts the server sends are editable templates."),
         new IntentEntry("act on messages as they arrive", "rules",
            "Global rules match on the message and take an action - move, tag, reply or refuse."),
         new IntentEntry("tell everyone about maintenance", "sendout",
            "Server sendout mails every account, or every account matching a wildcard."),
         new IntentEntry("my spam filter blames my own gateway", "relays",
            "Register the gateway as an incoming relay so its address is not treated as the client."),

         // ---- Ports, clients and TLS ---------------------------------------
         new IntentEntry("open the submission port", "ports",
            "Add a listener - 587 with STARTTLS, or 465 for implicit TLS."),
         new IntentEntry("make clients use an encrypted connection", "ports",
            "Set the connection security on each listener, then require it for authentication."),
         new IntentEntry("install a certificate", "certs",
            "Add the certificate and key, then attach it to the ports that should present it."),
         new IntentEntry("my certificate has expired", "certs",
            "Replace the certificate here; the expiry date of each one is listed."),
         new IntentEntry("renew certificates automatically", "acme",
            "Point the server at an ACME authority such as Let's Encrypt and it will renew on its own."),
         new IntentEntry("prove to other servers that tls is required", "security",
            "MTA-STS and DANE publish that commitment; TLS-RPT tells you who failed it."),
         new IntentEntry("set up outlook automatically", "webservices",
            "Autodiscover and autoconfig are served by the built-in HTTP listener."),
         new IntentEntry("configure a phone or tablet", "webservices",
            "Autoconfiguration hands clients the right host names, ports and security."),
         new IntentEntry("use my own name servers", "dns",
            "Set the resolvers the server queries, and whether answers are DNSSEC validated."),

         // ---- Housekeeping --------------------------------------------------
         new IntentEntry("back up the server", "backup",
            "Choose what to include and where to write it."),
         new IntentEntry("restore from a backup", "backup",
            "Restore configuration, domains and messages from a backup file."),
         new IntentEntry("move to a new server", "backup",
            "Back up here and restore on the new machine."),
         new IntentEntry("the server is slow", "performance",
            "Thread counts, database connections and caching are the levers."),
         new IntentEntry("logs are filling the disk", "logging",
            "Reduce the log level, or set how many days of logs are kept."),
         new IntentEntry("turn on more detailed logging", "logging",
            "Raise the log level and choose which log files are written."),
         // The three phrases below are for capabilities the server has and the
         // interface never advertised in words anybody would type. The log format
         // and the SQL log device were selectable for years while the server
         // ignored them; now that both work, "how do I get my logs into something
         // that reads them" is a question with an answer, and it should be
         // findable in the terms it is asked in.
         new IntentEntry("send my logs to a log analyser", "logging",
            "Choose the NCSA Common Log Format so an analyser can read the lines, or JSON for a log shipper."),
         new IntentEntry("write the log to a database", "logging",
            "Set the log destination to the database; entries fall back to the log files if it is unreachable."),
         new IntentEntry("make awstats work", "logging",
            "Turn on the AWStats-compatible log. That is a separate file - the log line format setting does not change it."),
         new IntentEntry("run code when mail arrives", "scripts",
            "Event scripts run on delivery, logon and error events."),
         new IntentEntry("keep a copy of every message", "advanced",
            "Archiving writes a copy of every message to a folder you choose."),
         new IntentEntry("change a setting with no page", "hardening",
            "Individual hMailServer.INI values are editable here."),
         // MaxSubmissionsPerIPPerMinute. An abused account sending thousands of
         // messages an hour is one of the commonest reasons an administrator opens
         // this application in a hurry, and the only setting that helps was
         // reachable by knowing its name.
         new IntentEntry("limit how many messages an account can send per minute", "hardening",
            "Set the per-IP submission rate limit; authenticated senders above it are refused."),
         new IntentEntry("monitor the server from outside", "api",
            "Prometheus metrics, health endpoints and the REST API are enabled here."),
         new IntentEntry("what version am i running", "about",
            "Version, build and licence information.")
      };
   }
}
