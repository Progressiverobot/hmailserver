// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using hMailServer.Shared;

namespace ImportTool.MboxImport
{
   public partial class ucMboxProgress : UserControl, IWizardPage
   {
      // The page that chose the destination account; this page imports into it.
      private readonly ucMboxAccount accountPage_;

      public ucMboxProgress(ucMboxAccount accountPage)
      {
         InitializeComponent();
         accountPage_ = accountPage;
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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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
         var destinationDirectory = GetAccountDirectory(application, accountPage_.SelectedAccount);
         Directory.CreateDirectory(destinationDirectory);

         progressFiles.Minimum = 0;
         progressFiles.Maximum = ucMboxSelect.SelectedSources.Count;
         progressFiles.Value = 0;

         var imported = 0;
         var failed = 0;

         foreach (var source in ucMboxSelect.SelectedSources)
         {
            labelCurrentTask.Text = "Importing " + source.Display + "...";
            progressCurrentFile.Minimum = 0;
            progressCurrentFile.Maximum = 100;
            progressCurrentFile.Value = 0;

            try
            {
               if (source.Kind == ImportSourceKind.Mbox)
                  ImportMbox(utilities, destinationDirectory, source, ref imported, ref failed);
               else
                  ImportMaildirFolder(application, utilities, destinationDirectory, source, ref imported, ref failed);
            }
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
            {
               failed++;
               AddToLog(source.Display + " failed: " + ex.Message);
            }

            progressFiles.Value++;
            Application.DoEvents();
         }

         labelCurrentTask.Text = "Done.";
         AddToLog("Import completed. Messages imported: " + imported + (failed > 0 ? ", failed: " + failed : "") + ".");
      }

      private void ImportMbox(hMailServer.Utilities utilities, string destinationDirectory, ImportSource source, ref int imported, ref int failed)
      {
         var fileSize = new FileInfo(source.Path).Length;
         var importedHere = 0;
         var failedHere = 0;

         MboxParser.Parse(
            source.Path,
            messageBytes =>
            {
               if (ImportMessage(utilities, destinationDirectory, source.FolderName, messageBytes) != null)
                  importedHere++;
               else
                  failedHere++;
            },
            consumed =>
            {
               if (fileSize > 0)
                  progressCurrentFile.Value = (int)Math.Min(100, consumed * 100 / fileSize);
               Application.DoEvents();
            });

         imported += importedHere;
         failed += failedHere;
      }

      /// <summary>
      /// One Maildir folder: every message file imported into the IMAP folder of the
      /// same name, then the flags the file names carry set on the copies in one pass
      /// over the folder - one pass, because reading the folder's messages over COM is
      /// the whole folder each time, and a message per pass would be quadratic.
      /// </summary>
      private void ImportMaildirFolder(hMailServer.Application application, hMailServer.Utilities utilities, string destinationDirectory, ImportSource source, ref int imported, ref int failed)
      {
         var messages = MaildirReader.Messages(source.FolderName, source.Path).ToList();
         var flagged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
         var done = 0;

         foreach (var message in messages)
         {
            var fileName = ImportMessage(utilities, destinationDirectory, source.FolderName, MaildirReader.ReadMessage(message.Path));
            if (fileName == null)
            {
               failed++;
            }
            else
            {
               imported++;
               if (message.Flags.Length > 0)
                  flagged[Path.GetFileName(fileName)] = message.Flags;
            }

            done++;
            progressCurrentFile.Value = messages.Count == 0 ? 100 : (int)Math.Min(100, (long)done * 100 / messages.Count);
            Application.DoEvents();
         }

         if (flagged.Count > 0)
         {
            labelCurrentTask.Text = "Setting the flags in " + source.FolderName + "...";
            Application.DoEvents();
            ApplyFlags(application, source.FolderName, flagged);
         }
      }

      /// <summary>
      /// Sets the flags on the copies just imported into one folder: the folder walked
      /// from the account's root along the hierarchy delimiter, its messages read once,
      /// from the newest backwards until every flagged copy has been found.
      /// </summary>
      private void ApplyFlags(hMailServer.Application application, string folderName, Dictionary<string, string> flagged)
      {
         var delimiter = application.Settings.IMAPHierarchyDelimiter;
         if (string.IsNullOrEmpty(delimiter))
            delimiter = ".";

         var parts = accountPage_.SelectedAccount.Split('@');
         var account = application.Domains.get_ItemByName(parts[1]).Accounts.get_ItemByAddress(accountPage_.SelectedAccount);

         hMailServer.IMAPFolders folders = account.IMAPFolders;
         hMailServer.IMAPFolder folder = null;
         foreach (var part in folderName.Split(new[] { delimiter }, StringSplitOptions.RemoveEmptyEntries))
         {
            folder = folders.get_ItemByName(part);
            folders = folder.SubFolders;
         }
         if (folder == null)
            return;

         var messages = folder.Messages;
         var remaining = flagged.Count;
         for (var i = messages.Count - 1; i >= 0 && remaining > 0; i--)
         {
            var message = messages[i];
            string flags;
            if (!flagged.TryGetValue(Path.GetFileName(message.Filename), out flags))
               continue;

            foreach (var flag in flags.Split(' '))
            {
               switch (flag)
               {
                  case "\\Seen": message.set_Flag(hMailServer.eMessageFlag.eMFSeen, true); break;
                  case "\\Flagged": message.set_Flag(hMailServer.eMessageFlag.eMFFlagged, true); break;
                  case "\\Answered": message.set_Flag(hMailServer.eMessageFlag.eMFAnswered, true); break;
                  case "\\Draft": message.set_Flag(hMailServer.eMessageFlag.eMFDraft, true); break;
                  case "\\Deleted": message.set_Flag(hMailServer.eMessageFlag.eMFDeleted, true); break;
               }
            }
            message.Save();
            remaining--;
         }

         if (remaining > 0)
            AddToLog(folderName + ": " + remaining + " message(s) were imported but their flags could not be set - the copies were not found in the folder.");
      }

      /// <summary>Imports one message; the file it now lives in on success, null on failure.</summary>
      private string ImportMessage(hMailServer.Utilities utilities, string destinationDirectory, string folderName, byte[] messageBytes)
      {
         // Use the server's on-disk convention, {account}\XY\{GUID}.eml where
         // XY is the GUID's first two characters. A file placed anywhere else
         // is moved (and renamed) by the import, after which a failure would
         // leave it orphaned at a path we no longer know.
         var guid = Guid.NewGuid().ToString().ToUpperInvariant();
         var subDirectory = Path.Join(destinationDirectory, guid.Substring(0, 2));
         Directory.CreateDirectory(subDirectory);
         var fileName = Path.Join(subDirectory, "{" + guid + "}.eml");
         File.WriteAllBytes(fileName, messageBytes);

         try
         {
            if (utilities.ImportMessageFromFileToIMAPFolder(fileName, accountPage_.SelectedAccountID, folderName))
               return fileName;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            AddToLog("A message failed to import: " + ex.Message);
         }

         // The server did not take ownership of the file; do not leave it
         // behind. Cleanup failure must not abort the remaining import.
         try
         {
            File.Delete(fileName);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }

         return null;
      }

      private static string GetAccountDirectory(hMailServer.Application application, string selectedAccount)
      {
         var dataDirectory = application.Settings.Directories.DataDirectory;
         var parts = selectedAccount.Split('@');
         if (parts.Length != 2)
            throw new InvalidOperationException("Unexpected account address: " + selectedAccount);
         return Path.Join(dataDirectory, parts[1], parts[0]);
      }

      private void AddToLog(string message)
      {
         textLog.AppendText(message + Environment.NewLine);
         Application.DoEvents();
      }
   }
}
