// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Live server statistics: a "needs attention" setup summary, five KPI cards
   /// and two charts.
   ///
   /// The charts are <see cref="AccessibleChartCard"/> instances built from
   /// <see cref="ChartCatalog"/> rather than LiveCharts controls declared in the
   /// XAML. That is what gives them a High Contrast palette, a table view of the
   /// samples, and a legend that identifies each series by dash pattern and shape;
   /// this page's remaining job is to read the status snapshot and push one sample
   /// per series into each card.
   ///
   /// The setup summary runs <see cref="ExternalSetupChecks"/> - the same checks
   /// the External setup page shows in full - because the administrator who most
   /// needs that page is the one who does not know it exists. The dashboard is
   /// where they land, so it is where "3 things need attention" has to appear.
   /// </summary>
   public partial class DashboardView : UserControl, IPageLifecycle
   {
      private const int HistoryLength = 90;

      private readonly DispatcherTimer timer_;
      private readonly AccessibleChartCard throughputCard_;
      private readonly AccessibleChartCard sessionsCard_;

      private long lastProcessed_ = -1;
      private DateTime lastSampleUtc_ = DateTime.MinValue;

      // The range selector. "live" shows the two cards above, fed every two
      // seconds from Status and holding three minutes; the other ranges show two
      // cards of the same shape loaded from the history the server keeps
      // (MetricsHistoryDays), through Utilities.GetMetricHistory.
      private readonly StackPanel rangeBar_ = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
      private readonly TextBlock rangeNote_ = new()
      {
         FontSize = Typography.Caption,
         Opacity = 0.75,
         VerticalAlignment = VerticalAlignment.Center,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(12, 0, 0, 0)
      };
      private string range_ = "live";
      private AccessibleChartCard historyThroughput_;
      private AccessibleChartCard historySessions_;

      public DashboardView()
      {
         InitializeComponent();

         throughputCard_ = new AccessibleChartCard(ChartCatalog.DashboardThroughput, HistoryLength)
         {
            EmptyText = "No delivery activity yet",
            // Taller than the 300 the bare chart used: the card now carries a
            // legend and a summary line under the plot, and squeezing the plot to
            // keep the old height is how you end up with a chart too short to read.
            Height = 340,
            Margin = new Thickness(0, 0, 12, 0)
         };

         sessionsCard_ = new AccessibleChartCard(ChartCatalog.DashboardSessions, HistoryLength)
         {
            EmptyText = "No active sessions",
            Height = 340
         };

         Grid.SetColumn(throughputCard_, 0);
         Grid.SetColumn(sessionsCard_, 1);
         ChartsGrid.Children.Add(throughputCard_);
         ChartsGrid.Children.Add(sessionsCard_);

         BuildRangeSelector_();

         timer_ = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
         timer_.Tick += (s, e) => Refresh();
      }

      private void BuildRangeSelector_()
      {
         rangeBar_.Children.Add(new TextBlock
         {
            Text = "Show",
            FontSize = Typography.Body,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
         });

         foreach ((string key, string label) in new[] { ("live", "Live"), ("24h", "24 hours"), ("7d", "7 days"), ("30d", "30 days") })
         {
            var button = new Wpf.Ui.Controls.Button
            {
               Content = label,
               Appearance = key == "live" ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary,
               Margin = new Thickness(0, 0, 6, 0),
               Tag = key
            };
            AutomationProperties.SetName(button, "Show " + label);
            button.Click += (s, e) => ShowRange_(key);
            rangeBar_.Children.Add(button);
         }

         rangeBar_.Children.Add(rangeNote_);

         var parent = (Panel)ChartsGrid.Parent;
         parent.Children.Insert(parent.Children.IndexOf(ChartsGrid), rangeBar_);
      }

      private void ShowRange_(string range)
      {
         range_ = range;

         foreach (Wpf.Ui.Controls.Button button in rangeBar_.Children.OfType<Wpf.Ui.Controls.Button>())
            button.Appearance = (string)button.Tag == range ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;

         ChartsGrid.Children.Clear();

         if (range == "live")
         {
            historyThroughput_ = null;
            historySessions_ = null;
            rangeNote_.Text = "";
            ChartsGrid.Children.Add(throughputCard_);
            ChartsGrid.Children.Add(sessionsCard_);
            return;
         }

         int minutes = range == "30d" ? 30 * 24 * 60 : range == "7d" ? 7 * 24 * 60 : 24 * 60;
         int bucket = range == "30d" ? 60 : range == "7d" ? 10 : 1;

         try
         {
            dynamic utilities = ServerSession.Current.Application.Utilities;

            // Throughput is a total; the chart wants a rate. The difference between
            // consecutive buckets over the bucket length is messages per minute, and
            // a drop (a restart reset the counter) is shown as zero rather than as a
            // negative rate.
            (bool enabled, int retention, var processed) = Samples_((string)utilities.GetMetricHistory("processed_messages_total", minutes, bucket));
            var smtp = Samples_((string)utilities.GetMetricHistory("sessions_smtp", minutes, bucket)).samples;
            var imap = Samples_((string)utilities.GetMetricHistory("sessions_imap", minutes, bucket)).samples;
            var pop3 = Samples_((string)utilities.GetMetricHistory("sessions_pop3", minutes, bucket)).samples;

            if (!enabled)
            {
               rangeNote_.Text = "The server keeps no history: MetricsHistoryDays is 0 in hMailServer.ini. Set it to the days to keep and restart the service.";
               ChartsGrid.Children.Add(throughputCard_);
               ChartsGrid.Children.Add(sessionsCard_);
               return;
            }

            historyThroughput_ = new AccessibleChartCard(ChartCatalog.DashboardThroughput, Math.Max(2, processed.Count))
            {
               EmptyText = "No samples in this range yet",
               Height = 340,
               Margin = new Thickness(0, 0, 12, 0)
            };

            for (int i = 1; i < processed.Count; i++)
            {
               double rate = Math.Max(0, processed[i].value - processed[i - 1].value) / bucket;
               historyThroughput_.Push(processed[i].time, rate);
            }

            var times = smtp.Select(x => x.time).Union(imap.Select(x => x.time)).Union(pop3.Select(x => x.time)).OrderBy(t => t).ToList();
            var smtpByTime = smtp.ToDictionary(x => x.time, x => x.value);
            var imapByTime = imap.ToDictionary(x => x.time, x => x.value);
            var pop3ByTime = pop3.ToDictionary(x => x.time, x => x.value);

            historySessions_ = new AccessibleChartCard(ChartCatalog.DashboardSessions, Math.Max(2, times.Count))
            {
               EmptyText = "No samples in this range yet",
               Height = 340
            };

            foreach (DateTime time in times)
            {
               historySessions_.Push(time,
                  smtpByTime.TryGetValue(time, out double a) ? a : (double?)null,
                  imapByTime.TryGetValue(time, out double b) ? b : (double?)null,
                  pop3ByTime.TryGetValue(time, out double c) ? c : (double?)null);
            }

            Grid.SetColumn(historyThroughput_, 0);
            Grid.SetColumn(historySessions_, 1);
            ChartsGrid.Children.Add(historyThroughput_);
            ChartsGrid.Children.Add(historySessions_);

            string per = bucket == 1 ? "minute" : bucket == 10 ? "ten minutes" : "hour";
            rangeNote_.Text = processed.Count == 0
               ? "No samples in this range yet: the server records one a minute and keeps " + retention + " days."
               : "One point per " + per + ", from the " + retention + "-day history the server keeps. Throughput is the change in the processed-messages total per minute.";
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            rangeNote_.Text = "Could not load the history: " + ex.Message;
            ChartsGrid.Children.Add(throughputCard_);
            ChartsGrid.Children.Add(sessionsCard_);
         }
      }

      // The JSON Utilities.GetMetricHistory returns, as points. Times are as the
      // server's database stored them: its own clock, shown as it is.
      private static (bool enabled, int retention, System.Collections.Generic.List<(DateTime time, double value)> samples) Samples_(string json)
      {
         var samples = new System.Collections.Generic.List<(DateTime time, double value)>();
         bool enabled = false;
         int retention = 0;

         using (JsonDocument document = JsonDocument.Parse(json))
         {
            JsonElement root = document.RootElement;
            enabled = root.TryGetProperty("enabled", out JsonElement enabledElement) && enabledElement.GetBoolean();
            retention = root.TryGetProperty("retention_days", out JsonElement retentionElement) ? retentionElement.GetInt32() : 0;

            if (root.TryGetProperty("samples", out JsonElement array))
            {
               foreach (JsonElement sample in array.EnumerateArray())
               {
                  string time = sample.GetProperty("time").GetString() ?? "";
                  double value = sample.GetProperty("value").GetDouble();

                  if (DateTime.TryParseExact(time, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
                     samples.Add((parsed, value));
               }
            }
         }

         return (enabled, retention, samples);
      }

      public void OnEnter()
      {
         Refresh();
         timer_.Start();
         BeginSetupCheck_();
      }

      public void OnLeave()
      {
         timer_.Stop();

         // Orphan any setup check still in flight: a DNS pass that completes
         // after the user has left must not repaint a page they are no longer
         // on, and re-entering starts a fresh check anyway.
         setupGeneration_++;
      }

      private void Refresh()
      {
         var session = ServerSession.Current;
         if (session == null || !session.IsConnected)
            return;

         try
         {
            var snap = session.ReadStatus();

            SetKpi_(KpiProcessed, "Messages processed", snap.ProcessedMessages.ToString("N0"));
            SetKpi_(KpiSpam, "Spam blocked", snap.SpamBlocked.ToString("N0"));
            SetKpi_(KpiViruses, "Viruses removed", snap.VirusesRemoved.ToString("N0"));
            SetKpi_(KpiUptime, "Uptime", FormatUptime(snap.StartTime));
            ShowQueue_(snap.QueueLength);

            // Two clocks on purpose. The table's Time column has to be local time
            // to mean anything to the reader, but the throughput rate is a division
            // by an elapsed interval, and a daylight-saving change or a clock
            // correction on local time would turn one sample into a spike of
            // thousands of messages a minute.
            DateTime nowLocal = DateTime.Now;
            DateTime nowUtc = DateTime.UtcNow;

            sessionsCard_.Push(nowLocal, snap.SmtpSessions, snap.ImapSessions, snap.Pop3Sessions);

            if (lastProcessed_ >= 0 && lastSampleUtc_ != DateTime.MinValue)
            {
               double seconds = (nowUtc - lastSampleUtc_).TotalSeconds;
               if (seconds > 0.5)
               {
                  throughputCard_.Push(nowLocal,
                     Math.Max(0, snap.ProcessedMessages - lastProcessed_) * 60.0 / seconds);
               }
            }

            lastProcessed_ = snap.ProcessedMessages;
            lastSampleUtc_ = nowUtc;

            SubtitleText.Text = "Live server statistics - last update " + nowLocal.ToLongTimeString();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            SubtitleText.Text = "Connection to the server lost.";
            timer_.Stop();
         }
      }

      /// <summary>
      /// Sets a KPI value and the accessible name that goes with it. The name has
      /// to carry the label as well as the number, because the five cards are five
      /// unlabelled numbers to anything reading the automation tree - "312" on its
      /// own could be the queue, the spam count or the uptime in minutes.
      /// </summary>
      private static void SetKpi_(TextBlock value, string label, string text)
      {
         value.Text = text;
         AutomationProperties.SetName(value, label + ", " + text);
      }

      /// <summary>
      /// Shows the queue length, and says whether it is a backlog in three
      /// independent channels: the colour of the number, a shape plus the word
      /// "Backlog" underneath it, and the value's accessible name.
      ///
      /// Colour used to be the only channel. That made the one piece of judgement
      /// on the dashboard - "this queue is not draining" - invisible to anyone who
      /// cannot distinguish amber from the body text, which includes every user of
      /// a High Contrast theme, where the amber is replaced by a system colour that
      /// may well be the same one the number would have had anyway.
      /// </summary>
      private void ShowQueue_(int queueLength)
      {
         string text = queueLength.ToString("N0");
         StatusLevel level = StatusSemantics.ForQueueLength(queueLength);
         StatusPresentation status = StatusSemantics.For(level);

         KpiQueue.Text = text;
         KpiQueue.SetResourceReference(TextBlock.ForegroundProperty, status.BrushKey);

         if (level == StatusLevel.Normal)
         {
            // Hidden, not Collapsed. A collapsed badge gives its space back, so the card
            // grew by the badge's height the moment the queue crossed the backlog
            // threshold - and because the five KPI cards share a UniformGrid, the whole
            // row jumped taller at precisely the moment an operator was most likely to be
            // staring at it. Reserving the space costs nineteen pixels of empty card in
            // the normal case and buys a dashboard that does not move under the reader.
            KpiQueueBadge.Visibility = Visibility.Hidden;
            AutomationProperties.SetName(KpiQueue, "In queue, " + text);
            return;
         }

         ShapeMarkVisuals.ApplyMark(KpiQueueShape, status.Shape, status.BrushKey);
         KpiQueueBadgeText.Text = "Backlog";
         KpiQueueBadgeText.SetResourceReference(TextBlock.ForegroundProperty, status.BrushKey);
         KpiQueueBadge.Visibility = Visibility.Visible;
         KpiQueueBadge.ToolTip = "More than " + StatusSemantics.QueueBacklogThreshold.ToString("N0")
            + " messages are waiting to be delivered.";

         AutomationProperties.SetName(KpiQueue,
            "In queue, " + text + ". " + status.SeverityWord + ": backlog.");
      }

      // ---- the "needs attention" setup summary ---------------------------------

      /// <summary>
      /// Bumped whenever a new check starts (or the page is left). A check
      /// carries the generation it was started for and applies nothing if the
      /// counter has moved on - the same pattern ExternalSetupView uses - so a
      /// slow DNS pass can never overwrite a newer summary with stale verdicts.
      /// </summary>
      private int setupGeneration_;

      /// <summary>
      /// Starts the setup check without holding up the dashboard. The COM/INI/
      /// file half runs on the UI thread because the COM objects are apartment-
      /// bound there (every page reads them on that thread), but it is deferred
      /// to Background priority so the page paints first; the DNS half - the
      /// part that can block for seconds - runs on a background task, exactly as
      /// ExternalSetupView runs it.
      /// </summary>
      private void BeginSetupCheck_()
      {
         int generation = ++setupGeneration_;

         ShowSetupChecking_();

         Dispatcher.BeginInvoke(new Action(() =>
         {
            if (generation != setupGeneration_)
               return;

            var session = ServerSession.Current;
            if (session == null || !session.IsConnected)
            {
               ShowSetupUncheckable_("The external setup could not be checked - not connected to the server.");
               return;
            }

            ExternalSetupChecks checks;
            try
            {
               checks = ExternalSetupChecks.Run();
            }
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
            {
               // Run() degrades per-item rather than throwing, so this is belt
               // and braces - but a dashboard that dies building a summary card
               // would be strictly worse than a dashboard without one.
               ShowSetupUncheckable_("The external setup could not be checked - " + ServerSession.DescribeComError(ex));
               return;
            }

            RenderAttention_(checks, checks.DnsLookupCount);

            if (checks.DnsLookupCount == 0)
               return;

            Task.Run(() =>
            {
               checks.ResolveDnsLookups();

               Dispatcher.BeginInvoke(new Action(() =>
               {
                  if (generation != setupGeneration_)
                     return;

                  checks.ApplyDnsResults();
                  RenderAttention_(checks, 0);
               }));
            });
         }), DispatcherPriority.Background);
      }

      /// <summary>The transient state before the first verdict: plain text, no
      /// mark, because "checking" is not a status and must not borrow one.</summary>
      private void ShowSetupChecking_()
      {
         AttentionMark.Visibility = Visibility.Collapsed;
         AttentionStateWord.Visibility = Visibility.Collapsed;
         AttentionSummary.Text = "Checking what this server still needs done outside it…";
         AutomationProperties.SetName(AttentionSummary, AttentionSummary.Text);
         AttentionItems.Children.Clear();
         AttentionNote.Visibility = Visibility.Collapsed;
      }

      /// <summary>Honest degradation: the summary itself could not be built, which
      /// is presented as "Cannot tell", never as all-clear.</summary>
      private void ShowSetupUncheckable_(string message)
      {
         SetAttentionHeader_(SetupItemState.CannotTell);
         AttentionSummary.Text = message;
         AutomationProperties.SetName(AttentionSummary,
            ExternalSetupChecks.StateWord(SetupItemState.CannotTell) + ": " + message);
         AttentionItems.Children.Clear();
         AttentionNote.Visibility = Visibility.Collapsed;
      }

      /// <summary>
      /// Renders the aggregate and the items that need action. The full page
      /// (External setup) shows all four states per item; this card shows the
      /// counts, the action items as one-line links to the page that fixes each,
      /// and a positive all-clear when there is nothing to do - an empty box
      /// would read as broken, and silence would read as "never checked".
      /// </summary>
      private void RenderAttention_(ExternalSetupChecks checks, int pendingDnsLookups)
      {
         int action = checks.Items.Count(i => i.State == SetupItemState.ActionNeeded);
         int unknown = checks.Items.Count(i => i.State == SetupItemState.CannotTell);

         SetupItemState overall = action > 0 ? SetupItemState.ActionNeeded
            : unknown > 0 ? SetupItemState.CannotTell
            : SetupItemState.Done;

         SetAttentionHeader_(overall);

         AttentionSummary.Text = SummaryText_(action, unknown, checks.Items.Count);

         // One utterance for a screen reader: the state word first, then the
         // counts, so the severity is heard even though the visible word sits in
         // a separate element.
         AutomationProperties.SetName(AttentionSummary,
            ExternalSetupChecks.StateWord(overall) + ": " + AttentionSummary.Text);

         AttentionItems.Children.Clear();
         int index = 0;
         foreach (SetupItem item in checks.Items.Where(i => i.State == SetupItemState.ActionNeeded))
            AttentionItems.Children.Add(AttentionLine_(item, index++));

         string note = "";
         if (pendingDnsLookups > 0)
            note = (pendingDnsLookups == 1 ? "1 DNS record is" : pendingDnsLookups + " DNS records are")
               + " still being checked in the background; this summary will update.";
         if (checks.FailedReads > 0)
            note += (note.Length > 0 ? " " : "")
               + checks.FailedReads + " value(s) could not be read from the server, so this summary may be incomplete.";

         AttentionNote.Text = note;
         AttentionNote.Visibility = note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
      }

      /// <summary>Colour, shape AND word for the card's overall state - the same
      /// three channels every status badge in this application carries, so the
      /// meaning survives greyscale, colour blindness and High Contrast.</summary>
      private void SetAttentionHeader_(SetupItemState overall)
      {
         StatusPresentation presentation = StatusSemantics.For(ExternalSetupChecks.LevelFor(overall));

         ShapeMarkVisuals.ApplyMark(AttentionMark, presentation.Shape, presentation.BrushKey);
         AttentionMark.Visibility = Visibility.Visible;

         AttentionStateWord.Text = ExternalSetupChecks.StateWord(overall);
         AttentionStateWord.SetResourceReference(TextBlock.ForegroundProperty, presentation.BrushKey);
         AttentionStateWord.Visibility = Visibility.Visible;
      }

      private static string SummaryText_(int action, int unknown, int total)
      {
         if (action > 0)
         {
            string text = action == 1 ? "1 thing needs attention" : action + " things need attention";
            if (unknown > 0)
               text += ", " + (unknown == 1 ? "1 item cannot be checked from here" : unknown + " items cannot be checked from here");
            return text + ".";
         }

         if (unknown > 0)
            return "Nothing needs action, but "
               + (unknown == 1 ? "1 item cannot be checked from here." : unknown + " items cannot be checked from here.");

         return "Nothing needs attention - all " + total + " external setup checks are done or not needed.";
      }

      /// <summary>
      /// One action item as one line: the warning mark plus the item's title as a
      /// link that opens the page owning its settings - see it AND do it, without
      /// hunting. The state is carried by shape, colour and the words "Action
      /// needed" in the accessible name, never by colour alone.
      /// </summary>
      private FrameworkElement AttentionLine_(SetupItem item, int index)
      {
         var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         StatusPresentation presentation = StatusSemantics.For(ExternalSetupChecks.LevelFor(item.State));

         var mark = new System.Windows.Shapes.Path
         {
            Width = 9,
            Height = 9,
            Stretch = System.Windows.Media.Stretch.Fill,
            Margin = new Thickness(1, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
         };
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);
         row.Children.Add(mark);

         // Every item on the External setup page records the navigation key of
         // the page that owns its settings; fall back to the checklist itself if
         // one ever does not.
         string page = item.Page ?? "externalsetup";
         string pageTitle = NavigationMap.TitleOf(page);

         var link = new Wpf.Ui.Controls.Button
         {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Label,
            Padding = new Thickness(6, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap },
            ToolTip = "Open " + pageTitle + ", which owns the settings for: " + item.Title
         };
         AutomationProperties.SetName(link, "Action needed: " + item.Title + ". Opens " + pageTitle + ".");
         AutomationProperties.SetAutomationId(link, "dashboard-attention-" + index);
         link.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         Grid.SetColumn(link, 1);
         row.Children.Add(link);

         return row;
      }

      private void OpenExternalSetup_Click(object sender, RoutedEventArgs e)
         => (Application.Current?.MainWindow as MainWindow)?.NavigateTo("externalsetup");

      private static string FormatUptime(string startTime)
      {
         if (DateTime.TryParse(startTime, out DateTime started))
         {
            TimeSpan up = DateTime.Now - started;
            if (up.TotalSeconds < 0) return startTime;
            if (up.TotalDays >= 1) return $"{(int)up.TotalDays}d {up.Hours}h";
            if (up.TotalHours >= 1) return $"{up.Hours}h {up.Minutes}m";
            return $"{Math.Max(0, (int)up.TotalMinutes)}m";
         }
         return string.IsNullOrEmpty(startTime) ? "-" : startTime;
      }
   }
}
