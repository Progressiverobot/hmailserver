// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    A recipient whose mailbox is already full is refused during the SMTP
   ///    conversation instead of being accepted and bounced.
   ///
   ///    The behaviour this replaces is not merely untidy. Quotas were checked only
   ///    at delivery, so an over-quota mailbox meant the message was accepted, then
   ///    answered with a non-delivery report addressed to the envelope sender - and
   ///    for the traffic that fills mailboxes fastest, the envelope sender is forged.
   ///    The report therefore goes to somebody who never sent anything, from this
   ///    server, which is backscatter: this server becomes the one sending unwanted
   ///    mail, and earns the reputation damage on the forger's behalf. A mailbox left
   ///    full over a weekend generates one of those for every message that arrives.
   ///
   ///    The refusal is 452 with the enhanced code 4.2.2, which is TEMPORARY, and
   ///    that is the substance rather than a detail. A 550 would destroy on the spot
   ///    the mail a legitimate sender is entitled to have retried; 452 leaves the
   ///    message in the sending server's queue, where it waits for the mailbox to be
   ///    emptied and, if it never is, is eventually reported by the machine that
   ///    actually knows who the sender is.
   ///
   ///    Both tests use the same over-quota mailbox and differ only in
   ///    RejectFullMailboxAtRcpt, so the second is a genuine control: it demonstrates
   ///    the backscatter by producing it.
   /// </summary>
   [TestFixture]
   public class OverQuotaRecipientRefusal : TestFixtureBase
   {
      private const string FullMailbox = "fullbox@example.test";
      private const string BounceTarget = "quotabounce@example.test";

      /// <summary>
      ///    Puts more than a megabyte into the mailbox and then lowers the quota to
      ///    one megabyte, which is the only way to reach the state being tested.
      ///
      ///    Filling it by SMTP cannot get there: delivery already refuses anything
      ///    that would take a mailbox past its quota, so a mailbox can never exceed
      ///    one by being delivered to. It arrives over quota the way real ones do -
      ///    the limit is lowered underneath it, or it was filled through a path with
      ///    no quota check. APPEND is that path here, and one 1.5 MB message is
      ///    cheaper than the twenty SMTP transactions it would otherwise take.
      /// </summary>
      private void FillTheMailboxAndLowerTheQuota()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, FullMailbox, "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, BounceTarget, "test");

         var message = new StringBuilder();
         message.Append("From: filler@example.test\r\n");
         message.Append("Subject: filler\r\n\r\n");
         message.Append(new string('x', 1500 * 1024));

         var imap = new ImapClientSimulator();
         imap.Connect();
         imap.Logon(FullMailbox, "test");

         string appended = imap.SendSingleCommandWithLiteral(
            "A01 APPEND INBOX {" + message.Length + "}", message.ToString());

         StringAssert.Contains("A01 OK", appended,
            "The filler message must be stored, or the mailbox is not over quota and " +
            "neither test below is testing anything. Got: " + appended);

         imap.Disconnect();

         account.MaxSize = 1; // MB, against a mailbox now holding about 1.5.
         account.Save();
      }

      /// <summary>
      ///    Opens an SMTP conversation and returns the server's answer to RCPT TO.
      ///    Raw rather than through the simulator's Send helpers because the answer
      ///    to that one command IS the assertion - a helper that throws on a refusal
      ///    would hide the code being checked.
      /// </summary>
      private static string RcptResponseFor(string recipient, string envelopeSender)
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(25), "Could not connect to the SMTP server.");
         ClassicAssert.IsTrue(socket.Receive().StartsWith("220"));
         ClassicAssert.IsTrue(socket.SendAndReceive("EHLO example.test\r\n").StartsWith("250"));

         string mailFrom = socket.SendAndReceive("MAIL FROM:<" + envelopeSender + ">\r\n");
         ClassicAssert.IsTrue(mailFrom.StartsWith("250"), mailFrom);

         string rcpt = socket.SendAndReceive("RCPT TO:<" + recipient + ">\r\n");

         socket.SendAndReceive("QUIT\r\n");
         socket.Disconnect();

         return rcpt;
      }

      [Test]
      [Description("A mailbox already at its quota is refused at RCPT TO with a temporary 452 4.2.2, so no message is accepted that could only be answered with a bounce to a forged sender")]
      public void AFullMailboxIsRefusedDuringTheConversation()
      {
         FillTheMailboxAndLowerTheQuota();

         string rcpt = RcptResponseFor(FullMailbox, "outsider@unaligned-envelope.test");

         ClassicAssert.IsTrue(rcpt.StartsWith("452"), "A full mailbox must be refused at RCPT TO. Got: " + rcpt);

         // Temporary, and asserted as such rather than merely "a failure". A
         // permanent refusal here would throw away mail that will be deliverable as
         // soon as somebody empties the mailbox, which is usually within a day.
         ClassicAssert.IsFalse(rcpt.StartsWith("5"), "The refusal must be temporary, not permanent. Got: " + rcpt);

         // RFC 3463 4.2.2, "mailbox full" - the code that tells the sending server
         // what to say if it eventually gives up.
         StringAssert.Contains("4.2.2", rcpt, "The refusal must carry the mailbox-full enhanced code. Got: " + rcpt);

         // And nothing was queued: the whole point is that no message exists to be
         // bounced. A recipient refused at RCPT never reaches the delivery queue.
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      [Test]
      [Description("With RejectFullMailboxAtRcpt=0 the old behaviour returns in full: the message is accepted and the envelope sender receives the bounce, which is the backscatter the default prevents")]
      public void WithTheCheckDisabledTheMessageIsAcceptedAndBouncedInstead()
      {
         FillTheMailboxAndLowerTheQuota();

         try
         {
            ServerIniFile.SetSetting("RejectFullMailboxAtRcpt", "0");
            RestartServerAndReacquireCom();

            // Every COM proxy taken above is dead from here on. Nothing below needs
            // one: the SMTP and POP3 simulators are raw sockets.
            string rcpt = RcptResponseFor(FullMailbox, BounceTarget);

            ClassicAssert.IsTrue(rcpt.StartsWith("250"),
               "With the check off, RCPT TO must be accepted exactly as it was before this " +
               "setting existed. If this is a 452 the setting is not an escape hatch, and the " +
               "test above proves nothing about which mechanism produced it. Got: " + rcpt);

            SmtpClientSimulator.StaticSend(BounceTarget, FullMailbox, "into a full mailbox", "Body.");

            // The bounce. This is what the default now prevents - and the envelope
            // sender it is addressed to is chosen by whoever connected, which is why
            // sending it to a stranger is the failure mode that matters.
            string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(BounceTarget, "test");

            StringAssert.Contains("Inbox is full", bounce,
               "With the check off the message is accepted and then answered with a non-delivery " +
               "report to the envelope sender - the behaviour the default replaces. Got: " + bounce);
         }
         finally
         {
            ServerIniFile.SetSetting("RejectFullMailboxAtRcpt", null);
            RestartServerAndReacquireCom();
         }
      }
   }
}
