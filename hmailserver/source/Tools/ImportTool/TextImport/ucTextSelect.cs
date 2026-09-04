// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using hMailServer.Shared;

namespace ImportTool.TextImport
{
   public partial class ucTextSelect : UserControl, IWizardPage
   {
      internal static string SelectedFile;
      internal static string SelectedDomain;

      public ucTextSelect()
      {
         InitializeComponent();

         var domains = Globals.GetApp().Domains;
         for (int i = 0; i < domains.Count; i++)
         {
            var domain = domains[i];
            comboDomains.Items.Add(domain.Name);
         }

         if (comboDomains.Items.Count > 0)
            comboDomains.SelectedIndex = 0;
      }

      public string Title
      {
         get { return "Select file and destination domain"; }
      }

      public void OnShowPage(Dictionary<string, string> _state)
      {
      }

      public bool OnLeavePage(bool next)
      {
         if (!next)
            return true;

         if (textFile.Text.Length == 0)
         {
            MessageBox.Show("You must enter the name of the file to import.", "hMailServer Import Tool");
            return false;
         }

         if (!File.Exists(textFile.Text))
         {
            MessageBox.Show("The file " + textFile.Text + " does not exist.", "hMailServer Import Tool");
            return false;
         }

         if (comboDomains.SelectedItem == null)
         {
            MessageBox.Show("You must select a domain to import the accounts to.", "hMailServer Import Tool");
            return false;
         }

         SelectedFile = textFile.Text;
         SelectedDomain = (string)comboDomains.SelectedItem;

         return true;
      }

      private void buttonBrowse_Click(object sender, EventArgs e)
      {
         using (var dialog = new OpenFileDialog())
         {
            dialog.Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*";
            if (dialog.ShowDialog(this) == DialogResult.OK)
               textFile.Text = dialog.FileName;
         }
      }
   }
}
