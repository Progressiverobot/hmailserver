// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Diagnostics;
using hMailServer.Shared;

namespace DBSetupQuick
{
   static class Program
   {
      private static hMailServer.Application _application;

      [STAThread]
      static int Main()
      {
         ToolApplication.Initialize();

         CommandLineParser.Parse();

         _application = new hMailServer.Application();

         // Propagate the outcome to the installer: a failed database create/upgrade
         // must fail the install rather than silently produce a broken server.
         return _application.Database.DatabaseExists ? UpgradeDatabase() : CreateDatabase();
      }

      private static int UpgradeDatabase()
      {
         try
         {
            // Database upgrader. Resolve relative to this executable rather than
            // the working directory, which the installer leaves unspecified.
            System.Diagnostics.ProcessStartInfo upgradeProcess = new System.Diagnostics.ProcessStartInfo();
            upgradeProcess.FileName = System.IO.Path.Combine(AppContext.BaseDirectory, "DBUpdater.exe");

            // Means that it should automatically exit if already up to date. This is always
            // the case when we launch it via 'quick'.
            string arguments = "/SilentIfOk";

            // If the /silent param has been supplied to this process, we should forward it to the updater
            if (CommandLineParser.IsSilent())
               arguments += " /silent";

            // Forward the administrator password as well. The create path has always
            // done this; the upgrade path did not, and that asymmetry is what made an
            // unattended upgrade hang. DBUpdater authenticates before it can upgrade,
            // had no password to authenticate with, and fell through to a modal dialog
            // nobody was there to answer.
            if (CommandLineParser.ContainsArgument("password"))
               arguments += " password:" + CommandLineParser.GetArgument("password");

            upgradeProcess.Arguments = arguments;

            // Launch upgrader and wait for it to complete.
            Process p = Process.Start(upgradeProcess);
            p.WaitForExit();

            return p.ExitCode;
         }
         catch (Exception ex)
         {
            Console.Error.WriteLine("Failed to start DBUpdater.exe: " + ex.Message);

            if (!CommandLineParser.IsSilent())
               MessageBox.Show("Failed to start DBUpdater.exe" + Environment.NewLine + ex.Message, "hMailServer");

            return 1;
         }
      }

      private static int CreateDatabase()
      {
         string adminPassword = string.Empty;

         if (CommandLineParser.ContainsArgument("password"))
            adminPassword = CommandLineParser.GetArgument("password");

         if (!Authenticator.AuthenticateUser(_application, adminPassword))
            return 3;

         if (_application.Database.DatabaseType == hMailServer.eDBtype.hDBTypeMSSQLCE ||
             _application.Database.DatabaseType == hMailServer.eDBtype.hDBTypeUnknown)
         {
            return InitializeInternalDatabase();
         }

         return 0;
      }

      private static int InitializeInternalDatabase()
      {
         try
         {
            hMailServer.Database database = _application.Database;

            database.CreateInternalDatabase();

            // Database has been upgraded. Reinitialize the connections.
            _application.Reinitialize();

            // Re-initialize to connect to the newly created database.
            _application.Reinitialize();

            return 0;
         }
         catch (Exception ex)
         {
            Console.Error.WriteLine("Failed to create the hMailServer database: " + ex.Message);

            if (!CommandLineParser.IsSilent())
               MessageBox.Show(ex.Message, "hMailServer", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return 1;
         }
      }

   }
}