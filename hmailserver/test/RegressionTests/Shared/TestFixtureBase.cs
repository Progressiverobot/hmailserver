// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

         if (TestContext.CurrentContext.Result.FailCount > 0 || memorySafetyEvents.Length > 0)
         {
            Console.WriteLine("hMailServer log:");
            Console.WriteLine(LogHandler.ReadCurrentDefaultLog());
            Console.WriteLine();
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