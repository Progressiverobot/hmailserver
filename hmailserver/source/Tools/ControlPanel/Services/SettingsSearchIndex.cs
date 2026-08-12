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
   /// A setting that matched a search, with everything a caller needs to rank it
   /// against results from other sources and to tell the user where it lives.
   /// </summary>
   public sealed class SettingMatch
   {
      internal SettingMatch(SettingEntry entry, int rank)
      {
         Entry = entry;
         Rank = rank;
      }

      /// <summary>The indexed setting.</summary>
      public SettingEntry Entry { get; }

      /// <summary>Match score, lower being better. See <see cref="SearchTerms"/>.</summary>
      public int Rank { get; }

      public string Label => Entry.Label;
      public string Key => Entry.Key;
      public string Page => Entry.Page;

      /// <summary>
      /// The full navigation trail to the page hosting the setting, so that a
      /// result can say where it will take the reader before they go there.
      /// Resolved through <see cref="NavigationMap"/> rather than stored, so a
      /// page that moves in the navigation cannot leave stale trails behind in
      /// the generated index.
      /// </summary>
      public string Location => NavigationMap.LocationOf(Page);
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
   ///
   /// Ranking is delegated to <see cref="SearchQuery"/> rather than done here.
   /// It used to be a private prefix/substring ladder, which meant the palette
   /// scored settings by one rule and page names by another; a single list whose
   /// rows were ranked by two incomparable scales put results in an order that
   /// could not be reasoned about. Page titles for results come from
   /// <see cref="NavigationMap"/> for the same reason - a second hand-maintained
   /// table of page titles drifts from the navigation the moment either changes.
   /// </summary>
   public static partial class SettingsSearchIndex
   {
      /// <summary>
      /// Added to a match found only in the INI key or COM path, so that a
      /// setting matched by the words on screen outranks one matched by an
      /// identifier. Somebody typing "delete logs" means the label; somebody
      /// typing "LogDeleteDays" gets an exact key match, which still wins.
      /// </summary>
      private const int KeyPenalty = 20;

      /// <summary>
      /// Search results, most relevant first. Each result carries the page the
      /// caller should navigate to.
      /// </summary>
      public static IEnumerable<SettingMatch> Search(string query) => Search(new SearchQuery(query));

      /// <summary>
      /// Overload for a caller that is already searching several sources with
      /// one query and should not pay to normalise it again per source.
      /// </summary>
      public static IEnumerable<SettingMatch> Search(SearchQuery query)
      {
         if (query == null || query.IsEmpty)
            return Array.Empty<SettingMatch>();

         var matches = new List<SettingMatch>();

         foreach (SettingEntry entry in Entries)
         {
            int byLabel = query.Score(entry.Label);
            int byKey = query.Score(entry.Key);
            if (byKey != SearchTerms.NoMatch)
               byKey += KeyPenalty;

            int rank = Math.Min(byLabel, byKey);
            if (rank == SearchTerms.NoMatch)
               continue;

            matches.Add(new SettingMatch(entry, rank));
         }

         // Label as the tie-break rather than the key: two settings that score
         // the same are told apart by the reader on their wording, so listing
         // them in wording order is what makes the list scannable.
         return matches
            .OrderBy(m => m.Rank)
            .ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
      }
   }
}
