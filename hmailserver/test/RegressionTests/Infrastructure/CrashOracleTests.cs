// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.IO;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The crash oracle: the thing that makes a memory-safety fault visible to the
   ///    test runner.
   ///
   ///    Why this is needed at all. The project is built with
   ///    &lt;ExceptionHandling&gt;Async&lt;/ExceptionHandling&gt; (/EHa) at both
   ///    configurations, and under /EHa a catch (...) catches structured exceptions
   ///    as well as C++ ones. So an access violation inside any try block can be
   ///    caught, reported as an ordinary failure, and shrugged off - and there are
   ///    over 1300 catch (...) sites in the server. Before this change there was no
   ///    set_terminate, no SetUnhandledExceptionFilter and no _set_se_translator
   ///    anywhere in hmailserver/source/Server, so the only memory-safety faults the
   ///    suite could see were the ones that happened to reach the single
   ///    __try/__except in ExceptionHandler::Run. Everything else was invisible: a
   ///    green run said nothing about memory safety.
   ///
   ///    What the oracle adds is a vectored exception handler. Windows delivers
   ///    every exception to vectored handlers at first chance, before it starts
   ///    looking for a frame-based handler, so it sees the fault whether or not a
   ///    catch (...) goes on to hide it. Each one is appended to
   ///    &lt;LogDirectory&gt;\crash-oracle.log, which is what these tests read and
   ///    what CrashOracleAsserts.AssertNoMemorySafetyEvents() is meant to check
   ///    before every fixture.
   ///
   ///    These tests provoke the fault the only way the suite can from outside: the
   ///    existing crash-simulation hook (hMailServer.ini CrashSimulationMode, read in
   ///    SMTPConnection::ProtocolHELP_), where mode 3 dereferences a null pointer and
   ///    mode 2 throws an ordinary C++ exception. That gives both a positive and a
   ///    negative control for the observer's filter.
   ///
   ///    Each test cleans up after itself: the simulated faults produce real
   ///    minidumps and real error-log entries, and PerformBasicSetup calls
   ///    AssertNoReportedError, so anything left behind would fail an unrelated
   ///    fixture.
   /// </summary>
   [TestFixture]
   public class CrashOracleTests : TestFixtureBase
   {
      private string[] GetMinidumps()
      {
         var logDirectory = _settings.Directories.LogDirectory;

         return Directory.GetFiles(logDirectory, "minidump*.dmp");
      }

      private void DeleteAllMinidumps()
      {
         foreach (var minidump in GetMinidumps())
            CustomAsserts.AssertDeleteFile(minidump);
      }

      // Same provocation as ExceptionHandlerTests: HELP is the command the crash
      // simulation hook sits in.
      private static void TriggerCrashSimulationError()
      {
         using (var tcpConnection = new TcpConnection())
         {
            tcpConnection.Connect(25);
            tcpConnection.Send("help\r\n");
            tcpConnection.Receive();
         }
      }

      // Everything the simulated crashes leave behind, in one place, so it can be
      // called from a finally without hiding the real failure.
      private void CleanUpAfterSimulatedCrash(string expectedErrorContent)
      {
         _settings.CrashSimulationMode = 0;

         try
         {
            CustomAsserts.AssertReportedError(expectedErrorContent);
         }
         finally
         {
            // Settled rather than deleted once. AssertReportedError above returns as soon
            // as the entry it was told to look for is present, and a crash simulation
            // writes several from different paths - the last of them only after an
            // out-of-process minidump writer finishes. A single delete at that moment
            // leaves the stragglers to recreate the file, and then every fixture that runs
            // after this one fails in setup on an error this test caused deliberately.
            LogHandler.ClearErrorLogUntilSettled();
            DeleteAllMinidumps();
            CrashOracleAsserts.Clear();
         }
      }

      [Test]
      [Description("An access violation is recorded where the test runner can see it")]
      public void AccessViolationIsRecordedByTheCrashOracle()
      {
         // Fails today because nothing writes crash-oracle.log: the server has no
         // first-chance observer at all, so ReadRecords finds no file and no records.
         CrashOracleAsserts.Clear();
         DeleteAllMinidumps();

         try
         {
            _settings.CrashSimulationMode = 3;

            TriggerCrashSimulationError();

            var record = CrashOracleAsserts.AssertRecordWritten(CrashOracleAsserts.FirstChance, "code=0xC0000005");

            // The record has to identify the fault well enough to be worth having:
            // which process, which thread, and where. Without the address the record
            // cannot be lined up with the minidump written beside it.
            Assert.IsTrue(record.Contains("address=0x"), record);
            Assert.IsTrue(record.Contains("thread="), record);
            Assert.IsTrue(record.Contains("pid="), record);
         }
         finally
         {
            CleanUpAfterSimulatedCrash("An error has been detected. A mini dump has been written");
         }
      }

      [Test]
      [Description("An ordinary C++ exception is not recorded as a memory-safety fault")]
      public void OnlyMemorySafetyFaultsAreRecorded()
      {
         // The negative control, and the reason it is in the same test as the
         // positive one: on its own, "no records were written" passes trivially
         // against today's code, where nothing is ever written. Pairing them means
         // the test can only pass if the observer is both present and selective.
         CrashOracleAsserts.Clear();
         DeleteAllMinidumps();

         try
         {
            // Mode 2 throws std::logic_error. That is a C++ throw, which reaches the
            // kernel as SEH code 0xE06D7363 - an application-defined code, not a
            // memory-safety fault. The observer must ignore it; if it did not, every
            // DisconnectedException in a normal run would be recorded as a crash and
            // the oracle would be worthless.
            _settings.CrashSimulationMode = 2;

            TriggerCrashSimulationError();

            CustomAsserts.AssertReportedError("Message: Crash simulation test");

            var records = CrashOracleAsserts.ReadRecords(null);
            Assert.AreEqual(0, records.Length,
               "A C++ exception was recorded as a memory-safety fault: " + string.Join(" | ", records));

            // Now the positive half, in the same fixture and against the same file:
            // an actual null dereference must be recorded.
            _settings.CrashSimulationMode = 3;

            TriggerCrashSimulationError();

            CrashOracleAsserts.AssertRecordWritten(CrashOracleAsserts.FirstChance, "code=0xC0000005");
         }
         finally
         {
            CleanUpAfterSimulatedCrash("An error has been detected. A mini dump has been written");
         }
      }

      [Test]
      [Description("A healthy server on the default configuration records nothing")]
      public void DefaultConfigurationRecordsNoFaults()
      {
         // Passes both before and after the change, and is here on purpose: a
         // diagnostic that fires on the shipped default configuration would make
         // every run fail the preflight and would train everyone to ignore it. The
         // marker file must simply not exist on a server that is behaving.
         _settings.CrashSimulationMode = 0;

         CrashOracleAsserts.Clear();

         TriggerCrashSimulationError();

         // And some genuine work, so that the "nothing was recorded" claim covers a
         // server actually handling mail rather than one sitting idle.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "crashoracle@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Crash oracle", "Body");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         CrashOracleAsserts.AssertNoMemorySafetyEvents();
         CustomAsserts.AssertNoReportedError();

         Assert.IsFalse(File.Exists(CrashOracleAsserts.GetMarkerFileName()),
            "The crash oracle created its marker file on a default configuration.");
      }

      [Test]
      [Description("The preflight fails when a record is present, and passes once it is cleared")]
      public void PreflightFailsWhenTheMarkerFileHasRecords()
      {
         // A negative control on the oracle's own test-side plumbing rather than on
         // the server: a preflight that cannot fail is not a preflight. It passes
         // before and after the server change, which is the point - it pins the
         // helper that every other fixture will depend on.
         //
         // It pins the decision the preflight makes - the records it fails on, and the
         // message it fails with - rather than catching the AssertionException. NUnit 3
         // records a failed assertion in the test result at the moment Assert.Fail
         // runs, so a test that swallows the exception is still reported as failed.
         // What is left in the assert itself is one if over GetMemorySafetyEvents().
         var markerFile = CrashOracleAsserts.GetMarkerFileName();

         CrashOracleAsserts.Clear();

         try
         {
            const string record =
               "2026-08-12 00:00:00.000 FIRSTCHANCE code=0xC0000005 address=0x0000000000000000 thread=1 pid=1 event=1";

            File.WriteAllText(markerFile, record + "\r\n");

            Assert.AreEqual(1, CrashOracleAsserts.ReadRecords(CrashOracleAsserts.FirstChance).Length);

            var events = CrashOracleAsserts.GetMemorySafetyEvents();

            Assert.AreEqual(1, events.Length,
               "The preflight would have passed even though the marker file contained a memory-safety record.");

            // And it fails with something actionable: the record itself, and the file to
            // look in for the minidump written beside it.
            var message = CrashOracleAsserts.DescribeMemorySafetyEvents(events);

            Assert.IsTrue(message.Contains(record), "The failure message does not quote the record.");
            Assert.IsTrue(message.Contains(markerFile), "The failure message does not name the marker file.");
         }
         finally
         {
            CrashOracleAsserts.Clear();
         }

         // And with the file gone, the preflight is quiet again - so it cannot latch
         // on and fail the rest of the run once somebody has dealt with a fault. This
         // call is the real assert, exactly as SetUp makes it.
         Assert.AreEqual(0, CrashOracleAsserts.GetMemorySafetyEvents().Length);
         CrashOracleAsserts.AssertNoMemorySafetyEvents();
      }
   }
}
