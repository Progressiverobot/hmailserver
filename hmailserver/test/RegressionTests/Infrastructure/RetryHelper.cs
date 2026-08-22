// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;

namespace RegressionTests.Infrastructure
{
   internal class RetryHelper
   {
      public delegate void ActionDelegate();

      public static void TryAction(TimeSpan duration, ActionDelegate action)
      {
         var timeout = DateTime.Now + duration;

         while (DateTime.Now < timeout)
         {
            try
            {
               action();
               return;
            }
            catch
            {
               // Will retry.
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(500));
         }

         action();
      }
   }
}