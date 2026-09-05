// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The navigation was reorganised around tasks instead of around the classic
   /// Administrator tree. A reorganisation can break two things that a build
   /// cannot notice, and both of them are worse than the layout that was
   /// replaced: a page can stop being reachable, and a page title that appears
   /// in the manual can be quietly renamed. These tests are what makes those two
   /// failures loud.
   ///
   /// Every test here fails against the unfixed code, most of them because
   /// NavigationMap did not exist: the tree was 60 lines of nested Item/Group
   /// calls inside MainWindow, which the test project cannot reference (it builds
   /// against ControlPanel.Core, which has no WPF) and which nothing could
   /// verify. That is the defect these tests exist to keep fixed - the structure
   /// has to be data before it can be checked at all.
   /// </summary>
   public class NavigationMapTests
   {
      /// <summary>
      /// Every page title as it stands, pinned. hMailServer's page names appear
      /// in the manual, in years of forum answers and in customers' own runbooks,
      /// so the reorganisation moved pages and changed nothing they are called.
      /// A failure here means a title was changed: either revert it, or accept
      /// that every existing instruction naming that page is now wrong.
      ///
      /// One title HAS changed, and it is the only one: "Auto-ban &amp; SSL/TLS"
      /// became "SSL/TLS" when the page was split in two, because a title naming
      /// two subjects cannot survive on a page holding one of them. That is the
      /// only reason to edit a row here, and it costs the three things
      /// PageSplitTests pins - the old title as an alias on both halves, a legacy
      /// path that still resolves, and each half signposting the other.
      ///
      /// Fails against the unfixed code because NavigationMap does not exist
      /// there; from now on it fails whenever a title moves.
      /// </summary>
      [Theory]
      [InlineData("welcome", "Welcome")]
      [InlineData("dashboard", "Dashboard")]
      [InlineData("status", "Server status")]
      [InlineData("queue", "Delivery queue")]
      [InlineData("logs", "Live logs")]
      [InlineData("domains", "Domains")]
      [InlineData("rules", "Rules")]
      [InlineData("protocols", "Protocols")]
      [InlineData("delivery", "Delivery of e-mail")]
      [InlineData("routes", "Routes")]
      [InlineData("publicfolders", "Public folders")]
      [InlineData("spamoverview", "Spam filtering overview")]
      [InlineData("antispam", "Anti-spam settings")]
      [InlineData("surbl", "SURBL servers")]
      [InlineData("dnsbl", "DNS blacklists")]
      [InlineData("spamwhitelist", "White list")]
      [InlineData("greylistwhitelist", "Greylisting white list")]
      [InlineData("antivirus", "Anti-virus settings")]
      [InlineData("blockedattachments", "Blocked attachments")]
      [InlineData("logging", "Logging")]
      [InlineData("tls", "SSL/TLS")]
      [InlineData("autoban", "Auto-ban")]
      [InlineData("performance", "Performance")]
      [InlineData("advanced", "Advanced")]
      [InlineData("hardening", "Server limits & expert settings")]
      [InlineData("scripts", "Event scripts")]
      [InlineData("servermessages", "Server messages")]
      [InlineData("groups", "Groups")]
      [InlineData("authentication", "Authentication")]
      [InlineData("adminaccess", "Administrative access")]
      [InlineData("ipranges", "IP ranges")]
      [InlineData("certs", "SSL certificates")]
      [InlineData("security", "Transport security")]
      [InlineData("acme", "Certificates (ACME)")]
      [InlineData("ports", "TCP/IP ports")]
      [InlineData("relays", "Incoming relays")]
      [InlineData("dns", "DNS resolver")]
      [InlineData("webservices", "Web services & autoconfiguration")]
      [InlineData("api", "API & monitoring")]
      [InlineData("backup", "Backup & restore")]
      [InlineData("mxquery", "MX query")]
      [InlineData("sendout", "Server sendout")]
      [InlineData("diagnostics", "Diagnostics")]
      [InlineData("about", "About")]
      public void PageTitle_IsUnchangedByTheReorganisation(string key, string title)
      {
         NavNode page = NavigationMap.Find(key);

         Assert.True(page != null, "'" + key + "' is not in the navigation map, so the page is unreachable.");
         Assert.Equal(title, page.Title);
      }

      /// <summary>
      /// The reorganisation is a change of routes, not of pages, so the set of
      /// pages the navigation offers and the set MainWindow can build must be
      /// identical in both directions. A page in MainWindow but not here is
      /// unreachable; a page here but not in MainWindow selects a tree node that
      /// shows nothing.
      /// </summary>
      [Fact]
      public void NavigationPages_AreExactlyThePagesMainWindowRegisters()
      {
         string mainWindow = ReadMainWindowSource();
         if (mainWindow == null)
            return;   // sources not available (e.g. running from a packaged drop)

         var registered = new HashSet<string>(
            Regex.Matches(mainWindow, "pageFactories_\\[\"([^\"]+)\"\\]").Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);

         // If the scan stops matching, everything below would pass vacuously.
         Assert.True(registered.Count >= 40,
            $"Only found {registered.Count} page factories in MainWindow; the scan needs updating.");

         var mapped = new HashSet<string>(NavigationMap.Pages.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);

         List<string> unreachable = registered.Where(k => !mapped.Contains(k)).OrderBy(k => k).ToList();
         Assert.True(unreachable.Count == 0,
            "These pages exist but the navigation has nowhere to reach them from: " + string.Join(", ", unreachable));

         List<string> phantom = mapped.Where(k => !registered.Contains(k)).OrderBy(k => k).ToList();
         Assert.True(phantom.Count == 0,
            "The navigation offers these pages but MainWindow cannot build them, so selecting them shows nothing: " +
            string.Join(", ", phantom));
      }

      /// <summary>
      /// A page with two homes makes the breadcrumb, the palette's location line
      /// and tree selection disagree, each silently picking whichever it happened
      /// to find first. NavigationMap throws on a duplicate key at start-up, so
      /// this reaching the assertion at all means the guard works.
      /// </summary>
      [Fact]
      public void EveryPage_HasExactlyOneHome()
      {
         List<string> duplicates = NavigationMap.Pages
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

         Assert.True(duplicates.Count == 0, "Pages listed more than once: " + string.Join(", ", duplicates));
         Assert.Equal(NavigationMap.Pages.Count, NavigationMap.Pages.Select(p => p.Title).Distinct().Count());
      }

      /// <summary>
      /// The defect being fixed, stated as an assertion. The old tree put 30 of
      /// 42 pages under one group called "Settings" - a name that means "we have
      /// not decided what these are" - and reached three levels deep to do it.
      /// Both are easy to reintroduce one page at a time.
      /// </summary>
      [Fact]
      public void NavigationIsGroupedByTask_NotByAnUndifferentiatedSettingsBucket()
      {
         var buckets = new[] { "settings", "advanced", "misc", "miscellaneous", "other", "general", "configuration" };

         List<string> offenders = NavigationMap.Groups
            .Where(g => buckets.Contains(g.Title.Trim().ToLowerInvariant()))
            .Select(g => g.Title)
            .ToList();

         Assert.True(offenders.Count == 0,
            "These group headings name our storage layout rather than the administrator's task: " +
            string.Join(", ", offenders));

         // Depth: a page is a group and a click. Anything deeper is the old tree
         // growing back.
         foreach (NavNode page in NavigationMap.Pages)
         {
            int depth = NavigationMap.PathTo(page.Key).Count;
            Assert.True(depth <= 2,
               $"'{page.Title}' is {depth} levels deep ({NavigationMap.LocationOf(page.Key)}); the navigation is capped at two.");
         }

         // A group holding one page is not a category, it is an extra click.
         foreach (NavNode group in NavigationMap.Groups)
         {
            Assert.True(group.Children.Count >= 2,
               $"The group '{group.Title}' holds {group.Children.Count} page(s); that is a click, not a category.");
         }
      }

      /// <summary>
      /// "A page whose title needs the manual has failed." Every page carries a
      /// one-line purpose, which becomes its tool tip, its accessible help text
      /// and a searchable description in the palette. An empty one silently
      /// removes a page from purpose-based search.
      /// </summary>
      [Fact]
      public void EveryPageAndGroup_SaysWhatItIsFor()
      {
         foreach (NavNode node in NavigationMap.Pages.Concat(NavigationMap.Groups))
         {
            Assert.False(string.IsNullOrWhiteSpace(node.Purpose),
               "'" + node.Title + "' has no purpose statement, so it can only be found by name.");
            Assert.True(node.Purpose.Length >= 20,
               "'" + node.Title + "' has a purpose statement too short to say anything: " + node.Purpose);
         }
      }

      /// <summary>The breadcrumb needs the trail; an unknown key must produce nothing to draw.</summary>
      [Fact]
      public void PathTo_GivesTheTrailFromTheGroupToThePage()
      {
         IReadOnlyList<NavNode> trail = NavigationMap.PathTo("hardening");

         Assert.Equal(2, trail.Count);
         Assert.Equal("Maintenance", trail[0].Title);
         Assert.Equal("Server limits & expert settings", trail[1].Title);
         Assert.Equal("Maintenance > Server limits & expert settings", NavigationMap.LocationOf("hardening"));
         Assert.Equal("Maintenance", NavigationMap.GroupOf("hardening"));
      }

      [Fact]
      public void PathTo_TopLevelPage_IsJustThePage()
      {
         Assert.Equal(new[] { "Dashboard" }, NavigationMap.PathTo("dashboard").Select(n => n.Title));
         Assert.Equal("", NavigationMap.GroupOf("dashboard"));
      }

      [Fact]
      public void PathTo_UnknownPage_IsEmptyRatherThanThrowing()
      {
         Assert.Empty(NavigationMap.PathTo("a-page-that-was-removed"));
         Assert.Empty(NavigationMap.PathTo(null));
         Assert.Equal("", NavigationMap.LocationOf("a-page-that-was-removed"));
      }

      /// <summary>
      /// An unknown key shows as itself rather than as a blank, so a stale
      /// reference is visible in the interface instead of rendering an empty row
      /// that nobody can report usefully.
      /// </summary>
      [Fact]
      public void TitleOf_UnknownPage_FallsBackToTheKey()
      {
         Assert.Equal("a-page-that-was-removed", NavigationMap.TitleOf("a-page-that-was-removed"));
         Assert.Equal("", NavigationMap.TitleOf(null));
      }

      /// <summary>
      /// Documentation, forum answers and customers' runbooks all name paths that
      /// this reorganisation moved. Every one of those paths has to still resolve,
      /// or reorganising the tree invalidates every set of instructions ever
      /// written about the product - which is a worse outcome than the tree it
      /// replaced.
      /// </summary>
      [Theory]
      [InlineData("Settings > Anti-spam > SURBL servers", "surbl")]
      [InlineData("Settings > Anti-spam > Greylisting white list", "greylistwhitelist")]
      [InlineData("Settings > Anti-virus > Blocked attachments", "blockedattachments")]
      [InlineData("Settings > Advanced > IP ranges", "ipranges")]
      [InlineData("Settings > Advanced > TCP/IP ports", "ports")]
      [InlineData("Settings > Advanced > Routes", "routes")]
      [InlineData("Settings > Advanced hardening", "hardening")]
      // The split page: the combined path now resolves to the brute-force half,
      // and both halves also have a path of their own.
      [InlineData("Settings > Security > Auto-ban & SSL/TLS", "autoban")]
      [InlineData("Settings > Advanced > Auto-ban", "autoban")]
      [InlineData("Settings > Advanced > SSL/TLS", "tls")]
      [InlineData("Settings > Security > Certificates (ACME)", "acme")]
      [InlineData("Settings > Network > Web services & autoconfiguration", "webservices")]
      [InlineData("Settings > Maintenance > Event scripts", "scripts")]
      [InlineData("Utilities > MX-query", "mxquery")]
      [InlineData("Utilities > Diagnostics", "diagnostics")]
      [InlineData("Status > Delivery queue", "queue")]
      public void LegacyPath_StillResolves(string path, string key)
      {
         Assert.True(NavigationMap.LegacyPaths.TryGetValue(path, out string resolved),
            "'" + path + "' no longer resolves, so instructions naming it now lead nowhere.");
         Assert.Equal(key, resolved);
      }

      [Fact]
      public void EveryLegacyPath_PointsAtALivePage()
      {
         List<string> broken = NavigationMap.LegacyPaths
            .Where(entry => NavigationMap.Find(entry.Value) == null)
            .Select(entry => entry.Key + " -> " + entry.Value)
            .OrderBy(s => s)
            .ToList();

         Assert.True(broken.Count == 0, "Legacy paths pointing at pages that no longer exist: " + string.Join(", ", broken));
      }

      /// <summary>
      /// The signposts that let a subject span pages without duplicating a page
      /// into two branches. A link to a page that does not exist renders nothing;
      /// a link to the page you are already on is noise.
      /// </summary>
      [Fact]
      public void EveryRelatedLink_PointsAtADifferentLivePage()
      {
         var broken = new List<string>();

         foreach (NavNode page in NavigationMap.Pages)
         {
            foreach (string related in page.SeeAlso)
            {
               if (NavigationMap.Find(related) == null)
                  broken.Add(page.Key + " -> " + related + " (no such page)");
               else if (string.Equals(related, page.Key, StringComparison.OrdinalIgnoreCase))
                  broken.Add(page.Key + " -> itself");
            }

            Assert.Equal(page.SeeAlso.Count, page.SeeAlso.Distinct(StringComparer.OrdinalIgnoreCase).Count());
         }

         Assert.True(broken.Count == 0, "Broken related links: " + string.Join(", ", broken));
      }

      /// <summary>
      /// The four pages the roadmap named as one subject spread across two
      /// branches. They are now one group plus signposts, and an administrator
      /// setting up transport security must be able to reach the rest from any
      /// one of them.
      /// </summary>
      [Theory]
      [InlineData("certs")]
      [InlineData("acme")]
      [InlineData("security")]
      [InlineData("ports")]
      [InlineData("webservices")]
      public void TheTlsSubject_IsCrossLinked(string key)
      {
         NavNode page = NavigationMap.Find(key);
         Assert.NotNull(page);

         var tlsSubject = new[] { "certs", "acme", "security", "ports", "webservices", "tls" };
         Assert.True(page.SeeAlso.Any(related => tlsSubject.Contains(related, StringComparer.OrdinalIgnoreCase)),
            "'" + page.Title + "' is part of the transport-security story but signposts none of the other pages in it.");
      }

      /// <summary>
      /// Every settings page the generated index knows about must have a home in
      /// the navigation, or the palette's "which page does this live on" line has
      /// nothing to say and picking the result goes nowhere.
      /// </summary>
      [Fact]
      public void EveryIndexedSettingsPage_HasANavigationHome()
      {
         List<string> homeless = SettingsSearchIndex.Entries
            .Select(e => e.Page)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(page => NavigationMap.Find(page) == null)
            .OrderBy(p => p)
            .ToList();

         Assert.True(homeless.Count == 0,
            "The settings index points at pages the navigation does not contain: " + string.Join(", ", homeless));
      }

      /// <summary>
      /// MainWindow must build the tree from the map rather than writing it out
      /// again. Two sources of truth is how the palette and the sidebar came to
      /// disagree in the first place - the palette read page names back out of
      /// TreeViewItem headers - and the copy is easy to reintroduce by hand.
      /// </summary>
      [Fact]
      public void MainWindow_BuildsTheTreeFromTheNavigationMap()
      {
         string mainWindow = ReadMainWindowSource();
         if (mainWindow == null)
            return;   // sources not available (e.g. running from a packaged drop)

         Assert.Contains("NavigationMap.Roots", mainWindow);

         // The old shape: page titles paired with keys in the code-behind.
         Assert.DoesNotMatch(new Regex("Item\\(\"[^\"]+\",\\s*\"[a-z]+\"\\)"), mainWindow);
         Assert.DoesNotContain("Mirrors the classic Administrator tree layout", mainWindow);
      }

      /// <summary>
      /// The palette used to be reachable only by knowing Ctrl+K existed, which
      /// is not a discoverable interface and is unusable to anyone who cannot
      /// learn shortcuts from nothing. A visible, tab-reachable control has to
      /// open it too.
      /// </summary>
      [Fact]
      public void Sidebar_HasAKeyboardReachableSearchButton()
      {
         string xaml = ReadMainWindowXaml();
         if (xaml == null)
            return;   // sources not available

         Assert.Contains("x:Name=\"SearchButton\"", xaml);
         Assert.Contains("Click=\"Search_Click\"", xaml);
         Assert.Contains("nav-search", xaml);

         // A button carrying only an icon is unusable to a screen reader.
         Assert.Matches(new Regex("x:Name=\"SearchButton\"[\\s\\S]{0,900}?AutomationProperties\\.Name="), xaml);

         string code = ReadMainWindowSource();
         if (code != null)
         {
            Assert.Contains("private void Search_Click", code);
            // The shortcut has to keep working as well as the button.
            Assert.Matches(new Regex("Key\\.K[\\s\\S]{0,200}?ShowPalette"), code);
         }
      }

      /// <summary>
      /// A palette jump that replaces the page with no indication of where the
      /// user landed teaches nothing, so the palette has to be used again next
      /// time. The breadcrumb is what makes the jump legible.
      /// </summary>
      [Fact]
      public void ContentArea_HasABreadcrumbBar()
      {
         string xaml = ReadMainWindowXaml();
         if (xaml == null)
            return;   // sources not available

         Assert.Contains("x:Name=\"BreadcrumbBar\"", xaml);
         Assert.Contains("x:Name=\"BreadcrumbHost\"", xaml);
         Assert.Contains("x:Name=\"RelatedHost\"", xaml);

         string code = ReadMainWindowSource();
         if (code != null)
         {
            Assert.Contains("UpdateBreadcrumb", code);
            // The trail has to come from the map, not be assembled by hand.
            Assert.Contains("NavigationMap.PathTo", code);
         }
      }

      /// <summary>Automation ids are what any UI automation and the accessibility tooling hold on to.</summary>
      [Fact]
      public void Slug_ProducesStableAutomationIds()
      {
         Assert.Equal("tls---certificates", NavigationMap.Slug("TLS & certificates"));
         Assert.Equal("spam---virus-filtering", NavigationMap.Slug("Spam & virus filtering"));
         Assert.Equal("", NavigationMap.Slug(null));
      }

      private static string ReadMainWindowSource() => ReadControlPanelFile("MainWindow.xaml.cs");

      private static string ReadMainWindowXaml() => ReadControlPanelFile("MainWindow.xaml");

      private static string ReadControlPanelFile(string relativePath)
      {
         string controlPanel = FindControlPanelDirectory();
         if (controlPanel == null)
            return null;

         string path = Path.Join(controlPanel, relativePath);
         return File.Exists(path) ? File.ReadAllText(path) : null;
      }

      /// <summary>Walks up from the test binaries to the Control Panel sources.</summary>
      private static string FindControlPanelDirectory()
      {
         var directory = new DirectoryInfo(AppContext.BaseDirectory);

         while (directory != null)
         {
            string candidate = Path.Join(directory.FullName, "ControlPanel");
            if (Directory.Exists(Path.Join(candidate, "Views")))
               return candidate;

            directory = directory.Parent;
         }

         return null;
      }
   }
}
