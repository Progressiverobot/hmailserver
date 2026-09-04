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
   /// The command palette can only find a setting if the generated index still
   /// matches the settings pages. These tests fail when a setting is added,
   /// removed or relabelled without re-running build/generate-settings-index.ps1,
   /// so a setting can never quietly become unsearchable.
   /// </summary>
   public class SettingsSearchIndexTests
   {
      [Fact]
      public void Index_IsNotEmpty()
      {
         Assert.NotEmpty(SettingsSearchIndex.Entries);
      }

      [Fact]
      public void EverySetting_HasLabelKeyAndPage()
      {
         foreach (SettingEntry entry in SettingsSearchIndex.Entries)
         {
            Assert.False(string.IsNullOrWhiteSpace(entry.Label), "A setting has no label.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Key), $"Setting '{entry.Label}' has no key.");
            Assert.False(string.IsNullOrWhiteSpace(entry.Page), $"Setting '{entry.Label}' has no page.");
         }
      }

      [Fact]
      public void Search_FindsSettingByLabel()
      {
         var results = SettingsSearchIndex.Search("Delete logs older").ToList();

         Assert.NotEmpty(results);
         Assert.Equal("logging", results[0].Page);
      }

      [Fact]
      public void Search_FindsSettingByIniKey()
      {
         var results = SettingsSearchIndex.Search("LogDeleteDays").ToList();

         Assert.NotEmpty(results);
         Assert.Equal("logging", results[0].Page);
      }

      [Fact]
      public void Search_IsCaseInsensitive()
      {
         Assert.NotEmpty(SettingsSearchIndex.Search("logdeletedays").ToList());
      }

      [Fact]
      public void Search_EmptyQuery_ReturnsNothing()
      {
         Assert.Empty(SettingsSearchIndex.Search("").ToList());
         Assert.Empty(SettingsSearchIndex.Search("   ").ToList());
      }

      /// <summary>
      /// Reads the two settings-page sources the generator reads and asserts that
      /// every setting they define is present in the checked-in index.
      /// </summary>
      [Fact]
      public void Index_MatchesTheSettingsPages()
      {
         string controlPanel = FindControlPanelDirectory();
         if (controlPanel == null)
            return;   // sources not available (e.g. running from a packaged drop)

         var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

         string featureSource = File.ReadAllText(Path.Join(controlPanel, "Views", "FeatureSettingsView.xaml.cs"));
         expected.UnionWith(Regex.Matches(featureSource, "Key = \"([^\"]+)\"[^}]*?Label = \"([^\"]*)\"")
            .Where(m => !string.IsNullOrWhiteSpace(m.Groups[2].Value))
            .Select(m => m.Groups[1].Value));

         string serverSource = File.ReadAllText(Path.Join(controlPanel, "Views", "ServerSettingsView.xaml.cs"));
         expected.UnionWith(Regex.Matches(serverSource, "Path = \"([^\"]+)\",\\s*Label = \"([^\"]*)\"")
            .Where(m => !string.IsNullOrWhiteSpace(m.Groups[2].Value))
            .Select(m => m.Groups[1].Value));
         expected.UnionWith(Regex.Matches(serverSource, "Label = \"([^\"]*)\",\\s*Path = \"([^\"]+)\"")
            .Where(m => !string.IsNullOrWhiteSpace(m.Groups[1].Value))
            .Select(m => m.Groups[2].Value));

         var indexed = new HashSet<string>(SettingsSearchIndex.Entries.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);

         List<string> missing = expected.Where(key => !indexed.Contains(key)).OrderBy(k => k).ToList();

         Assert.True(missing.Count == 0,
            "These settings are not in the search index, so the palette cannot find them. " +
            "Re-run build/generate-settings-index.ps1: " + string.Join(", ", missing));
      }

      /// <summary>
      /// Settings that live on hand-written pages rather than in the two
      /// table-driven views. They reach the index through a different scan, so
      /// they can drop out of it while everything else still looks healthy;
      /// naming them here means that shows up as a failure instead of as a
      /// palette that silently stops finding them.
      /// </summary>
      [Theory]
      [InlineData("BackupMessagesDBOnly", "backup")]
      [InlineData("Destination", "backup")]
      [InlineData("BackupDomains", "backup")]
      [InlineData("BackupMessages", "backup")]
      [InlineData("BackupSettings", "backup")]
      [InlineData("CompressDestinationFiles", "backup")]
      [InlineData("AllowSMTPConnections", "ipranges")]
      [InlineData("AllowIMAPConnections", "ipranges")]
      [InlineData("AllowPOP3Connections", "ipranges")]
      [InlineData("RequireSSLTLSForAuth", "ipranges")]
      public void HandWrittenPageSetting_IsIndexedAndSearchable(string key, string page)
      {
         SettingEntry entry = SettingsSearchIndex.Entries
            .FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

         Assert.True(entry != null,
            $"'{key}' is not in the search index, so the palette cannot find it. " +
            "Re-run build/generate-settings-index.ps1; if that does not bring it back, the " +
            "generator's hand-written-page scan no longer recognises the page.");
         Assert.Equal(page, entry.Page);

         // The label is what an administrator actually types, so it has to find
         // the setting as well as the key does.
         Assert.Contains(SettingsSearchIndex.Search(entry.Label), r => r.Page == page);
         Assert.Contains(SettingsSearchIndex.Search(key), r => r.Page == page);
      }

      /// <summary>
      /// A setting belongs on the page that owns the feature it configures, not on
      /// the page that matches where the value happens to be stored.
      ///
      /// hMailServer keeps settings in two places - the settings database, reached
      /// over COM, and hMailServer.INI - and the Control Panel has one settings view
      /// for each. That is an implementation detail of the Control Panel, and for a
      /// long time it leaked straight into the navigation: half of greylisting was
      /// on the Anti-spam page and the other half on a catch-all page called
      /// "Advanced INI settings", the IMAP search limits that the README tells
      /// owners of large mailboxes to raise were nowhere near IMAP, and the TLS
      /// session-resumption settings were not on the SSL/TLS page. Nobody looking
      /// for any of them would have looked there, because nobody knows or cares
      /// which of the two stores a value lives in.
      ///
      /// Each row below is one setting that was moved for that reason. They are
      /// pinned because the move is invisible in the source - both views can edit
      /// an INI value, so putting one back on the wrong page compiles, runs, and
      /// costs nothing until somebody cannot find it.
      /// </summary>
      [Theory]
      [InlineData("GreylistingEnabledDuringRecordExpiration", "antispam")]
      [InlineData("GreylistingRecordExpirationInterval", "antispam")]
      [InlineData("SAMoveVsCopy", "antispam")]
      [InlineData("IMAPSearchTimeout", "protocols")]
      [InlineData("IMAPSearchMaxMegabytes", "protocols")]
      [InlineData("SMTPDMaxTimeout", "protocols")]
      [InlineData("POP3CMaxTimeout", "protocols")]
      [InlineData("TlsSessionTicketsEnabled", "tls")]
      [InlineData("TlsSessionCacheSize", "tls")]
      [InlineData("TlsSessionTimeoutSeconds", "tls")]
      [InlineData("TlsTicketKeyRotationSeconds", "tls")]
      [InlineData("MXTriesFactor", "delivery")]
      [InlineData("MaxNumberOfExternalFetchThreads", "performance")]
      [InlineData("SRSEnabled", "security")]
      [InlineData("BATVEnabled", "security")]
      [InlineData("RewriteEnvelopeFromWhenForwarding", "security")]
      public void Setting_IsFiledWithItsFeature_NotWithItsStorage(string key, string page)
      {
         SettingEntry entry = SettingsSearchIndex.Entries
            .FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

         Assert.True(entry != null, $"'{key}' has fallen out of the search index entirely.");
         Assert.True(string.Equals(page, entry.Page, StringComparison.OrdinalIgnoreCase),
            $"'{key}' is on the '{entry.Page}' page. It configures a feature that lives on '{page}', " +
            "and it was deliberately moved there so that an administrator finds the whole feature in one " +
            "place. If it has genuinely changed subject, update this row; if it moved back to a catch-all " +
            "page because that is where its storage lives, that is the mistake this test exists to catch.");
      }

      /// <summary>
      /// Every hMailServer.INI setting a page reads or writes directly must be
      /// searchable, whichever page ends up owning it in the index.
      /// </summary>
      [Fact]
      public void IniSettingsUsedByHandWrittenPages_AreIndexed()
      {
         string controlPanel = FindControlPanelDirectory();
         if (controlPanel == null)
            return;   // sources not available (e.g. running from a packaged drop)

         var indexed = new HashSet<string>(SettingsSearchIndex.Entries.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
         var missing = new List<string>();

         foreach (string file in Directory.GetFiles(Path.Join(controlPanel, "Views"), "*.cs"))
         {
            // The two table-driven views are covered by Index_MatchesTheSettingsPages.
            string name = Path.GetFileName(file);
            if (name.StartsWith("FeatureSettingsView.") || name.StartsWith("ServerSettingsView."))
               continue;

            missing.AddRange(Regex.Matches(File.ReadAllText(file), "\\.(?:Read|Write)(?:Bool|String|Int|Number)\\(\\s*\"([^\"]+)\"")
               .Select(m => m.Groups[1].Value)
               .Where(key => !indexed.Contains(key))
               .Select(key => name + ": " + key));
         }

         Assert.True(missing.Count == 0,
            "These INI settings are edited on a page but are not in the search index: " +
            string.Join(", ", missing.Distinct().OrderBy(k => k)));
      }

      /// <summary>
      /// The generator maps each settings page onto a nav key with a hard-coded
      /// table of its own, so a page added to MainWindow without a matching
      /// entry in that table produces no index entries at all - the page still
      /// works, but none of its settings can be found from the palette. Assert
      /// that every table-driven settings page contributes at least one entry,
      /// which is a faster signal than waiting for CI to re-run the generator.
      ///
      /// Only the two table-driven views are checked. The other navigable pages
      /// are deliberately not required to appear: status, queue, log and
      /// dashboard pages own no setting, and the list pages (routes, domains,
      /// rules, ports, certificates) edit properties of individual objects
      /// rather than server settings, so indexing them would be search noise.
      /// </summary>
      [Fact]
      public void EverySettingsPage_ContributesEntriesToTheIndex()
      {
         string mainWindow = ReadMainWindowSource();
         if (mainWindow == null)
            return;   // sources not available (e.g. running from a packaged drop)

         var settingsPages = Regex.Matches(mainWindow,
               "pageFactories_\\[\"([^\"]+)\"\\]\\s*=\\s*\\(\\)\\s*=>\\s*new (?:Feature|Server)SettingsView\\(")
            .Select(m => m.Groups[1].Value)
            .ToList();

         // If the regex stops matching, the test would pass vacuously.
         Assert.True(settingsPages.Count >= 10,
            $"Only found {settingsPages.Count} settings pages in MainWindow; the page-factory scan needs updating.");

         var indexedPages = new HashSet<string>(SettingsSearchIndex.Entries.Select(e => e.Page), StringComparer.OrdinalIgnoreCase);

         List<string> unindexed = settingsPages.Where(p => !indexedPages.Contains(p)).OrderBy(p => p).ToList();

         Assert.True(unindexed.Count == 0,
            "These settings pages contribute nothing to the search index, so none of their settings " +
            "can be found from the palette. Re-run build/generate-settings-index.ps1; if that does not " +
            "help, the page is missing from the $featurePages / $serverPages tables in the generator: " +
            string.Join(", ", unindexed));
      }

      /// <summary>
      /// The reverse check: a page tag in the index that MainWindow cannot
      /// navigate to sends the palette nowhere when the result is picked, and a
      /// typo in the generator's nav-key tables is exactly how that happens.
      /// </summary>
      [Fact]
      public void EveryIndexedPage_IsANavigablePage()
      {
         string mainWindow = ReadMainWindowSource();
         if (mainWindow == null)
            return;   // sources not available (e.g. running from a packaged drop)

         var navigable = new HashSet<string>(
            Regex.Matches(mainWindow, "pageFactories_\\[\"([^\"]+)\"\\]").Select(m => m.Groups[1].Value),
            StringComparer.OrdinalIgnoreCase);

         Assert.True(navigable.Count >= 20,
            $"Only found {navigable.Count} pages in MainWindow; the page-factory scan needs updating.");

         List<string> unreachable = SettingsSearchIndex.Entries
            .Select(e => e.Page)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !navigable.Contains(p))
            .OrderBy(p => p)
            .ToList();

         Assert.True(unreachable.Count == 0,
            "The index points settings at pages MainWindow cannot navigate to, so picking them in the " +
            "palette does nothing: " + string.Join(", ", unreachable));
      }

      /// <summary>Reads MainWindow.xaml.cs, or null when the sources are not next to the binaries.</summary>
      private static string ReadMainWindowSource()
      {
         string controlPanel = FindControlPanelDirectory();
         if (controlPanel == null)
            return null;

         string path = Path.Join(controlPanel, "MainWindow.xaml.cs");
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
