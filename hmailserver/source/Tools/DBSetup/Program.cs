// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using hMailServer.Shared;

namespace DBSetup
{
   static class Program
   {
      /// <summary>
      /// The main entry point for the application.
      /// </summary>
      [STAThread]
      static void Main()
      {
         Application.EnableVisualStyles();
         Application.SetCompatibleTextRenderingDefault(false);

         CommandLineParser.Parse();
         
         hMailServer.Application application = new hMailServer.Application();
         if (!Authenticator.AuthenticateUser(application))
            return;

         Globals.SetApp(application);

         Application.Run(new formMain());
      }
   }
}