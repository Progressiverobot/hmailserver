// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using hMailServer.Shared;

namespace DBUpdater
{
   static class Program
   {
      // Exit codes consumed by DBSetupQuick and the installer: a failed or cancelled
      // upgrade must not look like a successful install.
      private const int ExitSuccess = 0;
      private const int ExitUpgradeFailed = 1;
      private const int ExitConfigurationError = 2;
      private const int ExitAuthenticationCancelled = 3;

      /// <summary>
      /// The main entry point for the application.
      /// </summary>
      [STAThread]
      static int Main()
      {
         ToolApplication.Initialize();

         string databaseOldErrorMessage = "The database is too old for this version of hMailServer.";

         try
         {
            CommandLineParser.Parse();

            hMailServer.Application application = new hMailServer.Application();

             try
             {
                 application.Connect();
             }
             catch (Exception ex)
             {
                 if (!ex.Message.Contains(databaseOldErrorMessage))
                     throw;

             }


            int from = application.Database.CurrentVersion;
            int to = application.Database.RequiredVersion;

            if (from == to)
            {
               if (!CommandLineParser.ContainsArgument("/SilentIfOk") && !CommandLineParser.IsSilent())
                  MessageBox.Show("Your hMailServer database is already up to date.", "hMailServer Administrator");

               return ExitSuccess;
            }

            if (!Authenticator.AuthenticateUser(application))
               return ExitAuthenticationCancelled;

            formMain main = new formMain(application);

            if (!main.LoadSettings())
               return ExitConfigurationError;

            if (!main.CreateUpgradePath())
               return ExitConfigurationError;

            if (CommandLineParser.IsSilent())
            {
               // Silently perform the upgrade
               main.DoUpgrade();
               return main.UpgradeSucceeded ? ExitSuccess : ExitUpgradeFailed;
            }

            // Do it the default way.
            Application.Run(main);

            // Closing the window without a completed upgrade leaves the database on
            // the old schema; report that as a failure so callers do not carry on.
            return main.UpgradeSucceeded ? ExitSuccess : ExitUpgradeFailed;
         }
         catch (Exception ex)
         {
            Console.Error.WriteLine("hMailServer database upgrade failed: " + ex.Message);

            if (!CommandLineParser.IsSilent())
               MessageBox.Show(ex.Message + Environment.NewLine + Environment.NewLine + "Please check the hMailServer error log for further details.", "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return ExitUpgradeFailed;
         }
      }
   }
}