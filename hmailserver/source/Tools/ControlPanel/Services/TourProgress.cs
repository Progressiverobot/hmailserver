// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Which tours this administrator has finished, and where they stopped in
   /// one they left.
   ///
   /// Two facts, because they answer different questions. "Finished" answers
   /// "should the first-run walk start itself the next time this person signs
   /// in", and it is kept with the version it was finished at, so a tour that
   /// grows a stop can be offered again without offering it to everybody every
   /// release. "Where they stopped" answers "take me back to where I was",
   /// which is the whole difference between a tour somebody abandons and one
   /// they abandon and come back to; it is dropped the moment the tour is
   /// finished, and dropped as stale when the version it belongs to has moved.
   ///
   /// Storage is the caller's problem, as it is for <see cref="PaletteUsage"/>:
   /// this serialises to one short string so the shell can keep it in HKCU
   /// beside the window bounds without this file knowing about the registry,
   /// and so the tests need no I/O.
   /// </summary>
   public sealed class TourProgress
   {
      private readonly Dictionary<string, int> finished_ = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      private readonly Dictionary<string, TourStop> stopped_ = new Dictionary<string, TourStop>(StringComparer.OrdinalIgnoreCase);

      private struct TourStop
      {
         public int Version;
         public int Step;
      }

      /// <summary>The ids of every tour finished, in no particular order. Used by the tests and by the palette.</summary>
      public IReadOnlyCollection<string> Finished => finished_.Keys.ToList();

      /// <summary>
      /// Whether this person has finished this tour at this version or later.
      /// A tour finished at an older version answers false, which is what puts
      /// it back in front of somebody after it has grown a stop.
      /// </summary>
      public bool HasFinished(Tour tour)
         => tour != null && finished_.TryGetValue(tour.Id, out int version) && version >= tour.Version;

      /// <summary>
      /// The step to resume at: the one they stopped on, if it is still within
      /// the tour and belongs to this version of it, else the first. Never
      /// returns a step index the tour does not have - a record can outlive the
      /// tour it describes, and resuming past the end would show nothing.
      /// </summary>
      public int ResumeAt(Tour tour)
      {
         if (tour == null || tour.Steps == null || tour.Steps.Count == 0)
            return 0;

         if (!stopped_.TryGetValue(tour.Id, out TourStop stop))
            return 0;

         if (stop.Version != tour.Version || stop.Step < 0 || stop.Step >= tour.Steps.Count)
            return 0;

         return stop.Step;
      }

      /// <summary>
      /// Records that the tour was left on this step. Finishing clears it, so
      /// the record never sends somebody back into a tour they completed.
      /// </summary>
      public void Left(Tour tour, int step)
      {
         if (tour == null)
            return;

         if (step <= 0 || step >= tour.Steps.Count)
         {
            stopped_.Remove(tour.Id);
            return;
         }

         stopped_[tour.Id] = new TourStop { Version = tour.Version, Step = step };
      }

      /// <summary>Records that the tour was seen through to its end, at the version it is now.</summary>
      public void Completed(Tour tour)
      {
         if (tour == null)
            return;

         int was = finished_.TryGetValue(tour.Id, out int version) ? version : 0;
         finished_[tour.Id] = Math.Max(was, tour.Version);
         stopped_.Remove(tour.Id);
      }

      /// <summary>
      /// One line, finished then resume points: "firstrun=1|firstrun@3".
      /// Readable in regedit on purpose - a maintainer looking at "the walk
      /// keeps starting itself" should be able to see the state without a
      /// decoder, and delete it without one either.
      /// </summary>
      public string Serialize()
      {
         var text = new StringBuilder();
         foreach (KeyValuePair<string, int> tour in finished_.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
         {
            if (text.Length > 0)
               text.Append(',');
            text.Append(tour.Key).Append('=').Append(tour.Value.ToString(CultureInfo.InvariantCulture));
         }

         text.Append('|');
         bool first = true;
         foreach (KeyValuePair<string, TourStop> stop in stopped_.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
         {
            if (!first)
               text.Append(',');
            first = false;
            text.Append(stop.Key).Append('@')
               .Append(stop.Value.Step.ToString(CultureInfo.InvariantCulture)).Append('v')
               .Append(stop.Value.Version.ToString(CultureInfo.InvariantCulture));
         }

         return text.ToString();
      }

      /// <summary>
      /// Reads back <see cref="Serialize"/>. Every kind of damage is survivable
      /// and silent, as it is for the palette's history: this comes out of the
      /// registry, where anything can happen to it, and the worst consequence
      /// of losing it is being offered a tour twice. Ids for tours that no
      /// longer exist are kept rather than dropped, because this class does not
      /// know which ids are live - <see cref="TourCatalog"/> is what does.
      /// </summary>
      public static TourProgress Deserialize(string text)
      {
         var progress = new TourProgress();
         if (string.IsNullOrWhiteSpace(text))
            return progress;

         string[] halves = text.Split('|');

         foreach (string pair in halves[0].Split(','))
         {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
               continue;

            string id = pair.Substring(0, equals).Trim();
            if (id.Length == 0)
               continue;

            if (!int.TryParse(pair.Substring(equals + 1).Trim(), NumberStyles.Integer,
                   CultureInfo.InvariantCulture, out int version) || version <= 0)
               continue;

            progress.finished_[id] = version;
         }

         if (halves.Length > 1)
         {
            foreach (string pair in halves[1].Split(','))
            {
               int at = pair.IndexOf('@');
               if (at <= 0)
                  continue;

               string id = pair.Substring(0, at).Trim();
               string rest = pair.Substring(at + 1).Trim();
               int mark = rest.IndexOf('v');
               if (id.Length == 0 || mark <= 0)
                  continue;

               if (!int.TryParse(rest.Substring(0, mark), NumberStyles.Integer, CultureInfo.InvariantCulture, out int step) || step <= 0)
                  continue;
               if (!int.TryParse(rest.Substring(mark + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int version) || version <= 0)
                  continue;

               progress.stopped_[id] = new TourStop { Version = version, Step = step };
            }
         }

         return progress;
      }
   }
}
