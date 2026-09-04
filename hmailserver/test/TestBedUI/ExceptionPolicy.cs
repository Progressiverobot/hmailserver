// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;

namespace TestBedUI
{
   /// <summary>
   ///    The one question every broad catch in the suite has to answer before it
   ///    swallows anything: is the process still in a state the code above the
   ///    catch can reason about? A handler written as
   ///    <c>catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))</c> keeps its
   ///    "carry on" behaviour for everything that is genuinely recoverable - a
   ///    refused connection, a file in use, a COM call that failed - and lets the
   ///    exceptions that mean the runtime itself is compromised reach the
   ///    unhandled-exception path, where a test run stops instead of reporting
   ///    results computed on a corrupted process.
   /// </summary>
   public static class ExceptionPolicy
   {
      public static bool IsFatal(Exception exception)
      {
         for (Exception current = exception; current != null; current = current.InnerException)
         {
            if (current is OutOfMemoryException ||
                current is InsufficientExecutionStackException ||
                current is StackOverflowException ||
                current is AccessViolationException ||
                current is ThreadAbortException)
               return true;
         }

         return false;
      }
   }
}
