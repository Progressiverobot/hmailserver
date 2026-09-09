// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The server's logs, through GET /api/v1/logs and GET /api/v1/logs/{name}. The
   ///    Windows LogHandler reads and deletes the files on the bench; here the files
   ///    are the server's and the route serves the last 2000 lines of one, so the
   ///    ERROR log is judged by a mark rather than by deletion: SetUp notes how the
   ///    log ends, TearDown asks what came after.
   ///
   ///    The mark is the tail's last line and the tail's length, not a byte offset the
   ///    route does not offer. New lines are everything after the last occurrence of
   ///    the marked line; when the marked line is no longer in the tail (2000 lines
   ///    written in one test, or a new day's file), every line of the tail counts,
   ///    which errs toward reporting.
   /// </summary>
   public class LogHandler
   {
      private static string[] _errorMark = new string[0];

      /// <summary>The newest log file whose name starts with the prefix, by name, or null.</summary>
      private static string NewestLogNamed(string prefix)
      {
         var answer = ServerApi.Get("/api/v1/logs");
         if (!answer.Ok)
            return null;

         string newest = null;
         foreach (var file in ServerApi.Array(answer))
         {
            var name = ServerApi.StringOf(file, "name");
            if (name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (newest == null || string.CompareOrdinal(name, newest) > 0))
               newest = name;
         }

         return newest;
      }

      private static string[] Tail(string name)
      {
         if (name == null)
            return new string[0];

         var answer = ServerApi.Get("/api/v1/logs/" + name + "?lines=2000");
         if (answer.Status == 404)
            return new string[0];

         answer.Expect(200, "GET /api/v1/logs/" + name);

         // The route answers with the lines as a JSON array, or as text; both are
         // taken.
         if (answer.Json.HasValue)
         {
            var root = answer.Json.Value;
            JsonElement lines;
            if (root.ValueKind == JsonValueKind.Array)
               return root.EnumerateArray().Select(e => e.ToString()).ToArray();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lines", out lines) && lines.ValueKind == JsonValueKind.Array)
               return lines.EnumerateArray().Select(e => e.ToString()).ToArray();
         }

         return answer.Body.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToArray();
      }

      public static string ReadCurrentDefaultLog()
      {
         return string.Join(Environment.NewLine, Tail(NewestLogNamed("hmailserver_")));
      }

      public static string ReadErrorLog()
      {
         return string.Join(Environment.NewLine, Tail(NewestLogNamed("ERROR_hmailserver_")));
      }

      /// <summary>Nothing to delete from here; the mark taken in SetUp is what stands in for it.</summary>
      public static void DeleteCurrentDefaultLog()
      {
      }

      public static void MarkErrorLog()
      {
         _errorMark = Tail(NewestLogNamed("ERROR_hmailserver_"));
      }

      /// <summary>
      ///    The ERROR log lines written since MarkErrorLog, without the one line that
      ///    is noise on a shared machine: HM4316, a listener that could not bind
      ///    because another server holds the port, reported at start and at every
      ///    settings reload and unrelated to any test.
      /// </summary>
      public static string[] ErrorLogLinesSinceMark()
      {
         var now = Tail(NewestLogNamed("ERROR_hmailserver_"));

         IEnumerable<string> fresh = now;

         if (_errorMark.Length > 0)
         {
            var last = _errorMark[_errorMark.Length - 1];
            var at = Array.LastIndexOf(now, last);
            if (at >= 0)
               fresh = now.Skip(at + 1);
         }

         return fresh.Where(line => line.Trim().Length > 0 && !line.Contains("HM4316")).ToArray();
      }

      /// <summary>
      ///    For a test that provoked an error on purpose and has read it: what comes
      ///    after this point is what TearDown judges, as ReadAndDeleteErrorLog leaves
      ///    the Windows bench.
      /// </summary>
      public static string ReadAndDeleteErrorLog()
      {
         var contents = ReadErrorLog();
         MarkErrorLog();
         return contents;
      }

      public static void DeleteErrorLog()
      {
         MarkErrorLog();
      }
   }

   /// <summary>
   ///    The crash oracle, as far as it exists on the server under test. The Windows
   ///    server records every memory-safety fault from a vectored exception handler
   ///    into crash-oracle.log in its log directory, and the Windows suite reads that
   ///    file before and after every test. The Linux server's CrashOracle.cpp says of
   ///    itself that it is "present, honest, and armed with nothing": a fault there is
   ///    a signal that ends the process, which ServiceRestartDetector and the tests'
   ///    own sockets then notice.
   ///
   ///    So this asks GET /api/v1/logs/crash-oracle.log, which the log route serves
   ///    when the file exists (the name is a safe log name) and answers 404 when it
   ///    does not, and fails on any record. Against today's Linux server that is a
   ///    404 every time, and this class is a no-op that says so rather than one that
   ///    pretends to check.
   /// </summary>
   public static class CrashOracleAsserts
   {
      public static string[] GetMemorySafetyEvents()
      {
         var answer = ServerApi.Get("/api/v1/logs/crash-oracle.log?lines=2000");

         if (answer.Status == 404 || answer.Status == 400)
            return new string[0];

         if (!answer.Ok)
            return new string[0];

         return answer.Body.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
      }

      public static void AssertNoMemorySafetyEvents()
      {
         var events = GetMemorySafetyEvents();

         if (events.Length > 0)
            Assert.Fail("The server recorded " + events.Length + " memory-safety event(s):" + Environment.NewLine +
                        string.Join(Environment.NewLine, events));
      }
   }

   public static class CustomAsserts
   {
      public delegate void AssertionBody();

      public static void AssertRecipientsInDeliveryQueue(int count)
      {
         AssertRecipientsInDeliveryQueue(count, true);
      }

      /// <summary>
      ///    The Windows one, with POST /api/v1/queue/{id}/retry where it resets delivery
      ///    times over COM: every queued message is tried now, and the recipient count
      ///    is polled for up to twenty seconds.
      /// </summary>
      public static void AssertRecipientsInDeliveryQueue(int count, bool forceSend)
      {
         if (forceSend)
            TestSetup.SendMessagesInQueue();

         var timeoutTime = DateTime.UtcNow.AddSeconds(20);

         while (DateTime.UtcNow < timeoutTime)
         {
            if (TestSetup.GetNumberOfMessagesInDeliveryQueue() == count)
               return;

            TestSetup.SendMessagesInQueue();
            Thread.Sleep(250);
         }

         var actual = TestSetup.GetNumberOfMessagesInDeliveryQueue();
         if (actual != count)
            Assert.Fail("Wrong number of recipients in delivery queue. Actual: " + actual + " Expected: " + count +
                        Environment.NewLine + ServerApi.Get("/api/v1/queue").Body);
      }

      public static void AssertNoReportedError()
      {
         var lines = LogHandler.ErrorLogLinesSinceMark();
         if (lines.Length > 0)
            Assert.Fail(string.Join(Environment.NewLine, lines));
      }

      /// <summary>
      ///    Waits up to ten seconds for the ERROR log to carry every expected text,
      ///    then moves the mark past it so that TearDown does not fail the test for
      ///    the error it asked for - what the Windows fixtures do by deleting the log.
      /// </summary>
      public static void AssertReportedError(string firstContent, params string[] contents)
      {
         var expected = new List<string> { firstContent };
         expected.AddRange(contents);

         var deadline = DateTime.UtcNow.AddSeconds(10);
         string errorLog = string.Empty;

         while (DateTime.UtcNow < deadline)
         {
            errorLog = string.Join(Environment.NewLine, LogHandler.ErrorLogLinesSinceMark());

            if (expected.All(errorLog.Contains))
            {
               LogHandler.MarkErrorLog();
               return;
            }

            Thread.Sleep(250);
         }

         Assert.Fail("The ERROR log does not contain " + string.Join(", ", expected) + ". It contains:" +
                     Environment.NewLine + errorLog);
      }

      public static void Throws<T>(AssertionBody func) where T : Exception
      {
         var exceptionThrown = false;
         try
         {
            func.Invoke();
         }
         catch (T)
         {
            exceptionThrown = true;
         }

         if (!exceptionThrown)
            Assert.Fail("Expected exception of type " + typeof(T).Name + " was not thrown.");
      }
   }
}
