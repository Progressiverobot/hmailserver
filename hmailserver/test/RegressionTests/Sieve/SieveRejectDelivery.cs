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
   ///    reject and ereject (RFC 5429), asserted end to end on BOTH mailboxes: the
   ///    recipient's stays empty, and the sender RECEIVES a non-delivery report
   ///    carrying the script's reason. The second half is what distinguishes
   ///    reject from discard - a discard is silence, a reject is an answer - and
   ///    an implementation that merely dropped the message would pass every
   ///    recipient-side assertion and fail the sender-side one.
   ///
   ///    Both commands ride the same report machinery as a quota bounce, so its
   ///    loop guards (no bounce to a bounce, to a null sender, to auto-submitted
   ///    mail) apply to rejects without any new code path to get wrong.
   /// </summary>
   [TestFixture]
   public class SieveRejectDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static int accountSequence_;

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      private void NewPair(string recipientScript, out Account sender, out Account recipient)
      {
         accountSequence_++;
         sender = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-rej-sender-" + accountSequence_ + "@example.test", Password);
         recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-rej-rcpt-" + accountSequence_ + "@example.test", Password);

         SetScript(recipient, recipientScript);
      }

      [Test]
      [Description("reject: the recipient keeps nothing and the sender receives a report carrying the reason.")]
      public void RejectDropsTheCopyAndAnswersTheSender()
      {
         Account sender, recipient;
         NewPair(
            "require \"reject\";\r\n" +
            "reject \"This mailbox does not accept unsolicited proposals.\";\r\n",
            out sender, out recipient);

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "A proposal", "Body text.");

         // The report is itself a delivery; wait for the queue to finish both.
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder recipientInbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(recipientInbox, 0);

         string report = Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, Password);
         StringAssert.Contains("This mailbox does not accept unsolicited proposals.", report,
            "The sender's report does not carry the script's reason - a reject without the reason is " +
            "indistinguishable from a random delivery failure.");
      }

      [Test]
      [Description("ereject behaves identically at this point in the pipeline: report sent, nothing kept.")]
      public void ErejectAlsoAnswersTheSender()
      {
         Account sender, recipient;
         NewPair(
            "require \"ereject\";\r\n" +
            "ereject \"Refused by policy.\";\r\n",
            out sender, out recipient);

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "Anything", "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder recipientInbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(recipientInbox, 0);

         string report = Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, Password);
         StringAssert.Contains("Refused by policy.", report,
            "The ereject report does not carry the reason.");
      }

      /// <summary>
      ///    The negative control: an ordinary script sends no report. An
      ///    implementation that bounced everything would pass the tests above and
      ///    fail here.
      /// </summary>
      [Test]
      [Description("A script that keeps the message sends no report - the control the rest depend on.")]
      public void AKeepingScriptSendsNoReport()
      {
         Account sender, recipient;
         NewPair("keep;\r\n", out sender, out recipient);

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "Ordinary mail", "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder recipientInbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(recipientInbox, 1);

         IMAPFolder senderInbox = sender.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(senderInbox, 0);
      }

      /// <summary>
      ///    RFC 5429 2.1.1 forbids combining a reject with a delivery action; the
      ///    resolution here is the one that never misleads the sender: delivery
      ///    wins, the reject is cancelled. "Delivered but also bounced" would tell
      ///    the sender their mail failed when it is sitting in the inbox.
      /// </summary>
      [Test]
      [Description("A reject followed by a delivery action is cancelled: the message is delivered and no report is sent.")]
      public void ADeliveryActionAfterARejectWins()
      {
         Account sender, recipient;
         NewPair(
            "require \"reject\";\r\n" +
            "reject \"changed my mind\";\r\n" +
            "keep;\r\n",
            out sender, out recipient);

         SmtpClientSimulator.StaticSend(sender.Address, recipient.Address, "Contradictory script", "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder recipientInbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(recipientInbox, 1);

         IMAPFolder senderInbox = sender.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(senderInbox, 0);
      }
   }
}
