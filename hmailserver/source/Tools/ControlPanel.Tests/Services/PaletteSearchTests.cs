using System;
using System.Collections.Generic;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The palette has to answer intent, not just names.
   ///
   /// Against the unfixed code every test here fails, and most of them fail for a
   /// reason worth naming rather than merely because PaletteSearch did not exist:
   /// the old palette matched page names by character subsequence and setting
   /// labels by substring, so no query that described an outcome could match
   /// anything at all. "stop spam" returned nothing, because no page title and no
   /// setting label contains the word "stop" - which made the single most common
   /// reason for opening the application unsearchable.
   /// </summary>
   public class PaletteSearchTests
   {
      /// <summary>
      /// The four phrases the owner named. Each must put its answer in the first
      /// selectable row - not merely somewhere in the list, because a palette that
      /// has the right answer fourth still costs a decision.
      ///
      /// Fails against the unfixed code: the previous search matched none of these.
      /// </summary>
      [Theory]
      [InlineData("stop spam", "antispam")]
      [InlineData("block an IP", "ipranges")]
      [InlineData("let a device send mail", "ipranges")]
      [InlineData("why is mail queued", "queue")]
      public void OutcomePhrase_LandsOnTheRightPageFirst(string query, string page)
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query(query, null, null);

         PaletteRow first = FirstSelectable(rows);
         Assert.True(first != null, "'" + query + "' matched nothing at all.");
         Assert.Equal(page, first.Page);
      }

      /// <summary>
      /// Every intent has to lead somewhere that exists.
      ///
      /// An IntentIndex entry naming a page key that NavigationMap does not have is
      /// worse than a missing intent: the palette offers the row, the user picks it, and
      /// nothing happens - or the application navigates to nothing. It is also the
      /// easiest mistake to make here, because the phrases and the page keys are written
      /// in different files by hand, and nothing but this test connects them. Fails the
      /// moment a page is renamed without its intents being updated.
      /// </summary>
      [Fact]
      public void EveryIntentPointsAtAPageThatExists()
      {
         var missing = IntentIndex.Entries
            .Where(entry => NavigationMap.Find(entry.Page) == null)
            .Select(entry => "'" + entry.Phrase + "' -> " + entry.Page)
            .ToList();

         Assert.True(missing.Count == 0,
            "These intents name pages that do not exist in NavigationMap: "
            + string.Join("; ", missing));
      }

      /// <summary>
      /// A verb plus a noun must not return less than the noun alone.
      ///
      /// Two words is the commonest thing anybody types, and it was the one length the
      /// partial rule could never fire for: a two-token query needed two matches to
      /// reach it, and two matches is all of them, which the all-tokens branch had
      /// already answered. So "spam" worked and "reduce spam" showed "Nothing matched" -
      /// and a palette that punishes a user for being more specific teaches them not to
      /// use it. These are the pairs measured on the unfixed build; every one of them
      /// returned nothing.
      /// </summary>
      [Theory]
      [InlineData("reduce spam")]
      [InlineData("enable imap")]
      [InlineData("configure imap")]
      [InlineData("disable pop3")]
      [InlineData("clear queue")]
      [InlineData("flush queue")]
      [InlineData("import certificate")]
      [InlineData("change ports")]
      [InlineData("new domain")]
      [InlineData("add route")]
      [InlineData("schedule backup")]
      [InlineData("disable greylisting")]
      public void VerbPlusNoun_FindsAtLeastWhatTheNounAloneFinds(string query)
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query(query, null, null);

         Assert.True(FirstSelectable(rows) != null,
            "'" + query + "' matched nothing, while its noun alone does. Adding an "
            + "ordinary verb must never take results away.");
      }

      /// <summary>
      /// The other half of that rule. One matched word out of two is accepted only when
      /// the word that did NOT match is an action verb, so a verb paired with a word
      /// that appears nowhere still finds nothing - and, more importantly, two topical
      /// words still both have to match: "spam ports" must not return the ports page,
      /// which is what SearchTermsTests.Score_RefusesAMatchOnTooLittleOfTheQuery pins.
      /// </summary>
      [Theory]
      [InlineData("add zzzzqqq")]
      [InlineData("new zzzzqqq")]
      [InlineData("set zzzzqqq")]
      public void VerbPlusNonsense_MatchesNothing(string query)
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query(query, null, null);

         Assert.True(FirstSelectable(rows) == null,
            "'" + query + "' matched something. A three-letter verb plus a word that "
            + "appears nowhere must not be a wildcard.");
      }

      /// <summary>
      /// Phrasing is not a memory test. The synonym folding in SearchTerms has to
      /// carry the obvious rewordings, or the intent table would need every
      /// variant spelled out and would still miss the one a given person used.
      /// </summary>
      [Theory]
      [InlineData("mail is stuck", "queue")]
      [InlineData("why is mail stuck", "queue")]
      [InlineData("stop junk mail", "antispam")]
      [InlineData("ban an ip address", "ipranges")]
      [InlineData("let the printer send email", "ipranges")]
      [InlineData("add a user", "domains")]
      [InlineData("add a mailbox", "domains")]
      [InlineData("my ssl certificate expired", "certs")]
      [InlineData("the server is slow", "performance")]
      [InlineData("someone is guessing passwords", "tls")]
      public void RewordedOutcomePhrase_StillFindsThePage(string query, string page)
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query(query, null, null);

         Assert.Contains(page, rows.Where(r => r.IsSelectable).Take(4).Select(r => r.Page));
      }

      /// <summary>
      /// Every outcome phrase in the table has to actually work. An intent entry
      /// that does not reach its own page is worse than no entry: it looks like
      /// coverage in review and does nothing for the user. Adding a phrase is
      /// meant to be the cheapest possible response to "I could not find X", and
      /// this is what makes a careless addition fail loudly.
      /// </summary>
      [Fact]
      public void EveryOutcomePhrase_ReachesItsOwnPage()
      {
         var broken = new List<string>();

         foreach (IntentEntry intent in IntentIndex.Entries)
         {
            if (NavigationMap.Find(intent.Page) == null)
            {
               broken.Add("\"" + intent.Phrase + "\" points at '" + intent.Page + "', which is not a page");
               continue;
            }

            List<PaletteRow> selectable = PaletteSearch.Query(intent.Phrase, null, null)
               .Where(r => r.IsSelectable)
               .Take(3)
               .ToList();

            if (!selectable.Any(r => string.Equals(r.Page, intent.Page, StringComparison.OrdinalIgnoreCase)))
            {
               broken.Add("\"" + intent.Phrase + "\" does not reach " + intent.Page +
                          " (top results: " + string.Join(", ", selectable.Select(r => r.Page)) + ")");
            }
         }

         Assert.True(broken.Count == 0, string.Join("; ", broken));
      }

      /// <summary>
      /// Two phrases normalising to the same text would make one of them
      /// unreachable, and which one wins would depend on table order.
      /// </summary>
      [Fact]
      public void OutcomePhrases_AreUniqueAndExplainThemselves()
      {
         var seen = new Dictionary<string, string>(StringComparer.Ordinal);

         foreach (IntentEntry intent in IntentIndex.Entries)
         {
            string normalized = SearchTerms.Normalize(intent.Phrase);

            Assert.False(seen.ContainsKey(normalized),
               "\"" + intent.Phrase + "\" and \"" + (seen.TryGetValue(normalized, out string other) ? other : "") +
               "\" are the same phrase once normalised.");
            seen[normalized] = intent.Phrase;

            Assert.False(string.IsNullOrWhiteSpace(intent.Answer),
               "\"" + intent.Phrase + "\" does not say what the page will let the user do.");

            // A phrase that is only a noun is a name, and names are already
            // searched. The value of this table is the phrasing.
            Assert.True(normalized.Split(' ').Length >= 2,
               "\"" + intent.Phrase + "\" is a single word; page aliases cover names.");
         }
      }

      /// <summary>
      /// Typing a page name has to open that page, whatever else matched. This is
      /// the case the old palette did serve, and reorganising the ranking must not
      /// break it.
      /// </summary>
      [Fact]
      public void EveryPage_IsReachedByTypingItsTitle()
      {
         var broken = new List<string>();

         foreach (NavNode page in NavigationMap.Pages)
         {
            PaletteRow first = FirstSelectable(PaletteSearch.Query(page.Title, null, null));

            if (first == null || !string.Equals(first.Page, page.Key, StringComparison.OrdinalIgnoreCase))
               broken.Add("\"" + page.Title + "\" opened '" + (first?.Page ?? "nothing") + "'");
         }

         Assert.True(broken.Count == 0, string.Join("; ", broken));
      }

      /// <summary>
      /// The "Advanced hardening" rename is the one the roadmap called out: the
      /// page was renamed by hand, and every existing note naming the old title
      /// stopped working. Aliases are how a rename stops costing that.
      /// </summary>
      [Theory]
      [InlineData("Advanced hardening", "hardening")]
      [InlineData("hMailServer.INI", "hardening")]
      [InlineData("Anti-spam white list", "spamwhitelist")]
      [InlineData("DNS blacklists (DNSBL)", "dnsbl")]
      [InlineData("MX-query", "mxquery")]
      [InlineData("Auto-ban", "tls")]
      [InlineData("Let's Encrypt", "acme")]
      [InlineData("Shared folders", "publicfolders")]
      public void LegacyOrAlternativeName_StillFindsThePage(string query, string page)
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query(query, null, null);

         Assert.Contains(page, rows.Where(r => r.IsSelectable).Take(3).Select(r => r.Page));
      }

      /// <summary>
      /// A documented navigation path from an older release must still land the
      /// reader somewhere useful. Typing it is exactly what someone following
      /// written instructions does when the path is not there any more.
      /// </summary>
      [Theory]
      [InlineData("Settings > Anti-spam > SURBL servers", "surbl")]
      [InlineData("Settings > Advanced > IP ranges", "ipranges")]
      [InlineData("Utilities > MX-query", "mxquery")]
      public void LegacyNavigationPath_TypedVerbatim_FindsThePage(string query, string page)
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query(query, null, null);

         Assert.Contains(page, rows.Where(r => r.IsSelectable).Take(3).Select(r => r.Page));
      }

      /// <summary>
      /// A group name is a reasonable thing to type even though a group is not
      /// navigable, so it has to offer what is inside it.
      /// </summary>
      [Fact]
      public void GroupName_OffersThePagesInThatGroup()
      {
         List<string> pages = PaletteSearch.Query("TLS & certificates", null, null)
            .Where(r => r.IsSelectable)
            .Select(r => r.Page)
            .ToList();

         Assert.Contains("certs", pages);
         Assert.Contains("acme", pages);
         Assert.Contains("security", pages);
      }

      /// <summary>
      /// The specific improvement the roadmap asked for: results must say which
      /// page a setting lives on. Without it, picking a setting teleports the user
      /// somewhere unexplained and the palette has to be used again next time.
      ///
      /// Fails against the unfixed code, which displayed "label - page title" with
      /// no notion of where that page sat.
      /// </summary>
      [Fact]
      public void SettingResult_NamesThePageAndTheGroupItLivesOn()
      {
         PaletteRow row = PaletteSearch.Query("LogDeleteDays", null, null)
            .FirstOrDefault(r => r.Kind == PaletteRowKind.Setting);

         Assert.True(row != null, "The palette no longer finds a setting by its INI key.");
         Assert.Equal("logging", row.Page);
         Assert.Contains("Logging", row.Location);
         Assert.Contains(NavigationMap.GroupOf("logging"), row.Location);
         Assert.Contains("LogDeleteDays", row.Detail);
      }

      /// <summary>Every page row names the group it is in, and every task row names its destination.</summary>
      [Fact]
      public void EveryRow_SaysWhereItGoes()
      {
         foreach (PaletteRow row in PaletteSearch.Query("tls", null, null).Where(r => r.IsSelectable))
         {
            Assert.False(string.IsNullOrWhiteSpace(row.Page));
            Assert.NotNull(NavigationMap.Find(row.Page));

            if (row.Kind == PaletteRowKind.Page && NavigationMap.GroupOf(row.Page).Length > 0)
               Assert.Equal(NavigationMap.GroupOf(row.Page), row.Location);
            else if (row.Kind != PaletteRowKind.Page)
               Assert.Contains(NavigationMap.TitleOf(row.Page), row.Location);
         }
      }

      /// <summary>
      /// The palette is opened to go back somewhere far more often than to
      /// discover something, so an empty query has to offer that first.
      /// </summary>
      [Fact]
      public void EmptyQuery_OffersRecentlyVisitedFirst()
      {
         var usage = new PaletteUsage();
         usage.RecordVisit("logging");
         usage.RecordVisit("queue");
         usage.RecordVisit("antispam");

         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", usage, "antispam");

         Assert.Equal(PaletteSearch.RecentSection, rows[0].Section);
         Assert.Equal(PaletteRowKind.Header, rows[0].Kind);

         // "antispam" is the page the user is looking at, so it is not a shortcut
         // back to anywhere; "queue" is the last page they were actually on.
         List<string> recent = rows.Where(r => r.Section == PaletteSearch.RecentSection && r.IsSelectable)
            .Select(r => r.Page).ToList();
         Assert.Equal(new[] { "queue", "logging" }, recent);
      }

      /// <summary>
      /// "Most used" earns its place only when it says something "Recently
      /// visited" does not: the pages this installation lives in, even after a
      /// session spent looking at something else. Here two heavily-used pages have
      /// been pushed out of the recent window by eight other visits, and they must
      /// still be offered - which is precisely the case a single recency list
      /// cannot serve.
      /// </summary>
      [Fact]
      public void EmptyQuery_OffersMostUsedAndThenEveryPage()
      {
         var usage = new PaletteUsage();
         for (int i = 0; i < 5; i++)
         {
            usage.RecordVisit("queue");
            usage.RecordVisit("logs");
         }

         foreach (string key in new[] { "domains", "rules", "protocols", "delivery",
                                        "routes", "publicfolders", "antispam", "surbl" })
         {
            usage.RecordVisit(key);
         }

         Assert.DoesNotContain("queue", usage.Recent);

         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", usage, null);
         var sections = rows.Where(r => r.Kind == PaletteRowKind.Header).Select(r => r.Section).ToList();

         Assert.Equal(new[] { PaletteSearch.RecentSection, PaletteSearch.MostUsedSection, PaletteSearch.PageSection },
            sections);

         List<string> mostUsed = rows.Where(r => r.Section == PaletteSearch.MostUsedSection && r.IsSelectable)
            .Select(r => r.Page).ToList();
         Assert.Contains("queue", mostUsed);
         Assert.Contains("logs", mostUsed);

         // Every page stays listed: the unfiltered palette is the only place that
         // shows the shape of the application.
         List<string> pages = rows.Where(r => r.Section == PaletteSearch.PageSection && r.IsSelectable)
            .Select(r => r.Page).ToList();
         Assert.Equal(NavigationMap.Pages.Select(p => p.Key), pages);
      }

      /// <summary>
      /// A shortcut list that repeats the same four pages twice wastes the only
      /// part of the palette a user sees without typing.
      /// </summary>
      [Fact]
      public void EmptyQuery_DoesNotRepeatAPageBetweenShortcutSections()
      {
         var usage = new PaletteUsage();
         usage.RecordVisit("queue");
         usage.RecordVisit("logs");
         usage.RecordVisit("domains");

         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", usage, null);

         List<string> shortcuts = rows
            .Where(r => r.IsSelectable && r.Section != PaletteSearch.PageSection)
            .Select(r => r.Page)
            .ToList();

         Assert.Equal(shortcuts.Count, shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).Count());
      }

      /// <summary>
      /// The history lives in the registry and outlives releases, so it can name a
      /// page that no longer exists. Offering it would produce a row that
      /// navigates nowhere.
      /// </summary>
      [Fact]
      public void EmptyQuery_IgnoresHistoryForPagesThatNoLongerExist()
      {
         PaletteUsage usage = PaletteUsage.Deserialize("aPageWeDeleted=9,queue=1|aPageWeDeleted,queue");

         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", usage, null);

         Assert.DoesNotContain("aPageWeDeleted", rows.Select(r => r.Page ?? ""));
         Assert.All(rows.Where(r => r.IsSelectable), r => Assert.NotNull(NavigationMap.Find(r.Page)));
      }

      /// <summary>
      /// Pages that tie on match quality are ordered by how often this
      /// administrator actually opens them. Typing the group name scores every
      /// page in the group identically except the one whose own title matched, so
      /// "Certificates (ACME)" and "Transport security" tie exactly - and which of
      /// them a given administrator wants is not something the text can tell us,
      /// but their history can.
      /// </summary>
      [Fact]
      public void TiedResults_PreferThePagesThisAdministratorUses()
      {
         const string query = "TLS & certificates";

         List<PaletteRow> cold = PaletteSearch.Query(query, null, null)
            .Where(r => r.Kind == PaletteRowKind.Page).ToList();

         Assert.Contains("acme", cold.Select(r => r.Page));
         Assert.Contains("security", cold.Select(r => r.Page));
         Assert.Equal(cold.Single(r => r.Page == "acme").Score, cold.Single(r => r.Page == "security").Score);

         List<string> coldOrder = cold.Select(r => r.Page).ToList();
         Assert.True(coldOrder.IndexOf("acme") < coldOrder.IndexOf("security"),
            "With nothing known about the user, tied rows should fall back to title order.");

         var usage = new PaletteUsage();
         for (int i = 0; i < 3; i++)
         {
            usage.RecordVisit("security");
            usage.RecordVisit("about");
         }

         List<string> warmOrder = PaletteSearch.Query(query, usage, null)
            .Where(r => r.Kind == PaletteRowKind.Page)
            .Select(r => r.Page)
            .ToList();

         Assert.Equal(coldOrder.OrderBy(p => p, StringComparer.Ordinal),
            warmOrder.OrderBy(p => p, StringComparer.Ordinal));
         Assert.True(warmOrder.IndexOf("security") < warmOrder.IndexOf("acme"),
            "The page this administrator actually uses should win a tie.");
      }

      /// <summary>
      /// Section captions are rows in the same list as the results, which is what
      /// keeps the list a test can reason about identical to the list the user
      /// sees. That makes them a keyboard hazard: the selection must never land on
      /// one, or Enter silently does nothing.
      /// </summary>
      [Fact]
      public void SectionCaptions_AreNotSelectable()
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", new PaletteUsage(), null);

         Assert.Contains(rows, r => r.Kind == PaletteRowKind.Header);
         Assert.All(rows.Where(r => r.Kind == PaletteRowKind.Header), r =>
         {
            Assert.False(r.IsSelectable);
            Assert.Null(r.Page);
         });
      }

      [Fact]
      public void FirstSelectable_SkipsTheLeadingCaption()
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", new PaletteUsage(), null);

         int first = PaletteSearch.FirstSelectable(rows);

         Assert.Equal(PaletteRowKind.Header, rows[0].Kind);
         Assert.Equal(1, first);
         Assert.True(rows[first].IsSelectable);
      }

      /// <summary>
      /// Moving down through a section boundary must step over the caption in one
      /// key press. Incrementing an index would land on it and leave the palette
      /// looking broken.
      /// </summary>
      [Fact]
      public void ArrowKeys_StepOverSectionCaptions()
      {
         var usage = new PaletteUsage();
         usage.RecordVisit("queue");
         usage.RecordVisit("logs");

         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", usage, null);

         // Walk the whole list the way the palette's Down key does and assert the
         // selection is never on a caption.
         int index = PaletteSearch.FirstSelectable(rows);
         int visited = 0;

         while (true)
         {
            Assert.True(rows[index].IsSelectable, "The selection landed on the caption '" + rows[index].Title + "'.");
            visited++;

            int next = PaletteSearch.NextSelectable(rows, index, 1);
            if (next == index)
               break;

            index = next;
         }

         Assert.Equal(rows.Count(r => r.IsSelectable), visited);
         Assert.Equal(PaletteSearch.LastSelectable(rows), index);
      }

      /// <summary>
      /// The ends of the list are walls, not a wrap-around: wrapping in a search
      /// palette moves the highlight to the far end of the list without the user
      /// asking, and they lose their place.
      /// </summary>
      [Fact]
      public void ArrowKeys_ClampAtBothEnds()
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("", new PaletteUsage(), null);

         int first = PaletteSearch.FirstSelectable(rows);
         int last = PaletteSearch.LastSelectable(rows);

         Assert.Equal(first, PaletteSearch.NextSelectable(rows, first, -1));
         Assert.Equal(last, PaletteSearch.NextSelectable(rows, last, 1));
      }

      [Fact]
      public void NoResults_LeavesNothingSelectable()
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("zzqqxx", null, null);

         Assert.Empty(rows);
         Assert.Equal(-1, PaletteSearch.FirstSelectable(rows));
         Assert.Equal(-1, PaletteSearch.LastSelectable(rows));
         Assert.Equal(0, PaletteSearch.NextSelectable(rows, 0, 1));
      }

      /// <summary>Nobody types our punctuation, and half of it cannot be typed on a phone keyboard at all.</summary>
      [Theory]
      [InlineData("STOP SPAM!")]
      [InlineData("  stop   spam  ")]
      [InlineData("stop-spam")]
      public void Matching_IgnoresCasePunctuationAndSpacing(string query)
      {
         PaletteRow first = FirstSelectable(PaletteSearch.Query(query, null, null));

         Assert.True(first != null, "'" + query + "' matched nothing.");
         Assert.Equal("antispam", first.Page);
      }

      /// <summary>
      /// The purpose statements are searchable, which is what the roadmap meant by
      /// "a searchable description is far more useful than a searchable
      /// identifier". DNSSEC appears in no page title and no setting label - only
      /// in the DNS resolver's purpose line.
      /// </summary>
      [Fact]
      public void PurposeStatements_AreSearchable()
      {
         List<string> pages = PaletteSearch.Query("DNSSEC validated", null, null)
            .Where(r => r.IsSelectable)
            .Select(r => r.Page)
            .ToList();

         Assert.Contains("dns", pages);
      }

      /// <summary>
      /// Typing initials for a page you already know is a habit worth keeping; it
      /// is the one thing the old subsequence matcher was good at.
      /// </summary>
      [Fact]
      public void Initials_StillFindAPage()
      {
         Assert.Contains("queue", PaletteSearch.Query("dq", null, null)
            .Where(r => r.IsSelectable).Take(6).Select(r => r.Page));
      }

      /// <summary>
      /// A setting still outranks nothing but is never allowed to outrank the page
      /// whose name was typed - a query matching thirty settings and one page title
      /// almost always means the page.
      /// </summary>
      [Fact]
      public void PageName_OutranksSettingsThatShareTheWord()
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query("Logging", null, null);

         PaletteRow first = FirstSelectable(rows);
         Assert.NotNull(first);
         Assert.Equal(PaletteRowKind.Page, first.Kind);
         Assert.Equal("logging", first.Page);

         // ...and the settings are still offered, further down.
         Assert.Contains(rows, r => r.Kind == PaletteRowKind.Setting);
      }

      /// <summary>The previous palette's cap on setting results is unchanged.</summary>
      [Fact]
      public void SettingResults_AreCapped()
      {
         int settings = PaletteSearch.Query("a", null, null).Count(r => r.Kind == PaletteRowKind.Setting);

         Assert.True(settings <= PaletteSearch.MaxSettings,
            $"{settings} setting rows; the cap is {PaletteSearch.MaxSettings}.");
      }

      [Fact]
      public void TaskResults_AreCappedAndNeverRepeatADestination()
      {
         List<PaletteRow> tasks = PaletteSearch.Query("mail", null, null)
            .Where(r => r.Kind == PaletteRowKind.Task).ToList();

         Assert.True(tasks.Count <= PaletteSearch.MaxTasks,
            $"{tasks.Count} task rows; the cap is {PaletteSearch.MaxTasks}.");
         Assert.Equal(tasks.Count, tasks.Select(r => r.Page).Distinct(StringComparer.OrdinalIgnoreCase).Count());
      }

      /// <summary>
      /// A query is scored once and reused across every source. This is the reason
      /// page names and setting labels are finally comparable, so it is worth
      /// pinning that both entry points agree.
      /// </summary>
      [Fact]
      public void SettingsIndex_AndPalette_UseTheSameMatcher()
      {
         var query = new SearchQuery("Delete logs older");

         List<string> viaQuery = SettingsSearchIndex.Search(query).Select(m => m.Page).ToList();
         List<string> viaString = SettingsSearchIndex.Search("Delete logs older").Select(m => m.Page).ToList();

         Assert.Equal(viaString, viaQuery);
         Assert.Equal("logging", viaQuery.First());
      }

      /// <summary>
      /// A setting matched only on its identifier ranks below one matched on the
      /// words an administrator can actually see.
      /// </summary>
      [Fact]
      public void LabelMatch_OutranksKeyMatch()
      {
         List<SettingMatch> matches = SettingsSearchIndex.Search("greylisting").ToList();

         Assert.NotEmpty(matches);

         SettingMatch byLabel = matches.FirstOrDefault(m =>
            m.Label.IndexOf("greylisting", StringComparison.OrdinalIgnoreCase) >= 0);
         SettingMatch byKeyOnly = matches.FirstOrDefault(m =>
            m.Label.IndexOf("greylisting", StringComparison.OrdinalIgnoreCase) < 0);

         if (byLabel != null && byKeyOnly != null)
            Assert.True(byLabel.Rank < byKeyOnly.Rank, "A key-only match outranked a visible-label match.");
      }

      private static PaletteRow FirstSelectable(IReadOnlyList<PaletteRow> rows)
      {
         int index = PaletteSearch.FirstSelectable(rows);
         return index < 0 ? null : rows[index];
      }
   }
}
