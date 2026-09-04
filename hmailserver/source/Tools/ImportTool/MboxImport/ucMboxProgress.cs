// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using hMailServer.Shared;

namespace ImportTool.MboxImport
{
   public partial class ucMboxProgress : UserControl, IWizardPage
   {
      public ucMboxProgress()
      {
         InitializeComponent();
      }

      public string Title
      {
         get { return "Import"; }
      }

      public void OnShowPage(Dictionary<string, string> _state)
      {
         textLog.Clear();

         try
         {
            RunImport();
         }
         catch (Exception ex)
         {
            AddToLog("The import failed: " + ex.Message);
         }
      }

      public bool OnLeavePage(bool next)
      {
         return true;
      }

      /// <summary>
      /// The IMAP folder a mbox file is imported into: the file name without
      /// its extension, with "inbox" mapping to the account's real inbox.
      /// </summary>
      internal static string GetFolderNameForFile(string path)
      {
         var name = Path.GetFileNameWithoutExtension(path);

         if (string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase))
            return "INBOX";

         return name;
      }

      private void RunImport()
      {
         var application = Globals.GetApp();
         var utilities = application.Utilities;

         // The COM import requires the message file to already be located in
         // the account's folder inside the data directory.
         var destinationDirectory = GetAccountDirectory(application);
         Directory.CreateDirectory(destinationDirectory);

         progressFiles.Minimum = 0;
         progressFiles.Maximum = ucMboxSelect.SelectedFiles.Count;
         progressFiles.Value = 0;

         var imported = 0;
         var failed = 0;

         foreach (var file in ucMboxSelect.SelectedFiles)
         {
            var folderName = GetFolderNameForFile(file);
            labelCurrentTask.Text = "Importing " + Path.GetFileName(file) + "...";

            progressCurrentFile.Minimum = 0;
            progressCurrentFile.Maximum = 100;
            progressCurrentFile.Value = 0;
            var fileSize = new FileInfo(file).Length;

            try
            {
               MboxParser.Parse(
                  file,
                  messageBytes =>
                  {
                     if (ImportMessage(utilities, destinationDirectory, folderName, messageBytes))
                        imported++;
                     else
                        failed++;
                  },
                  consumed =>
                  {
                     if (fileSize > 0)
                        progressCurrentFile.Value = (int)Math.Min(100, consumed * 100 / fileSize);

                     Application.DoEvents();
                  });
            }
            catch (Exception ex)
            {
               failed++;
               AddToLog(Path.GetFileName(file) + " failed: " + ex.Message);
            }

            progressFiles.Value++;
            Application.DoEvents();
         }

         labelCurrentTask.Text = "Done.";
         AddToLog("Import completed. Messages imported: " + imported + (failed > 0 ? ", failed: " + failed : "") + ".");
      }

      private bool ImportMessage(hMailServer.Utilities utilities, string destinationDirectory, string folderName, byte[] messageBytes)
      {
         // Use the server's on-disk convention, {account}\XY\{GUID}.eml where
         // XY is the GUID's first two characters. A file placed anywhere else
         // is moved (and renamed) by the import, after which a failure would
         // leave it orphaned at a path we no longer know.
         var guid = Guid.NewGuid().ToString().ToUpperInvariant();
         var subDirectory = Path.Combine(destinationDirectory, guid.Substring(0, 2));
         Directory.CreateDirectory(subDirectory);

         var fileName = Path.Combine(subDirectory, "{" + guid + "}.eml");

         File.WriteAllBytes(fileName, messageBytes);

         try
         {
            if (utilities.ImportMessageFromFileToIMAPFolder(fileName, ucMboxAccount.SelectedAccountID, folderName))
               return true;
         }
         catch (Exception ex)
         {
            AddToLog("A message failed to import: " + ex.Message);
         }

         // The server did not take ownership of the file; do not leave it
         // behind. Cleanup failure must not abort the remaining import.
         try
         {
            File.Delete(fileName);
         }
         catch (Exception)
         {
         }

         return false;
      }

      private static string GetAccountDirectory(hMailServer.Application application)
      {
         var dataDirectory = application.Settings.Directories.DataDirectory;

         var parts = ucMboxAccount.SelectedAccount.Split('@');
         if (parts.Length != 2)
            throw new InvalidOperationException("Unexpected account address: " + ucMboxAccount.SelectedAccount);

         return Path.Combine(dataDirectory, parts[1], parts[0]);
      }

      private void AddToLog(string message)
      {
         textLog.AppendText(message + Environment.NewLine);
         Application.DoEvents();
      }
   }
}
