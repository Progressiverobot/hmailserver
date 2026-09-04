// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ConfigureInstallation
{
   static class Program
   {

      static int Main(string []args)
      {
         if (args.Count() != 3)
         {
            Console.WriteLine("Expected three parameters: RootDir, Version, Build");
            return -1;
         }

         var rootDir = args[0];
         var version = args[1];
         var build = args[2];

         var versionParts = version.Split('.');

         var versionMajor = versionParts[0];
         var versionMinor = versionParts[1];
         var versionPatch = versionParts[2];

         if (!Directory.Exists(rootDir))
         {
            Console.WriteLine("Root directory {0} was not found.", rootDir);
            return -1;
         }

         // Write C++ version header file.
         string cppVersionFile = Paths.Combine(rootDir, @"hmailserver\source\Server\Common\Application\Version.h");
         if (!File.Exists(cppVersionFile))
         {
            Console.WriteLine("Version file {0} was not found.", cppVersionFile);
            return -1;
         }

         var numericVersion = string.Format("{0},{1},{2},{3}", versionMajor, versionMinor, versionPatch, build);

         Console.WriteLine("Writing c++ version info to {0}", cppVersionFile);
         // The same shape the file has in the repository: the licence header the
         // SPDX check requires, then the three defines at column one.
         var versionContent = string.Format(
            "// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors\n" +
            "// SPDX-License-Identifier: AGPL-3.0-or-later\n" +
            "\n" +
            "#pragma once\n" +
            "#define HMAILSERVER_VERSION \"{0}\"\n" +
            "#define HMAILSERVER_VERSION_NUMERIC {1}\n" +
            "#define HMAILSERVER_BUILD {2}\n", version, numericVersion, build);
         File.WriteAllText(cppVersionFile, versionContent);

         // Write C++ version header file.
         string phpVersionFile = Paths.Combine(rootDir, @"hmailserver\source\WebAdmin\include_versioncheck.php");

         Console.WriteLine("Writing php version info to {0}", phpVersionFile);
         var phpVersionContent = string.Format("<?php\r\ndefine('REQUIRED_VERSION', '{0}-B{1}');\r\n?>", version, build);
         File.WriteAllText(phpVersionFile, phpVersionContent);

         // Write installation program verison
         if (!ConfigureInstallationFile(Paths.Combine(rootDir, @"hmailserver\Installation\section_setup_64.iss"), version, build, true))
            return -1;

         Console.WriteLine("All done. Exiting.");
         return 0;
      }

      private static bool ConfigureInstallationFile(string installationFile, string version, string build, bool x64)
      {
         if (!File.Exists(installationFile))
         {
            Console.WriteLine("Installation file {0} was not found.", installationFile);
            return false;
         }

         Console.WriteLine("Writing install version and output name to {0}", installationFile);

         if (x64)
         {
            Ini.Write(installationFile, "Setup", "AppVerName", string.Format("hMailServer {0}-B{1}-x64", version, build));
            Ini.Write(installationFile, "Setup", "OutputBaseFilename", string.Format("hMailServer-{0}-B{1}-x64", version, build));            
         }
         else
         {
            Ini.Write(installationFile, "Setup", "AppVerName", string.Format("hMailServer {0}-B{1}-x86", version, build));
            Ini.Write(installationFile, "Setup", "OutputBaseFilename", string.Format("hMailServer-{0}-B{1}-x86", version, build));
         }

         // The build number travels in Version.h and the release tag; the installer's
         // version fields carry the version alone, so a build-only re-cut never has
         // to touch this file (RELEASE.md, step 7).
         Ini.Write(installationFile, "Setup", "AppVersion", version);
         Ini.Write(installationFile, "Setup", "VersionInfoVersion", version + ".0");

         return true;
      }
   }
}
