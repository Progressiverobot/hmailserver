// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using hMailServer.Shared;

namespace ImportTool.MboxImport
{
   public partial class ucMboxAccount : UserControl, IWizardPage
   {
      internal string SelectedAccount { get; private set; }
      internal int SelectedAccountID { get; private set; }

      public ucMboxAccount()
      {
         InitializeComponent();

         var domains = Globals.GetApp().Domains;
         for (int i = 0; i < domains.Count; i++)
            comboDomains.Items.Add(domains[i].Name);

         if (comboDomains.Items.Count > 0)
            comboDomains.SelectedIndex = 0;
      }

      public string Title
      {
         get { return "Select destination account"; }
      }

      public void OnShowPage(Dictionary<string, string> _state)
      {
      }

      public bool OnLeavePage(bool next)
      {
         if (!next)
            return true;

         if (listAccounts.SelectedItems.Count == 0)
         {
            MessageBox.Show("You must select a destination account.", "hMailServer Import Tool");
            return false;
         }

         var address = listAccounts.SelectedItems[0].Text;
         var domain = Globals.GetApp().Domains.get_ItemByName((string)comboDomains.SelectedItem);
         var account = domain.Accounts.get_ItemByAddress(address);

         SelectedAccount = address;
         SelectedAccountID = account.ID;

         return true;
      }

      private void comboDomains_SelectedIndexChanged(object sender, EventArgs e)
      {
         listAccounts.Items.Clear();

         var domain = Globals.GetApp().Domains.get_ItemByName((string)comboDomains.SelectedItem);
         var accounts = domain.Accounts;
         for (int i = 0; i < accounts.Count; i++)
            listAccounts.Items.Add(accounts[i].Address);
      }
   }
}
