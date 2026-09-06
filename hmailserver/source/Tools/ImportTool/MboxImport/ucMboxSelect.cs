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
   internal enum ImportSourceKind
   {
      /// <summary>One mbox file, one IMAP folder.</summary>
      Mbox,

      /// <summary>One folder of a Maildir tree: a directory with cur and new beneath it.</summary>
      Maildir
   }

   /// <summary>What the import page works through: a file or a directory, and the IMAP folder it becomes.</summary>
   internal sealed class ImportSource
   {
      public ImportSource(ImportSourceKind kind, string path, string folderName)
      {
         Kind = kind;
         Path = path;
         FolderName = folderName;
      }

      public ImportSourceKind Kind { get; private set; }
      public string Path { get; private set; }
      public string FolderName { get; private set; }

      public string Display
      {
         get { return Kind == ImportSourceKind.Mbox ? System.IO.Path.GetFileName(Path) : "Maildir folder " + FolderName; }
      }
   }

   public partial class ucMboxSelect : UserControl, IWizardPage
   {
      internal static readonly List<ImportSource> SelectedSources = new List<ImportSource>();

      public ucMboxSelect()
      {
         InitializeComponent();
      }

      public string Title
      {
         get { return "Select the mbox files or the Maildir to import"; }
      }

      public void OnShowPage(Dictionary<string, string> _state)
      {
      }

      public bool OnLeavePage(bool next)
      {
         if (!next)
            return true;

         if (listFiles.Items.Count == 0)
         {
            MessageBox.Show("Select a folder of mbox files, or a Maildir, with at least one message source in it.", "hMailServer Import Tool");
            return false;
         }

         SelectedSources.Clear();
         foreach (ListViewItem item in listFiles.Items)
            SelectedSources.Add((ImportSource)item.Tag);

         return true;
      }

      private void buttonSelectDirectory_Click(object sender, EventArgs e)
      {
         using (var dialog = new FolderBrowserDialog())
         {
            dialog.Description = "Select the folder holding the mbox files";
            if (dialog.ShowDialog(this) != DialogResult.OK)
               return;

            listFiles.Items.Clear();

            foreach (var file in Directory.GetFiles(dialog.SelectedPath))
            {
               // Skip mail-client index files that live next to the mboxes
               // (e.g. Thunderbird's Inbox.msf next to Inbox).
               var extension = Path.GetExtension(file);
               if (string.Equals(extension, ".msf", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".dat", StringComparison.OrdinalIgnoreCase))
                  continue;

               var folderName = ucMboxProgress.GetFolderNameForFile(file);
               var item = listFiles.Items.Add(file);
               item.SubItems.Add(folderName);
               item.Tag = new ImportSource(ImportSourceKind.Mbox, file, folderName);
            }
         }
      }

      private void buttonSelectMaildir_Click(object sender, EventArgs e)
      {
         using (var dialog = new FolderBrowserDialog())
         {
            dialog.Description = "Select the Maildir - the directory that holds cur, new and tmp";
            if (dialog.ShowDialog(this) != DialogResult.OK)
               return;

            if (!MaildirReader.IsMaildir(dialog.SelectedPath))
            {
               MessageBox.Show("That is not a Maildir: a Maildir is a directory with cur and new beneath it, and the folders below the INBOX beside them, their names starting with a dot.", "hMailServer Import Tool");
               return;
            }

            listFiles.Items.Clear();

            foreach (var folder in MaildirReader.Folders(dialog.SelectedPath))
            {
               var count = MaildirReader.Messages(folder.Key, folder.Value).Count();
               var item = listFiles.Items.Add(folder.Value + "  (" + count + " message" + (count == 1 ? "" : "s") + ")");
               item.SubItems.Add(folder.Key);
               item.Tag = new ImportSource(ImportSourceKind.Maildir, folder.Value, folder.Key);
            }
         }
      }
   }
}
