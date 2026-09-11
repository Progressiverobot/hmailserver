// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Server / database / health overview — the parts of the classic Administrator
   /// Status pane that the live Dashboard does not already cover (version, database
   /// details, server state and the configuration warnings).
   ///
   /// Every readout on this page is a caption in one grid column and a value in the
   /// next, and WPF infers no relationship between the two, so the values are set
   /// through <see cref="SetValue_"/> rather than by assigning Text - see the
   /// comment there for what a screen reader heard before.
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

      private void CheckUpdate_Click(object sender, RoutedEventArgs e) => RunUpdateAction_("check");
      private void DownloadUpdate_Click(object sender, RoutedEventArgs e) => RunUpdateAction_("download");
      private void InstallUpdate_Click(object sender, RoutedEventArgs e) => RunUpdateAction_("install");

      // The three update steps, on the server: a check reads the feed, a download
      // fetches and verifies the installer, an install hands it to the helper. Each
      // answers when it is done, so the cursor waits with it.
      private void RunUpdateAction_(string action)
      {
         dynamic app = ServerSession.Current?.Application;
         if (app == null)
            return;

         if (action == "install" && MessageBox.Show(
                L("Install the update now?\n\nThe verified installer is handed to the update helper: the service stops, the new version is installed, and the service starts again. If it does not come back, the previous version is reinstalled. This Control Panel loses its connection while that happens; reconnect afterwards to see the outcome here."),
                L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

         Mouse.OverrideCursor = Cursors.Wait;
         try
         {
            dynamic status = app.Status;
            bool done = action == "check" ? (bool)status.CheckForUpdate()
               : action == "download" ? (bool)status.DownloadUpdate()
               : (bool)status.InstallUpdate();
            string error = (string)status.UpdateLastError;
            ServerSession.Release(status);
            if (!done && !string.IsNullOrEmpty(error))
               MessageBox.Show(error, L("Control Panel"), MessageBoxButton.OK, MessageBoxImage.Warning);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("The update step could not be run: {0}", ex.Message), L("Control Panel"), MessageBoxButton.OK, MessageBoxImage.Error);
         }
         finally
         {
            Mouse.OverrideCursor = null;
         }
         Reload();
      }

      /// <summary>
      /// Pause and resume are Application.Stop()/Start() over COM - the engine stops
      /// while the Windows SERVICE keeps running, which is what the classic
      /// Administrator's pause did (its ucStatus button called exactly these). This
      /// went missing when that tool was retired: the Control Panel only offered a
      /// service-level restart, so "stop accepting mail for a moment without killing
      /// the process" stopped being possible from any UI.
      /// </summary>
      private void PauseResume_Click(object sender, RoutedEventArgs e)
      {
         dynamic app = ServerSession.Current?.Application;
         if (app == null)
            return;

         try
         {
            int state = (int)app.ServerState;

            if (state == ServerStateRunning_)
            {
               if (MessageBox.Show(
                      L("Pause the mail server?\n\nNo new connections will be accepted and no mail will be delivered until it is resumed. The Windows service keeps running, so this Control Panel stays connected."),
                      L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                  return;

               app.Stop();
            }
            else if (state == ServerStateStopped_)
            {
               app.Start();
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            // The likeliest failure is rights: pausing requires an administrator-level
            // COM session. Said outright rather than as a raw HRESULT.
            MessageBox.Show(F("The server state could not be changed: {0}", ex.Message),
               L("Control Panel"), MessageBoxButton.OK, MessageBoxImage.Error);
         }

         Reload();
      }

      private const int ServerStateStopped_ = 1;
      private const int ServerStateRunning_ = 3;

      private void UpdatePauseButton_(int state)
      {
         switch (state)
         {
            case ServerStateRunning_:
               PauseButton.Content = L("_Pause");
               PauseButtonIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Pause24;
               PauseButton.IsEnabled = true;
               break;
            case ServerStateStopped_:
               // The glyph swaps with the verb - a "Resume" button wearing a
               // pause icon says two things at once, and one of them is wrong.
               PauseButton.Content = L("_Resume");
               PauseButtonIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Play24;
               PauseButton.IsEnabled = true;
               break;
            default:
               // Starting or stopping: a transition is already in progress.
               PauseButton.IsEnabled = false;
               break;
         }

         AutomationProperties.SetName(PauseButton, F("{0} the mail server engine", MnemonicText.Strip((string)PauseButton.Content)));
      }

      private static string DatabaseTypeName(int type) => type switch
      {
         1 => "MySQL / MariaDB",   // no-loc: product names
         2 => "Microsoft SQL Server",   // no-loc
         3 => "PostgreSQL",   // no-loc
         4 => L("Built-in (SQL Server Compact)"),
         _ => L("Unknown")
      };

      private static string ServerStateName(int state) => state switch
      {
         1 => L("Stopped"),
         2 => L("Starting"),
         3 => L("Running"),
         4 => L("Stopping"),
         _ => L("Unknown")
      };

      private void Reload()
      {
         dynamic app = ServerSession.Current?.Application;
         if (app == null)
            return;

         // Server + database
         try
         {
            SetValue_(VersionValue, L("Version"),
               (string)app.Version + " (" + (string)app.VersionArchitecture + ")");
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { SetValue_(VersionValue, L("Version"), "-"); }

         // The update check's verdict, kept by the server; the row says what the
         // scheduled check (UpdateCheckEnabled) or an on-demand one last found.
         try
         {
            dynamic status = app.Status;
            int updateState = (int)status.UpdateState;
            string availableVersion = (string)status.AvailableVersion;
            string applyOutcome = (string)status.UpdateApplyOutcome;
            SetValue_(UpdateValue, L("Update"), UpdateText_(updateState, availableVersion,
               (string)status.AvailableVersionPublished, (string)status.UpdateLastChecked, (string)status.UpdateLastError, applyOutcome));
            ServerSession.Release(status);
            // Download once a newer release is known (a failed download can be retried);
            // install once one is verified and waiting.
            DownloadUpdateButton.IsEnabled = updateState == 2 || (updateState == 5 && !string.IsNullOrEmpty(availableVersion));
            InstallUpdateButton.IsEnabled = updateState == 3;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            SetValue_(UpdateValue, L("Update"), "-");
            DownloadUpdateButton.IsEnabled = false;
            InstallUpdateButton.IsEnabled = false;
         }

         try
         {
            int state = (int)app.ServerState;
            SetValue_(StateValue, L("State"), ServerStateName(state));
            UpdatePauseButton_(state);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            SetValue_(StateValue, L("State"), "-");
            PauseButton.IsEnabled = false;
         }

         try
         {
            dynamic db = app.Database;
            SetValue_(DbTypeValue, L("Database type"), DatabaseTypeName((int)db.DatabaseType));
            string host = (string)db.ServerName;
            SetValue_(DbHostValue, L("Database host"), string.IsNullOrEmpty(host) ? "-" : host);
            string name = (string)db.DatabaseName;
            SetValue_(DbNameValue, L("Database name"), string.IsNullOrEmpty(name) ? "-" : name);
            SetValue_(DbVersionValue, L("Database schema version"), ((int)db.CurrentVersion).ToString());
            ServerSession.Release(db);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            SetValue_(DbTypeValue, L("Database type"), "-");
            SetValue_(DbHostValue, L("Database host"), "-");
            SetValue_(DbNameValue, L("Database name"), "-");
            SetValue_(DbVersionValue, L("Database schema version"), "-");
         }

         // Statistics + uptime
         try
         {
            var snap = ServerSession.Current.ReadStatus();
            SetValue_(ProcessedValue, L("Processed messages"), snap.ProcessedMessages.ToString("N0"));
            SetValue_(SpamValue, L("Spam removed"), snap.SpamBlocked.ToString("N0"));
            SetValue_(VirusValue, L("Viruses removed"), snap.VirusesRemoved.ToString("N0"));
            SetValue_(SmtpValue, L("SMTP sessions"), snap.SmtpSessions.ToString());
            SetValue_(ImapValue, L("IMAP sessions"), snap.ImapSessions.ToString());
            SetValue_(Pop3Value, L("POP3 sessions"), snap.Pop3Sessions.ToString());
            SetValue_(StartedValue, L("Started"), string.IsNullOrEmpty(snap.StartTime) ? "-" : snap.StartTime);
            SetValue_(UptimeValue, L("Uptime"), FormatUptime(snap.StartTime));
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            SetValue_(ProcessedValue, L("Processed messages"), "-");
            SetValue_(SpamValue, L("Spam removed"), "-");
            SetValue_(VirusValue, L("Viruses removed"), "-");
            SetValue_(SmtpValue, L("SMTP sessions"), "-");
            SetValue_(ImapValue, L("IMAP sessions"), "-");
            SetValue_(Pop3Value, L("POP3 sessions"), "-");
         }

         LoadWarnings(app);
      }

      /// <summary>
      /// Sets a readout and the accessible name that goes with it.
      ///
      /// The caption and the value are two <c>TextBlock</c>s in adjacent grid cells
      /// and WPF infers no relationship between them, so UI Automation saw eighteen
      /// bare values on this page: a screen reader announced "6.2.18 (x64)",
      /// "Running", "3", "3", "0" with nothing at all saying which was which - and
      /// three of those are single digits that could equally be the SMTP, IMAP or
      /// POP3 session count.
      ///
      /// <c>AutomationProperties.LabeledBy</c> looks like the tool for this and is
      /// not: on a TextBlock it *replaces* the announced name, so the reader would
      /// hear the caption and never the number. Naming the value "caption, value" is
      /// what the dashboard's KPI row already does, and doing the same here means
      /// the two pages sound alike.
      /// </summary>
      // One line for the status card, from Status.UpdateState and its companions. The
      // last apply's outcome, reported when the service came back, comes first when
      // there is one: it is what the administrator who clicked Install is waiting for.
      internal static string UpdateText_(int state, string version, string published, string checkedAt, string error, string applyOutcome = null)
      {
         string when = string.IsNullOrEmpty(published) ? "" : F(" (published {0})", published);
         string outcome = string.IsNullOrEmpty(applyOutcome) ? "" : F("Last update: {0}. ", applyOutcome);
         switch (state)
         {
            case 1: return outcome + L("This is the latest release") + (string.IsNullOrEmpty(checkedAt) ? "" : F(", checked {0}", checkedAt));
            case 2: return outcome + F("{0} is available", version) + when;
            case 3: return outcome + F("{0} is downloaded and verified", version) + when + L("; Install update applies it");
            case 4: return outcome + F("Installing {0}: the service will stop and start", version);
            case 5: return outcome + F("The last update step failed: {0}", error);
            default: return outcome + L("Not checked yet (Check for updates, or turn on the daily check under Updates on the API & monitoring page)");
         }
      }

      private static void SetValue_(TextBlock value, string label, string text)
      {
         value.Text = text;
         AutomationProperties.SetName(value, label + ", " + text);
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
               if (((string)settings.HostName).Length == 0)
                  count += AddWarning(L("High"), L("No public host name is configured in the SMTP settings."));

               if ((bool)settings.DenyMailFromNull)
                  count += AddWarning(L("High"), L("Mail from an empty sender address is denied. Many servers send bounces from <>, which will be rejected."));

               dynamic ranges = settings.SecurityRanges;
               int autoban = 0;
               int rangeCount = (int)ranges.Count;
               for (int i = 0; i < rangeCount; i++)
               {
                  dynamic range = ranges.Item[i];
                  try
                  {
                     if ((bool)range.AllowDeliveryFromRemoteToRemote && !(bool)range.RequireSMTPAuthExternalToExternal)
                        count += AddWarning(L("Critical"), F("IP range '{0}' allows external-to-external delivery without authentication (open relay risk).", (string)range.Name));

                     if ((string)range.LowerIP == "127.0.0.1" && (string)range.UpperIP == "127.0.0.1" && (bool)range.Expires)
                        count += AddWarning(L("High"), L("Localhost is currently banned in the IP ranges."));

                     if ((bool)range.Expires)
                        autoban++;
                  }
                  finally
                  {
                     ServerSession.Release(range);
                  }
               }
               ServerSession.Release(ranges);

               if (autoban > 0)
                  count += AddWarning(L("Medium"), F("There is a total of {0} auto-ban IP range(s).", autoban));
            }
            finally
            {
               ServerSession.Release(settings);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            AddWarning(L("Info"), F("Could not evaluate all warnings: {0}", ex.Message));
            return;
         }

         if (count == 0)
         {
            var ok = new TextBlock
            {
               Text = L("No configuration warnings."),
               FontSize = Typography.Body,
               Margin = new Thickness(0, 2, 0, 2)
            };
            ok.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
            WarningsPanel.Children.Add(ok);
         }
      }

      /// <summary>
      /// Adds one configuration warning: a severity mark, the severity word, and
      /// the message.
      ///
      /// This used to be a filled pill in one of four hardcoded RGB values with
      /// <c>Brushes.White</c> printed on it, and that carried three defects at once.
      ///
      /// The colours never consulted the theme, so on a High Contrast desktop the
      /// one page that says "this server may be an open relay" drew its severities
      /// in exactly the four colours the user had just told Windows they could not
      /// read.
      ///
      /// White on the "Medium" amber #C28A00 is 3.03:1 and white on the "High"
      /// orange #D24F1A is 4.31:1, against the 4.5:1 that eleven-point text needs -
      /// so two of the four pills failed WCAG AA in every theme, not only in High
      /// Contrast. Black text would have fixed those two and broken Critical
      /// (3.50:1), which is what a filled pill costs you: two colours to get right
      /// per severity instead of one.
      ///
      /// And nothing joined the badge to the message, so a reader moving through the
      /// page heard "Critical" and then, as a separate unrelated item, a sentence.
      ///
      /// It is now the presentation the dashboard's backlog badge already uses and
      /// StatusSemanticsTests already pins: a shape, a word, and a theme brush key
      /// that ThemeTokens keeps correct in all three themes. There is no
      /// foreground/background pair left to get wrong, because there is no fill; the
      /// shape carries the severity for a reader who cannot separate the colours;
      /// and the message's accessible name carries the severity in words.
      /// </summary>
      private int AddWarning(string severity, string text)
      {
         StatusPresentation status = StatusSemantics.For(StatusSemantics.ForConfigurationWarning(severity));

         var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         var mark = new Path
         {
            Width = 10,
            Height = 10,
            Stretch = Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
         };
         // By resource key, never by resolved brush: ThemeTokens republishes these
         // on every theme change and on Windows switching High Contrast, and a badge
         // handed a brush would be the one element left painted for the old theme.
         ShapeMarkVisuals.ApplyMark(mark, status.Shape, status.BrushKey);

         var word = new TextBlock
         {
            Text = severity,
            FontSize = Typography.Caption,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
         };
         word.SetResourceReference(TextBlock.ForegroundProperty, status.BrushKey);

         var badge = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 1, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
         };
         badge.Children.Add(mark);
         badge.Children.Add(word);
         Grid.SetColumn(badge, 0);
         row.Children.Add(badge);

         var msg = new TextBlock { Text = text, FontSize = Typography.Body, TextWrapping = TextWrapping.Wrap };
         msg.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");

         // The severity said again, in the message's own accessible name, so that a
         // reader landing on the sentence directly - which is what a virtual cursor
         // does - gets the complete statement rather than a bare sentence whose
         // severity was two elements ago. It costs one repeated word to anyone
         // reading straight through, which is the cheaper of the two mistakes.
         AutomationProperties.SetName(msg, severity + ". " + text);

         Grid.SetColumn(msg, 1);
         row.Children.Add(msg);

         WarningsPanel.Children.Add(row);
         return 1;
      }

      private static string FormatUptime(string startTime)
      {
         if (DateTime.TryParse(startTime, out DateTime started))
         {
            TimeSpan up = DateTime.Now - started;
            if (up.TotalSeconds < 0) return startTime;
            if (up.TotalDays >= 1) return (int)up.TotalDays + "d " + up.Hours + "h " + up.Minutes + "m";
            if (up.TotalHours >= 1) return up.Hours + "h " + up.Minutes + "m";
            return Math.Max(0, (int)up.TotalMinutes) + "m";
         }
         return string.IsNullOrEmpty(startTime) ? "-" : startTime;
      }
   }
}
