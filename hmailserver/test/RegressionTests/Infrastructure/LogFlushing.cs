// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Issue #33: with KeepLogFilesOpen enabled, a log line sat in the C
   ///    runtime's ~4 KB write buffer until enough LATER lines arrived to push
   ///    it out - the close whose side effect was the flush is exactly what the
   ///    setting skips. On a quiet server that is unbounded; the reporter's
   ///    session tail stayed invisible, cut mid-line at the buffer boundary,
   ///    for forty minutes. The logger now flushes to the OS after each line
   ///    when it keeps the file open.
   ///
   ///    The whole regression suite otherwise runs with KeepFilesOpen off -
   ///    which is why 1,589 tests never noticed - so this fixture is the one
   ///    place the setting is switched on.
   /// </summary>
   [TestFixture]
   public class LogFlushing : TestFixtureBase
   {
      [Test]
      [Description("With KeepFilesOpen enabled, a session's log lines are readable the moment it ends.")]
      public void AKeptOpenLogIsReadableImmediately()
      {
         var logging = _application.Settings.Logging;
         logging.Enabled = true;
         logging.LogSMTP = true;
         logging.KeepFilesOpen = true;

         try
         {
            // One short session carrying a unique marker, and NO further server
            // activity afterwards - the poll below only reads the file. Without
            // the per-line flush the marker sits in the server's buffer and
            // this fails; any extra traffic here would push it out and mask the
            // bug, which is exactly how it stayed hidden.
            var marker = "flush-marker-" + Guid.NewGuid().ToString("N");

            var socket = new TcpConnection();
            ClassicAssert.IsTrue(socket.Connect(25), "Could not connect to the SMTP server.");
            socket.ReadUntil("220");
            socket.Send("HELO " + marker + "\r\n");
            socket.ReadUntil("250");
            socket.Send("QUIT\r\n");
            socket.ReadUntil("221");
            socket.Disconnect();

            ClassicAssert.IsTrue(LogHandler.DefaultLogContains("HELO " + marker),
               "The session's log lines must be flushed to the file when it ends, " +
               "not held in a buffer until unrelated later activity pushes them out.");
         }
         finally
         {
            logging.KeepFilesOpen = false;

            // The file stays open until the write AFTER the setting change, so
            // trigger one: later fixtures delete this log, which needs the
            // server's handle closed.
            new SmtpClientSimulator().GetWelcomeMessage();
         }
      }
   }
}
