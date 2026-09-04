// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using System.IO;

namespace PasswordDisplayer
{
   public partial class formDatabasePassword : Form
   {
      public formDatabasePassword()
      {
         InitializeComponent();
      }

      public string IniReadValue(string fileName, string Section, string Key)
      {
         return IniFile.GetValue(Section, Key, "", fileName);

      }


      private void formDatabasePassword_Load(object sender, EventArgs e)
      {
         // Locate hMailServer.ini
         RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\hMailServer");
         string installDir = key.GetValue( "InstallLocation" ) as string;
         string binDir = Paths.Combine(installDir, "Bin");
         string iniFile = Paths.Combine(binDir, "hMailServer.ini");

         // Read the database password.
         string encryptedPassword = IniReadValue(iniFile, "Database", "password");

         hMailServer.Application app = GetApp();

         string decryptedPassword = app.Utilities.BlowfishDecrypt(encryptedPassword);

         textPassword.Text = decryptedPassword;
      }

      private hMailServer.Application GetApp()
      {
         hMailServer.Application application = new hMailServer.Application();
         hMailServer.Account account = application.Authenticate("Administrator", "");
         
         if (account != null)
            return application;

         account = application.Authenticate("Administrator", "testar");

         if (account != null)
            return application;

         MessageBox.Show("Authentication failed", "Database password");

         return null;
      }

      private void buttonCopy_Click(object sender, EventArgs e)
      {
         
      }

      private void buttonCopyAndClose_Click(object sender, EventArgs e)
      {
         Clipboard.SetText(textPassword.Text);
         this.Close();
      }
   }
}
