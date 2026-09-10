// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The base class every linked fixture inherits, with the Windows one's shape
   ///    and the REST API under it:
   ///
   ///      SetUp     the server is still the one that started the run
   ///                (ServiceRestartDetector), the crash oracle has nothing to say,
   ///                the test domain is made afresh, and the ERROR log's current end
   ///                is noted;
   ///      TearDown  the server's log is printed for a failed test; what the test
   ///                made is removed; and a test that passed while the server wrote
   ///                a new ERROR line fails on that line, because an error nobody
   ///                looked at is the case the Windows check exists for.
   ///
   ///    The Windows base deletes the log files and reads them from disk; here they
   ///    are on another machine, so the ERROR log is read through GET /api/v1/logs
   ///    and judged by what was added since SetUp rather than by whether it exists.
   /// </summary>
   public class TestFixtureBase
   {
      protected Application _application;
      protected Domain _domain;
      protected Settings _settings;

      [OneTimeSetUp]
      public void TestFixtureSetUp()
      {
         SingletonProvider<TestSetup>.Instance.Authenticate();

         _application = SingletonProvider<TestSetup>.Instance.GetApp();
         _settings = _application.Settings;
      }

      /// <summary>
      ///    The Windows one stops the service and starts it again, for a test that
      ///    needs the server to re-read something it only reads at process start -
      ///    hMailServer.ini, which IniFileSettings caches for the life of the process.
      ///    Nothing in the REST API restarts the process: POST
      ///    /api/v1/server/reinitialize restarts the services inside it and does not
      ///    re-read the INI, so it is not the same thing and is not put here in its
      ///    place. A test that asks for a restart is stopped at that point, and the
      ///    tests in the same fixture that do not ask for one still run.
      /// </summary>
      protected void RestartServerAndReacquireCom()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoServerRestart);
      }

      [SetUp]
      public void SetUp()
      {
         // First, before anything is made on the server for a test that is not
         // going to run.
         NotOnThisServer.SkipIfRegistered();

         ServiceRestartDetector.ValidateProcessId();

         CrashOracleAsserts.AssertNoMemorySafetyEvents();

         _domain = SingletonProvider<TestSetup>.Instance.PerformBasicSetup();

         LogHandler.MarkErrorLog();

         TestSetup.GetLocalIpAddress();
      }

      [TearDown]
      public void TearDown()
      {
         var testFailed = TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed;

         if (testFailed)
         {
            Console.WriteLine("hMailServer log:");
            Console.WriteLine(LogHandler.ReadCurrentDefaultLog());
            Console.WriteLine();
         }

         // Read before the cleanup, which can add lines of its own (an account deleted
         // with a message still queued is the HM5165 the Windows base class describes),
         // and before anything that could throw.
         var newErrors = LogHandler.ErrorLogLinesSinceMark();

         try
         {
            SingletonProvider<TestSetup>.Instance.RemoveWhatTheTestMade();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Console.WriteLine("Could not clean up after this test: " + ex.Message);
         }

         if (newErrors.Length > 0)
         {
            Console.WriteLine("hMailServer ERROR log lines written during this test:");
            Console.WriteLine(string.Join(Environment.NewLine, newErrors));
            Console.WriteLine();

            // Only a test that passed is failed here: a failed test's server-side
            // error is diagnostic output for that failure, not a second failure.
            if (!testFailed)
               Assert.Fail("The server wrote to its ERROR log during a test that passed:" + Environment.NewLine +
                           string.Join(Environment.NewLine, newErrors));
         }

         CrashOracleAsserts.AssertNoMemorySafetyEvents();
      }
   }
}
