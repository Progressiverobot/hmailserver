// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   /// COM properties that used to accept, or answer, something they should not.
   ///
   /// Three shapes are covered, in the order the audit ranks them.
   ///
   /// Scripting.Language accepted any string at all and reported success. The script
   /// engine knows exactly two language names; anything else gives an empty file
   /// extension, so the file the server looks for is "EventHandlers." with nothing after
   /// the dot, nothing is found, and every event handler stops firing - including
   /// OnClientLogon and OnClientValidatePassword. A mistyped language switched an
   /// operator's anti-spam and logon handlers off in silence. The case of the name
   /// matters for the same reason: ScriptServer compares it with a case-sensitive ==.
   ///
   /// AntiVirus.Action ran a switch over its enum with no default and an uninitialised
   /// local, so a value outside the enum - and a scripting language passes this property
   /// an ordinary integer - stored whatever was in that stack slot as the action taken on
   /// a virus-bearing message.
   ///
   /// Result and Client are registered coclasses. Both can be created directly rather
   /// than received from the script engine, and every accessor on them dereferenced an
   /// empty shared_ptr. The server builds with /EHa, so catch (...) caught the access
   /// violation and returned a COM error - which is why nobody noticed. The crash oracle
   /// does notice, and TearDown fails on it, so those two tests are the ones that fail
   /// loudest against the unfixed server.
   /// </summary>
   [TestFixture]
   public class ComPropertyValidation : TestFixtureBase
   {
      private const string StampHeader = "X-Handler-Ran";

      private static string StampingScript()
      {
         return "Sub OnAcceptMessage(oClient, oMessage)\r\n" +
                "   oMessage.HeaderValue(\"" + StampHeader + "\") = \"yes\"\r\n" +
                "   oMessage.Save\r\n" +
                "End Sub\r\n";
      }

      /// Restores the language first and the script second: if the language under test
      /// were left in place, CurrentScriptFile would name a different file and the empty
      /// script would be written somewhere harmless instead of over the one in force.
      private void RestoreScriptingDefaults(string originalLanguage)
      {
         var scripting = _settings.Scripting;

         scripting.Language = originalLanguage;

         File.WriteAllText(scripting.CurrentScriptFile, string.Empty);
         scripting.Reload();
         scripting.Enabled = false;

         LogHandler.DeleteErrorLog();
      }

      [Test]
      [Description("Scripting.Language refuses a language the engine cannot run")]
      public void AnUnsupportedScriptLanguageIsRefused()
      {
         var scripting = _settings.Scripting;
         var originalLanguage = scripting.Language;

         try
         {
            Assert.Throws<COMException>(() => scripting.Language = "Perl");
            Assert.Throws<COMException>(() => scripting.Language = "");
            Assert.Throws<COMException>(() => scripting.Language = "VBScript2");

            // The refusals left the setting alone. Before the fix each of these was
            // stored and S_OK returned.
            Assert.AreEqual(originalLanguage, scripting.Language);
         }
         finally
         {
            scripting.Language = originalLanguage;
         }
      }

      [Test]
      [Description("A language name in the wrong case is stored in the spelling the engine compares against")]
      public void ALanguageNameInTheWrongCaseIsStoredCanonically()
      {
         var scripting = _settings.Scripting;
         var originalLanguage = scripting.Language;

         try
         {
            scripting.Language = "vbscript";

            // ScriptServer::GetScriptExtension_ and ScriptServer::FireEvent both compare
            // the stored language with String::operator==, which is case-sensitive. So
            // storing "vbscript" as the caller typed it leaves scripting exactly as dead
            // as storing "Perl" would - no extension, no file, no handlers - which is why
            // this is canonicalised rather than merely accepted.
            Assert.AreEqual("VBScript", scripting.Language);

            scripting.Language = "jscript";
            Assert.AreEqual("JScript", scripting.Language);
         }
         finally
         {
            scripting.Language = originalLanguage;
         }
      }

      [Test]
      [Description("An attempt to set an unsupported script language does not stop the event handlers firing")]
      public void AnUnsupportedScriptLanguageDoesNotSilenceTheEventHandlers()
      {
         var before = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "langbefore@example.test", "test");
         var after = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "langafter@example.test", "test");

         var scripting = _settings.Scripting;
         var originalLanguage = scripting.Language;

         try
         {
            File.WriteAllText(scripting.CurrentScriptFile, StampingScript());
            scripting.Enabled = true;
            scripting.Reload();

            // Establish that the handler runs at all, so the second half is measuring
            // the language change and not a script that never worked.
            SmtpClientSimulator.StaticSend(before.Address, before.Address, "Test", "SampleBody");
            StringAssert.Contains(StampHeader + ": yes",
               Pop3ClientSimulator.AssertGetFirstMessageText(before.Address, "test"));

            try
            {
               scripting.Language = "Perl";
            }
            catch (COMException)
            {
               // Expected once the setter validates. Swallowed here because this test is
               // about the consequence for the handlers, not about the refusal itself -
               // AnUnsupportedScriptLanguageIsRefused pins that.
            }

            scripting.Reload();

            // This is the half that fails against the unfixed server: "Perl" was stored,
            // the script file the server looked for was "EventHandlers." with no
            // extension, nothing loaded, and the message arrived with no stamp on it.
            SmtpClientSimulator.StaticSend(after.Address, after.Address, "Test", "SampleBody");
            StringAssert.Contains(StampHeader + ": yes",
               Pop3ClientSimulator.AssertGetFirstMessageText(after.Address, "test"));
         }
         finally
         {
            RestoreScriptingDefaults(originalLanguage);
         }
      }

      [Test]
      [Description("AntiVirus.Action refuses a value outside the enum instead of storing a stack value")]
      public void AnOutOfRangeAntivirusActionIsRefused()
      {
         var antiVirus = _settings.AntiVirus;
         var originalAction = antiVirus.Action;

         try
         {
            // A script or any other late-bound caller passes this property a plain
            // integer; nothing on the wire constrains it to the enum. The switch had no
            // default and iAction was uninitialised, so this used to store whatever was
            // on the stack as the action taken when a virus is found - which could be
            // neither of the two the server knows how to perform.
            Assert.Throws<COMException>(() => antiVirus.Action = (eAntivirusAction) 2);
            Assert.Throws<COMException>(() => antiVirus.Action = (eAntivirusAction) (-1));
            Assert.Throws<COMException>(() => antiVirus.Action = (eAntivirusAction) 99);

            Assert.AreEqual(originalAction, antiVirus.Action);

            // The negative control: the two values that are real must still be settable,
            // or "validated" would just mean "broken".
            antiVirus.Action = eAntivirusAction.hDeleteAttachments;
            Assert.AreEqual(eAntivirusAction.hDeleteAttachments, antiVirus.Action);

            antiVirus.Action = eAntivirusAction.hDeleteEmail;
            Assert.AreEqual(eAntivirusAction.hDeleteEmail, antiVirus.Action);
         }
         finally
         {
            antiVirus.Action = originalAction;
         }
      }

      [Test]
      [Description("A Result object created directly is refused without a memory-safety fault")]
      public void ResultObjectCreatedDirectlyIsRefused()
      {
         var result = new hMailServer.Result();

         // hMailServer.Result has its own ProgID and a LocalServer32 registration, so any
         // COM caller - and any event script, which reaches the object model through
         // CreateObject - can make one. It has no Result attached, and every accessor
         // dereferenced the empty shared_ptr.
         //
         // Against the unfixed server this test also fails in TearDown rather than here:
         // the accessor's catch (...) turns the access violation into the same COMException
         // the fix returns deliberately, but the crash oracle's vectored handler has
         // already recorded the fault, and CrashOracleAsserts.AssertNoMemorySafetyEvents
         // fails on the record.
         Assert.Throws<COMException>(() => { var unused = result.Value; });
         Assert.Throws<COMException>(() => { var unused = result.Parameter; });
         Assert.Throws<COMException>(() => { var unused = result.Message; });
         Assert.Throws<COMException>(() => result.Value = 1);
         Assert.Throws<COMException>(() => result.Parameter = 1);
         Assert.Throws<COMException>(() => result.Message = "x");
      }

      [Test]
      [Description("A Client object created directly is refused without a memory-safety fault")]
      public void ClientObjectCreatedDirectlyIsRefused()
      {
         var client = new hMailServer.Client();

         // Same defect and same oracle as ResultObjectCreatedDirectlyIsRefused; the
         // Client object is the one a script uses to read the peer address, so it is the
         // more likely of the two to be created by mistake.
         Assert.Throws<COMException>(() => { var unused = client.IPAddress; });
         Assert.Throws<COMException>(() => { var unused = client.Port; });
         Assert.Throws<COMException>(() => { var unused = client.Username; });
         Assert.Throws<COMException>(() => { var unused = client.HELO; });
         Assert.Throws<COMException>(() => { var unused = client.Authenticated; });
         Assert.Throws<COMException>(() => { var unused = client.EncryptedConnection; });
         Assert.Throws<COMException>(() => { var unused = client.CipherVersion; });
         Assert.Throws<COMException>(() => { var unused = client.CipherName; });
         Assert.Throws<COMException>(() => { var unused = client.CipherBits; });
         Assert.Throws<COMException>(() => { var unused = client.SessionID; });
      }

      [Test]
      [Description("Obsolete boolean properties answer with a value rather than leaving the caller's stack alone")]
      public void ObsoleteBooleanPropertiesAnswerWithAValue()
      {
         // Worth being straight about what this test can and cannot prove. Four getters -
         // Logging.MaskPasswordsInLog and SecurityRange.RequireAuthForDeliveryToLocal,
         // RequireAuthForDeliveryToRemote and IsForwardingRelay - returned S_OK without
         // ever writing their out parameter, so a caller read back whatever the interop
         // stub happened to leave in that stack slot. That is a COM contract violation
         // and the values below are now determinate; but an uninitialised slot can hold
         // the right answer by chance, so this fixture may pass against the unfixed
         // server. It is here to pin the contract, not to reproduce the defect.

         // Masking is unconditional, which is what the IDL help string says, so the only
         // honest answer is yes.
         Assert.IsTrue(_settings.Logging.MaskPasswordsInLog);

         var range = _settings.SecurityRanges.get_ItemByName("My computer");

         Assert.IsNotNull(range, "the default 'My computer' range is expected to exist");

         // Replaced in 5.1 by the four RequireSMTPAuth* properties. These two are now
         // answered from those rather than left unanswered, so they agree with what the
         // range actually enforces.
         Assert.AreEqual(range.RequireSMTPAuthLocalToLocal || range.RequireSMTPAuthExternalToLocal,
            range.RequireAuthForDeliveryToLocal);

         Assert.AreEqual(range.RequireSMTPAuthLocalToExternal || range.RequireSMTPAuthExternalToExternal,
            range.RequireAuthForDeliveryToRemote);

         // Nothing in a range expresses this any more, so it is flatly false.
         Assert.IsFalse(range.IsForwardingRelay);
      }
   }
}
