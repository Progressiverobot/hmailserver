using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>One series' history. A null entry is a sample that was not taken.</summary>
   public sealed class ChartSeriesSamples
   {
      public ChartSeriesSamples(string name, IReadOnlyList<double?> values)
      {
         Name = name ?? "";
         Values = values ?? Array.Empty<double?>();
      }

      public string Name { get; }

      /// <summary>Oldest first, so the newest sample is the last entry.</summary>
      public IReadOnlyList<double?> Values { get; }
   }

   /// <summary>The numbers a reader actually wants: where it is now, and how bad it got.</summary>
   public sealed class ChartSeriesSummary
   {
      internal ChartSeriesSummary(string name, double? latest, double? peak, double? average, int sampleCount)
      {
         Name = name;
         Latest = latest;
         Peak = peak;
         Average = average;
         SampleCount = sampleCount;
      }

      public string Name { get; }

      /// <summary>The newest sample that carries a value, or null when there is none.</summary>
      public double? Latest { get; }

      public double? Peak { get; }

      public double? Average { get; }

      /// <summary>How many samples carried a value (nulls are not counted).</summary>
      public int SampleCount { get; }
   }

   /// <summary>
   /// A one-line spoken/readable description of a chart, plus the per-series
   /// numbers behind it. Computed on every refresh because it is cheap; the full
   /// table is only built when it is on screen.
   /// </summary>
   public sealed class ChartSummary
   {
      internal ChartSummary(IReadOnlyList<ChartSeriesSummary> series, bool hasData, string text)
      {
         Series = series;
         HasData = hasData;
         Text = text;
      }

      public IReadOnlyList<ChartSeriesSummary> Series { get; }

      /// <summary>False when no series has produced a single value yet.</summary>
      public bool HasData { get; }

      /// <summary>Latest and peak per series, as a sentence.</summary>
      public string Text { get; }
   }

   /// <summary>One row of a chart's table view: a timestamp and one cell per series.</summary>
   public sealed class ChartTableRow
   {
      internal ChartTableRow(string timeLabel, string[] cells)
      {
         TimeLabel = timeLabel;
         Cells = cells;
      }

      public string TimeLabel { get; }

      /// <summary>
      /// Already-formatted values, one per series, in the order of
      /// <see cref="ChartDefinition.SeriesNames"/>. A string array rather than a
      /// list because WPF's <c>Cells[0]</c> binding path resolves an array's
      /// indexer directly.
      /// </summary>
      public string[] Cells { get; }
   }

   /// <summary>
   /// The table view of a chart: real column headers, one row per sample, newest
   /// first, with the numbers as text.
   ///
   /// This is not a fallback for the picture, it is the same data in the form that
   /// works for the three cases the chart cannot serve. A High Contrast reader
   /// gets values instead of near-identical lines; a screen-reader user gets a
   /// grid with headers instead of a Skia canvas that exposes nothing at all to
   /// UI Automation; and anybody who wants to know whether the spike was 41 or 43
   /// can read it off, which no amount of tooltip work makes reliable on a
   /// two-second sampling interval.
   ///
   /// Newest first is deliberate. Reading a table with a screen reader is
   /// sequential, and the row a reader wants is nearly always the one that just
   /// arrived; putting it last would mean 90 rows of history before the answer.
   /// </summary>
   public sealed class ChartDataTable
   {
      /// <summary>Shown for a sample that was not taken. Never "0" - see Build.</summary>
      public const string NoValue = "-";

      private ChartDataTable(ChartDefinition definition, IReadOnlyList<string> headers,
                             IReadOnlyList<ChartTableRow> rows, ChartSummary summary, string caption)
      {
         Definition = definition;
         Headers = headers;
         Rows = rows;
         Summary = summary;
         Caption = caption;
      }

      public ChartDefinition Definition { get; }

      /// <summary>
      ///    Refuses a series list that does not match the definition, so that a table
      ///    can never be built with more or fewer value columns than it has headers.
      /// </summary>
      private static void AssertSeriesMatchDefinition_(ChartDefinition definition,
                                                       IReadOnlyList<ChartSeriesSamples> series)
      {
         int supplied = series == null ? 0 : series.Count;
         int expected = definition.SeriesNames.Count;

         if (supplied == expected)
            return;

         throw new ArgumentException(
            "The chart '" + definition.Title + "' defines " + expected + " series but "
            + supplied + " were supplied. Headers come from the definition and values "
            + "from this list, so a mismatch builds a table whose columns do not line up.",
            nameof(series));
      }

      /// <summary>Time column, then one per series - <see cref="ChartDefinition.TableHeaders"/>.</summary>
      public IReadOnlyList<string> Headers { get; }

      /// <summary>Newest sample first.</summary>
      public IReadOnlyList<ChartTableRow> Rows { get; }

      public ChartSummary Summary { get; }

      /// <summary>Title and unit, used as the grid's accessible name.</summary>
      public string Caption { get; }

      public bool IsEmpty => Rows.Count == 0;

      /// <summary>
      /// Builds the table. Series are aligned at their newest sample, not their
      /// oldest: these are trimmed ring buffers, so if one series has fewer
      /// samples than another it is the <em>start</em> of its history that is
      /// missing, and lining them up from index zero would report every value
      /// against the wrong timestamp.
      /// </summary>
      public static ChartDataTable Build(ChartDefinition definition,
                                         IReadOnlyList<ChartSeriesSamples> series,
                                         IReadOnlyList<DateTime> times)
      {
         if (definition == null)
            throw new ArgumentNullException(nameof(definition));

         // The headers come from the definition and the value cells from the series
         // list, so a caller passing a series list of a different length than the
         // definition describes builds a table whose rows and headers disagree - and
         // ToDelimitedText then emits a TSV with the wrong number of columns, which
         // pastes into a spreadsheet as silently misaligned data rather than as an
         // error. Nothing in the application does this today (Push always supplies one
         // value per series), so this guards the next caller rather than fixing a live
         // defect, which is when a guard is cheapest.
         //
         // Thrown rather than tolerated: a mismatch means the caller and the definition
         // disagree about what the chart is, and quietly padding or truncating would
         // turn a wiring mistake into wrong numbers on a screen somebody trusts.
         AssertSeriesMatchDefinition_(definition, series);

         int sampleCount = SampleCount_(series);
         var rows = new List<ChartTableRow>(sampleCount);

         for (int i = sampleCount - 1; i >= 0; i--)
            rows.Add(Row_(definition, series, times, sampleCount, i));

         return new ChartDataTable(definition, definition.TableHeaders, rows,
            Summarise(definition, series), Caption_(definition));
      }

      /// <summary>
      /// Just the newest row, or null when there are no samples at all.
      ///
      /// This exists so that a live table can prepend the sample that just arrived
      /// instead of being rebuilt from scratch every two seconds. Rebuilding drops
      /// the reader's selected row and scroll position - which, on a page whose
      /// whole purpose is to let somebody read the numbers, is the difference
      /// between a table and a table you cannot use. It shares
      /// <see cref="Row_"/> with <see cref="Build"/> so the incremental row and the
      /// batch row can never format differently.
      /// </summary>
      public static ChartTableRow BuildNewestRow(ChartDefinition definition,
                                                 IReadOnlyList<ChartSeriesSamples> series,
                                                 IReadOnlyList<DateTime> times)
      {
         if (definition == null)
            throw new ArgumentNullException(nameof(definition));

         int sampleCount = SampleCount_(series);
         return sampleCount == 0 ? null : Row_(definition, series, times, sampleCount, sampleCount - 1);
      }

      private static int SampleCount_(IReadOnlyList<ChartSeriesSamples> series)
      {
         if (series == null)
            return 0;

         int count = 0;
         for (int s = 0; s < series.Count; s++)
            count = Math.Max(count, series[s].Values.Count);
         return count;
      }

      private static ChartTableRow Row_(ChartDefinition definition, IReadOnlyList<ChartSeriesSamples> series,
                                        IReadOnlyList<DateTime> times, int sampleCount, int index)
      {
         // No series list means no value cells - normalised here so the indexing
         // below is provably never a null dereference.
         series ??= Array.Empty<ChartSeriesSamples>();
         var cells = new string[series.Count];

         for (int s = 0; s < series.Count; s++)
         {
            IReadOnlyList<double?> values = series[s].Values;
            int offset = sampleCount - values.Count;
            double? value = index >= offset ? values[index - offset] : null;
            cells[s] = Format(value, definition.ValueFormat);
         }

         int timeIndex = index - (sampleCount - (times?.Count ?? 0));
         string label = times != null && timeIndex >= 0 && timeIndex < times.Count
            ? times[timeIndex].ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : NoValue;

         return new ChartTableRow(label, cells);
      }

      /// <summary>
      /// The cheap path: latest, peak and average per series plus the sentence
      /// that describes them, without materialising a row per sample. The
      /// dashboard calls this every two seconds whether the table is showing or
      /// not, because it feeds the always-visible summary line under the chart and
      /// the chart's own accessible description.
      /// </summary>
      public static ChartSummary Summarise(ChartDefinition definition, IReadOnlyList<ChartSeriesSamples> series)
      {
         if (definition == null)
            throw new ArgumentNullException(nameof(definition));

         var summaries = new List<ChartSeriesSummary>();
         bool hasData = false;

         series ??= Array.Empty<ChartSeriesSamples>();
         for (int s = 0; s < series.Count; s++)
         {
            IReadOnlyList<double?> values = series[s].Values;
            double? latest = null;
            double? peak = null;
            double total = 0;
            int counted = 0;

            for (int i = 0; i < values.Count; i++)
            {
               double? value = values[i];
               if (!value.HasValue)
                  continue;

               latest = value;
               peak = peak.HasValue ? Math.Max(peak.Value, value.Value) : value.Value;
               total += value.Value;
               counted++;
            }

            hasData |= counted > 0;
            summaries.Add(new ChartSeriesSummary(series[s].Name, latest, peak,
               counted > 0 ? total / counted : (double?) null, counted));
         }

         return new ChartSummary(summaries, hasData, SummaryText_(definition, summaries, hasData));
      }

      /// <summary>
      /// The whole table as delimited text, header row included, for the Copy
      /// button. Tab-separated by default because that is what pastes into a
      /// spreadsheet as columns, and no cell can contain a tab: every cell here is
      /// a formatted number or a HH:mm:ss timestamp.
      /// </summary>
      public string ToDelimitedText(char separator = '\t')
      {
         var text = new StringBuilder();

         for (int i = 0; i < Headers.Count; i++)
         {
            if (i > 0)
               text.Append(separator);
            text.Append(Headers[i]);
         }
         text.Append("\r\n");

         foreach (ChartTableRow row in Rows)
         {
            text.Append(row.TimeLabel);
            foreach (string cell in row.Cells)
            {
               text.Append(separator);
               text.Append(cell);
            }
            text.Append("\r\n");
         }

         return text.ToString();
      }

      /// <summary>
      /// Formats one value. A sample that was never taken reads as "-", never as
      /// "0": on the throughput chart the first sample is genuinely unknown
      /// (a rate needs two counter readings), and printing it as zero would
      /// invent a delivery outage that never happened.
      /// </summary>
      public static string Format(double? value, string valueFormat)
      {
         return value.HasValue
            ? value.Value.ToString(valueFormat ?? "N0", CultureInfo.CurrentCulture)
            : NoValue;
      }

      private static string Caption_(ChartDefinition definition)
      {
         return definition.Unit.Length == 0
            ? definition.Title
            : definition.Title + " (" + definition.Unit + ")";
      }

      private static string SummaryText_(ChartDefinition definition,
                                         IReadOnlyList<ChartSeriesSummary> summaries, bool hasData)
      {
         if (!hasData)
            return "No samples yet.";

         var text = new StringBuilder();
         text.Append("Latest ").Append(Values_(definition, summaries, true));
         text.Append("; peak ").Append(Values_(definition, summaries, false));

         if (definition.Unit.Length > 0)
            text.Append(' ').Append(definition.Unit);

         int samples = 0;
         foreach (ChartSeriesSummary summary in summaries)
            samples = Math.Max(samples, summary.SampleCount);
         text.Append("; ").Append(samples.ToString(CultureInfo.CurrentCulture));
         text.Append(samples == 1 ? " sample." : " samples.");

         return text.ToString();
      }

      private static string Values_(ChartDefinition definition,
                                    IReadOnlyList<ChartSeriesSummary> summaries, bool latest)
      {
         var text = new StringBuilder();
         for (int i = 0; i < summaries.Count; i++)
         {
            if (i > 0)
               text.Append(", ");

            // A single-series chart names itself in the card title, so repeating
            // the series name here ("Latest Delivered 12.5") only adds noise for
            // someone listening to it.
            if (summaries.Count > 1)
               text.Append(summaries[i].Name).Append(' ');

            text.Append(Format(latest ? summaries[i].Latest : summaries[i].Peak, definition.ValueFormat));
         }

         return text.ToString();
      }
   }
}
