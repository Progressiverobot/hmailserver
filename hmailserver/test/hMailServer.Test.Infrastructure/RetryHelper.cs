// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace hMailServer.Test.Infrastructure
{
   public class RetryHelper
   {
      public static void TryAction(Action a, TimeSpan retryTime, TimeSpan timeout)
      {
         DateTime endTime = DateTime.Now + timeout;

         while (true)
         {
            try
            {
               a();
               return;
            }
            catch
            {
               if (DateTime.Now > endTime)
                  throw;

               Thread.Sleep(retryTime);
            }
         }
      }
   }
}
