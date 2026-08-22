// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   public class LogHandler
   {
      public static void DeleteEventLog()
      {
         CustomAsserts.AssertDeleteFile(GetEventLogFileName());
      }

      public static void DeleteErrorLog()
      {
         var errorLog = GetErrorLogFileName();

         if (File.Exists(errorLog)) File.Delete(errorLog);
      }

      public static string ReadAndDeleteErrorLog()
      {
         var contents = ReadErrorLog();

         DeleteErrorLog();

         return contents;
      }

      /// <summary>
      ///    Clears the ERROR log and keeps clearing it until it has stayed absent for a
      ///    settling window. For use by a test that provoked errors on purpose.
      ///
      ///    DeleteErrorLog on its own is not enough after a deliberate crash, and this has
      ///    cost two full twenty-minute regression runs. One crash simulation writes at
      ///    least three ERROR entries from three different paths - the protocol parse
      ///    failure (HM5136), the exception handler (HM4208), and the "a mini dump has
      ///    been written" line (HM5519) which cannot be logged until an OUT-OF-PROCESS
      ///    minidump writer has finished. A test that waits for the entries it cares
      ///    about and then deletes the file therefore deletes it while the rest are still
      ///    arriving, and the stragglers recreate it moments later.
      ///
      ///    Nothing notices at the time. The next fixture's PerformBasicSetup calls
      ///    AssertNoReportedError, which fails if the ERROR log exists at all, so every
      ///    test that runs after the leak fails in setup - 683 of them in the run that
      ///    prompted this, all reporting a deliberate "Crash simulation test" line, none
      ///    of them broken.
      ///
      ///    Bounded on both sides: it gives up after <paramref name="timeoutSeconds"/> so
      ///    that a genuinely broken server writing an error every second cannot spin here
      ///    for ever, and it returns as soon as the log has been absent for the settle
      ///    window rather than always paying the full timeout.
      /// </summary>
      /// <returns>
      ///    True if the log is absent and settled. False means errors are still arriving,
      ///    which the caller should treat as a real problem rather than ignore.
      /// </returns>
      public static bool ClearErrorLogUntilSettled(int settleMilliseconds = 2500, int timeoutSeconds = 25)
      {
         var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
         DateTime? quietSince = null;

         while (DateTime.UtcNow < deadline)
         {
            if (File.Exists(GetErrorLogFileName()))
            {
               DeleteErrorLog();

               // A straggler restarts the window: what matters is that nothing has
               // arrived for the whole of it, not that something was deleted recently.
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

         // Best effort on the way out, so the next fixture has the best chance even
         // though this one is about to report the problem.
         DeleteErrorLog();
         return false;
      }

      public static string ReadErrorLog()
      {
         var file = GetErrorLogFileName();
         CustomAsserts.AssertFileExists(file, false);

         // Read the file without taking a lock.
         // If a lock is taken, hMailServer will not be able to write to the file, while 
         // this code is reading from it.
         using (var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
         using (var textReader = new StreamReader(fileStream))
         {
            return textReader.ReadToEnd();
         }
      }


      public static string GetErrorLogFileName()
      {
         return SingletonProvider<TestSetup>.Instance.GetApp().Settings.Logging.CurrentErrorLog;
      }

      public static string GetDefaultLogFileName()
      {
         return SingletonProvider<TestSetup>.Instance.GetApp().Settings.Logging.CurrentDefaultLog;
      }

      public static void DeleteCurrentDefaultLog()
      {
         for (var i = 0; i < 50; i++)
            try
            {
               var filename = GetDefaultLogFileName();
               if (File.Exists(filename))
                  File.Delete(filename);

               return;
            }
            catch (Exception)
            {
               Thread.Sleep(100);
            }

         throw new Exception("Failed to delete default log file.");
      }

      public static string ReadCurrentDefaultLog()
      {
         var filename = GetDefaultLogFileName();
         var content = string.Empty;
         if (File.Exists(filename))
            return TestSetup.ReadExistingTextFile(filename);

         return content;
      }

      public static bool DefaultLogContains(string data)
      {
         var filename = GetDefaultLogFileName();

         for (var i = 0; i < 40; i++)
         {
            if (File.Exists(filename))
            {
               var content = TestSetup.ReadExistingTextFile(filename);
               if (content.Contains(data))
                  return true;
            }

            Thread.Sleep(250);
         }

         return false;
      }

      public static string GetEventLogFileName()
      {
         return SingletonProvider<TestSetup>.Instance.GetApp().Settings.Logging.CurrentEventLog;
      }
   }
}