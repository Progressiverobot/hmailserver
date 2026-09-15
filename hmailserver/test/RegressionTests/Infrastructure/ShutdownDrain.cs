// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises the graceful-shutdown drain (ShutdownDrainSeconds, in the
   ///    settings store): when stopping, the server gives in-flight client
   ///    sessions a bounded window to finish before tearing the listeners down.
   /// </summary>
   [TestFixture]
   public class ShutdownDrain : TestFixtureBase
   {

      private void WriteSetting(string key, string value)
      {
         IniFileSetting.Write(key, value);
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
