using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Everything a chart needs to describe itself in words: its title, its unit,
   /// how its numbers are formatted, and the name of each series.
   ///
   /// The point of pulling this out of the view is that the table view, the
   /// legend, the accessible names and the clipboard export all have to agree with
   /// the picture. When the series names lived as string literals on the LiveCharts
   /// series objects there was nowhere for the table's column headers to come
   /// from, so there was no table.
   /// </summary>
   public sealed class ChartDefinition
   {
      public ChartDefinition(string id, string title, string description, string unit,
                             string valueFormat, params string[] seriesNames)
      {
         if (string.IsNullOrEmpty(id))
            throw new ArgumentException("A chart needs a stable id.", nameof(id));
         if (seriesNames == null || seriesNames.Length == 0)
            throw new ArgumentException("A chart needs at least one series.", nameof(seriesNames));

         Id = id;
         Title = title ?? "";
         Description = description ?? "";
         Unit = unit ?? "";
         ValueFormat = string.IsNullOrEmpty(valueFormat) ? "N0" : valueFormat;
         TimeHeader = "Time";

         var names = new string[seriesNames.Length];
         Array.Copy(seriesNames, names, seriesNames.Length);
         SeriesNames = names;

         var headers = new List<string>(names.Length + 1) { TimeHeader };
         foreach (string name in names)
            headers.Add(Unit.Length == 0 ? name : name + " (" + Unit + ")");
         TableHeaders = headers;
      }

      /// <summary>Stable identifier, e.g. "dashboard.sessions".</summary>
      public string Id { get; }

      public string Title { get; }

      /// <summary>One sentence saying what is plotted and how often it is sampled.</summary>
      public string Description { get; }

      /// <summary>"sessions", "messages / minute" - never null, may be empty.</summary>
      public string Unit { get; }

      /// <summary>A .NET numeric format string, e.g. "N0" or "N1".</summary>
      public string ValueFormat { get; }

      /// <summary>Header of the table view's first column.</summary>
      public string TimeHeader { get; }

      public IReadOnlyList<string> SeriesNames { get; }

      /// <summary>
      /// The table view's column headers: the time column, then one column per
      /// series carrying the unit, so a screen reader announcing a cell says
      /// "SMTP (sessions), 4" rather than a bare number.
      /// </summary>
      public IReadOnlyList<string> TableHeaders { get; }
   }

   /// <summary>
   /// Every chart the Control Panel draws.
   ///
   /// This is the structural half of the "every chart has a table view" promise.
   /// AccessibleChartCard cannot be constructed without a ChartDefinition, and it
   /// builds its table view from that definition, so a chart that renders at all
   /// has a table - there is no code path that produces a picture and no numbers.
   /// The catalogue is what lets a test walk the whole set and check it, and what
   /// makes adding a chart without a table an obviously wrong thing to try.
   /// </summary>
   public static class ChartCatalog
   {
      public const string DashboardThroughputId = "dashboard.throughput";
      public const string DashboardSessionsId = "dashboard.sessions";

      /// <summary>Messages delivered per minute, on the Dashboard page.</summary>
      public static ChartDefinition DashboardThroughput { get; } = new ChartDefinition(
         DashboardThroughputId,
         "Delivery throughput",
         "Messages the server finished processing, per minute, sampled every 2 seconds.",
         "messages / minute",
         // A rate derived from a counter difference over a ~2 second window is
         // rarely a whole number; rounding it to "0" or "60" in the table would
         // be inventing precision the chart does not have.
         "N1",
         "Delivered");

      /// <summary>Concurrent protocol sessions, on the Dashboard page.</summary>
      public static ChartDefinition DashboardSessions { get; } = new ChartDefinition(
         DashboardSessionsId,
         "Active sessions",
         "Concurrent SMTP, IMAP and POP3 sessions, sampled every 2 seconds.",
         "sessions",
         "N0",
         "SMTP", "IMAP", "POP3");

      public static IReadOnlyList<ChartDefinition> All { get; } = new[]
      {
         DashboardThroughput,
         DashboardSessions
      };

      /// <summary>The definition with this id, or null when there is no such chart.</summary>
      public static ChartDefinition ById(string id)
         => All.FirstOrDefault(definition => string.Equals(definition.Id, id, StringComparison.Ordinal));
   }
}
