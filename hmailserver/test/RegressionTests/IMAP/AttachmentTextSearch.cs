// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    SEARCH BODY and SEARCH TEXT read the text-bearing attachments as well as
   ///    the message body, and the full-text index tokenises them too, so a phrase
   ///    that exists only inside an attached text, HTML or CSV file is found - and
   ///    found identically with the index on and off, which is the property the
   ///    index is held to. A binary attachment that happens to contain the bytes
   ///    is not searched: the search is for text a person could read, not for
   ///    byte patterns in an executable.
   ///
   ///    Every attachment here is built by hand as a MIME message rather than
   ///    through a helper, so that the transfer encoding is the one under test:
   ///    base64 for the file the way every real client sends it, 7bit for the
   ///    control, and a CSV under text/csv to show the rule is "any text type".
   /// </summary>
   [TestFixture]
   public class AttachmentTextSearch : TestFixtureBase
   {
      private const string Password = "test";

      [TearDown]
      public void TearDownIndex()
      {
         IniFileSetting.Delete("IndexerFullText");
         _application.Reinitialize();
         _settings.MessageIndexing.Enabled = false;
      }

      private static string Base64Lines(string text)
      {
         string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
         var lines = new StringBuilder();
         for (int i = 0; i < encoded.Length; i += 76)
            lines.Append(encoded.Substring(i, Math.Min(76, encoded.Length - i))).Append("\r\n");
         return lines.ToString();
      }

      // A multipart/mixed message with a short body and one attachment of the given type.
      private static string MessageWithAttachment(string from, string to, string subject, string body,
         string contentType, string fileName, string transferEncoding, string encodedContent)
      {
         return
            "From: " + from + "\r\n" +
            "To: " + to + "\r\n" +
            "Subject: " + subject + "\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"attachment-text-search\"\r\n" +
            "\r\n" +
            "--attachment-text-search\r\n" +
            "Content-Type: text/plain; charset=us-ascii\r\n" +
            "\r\n" +
            body + "\r\n" +
            "--attachment-text-search\r\n" +
            "Content-Type: " + contentType + "; name=\"" + fileName + "\"\r\n" +
            "Content-Disposition: attachment; filename=\"" + fileName + "\"\r\n" +
            "Content-Transfer-Encoding: " + transferEncoding + "\r\n" +
            "\r\n" +
            encodedContent +
            "--attachment-text-search--\r\n";
      }

      private Account DeliverTheFourMessages()
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "attachsearch@example.test", Password);

         // 1: a base64 text/plain attachment carrying the needle; the body does not.
         SmtpClientSimulator.StaticSendRaw("sender@example.test", account.Address,
            MessageWithAttachment("sender@example.test", account.Address, "Minutes", "Please find the minutes attached.",
               "text/plain", "minutes.txt", "base64",
               Base64Lines("Action items\r\nThe quarterly figure is BorealisNeedleOne and it is final.\r\n")));

         // 2: a 7bit text/csv attachment carrying a different needle.
         SmtpClientSimulator.StaticSendRaw("sender@example.test", account.Address,
            MessageWithAttachment("sender@example.test", account.Address, "Export", "The export is attached.",
               "text/csv", "export.csv", "7bit",
               "id,name,note\r\n1,Widget,BorealisNeedleTwo\r\n"));

         // 3: a base64 text/html attachment; the needle sits inside markup.
         SmtpClientSimulator.StaticSendRaw("sender@example.test", account.Address,
            MessageWithAttachment("sender@example.test", account.Address, "Report", "The report is attached.",
               "text/html", "report.html", "base64",
               Base64Lines("<html><body><p>Total: <b>BorealisNeedleThree</b></p></body></html>")));

         // 4: a binary attachment (application/octet-stream) whose bytes contain the
         // fourth needle. Not text, so not searched.
         SmtpClientSimulator.StaticSendRaw("sender@example.test", account.Address,
            MessageWithAttachment("sender@example.test", account.Address, "Binary", "A binary file is attached.",
               "application/octet-stream", "blob.bin", "base64",
               Base64Lines("BorealisNeedleFour")));

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         Pop3ClientSimulator.AssertMessageCount(account.Address, Password, 4);
         return account;
      }

      private static string Search(string address, string query)
      {
         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(address, Password));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));
         string result = simulator.Search(query);
         simulator.Close();
         return result;
      }

      private void WaitForIndexer()
      {
         var indexing = _settings.MessageIndexing;
         indexing.Index();
         for (var i = 0; i < 1000; i++)
         {
            if (indexing.TotalIndexedCount == indexing.TotalMessageCount)
            {
               indexing.Index();
               return;
            }
            Thread.Sleep(20);
         }
         Assert.Fail("The message indexer did not catch up. Message count: " + indexing.TotalMessageCount +
                     ", indexed count: " + indexing.TotalIndexedCount);
      }

      private static void AssertTheSearchesAnswerTheSame(string address)
      {
         Assert.AreEqual("1", Search(address, "BODY \"BorealisNeedleOne\""),
            "A phrase inside a base64 text/plain attachment must be found by SEARCH BODY.");
         Assert.AreEqual("2", Search(address, "BODY \"BorealisNeedleTwo\""),
            "A phrase inside a 7bit text/csv attachment must be found by SEARCH BODY.");
         Assert.AreEqual("3", Search(address, "TEXT \"BorealisNeedleThree\""),
            "A phrase inside a base64 text/html attachment must be found by SEARCH TEXT.");
         Assert.AreEqual("", Search(address, "BODY \"BorealisNeedleFour\""),
            "Bytes inside a binary attachment are not text and must not be searched.");
         Assert.AreEqual("1", Search(address, "BODY \"minutes attached\""),
            "The body itself is still searched.");
         Assert.AreEqual("1", Search(address, "BODY \"quarterly figure\""),
            "The whole decoded attachment is searched, not only a token in it.");
      }

      [Test]
      [Description("SEARCH BODY and TEXT find phrases that exist only inside text, CSV and HTML attachments, decoded from base64 and 7bit, and not inside a binary one - with the index off.")]
      public void AttachmentTextIsSearchedWithTheIndexOff()
      {
         Account account = DeliverTheFourMessages();
         AssertTheSearchesAnswerTheSame(account.Address);
      }

      /// <summary>
      ///    The index may only ever exclude a message. If it did not tokenise the
      ///    attachments, a search for a phrase that exists only inside one would be
      ///    excluded by the index before the scan could find it - which is exactly
      ///    what the roadmap row called "invisible in a way that is correct rather
      ///    than wrong". With the attachments indexed, the answers with the index on
      ///    are the same as with it off.
      /// </summary>
      [Test]
      [Description("With the full-text index on, the same searches give the same answers, because the index tokenises the attachments too.")]
      public void AttachmentTextIsIndexedSoTheIndexDoesNotHideIt()
      {
         Account account = DeliverTheFourMessages();

         IniFileSetting.Write("IndexerFullText", "1");
         _application.Reinitialize();
         _settings.MessageIndexing.Enabled = true;
         WaitForIndexer();

         AssertTheSearchesAnswerTheSame(account.Address);
      }
   }
}
