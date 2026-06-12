// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Live server dashboard with gauges, statistics cards and throughput history.

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using hMailServer.Administrator.Controls;
using hMailServer.Administrator.Utilities;

namespace hMailServer.Administrator
{
   public partial class ucDashboard : UserControl, ISettingsControl
   {
      private hMailServer.Application _application;
      private Timer _refreshTimer;

      // Controls
      private StatCard _cardUptime;
      private StatCard _cardProcessed;
      private StatCard _cardSpam;
      private StatCard _cardViruses;
      private StatCard _cardQueue;
      private ArcGauge _gaugeSmtp;
      private ArcGauge _gaugeImap;
      private ArcGauge _gaugePop3;
      private Sparkline _throughput;
      private Label _labelHeader;
      private Label _labelUpdated;

      // Throughput tracking
      private long _lastProcessed = -1;
      private DateTime _lastSample = DateTime.MinValue;

      public ucDashboard()
      {
         BuildUi();
      }

      private void BuildUi()
      {
         SuspendLayout();

         BackColor = Color.FromArgb(246, 248, 250);
         AutoScroll = true;

         _labelHeader = new Label
         {
            Text = "Server Dashboard",
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = Color.FromArgb(36, 41, 47),
            AutoSize = true,
            Location = new Point(16, 12)
         };
         Controls.Add(_labelHeader);

         _labelUpdated = new Label
         {
            Text = "",
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(130, 140, 152),
            AutoSize = true,
            Location = new Point(18, 44)
         };
         Controls.Add(_labelUpdated);

         // Row 1: stat cards
         _cardUptime = MakeCard("Server started", Color.FromArgb(9, 105, 218));
         _cardProcessed = MakeCard("Messages processed", Color.FromArgb(46, 160, 67));
         _cardQueue = MakeCard("Messages in queue", Color.FromArgb(227, 160, 8));
         _cardSpam = MakeCard("Spam blocked", Color.FromArgb(130, 80, 223));
         _cardViruses = MakeCard("Viruses removed", Color.FromArgb(207, 34, 46));

         // Row 2: session gauges
         _gaugeSmtp = MakeGauge("SMTP sessions");
         _gaugeImap = MakeGauge("IMAP sessions");
         _gaugePop3 = MakeGauge("POP3 sessions");

         // Row 3: throughput
         _throughput = new Sparkline
         {
            Text = "Delivery throughput (messages per minute)",
            Capacity = 150
         };
         Controls.Add(_throughput);

         _refreshTimer = new Timer { Interval = 2000 };
         _refreshTimer.Tick += RefreshTimer_Tick;

         Resize += (s, e) => LayoutDashboard();
         LayoutDashboard();

         ResumeLayout();
      }

      private StatCard MakeCard(string caption, Color accent)
      {
         StatCard card = new StatCard { Text = caption, AccentColor = accent };
         Controls.Add(card);
         return card;
      }

      private ArcGauge MakeGauge(string caption)
      {
         ArcGauge gauge = new ArcGauge { Text = caption, UnitText = "max" };
         Controls.Add(gauge);
         return gauge;
      }

      private void LayoutDashboard()
      {
         int margin = 16;
         int top = 70;
         int width = Math.Max(720, ClientSize.Width);

         // Row 1: five stat cards share the row
         int cardGap = 12;
         int cardWidth = (width - margin * 2 - cardGap * 4) / 5;
         int cardHeight = 84;
         StatCard[] cards = { _cardUptime, _cardProcessed, _cardQueue, _cardSpam, _cardViruses };
         for (int i = 0; i < cards.Length; i++)
         {
            cards[i].SetBounds(margin + i * (cardWidth + cardGap), top, cardWidth, cardHeight);
         }

         // Row 2: three gauges
         int gaugeTop = top + cardHeight + 18;
         int gaugeGap = 14;
         int gaugeWidth = (width - margin * 2 - gaugeGap * 2) / 3;
         int gaugeHeight = Math.Min(220, gaugeWidth);
         ArcGauge[] gauges = { _gaugeSmtp, _gaugeImap, _gaugePop3 };
         for (int i = 0; i < gauges.Length; i++)
         {
            gauges[i].SetBounds(margin + i * (gaugeWidth + gaugeGap), gaugeTop, gaugeWidth, gaugeHeight);
         }

         // Row 3: sparkline
         int sparkTop = gaugeTop + gaugeHeight + 18;
         _throughput.SetBounds(margin, sparkTop, width - margin * 2, 170);
      }

      public bool Dirty
      {
         get { return false; }
      }

      public void LoadData()
      {
         _application = APICreator.Application;

         try
         {
            hMailServer.Settings settings = _application.Settings;
            _gaugeSmtp.Maximum = Math.Max(1, settings.MaxSMTPConnections);
            _gaugeImap.Maximum = Math.Max(1, settings.MaxIMAPConnections);
            _gaugePop3.Maximum = Math.Max(1, settings.MaxPOP3Connections);
            Marshal.ReleaseComObject(settings);
         }
         catch (Exception)
         {
            // Settings unavailable; keep default maxima.
         }

         _lastProcessed = -1;
         _lastSample = DateTime.MinValue;
         _throughput.Reset();

         RefreshStatistics();
         _refreshTimer.Enabled = true;
      }

      public bool SaveData()
      {
         return true;
      }

      public void LoadResources()
      {
      }

      public void OnLeavePage()
      {
         _refreshTimer.Enabled = false;
      }

      private void RefreshTimer_Tick(object sender, EventArgs e)
      {
         RefreshStatistics();
      }

      private void RefreshStatistics()
      {
         if (_application == null)
            return;

         try
         {
            hMailServer.Status status = _application.Status;

            long processed = status.ProcessedMessages;
            _cardProcessed.ValueText = processed.ToString("N0");
            _cardSpam.ValueText = ((long)status.RemovedSpamMessages).ToString("N0");
            _cardViruses.ValueText = ((long)status.RemovedViruses).ToString("N0");

            string startTime = status.StartTime;
            _cardUptime.Text = "Server started";
            _cardUptime.ValueText = FormatUptime(startTime);

            // Queue depth: UndeliveredMessages is a newline-separated list.
            string queue = status.UndeliveredMessages;
            int queueCount = 0;
            if (!string.IsNullOrEmpty(queue))
            {
               foreach (string line in queue.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                  if (line.Trim().Length > 0)
                     queueCount++;
            }
            _cardQueue.ValueText = queueCount.ToString("N0");

            _gaugeSmtp.Value = status.get_SessionCount(eSessionType.eSTSMTP);
            _gaugeImap.Value = status.get_SessionCount(eSessionType.eSTIMAP);
            _gaugePop3.Value = status.get_SessionCount(eSessionType.eSTPOP3);

            // Throughput: delta processed / delta time, normalised to per-minute.
            DateTime now = DateTime.UtcNow;
            if (_lastProcessed >= 0 && _lastSample != DateTime.MinValue)
            {
               double seconds = (now - _lastSample).TotalSeconds;
               if (seconds > 0.5)
               {
                  double perMinute = Math.Max(0, processed - _lastProcessed) * 60.0 / seconds;
                  _throughput.AddPoint(perMinute);
               }
            }
            _lastProcessed = processed;
            _lastSample = now;

            _labelUpdated.Text = "Auto-refreshes every 2 seconds  -  last update " + DateTime.Now.ToLongTimeString();

            Marshal.ReleaseComObject(status);
         }
         catch (Exception)
         {
            // Server connection lost; stop refreshing quietly.
            _refreshTimer.Enabled = false;
            _labelUpdated.Text = "Connection to server lost.";
         }
      }

      private static string FormatUptime(string startTime)
      {
         DateTime started;
         if (DateTime.TryParse(startTime, out started))
         {
            TimeSpan up = DateTime.Now - started;
            if (up.TotalSeconds < 0)
               return startTime;
            if (up.TotalDays >= 1)
               return string.Format("{0}d {1}h {2}m", (int)up.TotalDays, up.Hours, up.Minutes);
            if (up.TotalHours >= 1)
               return string.Format("{0}h {1}m", up.Hours, up.Minutes);
            return string.Format("{0}m", Math.Max(0, (int)up.TotalMinutes));
         }
         return string.IsNullOrEmpty(startTime) ? "-" : startTime;
      }
   }
}
