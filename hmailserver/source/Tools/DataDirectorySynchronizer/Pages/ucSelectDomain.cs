// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using hMailServer.Shared;

namespace DataDirectorySynchronizer.Pages
{
   public partial class ucSelectDomain : UserControl, IWizardPage
   {
      public ucSelectDomain()
      {
         InitializeComponent();

         hMailServer.Application application = Globals.GetApp();
         hMailServer.Domains domains = application.Domains;

         for (int i = 0; i < domains.Count; i++)
         {
            hMailServer.Domain domain = domains[i];

            listViewDomains.Items.Add(domain.Name);

            Marshal.ReleaseComObject(domain);
         }

         Marshal.ReleaseComObject(domains);

      }

      public void OnShowPage(Dictionary<string, string> _state)
      {
         foreach (ListViewItem item in listViewDomains.Items)
         {
            item.Checked = Globals.SelectedDomains.Contains(item.Text);
         }

         checkPublicFolders.Checked = Globals.SynchronizePublicFolders;
      }

      public bool OnLeavePage(bool next)
      {
         Globals.SelectedDomains.Clear();

         foreach (ListViewItem item in listViewDomains.Items)
         {
            if (item.Checked)
               Globals.SelectedDomains.Add(item.Text);
         }

         Globals.SynchronizePublicFolders = checkPublicFolders.Checked;

         return true;
      }

      public string Title
      {
         get { return "Select what to synchronize"; }
      }

      private void buttonSelectAll_Click(object sender, EventArgs e)
      {
         foreach (ListViewItem item in listViewDomains.Items)
            item.Checked = true;

         checkPublicFolders.Checked = true;
      }

      private void buttonSelectNone_Click(object sender, EventArgs e)
      {
         foreach (ListViewItem item in listViewDomains.Items)
            item.Checked = false;

         checkPublicFolders.Checked = false;
      }
   }
}
