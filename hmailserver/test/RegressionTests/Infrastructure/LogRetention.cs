// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Verifies the operability log-retention task (hMailServer.ini [Settings]
   ///    LogDeleteDays): on startup the server prunes its own date-stamped log
   ///    files older than the configured window while keeping recent ones.
   /// </summary>
   [TestFixture]
   public class LogRetention : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      [Test]
      [Description("LogDeleteDays prunes old date-stamped log files on startup, keeping recent ones.")]
      public void TestLogRetentionDeletesOldFiles()
      {
         string logDirectory = _application.Settings.Directories.LogDirectory;
         Assert.IsTrue(Directory.Exists(logDirectory), "Log directory does not exist: " + logDirectory);

         string oldFile = Path.Combine(logDirectory, "hmailserver_retention_old_test.log");
         string newFile = Path.Combine(logDirectory, "hmailserver_retention_new_test.log");

         File.WriteAllText(oldFile, "stale log content\r\n");
         File.WriteAllText(newFile, "fresh log content\r\n");
         File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));
         File.SetLastWriteTime(newFile, DateTime.Now);

         try
         {
            // Enable retention with a one-day window and reinitialize so the
            // startup retention pass runs (and the new LogDeleteDays is read).
            WriteSetting("LogDeleteDays", "1");
            _application.Reinitialize();

            // The startup retention task runs asynchronously on the maintenance
            // work queue; poll briefly for the stale file to disappear.
            bool oldDeleted = false;
            for (int attempt = 0; attempt < 50; attempt++)
            {
               if (!File.Exists(oldFile))
               {
                  oldDeleted = true;
                  break;
               }
               System.Threading.Thread.Sleep(200);
            }

            Assert.IsTrue(oldDeleted, "Log retention should have deleted the 10-day-old log file.");
            Assert.IsTrue(File.Exists(newFile), "Log retention should have kept the recent log file.");
         }
         finally
         {
            WriteSetting("LogDeleteDays", "0");
            if (File.Exists(oldFile))
               File.Delete(oldFile);
            if (File.Exists(newFile))
               File.Delete(newFile);
            _application.Reinitialize();
         }
      }
   }
}
