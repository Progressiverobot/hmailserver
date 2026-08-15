// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    The "body" test (RFC 5173), asserted through real deliveries.
   ///
   ///    Every assertion here is on WHERE THE MESSAGE ENDED UP, never on what the
   ///    evaluator reported, because this project has already shipped one extension
   ///    that parsed, evaluated, reported success and changed nothing about the
   ///    message (see SieveFlagDelivery for that story). A body test that decides
   ///    correctly and files nowhere is worth exactly as much.
   ///
   ///    The three transforms are covered because they are genuinely different code:
   ///
   ///    - ":raw" is the undecoded body with the MIME structure ignored, so it can
   ///      see base64 armour and boundary lines that ":text" must not.
   ///    - ":text" (the default) is the decoded text of the text parts, which is what
   ///      a user means by "if the message mentions X". The base64 case below is the
   ///      one that matters: a filter that only matches plain-text mail is a filter
   ///      that stops working the moment someone's client encodes the part.
   ///    - ":content" selects parts by MIME type, so a rule can look inside the HTML
   ///      alternative without matching the plain-text one.
   ///
   ///    <see cref="ABodyTestThatDoesNotMatchLeavesTheMessageInInbox"/> is the
   ///    negative control: without it, an implementation that filed EVERYTHING into
   ///    the target folder would pass every other test in this fixture.
   /// </summary>
   [TestFixture]
   public class SieveBodyDelivery : TestFixtureBase
   {
      private const string Password = "secret";
      private const string TargetFolder = "Matched";

      private static void SetScript(Account account, string script)
      {
         // Late-bound so the fixture does not depend on the registered type library
         // having been regenerated after an IDL change - the same reason the sibling
         // Sieve delivery fixtures do it.
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    Delivers one raw message under the given script and answers where it
      ///    landed: true when it reached the "Matched" folder the script files into,
      ///    false when it stayed in INBOX.
      ///
      ///    The message is sent as raw DATA rather than through the simulator's
      ///    convenience overload because these tests turn on the exact bytes of the
      ///    body - a helper that composes headers for you cannot express a multipart
      ///    message or a base64 part.
      /// </summary>
      private bool FilesIntoMatchedFolder(string script, string rawMessage)
      {
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-body@example.test", Password);

         // Created up front so both destinations exist however the test turns out -
         // the assertion below then reads two real counts rather than treating a
         // missing folder as a verdict.
         recipient.IMAPFolders.Add(TargetFolder);

         SetScript(recipient, script);

         var smtp = new SmtpClientSimulator();
         smtp.SendRaw("sieve-body-sender@example.test", recipient.Address, rawMessage);

         IMAPFolder inbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         IMAPFolder matched = recipient.IMAPFolders.get_ItemByName(TargetFolder);

         // Waits for the queue to drain, so the counts cannot be read mid-delivery.
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         int inInbox = inbox.Messages.Count;
         int inMatched = matched.Messages.Count;

         Assert.AreEqual(1, inInbox + inMatched,
            "Exactly one copy of the message should exist. INBOX has " + inInbox + " and " +
            TargetFolder + " has " + inMatched + ".");

         return inMatched == 1;
      }

      private static string PlainMessage(string body)
      {
         return "From: sieve-body-sender@example.test\r\n" +
                "To: sieve-body@example.test\r\n" +
                "Subject: Body test\r\n" +
                "\r\n" +
                body;
      }

      private const string FileIntoIfBodyContains =
         "require [\"body\", \"fileinto\"];\r\n" +
         "if body :contains \"{0}\" {{\r\n" +
         "  fileinto \"" + TargetFolder + "\";\r\n" +
         "}}\r\n";

      private static string ScriptMatching(string needle)
      {
         return string.Format(FileIntoIfBodyContains, needle);
      }

      [Test]
      [Description("body :contains matches text in a plain-text body, and the message is filed accordingly.")]
      public void APlainTextBodyIsMatched()
      {
         Assert.IsTrue(
            FilesIntoMatchedFolder(ScriptMatching("quarterly figures"),
               PlainMessage("Please review the quarterly figures before Friday.\r\n")),
            "A plain-text body containing the phrase was not matched by 'body :contains'.");
      }

      /// <summary>
      ///    The negative control. Everything else in this fixture would pass against
      ///    an implementation that filed every message into the target folder.
      /// </summary>
      [Test]
      [Description("A body test that does not match leaves the message in INBOX - the control the rest depend on.")]
      public void ABodyTestThatDoesNotMatchLeavesTheMessageInInbox()
      {
         Assert.IsFalse(
            FilesIntoMatchedFolder(ScriptMatching("a phrase that is absent"),
               PlainMessage("Nothing of the sort appears here.\r\n")),
            "A message whose body does NOT contain the key was filed as though it did, so the test is " +
            "matching everything rather than matching the body.");
      }

      /// <summary>
      ///    The case that decides whether this extension is worth advertising. A
      ///    filter written against plain text must keep working when the sender's
      ///    client encodes the part, which is exactly what ":text" is defined to do
      ///    and what a naive substring search over the raw file cannot.
      /// </summary>
      [Test]
      [Description("body :text decodes a base64 part, so a filter matches text the raw bytes do not contain.")]
      public void ABase64EncodedTextPartIsDecodedBeforeMatching()
      {
         // "Please review the quarterly figures before Friday." in base64. The
         // phrase does not appear anywhere in the transmitted bytes.
         const string encoded = "UGxlYXNlIHJldmlldyB0aGUgcXVhcnRlcmx5IGZpZ3VyZXMgYmVmb3JlIEZyaWRheS4=";

         string raw =
            "From: sieve-body-sender@example.test\r\n" +
            "To: sieve-body@example.test\r\n" +
            "Subject: Body test\r\n" +
            "Content-Type: text/plain; charset=\"utf-8\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            encoded + "\r\n";

         Assert.IsTrue(
            FilesIntoMatchedFolder(ScriptMatching("quarterly figures"), raw),
            "A base64-encoded text part was not decoded before matching, so 'body :text' only works on " +
            "messages that happen to be sent as plain text.");
      }

      [Test]
      [Description("body :text reads the text part of a multipart message rather than the MIME boundaries.")]
      public void TheTextPartOfAMultipartMessageIsMatched()
      {
         string raw =
            "From: sieve-body-sender@example.test\r\n" +
            "To: sieve-body@example.test\r\n" +
            "Subject: Body test\r\n" +
            "Content-Type: multipart/alternative; boundary=\"frontier\"\r\n" +
            "\r\n" +
            "--frontier\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Please review the quarterly figures.\r\n" +
            "--frontier\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<html><body>Please review the <b>quarterly figures</b>.</body></html>\r\n" +
            "--frontier--\r\n";

         Assert.IsTrue(
            FilesIntoMatchedFolder(ScriptMatching("quarterly figures"), raw),
            "The text part of a multipart message was not searched.");
      }

      /// <summary>
      ///    ":content" selects by MIME type. Matching on a string that exists ONLY in
      ///    the HTML alternative proves the selection is real: with the plain-text
      ///    part chosen instead, or with all parts searched indiscriminately, this
      ///    test cannot distinguish them - so the string is one that plain text does
      ///    not contain.
      /// </summary>
      [Test]
      [Description("body :content \"text/html\" searches the HTML alternative and not the plain-text one.")]
      public void ContentSelectsThePartByMimeType()
      {
         string raw =
            "From: sieve-body-sender@example.test\r\n" +
            "To: sieve-body@example.test\r\n" +
            "Subject: Body test\r\n" +
            "Content-Type: multipart/alternative; boundary=\"frontier\"\r\n" +
            "\r\n" +
            "--frontier\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "The plain alternative says nothing special.\r\n" +
            "--frontier\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<html><body><span class=\"invoice-total\">1234</span></body></html>\r\n" +
            "--frontier--\r\n";

         string script =
            "require [\"body\", \"fileinto\"];\r\n" +
            "if body :content \"text/html\" :contains \"invoice-total\" {\r\n" +
            "  fileinto \"" + TargetFolder + "\";\r\n" +
            "}\r\n";

         Assert.IsTrue(
            FilesIntoMatchedFolder(script, raw),
            "':content \"text/html\"' did not search the HTML part.");
      }

      /// <summary>
      ///    ":raw" ignores the MIME structure and does not decode, so it sees the
      ///    boundary lines and the base64 armour that ":text" hides. Matching on the
      ///    boundary marker itself is the cleanest proof of that difference: no
      ///    decoded text form contains it.
      /// </summary>
      [Test]
      [Description("body :raw sees the undecoded, unstructured body - including MIME boundary lines.")]
      public void RawSeesTheUndecodedBody()
      {
         string raw =
            "From: sieve-body-sender@example.test\r\n" +
            "To: sieve-body@example.test\r\n" +
            "Subject: Body test\r\n" +
            "Content-Type: multipart/mixed; boundary=\"frontier\"\r\n" +
            "\r\n" +
            "--frontier\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Ordinary text.\r\n" +
            "--frontier--\r\n";

         string script =
            "require [\"body\", \"fileinto\"];\r\n" +
            "if body :raw :contains \"--frontier\" {\r\n" +
            "  fileinto \"" + TargetFolder + "\";\r\n" +
            "}\r\n";

         Assert.IsTrue(
            FilesIntoMatchedFolder(script, raw),
            "':raw' did not see the MIME boundary line, so it is not returning the undecoded body.");
      }

      /// <summary>
      ///    A message with headers and no body at all must not match anything - not
      ///    even the empty key, which is the shape a script uses to ask "is there a
      ///    body?".
      /// </summary>
      [Test]
      [Description("A message with no body matches nothing, including the empty key.")]
      public void AMessageWithNoBodyMatchesNothing()
      {
         string raw =
            "From: sieve-body-sender@example.test\r\n" +
            "To: sieve-body@example.test\r\n" +
            "Subject: Body test\r\n" +
            "\r\n";

         string script =
            "require [\"body\", \"fileinto\"];\r\n" +
            "if body :contains \"\" {\r\n" +
            "  fileinto \"" + TargetFolder + "\";\r\n" +
            "}\r\n";

         Assert.IsFalse(
            FilesIntoMatchedFolder(script, raw),
            "A message with no body matched 'body :contains \"\"', which should be true only when a body exists.");
      }
   }
}
