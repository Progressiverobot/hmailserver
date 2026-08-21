using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   public partial class BackupView : UserControl, IPageLifecycle
   {
      // BackupMessagesDBOnly changes what a backup and a restore do, but it lives
      // in hMailServer.ini rather than in the COM backup settings, so it needs its
      // own store alongside the COM properties on this page. The four schedule and
      // retention settings below live in the same file, for the same reason.
      private readonly IniFeatureStore iniStore_ = new IniFeatureStore();

      public BackupView()
      {
         InitializeComponent();
      }

      public void OnEnter()
      {
         if (iniStore_.IsAvailable)
         {
            CheckMessagesDbOnly.IsChecked = iniStore_.ReadBool("BackupMessagesDBOnly", false);

            // The schedule and retention settings, with the server's own defaults:
            // time "" and everything else 0, i.e. no schedule and no pruning
            // (IniFileSettings.cpp reads them exactly so).
            ScheduleTimeBox.Text = iniStore_.ReadString("ScheduledBackupTime", "").Trim();
            IntervalBox.Text = iniStore_.ReadString("ScheduledBackupIntervalMinutes", "0").Trim();
            KeepCountBox.Text = iniStore_.ReadString("ScheduledBackupKeepCount", "0").Trim();
            MaxAgeBox.Text = iniStore_.ReadString("ScheduledBackupMaxAgeDays", "0").Trim();
         }
         else
         {
            CheckMessagesDbOnly.IsEnabled = false;
            MessagesDbOnlyNote.Text = "hMailServer.ini was not found on this machine, so BackupMessagesDBOnly " +
                                      "can only be changed on the server itself.";

            // An editor that cannot read the value back must not write it either -
            // it would misreport its own state on the next visit.
            ScheduleTimeBox.IsEnabled = false;
            IntervalBox.IsEnabled = false;
            KeepCountBox.IsEnabled = false;
            MaxAgeBox.IsEnabled = false;
            SaveScheduleButton.IsEnabled = false;
         }

         try
         {
            dynamic backup = ServerSession.Current.Application.Settings.Backup;
            DestinationBox.Text = (string) backup.Destination ?? "";
            CheckDomains.IsChecked = (bool) backup.BackupDomains;
            CheckMessages.IsChecked = (bool) backup.BackupMessages;
            CheckSettings.IsChecked = (bool) backup.BackupSettings;
            CheckCompress.IsChecked = (bool) backup.CompressDestinationFiles;
            ServerSession.Release(backup);
         }
         catch (Exception ex)
         {
            SubtitleText.Text = "Could not read the backup settings: " + ex.Message;
         }

         RefreshScheduleStatus();
      }

      public void OnLeave()
      {
      }

      private bool SaveBackupSettings()
      {
         try
         {
            if (iniStore_.IsAvailable)
               iniStore_.WriteBool("BackupMessagesDBOnly", CheckMessagesDbOnly.IsChecked == true);

            dynamic backup = ServerSession.Current.Application.Settings.Backup;
            // IInterfaceBackupSettings has no Save method: each property setter
            // writes straight through to the server's settings store, so the
            // assignments above are already persisted.
            backup.Destination = DestinationBox.Text.Trim();
            backup.BackupDomains = CheckDomains.IsChecked == true;
            backup.BackupMessages = CheckMessages.IsChecked == true;
            backup.BackupSettings = CheckSettings.IsChecked == true;
            backup.CompressDestinationFiles = CheckCompress.IsChecked == true;
            ServerSession.Release(backup);
            return true;
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the backup settings: " + ex.Message, "Control Panel");
            return false;
         }
      }

      private void BrowseDestination_Click(object sender, RoutedEventArgs e)
      {
         string folder = PathPicker.PickFolder(DestinationBox.Text);
         if (folder != null)
            DestinationBox.Text = folder;
      }

      private void BrowseBackupFile_Click(object sender, RoutedEventArgs e)
      {
         string file = PathPicker.PickFile(BackupFileBox.Text, "Backup files (*.xml)|*.xml|All files (*.*)|*.*");
         if (file != null)
            BackupFileBox.Text = file;
      }

      private void SaveSettings_Click(object sender, RoutedEventArgs e)
      {
         if (SaveBackupSettings())
         {
            // The COM properties are live immediately; the INI one is read when
            // the service starts, so don't claim both took effect.
            SubtitleText.Text = iniStore_.IsAvailable
               ? "Backup settings saved - the message-metadata-only switch applies after a service restart."
               : "Backup settings saved.";
            Services.Toast.Success("Backup settings saved.");

            // The destination may have changed, and the destination is where the
            // "last backup" state is read from.
            RefreshScheduleStatus();
         }
      }

      private void StartBackup_Click(object sender, RoutedEventArgs e)
      {
         if (!SaveBackupSettings())
            return;

         try
         {
            dynamic manager = ServerSession.Current.Application.BackupManager;
            manager.StartBackup();
            ServerSession.Release(manager);
            SubtitleText.Text = "Backup started " + DateTime.Now.ToLongTimeString() +
                                " - runs in the background on the server.";
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not start the backup: " + ex.Message, "Control Panel");
         }
      }

      private void StartRestore_Click(object sender, RoutedEventArgs e)
      {
         string backupFile = BackupFileBox.Text.Trim();
         if (backupFile.Length == 0)
         {
            MessageBox.Show("Enter the path of the backup XML file on the server.", "Control Panel");
            return;
         }

         if (MessageBox.Show(
             "Restoring replaces current data for the selected categories with the backup contents.\n\nContinue?",
             "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         try
         {
            dynamic manager = ServerSession.Current.Application.BackupManager;
            dynamic backup = manager.LoadBackup(backupFile);
            backup.RestoreDomains = RestoreDomains.IsChecked == true;
            backup.RestoreMessages = RestoreMessages.IsChecked == true;
            backup.RestoreSettings = RestoreSettings.IsChecked == true;
            backup.StartRestore();
            ServerSession.Release(backup);
            ServerSession.Release(manager);
            SubtitleText.Text = "Restore started - runs in the background on the server.";
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not start the restore: " + ex.Message, "Control Panel");
         }
      }

      // ---- Schedule & retention (hMailServer.ini) -----------------------------

      private void SaveSchedule_Click(object sender, RoutedEventArgs e)
      {
         if (!iniStore_.IsAvailable)
            return;

         string time = ScheduleTimeBox.Text.Trim();
         if (time.Length > 0 && !TryParseTimeOfDay(time, out _, out _))
         {
            // The server is deliberately strict (BackupScheduleTask::ParseTimeOfDay
            // refuses anything it would have to guess at), so refuse here too
            // rather than store a value the service will reject at start-up.
            MessageBox.Show("'" + time + "' is not a 24-hour HH:MM time. The server accepts values like 02:00 " +
                            "or 23:45 and refuses anything else rather than guess. Leave the box empty for no " +
                            "daily backup.", "Control Panel");
            return;
         }

         if (!TryReadCount(IntervalBox, "Backup interval in minutes", out int intervalMinutes))
            return;
         if (!TryReadCount(KeepCountBox, "Archives to keep", out int keepCount))
            return;
         if (!TryReadCount(MaxAgeBox, "Delete archives older than this many days", out int maxAgeDays))
            return;

         try
         {
            iniStore_.WriteString("ScheduledBackupTime", time);
            iniStore_.WriteString("ScheduledBackupIntervalMinutes", intervalMinutes.ToString(CultureInfo.InvariantCulture));
            iniStore_.WriteString("ScheduledBackupKeepCount", keepCount.ToString(CultureInfo.InvariantCulture));
            iniStore_.WriteString("ScheduledBackupMaxAgeDays", maxAgeDays.ToString(CultureInfo.InvariantCulture));
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not write to hMailServer.ini: " + ex.Message, "Control Panel");
            return;
         }

         // Show what was actually stored, normalised (an empty count box was 0).
         ScheduleTimeBox.Text = time;
         IntervalBox.Text = intervalMinutes.ToString(CultureInfo.InvariantCulture);
         KeepCountBox.Text = keepCount.ToString(CultureInfo.InvariantCulture);
         MaxAgeBox.Text = maxAgeDays.ToString(CultureInfo.InvariantCulture);

         SubtitleText.Text = "Backup schedule saved - the service reads these settings when it starts, " +
                             "so they apply after a service restart.";
         Services.Toast.Success("Backup schedule saved.");
         RefreshScheduleStatus();
      }

      private void RefreshScheduleStatus_Click(object sender, RoutedEventArgs e)
      {
         RefreshScheduleStatus();
      }

      private static bool TryReadCount(Wpf.Ui.Controls.TextBox box, string label, out int value)
      {
         if (!NumericField.TryValidate(box.Text, label, 0, int.MaxValue, out value, out bool hasValue, out string error))
         {
            MessageBox.Show(error, "Control Panel");
            return false;
         }

         // An empty box means the shipped default, which for all three counts is 0.
         if (!hasValue)
            value = 0;

         return true;
      }

      /// <summary>
      /// Recomputes the two state lines: whether a schedule is configured (from
      /// hMailServer.ini, the file the service reads at start-up) and how old the
      /// newest archive in the destination is (from the destination directory
      /// itself, which is also where the server recovers this fact from after a
      /// restart - see BackupScheduleTask::Seed_).
      /// </summary>
      private void RefreshScheduleStatus()
      {
         bool scheduleConfigured = false;
         int expectedMinutes = 0;

         StatusLevel scheduleLevel;
         string scheduleText;

         if (!iniStore_.IsAvailable)
         {
            scheduleLevel = StatusLevel.Warning;
            scheduleText = "Cannot tell whether a schedule is configured: hMailServer.ini was not found on " +
                           "this machine. Check ScheduledBackupTime and ScheduledBackupIntervalMinutes on the " +
                           "server itself.";
         }
         else
         {
            string time = iniStore_.ReadString("ScheduledBackupTime", "").Trim();
            int intervalMinutes = ParseServerInt(iniStore_.ReadString("ScheduledBackupIntervalMinutes", "0"));

            // These four branches mirror BackupScheduleTask: a valid daily time
            // wins over the interval; an invalid time falls back to the interval
            // if there is one; and with neither, no task is created at all.
            if (time.Length > 0 && TryParseTimeOfDay(time, out int hour, out int minute))
            {
               scheduleConfigured = true;
               expectedMinutes = 24 * 60;
               scheduleLevel = StatusLevel.Good;
               scheduleText = string.Format(CultureInfo.InvariantCulture,
                  "A schedule is configured: daily at {0:00}:{1:00}, server local time.", hour, minute);
            }
            else if (time.Length > 0 && intervalMinutes > 0)
            {
               scheduleConfigured = true;
               expectedMinutes = intervalMinutes;
               scheduleLevel = StatusLevel.Warning;
               scheduleText = "ScheduledBackupTime '" + time + "' is not a valid 24-hour HH:MM time, so the " +
                              "server falls back to the interval: one backup every " + intervalMinutes +
                              " minute(s).";
            }
            else if (time.Length > 0)
            {
               scheduleLevel = StatusLevel.Critical;
               scheduleText = "ScheduledBackupTime '" + time + "' is not a valid 24-hour HH:MM time and no " +
                              "interval is set, so no scheduled backup will ever run.";
            }
            else if (intervalMinutes > 0)
            {
               scheduleConfigured = true;
               expectedMinutes = intervalMinutes;
               scheduleLevel = StatusLevel.Good;
               scheduleText = "A schedule is configured: one backup every " + intervalMinutes + " minute(s).";
            }
            else
            {
               scheduleLevel = StatusLevel.Warning;
               scheduleText = "No schedule is configured, so no backup will ever run on its own. Backups " +
                              "happen only when someone clicks Start backup now.";
            }
         }

         ApplyStatus(ScheduleMark, ScheduleStatusText, scheduleLevel, scheduleText);

         StatusLevel lastLevel;
         string lastText;

         string destination = TryReadDestination();

         if (destination == null)
         {
            lastLevel = StatusLevel.Warning;
            lastText = "Cannot tell when the last backup ran: the backup settings could not be read from the server.";
         }
         else if (destination.Length == 0)
         {
            lastLevel = StatusLevel.Warning;
            lastText = scheduleConfigured
               ? "No destination folder is configured, so the schedule above will skip every run until one is set."
               : "No destination folder is configured, so a backup has nowhere to go.";
         }
         else
         {
            string newestName = null;
            DateTime newestTime = DateTime.MinValue;
            bool destinationReadable;

            try
            {
               foreach (string file in System.IO.Directory.EnumerateFiles(destination))
               {
                  string name = System.IO.Path.GetFileName(file);
                  if (TryParseArchiveName(name, out DateTime created) &&
                      (newestName == null || created > newestTime))
                  {
                     newestName = name;
                     newestTime = created;
                  }
               }
               destinationReadable = true;
            }
            catch (Exception)
            {
               destinationReadable = false;
            }

            if (!destinationReadable)
            {
               lastLevel = scheduleConfigured ? StatusLevel.Warning : StatusLevel.Information;
               lastText = "Cannot tell when the last backup ran: " + destination +
                          " could not be read from this machine. Check the folder on the server, or in the " +
                          "backup log on the Logs page.";
            }
            else if (newestName == null)
            {
               lastLevel = scheduleConfigured ? StatusLevel.Critical : StatusLevel.Warning;
               lastText = "No backup has ever completed into " + destination +
                          " - there is no HMBackup archive there." +
                          (scheduleConfigured
                             ? " If the schedule was only just set up, re-check after its first run was due; " +
                               "otherwise the backup log on the Logs page says why runs are being skipped."
                             : "");
            }
            else
            {
               TimeSpan age = DateTime.Now - newestTime;
               string described = "Last backup: " + DescribeAge(age) + " (" + newestName + ").";

               // 2.0 rather than 2, so the doubling happens in floating point. As an
               // int multiply this overflows for any expectedMinutes above
               // int.MaxValue / 2 - reachable, because ScheduledBackupIntervalMinutes
               // is read from the INI and nothing bounds it - and an overflow lands
               // NEGATIVE. age.TotalMinutes > (negative) is then true for every backup,
               // so the card would announce that backups are failing on a server whose
               // backups are fine, which is the one thing a status card must not do.
               if (scheduleConfigured && expectedMinutes > 0 && age.TotalMinutes > expectedMinutes * 2.0)
               {
                  lastLevel = StatusLevel.Critical;
                  lastText = described + " That is older than the schedule allows, so runs are failing or " +
                             "being skipped - the backup log on the Logs page says why.";
               }
               else if (scheduleConfigured)
               {
                  lastLevel = StatusLevel.Good;
                  lastText = described;
               }
               else
               {
                  lastLevel = StatusLevel.Information;
                  lastText = described + " With no schedule configured, nothing will take the next one.";
               }
            }
         }

         ApplyStatus(LastBackupMark, LastBackupStatusText, lastLevel, lastText);
      }

      /// <summary>The saved destination, "" when none is set, or null when COM could not be read.</summary>
      private static string TryReadDestination()
      {
         try
         {
            dynamic backup = ServerSession.Current.Application.Settings.Backup;
            string destination = (string) backup.Destination ?? "";
            ServerSession.Release(backup);
            return destination.Trim();
         }
         catch (Exception)
         {
            return null;
         }
      }

      private static void ApplyStatus(System.Windows.Shapes.Path mark, TextBlock text,
                                      StatusLevel level, string message)
      {
         StatusPresentation presentation = StatusSemantics.For(level);
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);

         text.Inlines.Clear();
         text.Inlines.Add(new Run(presentation.SeverityWord + ": ") { FontWeight = FontWeights.SemiBold });
         text.Inlines.Add(new Run(message));

         // The severity word is part of the text itself, but the name is set too so
         // the announcement survives any future change to how the inlines are built.
         System.Windows.Automation.AutomationProperties.SetName(text, presentation.SeverityWord + ": " + message);
      }

      /// <summary>
      /// Mirrors BackupScheduleTask::ParseTimeOfDay exactly: strict 24-hour
      /// "HH:MM", one-or-two-digit hour, exactly-two-digit minute, nothing else.
      /// The server refuses "26:00" and "2" rather than guessing, so the editor
      /// must refuse the same values or it would claim a schedule the service will
      /// not run.
      /// </summary>
      private static bool TryParseTimeOfDay(string value, out int hour, out int minute)
      {
         hour = 0;
         minute = 0;

         string trimmed = (value ?? "").Trim();

         int separator = trimmed.IndexOf(':');
         if (separator < 1)
            return false;

         string hourPart = trimmed.Substring(0, separator);
         string minutePart = trimmed.Substring(separator + 1);

         if (hourPart.Length > 2 || minutePart.Length != 2)
            return false;

         foreach (char c in hourPart)
            if (!char.IsAsciiDigit(c))
               return false;
         foreach (char c in minutePart)
            if (!char.IsAsciiDigit(c))
               return false;

         int parsedHour = int.Parse(hourPart, CultureInfo.InvariantCulture);
         int parsedMinute = int.Parse(minutePart, CultureInfo.InvariantCulture);

         if (parsedHour > 23 || parsedMinute > 59)
            return false;

         hour = parsedHour;
         minute = parsedMinute;
         return true;
      }

      /// <summary>
      /// Mirrors BackupRetention::ParseArchiveName: "HMBackup YYYY-MM-DD HHMMSS.7z",
      /// 29 characters, case-insensitive prefix and extension, digits in the digit
      /// positions, and a date that actually exists. The name is generated in
      /// exactly one place (BackupExecuter::StartBackup) and parsed by the server's
      /// own retention and scheduling code with these same rules - if the format
      /// ever changes, all of them change together.
      /// </summary>
      private static bool TryParseArchiveName(string fileName, out DateTime created)
      {
         created = DateTime.MinValue;

         if (fileName == null || fileName.Length != 29)
            return false;

         if (!fileName.StartsWith("HMBackup ", StringComparison.OrdinalIgnoreCase))
            return false;

         if (!fileName.EndsWith(".7z", StringComparison.OrdinalIgnoreCase))
            return false;

         if (fileName[13] != '-' || fileName[16] != '-' || fileName[19] != ' ')
            return false;

         int[] dateDigitOffsets = { 9, 10, 11, 12, 14, 15, 17, 18 };
         foreach (int offset in dateDigitOffsets)
            if (!char.IsAsciiDigit(fileName[offset]))
               return false;

         for (int offset = 20; offset <= 25; offset++)
            if (!char.IsAsciiDigit(fileName[offset]))
               return false;

         int year = int.Parse(fileName.Substring(9, 4), CultureInfo.InvariantCulture);
         int month = int.Parse(fileName.Substring(14, 2), CultureInfo.InvariantCulture);
         int day = int.Parse(fileName.Substring(17, 2), CultureInfo.InvariantCulture);
         int hour = int.Parse(fileName.Substring(20, 2), CultureInfo.InvariantCulture);
         int minute = int.Parse(fileName.Substring(22, 2), CultureInfo.InvariantCulture);
         int second = int.Parse(fileName.Substring(24, 2), CultureInfo.InvariantCulture);

         try
         {
            // The archive name carries the server's local time.
            created = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
         }
         catch (ArgumentOutOfRangeException)
         {
            // 2026-02-30 and friends: digits in the right places that do not
            // describe a real instant are not a name the server wrote.
            return false;
         }

         return true;
      }

      /// <summary>
      /// How the running service would read a numeric INI value that this page did
      /// not write: anything unparseable counts as 0, i.e. off.
      /// </summary>
      private static int ParseServerInt(string value)
      {
         return int.TryParse((value ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
      }

      private static string DescribeAge(TimeSpan age)
      {
         if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

         if (age.TotalMinutes < 1)
            return "less than a minute ago";

         if (age.TotalHours < 1)
         {
            int minutes = (int) age.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : minutes + " minutes ago";
         }

         if (age.TotalDays < 2)
         {
            int hours = (int) age.TotalHours;
            return hours == 1 ? "1 hour ago" : hours + " hours ago";
         }

         return (int) age.TotalDays + " days ago";
      }
   }

   /// <summary>
   /// String-typed access to hMailServer.ini for the backup page. These exist as
   /// named ReadString/WriteString methods - rather than calls to the store's
   /// plain Read/Write - so that the settings-index generator and the test that
   /// audits it can both see, by scanning this source, exactly which INI keys the
   /// page edits. The same convention the ReadBool/WriteBool calls above follow.
   /// </summary>
   internal static class BackupIniStringAccess
   {
      public static string ReadString(this IniFeatureStore store, string key, string defaultValue)
      {
         return store.Read(key, defaultValue);
      }

      public static void WriteString(this IniFeatureStore store, string key, string value)
      {
         store.Write(key, value);
      }
   }
}
