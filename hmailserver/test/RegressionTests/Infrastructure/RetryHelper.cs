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