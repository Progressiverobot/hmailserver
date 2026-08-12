using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// One searchable setting: what it is called on screen, the underlying
   /// hMailServer.INI key or COM property path, and the navigation page that
   /// hosts it.
   /// </summary>
   public sealed class SettingEntry
   {
      public SettingEntry(string label, string key, string page)
      {
         Label = label;
         Key = key;
         Page = page;
      }

      public string Label { get; }
      public string Key { get; }
      public string Page { get; }
   }

   /// <summary>
   /// Lets the command palette find an individual setting rather than only a
   /// page name. Settings are spread across pages by topic, and a setting whose
   /// page an administrator does not guess is effectively invisible - searching
   /// for the setting itself removes that failure mode, and keeps removing it
   /// as settings are added.
   ///
   /// The entries are generated from the page definitions by
   /// build/generate-settings-index.ps1 (see SettingsSearchIndex.g.cs), so the
   /// index cannot drift away from what the pages actually show.
   /// </summary>
   public static partial class SettingsSearchIndex
   {
      /// <summary>
      /// Human-readable page titles, keyed by the navigation tag, so a result
      /// can say which page it will open.
      /// </summary>
      private static readonly Dictionary<string, string> PageTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
      {
         ["protocols"] = "Protocols",
         ["delivery"] = "Delivery of e-mail",
         ["antispam"] = "Anti-spam settings",
         ["antivirus"] = "Anti-virus settings",
         ["tls"] = "Auto-ban & SSL/TLS",
         ["logging"] = "Logging",
         ["performance"] = "Performance",
         ["advanced"] = "Advanced & scripting",
         ["adminaccess"] = "Administrative access",
         ["security"] = "Transport security",
         ["acme"] = "Certificates (ACME)",
         ["api"] = "API & monitoring",
         ["hardening"] = "Advanced INI settings",
         ["authentication"] = "Authentication",
         ["dns"] = "DNS resolver",
         ["webservices"] = "Web services & autoconfiguration",

         // Hand-written pages that own settings of their own. These titles
         // cover every page the generator scans for them, so a page that gains
         // its first setting is named properly the moment it is indexed.
         ["backup"] = "Backup & restore",
         ["ipranges"] = "IP ranges",
         ["ports"] = "TCP/IP ports",
         ["routes"] = "Routes",
         ["domains"] = "Domains",
         ["rules"] = "Rules",
         ["certs"] = "SSL certificates",
      };

      /// <summary>
      /// Search results, most relevant first. Each result carries the page tag
      /// the caller should navigate to.
      /// </summary>
      public static IEnumerable<(string Display, string Page)> Search(string query)
      {
         if (string.IsNullOrWhiteSpace(query))
            yield break;

         string needle = query.Trim();

         // Rank: label prefix, then label substring, then INI key/COM path.
         var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

         foreach (var ranked in Entries
            .Select(e => new
            {
               Entry = e,
               Rank = e.Label.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 0
                    : e.Label.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                    : e.Key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                    : -1
            })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Entry.Label, StringComparer.OrdinalIgnoreCase))
         {
            SettingEntry entry = ranked.Entry;

            string pageTitle = PageTitles.TryGetValue(entry.Page, out string title) ? title : entry.Page;
            string display = entry.Label + "  —  " + pageTitle;

            // The same label can appear once per page; don't list it twice.
            if (!seen.Add(display))
               continue;

            yield return (display, entry.Page);
         }
      }
   }
}
