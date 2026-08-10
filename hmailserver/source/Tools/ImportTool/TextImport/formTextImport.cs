// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Windows.Forms;

namespace ImportTool.TextImport
{
   public partial class formTextImport : Form
   {
      public formTextImport()
      {
         InitializeComponent();

         wizard.AddPage(new ucTextSelect());
         wizard.AddPage(new ucTextProgress());
      }

      private void wizard_OnCancel(object sender, EventArgs e)
      {
         Close();
      }

      private void formTextImport_Shown(object sender, EventArgs e)
      {
         wizard.Start();
      }

      private void wizard_PageChanged(int currentPage, int lastPage)
      {
         Text = "Import accounts from a text file - Step " + currentPage + " of " + lastPage;
      }
   }
}
