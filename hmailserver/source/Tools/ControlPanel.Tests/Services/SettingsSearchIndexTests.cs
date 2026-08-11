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
