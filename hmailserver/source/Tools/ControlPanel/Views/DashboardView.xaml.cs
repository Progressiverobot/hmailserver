using System;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Threading;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class DashboardView : UserControl, IPageLifecycle
   {
      private const int HistoryLength = 90;

      private readonly DispatcherTimer timer_;
      private readonly ObservableCollection<ObservableValue> throughput_ = new();
      private readonly ObservableCollection<ObservableValue> smtp_ = new();
      private readonly ObservableCollection<ObservableValue> imap_ = new();
      private readonly ObservableCollection<ObservableValue> pop3_ = new();

      private long lastProcessed_ = -1;
      private DateTime lastSample_ = DateTime.MinValue;

      private static readonly SKColor Accent = new(0x2F, 0x81, 0xF7);
      private static readonly SKColor Green = new(0x3F, 0xB9, 0x50);
      private static readonly SKColor Purple = new(0xA3, 0x71, 0xF7);
      private static readonly SKColor Amber = new(0xD2, 0x99, 0x22);
      private static readonly SKColor Muted = new(0x8B, 0x94, 0x9E);

      public DashboardView()
      {
         InitializeComponent();

         var axisPaint = new SolidColorPaint(Muted) { SKTypeface = SKTypeface.FromFamilyName("Segoe UI") };
         var gridPaint = new SolidColorPaint(new SKColor(0x8B, 0x94, 0x9E, 40)) { StrokeThickness = 1 };

         ThroughputChart.Series = new ISeries[]
         {
            new LineSeries<ObservableValue>
            {
               Values = throughput_,
               GeometrySize = 0,
               LineSmoothness = 0.8,
               Stroke = new SolidColorPaint(Accent) { StrokeThickness = 2.5f },
               Fill = new LinearGradientPaint(
                  new[] { Accent.WithAlpha(70), Accent.WithAlpha(0) },
                  new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
               Name = "msgs/min"
            }
         };
         ThroughputChart.XAxes = new[] { new Axis { IsVisible = false } };
         ThroughputChart.YAxes = new[] { new Axis { LabelsPaint = axisPaint, SeparatorsPaint = gridPaint, MinLimit = 0 } };
         ThroughputChart.AnimationsSpeed = TimeSpan.FromMilliseconds(400);

         SessionsChart.Series = new ISeries[]
         {
            MakeSessionSeries("SMTP", smtp_, Green),
            MakeSessionSeries("IMAP", imap_, Purple),
            MakeSessionSeries("POP3", pop3_, Amber)
         };
         SessionsChart.XAxes = new[] { new Axis { IsVisible = false } };
         SessionsChart.YAxes = new[] { new Axis { LabelsPaint = axisPaint, SeparatorsPaint = gridPaint, MinLimit = 0 } };
         SessionsChart.AnimationsSpeed = TimeSpan.FromMilliseconds(400);

         timer_ = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
         timer_.Tick += (s, e) => Refresh();
      }

      private static LineSeries<ObservableValue> MakeSessionSeries(
         string name, ObservableCollection<ObservableValue> values, SKColor color)
      {
         return new LineSeries<ObservableValue>
         {
            Name = name,
            Values = values,
            GeometrySize = 0,
            LineSmoothness = 0.8,
            Stroke = new SolidColorPaint(color) { StrokeThickness = 2f },
            Fill = null
         };
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

            KpiProcessed.Text = snap.ProcessedMessages.ToString("N0");
            KpiQueue.Text = snap.QueueLength.ToString("N0");
            KpiSpam.Text = snap.SpamBlocked.ToString("N0");
            KpiViruses.Text = snap.VirusesRemoved.ToString("N0");
            KpiUptime.Text = FormatUptime(snap.StartTime);

            Push(smtp_, snap.SmtpSessions);
            Push(imap_, snap.ImapSessions);
            Push(pop3_, snap.Pop3Sessions);

            DateTime now = DateTime.UtcNow;
            if (lastProcessed_ >= 0 && lastSample_ != DateTime.MinValue)
            {
               double seconds = (now - lastSample_).TotalSeconds;
               if (seconds > 0.5)
                  Push(throughput_, Math.Max(0, snap.ProcessedMessages - lastProcessed_) * 60.0 / seconds);
            }
            lastProcessed_ = snap.ProcessedMessages;
            lastSample_ = now;

            SubtitleText.Text = "Live server statistics - last update " + DateTime.Now.ToLongTimeString();
         }
         catch (Exception)
         {
            SubtitleText.Text = "Connection to the server lost.";
            timer_.Stop();
         }
      }

      private static void Push(ObservableCollection<ObservableValue> series, double value)
      {
         series.Add(new ObservableValue(value));
         while (series.Count > HistoryLength)
            series.RemoveAt(0);
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
