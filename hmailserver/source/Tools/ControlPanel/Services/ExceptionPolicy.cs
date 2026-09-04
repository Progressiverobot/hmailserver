// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   ///    The Control Panel catches broadly on purpose: almost every call crosses
   ///    into the COM API and can fail with COMException, InvalidComObjectException,
   ///    a reflection wrapper around either, or a server-side error surfaced as a
   ///    plain Exception, and a settings page that cannot read one value has to show
   ///    a message rather than take the window down. What such a handler must never
   ///    do is swallow an exception that means the process itself is no longer
   ///    sound. Every broad catch is therefore written as
   ///    <c>catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))</c>: the
   ///    recoverable failures keep their "show it and carry on" handling, and the
   ///    fatal ones propagate to the application's unhandled-exception path.
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
