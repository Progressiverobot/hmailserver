// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using hMailServer.Shared;


namespace DBSetup.Pages
{
   public partial class ucDBConnectionInfo : UserControl, IWizardPage
   {
      private Dictionary<string, string> _state;

      public ucDBConnectionInfo()
      {
         InitializeComponent();
      }

      public string Title
      {
         get
         {
            return "Enter server connection information";
         }
      }

      public void OnShowPage(Dictionary<string, string> state)
      {
         _state = state;

         if (_state.TryGetValue("ServerAddress", out string serverAddress))
            textServerAddress.Text = serverAddress;
         if (_state.TryGetValue("ServerPort", out string serverPort))
            textServerPort.Text = serverPort;
         if (_state.TryGetValue("DatabaseName", out string databaseName))
            textDatabaseName.Text = databaseName;

         if (_state.TryGetValue("Authentication", out string authentication))
         {
            if (authentication == "Server")
               radioUseServerAuthentication.Checked = true;
            else if (authentication == "Windows")
               radioUseWindowsAuthentication.Checked = true;
         }

         if (_state.TryGetValue("Username", out string username))
            textUsername.Text = username;

         if (_state.TryGetValue("Password", out string password))
            textPassword.Text = password;

         hMailServer.eDBtype dbType = Globals.GetDatabaseType(_state["ServerType"]);
         switch (dbType)
         {
            case hMailServer.eDBtype.hDBTypeMySQL:
               radioUseWindowsAuthentication.Enabled = false;
               radioUseServerAuthentication.Checked = true;
               break;
            case hMailServer.eDBtype.hDBTypePostgreSQL:
               radioUseWindowsAuthentication.Enabled = false;
               radioUseServerAuthentication.Checked = true;
               break;
            case hMailServer.eDBtype.hDBTypeMSSQL:
               radioUseWindowsAuthentication.Enabled = true;
               textServerPort.Text = "";
               break;
         }

         if (textServerPort.Text.Length == 0 || 
             textServerPort.Number == 3306 ||
             textServerPort.Number == 5432)
         {
            switch (dbType)
            {
               case hMailServer.eDBtype.hDBTypeMySQL:
                  textServerPort.Number = 3306;
                  break;
               case hMailServer.eDBtype.hDBTypePostgreSQL:
                  textServerPort.Number = 5432;
                  break;
            }
         }

         EnableDisable();
      }

      public bool OnLeavePage(bool next)
      {
         if (next)
         {
            if (textDatabaseName.Text.Length == 0)
            {
               MessageBox.Show("You must enter the name of the database to create.", "hMailServer");
               return false;
            }

            if (textServerAddress.Text.Length == 0)
            {
               MessageBox.Show("You must enter the name of the database server to connect to.", "hMailServer");
               return false;
            }
         }

         _state["ServerAddress"] = textServerAddress.Text;
         _state["ServerPort"] = textServerPort.Text;
         _state["DatabaseName"] = textDatabaseName.Text;
         
         if (radioUseServerAuthentication.Checked)
            _state["Authentication"] = "Server";
         else
            _state["Authentication"] = "Windows";

         _state["Username"] = textUsername.Text;
         _state["Password"] = textPassword.Text;

         return true;
      }

      private void EnableDisable()
      {
         textUsername.Enabled = radioUseServerAuthentication.Checked;
         textPassword.Enabled = radioUseServerAuthentication.Checked;

         hMailServer.eDBtype dbType = Globals.GetDatabaseType(_state["ServerType"]);
         textServerPort.Enabled = dbType != hMailServer.eDBtype.hDBTypeMSSQL;
      }

      private void radioUseServerAuthentication_CheckedChanged(object sender, EventArgs e)
      {
         EnableDisable();
      }

      private void radioUseWindowsAuthentication_CheckedChanged(object sender, EventArgs e)
      {
         EnableDisable();
      }

   }
}
