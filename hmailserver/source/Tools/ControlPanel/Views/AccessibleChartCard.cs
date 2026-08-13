using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// A chart with the three things a chart on its own cannot provide: a palette
   /// that follows the theme (including High Contrast) and re-follows it when the
   /// theme changes at runtime, a table view of the same data reachable from the
   /// keyboard, and a legend that identifies each series by dash pattern and shape
   /// as well as by colour.
   ///
   /// Every chart in the Control Panel is one of these. That is the mechanism
   /// behind "every chart has a table view": the card cannot be constructed without
   /// a <see cref="ChartDefinition"/>, and it builds the table, the legend, the
   /// summary line and the clipboard export from that same definition and the same
   /// sample history that feeds the picture. There is no way to add a chart here
   /// that draws a line and has no numbers behind it.
   ///
   /// The card owns the sample history rather than taking a collection from its
   /// caller. When the chart owned LiveCharts' ObservableValue collections and
   /// nothing else, there was no history to tabulate - ObservableValue carries a
   /// number and no timestamp, and the axis was hidden, so the "when" of every
   /// sample existed only as a pixel position. Holding the values and the sample
   /// times together is what makes a table with a Time column possible, and it
   /// guarantees the table and the picture can never disagree.
   /// </summary>
   public sealed class AccessibleChartCard : UserControl
   {
      private const int DefaultHistoryLength = 90;
      private const double SwatchWidth = 26;
      private const double SwatchHeight = 12;

      // One shared typeface: SKTypeface is unmanaged and the palette is rebuilt on
      // every theme change, so allocating one per rebuild would leak steadily for
      // as long as the user keeps toggling the theme.
      private static readonly SKTypeface AxisTypeface = SKTypeface.FromFamilyName("Segoe UI");

      private readonly ChartDefinition definition_;
      private readonly int historyLength_;

      private readonly Border surface_ = new();
      private readonly CartesianChart chart_ = new();
      private readonly DataGrid table_ = new();
      private readonly TextBlock titleText_ = new();
      private readonly TextBlock placeholder_ = new();
      private readonly TextBlock summaryText_ = new();
      private readonly WrapPanel legend_ = new();
      private readonly Wpf.Ui.Controls.Button viewButton_ = new();
      private readonly Wpf.Ui.Controls.Button copyButton_ = new();

      private readonly ObservableCollection<TableRow> tableRows_ = new();
      private readonly List<double?>[] samples_;
      private readonly ObservableCollection<ObservableValue>[] plotted_;
      private readonly List<DateTime> times_ = new();

      /// <summary>Legend value labels, in series order, updated in place on refresh.</summary>
      private readonly List<TextBlock> legendValues_ = new();

      private IReadOnlyList<ChartSeriesStyle> styles_;
      private ChartSummary summary_;
      private bool tableVisible_;
      private bool viewChosenByUser_;

      public AccessibleChartCard(ChartDefinition definition, int historyLength = DefaultHistoryLength)
      {
         definition_ = definition ?? throw new ArgumentNullException(nameof(definition));
         historyLength_ = Math.Max(2, historyLength);

         int seriesCount = definition_.SeriesNames.Count;
         samples_ = new List<double?>[seriesCount];
         plotted_ = new ObservableCollection<ObservableValue>[seriesCount];
         for (int i = 0; i < seriesCount; i++)
         {
            samples_[i] = new List<double?>();
            plotted_[i] = new ObservableCollection<ObservableValue>();
         }

         // The card is a container, not a control: the tab stops inside it are the
         // two buttons and the table, and adding a third stop that does nothing
         // when activated makes the page slower to get through by keyboard.
         Focusable = false;

         Build_();
         ApplyTheme_();
         UpdateSummary_();

         Loaded += OnLoaded;
         Unloaded += OnUnloaded;
      }

      /// <summary>Shown over the chart while no series has a non-zero value.</summary>
      public string EmptyText
      {
         get => placeholder_.Text;
         set => placeholder_.Text = value ?? "";
      }

      /// <summary>True while the table view is showing instead of the chart.</summary>
      public bool IsTableVisible => tableVisible_;

      /// <summary>
      /// Adds one sample per series, oldest entries falling off the end of the
      /// history. Pass null for a series that was not measured this time round -
      /// it will read as "-" in the table rather than as a zero.
      /// </summary>
      public void Push(DateTime timestamp, params double?[] values)
      {
         for (int i = 0; i < samples_.Length; i++)
         {
            double? value = values != null && i < values.Length ? values[i] : null;

            samples_[i].Add(value);
            plotted_[i].Add(new ObservableValue(value));

            while (samples_[i].Count > historyLength_)
               samples_[i].RemoveAt(0);
            while (plotted_[i].Count > historyLength_)
               plotted_[i].RemoveAt(0);
         }

         times_.Add(timestamp);
         while (times_.Count > historyLength_)
            times_.RemoveAt(0);

         UpdateSummary_();

         if (tableVisible_)
            PrependNewestRow_();
      }

      /// <summary>Switches between the chart and the table view.</summary>
      public void ShowTable(bool show) => ShowTable_(show, false);

      private void OnLoaded(object sender, RoutedEventArgs e)
      {
         // Hooked here rather than in the constructor: pages are cached and swapped
         // in and out of the content host, so a constructor-time subscription to a
         // static event would pin every page that has ever been visited. Detached
         // first because WPF can raise Loaded again without an intervening Unloaded
         // (a re-parented element does), and a double subscription would restyle the
         // chart twice on every theme change.
         ThemeTokens.Changed -= OnThemeChanged;
         ThemeTokens.Changed += OnThemeChanged;

         // The theme can have changed while this page was off screen, and it can
         // have changed before the page was ever built.
         ApplyTheme_();

         // High Contrast means the user has told Windows they cannot read subtle
         // differences in colour. Three lines that differ mainly in colour are
         // exactly that, so open on the numbers - unless they have already said
         // which view they want, in which case respect it.
         if (!viewChosenByUser_ && ThemeTokens.CurrentChartTheme == ChartTheme.HighContrast)
            ShowTable_(true, false);
      }

      private void OnUnloaded(object sender, RoutedEventArgs e)
      {
         ThemeTokens.Changed -= OnThemeChanged;
      }

      private void OnThemeChanged(object sender, EventArgs e)
      {
         // The numbers have not changed, so the table is left alone - rebuilding it
         // here would throw away the reader's place in it for no reason.
         ApplyTheme_();
      }

      // ---- Layout ------------------------------------------------------------

      private void Build_()
      {
         titleText_.Text = definition_.Unit.Length == 0
            ? definition_.Title
            : definition_.Title + " (" + definition_.Unit + ")";
         titleText_.FontWeight = FontWeights.SemiBold;
         titleText_.FontSize = Typography.Body;
         titleText_.TextWrapping = TextWrapping.Wrap;
         titleText_.VerticalAlignment = VerticalAlignment.Center;
         titleText_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

         viewButton_.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
         viewButton_.FontSize = Typography.Caption;
         viewButton_.Padding = new Thickness(10, 4, 10, 4);
         viewButton_.MinWidth = 96;
         viewButton_.Margin = new Thickness(8, 0, 0, 0);
         viewButton_.Click += (s, e) =>
         {
            viewChosenByUser_ = true;
            ShowTable_(!tableVisible_, true);
         };

         copyButton_.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
         copyButton_.FontSize = Typography.Caption;
         copyButton_.Padding = new Thickness(10, 4, 10, 4);
         copyButton_.Content = "Copy";
         copyButton_.Margin = new Thickness(6, 0, 0, 0);
         copyButton_.ToolTip = "Copy every sample of " + definition_.Title.ToLowerInvariant()
            + " to the clipboard as tab-separated text";
         AutomationProperties.SetName(copyButton_, "Copy the " + definition_.Title.ToLowerInvariant()
            + " data to the clipboard");
         AutomationProperties.SetAutomationId(copyButton_, definition_.Id + ".copy");
         copyButton_.Click += Copy_Click;

         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         header.Children.Add(titleText_);
         Grid.SetColumn(viewButton_, 1);
         header.Children.Add(viewButton_);
         Grid.SetColumn(copyButton_, 2);
         header.Children.Add(copyButton_);

         chart_.Background = Brushes.Transparent;
         chart_.Focusable = false;
         chart_.AnimationsSpeed = TimeSpan.FromMilliseconds(400);
         chart_.Margin = new Thickness(0, 8, 0, 0);
         AutomationProperties.SetName(chart_, definition_.Title);
         AutomationProperties.SetAutomationId(chart_, definition_.Id);

         placeholder_.HorizontalAlignment = HorizontalAlignment.Center;
         placeholder_.VerticalAlignment = VerticalAlignment.Center;
         placeholder_.FontSize = Typography.Body;
         placeholder_.Text = "No data yet";

         BuildTable_();

         var plot = new Grid();
         plot.Children.Add(chart_);
         plot.Children.Add(placeholder_);
         plot.Children.Add(table_);

         legend_.Orientation = Orientation.Horizontal;
         legend_.Margin = new Thickness(0, 8, 0, 0);

         summaryText_.FontSize = Typography.Caption;
         summaryText_.TextWrapping = TextWrapping.Wrap;
         summaryText_.Margin = new Thickness(0, 4, 0, 0);
         // Deliberately not an automation live region. It changes every two
         // seconds; announcing that unprompted would talk over everything else the
         // user is doing. It is here to be read - by eye or by the screen reader's
         // own navigation - when the reader wants it.

         var footer = new StackPanel();
         footer.Children.Add(legend_);
         footer.Children.Add(summaryText_);

         var body = new Grid();
         body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         body.Children.Add(header);
         Grid.SetRow(plot, 1);
         body.Children.Add(plot);
         Grid.SetRow(footer, 2);
         body.Children.Add(footer);

         surface_.SetResourceReference(StyleProperty, "Card");
         surface_.Child = body;

         // Named on the UserControl, not on the Border. Border has no automation
         // peer, so AutomationProperties on it are silently thrown away - a trap
         // worth knowing about, because the code looks like it works. The
         // UserControl is a Control and does have a peer, so a screen reader user
         // moving by grouping hears "Active sessions" and its description rather
         // than landing in a run of unlabelled numbers.
         AutomationProperties.SetName(this, definition_.Title);
         AutomationProperties.SetHelpText(this, definition_.Description);

         Content = surface_;
         UpdateViewButton_();
      }

      private void BuildTable_()
      {
         table_.AutoGenerateColumns = false;
         table_.IsReadOnly = true;
         table_.CanUserAddRows = false;
         table_.CanUserDeleteRows = false;
         table_.CanUserResizeRows = false;
         table_.HeadersVisibility = DataGridHeadersVisibility.Column;
         table_.GridLinesVisibility = DataGridGridLinesVisibility.None;
         table_.Background = Brushes.Transparent;
         table_.BorderThickness = new Thickness(0);
         table_.RowHeight = 24;
         table_.FontSize = Typography.Label;
         table_.SelectionMode = DataGridSelectionMode.Single;
         table_.SelectionUnit = DataGridSelectionUnit.FullRow;
         table_.ItemsSource = tableRows_;
         table_.Visibility = Visibility.Collapsed;
         table_.Margin = new Thickness(0, 8, 0, 0);
         AutomationProperties.SetName(table_, definition_.Title + " data table");
         AutomationProperties.SetAutomationId(table_, definition_.Id + ".table");

         var numeric = new Style(typeof(TextBlock));
         numeric.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right));
         numeric.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0)));
         numeric.Seal();

         table_.Columns.Add(new DataGridTextColumn
         {
            Header = definition_.TimeHeader,
            Binding = new Binding("TimeLabel"),
            Width = new DataGridLength(92)
         });

         for (int i = 0; i < definition_.SeriesNames.Count; i++)
         {
            table_.Columns.Add(new DataGridTextColumn
            {
               // The header carries the unit as well as the series name, because a
               // cell announced on its own is announced with its column header and
               // "SMTP (sessions), 4" is a complete sentence where "4" is not.
               Header = definition_.TableHeaders[i + 1],
               Binding = new Binding("Cells[" + i.ToString(CultureInfo.InvariantCulture) + "]"),
               Width = new DataGridLength(1, DataGridLengthUnitType.Star),
               ElementStyle = numeric
            });
         }
      }

      // ---- Theme -------------------------------------------------------------

      private void ApplyTheme_()
      {
         ChartTheme theme = ThemeTokens.CurrentChartTheme;
         ChartSystemColors system = ThemeTokens.CurrentSystemColors;

         styles_ = ChartPalette.ResolveSeries(theme, system, definition_.SeriesNames);
         ChartChrome chrome = ChartPalette.ResolveChrome(theme, system);

         var axisPaint = new SolidColorPaint(Skia_(chrome.AxisTextArgb)) { SKTypeface = AxisTypeface };
         var gridPaint = new SolidColorPaint(Skia_(chrome.GridLineArgb)) { StrokeThickness = 1 };

         // The whole series array is rebuilt rather than repainted in place. Paints
         // are cached inside the chart's drawing tasks, and mutating a live paint's
         // colour is not guaranteed to invalidate what has already been queued;
         // handing the chart new series is the one path LiveCharts is certain to
         // redraw. The sample collections are reused, so no history is lost.
         var series = new ISeries[styles_.Count];
         for (int i = 0; i < styles_.Count; i++)
            series[i] = MakeSeries_(styles_[i], plotted_[i], chrome.LineSmoothness);

         chart_.Series = series;
         chart_.XAxes = new[] { new Axis { IsVisible = false } };
         chart_.YAxes = new[]
         {
            new Axis
            {
               LabelsPaint = axisPaint,
               SeparatorsPaint = gridPaint,
               TextSize = 12,
               MinLimit = 0
            }
         };
         chart_.TooltipBackgroundPaint = new SolidColorPaint(Skia_(chrome.TooltipBackgroundArgb));
         chart_.TooltipTextPaint = new SolidColorPaint(Skia_(chrome.TooltipTextArgb));

         placeholder_.Foreground = ShapeMarkVisuals.ToBrush(chrome.PlaceholderTextArgb);

         ApplySurface_(chrome);
         BuildLegend_();
      }

      /// <summary>
      /// In High Contrast the palette also owns the card's own colours - see
      /// ChartChrome.SurfaceArgb for why taking the system foreground without the
      /// system background is how you end up with black on black.
      /// </summary>
      private void ApplySurface_(ChartChrome chrome)
      {
         if (chrome.SurfaceArgb.HasValue)
         {
            surface_.Background = ShapeMarkVisuals.ToBrush(chrome.SurfaceArgb.Value);
            surface_.BorderBrush = ShapeMarkVisuals.ToBrush(chrome.SurfaceBorderArgb ?? chrome.AxisTextArgb);
            surface_.BorderThickness = new Thickness(1);

            Brush text = ShapeMarkVisuals.ToBrush(chrome.SurfaceTextArgb ?? chrome.AxisTextArgb);
            titleText_.Foreground = text;
            summaryText_.Foreground = text;
            table_.Foreground = text;

            // The table needs its cells and headers overridden explicitly, not just
            // the grid's inherited Foreground. App.xaml styles DataGridCell and
            // DataGridColumnHeader implicitly with the WPF-UI theme text brushes, and
            // a style setter beats an inherited value - so on a High Contrast White
            // desktop the cells would keep the dark theme's near-white text and the
            // whole table would be white on white. That is the exact failure this
            // work exists to remove, so it must not be reintroduced by the table.
            table_.CellStyle = DerivedStyle_(typeof(DataGridCell), text);
            table_.ColumnHeaderStyle = DerivedStyle_(typeof(DataGridColumnHeader), text);
            return;
         }

         // Back to the theme's own brushes. Clearing the local value rather than
         // assigning a resolved brush matters: the Card style and the theme
         // dictionary keep updating these, and a brush pinned here would freeze the
         // card on whatever colour was current when High Contrast was switched off.
         surface_.ClearValue(Border.BackgroundProperty);
         surface_.ClearValue(Border.BorderBrushProperty);
         surface_.ClearValue(Border.BorderThicknessProperty);
         table_.ClearValue(Control.ForegroundProperty);
         table_.ClearValue(DataGrid.CellStyleProperty);
         table_.ClearValue(DataGrid.ColumnHeaderStyleProperty);
         titleText_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         summaryText_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
      }

      /// <summary>
      /// A style for one of the data-grid parts that keeps everything App.xaml gave
      /// it - the cell template, the hairline divider, the padding - and overrides
      /// only the foreground.
      /// </summary>
      private Style DerivedStyle_(Type targetType, Brush foreground)
      {
         var style = new Style(targetType);
         if (TryFindResource(targetType) is Style implicitStyle)
            style.BasedOn = implicitStyle;
         style.Setters.Add(new Setter(Control.ForegroundProperty, foreground));
         style.Seal();
         return style;
      }

      private static ISeries MakeSeries_(ChartSeriesStyle style,
                                        ObservableCollection<ObservableValue> values, double smoothness)
      {
         SKColor color = Skia_(style.Argb);
         var stroke = new SolidColorPaint(color) { StrokeThickness = style.StrokeThickness };

         float[] dash = style.DashIntervals;
         if (dash != null)
            stroke.PathEffect = new DashEffect(dash, 0f);

         return new LineSeries<ObservableValue>
         {
            Name = style.Name,
            Values = values,
            GeometrySize = 0,
            LineSmoothness = smoothness,
            Stroke = stroke,
            Fill = style.AreaAlpha == 0
               ? null
               : new LinearGradientPaint(
                  new[] { color.WithAlpha(style.AreaAlpha), color.WithAlpha(0) },
                  new SKPoint(0.5f, 0), new SKPoint(0.5f, 1))
         };
      }

      private void BuildLegend_()
      {
         legend_.Children.Clear();
         legendValues_.Clear();

         // A single-series chart needs no legend: the card title already says what
         // the one line is, and a legend with one entry is furniture.
         if (styles_.Count < 2)
            return;

         foreach (ChartSeriesStyle style in styles_)
         {
            Brush brush = ShapeMarkVisuals.ToBrush(style.Argb);

            var swatch = new Canvas
            {
               Width = SwatchWidth,
               Height = SwatchHeight,
               VerticalAlignment = VerticalAlignment.Center,
               Margin = new Thickness(0, 0, 6, 0)
            };

            var line = new Line
            {
               X1 = 0,
               Y1 = SwatchHeight / 2,
               X2 = SwatchWidth,
               Y2 = SwatchHeight / 2,
               Stroke = brush,
               StrokeThickness = style.StrokeThickness
            };
            DoubleCollection dashes = ShapeMarkVisuals.DashArrayFor(style.Pattern, style.StrokeThickness);
            if (dashes != null)
               line.StrokeDashArray = dashes;
            swatch.Children.Add(line);

            // Deliberately no marker shape on the swatch. MakeSeries_ sets
            // GeometrySize = 0, so Skia draws no markers in the plot at all - and a
            // legend that shows a circle, square or triangle the chart never draws is
            // worse than one that shows less: it tells a screen-reader user, in the
            // accessible name below, to look for something that is not there, and it
            // invites a sighted user to hunt for a shape among the lines.
            //
            // The swatch therefore shows exactly what the plot shows: the colour and
            // the dash pattern. That is still two channels, so identity is never
            // carried by colour alone, which is the requirement.
            //
            // Giving the series real per-series geometry is the better end state and is
            // deliberately not done here: LiveCharts needs the geometry as a second
            // generic argument (LineSeries<ObservableValue, CircleGeometry> and so on),
            // so it is a shape-to-closed-type mapping rather than a property, and doing
            // it badly would put a marker on every point of a three-minute series and
            // bury the line. It is recorded as still open rather than half-done.

            var name = new TextBlock
            {
               Text = style.Name,
               FontSize = Typography.Caption,
               VerticalAlignment = VerticalAlignment.Center
            };
            // The dash pattern is what identifies the series when the colour cannot be
            // relied on, so it is said in words to anyone listening rather than only
            // drawn. The marker shape is deliberately NOT announced: the plot draws no
            // markers (GeometrySize = 0), and describing one would send a screen-reader
            // user looking for something that does not exist.
            AutomationProperties.SetName(name, style.Name + ", " + style.PatternText + " line");

            var value = new TextBlock
            {
               FontSize = Typography.Caption,
               FontWeight = FontWeights.SemiBold,
               VerticalAlignment = VerticalAlignment.Center,
               Margin = new Thickness(5, 0, 0, 0),
               Foreground = brush
            };
            legendValues_.Add(value);

            var item = new StackPanel
            {
               Orientation = Orientation.Horizontal,
               Margin = new Thickness(0, 0, 16, 0)
            };
            item.Children.Add(swatch);
            item.Children.Add(name);
            item.Children.Add(value);
            legend_.Children.Add(item);
         }

         UpdateLegendValues_();
      }

      // ---- Data --------------------------------------------------------------

      private void UpdateSummary_()
      {
         summary_ = ChartDataTable.Summarise(definition_, SeriesSamples_());

         summaryText_.Text = summary_.Text;

         // The chart itself is a Skia canvas: UI Automation can see a control with
         // a name and nothing inside it. Its accessible description is therefore
         // the only thing a screen reader can learn from the picture, so it carries
         // the latest and peak numbers.
         AutomationProperties.SetHelpText(chart_, summary_.Text);
         AutomationProperties.SetHelpText(table_, summary_.Text);

         UpdateLegendValues_();
         UpdatePlaceholder_();
      }

      private void UpdateLegendValues_()
      {
         if (summary_ == null)
            return;

         for (int i = 0; i < legendValues_.Count && i < summary_.Series.Count; i++)
         {
            ChartSeriesSummary series = summary_.Series[i];
            legendValues_[i].Text = ChartDataTable.Format(series.Latest, definition_.ValueFormat);

            string spoken = series.Name + " latest "
               + ChartDataTable.Format(series.Latest, definition_.ValueFormat);
            if (series.Peak.HasValue)
               spoken += ", peak " + ChartDataTable.Format(series.Peak, definition_.ValueFormat);
            if (definition_.Unit.Length > 0)
               spoken += " " + definition_.Unit;

            AutomationProperties.SetName(legendValues_[i], spoken);
            legendValues_[i].ToolTip = spoken;
         }
      }

      private void UpdatePlaceholder_()
      {
         // The placeholder only ever covers the chart. In the table view a run of
         // zeroes is information - it says the server was idle, which is not the
         // same statement as "there is nothing to show".
         placeholder_.Visibility = !tableVisible_ && !HasActivity_()
            ? Visibility.Visible
            : Visibility.Collapsed;
      }

      private void RebuildTable_()
      {
         ChartDataTable table = ChartDataTable.Build(definition_, SeriesSamples_(), times_);

         tableRows_.Clear();
         foreach (ChartTableRow row in table.Rows)
            tableRows_.Add(Row_(row));
      }

      /// <summary>
      /// Adds the sample that just arrived to the top of the table and drops the
      /// oldest one, instead of rebuilding all ninety rows. See
      /// ChartDataTable.BuildNewestRow: rebuilding every two seconds resets the
      /// selection and the scroll position, so the reader can never finish reading
      /// a row.
      /// </summary>
      private void PrependNewestRow_()
      {
         ChartTableRow row = ChartDataTable.BuildNewestRow(definition_, SeriesSamples_(), times_);
         if (row == null)
            return;

         tableRows_.Insert(0, Row_(row));
         while (tableRows_.Count > historyLength_)
            tableRows_.RemoveAt(tableRows_.Count - 1);
      }

      private static TableRow Row_(ChartTableRow row)
      {
         return new TableRow { TimeLabel = row.TimeLabel, Cells = new List<string>(row.Cells) };
      }

      private void ShowTable_(bool show, bool moveFocus)
      {
         tableVisible_ = show;
         table_.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
         chart_.Visibility = show ? Visibility.Collapsed : Visibility.Visible;

         UpdatePlaceholder_();
         UpdateViewButton_();

         if (!show)
            return;

         RebuildTable_();

         // Only when the user asked for the table: moving focus on the automatic
         // High Contrast switch would yank it out of whatever they were doing when
         // the page loaded.
         if (moveFocus)
            table_.Focus();
      }

      private void UpdateViewButton_()
      {
         viewButton_.Content = tableVisible_ ? "Show chart" : "Show table";
         viewButton_.ToolTip = tableVisible_
            ? "Show " + definition_.Title.ToLowerInvariant() + " as a chart"
            : "Show every " + definition_.Title.ToLowerInvariant() + " sample as a table of numbers";
         AutomationProperties.SetName(viewButton_, tableVisible_
            ? "Show " + definition_.Title.ToLowerInvariant() + " as a chart"
            : "Show " + definition_.Title.ToLowerInvariant() + " as a data table");
         AutomationProperties.SetAutomationId(viewButton_, definition_.Id + ".view");
      }

      private void Copy_Click(object sender, RoutedEventArgs e)
      {
         ChartDataTable table = ChartDataTable.Build(definition_, SeriesSamples_(), times_);

         try
         {
            Clipboard.SetText(table.ToDelimitedText());
            Toast.Info(table.Rows.Count == 1
               ? "1 sample copied to the clipboard."
               : table.Rows.Count.ToString("N0", CultureInfo.CurrentCulture)
                 + " samples copied to the clipboard.", definition_.Title);
         }
         catch (Exception)
         {
            // Another process can hold the clipboard open; that is not worth a
            // dialog, and the table itself is still on screen to read.
         }
      }

      private IReadOnlyList<ChartSeriesSamples> SeriesSamples_()
      {
         var series = new ChartSeriesSamples[samples_.Length];
         for (int i = 0; i < samples_.Length; i++)
            series[i] = new ChartSeriesSamples(definition_.SeriesNames[i], samples_[i]);
         return series;
      }

      private bool HasActivity_()
      {
         foreach (List<double?> series in samples_)
         {
            foreach (double? value in series)
            {
               if (value.GetValueOrDefault() > 0)
                  return true;
            }
         }

         return false;
      }

      private static SKColor Skia_(uint argb)
      {
         return new SKColor((byte) (argb >> 16), (byte) (argb >> 8), (byte) argb, (byte) (argb >> 24));
      }

      /// <summary>
      /// One row as the DataGrid sees it.
      ///
      /// Cells is a List rather than the string[] the model hands over, because the
      /// column bindings use the path "Cells[0]" and WPF resolves an indexer by
      /// reflecting for an Item property on the runtime type. A List has one; an
      /// array only implements IList explicitly and leans on a special case in the
      /// binding engine. One copy per row at construction is a cheap price for not
      /// depending on that.
      /// </summary>
      private sealed class TableRow
      {
         public string TimeLabel { get; set; }

         public List<string> Cells { get; set; }
      }
   }
}
