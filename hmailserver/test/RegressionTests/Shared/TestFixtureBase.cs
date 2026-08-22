// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;

namespace RegressionTests.Shared
{
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
      ///    Restarts the service and re-seats this fixture's cached COM objects, for a test
      ///    that needs the server to re-read something it only reads at process start -
      ///    hMailServer.ini, in practice, which IniFileSettings caches for the life of the
      ///    process.
      ///
      ///    _application and _settings are proxies to an out-of-process COM server, so a
      ///    restart disconnects them and every later call in the fixture would talk to a dead
      ///    process. Re-assigning them here is what lets such a fixture run in the ordinary
      ///    suite instead of being marked Explicit.
      ///
      ///    Whatever the test changed must be put back and the service restarted again
      ///    before the fixture ends, or every fixture after it runs against the changed
      ///    configuration.
      /// </summary>
      protected void RestartServerAndReacquireCom()
      {
         SingletonProvider<TestSetup>.Instance.RestartServiceAndReacquire();

         _application = SingletonProvider<TestSetup>.Instance.GetApp();
         _settings = _application.Settings;
      }

      [SetUp]
      public void SetUp()
      {
         ServiceRestartDetector.ValidateProcessId();

         // The crash oracle, and the line that makes it one.
         //
         // The server builds with /EHa, so catch (...) catches structured exceptions as
         // well as C++ ones: an access violation inside any try block is swallowed and
         // the run goes green. A vectored exception handler now records every
         // memory-safety fault at first chance, before any frame-based handler can
         // absorb it - but a log file nobody reads is not an oracle, so it is checked
         // here, before every test. Without this call the whole mechanism is inert and
         // 1144 passing tests continue to say nothing about a null dereference in a
         // request handler.
         //
         // Placed before PerformBasicSetup deliberately: a fault recorded by an earlier
         // test should fail the next test to run rather than be attributed to whatever
         // setup does next.
         CrashOracleAsserts.AssertNoMemorySafetyEvents();

         _domain = SingletonProvider<TestSetup>.Instance.PerformBasicSetup();

         LogHandler.DeleteCurrentDefaultLog();

         // make sure we have internet access.
         TestSetup.GetLocalIpAddress();
      }

      [TearDown]
      public void TearDown()
      {
         // Read before the assert below, because that one throws.
         var memorySafetyEvents = CrashOracleAsserts.GetMemorySafetyEvents();

         var testFailed = TestContext.CurrentContext.Result.FailCount > 0;

         if (testFailed || memorySafetyEvents.Length > 0)
         {
            Console.WriteLine("hMailServer log:");
            Console.WriteLine(LogHandler.ReadCurrentDefaultLog());
            Console.WriteLine();
         }

         // A failing test's server-side error belongs to that test and to nothing else.
         //
         // PerformBasicSetup calls AssertNoReportedError, which fails a test if the
         // server's ERROR log exists AT ALL. So an error provoked by a test that has
         // already failed goes on to fail every test after it, in every fixture, on a
         // line that has nothing to do with any of them - and the run reports hundreds of
         // failures with one cause. That is not hypothetical: on 19 August 2026 a slow
         // resolver failed one SPF test, its cleanup deleted the account while the
         // message was still queued, the server correctly reported HM5165, and the run
         // finished 995/1653 with 653 of the 658 failures quoting that single line.
         //
         // So when the test has already failed, the error log is printed WITH that
         // failure - which is where it is diagnostically useful, next to the test that
         // caused it - and then cleared. A test that PASSED is deliberately left alone,
         // so the SetUp check still catches what it exists for: an error nobody noticed,
         // because the test that provoked it never looked.
         if (testFailed)
         {
            // Wrapped, because GetErrorLogFileName is a COM call to the server: a test
            // that failed BECAUSE the server died would otherwise throw here, and an
            // exception from TearDown replaces the real failure with a confusing one.
            try
            {
               if (System.IO.File.Exists(LogHandler.GetErrorLogFileName()))
               {
                  Console.WriteLine("hMailServer ERROR log, reported against this test and then cleared");
                  Console.WriteLine("so that it cannot fail every test after it:");
                  Console.WriteLine(LogHandler.ReadErrorLog());
                  Console.WriteLine();
               }

               // Cleared until it STAYS cleared, and UNCONDITIONALLY for a failed
               // test - not only when a log already exists at this moment.
               //
               // Both halves were learned the same night. The errors a failing test
               // provokes are frequently written by a background thread (a delivery
               // worker retrying, a minidump writer finishing), so deleting the file
               // once leaves the stragglers to recreate it and fail the NEXT test.
               // And guarding the clear on the file existing HERE misses the case
               // that matters most: a test whose failure is that a delivery never
               // completed fails BEFORE the delivery thread reports it, so at this
               // instant there is nothing to clear and a second later there is. That
               // is exactly how POP3ServerNotSupportingSSL failed on HM5165 raised by
               // the test before it.
               // Drained BEFORE the log is settled, and that order is the point.
               //
               // A message left in the delivery queue is a delivery that has not
               // happened yet, and the error it eventually raises belongs to a test
               // that has not started: the next SetUp recreates the test domain,
               // which deletes its accounts, so the queued message has no recipient
               // and LocalDelivery correctly reports HM5165 - "the recipient account
               // appears to have been deleted after the message was received". No
               // amount of log-clearing here can pre-empt that, because at this
               // moment the error does not exist. Seen 19 August 2026 as
               // API.Events.TestOnDeliveryStart_SetHtmlBodyEmpty failing on an
               // HM5165 caused by the test before it.
               //
               // AntiVirus.ScannerFailurePolicy has done this in its own teardown
               // for the same reason since it was written, which is the argument for
               // doing it here rather than once per fixture that happens to notice.
               //
               // Failed tests only, like the log: a test that PASSED and still left
               // mail queued is a different defect, and hiding it here would remove
               // the only signal that it exists.
               _application.GlobalObjects.DeliveryQueue.Clear();

               LogHandler.ClearErrorLogUntilSettled();
            }
            catch (Exception ex)
            {
               Console.WriteLine("Could not clean up after this test's failure: " + ex.Message);
            }
         }

         // Checked here as well as in SetUp, and this is the check that attributes the
         // fault correctly: a fault provoked by this test fails this test, rather than
         // whichever test happened to run next. The SetUp check stays as the backstop
         // for a fault raised on a background thread between two tests, and for the
         // fault raised by the last test of a fixture whose own TearDown cleared it.
         CrashOracleAsserts.AssertNoMemorySafetyEvents();
      }
   }
}