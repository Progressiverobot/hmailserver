// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// One node of the navigation: either a group heading or a page. Pages carry
   /// the same navigation key the page factories in MainWindow are registered
   /// under, so this map describes routes to pages and never the pages
   /// themselves.
   /// </summary>
   public sealed class NavNode
   {
      internal NavNode(string key, string title, string purpose, IReadOnlyList<string> aliases,
         IReadOnlyList<string> seeAlso, IReadOnlyList<NavNode> children, string icon = null)
      {
         Key = key;
         Title = title;
         Purpose = purpose;
         Aliases = aliases;
         SeeAlso = seeAlso;
         Children = children;
         Icon = icon;
      }

      /// <summary>The navigation key of the page, or null for a group heading.</summary>
      public string Key { get; }

      /// <summary>What the node is called on screen.</summary>
      public string Title { get; }

      /// <summary>
      /// One line saying what this is for, in the administrator's terms. Shown
      /// as the tool tip and the accessible help text in the tree, and searched
      /// by the palette, so that a page can be found by what it does and not
      /// only by what it is called.
      /// </summary>
      public string Purpose { get; }

      /// <summary>
      /// Other names for this node: legacy and renamed titles, the page's own
      /// on-screen heading where it differs from the navigation label, protocol
      /// acronyms, and the nouns an administrator would use instead of ours.
      /// Searched but never displayed.
      /// </summary>
      public IReadOnlyList<string> Aliases { get; }

      /// <summary>
      /// Page keys of the neighbouring subjects an administrator on this page
      /// very often needs next. Rendered as signposts beside the breadcrumb.
      /// This is how a subject that genuinely spans several pages - transport
      /// security is five subsystems to us and one question to the user - stays
      /// navigable without duplicating pages into two branches of the tree.
      /// </summary>
      public IReadOnlyList<string> SeeAlso { get; }

      /// <summary>Child nodes; empty for a page.</summary>
      public IReadOnlyList<NavNode> Children { get; }

      /// <summary>The group this node sits in, or null at the top level.</summary>
      public NavNode Parent { get; internal set; }

      /// <summary>True when this node is a navigable page rather than a heading.</summary>
      public bool IsPage => Key != null;

      /// <summary>
      /// The Segoe Fluent Icons glyph for this node, as the NAME of a
      /// Wpf.Ui.Controls.SymbolRegular member ("Globe24"). A string rather than
      /// the enum so this file stays WPF-free and testable; MainWindow resolves
      /// it, and an unknown name renders no icon rather than failing - loudly
      /// in DEBUG builds, because Enum.TryParse failing silently is how a
      /// mistyped name once shipped one group bare among seven with glyphs.
      /// Set on the top-level groups - a glyph on all fifty leaf pages would be
      /// noise, and Windows 11's own settings surfaces put them on the
      /// top level.
      /// </summary>
      public string Icon { get; }
   }

   /// <summary>
   /// The Control Panel's information architecture, as data.
   ///
   /// It replaces the hand-built tree in MainWindow, whose comment said it
   /// "mirrors the classic Administrator tree layout" - a 2000s MFC layout
   /// organised around where the product keeps things. Of 42 pages, 30 sat
   /// under a single "Settings" group, three levels deep; "Settings" is not a
   /// category, it is where things go when nobody has decided what they are.
   /// Everything here is grouped by what an administrator is trying to achieve
   /// instead, and the depth is capped at two so that a page is a heading and a
   /// click rather than a heading, a sub-heading and a click.
   ///
   /// Two rules constrain the reorganisation, because the pages themselves are
   /// not being rewritten:
   ///
   ///   - Every navigation key registered in MainWindow appears here exactly
   ///     once. A page that stops being reachable is a regression, not a
   ///     simplification, and NavigationMapTests fails if the two sets differ.
   ///   - No page title changes. Titles appear in the manual, in forum answers
   ///     and in support mail; renaming them to read better here would silently
   ///     invalidate all of it. Where a better name exists it is added as an
   ///     alias, which the palette searches, instead of replacing the title.
   ///
   ///     Splitting a page is the one case that cannot obey that rule, because a
   ///     title naming two subjects cannot survive on a page that now holds one
   ///     of them. "Auto-ban &amp; SSL/TLS" is the only one so far, and the cost
   ///     is paid rather than avoided: the old title is an alias on BOTH halves,
   ///     the documented path to it still resolves (to the auto-ban half - see
   ///     <see cref="LegacyPaths"/>), and each half signposts the other. Anyone
   ///     splitting another page owes the reader the same three things.
   ///
   /// Living in a WPF-free file rather than in MainWindow is deliberate: the
   /// structure is the thing worth testing, and the tests reference
   /// ControlPanel.Core, which has no WPF.
   /// </summary>
   public static class NavigationMap
   {
      private static readonly List<NavNode> roots_ = BuildRoots();
      private static readonly Dictionary<string, NavNode> byKey_ = new Dictionary<string, NavNode>(StringComparer.OrdinalIgnoreCase);
      private static readonly List<NavNode> pages_ = new List<NavNode>();
      private static readonly List<NavNode> groups_ = new List<NavNode>();

      static NavigationMap()
      {
         foreach (NavNode root in roots_)
            Index(root, null);
      }

      /// <summary>The top-level nodes, in display order.</summary>
      public static IReadOnlyList<NavNode> Roots => roots_;

      /// <summary>Every page, in display order.</summary>
      public static IReadOnlyList<NavNode> Pages => pages_;

      /// <summary>Every group heading, in display order.</summary>
      public static IReadOnlyList<NavNode> Groups => groups_;

      /// <summary>The page with this navigation key, or null.</summary>
      public static NavNode Find(string key)
         => key != null && byKey_.TryGetValue(key, out NavNode node) ? node : null;

      /// <summary>
      /// The on-screen title of a page, or the key itself when it is unknown.
      /// Returning the key rather than an empty string keeps a stale reference
      /// visible in the interface instead of rendering as a blank row.
      /// </summary>
      public static string TitleOf(string key)
      {
         NavNode node = Find(key);
         return node != null ? node.Title : key ?? "";
      }

      /// <summary>
      /// The trail from the top level down to and including the page, or an
      /// empty list for an unknown key.
      /// </summary>
      public static IReadOnlyList<NavNode> PathTo(string key)
      {
         NavNode node = Find(key);
         if (node == null)
            return Array.Empty<NavNode>();

         var trail = new List<NavNode>();
         for (NavNode current = node; current != null; current = current.Parent)
            trail.Add(current);

         trail.Reverse();
         return trail;
      }

      /// <summary>
      /// The breadcrumb as one string ("Spam &amp; virus filtering &gt; Anti-spam
      /// settings"), for a search result that has to say where the thing it
      /// found actually lives. Uses a plain "&gt;" rather than a chevron
      /// character so it is safe in a tool tip, an accessible name and a test
      /// assertion alike.
      /// </summary>
      public static string LocationOf(string key)
         => string.Join(" > ", PathTo(key).Select(n => n.Title));

      /// <summary>The title of the group a page sits in, or "" at the top level.</summary>
      public static string GroupOf(string key)
      {
         NavNode node = Find(key);
         return node?.Parent != null ? node.Parent.Title : "";
      }

      /// <summary>
      /// Where the page used to be. Keyed on the path an administrator would
      /// find in the manual, in a forum answer or in their own notes - both the
      /// classic Administrator paths and the 6.2.18 Control Panel paths this
      /// reorganisation replaces.
      ///
      /// The palette searches these, so a documented route still lands on the
      /// right page after the tree has moved. Without it, reorganising the
      /// navigation would quietly invalidate every set of instructions ever
      /// written about this product, which is a worse outcome than a badly
      /// organised tree.
      /// </summary>
      public static readonly IReadOnlyDictionary<string, string> LegacyPaths =
         new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
         {
            ["Status > Server status"] = "status",
            ["Status > Delivery queue"] = "queue",
            ["Status > Live logs"] = "logs",

            ["Settings > Protocols"] = "protocols",
            ["Settings > Protocols > SMTP"] = "protocols",
            ["Settings > Protocols > POP3"] = "protocols",
            ["Settings > Protocols > IMAP"] = "protocols",
            ["Settings > Delivery of e-mail"] = "delivery",
            ["Settings > Public folders"] = "publicfolders",
            ["Settings > Logging"] = "logging",

            ["Settings > Anti-spam"] = "antispam",
            ["Settings > Anti-spam > SURBL servers"] = "surbl",
            ["Settings > Anti-spam > DNS blacklists"] = "dnsbl",
            ["Settings > Anti-spam > White list"] = "spamwhitelist",
            ["Settings > Anti-spam > Greylisting white list"] = "greylistwhitelist",
            ["Settings > Anti-virus"] = "antivirus",
            ["Settings > Anti-virus > Blocked attachments"] = "blockedattachments",

            ["Settings > Advanced"] = "advanced",
            ["Settings > Advanced > Routes"] = "routes",
            ["Settings > Advanced > IP ranges"] = "ipranges",
            ["Settings > Advanced > SSL certificates"] = "certs",
            ["Settings > Advanced > TCP/IP ports"] = "ports",
            ["Settings > Advanced > Incoming relays"] = "relays",
            ["Settings > Advanced > Groups"] = "groups",
            ["Settings > Advanced > Server messages"] = "servermessages",
            ["Settings > Advanced > Scripts"] = "scripts",
            ["Settings > Advanced hardening"] = "hardening",

            ["Settings > Security > Authentication"] = "authentication",
            ["Settings > Security > Administrative access"] = "adminaccess",

            // "Auto-ban & SSL/TLS" was one page holding two unrelated subjects,
            // and it is now two: "Auto-ban" under Access & abuse protection and
            // "SSL/TLS" under TLS & certificates.
            //
            // A path can only resolve to one page, so the combined path resolves
            // to the brute-force half: an administrator typing it is following a
            // note about repeated logon failures far more often than one about
            // cipher suites, and the auto-ban settings are the ones with no other
            // home (versions and ciphers are findable from four other TLS pages).
            // The other half is not lost - the old title is an alias on BOTH
            // pages, so typing it in the palette offers the two of them, and each
            // page signposts the other.
            ["Settings > Security > Auto-ban & SSL/TLS"] = "autoban",
            ["Settings > Security > Auto-ban"] = "autoban",
            ["Settings > Advanced > Auto-ban"] = "autoban",
            ["Settings > Security > SSL/TLS"] = "tls",
            ["Settings > Advanced > SSL/TLS"] = "tls",
            ["Settings > Security > IP ranges"] = "ipranges",
            ["Settings > Security > SSL certificates"] = "certs",
            ["Settings > Security > Transport security"] = "security",
            ["Settings > Security > Certificates (ACME)"] = "acme",

            ["Settings > Network > TCP/IP ports"] = "ports",
            ["Settings > Network > Incoming relays"] = "relays",
            ["Settings > Network > DNS resolver"] = "dns",
            ["Settings > Network > Web services & autoconfiguration"] = "webservices",
            ["Settings > Network > API & monitoring"] = "api",

            ["Settings > Maintenance > Performance"] = "performance",
            ["Settings > Maintenance > Advanced & scripting"] = "advanced",
            ["Settings > Maintenance > Advanced INI settings"] = "hardening",
            ["Settings > Maintenance > Event scripts"] = "scripts",
            ["Settings > Maintenance > Server messages"] = "servermessages",
            ["Settings > Maintenance > Groups"] = "groups",

            ["Utilities > MX-query"] = "mxquery",
            ["Utilities > Server sendout"] = "sendout",
            ["Utilities > Diagnostics"] = "diagnostics",
            ["Utilities > Backup"] = "backup",
            ["Utilities > Backup & restore"] = "backup"
         });

      /// <summary>
      /// A lower-case, hyphen-separated form of a title, for automation ids.
      /// Kept here rather than in MainWindow so that the ids the accessibility
      /// tooling and any UI automation depend on are derived from the same
      /// titles the map defines.
      /// </summary>
      public static string Slug(string text)
         => new string((text ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

      private static void Index(NavNode node, NavNode parent)
      {
         node.Parent = parent;

         if (node.IsPage)
         {
            // A duplicated key would give the page two homes, and every consumer
            // here (breadcrumb, palette, tree selection) silently picks the
            // first. Fail loudly at start-up instead: this is a table in one
            // file, so a duplicate is always a typo.
            if (byKey_.ContainsKey(node.Key))
               throw new InvalidOperationException("The navigation map lists the page '" + node.Key + "' more than once.");

            byKey_[node.Key] = node;
            pages_.Add(node);
         }
         else
         {
            groups_.Add(node);
         }

         foreach (NavNode child in node.Children)
            Index(child, node);
      }

      /// <summary>
      /// Aliases are written as one pipe-separated string per page rather than
      /// as a string array. With 42 pages and up to a dozen aliases each, array
      /// syntax buries the actual content in punctuation, and the point of this
      /// table is that a maintainer can read the alias list and judge whether it
      /// covers how people talk.
      /// </summary>
      private static NavNode Page(string key, string title, string purpose, string aliases = "", string seeAlso = "")
         => new NavNode(key, title, purpose, Split(aliases), Split(seeAlso), Array.Empty<NavNode>());

      private static NavNode Group(string title, string purpose, string aliases, params NavNode[] children)
         => new NavNode(null, title, purpose, Split(aliases), Array.Empty<string>(), children);

      private static NavNode Group(string icon, string title, string purpose, string aliases, params NavNode[] children)
         => new NavNode(null, title, purpose, Split(aliases), Array.Empty<string>(), children, icon);

      private static IReadOnlyList<string> Split(string pipeSeparated)
         => string.IsNullOrEmpty(pipeSeparated)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : pipeSeparated.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

      /// <summary>
      /// The tree itself. Group order is the order an administrator meets these
      /// subjects: first "is it working", then the things they were asked to
      /// change, then the things they configure once.
      /// </summary>
      private static List<NavNode> BuildRoots()
      {
         return new List<NavNode>
         {
            Page("welcome", "Welcome",
               "Start here: jump straight to a common task, or press Ctrl+K and describe what you want to do.",
               aliases: "Home|Start|Getting started|Quick actions|First steps",
               seeAlso: "dashboard"),

            Page("dashboard", "Dashboard",
               "Live counters and charts for processed mail, spam, viruses and sessions.",
               aliases: "Overview|Charts|Graphs|Statistics|Counters|Throughput",
               seeAlso: "status|queue"),

            Group("Pulse24", "Monitoring & troubleshooting",
               "Is mail flowing, and if it is not, where has it stopped?",
               "Status|Health|Troubleshooting|Problems|Something is wrong|Observability",

               Page("status", "Server status",
                  "Which services are running, who is connected right now, and how long the server has been up.",
                  aliases: "Sessions|Connections|Uptime|Running|Service state|Live sessions",
                  seeAlso: "queue|dashboard|logs"),

               Page("queue", "Delivery queue",
                  "Messages still waiting to be delivered, why each is waiting, and when it will be retried.",
                  aliases: "Spool|Stuck mail|Pending mail|Outbound queue|Retry|Not delivered|Backlog",
                  seeAlso: "logs|delivery|status|stalledmail"),

               // The guide to mail that stops moving on a healthy-looking server,
               // as a page with a button beside each step. The queue shows that a
               // message is waiting; this is what to do when the reason is not on
               // the queue page - which is the case the guide was written for.
               Page("stalledmail", "Diagnosing stalled mail",
                  "Mail is not moving and nothing has crashed: which half has stalled, the log lines that name the cause, and the setting that bounds each stage.",
                  aliases: "Stalled mail|Slow mail|Mail is stalled|Timed out while sending end of data|Accepted but never delivered|Hung|Wedged|Stall diagnosis|Diagnosing slow mail",
                  seeAlso: "queue|logs|diagnostics"),

               // Next to the queue on purpose: the two answer the same question at
               // different moments. The queue says where a message is now; the trace
               // says what happened to one that has already gone.
               Page("messagetrace", "Message trace",
                  "What happened to a particular message - searchable by sender or recipient, instead of grepping logs.",
                  aliases: "Message trace|Delivery history|Where did my email go|Did it arrive|Track a message|Was it delivered|Message log|Audit|What happened to|Trace",
                  seeAlso: "queue|logs|quarantine"),

               Page("logs", "Live logs",
                  "Stream the server log as it is written - the fastest way to see what happened to one message.",
                  aliases: "Log viewer|Tail|Trace|Debug|SMTP conversation|Error log|Awstats",
                  seeAlso: "logging|queue|diagnostics"),

               Page("diagnostics", "Diagnostics",
                  "Run the built-in connectivity and configuration checks, and read the last message-store consistency scan.",
                  aliases: "Self test|Health check|Port 25 test|Connectivity|Message store consistency|Missing files",
                  seeAlso: "mxquery|logs|queue"),

               Page("mxquery", "MX query",
                  "Look up where e-mail for a domain is delivered, to check DNS before blaming the server.",
                  aliases: "MX-query|MX lookup|DNS lookup|nslookup|Mail exchanger|Where does mail go",
                  seeAlso: "routes|dns|diagnostics"),

               // Several shipped features are inert until something is done where
               // the server has no reach - a DNS zone, a CA, an identity provider.
               // This page is the one place that says which of those are still
               // outstanding, so it lives with the other "is it actually working"
               // pages rather than beside any one feature's settings.
               Page("externalsetup", "External setup",
                  "Everything this server needs done outside it - DNS records, key and CA files, trusted lists - and the state of each one.",
                  aliases: "External prerequisites|Prerequisites|Setup checklist|Checklist|Outside the server|What still needs doing|Publish a DNS record|TXT record|Inert features|Action needed",
                  seeAlso: "security|diagnostics|domains"),

               // Sits beside External setup on purpose: that page says WHICH
               // external steps are outstanding, this one produces the exact
               // DNS records those steps need and checks each one.
               Page("dnsrecords", "DNS records",
                  "The exact SPF, DKIM, DMARC, MTA-STS and TLS-RPT records each domain should publish, with a check for whether each one is live.",
                  aliases: "SPF record|DKIM record|DMARC record|MTA-STS record|TLS-RPT record|TXT records|Reverse DNS|PTR record|What DNS records do I need|Publish DNS records|DNS checklist|Sender authentication",
                  seeAlso: "externalsetup|domains|security"),

               Page("logging", "Logging",
                  "Which log files the server writes, how much detail they carry, and how long they are kept.",
                  aliases: "Log level|Log files|Log folder|Retention|Delete old logs|Verbosity|Debug logging",
                  seeAlso: "logs|performance"),

               Page("api", "API & monitoring",
                  "The REST API, Prometheus metrics and health endpoints an outside monitoring system reads.",
                  aliases: "REST|Prometheus|Grafana|Metrics|Health endpoint|Nagios|Zabbix|Webhooks|Automation",
                  seeAlso: "apikeys|logging|status|webservices"),

               // Its own page rather than a card on API & monitoring: keys are a
               // list of objects with their own lifecycle, and the page has to be
               // usable while the REST listener is switched off - which is the
               // state every installation is in when it first comes here.
               Page("apikeys", "REST API keys",
                  "Scoped, expiring credentials for the REST API, so automation does not have to carry the administrator password.",
                  aliases: "API key|Bearer token|Access token|Credential|Service account for the API|Revoke a key|hmapi|Automation credential|Read-only key",
                  seeAlso: "api|adminaccess")),

            Group("Globe24", "Accounts & domains",
               "The people and the domains this server carries mail for.",
               "Users|Mailboxes|People|Addresses|Tenants",

               Page("domains", "Domains",
                  "Add domains, accounts, aliases, distribution lists, DKIM signing and external (POP3) collection.",
                  aliases: "Accounts|Users|Mailboxes|Add a user|Aliases|Distribution lists|Mailing list|Quota|Size limit|DKIM|External accounts|Domain aliases|Signatures|Forwarding",
                  seeAlso: "publicfolders|groups|rules"),

               Page("publicfolders", "Public folders",
                  "Shared IMAP folders, and which accounts or groups may read and write each one.",
                  aliases: "Shared folders|Shared mailbox|IMAP ACL|Permissions|Team folder|Shared calendar",
                  seeAlso: "groups|domains"),

               Page("groups", "Groups",
                  "Security groups, so shared-folder permissions can be granted to several accounts at once.",
                  aliases: "Security groups|Roles|Membership|Permission groups",
                  seeAlso: "publicfolders|domains")),

            Group("MailArrowForward20", "Mail flow & delivery",
               "How mail gets in, where it goes next, and what happens to it on the way.",
               "Delivery|Routing|Transport|SMTP|Relay|Mail flow",

               Page("delivery", "Delivery of e-mail",
                  "Relaying to an upstream server, retry intervals, bounce handling and what happens to local mail.",
                  aliases: "SMTP relayer|Smarthost|Send through my ISP|Retries|Bounce|Non-delivery report|Host name|Max message size|Deliver local to local",
                  seeAlso: "routes|queue|ipranges"),

               Page("routes", "Routes",
                  "Deliver mail for named domains to a chosen server instead of looking up its MX record.",
                  aliases: "Static route|Transport map|Send domain to server|Internal relay|Hybrid|Split delivery",
                  seeAlso: "delivery|mxquery"),

               Page("relays", "Incoming relays",
                  "Upstream gateways whose IP address must not be treated as the connecting client in spam checks.",
                  aliases: "Trusted relay|Upstream gateway|Front-end filter|Load balancer|Proxy|Received header",
                  seeAlso: "ipranges|antispam"),

               Page("rules", "Rules",
                  "Server-wide rules that inspect messages as they arrive and act on them.",
                  aliases: "Global rules|Filters|Conditions and actions|Sieve|Auto-reply|Move to folder|Tag subject",
                  seeAlso: "domains|scripts"),

               Page("servermessages", "Server messages",
                  "The greeting, bounce and error texts the server sends back to clients.",
                  aliases: "Banner|Greeting|Bounce text|Error text|Templates|Wording|Localise messages",
                  seeAlso: "delivery|protocols"),

               Page("sendout", "Server sendout",
                  "E-mail every account on the server at once - for maintenance announcements.",
                  aliases: "Announcement|Mail all users|Broadcast|Notify everyone|Maintenance notice",
                  seeAlso: "domains")),

            Group("ShieldCheckmark24", "Spam & virus filtering",
               "Everything that decides whether a message is wanted.",
               "Anti-spam|Antispam|Anti-virus|Antivirus|Filtering|Junk|Malware|Content filtering",

               // The group had five spam pages and no answer to the question an
               // administrator actually arrives with: what will all of this do to
               // a message? Each page is a correct editor for its own subject and
               // none of them can show the pipeline - the order the checks run in,
               // what each one adds to the score, the two thresholds that turn a
               // score into a verdict, and the several arrangements in which the
               // whole thing silently does nothing.
               //
               // Deliberately read-only. It is a diagnosis, not a sixth editor:
               // every row links to the page that owns the setting, which is also
               // what keeps this page from becoming a second place to change the
               // same value.
               Page("spamoverview", "Spam filtering overview",
                  "Every spam check in the order the server runs them, what each adds to the score, and what that score then does to the message.",
                  aliases: "Spam overview|Anti-spam overview|Spam pipeline|Spam summary|What is my spam configuration|Spam checks|Order of spam checks|Spam thresholds|Spam verdict|Why was this marked as spam",
                  seeAlso: "antispam|spamwhitelist|logs"),

               // Placed next to the settings that fill it rather than under a
               // diagnostics heading: the reason somebody opens this page is almost
               // always that a threshold on the anti-spam page just caught something
               // it should not have.
               Page("quarantine", "Quarantine",
                  "Messages held for review instead of being refused, and the two things you can do with each: release it to the recipient, or delete it.",
                  aliases: "Quarantine|Held messages|Release a message|False positive|Review spam|Quarantined mail|Where did my email go|Blocked message|Recover a message|Spam review queue",
                  seeAlso: "antispam|spamoverview|spamwhitelist"),

               Page("antispam", "Anti-spam settings",
                  "Scores and thresholds, SPF, DKIM and DMARC checks, greylisting, and SpamAssassin.",
                  aliases: "Spam filter|Stop spam|Score|Threshold|SPF|DKIM|DMARC|ARC|Greylisting|SpamAssassin|Junk mail|PTR|HELO check|Subject prefix",
                  seeAlso: "spamoverview|dnsbl|surbl|spamwhitelist|greylistwhitelist"),

               // The overview is first in every one of these lists on purpose: it
               // is the one page that says what the change just made on this page
               // does to a message, and only three signposts fit beside the
               // breadcrumb.
               Page("surbl", "SURBL servers",
                  "Block lists checked against the links found inside a message body.",
                  aliases: "SURBL|URI block list|Link blacklist|Body URL check|Spamhaus DBL",
                  seeAlso: "spamoverview|antispam|dnsbl"),

               Page("dnsbl", "DNS blacklists",
                  "Block lists checked against the IP address that is connecting.",
                  aliases: "DNSBL|DNS blacklists (DNSBL)|RBL|Blackhole list|Spamhaus|Barracuda|Block an IP by reputation",
                  seeAlso: "spamoverview|antispam|surbl|ipranges"),

               Page("spamwhitelist", "White list",
                  "Senders and IP ranges that bypass spam protection entirely.",
                  aliases: "Anti-spam white list|Allow list|Safe senders|False positive|Let a sender through|Exempt from spam filter",
                  seeAlso: "spamoverview|antispam|greylistwhitelist"),

               Page("blockedsenders", "Blocked senders",
                  "Envelope senders refused outright or scored - one address, or a whole domain with its subdomains.",
                  aliases: "Blacklist|Block list|Deny list|Block a sender|Banned senders|Refuse mail from",
                  seeAlso: "spamwhitelist|antispam|spamoverview"),

               Page("greylistwhitelist", "Greylisting white list",
                  "IP addresses exempt from the greylisting delay, for senders that will not retry.",
                  aliases: "Greylist exemption|Greylisting delay|Slow mail|First message delayed|Retry delay",
                  seeAlso: "spamoverview|antispam|spamwhitelist"),

               // The same argument as the spam overview above, and the anti-virus
               // case is sharper: the settings page can show that a scanner is
               // switched on and cannot show whether it is able to run. A scanner
               // enabled with a missing host or executable errors on every message,
               // and VirusScanner::ScanFile_ treats "every scanner errored" as
               // NoVirusFound - so the mail is delivered as though it had been
               // examined. Nothing in an editor for those settings can say that.
               Page("virusoverview", "Virus scanning overview",
                  "Which scanners can actually run, what size of message is scanned, and what happens to one that is found to be infected.",
                  aliases: "Anti-virus overview|Antivirus overview|Virus overview|Virus summary|What is my virus configuration|Is virus scanning working|Am I scanning for viruses|Scanner not working|ClamAV not scanning|Unscanned mail",
                  seeAlso: "antivirus|blockedattachments|logs"),

               Page("antivirus", "Anti-virus settings",
                  "ClamAV and external scanners, and what to do with a message that is found to be infected.",
                  aliases: "Virus scanner|ClamAV|Malware|Infected mail|Scan attachments|External scanner",
                  seeAlso: "virusoverview|blockedattachments"),

               Page("blockedattachments", "Blocked attachments",
                  "File-name patterns stripped from incoming messages regardless of what a scanner says.",
                  aliases: "Attachment blocking|Block exe|File extensions|Strip attachments|Dangerous files|Zip",
                  seeAlso: "virusoverview|antivirus")),

            Group("PlugConnected24", "Connections & protocols",
               "Which services listen, on what, and how clients reach them.",
               "Network|Ports|Listeners|Protocols|Bindings|Client access",

               Page("protocols", "Protocols",
                  "Per-protocol limits and behaviour for SMTP, POP3 and IMAP.",
                  aliases: "SMTP|POP3|IMAP|Timeouts|Max connections|Message size limit|Authentication required|Welcome banner",
                  seeAlso: "ports|delivery|authentication"),

               Page("ports", "TCP/IP ports",
                  "The addresses and ports each protocol listens on, and the connection security used on each.",
                  aliases: "Listeners|Bindings|Port 25|Port 465|Port 587|Submission|Port 993|Port 995|STARTTLS|Implicit TLS|Interface|Bind address|Open a port",
                  seeAlso: "certs|tls|protocols"),

               Page("webservices", "Web services & autoconfiguration",
                  "The built-in HTTP listener: Autodiscover, autoconfig, the MTA-STS policy and ACME challenges.",
                  aliases: "HTTP|Autodiscover|Autoconfig|Outlook setup|Thunderbird setup|Mobile setup|MTA-STS policy|Well-known|Web server",
                  seeAlso: "acme|security|api"),

               Page("dns", "DNS resolver",
                  "Which name servers the server asks, and whether answers are DNSSEC validated.",
                  aliases: "Name servers|Resolver|DNSSEC|Lookup failures|DNS cache|EDNS",
                  seeAlso: "security|antispam|mxquery")),

            Group("LockClosed24", "TLS & certificates",
               "Is transport security correct, and will it still be correct next month?",
               "TLS|SSL|Certificates|Encryption|MTA-STS|DANE|Transport security",

               Page("certs", "SSL certificates",
                  "The certificates this server presents, which port uses which, and when each expires.",
                  aliases: "Certificate|SSL certificate|TLS certificate|PFX|PEM|Expiry|Renew|Install a certificate|Chain|Private key",
                  seeAlso: "acme|ports|security"),

               Page("acme", "Certificates (ACME)",
                  "Issue and renew certificates automatically from Let's Encrypt or another ACME authority.",
                  aliases: "ACME|Let's Encrypt|Letsencrypt|Automatic renewal|http-01|Certbot|Free certificate",
                  seeAlso: "certs|webservices|ports"),

               // The TLS half of what used to be "Auto-ban & SSL/TLS". The title
               // is new because the old one named two subjects and this page is
               // now one of them; the old title stays as an alias, and it is also
               // an alias on the Auto-ban page, so a note naming it reaches
               // whichever half its reader wanted.
               // Four pages own the pieces of transport security and none of them
               // can answer the question an administrator arrives with: is anything
               // here carrying a password in the clear, and will it still serve a
               // valid certificate next month? The interesting states only exist in
               // the combination - a port set to use TLS with no certificate does
               // not fall back to plaintext, it fails to start; the AEAD-ONLY preset
               // leaves TLS 1.0 and 1.1 advertised with no suite they can use; the
               // one control that refuses a plaintext password lives on the IP
               // ranges page, which is why it is so often never seen.
               //
               // Read-only, like the spam and virus overviews, and for the same
               // reason: it must not become a fifth place to change the same value.
               Page("tlsoverview", "Transport encryption overview",
                  "What every listener protects, which certificate it presents and when that expires, and what can still be negotiated.",
                  aliases: "TLS overview|SSL overview|Encryption overview|Is my mail encrypted|Plaintext password|Passwords in the clear|Unencrypted port|Certificate expiry|Certificate expiring|What TLS am I using|STARTTLS not required",
                  seeAlso: "tls|certs|ports"),

               Page("tls", "SSL/TLS",
                  "Which TLS versions and ciphers this server will negotiate, and whether it verifies the certificate of the server it delivers to.",
                  aliases: "Auto-ban & SSL/TLS|TLS versions|TLS 1.0|TLS 1.1|TLS 1.2|TLS 1.3|Ciphers|Cipher list|Cipher suites|OpenSSL cipher string|Disable old TLS|Weak ciphers|ChaCha20|Verify remote certificate",
                  // autoban stays in this list. This page and Auto-ban are the two
                  // halves of the old "Auto-ban & SSL/TLS", and PageSplitTests holds
                  // them to signposting each other so a reader who followed the old
                  // title and landed on the wrong half can get to the right one.
                  seeAlso: "tlsoverview|certs|ports|security|autoban"),

               Page("security", "Transport security",
                  "DANE, MTA-STS, ARC and TLS reporting - proving to other servers that TLS is required.",
                  aliases: "DANE|TLSA|MTA-STS|TLS-RPT|TLS reporting|ARC|Downgrade|Opportunistic TLS|Enforce TLS",
                  seeAlso: "certs|dns|webservices")),

            Group("ShieldKeyhole24", "Access & abuse protection",
               "Who may connect, who may authenticate, and what happens when someone keeps trying.",
               "Security|Access control|Authentication|Abuse|Brute force|Who can connect",

               Page("authentication", "Authentication",
                  "How accounts prove who they are: password hashing, OAuth2 / XOAUTH2, and required mechanisms.",
                  aliases: "Password hashing|bcrypt|Argon2|OAuth2|XOAUTH2|Modern authentication|Microsoft 365|Google|SASL|CRAM-MD5|APOP|Two-factor|Pepper",
                  seeAlso: "adminaccess|autoban|ipranges|protocols"),

               // Directory authentication is how accounts prove who they are, so it
               // belongs beside Authentication - not filed under "advanced INI" because
               // that happens to be where its settings are stored.
               Page("ldap", "Directory authentication (LDAP)",
                  "Authenticate accounts against Active Directory or any LDAP directory, so users sign in with their domain password.",
                  aliases: "LDAP|Active Directory|AD|Directory|Domain password|Bind|LDAPS|StartTLS|Single sign-on|Domain accounts|DC",
                  seeAlso: "authentication|adminaccess|domains|directorysync"),

               // Beside directory authentication, and deliberately not folded into it.
               // The two answer different questions: that page is about whether somebody
               // who already has a mailbox can log into it, this one about which people
               // have a mailbox at all. Somebody arriving to bulk-create accounts from
               // the directory is not looking for a bind method, and somebody debugging
               // a logon does not want a provisioning run one click away.
               Page("directorysync", "Directory synchronisation",
                  "Create and update mailboxes to match an LDAP directory - preview first, then apply.",
                  aliases: "Directory sync|Provisioning|Provision accounts|Account source|Bulk create accounts|Import users|Sync users|LDAP sync|AD sync|Create accounts from Active Directory|Onboarding|Leavers",
                  seeAlso: "ldap|domains"),

               Page("adminaccess", "Administrative access",
                  "The server administration password, and who is allowed to use these tools at all.",
                  aliases: "Admin password|Administrator password|Change the admin password|Remote administration|COM API access",
                  seeAlso: "authentication"),

               // The brute-force half of what used to be "Auto-ban & SSL/TLS".
               // The two subjects on that page shared nothing: an administrator
               // arrives at one of them because somebody is guessing passwords and
               // at the other because a client cannot negotiate a cipher, and
               // whichever they wanted, half the page was noise. Splitting it also
               // lets each half sit in the group where its subject lives - this one
               // beside IP ranges, which is where the ban it creates ends up.
               Page("autoban", "Auto-ban",
                  "Lock out an address that keeps failing to log on - and let one back in that should not have been locked out.",
                  aliases: "Auto-ban & SSL/TLS|Autoban|Brute force|Bruteforce|Lockout|Locked out|Failed logons|Failed logins|Password guessing|Dictionary attack|Fail2ban|Hammering|Temporary ban|Logon failure list",
                  // "tls" is here, and "autoban" is on the SSL/TLS page, so a
                  // reader who followed an old note about the combined page and
                  // wanted the other half has a way across. Only the first three
                  // are drawn beside the breadcrumb, and the order puts the escape
                  // hatch inside that cut on this page rather than on the other
                  // one, because this is the half the documented path resolves to.
                  seeAlso: "ipranges|authentication|tls|logs"),

               Page("ipranges", "IP ranges",
                  "Which addresses may connect, which may relay without authenticating, and which must authenticate first.",
                  aliases: "IP range|Firewall|Block an IP|Allow an IP|Open relay|Relay permissions|Require authentication|Let a device send mail|Printer|Scanner|LAN|Localhost|My IP",
                  seeAlso: "authentication|autoban|relays|delivery")),

            Group("Wrench24", "Maintenance",
               "Housekeeping, tuning, and the settings that still have no better home.",
               "Housekeeping|Backup|Tuning|Advanced|Scripting|INI",

               Page("backup", "Backup & restore",
                  "Back up or restore the configuration, the domains and the messages.",
                  aliases: "Backup|Restore|Export|Import|Disaster recovery|Copy to another server|Migrate",
                  seeAlso: "hardening|domains"),

               Page("performance", "Performance",
                  "Thread counts, database connections and caching - what to change when the server is slow.",
                  aliases: "Threads|Workers|Database connections|Cache|Slow server|Tuning|Concurrency|Memory",
                  seeAlso: "hardening|logging"),

               // Was "Advanced & scripting", which named this page after the one thing
               // it shares with its neighbour: the engine's on/off switch lives here,
               // the script itself is edited on "Event scripts". Two names for adjacent
               // concepts, one of them a catch-all - so this one is now what the page
               // calls itself, and its description says where the switch is. The old
               // title stays as an alias; the key is unchanged, so links still arrive.
               Page("advanced", "Advanced",
                  "Archiving, mirroring, the default domain, disk-space limits, and the switch that turns the event-scripting engine on.",
                  aliases: "Advanced & scripting|Archive|Archiving|Mirror|Default domain|Scripting engine|Enable scripting|VBScript|JScript|IPv6 preference",
                  seeAlso: "scripts|hardening"),

               Page("scripts", "Event scripts",
                  "Edit the event-handler script the server runs on delivery, logon and error events. The engine itself is switched on under Advanced.",
                  aliases: "EventHandlers|Scripts|OnDeliverMessage|OnClientLogon|OnAcceptMessage|VBScript|Run code on mail",
                  seeAlso: "advanced|rules"),

               // Renamed from "Advanced INI settings", which named the page after
               // the file its values are stored in. Everything on it that belonged
               // to a feature with a page of its own has moved to that page; what
               // is left really is server-wide, and the title now says so. Both old
               // titles stay as aliases, and the key is unchanged, so every link,
               // bookmark and remembered path still arrives here.
               Page("hardening", "Server limits & expert settings",
                  "Server-wide ceilings, durability and abuse controls that belong to no single protocol or feature.",
                  aliases: "Advanced INI settings|Advanced hardening|INI settings|hMailServer.INI|Registry|Undocumented|Expert settings|Everything else|" +
                           "Timeouts|Queue bounds|Sending limits|Rate limit|Throttle|fsync|Durability|DPAPI|Received headers|mailer-daemon",
                  seeAlso: "advanced|performance|backup")),

            Page("about", "About",
               "Version, build, licence and where to get support.",
               aliases: "Version|Build|Licence|License|Support|Credits|Copyright")
         };
      }
   }
}
