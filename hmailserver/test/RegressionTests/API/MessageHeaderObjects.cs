// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   /// A MessageHeader object handed to a script has to keep referring to the header it
   /// was asked for, for as long as the script holds it.
   ///
   /// It did not. MimeHeader keeps its fields in a std::vector&lt;MimeField&gt; by
   /// value, and InterfaceMessageHeader cached the raw MimeField* it was handed. Adding
   /// a header appends to that vector, and an append that grows it invalidates every
   /// such pointer; deleting one erases the element, which move-assigns each following
   /// field down one slot and destroys the last slot. So after either operation a
   /// MessageHeader object a script was still holding addressed a *different* field -
   /// or a destroyed one.
   ///
   /// The consequence is not just a bad read. A script that wrote through one of those
   /// objects wrote onto a header it had never asked for, in a message that is then
   /// saved to disk, which is a silent corruption of somebody's mail. Both tests below
   /// are arranged so that which field the stale pointer lands on is determined rather
   /// than a matter of luck: the fields are appended in a known order and the one that
   /// is deleted is the one before them.
   ///
   /// The third test is a different defect on the same object. MimeField::SetName does
   /// not set the field's modified flag, and MimeField::Store writes a field's original
   /// raw line verbatim while that flag is clear - so renaming a header read from the
   /// wire was visible to the script that did it and silently absent from the saved
   /// message. That is the "appears to work and does nothing" shape, on a property that
   /// the IDL says is writable.
   /// </summary>
   [TestFixture]
   public class MessageHeaderObjects : TestFixtureBase
   {
      private void WriteScriptAndReload(string script)
      {
         var scripting = _settings.Scripting;
         File.WriteAllText(scripting.CurrentScriptFile, script);
         scripting.Enabled = true;
         scripting.Reload();

         Assert.IsTrue(string.IsNullOrEmpty(scripting.CheckSyntax()),
            "the test's own script was expected to compile: " + scripting.CheckSyntax());
      }

      /// Leaves the event directory holding a script that compiles and scripting off,
      /// so a fixture that runs next does not inherit this one's handlers.
      private void RestoreEmptyScript()
      {
         var scripting = _settings.Scripting;
         File.WriteAllText(scripting.CurrentScriptFile, string.Empty);
         scripting.Reload();
         scripting.Enabled = false;

         LogHandler.DeleteErrorLog();
      }

      [Test]
      [Description("A deleted MessageHeader object cannot write its value onto another header")]
      public void DeletedHeaderObjectCannotWriteOntoAnotherHeader()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "hdrdelete@example.test", "test");

         // X-Probe-A and X-Probe-B are appended in that order, so B sits immediately
         // after A. Deleting A moves B down into A's slot, which is exactly where the
         // object held for A used to point - so a write through it lands on B.
         //
         // On Error Resume Next is used rather than letting the failure surface,
         // because an unhandled script error is written to the server's error log, and
         // an error log that exists at all fails the next fixture's PerformBasicSetup.
         var script =
            "Sub OnAcceptMessage(oClient, oMessage)\r\n" +
            "   oMessage.HeaderValue(\"X-Probe-A\") = \"aaa\"\r\n" +
            "   oMessage.HeaderValue(\"X-Probe-B\") = \"bbb\"\r\n" +
            "\r\n" +
            "   Set hA = oMessage.Headers.ItemByName(\"X-Probe-A\")\r\n" +
            "   hA.Delete\r\n" +
            "\r\n" +
            "   On Error Resume Next\r\n" +
            "   Err.Clear\r\n" +
            "   hA.Value = \"hijacked\"\r\n" +
            "   writeRefused = (Err.Number <> 0)\r\n" +
            "   Err.Clear\r\n" +
            "   On Error Goto 0\r\n" +
            "\r\n" +
            "   oMessage.HeaderValue(\"X-Write-Refused\") = CStr(writeRefused)\r\n" +
            "   oMessage.Save\r\n" +
            "End Sub\r\n";

         try
         {
            WriteScriptAndReload(script);

            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");

            var messageText = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

            // The header the script deleted really is gone - otherwise the rest of the
            // test would be measuring a Delete that never happened.
            StringAssert.DoesNotContain("X-Probe-A", messageText);

            // The header the script never touched still has its own value. Before the
            // fix this read "X-Probe-B: hijacked": the write went through a pointer to
            // a slot that now belonged to B.
            StringAssert.Contains("X-Probe-B: bbb", messageText);
            StringAssert.DoesNotContain("hijacked", messageText);

            // And the write was refused rather than silently redirected, which is what
            // tells the script author their object is finished.
            StringAssert.Contains("X-Write-Refused: True", messageText);
         }
         finally
         {
            RestoreEmptyScript();
         }
      }

      [Test]
      [Description("A MessageHeader object still names its own header after an earlier one is deleted")]
      public void HeaderObjectStillNamesItsOwnHeaderAfterAnEarlierOneIsDeleted()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "hdrshift@example.test", "test");

         // Three probes are appended in order, an object is taken for the middle one,
         // and the first is deleted. The erase shifts B into A's slot and C into B's,
         // so the pointer held for B addresses C afterwards - deterministically, which
         // is what makes the assertion below meaningful rather than a coin toss.
         var script =
            "Sub OnAcceptMessage(oClient, oMessage)\r\n" +
            "   oMessage.HeaderValue(\"X-Probe-A\") = \"aaa\"\r\n" +
            "   oMessage.HeaderValue(\"X-Probe-B\") = \"bbb\"\r\n" +
            "   oMessage.HeaderValue(\"X-Probe-C\") = \"ccc\"\r\n" +
            "\r\n" +
            "   Set hB = oMessage.Headers.ItemByName(\"X-Probe-B\")\r\n" +
            "   Set hA = oMessage.Headers.ItemByName(\"X-Probe-A\")\r\n" +
            "   hA.Delete\r\n" +
            "\r\n" +
            "   On Error Resume Next\r\n" +
            "   Err.Clear\r\n" +
            "   seen = hB.Name\r\n" +
            "   If Err.Number <> 0 Then seen = \"error\"\r\n" +
            "   Err.Clear\r\n" +
            "   On Error Goto 0\r\n" +
            "\r\n" +
            "   oMessage.HeaderValue(\"X-B-Seen-As\") = seen\r\n" +
            "   oMessage.Save\r\n" +
            "End Sub\r\n";

         try
         {
            WriteScriptAndReload(script);

            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");

            var messageText = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

            // Before the fix this said X-Probe-C, because that is what had been moved
            // into the slot the object still pointed at.
            StringAssert.Contains("X-B-Seen-As: X-Probe-B", messageText);
            StringAssert.DoesNotContain("X-B-Seen-As: X-Probe-C", messageText);
         }
         finally
         {
            RestoreEmptyScript();
         }
      }

      [Test]
      [Description("Renaming a header through the COM API reaches the saved message")]
      public void RenamingAHeaderReachesTheSavedMessage()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "hdrrename@example.test", "test");

         var script =
            "Sub OnAcceptMessage(oClient, oMessage)\r\n" +
            "   Set h = oMessage.Headers.ItemByName(\"X-Original-Name\")\r\n" +
            "   h.Name = \"X-Renamed-Header\"\r\n" +
            "   oMessage.Save\r\n" +
            "End Sub\r\n";

         try
         {
            WriteScriptAndReload(script);

            // Sent as raw text on purpose. The defect only shows on a field that came
            // off the wire: such a field carries the original header line it was parsed
            // from, and MimeField::Store writes that line verbatim unless the field is
            // marked modified. A field the script itself created has no such line and
            // so was never affected.
            var raw =
               "From: " + account.Address + "\r\n" +
               "To: " + account.Address + "\r\n" +
               "Date: Wed, 13 Aug 2026 09:00:00 +0000\r\n" +
               "Subject: rename test\r\n" +
               "X-Original-Name: keep-this-value\r\n" +
               "\r\n" +
               "SampleBody\r\n";

            SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, raw);

            var messageText = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

            // Before the fix the saved message still said X-Original-Name, while the
            // script that renamed it could read the new name back quite happily.
            StringAssert.Contains("X-Renamed-Header: keep-this-value", messageText);
            StringAssert.DoesNotContain("X-Original-Name", messageText);
         }
         finally
         {
            RestoreEmptyScript();
         }
      }

      [Test]
      [Description("A header name that would break the message structure is refused")]
      public void AHeaderNameContainingAColonIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "hdrname@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var header = account.Messages[0].Headers.get_ItemByName("Subject");

         // A colon, a space or a control character in a field name produces a message
         // file that no longer parses back into the headers it was given: the colon
         // splits the name, a CR or LF splits one header into two. put_Name wrote
         // whatever it was passed.
         Assert.Throws<COMException>(() => header.Name = "X-Broken: injected");
         Assert.Throws<COMException>(() => header.Name = "X-Broken\r\nX-Injected");
         Assert.Throws<COMException>(() => header.Name = "");

         // The refusals left the field alone.
         Assert.AreEqual("Subject", header.Name);
      }

      [Test]
      [Description("Headers.Item refuses an index outside the collection")]
      public void ItemRefusesAnIndexOutsideTheCollection()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "hdrindex@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var headers = account.Messages[0].Headers;

         Assert.Greater(headers.Count, 0);

         // A guard rail rather than a reproduction, and worth saying so: this passes
         // against the unfixed server too, because MimeHeader::GetField(unsigned) does
         // reject an index that is out of range whenever there is at least one field.
         // What it cannot reject is an index into a header with *no* fields - it tests
         // "iIndex <= fields_.size() - 1", and that subtraction underflows to SIZE_MAX
         // on an empty vector, so every index passes and the address of element [Index]
         // of an empty vector is returned. Reaching a zero-field header needs a message
         // over the 80 MB load limit or one whose MIME parse threw, neither of which
         // this suite can arrange cheaply. The bound is now enforced by
         // InterfaceMessageHeaders::get_Item, on the count, where it does not depend on
         // that arithmetic; these assertions pin the boundary it now enforces.
         Assert.Throws<COMException>(() => { var unused = headers[headers.Count]; });
         Assert.Throws<COMException>(() => { var unused = headers[-1]; });
      }
   }
}
