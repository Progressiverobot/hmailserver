using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>What a palette row is, which decides how it is drawn and whether it can be picked.</summary>
   public enum PaletteRowKind
   {
      /// <summary>A section caption. Never selectable, and skipped by the arrow keys.</summary>
      Header,

      /// <summary>A page to open.</summary>
      Page,

      /// <summary>An outcome phrase that resolves to a page.</summary>
      Task,

      /// <summary>An individual setting, which opens the page hosting it.</summary>
      Setting
   }

   /// <summary>One row of the command palette.</summary>
   public sealed class PaletteRow
   {
      internal PaletteRow(PaletteRowKind kind, string section, string title, string location,
         string detail, string page, int score)
      {
         Kind = kind;
         Section = section;
         Title = title;
         Location = location;
         Detail = detail;
         Page = page;
         Score = score;
      }

      public PaletteRowKind Kind { get; }

      /// <summary>The section caption this row sits under.</summary>
      public string Section { get; }

      /// <summary>The primary line: a page title, an outcome phrase, or a setting label.</summary>
      public string Title { get; }

      /// <summary>
      /// Where the thing lives. The whole point of showing it is that a palette
      /// which teleports the user somewhere without saying where they went
      /// trains them to keep using the palette; one that names the location
      /// teaches the structure and is needed less over time.
      /// </summary>
      public string Location { get; }

      /// <summary>Supporting line: the purpose of a page, what a task will do, or a setting's key.</summary>
      public string Detail { get; }

      /// <summary>The navigation key to open. Null on a header.</summary>
      public string Page { get; }

      /// <summary>Match score, lower being better. Zero for the rows of an unfiltered overview.</summary>
      public int Score { get; }

      /// <summary>False for section captions.</summary>
      public bool IsSelectable => Kind != PaletteRowKind.Header;
   }

   /// <summary>
   /// The command palette's search, kept out of the palette window so that it
   /// can be tested without a message pump.
   ///
   /// It answers three different questions in one ranked list:
   ///
   ///   - "take me to the page called X"  - page titles, their legacy titles and
   ///     the navigation paths those pages used to have;
   ///   - "I want to achieve X"           - the outcome phrases in
   ///     <see cref="IntentIndex"/>, which is the case a name-based search
   ///     cannot serve at all;
   ///   - "where is the setting called X" - <see cref="SettingsSearchIndex"/>,
   ///     which is what the palette did before and remains the fastest route for
   ///     somebody who already knows the name.
   ///
   /// With nothing typed it shows recently-visited and most-used pages before
   /// the full list, because the palette is opened far more often to return
   /// somewhere than to discover something.
   ///
   /// Section captions are rows in the same list rather than a separate visual
   /// layer, so that the list a test reasons about and the list the user sees are
   /// the same list. That makes captions a keyboard hazard - the selection must
   /// never land on one - which is why <see cref="NextSelectable"/> exists here
   /// rather than as arithmetic in the window's key handler.
   /// </summary>
   public static class PaletteSearch
   {
      public const string RecentSection = "Recently visited";
      public const string MostUsedSection = "Most used";
      public const string TaskSection = "Tasks";
      public const string PageSection = "Pages";
      public const string SettingSection = "Settings";

      /// <summary>Rows offered per shortcut section when nothing has been typed.</summary>
      public const int MaxShortcuts = 5;

      /// <summary>
      /// Outcome phrases offered at once. More than a handful and the section
      /// pushes the page and setting matches off screen, which trades one
      /// findability problem for another.
      /// </summary>
      public const int MaxTasks = 6;

      /// <summary>Settings offered at once. Unchanged from the previous palette.</summary>
      public const int MaxSettings = 12;

      // Per-source offsets added to the text score, so that sources compete in
      // one ranking. The order encodes an editorial judgement about what a
      // typed word most likely means: the name of a page beats a phrase about
      // it, a phrase beats an old name for the page, an old name beats "this
      // page's group is called that", and everything beats an individual
      // setting - a query matching 30 settings and one page title almost always
      // means the page.
      private const int PageTitleBase = 0;
      private const int TaskBase = 2;
      private const int PageAliasBase = 4;
      private const int LegacyPathBase = 6;
      private const int GroupBase = 20;
      private const int PagePurposeBase = 40;
      private const int SettingBase = 30;

      /// <summary>
      /// Builds the rows for a query. <paramref name="usage"/> and
      /// <paramref name="currentPage"/> may be null, which is what a caller
      /// without a history or without a page open passes.
      /// </summary>
      public static IReadOnlyList<PaletteRow> Query(string text, PaletteUsage usage, string currentPage)
      {
         var query = new SearchQuery(text);
         return query.IsEmpty ? Overview(usage, currentPage) : Matches(query, usage);
      }

      /// <summary>
      /// The index of the next selectable row from <paramref name="from"/> in
      /// <paramref name="direction"/>, skipping section captions and stopping at
      /// the ends of the list. Returns <paramref name="from"/> when there is
      /// nowhere to go, so a key press at the end of the list is a no-op rather
      /// than a wrap-around - wrapping in a search palette loses the user's
      /// place silently.
      /// </summary>
      public static int NextSelectable(IReadOnlyList<PaletteRow> rows, int from, int direction)
      {
         if (rows == null || rows.Count == 0 || direction == 0)
            return from;

         int step = direction > 0 ? 1 : -1;

         for (int index = from + step; index >= 0 && index < rows.Count; index += step)
         {
            if (rows[index].IsSelectable)
               return index;
         }

         return from;
      }

      /// <summary>The first selectable row, or -1 when there is nothing to select.</summary>
      public static int FirstSelectable(IReadOnlyList<PaletteRow> rows)
      {
         if (rows == null)
            return -1;

         for (int index = 0; index < rows.Count; index++)
         {
            if (rows[index].IsSelectable)
               return index;
         }

         return -1;
      }

      /// <summary>The last selectable row, or -1 when there is nothing to select.</summary>
      public static int LastSelectable(IReadOnlyList<PaletteRow> rows)
      {
         if (rows == null)
            return -1;

         for (int index = rows.Count - 1; index >= 0; index--)
         {
            if (rows[index].IsSelectable)
               return index;
         }

         return -1;
      }

      /// <summary>
      /// Nothing typed: shortcuts first, then every page in navigation order.
      /// The full list stays because it is the only place the palette shows the
      /// shape of the application, and somebody who opened the palette by
      /// accident should still be able to see where things are.
      /// </summary>
      private static IReadOnlyList<PaletteRow> Overview(PaletteUsage usage, string currentPage)
      {
         var rows = new List<PaletteRow>();
         var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

         // The page the user is already looking at is not a useful shortcut, and
         // it is always at the head of the recent list, so it would otherwise be
         // the first thing the palette offered every single time.
         if (currentPage != null)
            shown.Add(currentPage);

         if (usage != null)
         {
            AddShortcuts(rows, RecentSection, usage.Recent, shown);
            AddShortcuts(rows, MostUsedSection, usage.MostUsed, shown);
         }

         var pageRows = new List<PaletteRow>();
         foreach (NavNode page in NavigationMap.Pages)
            pageRows.Add(PageRow(PageSection, page, 0));

         Emit(rows, PageSection, pageRows);
         return rows;
      }

      private static void AddShortcuts(List<PaletteRow> rows, string section,
         IReadOnlyList<string> keys, HashSet<string> shown)
      {
         var shortcuts = new List<PaletteRow>();

         foreach (string key in keys)
         {
            if (shortcuts.Count >= MaxShortcuts)
               break;

            // A key can outlive the page it named - a page can be renamed or
            // removed between releases while the history in HKCU stays. Skipping
            // unknown keys here means an old history degrades quietly instead of
            // offering rows that navigate nowhere.
            NavNode page = NavigationMap.Find(key);
            if (page == null || !shown.Add(key))
               continue;

            shortcuts.Add(PageRow(section, page, 0));
         }

         Emit(rows, section, shortcuts);
      }

      private static IReadOnlyList<PaletteRow> Matches(SearchQuery query, PaletteUsage usage)
      {
         List<PaletteRow> tasks = MatchTasks(query);
         List<PaletteRow> pages = MatchPages(query);
         List<PaletteRow> settings = MatchSettings(query);

         Sort(tasks, usage);
         Sort(pages, usage);
         Sort(settings, usage);

         if (tasks.Count > MaxTasks)
            tasks.RemoveRange(MaxTasks, tasks.Count - MaxTasks);
         if (settings.Count > MaxSettings)
            settings.RemoveRange(MaxSettings, settings.Count - MaxSettings);

         // Sections compete on their best row, so that typing an exact page name
         // does not bury it under six task phrases, and typing an outcome phrase
         // does not bury the answer under pages that merely share a word with
         // it. The fixed order is only the tie-break.
         var sections = new List<(int Order, string Name, List<PaletteRow> Rows)>
         {
            (0, TaskSection, tasks),
            (1, PageSection, pages),
            (2, SettingSection, settings)
         };

         var rows = new List<PaletteRow>();
         foreach (var section in sections
            .Where(s => s.Rows.Count > 0)
            .OrderBy(s => s.Rows[0].Score)
            .ThenBy(s => s.Order))
         {
            Emit(rows, section.Name, section.Rows);
         }

         return rows;
      }

      private static List<PaletteRow> MatchTasks(SearchQuery query)
      {
         // Best phrase per destination. Several phrasings of the same intent are
         // deliberately in the table, and showing four rows that all open the
         // anti-spam page is worse than showing the one that matched best.
         var best = new Dictionary<string, PaletteRow>(StringComparer.OrdinalIgnoreCase);

         foreach (IntentEntry intent in IntentIndex.Entries)
         {
            if (NavigationMap.Find(intent.Page) == null)
               continue;

            int score = query.Score(intent.Phrase);
            if (score == SearchTerms.NoMatch)
               continue;

            score += TaskBase;

            if (best.TryGetValue(intent.Page, out PaletteRow existing) && existing.Score <= score)
               continue;

            best[intent.Page] = new PaletteRow(PaletteRowKind.Task, TaskSection, intent.Phrase,
               NavigationMap.LocationOf(intent.Page), intent.Answer, intent.Page, score);
         }

         return best.Values.ToList();
      }

      private static List<PaletteRow> MatchPages(SearchQuery query)
      {
         var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

         foreach (NavNode page in NavigationMap.Pages)
         {
            // Only the title gets the character-subsequence fallback. People do
            // type initials for a page they know ("dq" for the delivery queue),
            // but a subsequence over a dozen aliases and a sentence of prose
            // matches nearly everything and would make short queries useless.
            int score = Min(
               Add(query.ScoreLoose(page.Title), PageTitleBase),
               Add(query.BestScore(page.Aliases), PageAliasBase),
               Add(query.Score(page.Purpose), PagePurposeBase));

            if (score != SearchTerms.NoMatch)
               scores[page.Key] = score;
         }

         // Where the page used to be. Someone following instructions written
         // against an older release types the old path, and it has to work.
         foreach (KeyValuePair<string, string> legacy in NavigationMap.LegacyPaths)
         {
            if (NavigationMap.Find(legacy.Value) == null)
               continue;

            int score = Add(query.Score(legacy.Key), LegacyPathBase);
            if (score != SearchTerms.NoMatch)
               Keep(scores, legacy.Value, score);
         }

         // A query naming a group offers everything in it. "filtering" is a
         // reasonable thing to type and the group is not itself navigable, so
         // without this the query finds nothing at all.
         foreach (NavNode group in NavigationMap.Groups)
         {
            int score = Min(query.Score(group.Title), query.BestScore(group.Aliases));
            if (score == SearchTerms.NoMatch)
               continue;

            foreach (NavNode page in group.Children.Where(c => c.IsPage))
               Keep(scores, page.Key, Add(score, GroupBase));
         }

         return scores
            .Select(entry => PageRow(PageSection, NavigationMap.Find(entry.Key), entry.Value))
            .ToList();
      }

      private static List<PaletteRow> MatchSettings(SearchQuery query)
      {
         var rows = new List<PaletteRow>();
         var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

         foreach (SettingMatch match in SettingsSearchIndex.Search(query))
         {
            // A setting on a page the navigation cannot reach would be a dead
            // result. SettingsSearchIndexTests already fails when that happens;
            // this keeps the palette honest in the meantime.
            if (NavigationMap.Find(match.Page) == null)
               continue;

            // The same label legitimately appears on more than one page ("Host",
            // "Port"), so the page is part of the identity; only an exact repeat of
            // both is a duplicate row.
            if (!seen.Add(match.Label + " | " + match.Page))
               continue;

            rows.Add(new PaletteRow(PaletteRowKind.Setting, SettingSection, match.Label,
               match.Location, "Setting  -  " + match.Key,
               match.Page, Add(match.Rank, SettingBase)));
         }

         return rows;
      }

      private static PaletteRow PageRow(string section, NavNode page, int score)
      {
         // A page row is titled with the page, so repeating the page in its own
         // location line says nothing; the group is the part that locates it.
         // Task and setting rows are titled with something else, so those show
         // the whole trail including the page.
         string location = page.Parent != null ? page.Parent.Title : "";
         return new PaletteRow(PaletteRowKind.Page, section, page.Title, location, page.Purpose, page.Key, score);
      }

      private static void Emit(List<PaletteRow> rows, string section, List<PaletteRow> sectionRows)
      {
         if (sectionRows.Count == 0)
            return;

         rows.Add(new PaletteRow(PaletteRowKind.Header, section, section, "", "", null, 0));
         rows.AddRange(sectionRows);
      }

      /// <summary>
      /// Score first, then how often this administrator opens the page, then
      /// title. The usage tie-break is what makes the palette feel like it knows
      /// the installation: several pages routinely tie on score, and the one
      /// this person opens every day should win.
      /// </summary>
      private static void Sort(List<PaletteRow> rows, PaletteUsage usage)
      {
         rows.Sort((left, right) =>
         {
            int byScore = left.Score.CompareTo(right.Score);
            if (byScore != 0)
               return byScore;

            int leftVisits = usage != null ? usage.VisitCount(left.Page) : 0;
            int rightVisits = usage != null ? usage.VisitCount(right.Page) : 0;
            if (leftVisits != rightVisits)
               return rightVisits.CompareTo(leftVisits);

            return string.Compare(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
         });
      }

      /// <summary>Keeps the best (lowest) score seen for a page.</summary>
      private static void Keep(Dictionary<string, int> scores, string key, int score)
      {
         if (score == SearchTerms.NoMatch)
            return;

         if (!scores.TryGetValue(key, out int existing) || score < existing)
            scores[key] = score;
      }

      /// <summary>
      /// Adds a source offset without letting <see cref="SearchTerms.NoMatch"/>
      /// overflow into a very good score, which is exactly what plain addition
      /// on int.MaxValue would do.
      /// </summary>
      private static int Add(int score, int offset)
         => score == SearchTerms.NoMatch ? SearchTerms.NoMatch : score + offset;

      private static int Min(params int[] scores)
      {
         int best = SearchTerms.NoMatch;
         foreach (int score in scores)
         {
            if (score < best)
               best = score;
         }

         return best;
      }
   }
}
