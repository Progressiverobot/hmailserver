// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Windows.Forms;

namespace ImportTool.MboxImport
{
   public partial class formMboxImport : Form
   {
      public formMboxImport()
      {
         InitializeComponent();

         wizard.AddPage(new ucMboxSelect());
         wizard.AddPage(new ucMboxAccount());
         wizard.AddPage(new ucMboxProgress());
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
