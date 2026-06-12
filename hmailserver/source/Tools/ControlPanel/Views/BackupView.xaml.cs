using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class BackupView : UserControl, IPageLifecycle
   {
      public BackupView()
      {
         InitializeComponent();
      }

      public void OnEnter()
      {
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
            dynamic backup = ServerSession.Current.Application.Settings.Backup;
            backup.Destination = DestinationBox.Text.Trim();
            backup.BackupDomains = CheckDomains.IsChecked == true;
            backup.BackupMessages = CheckMessages.IsChecked == true;
            backup.BackupSettings = CheckSettings.IsChecked == true;
            backup.CompressDestinationFiles = CheckCompress.IsChecked == true;
            backup.Save();
            ServerSession.Release(backup);
            return true;
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the backup settings: " + ex.Message, "Control Panel");
            return false;
         }
      }

      private void SaveSettings_Click(object sender, RoutedEventArgs e)
      {
         if (SaveBackupSettings())
            SubtitleText.Text = "Backup settings saved.";
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
