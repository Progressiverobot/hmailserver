// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using hMailServer.Shared;

namespace ImportTool.MboxImport
{
   public partial class ucMboxSelect : UserControl, IWizardPage
   {
      internal static readonly List<string> SelectedFiles = new List<string>();

      public ucMboxSelect()
      {
         InitializeComponent();
      }

      public string Title
      {
         get { return "Select folder containing mbox files"; }
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
            MessageBox.Show("You must select a folder containing at least one file.", "hMailServer Import Tool");
            return false;
         }

         SelectedFiles.Clear();
         foreach (ListViewItem item in listFiles.Items)
            SelectedFiles.Add(item.Text);

         return true;
      }

      private void buttonSelectDirectory_Click(object sender, EventArgs e)
      {
         using (var dialog = new FolderBrowserDialog())
         {
            dialog.Description = "Select folder to import";
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

               var item = listFiles.Items.Add(file);
               item.SubItems.Add(ucMboxProgress.GetFolderNameForFile(file));
            }
         }
      }
   }
}
