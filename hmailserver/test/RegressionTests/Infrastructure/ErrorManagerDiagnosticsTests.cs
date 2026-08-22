// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The OnError event is the only hook an administrator has for forwarding
   ///    hMailServer errors anywhere else - a pager, a SIEM, a ticket. This is about
   ///    whether it actually receives them.
   /// </summary>
   [TestFixture]
   public class ErrorManagerDiagnosticsTests : TestFixtureBase
   {
      private const string Marker = "ONERROR_MARKER";

      [TearDown]
      public new void TearDown()
      {
         try
         {
            var scripting = _settings.Scripting;

            // Off first, so nothing can fire an event while the file is being replaced.
            scripting.Enabled = false;

            // An empty script compiles cleanly and deregisters every handler, which a
            // missing file would not do quietly - LoadScripts would fail to read it and
            // report HM5017. Deleted only after the server has stopped needing it.
            var file = scripting.CurrentScriptFile;

            File.WriteAllText(file, "");
            scripting.Reload();

            if (File.Exists(file))
               File.Delete(file);
         }
         catch (Exception)
         {
            // Restoring the previous state is best effort. The log cleaning below is
            // the part that must happen: an ERROR log left behind fails every fixture
            // that runs afterwards, in setup.
         }

         // This fixture reports errors on purpose - a script that will not compile -
         // and that failure also writes a "Script Error" line of its own.
         var settled = LogHandler.ClearErrorLogUntilSettled();

         Assert.IsTrue(settled,
            "Deliberate errors were still arriving after the settle timeout, so the ERROR "
            + "log could not be left clean.");
      }

      private void InstallScript(string script)
      {
         var scripting = _settings.Scripting;

         scripting.Language = "VBScript";

         File.WriteAllText(scripting.CurrentScriptFile, script);

         scripting.Enabled = true;
         scripting.Reload();
      }

      private static string ReadEventLogOrEmpty()
      {
         var fileName = LogHandler.GetEventLogFileName();

         if (!File.Exists(fileName))
            return string.Empty;

         // Read without taking a lock, so that the server can keep writing to it, and
         // let the StreamReader detect the byte order mark - the event log is UTF-16.
         using (var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
         using (var reader = new StreamReader(stream))
         {
            return reader.ReadToEnd();
         }
      }

      /// <summary>
      ///    An error whose description spans more than one line must still reach the
      ///    OnError handler.
      ///
      ///    The event is invoked by building script source text and appending it to the
      ///    administrator's script: OnError(severity, code, "source", "description").
      ///    The description used to be pasted in raw. ErrorManager flattened newlines
      ///    for the log line it writes and then did not do the same for the copy it put
      ///    into the script, so any error carrying a newline produced a call with a line
      ///    break inside a string literal - which neither VBScript nor JScript permits.
      ///    The generated call did not parse, the handler never ran, and the only trace
      ///    was a "Script Error" line about a call that appears in nobody's script.
      ///
      ///    Those are not obscure errors. The multi-line reports are the substantial
      ///    ones: script compile reports, resolver text, anything built from
      ///    FormatMessage output.
      ///
      ///    The provocation here is HM5710, whose description is
      ///    "... File: &lt;path&gt;[CRLF]Script Error: ...", reported when a reload finds a
      ///    script that will not compile. ScriptServer reports it deliberately BEFORE
      ///    installing anything, so that the previous - working - script handles it; and
      ///    the previous script here is the one carrying the OnError handler.
      ///
      ///    Against the unfixed server this fails on the first assertion: nothing is
      ///    ever written to the event log, because the handler is never reached.
      /// </summary>
      [Test]
      public void MultiLineErrorDescriptionShouldStillReachTheOnErrorHandler()
      {
         LogHandler.DeleteEventLog();

         // The script that is in force when the error happens.
         InstallScript(
            "Sub OnError(iSeverity, iCode, sSource, sDescription)\r\n"
            + "   EventLog.Write(\"" + Marker
            + " code=\" & iCode & \" source=\" & sSource & \" description=\" & sDescription)\r\n"
            + "End Sub\r\n");

         // Now hand the server a script which cannot compile. The reload fails, that
         // script is NOT installed, and the failure is reported - which fires OnError in
         // the script above with a description containing a newline.
         var scripting = _settings.Scripting;

         File.WriteAllText(scripting.CurrentScriptFile,
            "Sub OnError(iSeverity, iCode, sSource, sDescription)\r\n"
            + "End Sub\r\n"
            + "\r\n"
            + "Sub ThisWillNotCompile(\r\n");

         scripting.Reload();

         var eventLog = string.Empty;

         RetryHelper.TryAction(TimeSpan.FromSeconds(20), () =>
         {
            eventLog = ReadEventLogOrEmpty();

            if (!eventLog.Contains(Marker))
               throw new Exception(
                  "The OnError handler was never reached, so nothing was written to the event "
                  + "log. The event log contains:\r\n" + eventLog);
         });

         // The right error, not merely any error.
         StringAssert.Contains("source=ScriptServer::LoadScripts", eventLog);

         // And the description arrived whole, flattened onto one line with the same
         // [nl] marker the ERROR log uses, rather than being dropped on the floor.
         StringAssert.Contains("[nl]", eventLog);
         StringAssert.Contains("could not be compiled", eventLog);
      }
   }
}
