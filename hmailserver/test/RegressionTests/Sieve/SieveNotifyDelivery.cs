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
   ///    enotify (RFC 5435) with the mailto method (RFC 5436), asserted on the
   ///    NOTIFICATION ARRIVING in the target mailbox - a real generated message,
   ///    read back with its headers, because the loop markers on it (null return
   ///    path, Auto-Submitted: auto-notified) are what make the feature safe to
   ///    have and are exactly what a summary-token assertion cannot see.
   ///
   ///    The loop control is the test that matters: a message that is ITSELF
   ///    auto-submitted must not be notified about, or two servers notifying each
   ///    other's contact addresses ping-pong for ever.
   /// </summary>
   [TestFixture]
   public class SieveNotifyDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static int accountSequence_;

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      private void NewPair(out Account recipient, out Account watcher)
      {
         accountSequence_++;
         recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-ntf-rcpt-" + accountSequence_ + "@example.test", Password);
         watcher = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-ntf-watch-" + accountSequence_ + "@example.test", Password);

         SetScript(recipient,
            "require \"enotify\";\r\n" +
            "notify :message \"Something arrived\" \"mailto:" + watcher.Address + "\";\r\n");
      }

      [Test]
      [Description("The notification arrives in the target mailbox, with the loop markers on it.")]
      public void TheNotificationArrivesWithLoopMarkers()
      {
         Account recipient, watcher;
         NewPair(out recipient, out watcher);

         SmtpClientSimulator.StaticSend("sieve-ntf-sender@example.test", recipient.Address, "Original subject", "Body.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         // The triggering message delivered normally...
         IMAPFolder recipientInbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(recipientInbox, 1);

         // ...and the watcher received the notification.
         string notification = Pop3ClientSimulator.AssertGetFirstMessageText(watcher.Address, Password);

         StringAssert.Contains("Something arrived", notification,
            "The ':message' subject is not on the notification.");
         StringAssert.Contains("Auto-Submitted: auto-notified", notification,
            "The notification lacks Auto-Submitted: auto-notified (RFC 5436 2.7.1) - the marker that " +
            "stops the next auto-responder answering it. Without it this feature is a loop generator.");
         StringAssert.Contains("Return-Path: <>", notification,
            "The notification's envelope sender is not null, so a bounce of it can start an exchange.");
         StringAssert.Contains("sieve-ntf-sender@example.test", notification,
            "The notification body does not say who the original message was from.");
      }

      /// <summary>
      ///    The loop control. The triggering message carries Auto-Submitted, so
      ///    notifying about it is exactly the first half of a ping-pong between
      ///    two servers' contact addresses. It must be delivered normally and
      ///    notified about NOT AT ALL.
      /// </summary>
      [Test]
      [Description("A message that is itself auto-submitted is not notified about - the loop control.")]
      public void AnAutoSubmittedMessageIsNotNotifiedAbout()
      {
         Account recipient, watcher;
         NewPair(out recipient, out watcher);

         string raw =
            "From: robot@example.test\r\n" +
            "To: " + recipient.Address + "\r\n" +
            "Subject: Automated report\r\n" +
            "Auto-Submitted: auto-generated\r\n" +
            "\r\n" +
            "Nightly job output.\r\n";

         var smtp = new SmtpClientSimulator();
         smtp.SendRaw("robot@example.test", recipient.Address, raw);
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder recipientInbox = recipient.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(recipientInbox, 1);

         IMAPFolder watcherInbox = watcher.IMAPFolders.get_ItemByName("INBOX");
         CustomAsserts.AssertFolderMessageCount(watcherInbox, 0);
      }

      [Test]
      [Description("valid_notify_method steers between the supported and unsupported method.")]
      public void ValidNotifyMethodAnswersHonestly()
      {
         accountSequence_++;
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-ntf-rcpt-" + accountSequence_ + "@example.test", Password);
         recipient.IMAPFolders.Add("MailtoOk");
         recipient.IMAPFolders.Add("XmppOk");

         SetScript(recipient,
            "require [\"enotify\", \"fileinto\"];\r\n" +
            "if valid_notify_method \"mailto:x@example.test\" {\r\n" +
            "  fileinto \"MailtoOk\";\r\n" +
            "}\r\n" +
            "if valid_notify_method \"xmpp:x@example.test\" {\r\n" +
            "  fileinto \"XmppOk\";\r\n" +
            "}\r\n");

         SmtpClientSimulator.StaticSend("sieve-ntf-sender@example.test", recipient.Address, "Probe", "Body.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         IMAPFolder mailtoOk = recipient.IMAPFolders.get_ItemByName("MailtoOk");
         CustomAsserts.AssertFolderMessageCount(mailtoOk, 1);

         IMAPFolder xmppOk = recipient.IMAPFolders.get_ItemByName("XmppOk");
         CustomAsserts.AssertFolderMessageCount(xmppOk, 0);
      }
   }
}
