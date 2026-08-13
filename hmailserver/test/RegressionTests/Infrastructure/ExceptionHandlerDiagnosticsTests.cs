// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.IO;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    What the crash report itself says, and what happens when a dump cannot be
   ///    written at all.
   ///
   ///    Separate from ExceptionHandlerTests, which pins the count policy and the
   ///    happy path, because these are about the CONTENT of the report - the part an
   ///    administrator without a debugger, or without the .dmp any more, is left
   ///    with.
   /// </summary>
   [TestFixture]
   public class ExceptionHandlerDiagnosticsTests : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         DeleteAllMinidumps();
         LogHandler.DeleteErrorLog();
      }

      [TearDown]
      public new void TearDown()
      {
         _settings.CrashSimulationMode = 0;

         // Same discipline, and for the same reason, as ExceptionHandlerTests.TearDown:
         // one crash writes several ERROR entries from several paths and the last of
         // them cannot arrive until an out-of-process dump writer has finished, so
         // deleting the log once leaves stragglers to recreate it and every following
         // fixture fails in setup on AssertNoReportedError.
         bool settled = LogHandler.ClearErrorLogUntilSettled();

         DeleteAllMinidumps();

         // Crash simulation mode 3 is a real access violation, so the crash oracle has
         // recorded it. Cleared here, before the base TearDown asserts on it - NUnit
         // runs the derived teardown first - because the fault was the point of the
         // test.
         CrashOracleAsserts.Clear();

         Assert.IsTrue(settled,
            "Deliberate crash errors were still arriving after the settle timeout, so the "
            + "ERROR log could not be left clean. Every fixture that runs after this one "
            + "would fail in setup on AssertNoReportedError.");
      }

      private string LogDirectory
      {
         get { return _settings.Directories.LogDirectory; }
      }

      private string[] GetMinidumps()
      {
         return Directory.GetFiles(LogDirectory, "minidump*.dmp");
      }

      private void DeleteAllMinidumps()
      {
         foreach (var minidump in GetMinidumps())
            try
            {
               File.Delete(minidump);
            }
            catch (IOException)
            {
               // Left for the next run rather than failing teardown on a file the dump
               // writer has not let go of yet.
            }
      }

      private static void TriggerCrashSimulationError()
      {
         using (var tcpConnection = new TcpConnection())
         {
            tcpConnection.Connect(25);
            tcpConnection.Send("help\r\n");
            tcpConnection.Receive();
         }
      }

      private static void AssertErrorLogContains(params string[] expected)
      {
         RetryHelper.TryAction(TimeSpan.FromSeconds(20), () =>
         {
            var errorLog = LogHandler.ReadErrorLog();

            foreach (var text in expected)
               if (!errorLog.Contains(text))
                  throw new Exception(
                     string.Format("The ERROR log does not contain \"{0}\". It contains:\r\n{1}", text, errorLog));
         });
      }

      /// <summary>
      ///    A crash report has to say WHICH fault it was.
      ///
      ///    Against the unfixed server this fails on the very first assertion: the
      ///    exception code was accepted as a parameter of ExceptionLogger::Log and then
      ///    never referenced, so every crash - access violation, stack overflow, divide
      ///    by zero - was reported with the identical sentence "An error has been
      ///    detected. A mini dump has been written to ...", and nothing else. Two
      ///    reports could not be told apart, and an administrator with no debugger had
      ///    nothing to put in a bug report.
      /// </summary>
      [Test]
      public void CrashReportShouldNameTheExceptionCodeAndTheFaultingAddress()
      {
         _settings.CrashSimulationMode = 3;

         TriggerCrashSimulationError();

         AssertErrorLogContains(
            "An error has been detected. A mini dump has been written",
            "Exception code: 0xC0000005 (ACCESS_VIOLATION)",

            // Crash simulation mode 3 is memset(0, 1, 1), so this is a WRITE to
            // address zero. The address itself is left out of the expectation because
            // its width differs between the 32-bit and 64-bit builds.
            "The thread was writing address 0x",
            "Thread: ");
      }

      /// <summary>
      ///    A crash that cannot be dumped must still be reported.
      ///
      ///    Against the unfixed server this fails: once ten dumps exist and none is
      ///    four hours old, TryToMakeRoom returned false and Log returned immediately,
      ///    so the eleventh fault - and every fault after it, for four hours - produced
      ///    NOTHING an operator would ever see. The refusal went to LOG_DEBUG, which is
      ///    off on a default configuration, and the error-log entry was never reached.
      ///    The eleventh crash is often the one that finally makes somebody look.
      /// </summary>
      [Test]
      public void CrashWithNoRoomForAMinidumpShouldStillBeReported()
      {
         _settings.CrashSimulationMode = 3;

         for (var i = 1; i <= 11; i++)
            TriggerCrashSimulationError();

         RetryHelper.TryAction(TimeSpan.FromSeconds(20), () =>
         {
            var count = GetMinidumps().Length;
            if (count != 10)
               throw new Exception(string.Format("Expected 10 minidumps, found {0}.", count));
         });

         AssertErrorLogContains(
            "No mini dump has been written: The max count (10) is reached and no log is older than 4 hours.",
            "Exception code: 0xC0000005 (ACCESS_VIOLATION)");
      }

      /// <summary>
      ///    The dump directory is bounded in bytes as well as in files.
      ///
      ///    Against the unfixed server this fails: the only ceiling was ten files, and
      ///    ten files is a bound in megabytes purely because hMailServer.Minidump.exe
      ///    happens to pass MiniDumpNormal in a different executable. Four large dumps
      ///    were nowhere near the file ceiling, so a fifth was written regardless of
      ///    how much of the volume the first four had already taken - and the volume in
      ///    question is normally the one holding the mail.
      /// </summary>
      [Test]
      public void MinidumpsShouldBeBoundedByTotalSizeAndNotOnlyByCount()
      {
         const long dumpSize = 30L * 1024L * 1024L;

         // Four files, 120 MB in total, which is over the 100 MB ceiling but well under
         // the ten-file ceiling - so only a byte-aware policy refuses the next one.
         // SetLength rather than a real write: NTFS extends the file without writing
         // 120 MB, and boost::filesystem::file_size reports the full length either way.
         for (var i = 1; i <= 4; i++)
         {
            var name = Path.Combine(LogDirectory, string.Format("minidump_bytebudget_{0}.dmp", i));

            using (var stream = new FileStream(name, FileMode.Create, FileAccess.Write))
               stream.SetLength(dumpSize);
         }

         _settings.CrashSimulationMode = 3;

         TriggerCrashSimulationError();

         AssertErrorLogContains(
            "No mini dump has been written: The mini dumps in the log directory total",
            "byte ceiling, and no log is older than 4 hours",
            "Exception code: 0xC0000005 (ACCESS_VIOLATION)");

         // And no dump was written: the four placeholders are all that is there.
         Assert.AreEqual(4, GetMinidumps().Length);
      }
   }
}
