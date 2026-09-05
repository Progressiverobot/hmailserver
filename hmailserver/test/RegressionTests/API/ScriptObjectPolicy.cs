// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    ScriptAllowedObjects (hMailServer.ini, [Settings]) is the allow-list behind
   ///    CreateObject in VBScript and new ActiveXObject in JScript. Absent or "*", a
   ///    script may create any COM class on the machine, as it always could; empty,
   ///    none; otherwise exactly the ProgIDs and CLSIDs listed. A denied class fails
   ///    inside the script with the engine's own "can't create object" error (429),
   ///    which the script can trap, and the server writes one application-log line
   ///    naming the class and the setting. Every assertion here is on what the
   ///    script itself reports through EventLog.Write after trying.
   /// </summary>
   [TestFixture]
   public class ScriptObjectPolicy : TestFixtureBase
   {
      private const string Password = "test";

      // Scripting.FileSystemObject's CLSID, registered on every Windows since 98.
      private const string FileSystemObjectClsid = "{0D43FE01-F093-11CF-8940-00A0C9054228}";

      private const string VbScript =
         @"Sub OnAcceptMessage(oClient, oMessage)
              On Error Resume Next
              Set fso = CreateObject(""Scripting.FileSystemObject"")
              If Err.Number = 0 Then
                 EventLog.Write(""fso created"")
              Else
                 EventLog.Write(""fso denied "" & Err.Number)
              End If
              Err.Clear
              Set shell = CreateObject(""WScript.Shell"")
              If Err.Number = 0 Then
                 EventLog.Write(""shell created"")
              Else
                 EventLog.Write(""shell denied "" & Err.Number)
              End If
           End Sub";

      private const string JScript =
         @"function OnAcceptMessage(oClient, oMessage)
           {
              try
              {
                 var fso = new ActiveXObject('Scripting.FileSystemObject');
                 EventLog.Write('fso created');
              }
              catch (e)
              {
                 EventLog.Write('fso denied ' + (e.number & 0xFFFF));
              }
           }";

      [TearDown]
      public void RestoreTheUnrestrictedDefault()
      {
         IniFileSetting.Delete("ScriptAllowedObjects");
         _application.Reinitialize();

         var scripting = _settings.Scripting;
         File.WriteAllText(scripting.CurrentScriptFile, "");
         scripting.Enabled = false;
         scripting.Reload();
      }

      private void SetPolicy(string value)
      {
         if (value == null)
            IniFileSetting.Delete("ScriptAllowedObjects");
         else
            IniFileSetting.Write("ScriptAllowedObjects", value);
         _application.Reinitialize();
      }

      private void InstallScript(string language, string script)
      {
         LogHandler.DeleteEventLog();
         LogHandler.DeleteCurrentDefaultLog();

         var scripting = _settings.Scripting;
         scripting.Language = language;
         File.WriteAllText(scripting.CurrentScriptFile, script);
         scripting.Enabled = true;
         scripting.Reload();
      }

      // Delivers one message, which fires OnAcceptMessage, and waits until the
      // script has written its last line - "shell ..." for VBScript, "fso ..." for
      // JScript - so the event log is complete before it is read.
      private string FireTheScript(string lastLinePrefix)
      {
         // A fresh account each time: a test may fire the script more than once.
         string address = "policy" + Guid.NewGuid().ToString("N").Substring(0, 8) + "@example.test";
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, Password);
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Policy", "Body text.");
         Pop3ClientSimulator.AssertMessageCount(account.Address, Password, 1);

         string eventLog = "";
         for (int attempt = 0; attempt < 100; attempt++)
         {
            string file = LogHandler.GetEventLogFileName();
            if (File.Exists(file))
            {
               eventLog = File.ReadAllText(file);
               if (eventLog.Contains(lastLinePrefix))
                  return eventLog;
            }
            Thread.Sleep(100);
         }
         Assert.Fail("The script never wrote its '" + lastLinePrefix + "' line. Event log: " + eventLog);
         return eventLog;
      }

      [Test]
      [Description("With ScriptAllowedObjects present and empty, CreateObject fails inside the script with error 429 for every class, the message is still delivered, and the application log names the class and the setting.")]
      public void AnEmptyListDeniesEveryClass()
      {
         SetPolicy("");
         InstallScript("VBScript", VbScript);

         string eventLog = FireTheScript("shell ");
         StringAssert.Contains("fso denied 429", eventLog);
         StringAssert.Contains("shell denied 429", eventLog);
         StringAssert.DoesNotContain("created", eventLog);

         Assert.IsTrue(LogHandler.DefaultLogContains("Script denied: CreateObject of Scripting.FileSystemObject is not in ScriptAllowedObjects"),
            "The denial is logged with the class and the setting that denied it: " + LogHandler.ReadCurrentDefaultLog());
      }

      [Test]
      [Description("A listed ProgID is created; an unlisted one is denied in the same script.")]
      public void TheListNamesWhatMayBeCreated()
      {
         SetPolicy("Scripting.FileSystemObject");
         InstallScript("VBScript", VbScript);

         string eventLog = FireTheScript("shell ");
         StringAssert.Contains("fso created", eventLog);
         StringAssert.Contains("shell denied 429", eventLog);
      }

      [Test]
      [Description("A class may be listed by its CLSID in braces instead of its ProgID; whitespace and case are tolerated.")]
      public void AClsidNamesAClassToo()
      {
         SetPolicy(" " + FileSystemObjectClsid.ToLowerInvariant() + " , wscript.SHELL ");
         InstallScript("VBScript", VbScript);

         string eventLog = FireTheScript("shell ");
         StringAssert.Contains("fso created", eventLog);
         StringAssert.Contains("shell created", eventLog);
      }

      [Test]
      [Description("JScript's new ActiveXObject is held to the same list, and reports the same 429 inside the script.")]
      public void JScriptIsHeldToTheSameList()
      {
         SetPolicy("");
         InstallScript("JScript", JScript);

         string eventLog = FireTheScript("fso ");
         StringAssert.Contains("fso denied 429", eventLog);
      }

      [Test]
      [Description("With the key absent, or set to *, every class may be created - the behaviour every installation had before the setting existed.")]
      public void AbsentOrStarMeansUnrestricted()
      {
         SetPolicy(null);
         InstallScript("VBScript", VbScript);
         string eventLog = FireTheScript("shell ");
         StringAssert.Contains("fso created", eventLog);
         StringAssert.Contains("shell created", eventLog);

         SetPolicy("*");
         InstallScript("VBScript", VbScript);
         eventLog = FireTheScript("shell ");
         StringAssert.Contains("fso created", eventLog);
         StringAssert.Contains("shell created", eventLog);
      }
   }
}
