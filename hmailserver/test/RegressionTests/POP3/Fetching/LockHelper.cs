// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;

namespace RegressionTests.POP3.Fetching
{
   internal class LockHelper
   {
      public static void WaitForUnlock(FetchAccount fetchAccount)
      {
         var timeoutTime = DateTime.Now.Add(TimeSpan.FromSeconds(30));

         while (DateTime.Now < timeoutTime)
         {
            if (!fetchAccount.IsLocked)
               return;

            Thread.Sleep(100);
         }

         var defaultLog = LogHandler.ReadCurrentDefaultLog();
         Assert.Fail($"At {DateTime.Now}, fetch account was not unlocked.");
      }
   }
}