// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.MIME
{
   /// <summary>
   ///    The MIME parser sees untrusted input on every single message, and until now
   ///    nothing in the suite fed it anything it was not expecting. These are the three
   ///    shapes an attacker controls that the parser handled badly: how deep the
   ///    structure goes, what is inside a transfer-encoded part, and what bytes are in
   ///    a part it will parse a second time.
   ///
   ///    None of them is a malformed-message test for its own sake. Each one ends in a
   ///    concrete loss: the server process, the contents of an attachment, or the name
   ///    the virus scanner writes that attachment under.
   /// </summary>
   [TestFixture]
   public class MalformedInput : TestFixtureBase
   {
      // Well past MimeBody::MaxNestingDepth (20), and chosen to be well past the point
      // where the recursion this replaced exhausts a one-megabyte thread stack. Each
      // level is about fifty bytes, so this is a message of roughly 250 KB - a quarter
      // of the way to nothing, as messages go, and far below the 20 MB the suite
      // configures as the size limit.
      private const int NestingLevels = 5000;

      private static string BuildDeeplyNestedMultipart()
      {
         var message = new StringBuilder();

         message.Append("From: nested@example.test\r\n");
         message.Append("Subject: deeply nested\r\n");
         message.Append("MIME-Version: 1.0\r\n");

         // Each level declares a multipart with its own boundary and then opens that
         // boundary immediately. The part after level N's opening delimiter runs to the
         // end of the message, because level N's boundary never recurs - so the parser
         // hands the whole remainder to a child that is itself a multipart, N times
         // over. That is one recursive MimeBody::Load per level.
         for (int level = 1; level <= NestingLevels; level++)
         {
            message.Append("Content-Type: multipart/mixed; boundary=\"b" + level + "\"\r\n");
            message.Append("\r\n");
            message.Append("--b" + level + "\r\n");
         }

         message.Append("Content-Type: text/plain\r\n");
         message.Append("\r\n");
         message.Append("innermost\r\n");

         return message.ToString();
      }

      [Test]
      [Description("A multipart nested thousands of levels deep must not take the server down. " +
                   "MimeBody::Load recursed once per level with no bound.")]
      public void TestDeeplyNestedMultipartDoesNotExhaustTheStack()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "nested@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, BuildDeeplyNestedMultipart());

         // Delivery is where the parse happens - the rule engine, the trace-header
         // writer and the spam pipeline all build a MessageData from the spooled file -
         // so a message that arrives in the inbox is a message the parser survived.
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         // And it must still be usable afterwards, not merely present. BODYSTRUCTURE
         // walks the parsed tree, so this exercises what the parser actually built
         // rather than only what it wrote to disk.
         var imapSim = new ImapClientSimulator(account.Address, "test", "INBOX");
         var result = imapSim.Fetch("1 BODYSTRUCTURE");
         imapSim.Logout();

         StringAssert.Contains("OK FETCH completed", result,
            "The deeply nested message should still be fetchable over IMAP");

         // The nesting limit is deliberately not an error condition: the parser stops
         // splitting and keeps the rest as content. Nothing should be reported.
         CustomAsserts.AssertNoReportedError();
      }

      [Test]
      [Description("A base64 part containing a byte outside the base64 alphabet must decode " +
                   "in full. MimeCodeBase64::Decode stopped at the first such byte.")]
      public void TestBase64AttachmentWithStrayWhitespaceDecodesInFull()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "base64@example.test", "test");

         // Long enough that a truncation cannot be mistaken for a rounding difference,
         // and plain ASCII so the comparison is over exactly the bytes that went in.
         const string attachmentContent =
            "0123456789-the-quick-brown-fox-jumps-over-the-lazy-dog-0123456789";

         var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(attachmentContent));

         // One space, in the middle of the encoded data, well away from a line start so
         // it cannot be read as folding. RFC 2045 6.8 requires a decoder to ignore it.
         // The unfixed decoder stopped dead here and reported success.
         var corrupted = encoded.Substring(0, 8) + " " + encoded.Substring(8);

         var messageText =
            "From: base64@example.test\r\n" +
            "To: base64@example.test\r\n" +
            "Subject: base64 with a stray space\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"boundary-b64\"\r\n" +
            "\r\n" +
            "--boundary-b64\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "body\r\n" +
            "\r\n" +
            "--boundary-b64\r\n" +
            "Content-Type: application/octet-stream; name=\"payload.txt\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-Disposition: attachment; filename=\"payload.txt\"\r\n" +
            "\r\n" +
            corrupted + "\r\n" +
            "\r\n" +
            "--boundary-b64--\r\n";

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var message = CustomAsserts.AssertRetrieveFirstMessage(account.IMAPFolders.get_ItemByName("INBOX"));
         Assert.AreEqual(1, message.Attachments.Count);
         Assert.AreEqual("payload.txt", message.Attachments[0].Filename);

         var tempFile = Path.GetTempFileName();

         try
         {
            // SaveAs goes to MimeBody::WriteToFile, which is the same decode the virus
            // scanner gets when it is handed an attachment to look at.
            message.Attachments[0].SaveAs(tempFile);

            var saved = File.ReadAllText(tempFile);

            Assert.AreEqual(attachmentContent, saved,
               "The base64 attachment was truncated at the byte outside the alphabet rather than decoded in full");
         }
         finally
         {
            File.Delete(tempFile);
         }
      }

      [Test]
      [Description("A message/rfc822 part whose content carries a NUL byte must still take its " +
                   "filename from the encapsulated Subject, not from uninitialised heap.")]
      public void TestEncapsulatedMessageWithNulByteNamesItselfFromItsSubject()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "encapsulated@example.test", "test");

         // The encapsulated message has no filename or name parameter, so its name comes
         // from MimeBody::GenerateFileNameFromEncapsulatedSubject, which parses the part
         // a second time to read its Subject.
         //
         // The NUL is placed in a header BEFORE that Subject on purpose. The old code
         // copied the part with strncpy_s into an uninitialised buffer, and strncpy_s
         // stops at the first NUL in its source without padding what follows - so
         // everything from the NUL onwards, Subject included, was never copied, and the
         // parser read uninitialised heap in its place. Whatever it found there became
         // the filename: the value shown to COM clients, matched against the
         // blocked-attachment list, and used to name the temp file the virus scanner
         // writes.
         var messageText =
            "From: encapsulated@example.test\r\n" +
            "To: encapsulated@example.test\r\n" +
            "Subject: outer\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"boundary-enc\"\r\n" +
            "\r\n" +
            "--boundary-enc\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "body\r\n" +
            "\r\n" +
            "--boundary-enc\r\n" +
            "Content-Type: message/rfc822\r\n" +
            "Content-Disposition: attachment\r\n" +
            "\r\n" +
            "X-Pad: a\0aaaaaaaa\r\n" +
            "Subject: encapsulated-subject\r\n" +
            "From: inner@example.test\r\n" +
            "\r\n" +
            "inner body\r\n" +
            "\r\n" +
            "--boundary-enc--\r\n";

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var message = CustomAsserts.AssertRetrieveFirstMessage(account.IMAPFolders.get_ItemByName("INBOX"));
         Assert.AreEqual(1, message.Attachments.Count);

         Assert.AreEqual("encapsulated-subject.eml", message.Attachments[0].Filename,
            "The encapsulated part's filename should come from its Subject, which sits after the NUL byte");

         // Read twice. A name that came out of uninitialised memory has no reason to be
         // the same on the second read, and a test that only looks once would call that
         // a pass on whichever run happened to find plausible bytes.
         var secondRead = CustomAsserts.AssertRetrieveFirstMessage(account.IMAPFolders.get_ItemByName("INBOX"));
         Assert.AreEqual(message.Attachments[0].Filename, secondRead.Attachments[0].Filename,
            "The filename of an encapsulated part must be a function of the message, not of what was in the heap");
      }
   }
}
