// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
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
   ///                looked at is the case the Windows check exists for - save for
   ///                the lines this port is already known to write, which
   ///                WithoutKnownPlatformGaps names one by one.
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
      ///    wrote hMailServer.ini and needs the server to read it. Here it is POST
      ///    /api/v1/server/reinitialize, which is the same thing for that purpose:
      ///    Application::Reinitialize runs InitInstance again, whose
      ///    IniFileSettings::LoadSettings reads every setting from the file afresh -
      ///    the POSIX profile reader keeps no cache - which is why the Windows
      ///    fixtures that write the ini and then call Reinitialize (RestApiSettings,
      ///    ClientDiscovery) see what they wrote. What a reinitialise does not do is
      ///    start the process again; a fixture that needs that fails here, visibly,
      ///    rather than being skipped, and is registered by name if the need is real.
      ///    The process is the same afterwards, so ServiceRestartDetector's counter
      ///    carries on and nothing is re-baselined.
      /// </summary>
      protected void RestartServerAndReacquireCom()
      {
         _application.Reinitialize();

         _application = SingletonProvider<TestSetup>.Instance.GetApp();
         _settings = _application.Settings;
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
         LogHandler.MarkDefaultLog();
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
         var newErrors = WithoutKnownPlatformGaps(LogHandler.ErrorLogLinesSinceMark());

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

      /// <summary>
      ///    Drops the ERROR lines this build writes because of a gap in the port itself,
      ///    rather than because of anything a test did. Such a line is the platform
      ///    announcing a limitation that is already known and already on the roadmap; it
      ///    would otherwise fail every passing test that happens to send a message.
      ///
      ///    HM6406 is the only one so far: the SPF implementation resolves names through
      ///    the Windows DNS client, which this platform does not have, so every lookup is
      ///    refused and every SPF check returns TempError. RMSPF.cpp reports it under a
      ///    std::call_once, so it is written once for the life of the server process and
      ///    does not accumulate - but the one test that happens to be running when it
      ///    fires is failed by it, and which test that is depends on nothing but when the
      ///    server was last restarted. Measured: it was written at 03:48 on 10 September
      ///    2026 by a server that had started at 03:29, and it failed
      ///    AWStatsLoggingTests.SuccessfulDeliveriesShouldBeLogged, whose own assertions
      ///    had all passed. Note that an SPF lookup happens here even though use_spf is
      ///    false: SpamTestDMARC calls SPF::Test as part of a DMARC evaluation, and DMARC
      ///    is on.
      ///
      ///    Matched on the code and on the sentence that names the cause, so that an
      ///    HM6406 raised for some other reason still fails the test.
      /// </summary>
      private static string[] WithoutKnownPlatformGaps(string[] errorLines)
      {
         return errorLines
            .Where(line => !(line.Contains("HM6406") && line.Contains("resolves names through the Windows DNS client")))
            .ToArray();
      }
   }
}
