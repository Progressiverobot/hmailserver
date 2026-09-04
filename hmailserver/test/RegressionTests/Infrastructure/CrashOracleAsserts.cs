// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Reads the crash oracle's marker file - the mechanism by which the test
   ///    runner finds out that the server suffered a memory-safety fault.
   ///
   ///    This exists because a minidump on disk that nobody looks at is not an
   ///    oracle. The suite already notices when the process dies
   ///    (ServiceRestartDetector) and when the server reports an error
   ///    (CustomAsserts.AssertNoReportedError). Neither notices the case that
   ///    matters most: an access violation that a catch (...) caught - the project
   ///    builds with /EHa, so catch (...) catches structured exceptions too - and
   ///    that was then logged as an ordinary failure or not logged at all. The
   ///    server keeps running, the error log may look reasonable, and the test that
   ///    provoked the corruption passes.
   ///
   ///    The server records those faults from a vectored exception handler, which
   ///    Windows calls before any catch (...) gets a chance, into
   ///    &lt;LogDirectory&gt;\crash-oracle.log. One line per fault:
   ///
   ///    2026-08-12 14:03:11.123 FIRSTCHANCE code=0xC0000005 address=0x00007FF6... thread=4212 pid=9876 event=1
   ///
   ///    FIRSTCHANCE means "a memory-safety exception was raised", whoever went on
   ///    to handle or hide it. UNHANDLED means the process was terminated for it.
   /// </summary>
   public static class CrashOracleAsserts
   {
      public const string FirstChance = "FIRSTCHANCE";
      public const string Unhandled = "UNHANDLED";

      private const string MarkerFileName = "crash-oracle.log";

      public static string GetMarkerFileName()
      {
         var logDirectory = SingletonProvider<TestSetup>.Instance.GetApp().Settings.Directories.LogDirectory;

         return Paths.Combine(logDirectory, MarkerFileName);
      }

      /// <summary>
      ///    All records of the given kind, or all records if kind is null. An absent
      ///    marker file means no faults, which is the normal, healthy case.
      /// </summary>
      public static string[] ReadRecords(string kind)
      {
         var file = GetMarkerFileName();

         if (!File.Exists(file))
            return new string[0];

         string contents;

         // Read without taking an exclusive lock: the server opens the file for
         // append whenever it records a fault, and must not be blocked by us.
         using (var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
         using (var textReader = new StreamReader(fileStream))
         {
            contents = textReader.ReadToEnd();
         }

         return contents.Split('\n')
            .Select(raw => raw.Trim())
            .Where(line => line.Length > 0)
            .Where(line => kind == null || line.Contains(" " + kind + " "))
            .ToArray();
      }

      public static void Clear()
      {
         CustomAsserts.AssertDeleteFile(GetMarkerFileName());
      }

      /// <summary>
      ///    The preflight. Intended to be called from TestFixtureBase.SetUp, next to
      ///    ServiceRestartDetector.ValidateProcessId(), so that a memory-safety fault
      ///    provoked by one test cannot be ignored by the rest of the run.
      ///
      ///    Moves the marker file aside once it has reported it, so that a single fault
      ///    fails one test rather than every test after it - see ArchiveMarkerFile_ for
      ///    why that is the right way round. A test that provokes a fault on purpose is
      ///    still responsible for calling Clear() in a finally, so that it does not
      ///    fail an unrelated fixture and leave an archived file behind for a fault
      ///    that was the point of the test.
      /// </summary>
      public static void AssertNoMemorySafetyEvents()
      {
         var records = GetMemorySafetyEvents();

         if (records.Length == 0)
            return;

         Assert.Fail(DescribeMemorySafetyEvents(records) + ArchiveMarkerFile_());
      }

      /// <summary>
      ///    Moves the marker file aside, so that the fault is reported once instead of
      ///    failing every test that runs after it, and returns a sentence naming where
      ///    it went.
      ///
      ///    The first version of this class deliberately left the file in place, on the
      ///    reasoning that a fault should keep failing fixtures until somebody looks at
      ///    it. Running it proved that wrong: three faults - all three deliberately
      ///    provoked by ExceptionHandlerTests - failed 645 of 1162 tests, every one of
      ///    them at SetUp before it had run, so the run said nothing about anything
      ///    else. A fault has to be impossible to miss, which the failure it causes
      ///    achieves; it does not have to be impossible to get past.
      ///
      ///    Archived rather than deleted because the file is the evidence. The server
      ///    opens it with FILE_SHARE_DELETE, so moving it does not need the server to
      ///    be idle, and it reopens the path on the next fault.
      /// </summary>
      private static string ArchiveMarkerFile_()
      {
         var file = GetMarkerFileName();

         try
         {
            var archived = Paths.Combine(
               Path.GetDirectoryName(file),
               string.Format("crash-oracle.{0:yyyyMMdd-HHmmss-fff}.log", DateTime.Now));

            File.Move(file, archived);

            return Environment.NewLine + Environment.NewLine +
                   "The marker file has been moved to " + archived +
                   " so that the rest of the run still reports its own results. It is evidence: keep it, and look for a minidump written beside it.";
         }
         catch (IOException e)
         {
            // Left in place, which means the next test fails on the same fault. Noisy,
            // but it is the safe direction - nothing is lost.
            return Environment.NewLine + Environment.NewLine +
                   "The marker file could not be moved aside (" + e.Message +
                   "), so it has been left in place and the tests that follow will fail on these same records.";
         }
      }

      /// <summary>
      ///    The records the preflight fails on: every record in the marker file,
      ///    whatever kind. Non-empty means every fixture's SetUp fails until somebody
      ///    has dealt with the fault.
      ///
      ///    Split out from AssertNoMemorySafetyEvents so that the negative control can
      ///    pin the decision without going through an NUnit assert. NUnit 3 records a
      ///    failed assertion in the test result at the moment Assert.Fail runs, so a
      ///    test cannot demonstrate that an assert fires by catching its
      ///    AssertionException - it is reported as failed anyway.
      /// </summary>
      public static string[] GetMemorySafetyEvents()
      {
         return ReadRecords(null);
      }

      /// <summary>
      ///    The message the preflight fails with. Split out for the same reason as
      ///    GetMemorySafetyEvents.
      /// </summary>
      public static string DescribeMemorySafetyEvents(string[] records)
      {
         return string.Format(
            "The crash oracle recorded {0} memory-safety fault(s) in {1}. This is a memory-safety defect in the server, not a test failure - the server may well have carried on running, because /EHa lets catch (...) swallow an access violation. Records:{2}{3}",
            records.Length,
            GetMarkerFileName(),
            Environment.NewLine,
            string.Join(Environment.NewLine, records));
      }

      /// <summary>
      ///    Waits for at least one record of the given kind whose text contains
      ///    expectedContent, and returns it.
      /// </summary>
      public static string AssertRecordWritten(string kind, string expectedContent)
      {
         string found = null;

         RetryHelper.TryAction(TimeSpan.FromSeconds(20), () =>
         {
            var records = ReadRecords(kind);

            found = records.FirstOrDefault(record => record.Contains(expectedContent));
            if (found != null)
               return;

            throw new Exception(string.Format(
               "No {0} record containing '{1}' in {2}. Records found: {3}",
               kind, expectedContent, GetMarkerFileName(),
               records.Length == 0 ? "(none)" : string.Join(" | ", records)));
         });

         return found;
      }
   }
}
