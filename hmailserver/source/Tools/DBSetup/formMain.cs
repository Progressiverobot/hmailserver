// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using DBSetup.Pages;

namespace DBSetup
{
   public partial class formMain : Form
   {
      /// <summary>
      /// True once the wizard reached its final page. A page only advances when
      /// IWizardPage.OnLeavePage succeeds, so reaching the last page means the
      /// create/update task actually completed. Program.Main turns this into the
      /// process exit code the installer checks.
      /// </summary>
      public bool SetupCompleted { get; private set; }

      public formMain()
      {
         InitializeComponent();

         this.Cursor = Cursors.Default;

         wizard.AddPage(new ucWelcome());
         wizard.AddPage(new ucAction());
         wizard.AddPage(new ucSelectDatabaseType());
         wizard.AddPage(new ucDBConnectionInfo());
         wizard.AddPage(new ucServiceDependency());
         wizard.AddPage(new ucPerformTask());
         wizard.AddPage(new ucCompleted());
      }

      private void wizard_OnCancel(object sender, EventArgs e)
      {
         this.Close();
      }

      private void formMain_Shown(object sender, EventArgs e)
      {
         wizard.Start();
      }

      private void wizard_PageChanged(int currentPage, int lastPage)
      {
         this.Text = "hMailServer Database Setup - Step " + currentPage + " of " + lastPage;

         if (currentPage == lastPage)
            SetupCompleted = true;
      }
   }
}