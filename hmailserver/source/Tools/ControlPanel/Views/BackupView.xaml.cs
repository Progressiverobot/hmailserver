using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class BackupView : UserControl, IPageLifecycle
   {
      // BackupMessagesDBOnly changes what a backup and a restore do, but it lives
      // in hMailServer.ini rather than in the COM backup settings, so it needs its
      // own store alongside the COM properties on this page.
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
         }
         else
         {
            CheckMessagesDbOnly.IsEnabled = false;
            MessagesDbOnlyNote.Text = "hMailServer.ini was not found on this machine, so BackupMessagesDBOnly " +
                                      "can only be changed on the server itself.";
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
   }
}
