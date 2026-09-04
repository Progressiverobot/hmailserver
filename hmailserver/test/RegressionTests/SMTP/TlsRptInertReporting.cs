// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    SMTP TLS reporting (RFC 8460) is inert out of the box:
   ///    TlsRptReporterTask returns without sending anything while
   ///    TlsRptFromAddress is empty, which it is in the shipped configuration,
   ///    so every completed day of collected statistics is popped and
   ///    discarded. Not mailing third parties by default is the right default,
   ///    but it used to be entirely silent - an administrator who had published
   ///    a _smtp._tls record and was waiting for reports had no way to find out
   ///    from the server that none would ever be sent.
   ///
   ///    Two properties are pinned here, and the second matters as much as the
   ///    first. The notice must exist, and it must be an APPLICATION log entry
   ///    and not a reported error: an empty TlsRptFromAddress is the default, so
   ///    an ErrorManager entry would appear in the ERROR log of every stock
   ///    install, tell administrators a working server is broken, and fail the
   ///    clean-error-log assertion that every fixture's setup makes.
   ///
   ///    Both fail against the current code: nothing at all is written, so the
   ///    default log does not mention TlsRptFromAddress.
   /// </summary>
   [TestFixture]
   public class TlsRptInertReporting : TestFixtureBase
   {

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      [Test]
      [Description("A server with no TlsRptFromAddress says so once at startup, in the application log and not as a reported error")]
      public void InertTlsReportingIsAnnouncedAtStartup()
      {
         try
         {
            // The shipped default, made explicit: an earlier fixture could have
            // left a sender address behind.
            WriteSetting("TlsRptFromAddress", "");

            LogHandler.DeleteCurrentDefaultLog();
            LogHandler.DeleteErrorLog();

            // The notice is written where the scheduled task is constructed,
            // which is once per StartServers - so a reinitialization is a
            // startup for this purpose.
            _application.Reinitialize();

            string defaultLog = LogHandler.ReadCurrentDefaultLog();

            Assert.IsTrue(defaultLog.Contains("TlsRptFromAddress"),
               "Startup should name the setting that leaves TLS reporting inert. Log: " + defaultLog);

            // Said once, not once per hourly run: the notice lives in the task
            // constructor precisely so it cannot become a recurring entry. The
            // needle carries the setting name because the shorter phrase it
            // used to count ("will never send a report") is deliberately shared
            // with the DMARC reporter's sibling notice - two inert reporters
            // announce themselves per start, and this test counts only its own.
            Assert.AreEqual(1,
               defaultLog.Split(new[] { "will never send a report: TlsRptFromAddress" }, System.StringSplitOptions.None).Length - 1,
               "The notice should appear exactly once per start. Log: " + defaultLog);

            // The critical half. This is the default configuration, so it must
            // not reach the ERROR log.
            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            LogHandler.DeleteErrorLog();
         }
      }
   }
}
