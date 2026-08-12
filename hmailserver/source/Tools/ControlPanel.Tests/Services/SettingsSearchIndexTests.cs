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

         string featureSource = File.ReadAllText(Path.Combine(controlPanel, "Views", "FeatureSettingsView.xaml.cs"));
         foreach (Match m in Regex.Matches(featureSource, "Key = \"([^\"]+)\"[^}]*?Label = \"([^\"]*)\""))
         {
            if (!string.IsNullOrWhiteSpace(m.Groups[2].Value))
               expected.Add(m.Groups[1].Value);
         }

         string serverSource = File.ReadAllText(Path.Combine(controlPanel, "Views", "ServerSettingsView.xaml.cs"));
         foreach (Match m in Regex.Matches(serverSource, "Path = \"([^\"]+)\",\\s*Label = \"([^\"]*)\""))
         {
            if (!string.IsNullOrWhiteSpace(m.Groups[2].Value))
               expected.Add(m.Groups[1].Value);
         }
         foreach (Match m in Regex.Matches(serverSource, "Label = \"([^\"]*)\",\\s*Path = \"([^\"]+)\""))
         {
            if (!string.IsNullOrWhiteSpace(m.Groups[1].Value))
               expected.Add(m.Groups[2].Value);
         }

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

         foreach (string file in Directory.GetFiles(Path.Combine(controlPanel, "Views"), "*.cs"))
         {
            // The two table-driven views are covered by Index_MatchesTheSettingsPages.
            string name = Path.GetFileName(file);
            if (name.StartsWith("FeatureSettingsView.") || name.StartsWith("ServerSettingsView."))
               continue;

            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\\.(?:Read|Write)(?:Bool|String|Int|Number)\\(\\s*\"([^\"]+)\""))
            {
               string key = m.Groups[1].Value;
               if (!indexed.Contains(key))
                  missing.Add(name + ": " + key);
            }
         }

         Assert.True(missing.Count == 0,
            "These INI settings are edited on a page but are not in the search index: " +
            string.Join(", ", missing.Distinct().OrderBy(k => k)));
      }

      /// <summary>Walks up from the test binaries to the Control Panel sources.</summary>
      private static string FindControlPanelDirectory()
      {
         var directory = new DirectoryInfo(AppContext.BaseDirectory);

         while (directory != null)
         {
            string candidate = Path.Combine(directory.FullName, "ControlPanel");
            if (Directory.Exists(Path.Combine(candidate, "Views")))
               return candidate;

            directory = directory.Parent;
         }

         return null;
      }
   }
}
