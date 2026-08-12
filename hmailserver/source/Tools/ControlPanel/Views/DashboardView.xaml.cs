using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Live server statistics: five KPI cards and two charts.
   ///
   /// The charts are <see cref="AccessibleChartCard"/> instances built from
   /// <see cref="ChartCatalog"/> rather than LiveCharts controls declared in the
   /// XAML. That is what gives them a High Contrast palette, a table view of the
   /// samples, and a legend that identifies each series by dash pattern and shape;
   /// this page's remaining job is to read the status snapshot and push one sample
   /// per series into each card.
   /// </summary>
   public partial class DashboardView : UserControl, IPageLifecycle
   {
      private const int HistoryLength = 90;

      private readonly DispatcherTimer timer_;
      private readonly AccessibleChartCard throughputCard_;
      private readonly AccessibleChartCard sessionsCard_;

      private long lastProcessed_ = -1;
      private DateTime lastSampleUtc_ = DateTime.MinValue;

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

         timer_ = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
         timer_.Tick += (s, e) => Refresh();
      }

      public void OnEnter()
      {
         Refresh();
         timer_.Start();
      }

      public void OnLeave() => timer_.Stop();

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
         catch (Exception)
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
            KpiQueueBadge.Visibility = Visibility.Collapsed;
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

      private static string FormatUptime(string startTime)
      {
         if (DateTime.TryParse(startTime, out DateTime started))
         {
            TimeSpan up = DateTime.Now - started;
            if (up.TotalSeconds < 0) return startTime;
            if (up.TotalDays >= 1) return $"{(int) up.TotalDays}d {up.Hours}h";
            if (up.TotalHours >= 1) return $"{up.Hours}h {up.Minutes}m";
            return $"{Math.Max(0, (int) up.TotalMinutes)}m";
         }
         return string.IsNullOrEmpty(startTime) ? "-" : startTime;
      }
   }
}
