// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Windows.Forms;
using System.Linq;

namespace hMailServer.Shared
{
   public static class Authenticator
   {
      public static bool AuthenticateUser(hMailServer.Application application, string password)
      {
         hMailServer.Account account = application.Authenticate("Administrator", password);

         if (account != null)
            return true;

         return false;
      }

      public static bool AuthenticateUser(hMailServer.Application application)
      {
         // First try to authenticate using an empty password.
         if (AuthenticateUser(application, ""))
            return true;

         // Then the password the installer forwarded, if it forwarded one. This is
         // the path an unattended UPGRADE takes, and it needs its own case: the raw
         // argument sweep below cannot serve it, because the installer passes the
         // password as "password:VALUE", so the raw argument is that whole string
         // and never the value on its own.
         if (CommandLineParser.ContainsArgument("password") &&
             AuthenticateUser(application, CommandLineParser.GetArgument("password")))
            return true;

         // Try to authenticate using password on command line...
         string [] args = Environment.GetCommandLineArgs();
         if (args.Any(password => AuthenticateUser(application, password)))
            return true;

         // Under /silent there is nobody at the keyboard, so a modal dialog is not a
         // prompt - it is a hang, and it hangs the installer waiting on this process
         // rather than this process alone. Failing here lets the caller return an exit
         // code the installer reports out loud. A visible error beats an invisible wait.
         if (CommandLineParser.IsSilent())
            return false;

         while (true)
         {
            formEnterPassword passwordDlg = new formEnterPassword();

            if (passwordDlg.ShowDialog() != System.Windows.Forms.DialogResult.OK)
               return false;

            string password = passwordDlg.Password;

            if (AuthenticateUser(application, password))
               return true;

            MessageBox.Show("Invalid user name or password.", "hMailServer");
         }
      }
   }
}
