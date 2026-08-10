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

         // The shared wizard auto-runs all pages when /silent is passed. The
         // import wizards have no scripted-argument support, so a silent run
         // could only fail (or import with empty selections); refuse it.
         if (CommandLineParser.IsSilent())
         {
            MessageBox.Show("The hMailServer Import Tool does not support silent mode.", "hMailServer Import Tool");
            return;
         }

         hMailServer.Application application = new hMailServer.Application();
         if (!Authenticator.AuthenticateUser(application))
            return;

         Globals.SetApp(application);

         Application.Run(new formChooser());
      }
   }
}
