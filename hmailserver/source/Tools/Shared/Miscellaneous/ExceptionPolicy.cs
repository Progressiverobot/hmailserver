// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;

namespace hMailServer.Shared
{
   /// <summary>
   ///    The setup tools catch broadly where a failed COM call or a failed file
   ///    operation has to become a message rather than a crash. A broad catch is
   ///    written as <c>catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))</c>
   ///    so that the exceptions which mean the process is no longer sound are not
   ///    swallowed with the rest.
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
