// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    RFC 3464 delivery status notifications.
   ///
   ///    Until this existed a bounce from this server was a paragraph of English
   ///    and nothing else, so no sending system could act on one: a list manager
   ///    could not tell a dead address from a full mailbox, and monitoring could
   ///    not classify a failure at all. The report is now a multipart/report
   ///    (RFC 3462) carrying a per-recipient message/delivery-status part.
   ///
   ///    The assertions that matter are the ones on the STATUS VALUE. A test that
   ///    only checked that a Status field exists would pass on an implementation
   ///    that wrote "5.0.0" for everything, which is the whole failure this
   ///    feature is about - so three different failures are provoked here and
   ///    each is pinned to its own RFC 3463 code:
   ///
   ///       a full mailbox        5.2.2  (mailbox full - NOT a bad address)
   ///       a remote 550 at RCPT  5.1.1  (bad destination mailbox address)
   ///       a Sieve reject        5.7.1  (delivery not authorized, refused)
   ///
   ///    and each test asserts the OTHER two codes are absent, so a hard-coded
   ///    implementation cannot pass any of them.
   /// </summary>
   [TestFixture]
   public class DeliveryStatusNotification : TestFixtureBase
   {
      private const string Password = "test";

      private static int accountSequence_;

      // Unique per account created by this fixture, however the tests are ordered or parallelised.
      private static int NextAccountSequence()
      {
         return Interlocked.Increment(ref accountSequence_);
      }

      /// <summary>
      ///    One MIME part of the report: its header block and its body, as
      ///    written on the wire.
      /// </summary>
      private class ReportPart
      {
         public string Headers;
         public string Body;
      }

      /// <summary>
      ///    The top-level header block, which ends at the first blank line.
      /// </summary>
      private static string TopLevelHeaders(string report)
      {
         int headerEnd = report.IndexOf("\r\n\r\n", StringComparison.Ordinal);

         Assert.That(headerEnd, Is.GreaterThan(0),
            "The report has no header block at all:\r\n" + report);

         return report.Substring(0, headerEnd);
      }

      /// <summary>
      ///    The multipart boundary, taken from the TOP-LEVEL Content-Type only.
      ///    Reading it from anywhere else would find the boundary of the original
      ///    message, whose headers this report quotes back in its third part.
      /// </summary>
      private static string BoundaryOf(string report)
      {
         Match boundary = Regex.Match(TopLevelHeaders(report), "boundary=\"?([^\";\r\n]+)\"?");

         Assert.That(boundary.Success, Is.True,
            "The report's top-level Content-Type declares no boundary:\r\n" + report);

         return boundary.Groups[1].Value;
      }

      /// <summary>
      ///    The report's parts, in the order they appear on the wire. Order is
      ///    part of what is being asserted - RFC 3464 2.1 puts the human-readable
      ///    explanation first, and that is what makes the change invisible to a
      ///    mail client that has never heard of multipart/report.
      /// </summary>
      private static List<ReportPart> PartsOf(string report)
      {
         var parts = new List<ReportPart>();

         string[] pieces = report.Split(new[] { "\r\n--" + BoundaryOf(report) }, StringSplitOptions.None);

         // Piece zero is the top-level header block and the preamble, which is
         // not a part; the last piece is what follows the closing delimiter.
         for (var i = 1; i < pieces.Length; i++)
         {
            string piece = pieces[i];

            if (piece.StartsWith("--", StringComparison.Ordinal))
               continue;

            int bodyStart = piece.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyStart < 0)
               continue;

            parts.Add(new ReportPart
            {
               Headers = piece.Substring(0, bodyStart),
               Body = piece.Substring(bodyStart + 4)
            });
         }

         return parts;
      }

      private static ReportPart PartWithContentType(string report, string contentType)
      {
         ReportPart part = PartsOf(report).FirstOrDefault(candidate =>
            candidate.Headers.IndexOf("Content-Type: " + contentType, StringComparison.OrdinalIgnoreCase) >= 0);
         if (part == null)
            Assert.Fail("The report carries no " + contentType + " part:\r\n" + report);

         return part;
      }

      /// <summary>
      ///    The per-recipient group of the delivery-status part for one address.
      ///    RFC 3464 2.3 separates the per-message fields and each recipient's
      ///    fields with a blank line.
      /// </summary>
      private static string RecipientGroupFor(string report, string address)
      {
         string deliveryStatus = PartWithContentType(report, "message/delivery-status").Body;

         string group = deliveryStatus.Split(new[] { "\r\n\r\n" }, StringSplitOptions.None).FirstOrDefault(candidate =>
            candidate.IndexOf("Final-Recipient: rfc822; " + address, StringComparison.OrdinalIgnoreCase) >= 0);
         if (group == null)
            Assert.Fail("The delivery-status part names no recipient group for " + address +
                        ". The part was:\r\n" + deliveryStatus);

         return group;
      }

      /// <summary>
      ///    Every assertion that any RFC 3464 report must satisfy, whatever the
      ///    failure was. Called from each test so that a regression in the shape
      ///    of the report fails everywhere rather than in one place.
      /// </summary>
      private static void AssertWellFormedReport(string report, string failedAddress)
      {
         string topLevel = TopLevelHeaders(report);

         StringAssert.Contains("multipart/report", topLevel,
            "The report is not a multipart/report, so nothing can recognise it as a delivery " +
            "status notification. Top-level headers were:\r\n" + topLevel);

         StringAssert.Contains("report-type=delivery-status", topLevel,
            "multipart/report without report-type=delivery-status (RFC 3462 3) does not say WHICH " +
            "kind of report this is. Top-level headers were:\r\n" + topLevel);

         List<ReportPart> parts = PartsOf(report);

         Assert.That(parts.Count, Is.EqualTo(3),
            "RFC 3464 2.1 describes three parts - the human-readable notice, the delivery status, " +
            "and the original headers. Found " + parts.Count + ":\r\n" + report);

         // Part one is the human-readable notice, and it is FIRST. A client that
         // knows nothing of multipart/report shows the first part, so this is
         // what keeps the change invisible to one.
         StringAssert.Contains("text/plain", parts[0].Headers,
            "The first part of the report is not the human-readable text. A mail client that does " +
            "not understand multipart/report shows the first part, so this ordering is what stops " +
            "the machine-readable half being what a person sees. Part headers were:\r\n" + parts[0].Headers);

         StringAssert.Contains("Your message did not reach some or all of the intended recipients", parts[0].Body,
            "The human-readable part no longer carries the SEND_FAILED_NOTIFICATION server message. " +
            "That text is translatable and administrator-editable, and it must survive unchanged. " +
            "Part body was:\r\n" + parts[0].Body);

         StringAssert.Contains("message/delivery-status", parts[1].Headers,
            "The second part is not the machine-readable delivery status:\r\n" + parts[1].Headers);

         StringAssert.Contains("rfc822-headers", parts[2].Headers,
            "The third part is not the original message's headers (RFC 3462 3 names " +
            "text/rfc822-headers for the headers-only form):\r\n" + parts[2].Headers);

         // The per-message field RFC 3464 2.2.2 requires.
         StringAssert.Contains("Reporting-MTA: dns; ", parts[1].Body,
            "The delivery-status part has no Reporting-MTA, which RFC 3464 2.2.2 requires:\r\n" +
            parts[1].Body);

         string group = RecipientGroupFor(report, failedAddress);

         StringAssert.Contains("Action: failed", group,
            "The recipient group does not say the delivery failed:\r\n" + group);

         Assert.That(Regex.IsMatch(group, @"^Status: [245]\.\d{1,3}\.\d{1,3}\r?$", RegexOptions.Multiline), Is.True,
            "The Status field is missing or is not a well-formed RFC 3463 enhanced status code:\r\n" + group);
      }

      /// <summary>
      ///    Fills a mailbox's quota so the next message cannot be delivered to it.
      ///    The same shape SMTP.Basics uses for its bounce tests.
      /// </summary>
      private static string TwoMegabytesOfText()
      {
         var builder = new StringBuilder();
         for (var i = 0; i < 11000; i++)
            builder.Append(
               "1234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890\r\n");

         return builder.ToString();
      }

      private void NewSenderAndFullMailbox(out Account sender, out Account recipient)
      {
         int sequence = NextAccountSequence();

         sender = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "dsn-sender-" + sequence + "@example.test", Password);
         recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "dsn-full-" + sequence + "@example.test", Password);

         recipient.MaxSize = 1;
         recipient.Save();
      }

      [Test]
      [Description("A bounce is a multipart/report with the human text first, a delivery-status part and the original headers.")]
      public void TheReportIsAMultipartReportWithTheHumanTextStillFirst()
      {
         Account sender, recipient;
         NewSenderAndFullMailbox(out sender, out recipient);

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "A subject worth quoting back",
            TwoMegabytesOfText());

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         string report = Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, Password);

         AssertWellFormedReport(report, recipient.Address);

         // The third part exists to let the sender identify WHICH message this is
         // about, so it has to carry the original message's own headers.
         StringAssert.Contains("Subject: A subject worth quoting back",
            PartWithContentType(report, "text/rfc822-headers").Body,
            "The original-headers part does not contain the failed message's Subject, so the sender " +
            "cannot tell which of their messages this report is about.");

         // And the sentence a person actually reads is still there, unchanged.
         StringAssert.Contains("Inbox is full", PartWithContentType(report, "text/plain").Body,
            "The human-readable explanation lost its text.");
      }

      [Test]
      [Description("A full mailbox is reported as 5.2.2 mailbox full, not as a bad address and not as an undefined failure.")]
      public void AFullMailboxIsReportedAsMailboxFull()
      {
         Account sender, recipient;
         NewSenderAndFullMailbox(out sender, out recipient);

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "Too big", TwoMegabytesOfText());

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         string report = Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, Password);

         AssertWellFormedReport(report, recipient.Address);

         string group = RecipientGroupFor(report, recipient.Address);

         StringAssert.Contains("Final-Recipient: rfc822; " + recipient.Address, group,
            "The report does not name the failed recipient:\r\n" + group);

         StringAssert.Contains("Status: 5.2.2", group,
            "A full mailbox must be reported as RFC 3463 X.2.2 'mailbox full'. Reporting it as " +
            "anything else - and 5.1.1 in particular - tells a list manager to unsubscribe an " +
            "address that is perfectly good. The recipient group was:\r\n" + group);

         StringAssert.DoesNotContain("Status: 5.0.0", group,
            "The status is the undefined catch-all, which is what an implementation that hard-codes " +
            "one code produces:\r\n" + group);

         StringAssert.DoesNotContain("Status: 5.1.1", group,
            "A full mailbox has been reported as a bad destination address:\r\n" + group);

         // Nothing here talked to a remote server, so there is nothing honest to
         // put in either field. RFC 3464 2.3: an omitted optional field asserts
         // nothing; a filled-in one asserts something.
         StringAssert.DoesNotContain("Remote-MTA", group,
            "A Remote-MTA is named for a delivery that never left this server:\r\n" + group);

         StringAssert.DoesNotContain("Diagnostic-Code", group,
            "A remote diagnostic is quoted for a failure no remote server was involved in:\r\n" + group);
      }

      [Test]
      [Description("A remote server's 550 at RCPT TO becomes 5.1.1, with its own reply quoted as the Diagnostic-Code.")]
      public void ARemotePermanentRefusalCarriesTheRemotesOwnDiagnostic()
      {
         int sequence = NextAccountSequence();

         Account sender = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "dsn-relay-" + sequence + "@example.test", Password);

         const string RemoteAddress = "test@dummy-example.com";

         var deliveryResults = new Dictionary<string, int> { [RemoteAddress] = 550 };

         int smtpServerPort = TestSetup.GetNextFreePort();

         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(0, smtpServerPort, false);

            var smtp = new SmtpClientSimulator();
            smtp.Send(sender.Address, new List<string> { RemoteAddress }, "Refused", "Body text.");

            server.WaitForCompletion();

            CustomAsserts.AssertRecipientsInDeliveryQueue(0, true);

            string report = Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, Password);

            AssertWellFormedReport(report, RemoteAddress);

            string group = RecipientGroupFor(report, RemoteAddress);

            // RFC 5321 4.2.3 gives 550 at RCPT TO the meaning "mailbox
            // unavailable", which is RFC 3463 X.1.1. The simulator's reply
            // carries no enhanced code of its own, so this is the reply-code
            // mapping being exercised.
            StringAssert.Contains("Status: 5.1.1", group,
               "A permanent refusal of one recipient by the receiving server must be reported as " +
               "5.1.1, not as the undefined 5.0.0 and not as a mailbox-full code:\r\n" + group);

            StringAssert.DoesNotContain("Status: 5.0.0", group,
               "The status fell back to the undefined code although the reply code said more:\r\n" + group);

            StringAssert.DoesNotContain("Status: 5.2.2", group,
               "A refused address has been reported as a full mailbox:\r\n" + group);

            // RFC 3464 2.3.6: the diagnostic given by the OTHER MTA, verbatim.
            StringAssert.Contains("Diagnostic-Code: smtp; 550 " + RemoteAddress, group,
               "The remote server's own reply is not quoted, so the report says a delivery failed " +
               "without saying what the receiving server actually said:\r\n" + group);

            // RFC 3464 2.3.4. The route names 127.0.0.1 as its target host, so
            // that is the host this server really spoke to.
            StringAssert.Contains("Remote-MTA: dns; 127.0.0.1", group,
               "The report does not name the MTA that refused the message:\r\n" + group);
         }
      }

      [Test]
      [Description("A Sieve reject becomes 5.7.1 - refused by policy - which is neither a mailbox problem nor a bad address.")]
      public void ASieveRejectIsReportedAsDeliveryNotAuthorized()
      {
         int sequence = NextAccountSequence();

         Account sender = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "dsn-rej-sender-" + sequence + "@example.test", Password);
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "dsn-rej-rcpt-" + sequence + "@example.test", Password);

         recipient.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, recipient,
            new object[]
            {
               "require \"reject\";\r\n" +
               "reject \"This mailbox does not accept unsolicited proposals.\";\r\n"
            });

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "A proposal", "Body text.");

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         string report = Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, Password);

         AssertWellFormedReport(report, recipient.Address);

         string group = RecipientGroupFor(report, recipient.Address);

         // RFC 5429 2.1.2 gives "550 5.7.1" as the reply a Sieve reject produces
         // over SMTP; this is the same refusal arriving one step later.
         StringAssert.Contains("Status: 5.7.1", group,
            "A message refused by the recipient's own filter must be reported as 5.7.1, 'delivery " +
            "not authorized, message refused'. It is not a mailbox problem and the address is not " +
            "bad, so neither of those codes would be true:\r\n" + group);

         StringAssert.DoesNotContain("Status: 5.0.0", group,
            "The status is the undefined catch-all:\r\n" + group);

         StringAssert.DoesNotContain("Status: 5.2.2", group,
            "A policy refusal has been reported as a full mailbox:\r\n" + group);

         // The reason the script gave still reaches the person reading it.
         StringAssert.Contains("This mailbox does not accept unsolicited proposals.",
            PartWithContentType(report, "text/plain").Body,
            "The human-readable part lost the reason the script gave.");
      }
   }
}
