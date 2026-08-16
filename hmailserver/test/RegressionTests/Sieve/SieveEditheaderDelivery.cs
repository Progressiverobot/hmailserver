// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    editheader (RFC 5293): addheader and deleteheader, asserted on the
   ///    DELIVERED MESSAGE'S BYTES read back over POP3 - the file the user's client
   ///    will download - never on what the evaluator reported. The edits are
   ///    applied by the delivery path rewriting the stored file, which is exactly
   ///    the step that can silently not happen while every summary looks right.
   /// </summary>
   [TestFixture]
   public class SieveEditheaderDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static int accountSequence_;

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    Delivers one message under the given script and returns the delivered
      ///    text as a POP3 client retrieves it.
      /// </summary>
      private string DeliverAndFetch(string script, string extraHeaders = "")
      {
         accountSequence_++;
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-edith-" + accountSequence_ + "@example.test", Password);

         SetScript(recipient, script);

         string raw =
            "From: sieve-edith-sender@example.test\r\n" +
            "To: " + recipient.Address + "\r\n" +
            "Subject: Editheader test\r\n" +
            extraHeaders +
            "\r\n" +
            "Body text.\r\n";

         var smtp = new SmtpClientSimulator();
         smtp.SendRaw("sieve-edith-sender@example.test", recipient.Address, raw);

         return Pop3ClientSimulator.AssertGetFirstMessageText(recipient.Address, Password);
      }

      [Test]
      [Description("addheader inserts the field, at the top of the header by default.")]
      public void AddheaderInsertsAtTheTop()
      {
         string message = DeliverAndFetch(
            "require \"editheader\";\r\n" +
            "addheader \"X-Sieve-Filtered\" \"yes\";\r\n");

         StringAssert.Contains("X-Sieve-Filtered: yes", message,
            "The added header is not in the delivered message.");

         int added = message.IndexOf("X-Sieve-Filtered:");
         int from = message.IndexOf("From:");
         Assert.Less(added, from,
            "addheader without :last must insert at the TOP of the header (RFC 5293 4), above the existing fields.");
      }

      [Test]
      [Description("addheader :last appends below the existing fields instead.")]
      public void AddheaderLastAppendsAtTheBottom()
      {
         string message = DeliverAndFetch(
            "require \"editheader\";\r\n" +
            "addheader :last \"X-Sieve-Filtered\" \"yes\";\r\n");

         int added = message.IndexOf("X-Sieve-Filtered:");
         int subject = message.IndexOf("Subject:");

         Assert.Greater(added, subject,
            "addheader :last must append below the existing fields.");
      }

      [Test]
      [Description("deleteheader removes every instance of the named field.")]
      public void DeleteheaderRemovesTheNamedField()
      {
         string message = DeliverAndFetch(
            "require \"editheader\";\r\n" +
            "deleteheader \"X-Advertisement\";\r\n",
            "X-Advertisement: one\r\nX-Advertisement: two\r\n");

         StringAssert.DoesNotContain("X-Advertisement", message,
            "deleteheader left an instance of the field in the delivered message.");
      }

      /// <summary>
      ///    The negative control: a delete aimed at a value that does not match
      ///    must remove nothing. An implementation that deletes by name alone and
      ///    ignores the patterns passes the removal test and fails here.
      /// </summary>
      [Test]
      [Description("deleteheader with a non-matching value pattern removes nothing - the control the rest depend on.")]
      public void DeleteheaderWithNonMatchingPatternRemovesNothing()
      {
         string message = DeliverAndFetch(
            "require \"editheader\";\r\n" +
            "deleteheader :is \"X-Advertisement\" \"absent-value\";\r\n",
            "X-Advertisement: one\r\n");

         StringAssert.Contains("X-Advertisement: one", message,
            "A value-filtered deleteheader removed a field whose value did not match.");
      }

      [Test]
      [Description("deleteheader :contains removes only the matching instances.")]
      public void DeleteheaderWithPatternIsSelective()
      {
         string message = DeliverAndFetch(
            "require \"editheader\";\r\n" +
            "deleteheader :contains \"X-Advertisement\" \"spam\";\r\n",
            "X-Advertisement: pure spam offer\r\nX-Advertisement: legitimate\r\n");

         StringAssert.DoesNotContain("pure spam offer", message,
            "The matching instance survived a value-filtered deleteheader.");
         StringAssert.Contains("X-Advertisement: legitimate", message,
            "The NON-matching instance was removed - the pattern is not being consulted per instance.");
      }

      [Test]
      [Description("deleteheader :index 2 removes only the second instance.")]
      public void DeleteheaderIndexSelectsOneInstance()
      {
         string message = DeliverAndFetch(
            "require [\"editheader\", \"index\"];\r\n" +
            "deleteheader :index 2 \"X-Route-Hop\";\r\n",
            "X-Route-Hop: first\r\nX-Route-Hop: second\r\n");

         StringAssert.Contains("X-Route-Hop: first", message,
            "':index 2' removed the first instance.");
         StringAssert.DoesNotContain("X-Route-Hop: second", message,
            "':index 2' did not remove the second instance.");
      }

      /// <summary>
      ///    The protection: the trace of how a message travelled is not a script's
      ///    to edit, so a script trying is refused AT UPLOAD - CHECKSCRIPT-style
      ///    validation through the COM syntax checker, since a stored script never
      ///    gets the chance.
      /// </summary>
      [Test]
      [Description("A script editing Received or Return-Path is refused at upload.")]
      public void TraceHeadersAreProtected()
      {
         object utilities = _application.Utilities;

         foreach (string script in new[]
         {
            "require \"editheader\";\r\ndeleteheader \"Received\";",
            "require \"editheader\";\r\naddheader \"Received\" \"forged\";",
            "require \"editheader\";\r\ndeleteheader \"Return-Path\";"
         })
         {
            string error = (string) utilities.GetType().InvokeMember(
               "CheckSieveSyntax", BindingFlags.InvokeMethod, null, utilities, new object[] { script });

            Assert.IsNotEmpty(error,
               "A script editing a trace header was accepted at upload: " + script);
         }
      }
   }
}
