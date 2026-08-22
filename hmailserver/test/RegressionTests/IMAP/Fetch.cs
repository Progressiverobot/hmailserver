// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   public class Fetch : TestFixtureBase
   {
      [Test]
      [Description("Issue 218, IMAP: Problem with file name containing non-latin chars")]
      public void TestBodyStructureWithNonLatinCharacter()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var attachmentName = "本本本.zip";

         var filename = Path.Combine(Path.GetTempPath(), attachmentName);
         File.WriteAllText(filename, "tjena moss");

         var message = new Message();
         message.Charset = "utf-8";
         message.AddRecipient("test", account.Address);
         message.From = "Test";
         message.FromAddress = account.Address;
         message.Body = "hejsan";
         message.Attachments.Add(filename);
         message.Save();

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         // utf-8 representation of 本本本.zip:
         Assert.IsTrue(result.Contains("=?utf-8?B?5pys5pys5pys?=.zip"));
      }

      [Test]
      public void TestFetch()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody1");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody2");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 2);

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody3");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 3);


         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");
         var result = sim.Fetch("1 BODY[1]");
         Assert.IsTrue(result.Contains("SampleBody1"), result);
         result = sim.Fetch("2 BODY[1]");
         Assert.IsTrue(result.Contains("SampleBody2"), result);
         result = sim.Fetch("3 BODY[1]");
         Assert.IsTrue(result.Contains("SampleBody3"), result);
      }

      [Test]
      [Description("Issue 293, IMAP: bodystructure is sent instead of body")]
      public void TestFetchBody()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var attachmentName = "本本本.zip";

         var filename = Path.Combine(Path.GetTempPath(), attachmentName);
         File.WriteAllText(filename, "tjena moss");

         var message = new Message();
         message.Charset = "utf-8";
         message.AddRecipient("test", account.Address);
         message.From = "Test";
         message.FromAddress = account.Address;
         message.Body = "hejsan";
         message.Attachments.Add(filename);
         message.Save();

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var bodyStructureResponse = simulator.Fetch("1 BODYSTRUCTURE");
         var bodyResponse = simulator.Fetch("1 BODY");
         simulator.Disconnect();

         Assert.IsTrue(bodyStructureResponse.Contains("BOUNDARY"));
         Assert.IsFalse(bodyResponse.Contains("BOUNDARY"));
      }

      [Test]
      [Description("Issue 209, Date containing \" doesn't show up in OE")]
      public void TestFetchEnvelopeWithDateContainingQuote()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");

         var message = "From: Someone <someone@example.com>" + Environment.NewLine +
                       "To: Someoen <someone@example.com>" + Environment.NewLine +
                       "Date: Wed, 22 Apr 2009 11:05:09 \"GMT\"" + Environment.NewLine +
                       "Subject: Something" + Environment.NewLine +
                       Environment.NewLine +
                       "Hello" + Environment.NewLine;

         var smtpSimulator = new SmtpClientSimulator();
         smtpSimulator.SendRaw(account.Address, account.Address, message);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 ENVELOPE");
         simulator.Disconnect();

         Assert.IsTrue(result.Contains("Wed, 22 Apr 2009 11:05:09 GMT"));
      }

      [Test]
      public void IfInReplyToFieldContainsQuoteThenFetchHeadersShouldEncodeIt()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");

         var message = "From: Someone <someone@example.com>" + Environment.NewLine +
                       "To: Someoen <someone@example.com>" + Environment.NewLine +
                       "In-Reply-To: ShouldBeEncodedDueToQuote\"" + Environment.NewLine +
                       "Subject: Something" + Environment.NewLine +
                       Environment.NewLine +
                       "Hello" + Environment.NewLine;

         var smtpSimulator = new SmtpClientSimulator();
         smtpSimulator.SendRaw(account.Address, account.Address, message);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 ENVELOPE");
         simulator.Disconnect();

         Assert.IsFalse(result.Contains("ShouldBeEncodedDueToQuote"));
      }


      [Test]
      [Description("Issue 282, hMailServer not working with Symbian N60 ")]
      public void TestFetchHeaderFields()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");

         var message = "From: Someone <someone@example.com>" + Environment.NewLine +
                       "To: Someoen <someone@example.com>" + Environment.NewLine +
                       "Date: Wed, 22 Apr 2009 11:05:09 \"GMT\"" + Environment.NewLine +
                       "Subject: Something" + Environment.NewLine +
                       Environment.NewLine +
                       "Hello" + Environment.NewLine;

         var smtpSimulator = new SmtpClientSimulator();
         smtpSimulator.SendRaw(account.Address, account.Address, message);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODY.PEEK[HEADER.FIELDS (Subject From)]");
         simulator.Disconnect();


         Assert.IsTrue(result.Contains("Subject: Something"));
         Assert.IsTrue(result.Contains("From: Someone <someone@example.com>"));
         // The feedback should end with an empty header line.
         Assert.IsTrue(result.Contains("\r\n\r\n)"));
         Assert.IsFalse(result.Contains("Received:"));
      }

      [Test]
      public void RequestingSameHeaderFieldMultipleTimesShouldReturnItOnce()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");

         var message = "From: Someone <someone@example.com>" + Environment.NewLine +
                       "To: Someoen <someone@example.com>" + Environment.NewLine +
                       "Date: Wed, 22 Apr 2009 11:05:09 \"GMT\"" + Environment.NewLine +
                       "Subject: SubjectText" + Environment.NewLine +
                       Environment.NewLine +
                       "Hello" + Environment.NewLine;

         var smtpSimulator = new SmtpClientSimulator();
         smtpSimulator.SendRaw(account.Address, account.Address, message);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODY.PEEK[HEADER.FIELDS (Subject Subject)]");
         simulator.Disconnect();

         Assert.AreEqual(1, StringExtensions.Occurences(result, "SubjectText"));
      }


      [Test]
      [Description("Issue 282, hMailServer not working with Symbian N60 ")]
      public void TestFetchHeaderFieldsNot()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");

         var message = "From: Someone <someone@example.com>" + Environment.NewLine +
                       "To: Someoen <someone@example.com>" + Environment.NewLine +
                       "Date: Wed, 22 Apr 2009 11:05:09 \"GMT\"" + Environment.NewLine +
                       "Subject: Something" + Environment.NewLine +
                       Environment.NewLine +
                       "Hello" + Environment.NewLine;

         var smtpSimulator = new SmtpClientSimulator();
         smtpSimulator.SendRaw(account.Address, account.Address, message);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODY.PEEK[HEADER.FIELDS.NOT (Subject From)]");
         simulator.Disconnect();


         Assert.IsTrue(result.Contains("Received:"), result);
         Assert.IsFalse(result.Contains("Subject:"), result);
         Assert.IsFalse(result.Contains("From:"), result);
         // The feedback should end with an empty header line.
         Assert.IsTrue(result.Contains("\r\n\r\n)"), result);
      }

      [Test]
      public void TestFetchInvalid()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody1");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody2");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBody3");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 3);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");
         var result = sim.Fetch("0 BODY[1]");
         Assert.IsTrue(result.StartsWith("A17 OK FETCH completed"));
         result = sim.Fetch("-1 BODY[1]");
         Assert.IsTrue(result.StartsWith("A17 BAD"));
         result = sim.Fetch("-100 BODY[1]");
         Assert.IsTrue(result.StartsWith("A17 BAD"));
      }

      [Test]
      [Description("RFC 3501: partial fetch where start offset is beyond end of content must return empty string")]
      public void PartialFetch_StartBeyondEndReturnsEmptyString()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var result = sim.Fetch("1 BODY[HEADER]<99999.10>");
         Assert.IsTrue(result.Contains("BODY[HEADER]<99999>"), result);
         Assert.IsTrue(result.Contains("\"\""), result);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 524: BODY[TEXT] partial fetch where offset >= part size must return empty string, not crash")]
      public void PartialFetch_BodyText_StartBeyondEndReturnsEmptyString()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var result = sim.Fetch("1 BODY.PEEK[TEXT]<393216.393216>");
         Assert.IsTrue(result.Contains("BODY[TEXT]<393216>"), result);
         Assert.IsTrue(result.Contains("\"\""), result);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 524: BODY[1] partial fetch where offset >= part size must return empty string, not crash (Thunderbird chunked fetch scenario)")]
      public void PartialFetch_BodyPart_StartBeyondEndReturnsEmptyString()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // Reproduces the exact Thunderbird chunked-fetch scenario from issue #524.
         var result = sim.Fetch("1 BODY.PEEK[1]<393216.393216>");
         Assert.IsTrue(result.Contains("BODY[1]<393216>"), result);
         Assert.IsTrue(result.Contains("\"\""), result);

         sim.Disconnect();
      }

      [Test]
      [Description("RFC 3501: partial fetch where requested count exceeds remaining bytes must truncate")]
      public void PartialFetch_RequestedSizeExceedingRemainderTruncates()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var fullResult = sim.Fetch("1 BODY[HEADER]");
         var lStart = fullResult.IndexOf('{');
         var lEnd = fullResult.IndexOf('}', lStart);
         var fullSize = int.Parse(fullResult.Substring(lStart + 1, lEnd - lStart - 1));

         var partial = sim.Fetch("1 BODY[HEADER]<5.99999>");
         Assert.IsTrue(partial.Contains("BODY[HEADER]<5>"), partial);
         Assert.IsTrue(partial.Contains("{" + (fullSize - 5) + "}"), partial);

         sim.Disconnect();
      }

      [Test]
      [Description("BODY.PEEK must not set the \\Seen flag")]
      public void BodyPeekDoesNotSetSeenFlag()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         sim.Fetch("1 BODY.PEEK[TEXT]");
         var flags = sim.GetFlags(1);
         Assert.IsFalse(flags.Contains("\\Seen"), flags);

         sim.Disconnect();
      }

      [Test]
      [Description("BODY (without PEEK) must set the \\Seen flag")]
      public void BodyFetchSetsSeenFlag()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         sim.Fetch("1 BODY[TEXT]");
         var flags = sim.GetFlags(1);
         Assert.IsTrue(flags.Contains("\\Seen"), flags);

         sim.Disconnect();
      }

      [Test]
      [Description("Partial fetch of BODY[TEXT] must return correct byte slice")]
      public void PartialFetch_BodyText()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "SampleBodyContent");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var fullResult = sim.Fetch("1 BODY.PEEK[TEXT]");
         var lStart = fullResult.IndexOf('{');
         var lEnd = fullResult.IndexOf('}', lStart);
         var fullSize = int.Parse(fullResult.Substring(lStart + 1, lEnd - lStart - 1));
         var contentStart = fullResult.IndexOf("\r\n", lEnd) + 2;
         var fullText = fullResult.Substring(contentStart, fullSize);

         Assert.IsTrue(fullSize > 5, $"Body too short ({fullSize}) for a meaningful partial test");

         var partial = sim.Fetch("1 BODY.PEEK[TEXT]<0.5>");
         Assert.IsTrue(partial.Contains("BODY[TEXT]<0>"), partial);
         Assert.IsTrue(partial.Contains("{5}"), partial);
         var partialStart = partial.IndexOf("{5}") + 5;
         Assert.AreEqual(fullText.Substring(0, 5), partial.Substring(partialStart, 5));

         sim.Disconnect();
      }

      [Test]
      [Description("Partial fetch of BODY[HEADER.FIELDS] must return correct byte slice")]
      public void PartialFetch_HeaderFields()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");
         var message = "From: Someone <someone@example.com>" + Environment.NewLine +
                       "Subject: TestSubject" + Environment.NewLine +
                       Environment.NewLine +
                       "Hello" + Environment.NewLine;
         new SmtpClientSimulator().SendRaw(account.Address, account.Address, message);
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var fullResult = sim.Fetch("1 BODY.PEEK[HEADER.FIELDS (Subject)]");
         var lStart = fullResult.IndexOf('{');
         var lEnd = fullResult.IndexOf('}', lStart);
         var fullSize = int.Parse(fullResult.Substring(lStart + 1, lEnd - lStart - 1));
         var contentStart = fullResult.IndexOf("\r\n", lEnd) + 2;
         var fullContent = fullResult.Substring(contentStart, fullSize);

         Assert.IsTrue(fullSize > 5, $"HEADER.FIELDS response too short ({fullSize}) for a meaningful partial test");

         var partial = sim.Fetch("1 BODY.PEEK[HEADER.FIELDS (Subject)]<0.5>");
         Assert.IsTrue(partial.Contains("BODY[HEADER.FIELDS (SUBJECT)]<0>") || partial.Contains("BODY[HEADER.FIELDS (Subject)]<0>"), partial);
         Assert.IsTrue(partial.Contains("{5}"), partial);
         var partialStart = partial.IndexOf("{5}") + 5;
         Assert.AreEqual(fullContent.Substring(0, 5), partial.Substring(partialStart, 5));

         sim.Disconnect();
      }

      [Test]
      [Description("Partial fetch of BODY[HEADER.FIELDS.NOT] must return correct byte slice")]
      public void PartialFetch_HeaderFieldsNot()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimetest@example.test", "test");
         var message = "From: Someone <someone@example.com>" + Environment.NewLine +
                       "Subject: TestSubject" + Environment.NewLine +
                       Environment.NewLine +
                       "Hello" + Environment.NewLine;
         new SmtpClientSimulator().SendRaw(account.Address, account.Address, message);
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var fullResult = sim.Fetch("1 BODY.PEEK[HEADER.FIELDS.NOT (Subject)]");
         var lStart = fullResult.IndexOf('{');
         var lEnd = fullResult.IndexOf('}', lStart);
         var fullSize = int.Parse(fullResult.Substring(lStart + 1, lEnd - lStart - 1));
         var contentStart = fullResult.IndexOf("\r\n", lEnd) + 2;
         var fullContent = fullResult.Substring(contentStart, fullSize);

         Assert.IsTrue(fullSize > 5, $"HEADER.FIELDS.NOT response too short ({fullSize}) for a meaningful partial test");

         var partial = sim.Fetch("1 BODY.PEEK[HEADER.FIELDS.NOT (Subject)]<0.5>");
         Assert.IsTrue(partial.Contains("BODY[HEADER.FIELDS.NOT (SUBJECT)]<0>") || partial.Contains("BODY[HEADER.FIELDS.NOT (Subject)]<0>"), partial);
         Assert.IsTrue(partial.Contains("{5}"), partial);
         var partialStart = partial.IndexOf("{5}") + 5;
         Assert.AreEqual(fullContent.Substring(0, 5), partial.Substring(partialStart, 5));

         sim.Disconnect();
      }

      [Test]
      [Description("RFC822.SIZE must return a positive integer reflecting message size")]
      public void FetchRfc822SizeReturnsPositiveInteger()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var result = sim.Fetch("1 RFC822.SIZE");
         Assert.IsTrue(result.Contains("RFC822.SIZE"), result);
         var sizeStart = result.IndexOf("RFC822.SIZE ") + "RFC822.SIZE ".Length;
         var sizeEnd = result.IndexOfAny(new[] { ' ', ')' }, sizeStart);
         var size = int.Parse(result.Substring(sizeStart, sizeEnd - sizeStart));
         Assert.IsTrue(size > 0, $"RFC822.SIZE should be positive, got {size}");

         sim.Disconnect();
      }

      [Test]
      [Description("INTERNALDATE must return a non-empty quoted date string")]
      public void FetchInternaldateReturnsNonEmptyQuotedString()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var result = sim.Fetch("1 INTERNALDATE");
         Assert.IsTrue(result.Contains("INTERNALDATE \""), result);

         sim.Disconnect();
      }

      [Test]
      [Description("UID fetch must return a positive integer")]
      public void FetchUidReturnsPositiveInteger()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var result = sim.Fetch("1 UID");
         Assert.IsTrue(result.Contains("UID "), result);
         var uidStart = result.IndexOf("UID ") + 4;
         var uidEnd = result.IndexOfAny(new[] { ' ', ')' }, uidStart);
         var uid = int.Parse(result.Substring(uidStart, uidEnd - uidStart));
         Assert.IsTrue(uid > 0, $"UID should be positive, got {uid}");

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 334, IMAP FETCH does not properly honour the <start.size> partial body clause")]
      public void TestPartialFetch_HeaderOctetRange()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test subject", "Body text");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // Fetch full header to establish baseline size and content.
         var fullHeaderResult = sim.Fetch("1 BODY[HEADER]");
         var literalStart = fullHeaderResult.IndexOf('{');
         var literalEnd = fullHeaderResult.IndexOf('}', literalStart);
         var fullSize = int.Parse(fullHeaderResult.Substring(literalStart + 1, literalEnd - literalStart - 1));
         Assert.IsTrue(fullSize > 15, $"Header too short ({fullSize} bytes) for a meaningful partial test");

         var contentStart = fullHeaderResult.IndexOf("\r\n", literalEnd) + 2;
         var fullHeader = fullHeaderResult.Substring(contentStart, fullSize);

         // <0.10>: 10 bytes starting at offset 0.
         var result0 = sim.Fetch("1 BODY[HEADER]<0.10>");
         Assert.IsTrue(result0.Contains("BODY[HEADER]<0>"), result0);
         Assert.IsTrue(result0.Contains("{10}"), result0);
         var content0Start = result0.IndexOf("{10}") + 6;
         Assert.AreEqual(fullHeader.Substring(0, 10), result0.Substring(content0Start, 10));

         // <5.10>: 10 bytes starting at offset 5.
         var result5 = sim.Fetch("1 BODY[HEADER]<5.10>");
         Assert.IsTrue(result5.Contains("BODY[HEADER]<5>"), result5);
         Assert.IsTrue(result5.Contains("{10}"), result5);
         var content5Start = result5.IndexOf("{10}") + 6;
         Assert.AreEqual(fullHeader.Substring(5, 10), result5.Substring(content5Start, 10));

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 459, IMAP: BODY[X.MIME] for message/rfc822 part must return outer MIME headers, not inner message headers")]
      public void FetchMimeOfRfc822Part_ReturnsOuterMimeHeaders()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var innerMessage =
            "From: inner@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Inner Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Inner body\r\n";

         var outerMessage =
            "From: outer@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Outer Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"boundary123\"\r\n" +
            "\r\n" +
            "--boundary123\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Outer body\r\n" +
            "\r\n" +
            "--boundary123\r\n" +
            "Content-Type: message/rfc822\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "Content-Disposition: attachment\r\n" +
            "\r\n" +
            innerMessage +
            "--boundary123--\r\n";

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, outerMessage);
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // BODY[2.MIME] must return the MIME headers of part 2 (Content-Type: message/rfc822, etc.)
         // and must NOT return the inner message's RFC 822 headers (From, Subject, etc.)
         var mimeResult = sim.Fetch("1 BODY[2.MIME]");
         Assert.IsTrue(mimeResult.Contains("message/rfc822"), "BODY[2.MIME] should contain 'message/rfc822': " + mimeResult);
         Assert.IsFalse(mimeResult.Contains("Inner Subject"), "BODY[2.MIME] must not contain inner message Subject: " + mimeResult);
         Assert.IsFalse(mimeResult.Contains("inner@example.test"), "BODY[2.MIME] must not contain inner message From: " + mimeResult);

         // BODY[2.HEADER] must return the inner message's RFC 822 headers (From, Subject, etc.)
         var headerResult = sim.Fetch("1 BODY[2.HEADER]");
         Assert.IsTrue(headerResult.Contains("Inner Subject"), "BODY[2.HEADER] should contain inner Subject: " + headerResult);
         Assert.IsTrue(headerResult.Contains("inner@example.test"), "BODY[2.HEADER] should contain inner From: " + headerResult);

         // BODY[2.TEXT] must return the inner message's body text (without headers)
         var textResult = sim.Fetch("1 BODY[2.TEXT]");
         Assert.IsTrue(textResult.Contains("Inner body"), "BODY[2.TEXT] should contain inner body text: " + textResult);
         Assert.IsFalse(textResult.Contains("Inner Subject"), "BODY[2.TEXT] must not contain inner message headers: " + textResult);

         // BODY[2] must return the inner message including its headers and body
         var bodyResult = sim.Fetch("1 BODY[2]");
         Assert.IsTrue(bodyResult.Contains("Inner Subject"), "BODY[2] should contain inner message headers: " + bodyResult);
         Assert.IsTrue(bodyResult.Contains("Inner body"), "BODY[2] should contain inner message body: " + bodyResult);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 459, IMAP: BODY[X.MIME] and BODY[X.HEADER] must return identical data for non-message/rfc822 parts")]
      public void FetchMimeAndHeaderOfPlainPart_ReturnSameData()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var message =
            "From: test@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Test\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"boundary456\"\r\n" +
            "\r\n" +
            "--boundary456\r\n" +
            "Content-Type: text/plain; charset=US-ASCII\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "\r\n" +
            "Hello\r\n" +
            "--boundary456--\r\n";

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, message);
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // For a plain text part, BODY[1.MIME] and BODY[1.HEADER] should return the same MIME headers
         var mimeResult = sim.Fetch("1 BODY[1.MIME]");
         var headerResult = sim.Fetch("1 BODY[1.HEADER]");
         Assert.IsTrue(mimeResult.Contains("text/plain"), "BODY[1.MIME] should contain Content-Type: " + mimeResult);
         Assert.IsTrue(headerResult.Contains("text/plain"), "BODY[1.HEADER] should contain Content-Type: " + headerResult);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 459, IMAP: BODY[X.HEADER] must return inner RFC2822 headers, not outer MIME headers. " +
                   "A naive fix that skips LoadEncapsulatedMessage for any single-component path would break this.")]
      public void FetchHeaderOfRfc822Part_ReturnsInnerHeaders()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var innerMessage =
            "From: inner@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Inner Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Inner body\r\n";

         var outerMessage =
            "From: outer@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Outer Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"boundary123\"\r\n" +
            "\r\n" +
            "--boundary123\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Outer body\r\n" +
            "\r\n" +
            "--boundary123\r\n" +
            "Content-Type: message/rfc822\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "Content-Disposition: attachment\r\n" +
            "\r\n" +
            innerMessage +
            "--boundary123--\r\n";

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, outerMessage);
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // BODY[2.HEADER] must load the encapsulated message and return its RFC2822 headers.
         // A fix that skips loading for any single-component path would wrongly return the
         // outer MIME headers (Content-Type: message/rfc822) instead.
         var headerResult = sim.Fetch("1 BODY[2.HEADER]");
         Assert.IsTrue(headerResult.Contains("Inner Subject"), "BODY[2.HEADER] should contain inner RFC2822 Subject: " + headerResult);
         Assert.IsTrue(headerResult.Contains("inner@example.test"), "BODY[2.HEADER] should contain inner RFC2822 From: " + headerResult);
         Assert.IsFalse(headerResult.Contains("message/rfc822"), "BODY[2.HEADER] must not contain outer MIME Content-Type: " + headerResult);

         // BODY[2.MIME] must NOT load the encapsulated message and return the outer MIME headers.
         var mimeResult = sim.Fetch("1 BODY[2.MIME]");
         Assert.IsTrue(mimeResult.Contains("message/rfc822"), "BODY[2.MIME] should contain outer MIME Content-Type: " + mimeResult);
         Assert.IsFalse(mimeResult.Contains("Inner Subject"), "BODY[2.MIME] must not contain inner RFC2822 Subject: " + mimeResult);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 459, IMAP: BODY[X.Y] deep navigation into encapsulated message/rfc822 must still work")]
      public void FetchSubPartOfRfc822Part_ReturnsCorrectContent()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var innerMessage =
            "From: inner@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Inner Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/alternative; boundary=\"innerboundary\"\r\n" +
            "\r\n" +
            "--innerboundary\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Plain text part\r\n" +
            "--innerboundary\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<b>HTML part</b>\r\n" +
            "--innerboundary--\r\n";

         var outerMessage =
            "From: outer@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Outer Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"outerboundary\"\r\n" +
            "\r\n" +
            "--outerboundary\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Outer body\r\n" +
            "\r\n" +
            "--outerboundary\r\n" +
            "Content-Type: message/rfc822\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "\r\n" +
            innerMessage +
            "--outerboundary--\r\n";

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, outerMessage);
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // BODY[2.1] must return the plain text sub-part of the encapsulated message
         var part1Result = sim.Fetch("1 BODY[2.1]");
         Assert.IsTrue(part1Result.Contains("Plain text part"), "BODY[2.1] should return plain text sub-part: " + part1Result);

         // BODY[2.2] must return the HTML sub-part of the encapsulated message
         var part2Result = sim.Fetch("1 BODY[2.2]");
         Assert.IsTrue(part2Result.Contains("HTML part"), "BODY[2.2] should return HTML sub-part: " + part2Result);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 459, IMAP: BODY[X.Y.MIME] must traverse intermediate message/rfc822 parts and return the MIME headers of the target sub-part")]
      public void FetchMimeOfSubPartInsideRfc822Part_ReturnsCorrectMimeHeaders()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var innerMessage =
            "From: inner@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Inner Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/alternative; boundary=\"innerboundary\"\r\n" +
            "\r\n" +
            "--innerboundary\r\n" +
            "Content-Type: text/plain; charset=US-ASCII\r\n" +
            "\r\n" +
            "Plain text part\r\n" +
            "--innerboundary\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<b>HTML part</b>\r\n" +
            "--innerboundary--\r\n";

         var outerMessage =
            "From: outer@example.test\r\n" +
            "To: test@example.test\r\n" +
            "Subject: Outer Subject\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"outerboundary\"\r\n" +
            "\r\n" +
            "--outerboundary\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Outer body\r\n" +
            "\r\n" +
            "--outerboundary\r\n" +
            "Content-Type: message/rfc822\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "\r\n" +
            innerMessage +
            "--outerboundary--\r\n";

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, outerMessage);
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // BODY[2.1.MIME] must traverse through the message/rfc822 at part 2 (intermediate
         // component — encapsulated message is loaded for traversal) and then return the
         // MIME entity headers of sub-part 1 (final component — encapsulated message is NOT
         // loaded, so GetHeaderContents returns the sub-part's own headers, not inner content).
         var mime1Result = sim.Fetch("1 BODY[2.1.MIME]");
         Assert.IsTrue(mime1Result.Contains("text/plain"), "BODY[2.1.MIME] should contain Content-Type text/plain: " + mime1Result);
         Assert.IsTrue(mime1Result.Contains("US-ASCII"), "BODY[2.1.MIME] should contain charset from the sub-part header: " + mime1Result);
         Assert.IsFalse(mime1Result.Contains("Plain text part"), "BODY[2.1.MIME] must not contain body content: " + mime1Result);
         Assert.IsFalse(mime1Result.Contains("Inner Subject"), "BODY[2.1.MIME] must not contain inner RFC2822 headers: " + mime1Result);

         // BODY[2.2.MIME] must return the MIME headers of sub-part 2 (text/html).
         var mime2Result = sim.Fetch("1 BODY[2.2.MIME]");
         Assert.IsTrue(mime2Result.Contains("text/html"), "BODY[2.2.MIME] should contain Content-Type text/html: " + mime2Result);
         Assert.IsFalse(mime2Result.Contains("HTML part"), "BODY[2.2.MIME] must not contain body content: " + mime2Result);

         sim.Disconnect();
      }

      [Test]
      [Description("Valid partial fetch of a numbered body part (BODY[1]) must return the correct byte slice")]
      public void PartialFetch_BodyPart_ValidRangeReturnsCorrectBytes()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test subject", "SampleBodyContent");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // Fetch full body part to establish baseline.
         var fullResult = sim.Fetch("1 BODY.PEEK[1]");
         var literalStart = fullResult.IndexOf('{');
         var literalEnd = fullResult.IndexOf('}', literalStart);
         var fullSize = int.Parse(fullResult.Substring(literalStart + 1, literalEnd - literalStart - 1));
         Assert.IsTrue(fullSize > 5, $"Body part too short ({fullSize} bytes) for a meaningful partial test");
         var contentStart = fullResult.IndexOf("\r\n", literalEnd) + 2;
         var fullContent = fullResult.Substring(contentStart, fullSize);

         // <0.5>: 5 bytes from the start.
         var result0 = sim.Fetch("1 BODY.PEEK[1]<0.5>");
         Assert.IsTrue(result0.Contains("BODY[1]<0>"), result0);
         Assert.IsTrue(result0.Contains("{5}"), result0);
         var partial0Start = result0.IndexOf("{5}") + 5;
         Assert.AreEqual(fullContent.Substring(0, 5), result0.Substring(partial0Start, 5));

         // <5.5>: 5 bytes starting at offset 5.
         var result5 = sim.Fetch("1 BODY.PEEK[1]<5.5>");
         Assert.IsTrue(result5.Contains("BODY[1]<5>"), result5);
         Assert.IsTrue(result5.Contains("{5}"), result5);
         var partial5Start = result5.IndexOf("{5}") + 5;
         Assert.AreEqual(fullContent.Substring(5, 5), result5.Substring(partial5Start, 5));

         sim.Disconnect();
      }

      // ----------------------------------------------------------------------------
      // Issue #26: chunked partial FETCH, with a shorter final chunk.
      //
      // What the suite had before this: nine partial-fetch tests, every one of them a
      // SINGLE request for a handful of octets. Not one of them fetched a message in
      // successive chunks and put it back together, and not one of them touched
      // BODY[] at all - which is the only body form served straight from the message
      // file rather than from the parsed MIME tree, and the form Apple Mail and
      // Thunderbird actually use to pull a message down in pieces.
      //
      // The tests below are the ones the issue asks for. The reassembly ones prove the
      // property a chunking client depends on and nothing else was checking: that the
      // concatenation of the chunks is the message, byte for byte, when the last
      // request asks for fewer octets than the ones before it.
      // ----------------------------------------------------------------------------

      /// <summary>
      ///    Builds a deterministic, all-ASCII message of roughly 100 KB. Every line
      ///    carries its own index, so a chunk taken from the wrong offset shows up as a
      ///    specific wrong line number rather than as "some bytes differ".
      ///
      ///    Lower case and digits only in the body, and no line begins with the literal
      ///    tag the simulator watches for, because ImapClientSimulator ends its read at
      ///    the first line starting with that tag.
      /// </summary>
      private static string BuildChunkableMessage(string toAddress, int lineCount)
      {
         var builder = new StringBuilder();

         builder.Append("From: sender@example.test\r\n");
         builder.Append("To: " + toAddress + "\r\n");
         builder.Append("Subject: partial fetch chunking\r\n");
         builder.Append("MIME-Version: 1.0\r\n");
         builder.Append("Content-Type: text/plain; charset=us-ascii\r\n");
         builder.Append("\r\n");

         for (var i = 0; i < lineCount; i++)
            builder.Append(string.Format("line {0:D6} pack my box with five dozen liquor jugs 0123456789\r\n", i));

         return builder.ToString();
      }

      private static string Head(string response)
      {
         if (response == null)
            return "(null)";

         return response.Length <= 220 ? response : response.Substring(0, 220) + " [...]";
      }

      private static string Excerpt(string value, int index)
      {
         if (index >= value.Length)
            return "(past end)";

         var length = Math.Min(40, value.Length - index);
         return value.Substring(index, length);
      }

      /// <summary>
      ///    Reads the literal that follows a named part identifier in a FETCH response,
      ///    and checks the announced length against what actually arrived. A literal
      ///    whose declared size and delivered size disagree is precisely what a client
      ///    experiences as corruption, so that check lives here rather than in one test.
      /// </summary>
      private static string ReadFetchLiteral(string response, string partIdentifier)
      {
         var marker = partIdentifier + " {";
         var markerPos = response.IndexOf(marker, StringComparison.Ordinal);

         Assert.IsTrue(markerPos >= 0,
            string.Format("Expected \"{0}\" followed by a literal in the FETCH response. Response: {1}",
               partIdentifier, Head(response)));

         var sizeStart = markerPos + marker.Length;
         var sizeEnd = response.IndexOf('}', sizeStart);

         Assert.IsTrue(sizeEnd > sizeStart,
            "The literal length in the FETCH response is unterminated. Response: " + Head(response));

         var announced = int.Parse(response.Substring(sizeStart, sizeEnd - sizeStart));

         Assert.IsTrue(sizeEnd + 2 < response.Length && response[sizeEnd + 1] == '\r' && response[sizeEnd + 2] == '\n',
            "A literal must be followed by CRLF and then its octets. Response: " + Head(response));

         var contentStart = sizeEnd + 3;

         // Strictly less than: the closing ")" of the untagged FETCH response always
         // follows the literal, so the octets of the literal alone can never reach the
         // end of what was received.
         Assert.IsTrue(contentStart + announced < response.Length,
            string.Format(
               "The server announced a {0} octet literal for {1} but only {2} octets followed it. " +
               "An announced length the response cannot satisfy is what the client sees as a corrupt message.",
               announced, partIdentifier, response.Length - contentStart));

         // The item is the only one in these FETCH commands, so the octet immediately
         // after the literal is that closing parenthesis. If the server had announced
         // more octets than it wrote, or written more than it announced, this is where
         // it shows.
         Assert.AreEqual(")", response.Substring(contentStart + announced, 1),
            string.Format(
               "The octet after a {0} octet literal for {1} should be the closing \")\" of the FETCH response, " +
               "which is what says the announced length and the bytes written agree. Found \"{2}\".",
               announced, partIdentifier, Excerpt(response, contentStart + announced)));

         return response.Substring(contentStart, announced);
      }

      /// <summary>
      ///    Fetches a section whole and returns its octets, for use as the reference the
      ///    reassembled chunks are compared against.
      /// </summary>
      private static string FetchWhole(ImapClientSimulator sim, string section, string identifier)
      {
         var response = sim.Fetch("1 BODY.PEEK[" + section + "]");
         return ReadFetchLiteral(response, identifier);
      }

      private static void AssertChunkedReassemblyMatches(ImapClientSimulator sim, string section, string identifier,
         string whole)
      {
         // A chunk size that cannot divide the message evenly, so the final request is
         // always for fewer octets than the ones before it. That is the case issue #26
         // is about, and it is the ordinary case for every chunking client: the last
         // chunk is simply the remainder.
         var chunkSize = whole.Length / 7 + 1;

         Assert.IsTrue(chunkSize > 1, "Section too short to chunk meaningfully: " + whole.Length + " octets.");

         var reassembled = new StringBuilder();
         var offset = 0;
         var chunkCount = 0;
         var finalChunkLength = -1;

         while (offset < whole.Length)
         {
            var expected = Math.Min(chunkSize, whole.Length - offset);
            var chunkIdentifier = identifier + "<" + offset + ">";

            var response = sim.Fetch("1 BODY.PEEK[" + section + "]<" + offset + "." + chunkSize + ">");

            // RFC 3501 7.4.2: the response carries the origin octet, and only the
            // origin octet - not the size that was requested.
            Assert.IsTrue(response.IndexOf(chunkIdentifier, StringComparison.Ordinal) >= 0,
               string.Format("The chunk at octet {0} must echo its origin as \"{1}\". Response: {2}",
                  offset, chunkIdentifier, Head(response)));

            var chunk = ReadFetchLiteral(response, chunkIdentifier);

            Assert.AreEqual(expected, chunk.Length,
               string.Format(
                  "Chunk at octet {0}: {1} octets were asked for with {2} left in the section, so {3} should come back.",
                  offset, chunkSize, whole.Length - offset, expected));

            Assert.IsTrue(chunk.Length > 0, "A chunk inside the section returned no octets; the fetch cannot progress.");

            reassembled.Append(chunk);
            finalChunkLength = chunk.Length;
            offset += chunk.Length;
            chunkCount++;
         }

         Assert.IsTrue(chunkCount >= 3, "Expected the section to take several chunks, took " + chunkCount + ".");
         Assert.IsTrue(finalChunkLength < chunkSize,
            string.Format("The final chunk should be shorter than the {0} octet chunks before it, was {1}. " +
                          "That asymmetry is the whole point of this test.", chunkSize, finalChunkLength));

         var joined = reassembled.ToString();

         Assert.AreEqual(whole.Length, joined.Length,
            "The chunks do not add up to the length of the section fetched in one request.");

         for (var i = 0; i < whole.Length; i++)
         {
            if (whole[i] != joined[i])
            {
               Assert.Fail(string.Format(
                  "Chunked reassembly of BODY[{0}] differs from the single-request fetch at octet {1} " +
                  "(chunk {2}, offset {3} within that chunk).\r\n  expected: {4}\r\n  actual:   {5}",
                  section, i, i / chunkSize, i % chunkSize, Excerpt(whole, i), Excerpt(joined, i)));
            }
         }
      }

      [Test]
      [Description("Issue 26: a message fetched with successive BODY[]<offset.length> requests, the last " +
                   "of them shorter than the rest, must reassemble to the message byte for byte")]
      public void PartialFetch_BodyFull_ChunkedReassemblyWithShorterFinalChunk()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         new SmtpClientSimulator().SendRaw(account.Address, account.Address,
            BuildChunkableMessage(account.Address, 1600));

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var whole = FetchWhole(sim, "", "BODY[]");

         Assert.IsTrue(whole.Length > 90000,
            "The fixture message should be around 100 KB so the chunking is realistic, was " + whole.Length + ".");

         AssertChunkedReassemblyMatches(sim, "", "BODY[]", whole);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 26: chunked BODY[TEXT] with a shorter final chunk must reassemble byte for byte")]
      public void PartialFetch_BodyText_ChunkedReassemblyWithShorterFinalChunk()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         new SmtpClientSimulator().SendRaw(account.Address, account.Address,
            BuildChunkableMessage(account.Address, 1600));

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var whole = FetchWhole(sim, "TEXT", "BODY[TEXT]");

         // BODY[TEXT] is re-serialized from the parsed MIME tree rather than read from
         // the file, so this is deliberately a floor rather than an exact figure: the
         // point is only that there is enough of it to chunk several times.
         Assert.IsTrue(whole.Length > 50000,
            "The fixture body is about 100 KB, so BODY[TEXT] should be of that order. Got " + whole.Length + ".");

         AssertChunkedReassemblyMatches(sim, "TEXT", "BODY[TEXT]", whole);

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 26: the octets at the very end of a message - the last one on its own, a request " +
                   "that overruns the end, and a request that starts exactly at the end")]
      public void PartialFetch_BodyFull_EndOfMessageBoundaries()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         new SmtpClientSimulator().SendRaw(account.Address, account.Address,
            BuildChunkableMessage(account.Address, 200));

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var whole = FetchWhole(sim, "", "BODY[]");
         var size = whole.Length;

         // The last octet, asked for exactly.
         var lastOctet = sim.Fetch("1 BODY.PEEK[]<" + (size - 1) + ".1>");
         Assert.AreEqual(whole.Substring(size - 1, 1),
            ReadFetchLiteral(lastOctet, "BODY[]<" + (size - 1) + ">"),
            "BODY[]<size-1.1> must return the last octet of the message.");

         // The last octet, asked for as part of a range that overruns the end. RFC 3501:
         // the server returns as much as it can, and the literal must describe what it
         // actually sent.
         var overrun = sim.Fetch("1 BODY.PEEK[]<" + (size - 1) + ".5000>");
         Assert.AreEqual(whole.Substring(size - 1, 1),
            ReadFetchLiteral(overrun, "BODY[]<" + (size - 1) + ">"),
            "A range that overruns the end must be truncated to the single octet that remains.");

         // A request starting exactly at the end. Some clients issue one of these when
         // the message length is an exact multiple of their chunk size.
         var atEnd = sim.Fetch("1 BODY.PEEK[]<" + size + ".5000>");
         Assert.IsTrue(atEnd.Contains("BODY[]<" + size + ">"), atEnd);
         Assert.IsTrue(atEnd.Contains("\"\""),
            "A partial fetch starting at end-of-message must return an empty string. " + Head(atEnd));

         // And one past it, which is the same thing with a rounder number.
         var pastEnd = sim.Fetch("1 BODY.PEEK[]<" + (size + 4096) + ".5000>");
         Assert.IsTrue(pastEnd.Contains("BODY[]<" + (size + 4096) + ">"), pastEnd);
         Assert.IsTrue(pastEnd.Contains("\"\""),
            "A partial fetch starting past end-of-message must return an empty string. " + Head(pastEnd));

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 26: RFC822.SIZE must equal the number of octets BODY[] delivers - a chunking " +
                   "client computes the length of its final chunk from it")]
      public void PartialFetch_Rfc822SizeMatchesTheOctetsBodyFullDelivers()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         new SmtpClientSimulator().SendRaw(account.Address, account.Address,
            BuildChunkableMessage(account.Address, 400));

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var sizeResponse = sim.Fetch("1 RFC822.SIZE");
         var sizeStart = sizeResponse.IndexOf("RFC822.SIZE ", StringComparison.Ordinal) + "RFC822.SIZE ".Length;
         var sizeEnd = sizeResponse.IndexOfAny(new[] {' ', ')'}, sizeStart);
         var announcedSize = int.Parse(sizeResponse.Substring(sizeStart, sizeEnd - sizeStart));

         var whole = FetchWhole(sim, "", "BODY[]");

         Assert.AreEqual(announcedSize, whole.Length,
            "RFC822.SIZE and the BODY[] literal describe the same octets. A client that chunks by " +
            "RFC822.SIZE computes its last request as size-minus-offset, so any difference between " +
            "them truncates or overruns the final chunk.");

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 26 / upstream 459: BODY[HEADER]<5> - an origin octet with no \".size\" - must " +
                   "return the section from octet 5 onwards, not the first 5 octets labelled <0>")]
      public void PartialFetch_OriginWithoutSizeReturnsTheRemainderNotTheStart()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test subject", "Body text");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var whole = FetchWhole(sim, "HEADER", "BODY[HEADER]");
         Assert.IsTrue(whole.Length > 20, "Header too short (" + whole.Length + ") for a meaningful test.");

         // "<start>" with no size is the form the server uses in its own responses, so
         // clients do send it back. It used to parse as start 0 with a count taken from
         // the whole value: the client asked for the section from octet 5 and got the
         // FIRST 5 octets, labelled "<0>" - the wrong region of the message, silently.
         var partial = sim.Fetch("1 BODY.PEEK[HEADER]<5>");

         Assert.IsTrue(partial.Contains("BODY[HEADER]<5>"),
            "The origin octet asked for must be the one echoed. Before the fix the server answered " +
            "BODY[HEADER]<0> and sent the first five octets. Response: " + Head(partial));

         Assert.AreEqual(whole.Substring(5), ReadFetchLiteral(partial, "BODY[HEADER]<5>"),
            "BODY[HEADER]<5> must return everything from octet 5 to the end of the section.");

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 26: a FETCH whose data items do not parse must be answered BAD, not OK with an " +
                   "empty result - the parse result was discarded, so the syntax check had no effect")]
      public void Fetch_UnparseableDataItemsAreRefusedWithBad()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test subject", "Body text");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         var mismatchedBrackets = sim.Fetch("1 (BODY[HEADER)");
         Assert.IsTrue(mismatchedBrackets.StartsWith("A17 BAD"),
            "A FETCH with mismatched brackets used to be answered \"* 1 FETCH ()\" and OK: the client is " +
            "told it succeeded and handed nothing. Response: " + Head(mismatchedBrackets));

         // The session survives a command-level BAD.
         var afterwards = sim.Fetch("1 (UID)");
         Assert.IsTrue(afterwards.Contains("A17 OK FETCH completed"), Head(afterwards));

         sim.Disconnect();
      }

      [Test]
      [Description("Issue 26 / upstream 459: an unterminated <start.size> must be refused. The parse loop " +
                   "used to rewrite the command to itself and spin - one authenticated FETCH pinned a " +
                   "worker thread at 100% CPU for the life of the process. On an UNFIXED build this test " +
                   "does not merely fail: the thread it hangs never comes back, and every test after it " +
                   "runs one worker short, so run it against a build that carries the fix.")]
      public void Fetch_UnterminatedPartialRangeIsRefusedAndDoesNotSpin()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test subject", "Body text");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         sim.ConnectAndLogon(account.Address, "test");
         sim.SelectFolder("INBOX");

         // No closing ">". The round and square bracket counts balance, so nothing
         // rejected this before it reached the parse loop.
         var unterminated = sim.Fetch("1 (BODY[]<0.100 UID)");
         Assert.IsTrue(unterminated.StartsWith("A17 BAD"),
            "An unterminated partial specifier must be refused. Response: " + Head(unterminated));

         // The other shape that reached the same non-terminating branch: a "]" before
         // the "[", so the search for the closing bracket fails even though the counts
         // balance. Validation cannot catch this one, so the loop itself has to end.
         // Whether the server answers OK with no data items or BAD is beside the point
         // here - the assertion is that it answers at all.
         var reversedBrackets = sim.Fetch("1 (]BODY[ UID)");
         Assert.IsTrue(reversedBrackets.Contains("A17 "),
            "A FETCH whose brackets are in the wrong order must produce a tagged response rather than " +
            "spinning. Response: " + Head(reversedBrackets));

         var afterwards = sim.Fetch("1 (UID)");
         Assert.IsTrue(afterwards.Contains("A17 OK FETCH completed"), Head(afterwards));

         sim.Disconnect();
      }
   }
}