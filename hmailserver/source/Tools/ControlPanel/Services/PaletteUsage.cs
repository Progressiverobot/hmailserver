using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Which pages this administrator actually opens, so that the command
   /// palette can offer them before it offers the other forty.
   ///
   /// Two orderings are kept because they answer different questions.
   /// "Recently visited" answers "take me back to what I was just doing",
   /// which is what someone comparing two pages needs, and it has to be
   /// recency-ordered even for a page visited once. "Most used" answers "the
   /// handful of pages this installation lives in", which is stable across
   /// sessions and is what makes an empty palette useful on a Monday morning.
   /// A single frecency score was considered and rejected: it collapses both
   /// questions into one list that answers neither, and it cannot be explained
   /// to a user in a section heading.
   ///
   /// Storage is the caller's problem: this class serialises to and from one
   /// short string so the Control Panel can keep it beside the window bounds in
   /// HKCU without this file having to know about the registry, and so the tests
   /// need no I/O at all.
   /// </summary>
   public sealed class PaletteUsage
   {
      /// <summary>
      /// How many pages "Recently visited" remembers. Eight is about one
      /// palette-height of rows: long enough to cover a session's worth of
      /// back-and-forth, short enough that the list is still a shortlist rather
      /// than a second copy of the navigation tree.
      /// </summary>
      public const int MaxRecent = 8;

      /// <summary>
      /// Visit counts are clamped here. A Control Panel left open for months on
      /// a monitoring page would otherwise accumulate an unbounded number that
      /// no other page could ever overtake, freezing "Most used" permanently.
      /// </summary>
      public const int MaxVisitCount = 9999;

      private readonly Dictionary<string, int> visits_ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      private readonly List<string> recent_ = new List<string>();

      /// <summary>Pages visited, most recent first.</summary>
      public IReadOnlyList<string> Recent => recent_;

      /// <summary>Pages by visit count, most visited first, ties broken by recency.</summary>
      public IReadOnlyList<string> MostUsed
      {
         get
         {
            // Recency as the tie-break rather than the page key: with a fresh
            // profile almost every count is 1, and alphabetical order would
            // make "Most used" a list of pages beginning with A.
            return visits_.Keys
               .OrderByDescending(key => visits_[key])
               .ThenBy(key =>
               {
                  int index = recent_.FindIndex(r => string.Equals(r, key, StringComparison.OrdinalIgnoreCase));
                  return index < 0 ? int.MaxValue : index;
               })
               .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
               .ToList();
         }
      }

      /// <summary>How many times a page has been opened.</summary>
      public int VisitCount(string pageKey)
         => pageKey != null && visits_.TryGetValue(pageKey, out int count) ? count : 0;

      /// <summary>
      /// Records that a page was opened. Idempotent for the page that is
      /// already at the head of the recent list, because re-entering the page
      /// you are on - which happens on every reconnect, and every time the
      /// navigation tree re-raises its selection - is not a visit and must not
      /// inflate the count.
      /// </summary>
      public void RecordVisit(string pageKey)
      {
         if (string.IsNullOrWhiteSpace(pageKey))
            return;

         if (recent_.Count > 0 && string.Equals(recent_[0], pageKey, StringComparison.OrdinalIgnoreCase))
            return;

         visits_[pageKey] = Math.Min(MaxVisitCount, VisitCount(pageKey) + 1);

         recent_.RemoveAll(r => string.Equals(r, pageKey, StringComparison.OrdinalIgnoreCase));
         recent_.Insert(0, pageKey);
         if (recent_.Count > MaxRecent)
            recent_.RemoveRange(MaxRecent, recent_.Count - MaxRecent);
      }

      /// <summary>
      /// One line, counts then recency: "antispam=7,queue=3|queue,antispam".
      /// Deliberately readable in regedit - a maintainer looking at a bug report
      /// about the palette offering the wrong pages should be able to see the
      /// state without a decoder.
      /// </summary>
      public string Serialize()
      {
         var counts = new StringBuilder();
         foreach (KeyValuePair<string, int> visit in visits_.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
         {
            if (counts.Length > 0)
               counts.Append(',');
            counts.Append(visit.Key).Append('=').Append(visit.Value.ToString(CultureInfo.InvariantCulture));
         }

         return counts.Append('|').Append(string.Join(",", recent_)).ToString();
      }

      /// <summary>
      /// Reads back <see cref="Serialize"/>. Every kind of damage is survivable
      /// and silent: this comes out of the registry, where anything can happen
      /// to it, and losing the palette's history is not worth an error dialog on
      /// start-up. Unknown page keys are kept rather than dropped, because this
      /// class does not know which keys are live - the palette filters them
      /// against the navigation map, which is the component that does.
      /// </summary>
      public static PaletteUsage Deserialize(string text)
      {
         var usage = new PaletteUsage();
         if (string.IsNullOrWhiteSpace(text))
            return usage;

         string[] halves = text.Split('|');

         foreach (string pair in halves[0].Split(','))
         {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
               continue;

            string key = pair.Substring(0, equals).Trim();
            if (key.Length == 0)
               continue;

            if (!int.TryParse(pair.Substring(equals + 1).Trim(), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out int count) || count <= 0)
               continue;

            usage.visits_[key] = Math.Min(MaxVisitCount, count);
         }

         if (halves.Length > 1)
         {
            foreach (string trimmed in halves[1].Split(',').Select(key => key.Trim()))
            {
               if (trimmed.Length == 0 || usage.recent_.Count >= MaxRecent)
                  continue;
               if (usage.recent_.Any(r => string.Equals(r, trimmed, StringComparison.OrdinalIgnoreCase)))
                  continue;

               usage.recent_.Add(trimmed);
            }
         }

         return usage;
      }
   }
}
