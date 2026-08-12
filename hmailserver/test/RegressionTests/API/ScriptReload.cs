// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.IO;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   /// A hot reload of the event handlers either takes effect completely or not at
   /// all.
   ///
   /// LoadScripts used to install the new file text before checking that it
   /// compiled, and the early return it takes when the check fails never touched the
   /// has_on_*_ flags. A reload of a file with a typo in it therefore left the server
   /// holding the new, broken text while still advertising every handler the previous
   /// script had declared. Every one of those events then fired into a script that
   /// could not be parsed, so the handler silently did nothing - including
   /// OnClientLogon and OnClientValidatePassword, which is a security-relevant thing
   /// to get wrong, and OnAcceptMessage, which is where most people put their
   /// anti-spam decisions. Fail-closed only held on a cold start, where there were no
   /// flags set to begin with.
   ///
   /// The rule these tests pin is that the flags and the script text never disagree.
   /// A rejected reload keeps the last known-good script in force in its entirety,
   /// which is the conservative answer for a mail server: a typo should not quietly
   /// switch off a handler on a running server. The last test here is the other half
   /// of that - a reload that does compile must still replace the script, because
   /// "never change anything" would satisfy the tests above and be useless.
   /// </summary>
   [TestFixture]
   public class ScriptReload : TestFixtureBase
   {
      private const string VersionHeader = "X-Script-Version";

      /// A script that stamps a version onto every accepted message, so that which
      /// script is in force can be read off a delivered message.
      private static string AcceptMessageScript(string version)
      {
         return "Sub OnAcceptMessage(oClient, message)\r\n" +
                "   message.HeaderValue(\"" + VersionHeader + "\") = \"" + version + "\"\r\n" +
                "   message.Save()\r\n" +
                "End Sub\r\n";
      }

      /// The same script with the End Sub left off - the shape a reload typo usually
      /// takes. VBScript cannot parse it, so the load must be rejected. It still
      /// mentions OnAcceptMessage, so a delivered message carrying "second" would say
      /// that the broken script had been installed rather than merely advertised.
      private static string BrokenAcceptMessageScript()
      {
         return "Sub OnAcceptMessage(oClient, message)\r\n" +
                "   message.HeaderValue(\"" + VersionHeader + "\") = \"second\"\r\n" +
                "   message.Save()\r\n";
      }

      private static string LogonScript(string version)
      {
         return "Sub OnClientLogon(oClient)\r\n" +
                "   EventLog.Write(\"logon-handler-" + version + "\")\r\n" +
                "End Sub\r\n";
      }

      private static string BrokenLogonScript()
      {
         return "Sub OnClientLogon(oClient)\r\n" +
                "   EventLog.Write(\"logon-handler-second\")\r\n";
      }

      private void WriteScriptAndReload(string script)
      {
         var scripting = _settings.Scripting;
         File.WriteAllText(scripting.CurrentScriptFile, script);
         scripting.Enabled = true;
         scripting.Reload();
      }

      /// Waits for the event log to contain the given text and returns the log either
      /// way. The log is written while the session is still being torn down, so a
      /// single read can lose a race that has nothing to do with what is being tested.
      private static string WaitForEventLogText(string expected)
      {
         var contents = string.Empty;

         for (var i = 0; i < 50; i++)
         {
            contents = ReadEventLogIfPresent();

            if (contents.Contains(expected))
               return contents;

            Thread.Sleep(100);
         }

         return contents;
      }

      /// Reads the event log without insisting that it exists. When the bug is
      /// present the handler never runs, so the log may not have been created at all,
      /// and an assertion about its contents is a clearer failure than one about a
      /// missing file.
      private static string ReadEventLogIfPresent()
      {
         var file = LogHandler.GetEventLogFileName();

         if (!File.Exists(file))
            return string.Empty;

         using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
         using (var reader = new StreamReader(stream))
         {
            return reader.ReadToEnd();
         }
      }

      /// Leaves the event directory holding a script that compiles, so that a
      /// rejected reload in one test cannot be inherited by the next fixture - or by
      /// a cold start of the service.
      private void RestoreEmptyScript()
      {
         var scripting = _settings.Scripting;
         File.WriteAllText(scripting.CurrentScriptFile, string.Empty);
         scripting.Reload();
         scripting.Enabled = false;

         LogHandler.DeleteErrorLog();
      }

      [Test]
      [Description("A reload that fails to compile leaves the previous OnAcceptMessage handler working")]
      public void RejectedReloadKeepsThePreviousAcceptHandlerRunning()
      {
         var before = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "before@example.test", "test");
         var after = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "after@example.test", "test");

         try
         {
            WriteScriptAndReload(AcceptMessageScript("first"));

            // Establish that the good script really is in force, so that the second
            // half of the test is measuring the reload and not a script that never
            // worked.
            SmtpClientSimulator.StaticSend(before.Address, before.Address, "Test", "SampleBody");
            var firstMessage = Pop3ClientSimulator.AssertGetFirstMessageText(before.Address, "test");
            StringAssert.Contains(VersionHeader + ": first", firstMessage);

            // Now the typo. The load must be rejected...
            WriteScriptAndReload(BrokenAcceptMessageScript());
            Assert.IsFalse(string.IsNullOrEmpty(_settings.Scripting.CheckSyntax()),
               "the broken script was expected not to compile");

            // ...and the handler that was working must still be working. Before the
            // fix the flag said OnAcceptMessage existed while the text held was the
            // unparseable file, so the message arrived with no version header at all.
            SmtpClientSimulator.StaticSend(after.Address, after.Address, "Test", "SampleBody");
            var secondMessage = Pop3ClientSimulator.AssertGetFirstMessageText(after.Address, "test");

            StringAssert.Contains(VersionHeader + ": first", secondMessage);

            // Specifically not "second": the rejected script must not have been
            // installed either. Contents and flags have to agree, whichever way.
            StringAssert.DoesNotContain(VersionHeader + ": second", secondMessage);
         }
         finally
         {
            RestoreEmptyScript();
         }
      }

      [Test]
      [Description("A reload that fails to compile leaves the previous OnClientLogon handler working")]
      public void RejectedReloadKeepsTheLogonHandlerRunning()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         try
         {
            WriteScriptAndReload(LogonScript("first"));

            // Prove the handler runs at all, then clear the log so that anything read
            // later can only have been written after the rejected reload.
            Pop3ClientSimulator.AssertMessageCount("test@example.test", "test", 0);
            StringAssert.Contains("logon-handler-first", WaitForEventLogText("logon-handler-first"));
            LogHandler.DeleteEventLog();

            WriteScriptAndReload(BrokenLogonScript());
            Assert.IsFalse(string.IsNullOrEmpty(_settings.Scripting.CheckSyntax()),
               "the broken script was expected not to compile");

            Pop3ClientSimulator.AssertMessageCount("test@example.test", "test", 0);

            // This is the case that matters most. OnClientLogon is one of the events
            // the handler flags gate for security reasons; before the fix the server
            // went on believing the handler was there while the script it would have
            // run could not be parsed, so the handler was skipped without a word.
            StringAssert.Contains("logon-handler-first", WaitForEventLogText("logon-handler-first"));
         }
         finally
         {
            RestoreEmptyScript();
         }
      }

      [Test]
      [Description("A rejected reload is reported to the administrator")]
      public void RejectedReloadIsReported()
      {
         try
         {
            WriteScriptAndReload(AcceptMessageScript("first"));

            LogHandler.DeleteErrorLog();

            WriteScriptAndReload(BrokenAcceptMessageScript());

            // HM5710 says the reload was rejected and the previous script is still
            // running, as opposed to HM5016, which says nothing is loaded at all. An
            // administrator who has just saved a file needs to be told which of those
            // happened; a silent half-load is what the fix removes.
            CustomAsserts.AssertReportedError("HM5710");
         }
         finally
         {
            RestoreEmptyScript();
         }
      }

      [Test]
      [Description("A reload that does compile still replaces the previous script")]
      public void GoodReloadReplacesThePreviousScript()
      {
         var before = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "before@example.test", "test");
         var after = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "after@example.test", "test");

         try
         {
            WriteScriptAndReload(AcceptMessageScript("first"));

            SmtpClientSimulator.StaticSend(before.Address, before.Address, "Test", "SampleBody");
            StringAssert.Contains(VersionHeader + ": first",
               Pop3ClientSimulator.AssertGetFirstMessageText(before.Address, "test"));

            WriteScriptAndReload(AcceptMessageScript("second"));

            // The negative control for the tests above: keeping the old script when a
            // load fails must not turn into keeping the old script when it succeeds.
            SmtpClientSimulator.StaticSend(after.Address, after.Address, "Test", "SampleBody");
            StringAssert.Contains(VersionHeader + ": second",
               Pop3ClientSimulator.AssertGetFirstMessageText(after.Address, "test"));
         }
         finally
         {
            RestoreEmptyScript();
         }
      }
   }
}
