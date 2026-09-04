// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows.Forms;

namespace ImportTool.MboxImport
{
   public partial class formMboxImport : Form
   {
      public formMboxImport()
      {
         InitializeComponent();

         var accountPage = new ucMboxAccount();

         wizard.AddPage(new ucMboxSelect());
         wizard.AddPage(accountPage);
         wizard.AddPage(new ucMboxProgress(accountPage));
      }

      private void wizard_OnCancel(object sender, EventArgs e)
      {
         Close();
      }

      private void formMboxImport_Shown(object sender, EventArgs e)
      {
         wizard.Start();
      }

      private void wizard_PageChanged(int currentPage, int lastPage)
      {
         Text = "Import mbox files - Step " + currentPage + " of " + lastPage;
      }
   }
}
