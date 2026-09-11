// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Issue #158: an installation moved to another directory ran with half of
   ///    its paths pointing at the old tree, and nothing in the product would say
   ///    which half. Three things changed. The Diagnostics self-test has an
   ///    "Installation paths" section that lists every configured directory and
   ///    file with whether it exists; a relative [Directories] value is resolved
   ///    against the program folder, so a new installation can be written to be
   ///    movable; and setting the program folder over COM leaves the running server
   ///    with the same normalised value a restart would read.
   /// </summary>
   [TestFixture]
   public class InstallationPaths : TestFixtureBase
   {
      private const string DiagnosticName = "Installation paths";

      private static string RunInstallationPathsDiagnostic(Application application, out bool success)
      {
         var diagnostics = application.Diagnostics;
         var results = diagnostics.PerformTests();

         for (int i = 0; i < results.Count; i++)
         {
            var result = results.get_Item(i);

            if (result.Name == DiagnosticName)
            {
               success = result.Result;
               return result.ExecutionDetails;
            }
         }

         Assert.Fail("The diagnostics did not include a result named '" + DiagnosticName + "'.");
         success = false;
         return string.Empty;
      }

      [Test]
      public void TheInstallationPathsDiagnosticListsEveryConfiguredDirectoryAndWhetherItExists()
      {
         string details = RunInstallationPathsDiagnostic(_application, out bool success);

         var directories = _settings.Directories;

         StringAssert.Contains("hMailServer.ini", details, details);
         StringAssert.Contains("ProgramFolder: " + directories.ProgramDirectory, details, details);
         StringAssert.Contains("DataFolder: " + directories.DataDirectory, details, details);
         StringAssert.Contains("LogFolder: " + directories.LogDirectory, details, details);
         StringAssert.Contains("TempFolder: " + directories.TempDirectory, details, details);
         StringAssert.Contains("DBScripts: ", details, details);
         StringAssert.Contains("[Settings]", details, details);
         StringAssert.Contains("PostgreSQLSslRootCert: ", details, details);

         // Languages is a directory this platform genuinely uses - it is the
         // Control Panel's message catalogues, and the Control Panel reads them
         // through the COM API - so here it is reported with a path and it
         // counts towards the result below. A POSIX build reports the same label
         // with "not used on this platform" and does not count it, because the
         // only reader of what Languages::Load loads is COM/InterfaceLanguages.cpp
         // and that file is in no Linux build; before that change the diagnostic
         // could not pass on a correctly installed Linux package. This assertion
         // is what would catch the POSIX arm being applied to Windows by mistake.
         StringAssert.Contains("Languages: " + directories.ProgramDirectory + "Languages   [exists]", details, details);

         // The Control Deck page the REST listener serves at GET /, reported
         // whether or not it is installed and never counted: a server without it
         // still answers /api/v1/. It is here because a Deck that shows the
         // built-in "not installed" stub is explained by this one line and by
         // nothing else, on either platform.
         StringAssert.Contains("WebAdmin page: " + directories.ProgramDirectory + "WebAdmin\\index.html", details, details);

         // The directories the server cannot run without are marked as present,
         // which is the check that the existence test looks at the right thing.
         StringAssert.Contains("ProgramFolder: " + directories.ProgramDirectory + "   [exists]", details, details);
         StringAssert.Contains("DataFolder: " + directories.DataDirectory + "   [exists]", details, details);

         Assert.IsTrue(success, "Every configured path on the test machine should exist. The report:\r\n" + details);
      }

      [Test]
      public void SettingTheProgramFolderOverComNormalisesItTheWayARestartWould()
      {
         var directories = _settings.Directories;
         string original = directories.ProgramDirectory;

         StringAssert.EndsWith("\\", original, "The program folder is read with a trailing separator.");

         // Written without the separator, read back with it: what LoadSettings
         // does on the next start, done now, so DBScripts and the language
         // directory are derived from the same value both before and after.
         directories.ProgramDirectory = original.TrimEnd('\\');

         Assert.AreEqual(original, directories.ProgramDirectory);
      }

      [Test]
      public void ARelativeDirectoryValueIsResolvedAgainstTheProgramFolder()
      {
         string originalEventFolder = IniFileSetting.Read("Directories", "EventFolder");
         string programDirectory = _settings.Directories.ProgramDirectory;
         string expected = Paths.Combine(programDirectory, "EventScriptsProbe");

         try
         {
            // EventFolder, because it is the one directory a relative value can be
            // put in without the rest of the suite noticing: it is only read when an
            // event script runs, and no event script is configured here.
            IniFileSetting.Write("Directories", "EventFolder", "EventScriptsProbe");
            RestartServerAndReacquireCom();

            Assert.AreEqual(expected, _settings.Directories.EventDirectory,
               "A relative EventFolder must be resolved against ProgramFolder.");

            // And the diagnostic says so, and says the directory is not there,
            // because it is not.
            string details = RunInstallationPathsDiagnostic(_application, out bool success);
            StringAssert.Contains("EventFolder: " + expected + "   [MISSING]", details, details);
            Assert.IsFalse(success, "A configured directory that does not exist must fail the diagnostic.\r\n" + details);
         }
         finally
         {
            if (string.IsNullOrEmpty(originalEventFolder))
               IniFileSetting.Delete("Directories", "EventFolder");
            else
               IniFileSetting.Write("Directories", "EventFolder", originalEventFolder);

            RestartServerAndReacquireCom();
         }

         Assert.AreEqual(originalEventFolder, _settings.Directories.EventDirectory,
            "The original EventFolder must be back before the next fixture runs.");
      }
   }
}
