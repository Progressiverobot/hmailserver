// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Windows.Forms;
using hMailServer.Shared;

namespace ImportTool
{
   static class Program
   {
      [STAThread]
      static void Main()
      {
         ToolApplication.Initialize();

         CommandLineParser.Parse();

         hMailServer.Application application = new hMailServer.Application();
         if (!Authenticator.AuthenticateUser(application))
            return;

         Globals.SetApp(application);

         Application.Run(new formChooser());
      }
   }
}
