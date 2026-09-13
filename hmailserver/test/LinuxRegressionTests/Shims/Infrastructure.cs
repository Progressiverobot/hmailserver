// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
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
      private static string[] _defaultMark = new string[0];

      /// <summary>
      ///    The file the server is writing now, by the name the logging group gives
      ///    it: current_default_log and current_error_log are the server's own answer
      ///    to "which file is this", and only its last segment is needed, because the
      ///    log route is addressed by file name.
      ///
      ///    Guessing from the listing instead does not work, and cost a run to find
      ///    out: the log directory also holds hmailserver_awstats.log and
      ///    hmailserver_events.log, and "awstats" and "events" both sort after
      ///    "2026-09-09", so the newest name with the prefix was the AWStats file and
      ///    every read of "the log" came back without a line of the session in it.
      /// </summary>
      private static string CurrentLogNamed(string key, string prefix)
      {
         var answer = ServerApi.Get("/api/v1/settings/logging");

         if (answer.Ok && answer.Json.HasValue)
         {
            var path = ServerApi.StringOf(answer.Json.Value, key);

            if (!string.IsNullOrEmpty(path))
            {
               var slash = path.LastIndexOfAny(new[] { '/', '\\' });
               return slash < 0 ? path : path.Substring(slash + 1);
            }
         }

         // An older server without those fields: the newest file with the prefix.
         return NewestLogNamed(prefix);
      }

      /// <summary>The newest log file whose name starts with the prefix, by name, or null.</summary>
      private static string NewestLogNamed(string prefix)
      {
         var answer = ServerApi.Get("/api/v1/logs");
         if (!answer.Ok)
            return null;

         return ServerApi.Array(answer)
            .Select(file => ServerApi.StringOf(file, "name"))
            .Where(name => name != null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .FirstOrDefault();
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

      /// <summary>
      ///    The default log SINCE THE MARK, not the whole file. The Windows suite
      ///    reads a log that the gate's service restart left empty and that a test
      ///    wanting a clean one deletes; here the file is one per day on a server
      ///    that stays up across many runs, so the whole of it is hours of other
      ///    sessions. A fixture searching it for a string it must NOT find - the
      ///    plaintext-command-injection test looks for "RSET" - then fails on
      ///    somebody else's traffic, alone passes and in a full run does not.
      /// </summary>
      public static string ReadCurrentDefaultLog()
      {
         var now = Tail(CurrentLogNamed("current_default_log", "hmailserver_"));

         IEnumerable<string> fresh = now;

         if (_defaultMark.Length > 0)
         {
            var last = _defaultMark[_defaultMark.Length - 1];
            var at = Array.LastIndexOf(now, last);

            // Not found means the log rolled over or was replaced under us, and
            // everything now in it is newer than the mark.
            if (at >= 0)
               fresh = now.Skip(at + 1);
         }

         return string.Join(Environment.NewLine, fresh);
      }

      public static void MarkDefaultLog()
      {
         _defaultMark = Tail(CurrentLogNamed("current_default_log", "hmailserver_"));
      }

      public static string ReadErrorLog()
      {
         return string.Join(Environment.NewLine, Tail(CurrentLogNamed("current_error_log", "ERROR_hmailserver_")));
      }

      /// <summary>
      ///    No route deletes a log, so this takes a fresh mark instead - which is
      ///    what the callers mean by it: everything read after this call should be
      ///    what happened after this call.
      /// </summary>
      public static void DeleteCurrentDefaultLog()
      {
         MarkDefaultLog();
      }

      public static void MarkErrorLog()
      {
         _errorMark = Tail(CurrentLogNamed("current_error_log", "ERROR_hmailserver_"));
      }

      /// <summary>
      ///    The ERROR log lines written since MarkErrorLog, without the one line that
      ///    is noise on a shared machine: HM4316, a listener that could not bind
      ///    because another server holds the port, reported at start and at every
      ///    settings reload and unrelated to any test.
      /// </summary>
      public static string[] ErrorLogLinesSinceMark()
      {
         var now = Tail(CurrentLogNamed("current_error_log", "ERROR_hmailserver_"));

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

      /// <summary>
      ///    The file names GET /api/v1/settings/logging reports. They are the server's
      ///    paths, which a test can open only when the server is this machine - which
      ///    is what TestTarget.IsLocal says and what this environment is.
      /// </summary>
      /// <summary>
      ///    On the Windows bench the ERROR log is deleted before every test, so that
      ///    the file exists exactly when the test under way caused an error, and a
      ///    fixture asserts on File.Exists of this name. Here the file is the
      ///    server's own and holds the whole run, so the name answered is a scratch
      ///    file that holds the lines written since the mark - present when there
      ///    are any, absent when there are none - which is the same fact.
      /// </summary>
      public static string GetErrorLogFileName()
      {
         var scratch = Path.Combine(Path.GetTempPath(), "hmtest-error-log-since-mark.log");
         var fresh = ErrorLogLinesSinceMark();
         if (fresh.Length == 0)
         {
            if (File.Exists(scratch))
               File.Delete(scratch);
         }
         else
         {
            File.WriteAllText(scratch, string.Join(Environment.NewLine, fresh) + Environment.NewLine);
         }
         return scratch;
      }

      /// <summary>The server's own ERROR log file, for what needs the real one.</summary>
      public static string GetServerErrorLogFileName()
      {
         return hMailServer.SettingsApi.GetString(hMailServer.SettingsApi.Logging, "current_error_log");
      }

      public static string GetDefaultLogFileName()
      {
         return hMailServer.SettingsApi.GetString(hMailServer.SettingsApi.Logging, "current_default_log");
      }

      public static string GetEventLogFileName()
      {
         return hMailServer.SettingsApi.GetString(hMailServer.SettingsApi.Logging, "current_event_log");
      }

      /// <summary>
      ///    The event log is written by the server and no route deletes it; a mark is
      ///    taken instead, the same way the ERROR log is handled here.
      /// </summary>
      public static void DeleteEventLog()
      {
      }

      /// <summary>
      ///    Polls the default log for the text, as the Windows one polls the file.
      ///    The route serves the last 2000 lines rather than the whole file, so a
      ///    line pushed out of that window by 2000 later ones would be missed; ten
      ///    seconds of a single test's logging is well inside it.
      /// </summary>
      public static bool DefaultLogContains(string data)
      {
         for (var i = 0; i < 40; i++)
         {
            if (ReadCurrentDefaultLog().Contains(data))
               return true;

            Thread.Sleep(250);
         }

         return false;
      }

      /// <summary>
      ///    The Windows one deletes the ERROR log until nothing has re-created it for
      ///    the settle window. Nothing here deletes it, so the mark is moved instead
      ///    and the question asked of the lines after the mark: the contract - true
      ///    when no error has arrived for the whole window - is the same.
      /// </summary>
      public static bool ClearErrorLogUntilSettled(int settleMilliseconds = 2500, int timeoutSeconds = 25)
      {
         var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
         DateTime? quietSince = null;

         while (DateTime.UtcNow < deadline)
         {
            if (ErrorLogLinesSinceMark().Length > 0)
            {
               MarkErrorLog();
               quietSince = null;
               Thread.Sleep(150);
               continue;
            }

            if (quietSince == null)
               quietSince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - quietSince.Value).TotalMilliseconds >= settleMilliseconds)
               return true;

            Thread.Sleep(150);
         }

         MarkErrorLog();
         return false;
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

      /// <summary>
      ///    Nothing to clear: the crash oracle's file is the server's and no route
      ///    deletes it. Against the Linux server there is no file at all.
      /// </summary>
      public static void Clear()
      {
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

      /// <summary>
      ///    GET /api/v1/status reports the SMTP, IMAP and POP3 session counts; the
      ///    Windows one reads the same three from Status.SessionCount.
      /// </summary>
      public static void AssertSessionCount(hMailServer.eSessionType sessionType, int expectedCount)
      {
         var status = SingletonProvider<TestSetup>.Instance.GetApp().Status;

         // No early return. RetryHelper.TryAction retries while its action THROWS
         // and takes a clean return as success, so a guard that returned on
         // mismatch made a wrong count pass on the first attempt and left the
         // assertion below it reachable only when the values already agreed -
         // an assertion that could not fail. Throwing is what asks for another
         // attempt, and TryAction's last call is unguarded, so the real failure
         // arrives when the ten seconds are up.
         RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            RetryableAssert.AreEqual(expectedCount, status.get_SessionCount(sessionType)));
      }

      /// <summary>
      ///    Waits for a folder to hold exactly this many messages, reading the count
      ///    from the account's own GET /api/v1/me/folders/{id}/messages, as the
      ///    Windows one reads it from the COM folder.
      /// </summary>
      public static void AssertFolderMessageCount(hMailServer.IMAPFolder folder, int expectedCount)
      {
         if (expectedCount == 0)
            AssertRecipientsInDeliveryQueue(0);

         var currentCount = 0;
         var timeout = 100;
         while (timeout > 0)
         {
            currentCount = folder.Messages.Count;

            if (currentCount == expectedCount)
               return;

            timeout--;
            Thread.Sleep(100);
         }

         Assert.Fail("Wrong number of messages in mailbox " + folder.Name + ". Actual: " + currentCount +
                     " Expected: " + expectedCount);
      }

      public static hMailServer.Message AssertRetrieveFirstMessage(hMailServer.IMAPFolder folder)
      {
         var timeout = 100;
         while (timeout > 0)
         {
            if (folder.Messages.Count > 0)
               return folder.Messages[0];

            timeout--;
            Thread.Sleep(100);
         }

         Assert.Fail("Could not retrieve message from folder");
         return null;
      }

      public static hMailServer.Message AssertGetFirstMessage(hMailServer.Account account, string folderName)
      {
         var folder = account.IMAPFolders.get_ItemByName(folderName);

         AssertFolderMessageCount(folder, 1);

         return folder.Messages[0];
      }

      public static hMailServer.IMAPFolder AssertFolderExists(hMailServer.IMAPFolders folders, string folderName)
      {
         var timeout = 100;
         while (timeout > 0)
         {
            try
            {
               return folders.get_ItemByName(folderName);
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // Deliberately ignored: best effort only.
            }

            timeout--;
            Thread.Sleep(100);
         }

         Assert.Fail("Folder could not be found " + folderName);
         return null;
      }

      // The file helpers, the Windows ones unchanged: they act on this host, and a
      // fixture only reaches them with a path the server gave it.

      public static void AssertDeleteFile(string file)
      {
         for (var i = 0; i <= 400; i++)
         {
            if (!System.IO.File.Exists(file))
               return;

            try
            {
               System.IO.File.Delete(file);
               return;
            }
            catch (Exception)
            {
               if (i == 400)
                  throw;
            }

            Thread.Sleep(25);
         }
      }

      public static void AssertFileExists(string file, bool delete)
      {
         var timeout = 100;
         while (timeout > 0)
         {
            try
            {
               if (System.IO.File.Exists(file))
               {
                  if (delete)
                     System.IO.File.Delete(file);

                  return;
               }
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // Deliberately ignored: best effort only.
            }

            timeout--;
            Thread.Sleep(100);
         }

         Assert.Fail("Expected file does not exist:" + file);
      }

      public static void AssertFilesInDirectory(string directory, int expectedFileCount)
      {
         var count = 0;

         if (System.IO.Directory.Exists(directory))
         {
            var dirs = System.IO.Directory.GetDirectories(directory);
            count += dirs.Sum(dir => System.IO.Directory.GetFiles(dir).Length);
         }

         Assert.AreEqual(expectedFileCount, count);
      }

      public static void AssertFilesInUserDirectory(hMailServer.Account account, int expectedFileCount)
      {
         // Needs the server's data directory, which no route reports.
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         var domain = account.Address.Substring(account.Address.IndexOf("@") + 1);
         var mailbox = account.Address.Substring(0, account.Address.IndexOf("@"));

         var domainDir = Paths.Combine(settings.Directories.DataDirectory, domain);
         AssertFilesInDirectory(Paths.Combine(domainDir, mailbox), expectedFileCount);
      }

      public static string AssertLiveLogContents()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoLiveLog);
      }

      public static void AssertSpamAssassinIsRunning()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoExternalScanner + " (SpamAssassin)");
      }

      public static void AssertClamDRunning()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoExternalScanner + " (clamd)");
      }

      /// <summary>
      ///    A bounce for this address is in the queue. The Windows one walks the COM
      ///    delivery queue; GET /api/v1/queue lists the same rows with their
      ///    recipients.
      /// </summary>
      public static void AssertBounceMessageExistsInQueue(string bounceTo)
      {
         for (var i = 0; i < 100; i++)
         {
            var answer = ServerApi.Get("/api/v1/queue").Expect(200, "GET /api/v1/queue");

            foreach (var message in ServerApi.Array(answer, "messages"))
            {
               var from = ServerApi.StringOf(message, "sender") ?? string.Empty;
               var recipients = ServerApi.StringOf(message, "recipients") ?? string.Empty;

               if (from.Length == 0 && recipients.Contains(bounceTo))
                  return;
            }

            Thread.Sleep(100);
         }

         Assert.Fail("No bounce message to " + bounceTo + " in the delivery queue: " +
                     ServerApi.Get("/api/v1/queue").Body);
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
