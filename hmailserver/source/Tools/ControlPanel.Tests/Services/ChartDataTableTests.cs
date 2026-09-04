// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The table-view half of the chart accessibility work.
   ///
   /// Why these fail against the unfixed Control Panel: no chart had a table view of
   /// any kind. The dashboard handed LiveCharts two collections of ObservableValue -
   /// a number each, no timestamp - and hid the X axis, so the "when" of every
   /// sample existed only as a pixel position, and the series names existed only as
   /// a label on a Skia-rendered legend. There was nothing to build column headers
   /// from and no history to tabulate, which is the concrete reason the eight
   /// accessibility items had not been started rather than merely not finished.
   ///
   /// The first test is the "every chart type exposes a table view" check. It walks
   /// ChartCatalog, which is the whole set of charts the Control Panel draws:
   /// AccessibleChartCard cannot be constructed without one of these definitions and
   /// builds its table from it, so a chart that renders has a table by construction.
   /// </summary>
   public class ChartDataTableTests : IDisposable
   {
      private readonly CultureInfo culture_;

      /// <summary>
      /// The table formats numbers and timestamps for the person reading them, so
      /// production code formats with the current culture - which means these tests
      /// have to pin one, or "12.5" becomes "12,5" on a build agent in a different
      /// locale and the suite fails for no reason. xUnit constructs the class per
      /// test and disposes it afterwards, so the swap is scoped to one test and the
      /// thread is handed back as it was found.
      /// </summary>
      public ChartDataTableTests()
      {
         culture_ = CultureInfo.CurrentCulture;
         CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
      }

      public void Dispose()
      {
         CultureInfo.CurrentCulture = culture_;
      }

      [Fact]
      public void EveryChartInTheCatalogExposesATableView()
      {
         Assert.NotEmpty(ChartCatalog.All);

         foreach (ChartDefinition definition in ChartCatalog.All)
         {
            IReadOnlyList<ChartSeriesSamples> series = Samples_(definition, 3);
            var times = new List<DateTime>
            {
               new DateTime(2026, 8, 12, 9, 0, 0),
               new DateTime(2026, 8, 12, 9, 0, 2),
               new DateTime(2026, 8, 12, 9, 0, 4)
            };

            ChartDataTable table = ChartDataTable.Build(definition, series, times);

            Assert.False(table.IsEmpty, definition.Id + " produced an empty table.");
            Assert.Equal(definition.SeriesNames.Count + 1, table.Headers.Count);
            Assert.Equal(definition.TimeHeader, table.Headers[0]);

            for (int i = 0; i < definition.SeriesNames.Count; i++)
            {
               string header = table.Headers[i + 1];
               Assert.Contains(definition.SeriesNames[i], header);
               Assert.Contains(definition.Unit, header);
            }

            Assert.Equal(3, table.Rows.Count);
            Assert.All(table.Rows, row => Assert.Equal(definition.SeriesNames.Count, row.Cells.Length));
            Assert.All(table.Rows, row => Assert.False(string.IsNullOrEmpty(row.TimeLabel)));

            Assert.Contains(definition.Title, table.Caption);
            Assert.False(string.IsNullOrWhiteSpace(table.Summary.Text), definition.Id + " has no summary.");
            Assert.True(table.Summary.HasData);
            Assert.Equal(definition.SeriesNames.Count, table.Summary.Series.Count);

            string text = table.ToDelimitedText();
            foreach (string header in table.Headers)
               Assert.Contains(header, text);
         }
      }

      [Fact]
      public void CatalogIdsAreUniqueAndResolvable()
      {
         Assert.Equal(ChartCatalog.All.Count, ChartCatalog.All.Select(d => d.Id).Distinct().Count());

         foreach (ChartDefinition definition in ChartCatalog.All)
            Assert.Same(definition, ChartCatalog.ById(definition.Id));

         Assert.Null(ChartCatalog.ById("no.such.chart"));
      }

      [Fact]
      public void CatalogContainsBothDashboardCharts()
      {
         // Pins the two ids the Dashboard page asks for by name. Renaming one
         // without updating the page would otherwise leave the dashboard throwing a
         // null reference on startup.
         Assert.NotNull(ChartCatalog.ById(ChartCatalog.DashboardThroughputId));
         Assert.NotNull(ChartCatalog.ById(ChartCatalog.DashboardSessionsId));
         Assert.Same(ChartCatalog.DashboardThroughput, ChartCatalog.ById(ChartCatalog.DashboardThroughputId));
         Assert.Same(ChartCatalog.DashboardSessions, ChartCatalog.ById(ChartCatalog.DashboardSessionsId));
      }

      [Fact]
      public void BuildRefusesASeriesListThatDoesNotMatchTheDefinition()
      {
         // Headers come from the definition and value cells from the series list, so a
         // caller supplying a different number of series than the chart defines builds a
         // table whose columns do not line up - and ToDelimitedText then emits a TSV that
         // pastes into a spreadsheet as misaligned data rather than as an error. Refused
         // rather than padded: a mismatch means the caller and the definition disagree
         // about what the chart is, and guessing turns a wiring mistake into wrong
         // numbers on a screen somebody trusts.
         ChartDefinition definition = ChartCatalog.DashboardSessions;   // three series

         var tooFew = new[]
         {
            new ChartSeriesSamples("SMTP", new double?[] { 1, 2, 3 })
         };

         var times = new List<DateTime> { DateTime.Now, DateTime.Now, DateTime.Now };

         Assert.Throws<ArgumentException>(() => ChartDataTable.Build(definition, tooFew, times));
      }

      [Fact]
      public void RowsAreNewestFirst()
      {
         // Reading a table with a screen reader is sequential, and the row the
         // reader wants is nearly always the one that just arrived.
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         var series = new[]
         {
            new ChartSeriesSamples("SMTP", new double?[] { 1, 2, 3 }),
            new ChartSeriesSamples("IMAP", new double?[] { 4, 5, 6 }),
            new ChartSeriesSamples("POP3", new double?[] { 7, 8, 9 })
         };
         var times = new List<DateTime>
         {
            new DateTime(2026, 8, 12, 9, 0, 0),
            new DateTime(2026, 8, 12, 9, 0, 2),
            new DateTime(2026, 8, 12, 9, 0, 4)
         };

         ChartDataTable table = ChartDataTable.Build(definition, series, times);

         Assert.Equal("09:00:04", table.Rows[0].TimeLabel);
         Assert.Equal(new[] { "3", "6", "9" }, table.Rows[0].Cells);
         Assert.Equal("09:00:00", table.Rows[2].TimeLabel);
         Assert.Equal(new[] { "1", "4", "7" }, table.Rows[2].Cells);
      }

      [Fact]
      public void ASampleThatWasNeverTakenReadsAsNoValueNotAsZero()
      {
         // The throughput chart's first sample is genuinely unknown - a rate needs
         // two counter readings - and printing it as 0 would invent a delivery
         // outage that never happened.
         ChartDefinition definition = ChartCatalog.DashboardThroughput;
         var series = new[] { new ChartSeriesSamples("Delivered", new double?[] { null, 12.5 }) };

         ChartDataTable table = ChartDataTable.Build(definition, series, null);

         Assert.Equal("12.5", table.Rows[0].Cells[0]);
         Assert.Equal(ChartDataTable.NoValue, table.Rows[1].Cells[0]);
         Assert.NotEqual("0", table.Rows[1].Cells[0]);
      }

      [Fact]
      public void SeriesWithShorterHistoriesLineUpAtTheirNewestSample()
      {
         // These are trimmed ring buffers: a series with fewer samples is missing
         // the start of its history, not the end. Aligning from index zero would
         // report every value against the wrong timestamp.
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         var series = new[]
         {
            new ChartSeriesSamples("SMTP", new double?[] { 1, 2, 3 }),
            new ChartSeriesSamples("IMAP", new double?[] { 5, 6 }),
            new ChartSeriesSamples("POP3", new double?[] { 9 })
         };

         ChartDataTable table = ChartDataTable.Build(definition, series, null);

         Assert.Equal(3, table.Rows.Count);
         Assert.Equal(new[] { "3", "6", "9" }, table.Rows[0].Cells);
         Assert.Equal(new[] { "2", "5", ChartDataTable.NoValue }, table.Rows[1].Cells);
         Assert.Equal(new[] { "1", ChartDataTable.NoValue, ChartDataTable.NoValue }, table.Rows[2].Cells);
      }

      [Fact]
      public void TheTimeColumnAlsoLinesUpAtTheNewestSample()
      {
         ChartDefinition definition = ChartCatalog.DashboardThroughput;
         var series = new[] { new ChartSeriesSamples("Delivered", new double?[] { 1, 2, 3 }) };

         // Only two timestamps for three samples: the oldest row has no time.
         var times = new List<DateTime>
         {
            new DateTime(2026, 8, 12, 9, 0, 2),
            new DateTime(2026, 8, 12, 9, 0, 4)
         };

         ChartDataTable table = ChartDataTable.Build(definition, series, times);

         Assert.Equal("09:00:04", table.Rows[0].TimeLabel);
         Assert.Equal("09:00:02", table.Rows[1].TimeLabel);
         Assert.Equal(ChartDataTable.NoValue, table.Rows[2].TimeLabel);
      }

      [Fact]
      public void TheNewestRowIsFormattedExactlyLikeTheFullTablesFirstRow()
      {
         // The live table prepends this row every two seconds instead of rebuilding
         // ninety of them, so the two paths have to agree.
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         IReadOnlyList<ChartSeriesSamples> series = Samples_(definition, 5);
         var times = new List<DateTime>();
         for (int i = 0; i < 5; i++)
            times.Add(new DateTime(2026, 8, 12, 9, 0, 0).AddSeconds(2.0 * i));

         ChartDataTable table = ChartDataTable.Build(definition, series, times);
         ChartTableRow newest = ChartDataTable.BuildNewestRow(definition, series, times);

         Assert.NotNull(newest);
         Assert.Equal(table.Rows[0].TimeLabel, newest.TimeLabel);
         Assert.Equal(table.Rows[0].Cells, newest.Cells);
      }

      [Fact]
      public void TheNewestRowIsNullBeforeTheFirstSample()
      {
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         var empty = new[]
         {
            new ChartSeriesSamples("SMTP", new double?[0]),
            new ChartSeriesSamples("IMAP", new double?[0]),
            new ChartSeriesSamples("POP3", new double?[0])
         };

         Assert.Null(ChartDataTable.BuildNewestRow(definition, empty, null));
         Assert.True(ChartDataTable.Build(definition, empty, null).IsEmpty);
      }

      [Fact]
      public void SummariseReportsLatestPeakAverageAndSampleCount()
      {
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         var series = new[] { new ChartSeriesSamples("SMTP", new double?[] { 2, 10, 6 }) };

         ChartSummary summary = ChartDataTable.Summarise(definition, series);

         ChartSeriesSummary smtp = Assert.Single(summary.Series);
         Assert.Equal(6, smtp.Latest);
         Assert.Equal(10, smtp.Peak);
         Assert.Equal(6, smtp.Average);
         Assert.Equal(3, smtp.SampleCount);
         Assert.True(summary.HasData);
      }

      [Fact]
      public void SummariseSkipsGapsRatherThanCountingThemAsZero()
      {
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         var series = new[] { new ChartSeriesSamples("SMTP", new double?[] { null, 4, null, 8 }) };

         ChartSeriesSummary smtp = Assert.Single(ChartDataTable.Summarise(definition, series).Series);

         Assert.Equal(8, smtp.Latest);
         Assert.Equal(6, smtp.Average);
         Assert.Equal(2, smtp.SampleCount);
      }

      [Fact]
      public void SummariseSaysSoWhenThereIsNothingToSay()
      {
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         var series = new[] { new ChartSeriesSamples("SMTP", new double?[] { null, null }) };

         ChartSummary summary = ChartDataTable.Summarise(definition, series);

         Assert.False(summary.HasData);
         Assert.Equal("No samples yet.", summary.Text);
         Assert.Null(summary.Series[0].Latest);
         Assert.Null(summary.Series[0].Average);
         Assert.Equal(0, summary.Series[0].SampleCount);
      }

      [Fact]
      public void TheSummaryNamesEachSeriesOnlyWhenThereIsMoreThanOne()
      {
         ChartSummary multi = ChartDataTable.Summarise(ChartCatalog.DashboardSessions, new[]
         {
            new ChartSeriesSamples("SMTP", new double?[] { 4 }),
            new ChartSeriesSamples("IMAP", new double?[] { 2 }),
            new ChartSeriesSamples("POP3", new double?[] { 0 })
         });

         Assert.Contains("SMTP 4", multi.Text);
         Assert.Contains("IMAP 2", multi.Text);
         Assert.Contains("sessions", multi.Text);
         Assert.Contains("1 sample.", multi.Text);

         // A single-series chart already names itself in the card title, so
         // repeating it here is noise for somebody listening to the page.
         ChartSummary single = ChartDataTable.Summarise(ChartCatalog.DashboardThroughput, new[]
         {
            new ChartSeriesSamples("Delivered", new double?[] { 12.5, 40 })
         });

         Assert.DoesNotContain("Delivered", single.Text);
         Assert.Contains("Latest 40.0", single.Text);
         Assert.Contains("peak 40.0", single.Text);
         Assert.Contains("2 samples.", single.Text);
      }

      [Fact]
      public void DelimitedTextHasAHeaderRowAndOneRowPerSample()
      {
         ChartDefinition definition = ChartCatalog.DashboardSessions;
         var series = new[]
         {
            new ChartSeriesSamples("SMTP", new double?[] { 1, 2 }),
            new ChartSeriesSamples("IMAP", new double?[] { 3, 4 }),
            new ChartSeriesSamples("POP3", new double?[] { 5, 6 })
         };
         var times = new List<DateTime>
         {
            new DateTime(2026, 8, 12, 9, 0, 0),
            new DateTime(2026, 8, 12, 9, 0, 2)
         };

         string text = ChartDataTable.Build(definition, series, times).ToDelimitedText();
         string[] lines = text.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

         Assert.Equal(3, lines.Length);
         Assert.Equal("Time\tSMTP (sessions)\tIMAP (sessions)\tPOP3 (sessions)", lines[0]);
         Assert.Equal("09:00:02\t2\t4\t6", lines[1]);
         Assert.Equal("09:00:00\t1\t3\t5", lines[2]);
      }

      [Fact]
      public void AChartDefinitionNeedsAnIdAndAtLeastOneSeries()
      {
         Assert.Throws<ArgumentException>(() => new ChartDefinition("", "T", "D", "u", "N0", "A"));
         Assert.Throws<ArgumentException>(() => new ChartDefinition("id", "T", "D", "u", "N0"));
      }

      [Fact]
      public void BuildAndSummariseRefuseANullDefinition()
      {
         Assert.Throws<ArgumentNullException>(() => ChartDataTable.Build(null, null, null));
         Assert.Throws<ArgumentNullException>(() => ChartDataTable.Summarise(null, null));
         Assert.Throws<ArgumentNullException>(() => ChartDataTable.BuildNewestRow(null, null, null));
      }

      /// <summary>Synthetic history: series index + 1, then + 1 per sample.</summary>
      private static IReadOnlyList<ChartSeriesSamples> Samples_(ChartDefinition definition, int count)
      {
         var series = new List<ChartSeriesSamples>();
         for (int s = 0; s < definition.SeriesNames.Count; s++)
         {
            var values = new double?[count];
            for (int i = 0; i < count; i++)
               values[i] = s + 1 + i;
            series.Add(new ChartSeriesSamples(definition.SeriesNames[s], values));
         }

         return series;
      }
   }
}
