// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises the graceful-shutdown drain (hMailServer.ini [Settings]
   ///    ShutdownDrainSeconds): when stopping, the server gives in-flight client
   ///    sessions a bounded window to finish before tearing the listeners down.
   /// </summary>
   [TestFixture]
   public class ShutdownDrain : TestFixtureBase
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
      [Description("Graceful shutdown waits for active sessions up to ShutdownDrainSeconds, but returns promptly when idle.")]
      public void TestGracefulShutdownDrainWaitsForActiveSessions()
      {
         const int drainSeconds = 3;

         WriteSetting("ShutdownDrainSeconds", drainSeconds.ToString());

         // Reinitialize (not just Stop/Start) so the new ShutdownDrainSeconds is read:
         // the value is cached in IniFileSettings, only re-read by InitInstance().
         _application.Reinitialize();

         try
         {
            // Baseline: with no active client sessions, Stop() must not wait the drain window.
            Stopwatch idleTimer = Stopwatch.StartNew();
            _application.Stop();
            idleTimer.Stop();
            _application.Start();
            Assert.Less(idleTimer.ElapsedMilliseconds, 1500,
               "Stop() with no active sessions should not wait for the drain window.");

            // With an active IMAP session held open, Stop() should block for ~the full window
            // (the session never closes on its own, so the drain runs to the deadline).
            ImapClientSimulator imap = new ImapClientSimulator();
            imap.Connect(); // TCP connect + server banner => counted as an active IMAP session.

            try
            {
               Stopwatch drainTimer = Stopwatch.StartNew();
               _application.Stop();
               drainTimer.Stop();

               Assert.GreaterOrEqual(drainTimer.ElapsedMilliseconds, (drainSeconds * 1000) - 700,
                  "Stop() should wait for the drain window while a session is active.");
            }
            finally
            {
               imap.Disconnect();
               _application.Start();
            }
         }
         finally
         {
            WriteSetting("ShutdownDrainSeconds", "0");
            _application.Reinitialize();
         }
      }
   }
}
