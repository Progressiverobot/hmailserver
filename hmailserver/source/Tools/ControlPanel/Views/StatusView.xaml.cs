using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Server / database / health overview — the parts of the classic Administrator
   /// Status pane that the live Dashboard does not already cover (version, database
   /// details, server state and the configuration warnings).
   /// </summary>
   public partial class StatusView : UserControl, IPageLifecycle
   {
      public StatusView()
      {
         InitializeComponent();
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

      private static string DatabaseTypeName(int type) => type switch
      {
         1 => "MySQL / MariaDB",
         2 => "Microsoft SQL Server",
         3 => "PostgreSQL",
         4 => "Built-in (SQL Server Compact)",
         _ => "Unknown"
      };

      private static string ServerStateName(int state) => state switch
      {
         1 => "Stopped",
         2 => "Starting",
         3 => "Running",
         4 => "Stopping",
         _ => "Unknown"
      };

      private void Reload()
      {
         dynamic app = ServerSession.Current?.Application;
         if (app == null)
            return;

         // Server + database
         try
         {
            VersionValue.Text = (string) app.Version + " (" + (string) app.VersionArchitecture + ")";
         }
         catch (Exception) { VersionValue.Text = "-"; }

         try { StateValue.Text = ServerStateName((int) app.ServerState); }
         catch (Exception) { StateValue.Text = "-"; }

         try
         {
            dynamic db = app.Database;
            DbTypeValue.Text = DatabaseTypeName((int) db.DatabaseType);
            string host = (string) db.ServerName;
            DbHostValue.Text = string.IsNullOrEmpty(host) ? "-" : host;
            string name = (string) db.DatabaseName;
            DbNameValue.Text = string.IsNullOrEmpty(name) ? "-" : name;
            DbVersionValue.Text = ((int) db.CurrentVersion).ToString();
            ServerSession.Release(db);
         }
         catch (Exception)
         {
            DbTypeValue.Text = DbHostValue.Text = DbNameValue.Text = DbVersionValue.Text = "-";
         }

         // Statistics + uptime
         try
         {
            var snap = ServerSession.Current.ReadStatus();
            ProcessedValue.Text = snap.ProcessedMessages.ToString("N0");
            SpamValue.Text = snap.SpamBlocked.ToString("N0");
            VirusValue.Text = snap.VirusesRemoved.ToString("N0");
            SmtpValue.Text = snap.SmtpSessions.ToString();
            ImapValue.Text = snap.ImapSessions.ToString();
            Pop3Value.Text = snap.Pop3Sessions.ToString();
            StartedValue.Text = string.IsNullOrEmpty(snap.StartTime) ? "-" : snap.StartTime;
            UptimeValue.Text = FormatUptime(snap.StartTime);
         }
         catch (Exception)
         {
            ProcessedValue.Text = SpamValue.Text = VirusValue.Text = "-";
            SmtpValue.Text = ImapValue.Text = Pop3Value.Text = "-";
         }

         LoadWarnings(app);
      }

      private void LoadWarnings(dynamic app)
      {
         WarningsPanel.Children.Clear();
         int count = 0;

         try
         {
            dynamic settings = app.Settings;
            try
            {
               if (((string) settings.HostName).Length == 0)
                  count += AddWarning("High", "No public host name is configured in the SMTP settings.");

               if ((bool) settings.DenyMailFromNull)
                  count += AddWarning("High", "Mail from an empty sender address is denied. Many servers send bounces from <>, which will be rejected.");

               dynamic ranges = settings.SecurityRanges;
               int autoban = 0;
               int rangeCount = (int) ranges.Count;
               for (int i = 0; i < rangeCount; i++)
               {
                  dynamic range = ranges.Item[i];
                  try
                  {
                     if ((bool) range.AllowDeliveryFromRemoteToRemote && !(bool) range.RequireSMTPAuthExternalToExternal)
                        count += AddWarning("Critical", "IP range '" + (string) range.Name + "' allows external-to-external delivery without authentication (open relay risk).");

                     if ((string) range.LowerIP == "127.0.0.1" && (string) range.UpperIP == "127.0.0.1" && (bool) range.Expires)
                        count += AddWarning("High", "Localhost is currently banned in the IP ranges.");

                     if ((bool) range.Expires)
                        autoban++;
                  }
                  finally
                  {
                     ServerSession.Release(range);
                  }
               }
               ServerSession.Release(ranges);

               if (autoban > 0)
                  count += AddWarning("Medium", "There is a total of " + autoban + " auto-ban IP range(s).");
            }
            finally
            {
               ServerSession.Release(settings);
            }
         }
         catch (Exception ex)
         {
            AddWarning("Info", "Could not evaluate all warnings: " + ex.Message);
            return;
         }

         if (count == 0)
         {
            var ok = new TextBlock
            {
               Text = "No configuration warnings.",
               FontSize = 13,
               Margin = new Thickness(0, 2, 0, 2)
            };
            ok.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            WarningsPanel.Children.Add(ok);
         }
      }

      private int AddWarning(string severity, string text)
      {
         var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         var badge = new Border
         {
            Background = SeverityBrush(severity),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = severity, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
         };
         Grid.SetColumn(badge, 0);
         row.Children.Add(badge);

         var msg = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
         msg.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetColumn(msg, 1);
         row.Children.Add(msg);

         WarningsPanel.Children.Add(row);
         return 1;
      }

      private static Brush SeverityBrush(string severity) => severity switch
      {
         "Critical" => new SolidColorBrush(Color.FromRgb(0xC4, 0x18, 0x1E)),
         "High" => new SolidColorBrush(Color.FromRgb(0xD2, 0x4F, 0x1A)),
         "Medium" => new SolidColorBrush(Color.FromRgb(0xC2, 0x8A, 0x00)),
         _ => new SolidColorBrush(Color.FromRgb(0x55, 0x7A, 0x95))
      };

      private static string FormatUptime(string startTime)
      {
         if (DateTime.TryParse(startTime, out DateTime started))
         {
            TimeSpan up = DateTime.Now - started;
            if (up.TotalSeconds < 0) return startTime;
            if (up.TotalDays >= 1) return (int) up.TotalDays + "d " + up.Hours + "h " + up.Minutes + "m";
            if (up.TotalHours >= 1) return up.Hours + "h " + up.Minutes + "m";
            return Math.Max(0, (int) up.TotalMinutes) + "m";
         }
         return string.IsNullOrEmpty(startTime) ? "-" : startTime;
      }
   }
}
