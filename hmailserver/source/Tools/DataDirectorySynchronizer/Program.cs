// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows.Forms;
using hMailServer.Shared;

namespace DataDirectorySynchronizer
{
   static class Program
   {
      /// <summary>
      /// The main entry point for the application.
      /// </summary>
      [STAThread]
      static void Main()
      {
         ToolApplication.Initialize();

         CommandLineParser.Parse();

         hMailServer.Application application = new hMailServer.Application();
         if (!Authenticator.AuthenticateUser(application))
            return;

         Globals.SetApp(application);

         Application.Run(new formMain());
      }
   }
}