// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Notices when the server under test has been restarted between two tests,
   ///    which on the Windows bench means it crashed. The Windows detector compares
   ///    hMailServer.exe's process id; the server here is on another machine, and
   ///    GET /api/v1/status reports no process id and no uptime.
   ///
   ///    What it does report is the count of messages processed since the process
   ///    started, kept in memory by ServerStatus and reset to zero by a restart. So a
   ///    count that goes DOWN between two checks is a restart, and is failed as one.
   ///    This is weaker than the process-id check and says so: a restart before the
   ///    run's first message, or between two tests that processed none, is invisible
   ///    to it, and the tests that follow fail on their own sockets instead.
   /// </summary>
   public class ServiceRestartDetector
   {
      private static long? _lastProcessedMessages;
      private static readonly object LockObj = new object();

      public static void ValidateProcessId()
      {
         lock (LockObj)
         {
            var answer = ServerApi.Get("/api/v1/status");

            if (answer.Status != 200)
               throw new Exception("The server at " + TestTarget.RestBaseUrl + " did not answer GET /api/v1/status: " +
                                   answer.Status + " " + answer.Body);

            var processed = answer.Json.HasValue ? ServerApi.LongOf(answer.Json.Value, "processedMessages") : 0;

            if (_lastProcessedMessages.HasValue && processed < _lastProcessedMessages.Value)
               throw new Exception(string.Format(
                  "hMailServer appears to have restarted: its processed-message count went from {0} to {1}.",
                  _lastProcessedMessages.Value, processed));

            _lastProcessedMessages = processed;
         }
      }
   }
}
