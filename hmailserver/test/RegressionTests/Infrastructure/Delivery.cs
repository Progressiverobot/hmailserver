// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Collections.Generic;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   [TestFixture]
   public class BasicTests : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         SingletonProvider<TestSetup>.Instance.DisableSpamProtection();
      }

      [Test]
      public void TestAliases()
      {
         // Fetch default domain

         // Create another account
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test2@example.test", "test");

         // Add aliases
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "alias1@example.test", "test2@example.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "alias2@example.test", "test2@example.test");
         var smtpClientSimulator = new SmtpClientSimulator();

         // Spam folder
         smtpClientSimulator.Send("test@example.test", "test2@example.test", "Mail 1", "Mail 1");
         smtpClientSimulator.Send("test@example.test", "alias1@example.test", "Mail 2", "Mail 2");
         smtpClientSimulator.Send("test@example.test", "alias2@example.test", "Mail 3", "Mail 3");

         ImapClientSimulator.AssertMessageCount("test2@example.test", "test", "Inbox", 3);
      }

      [Test]
      public void TestMirror()
      {
         // Create a test account
         // Fetch the default domain
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mirror@example.test", "test");
         var account2 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mirror-test@example.test", "test");

         _settings.MirrorEMailAddress = "mirror-test@example.test";
         _settings.AddDeliveredToHeader = true;

         // Send 5 messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         for (var i = 0; i < 5; i++)
            smtpClientSimulator.Send("test@example.test", "mirror@example.test", "INBOX", "Mirror test message");

         // Check using POP3 that 5 messages exists.
         Pop3ClientSimulator.AssertMessageCount("mirror-test@example.test", "test", 5);

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(account2.Address, "test");

         Assert.IsTrue(message.Contains("Delivered-To: mirror@example.test"));
      }

      [Test]
      [Description("Issue 248, Delivered-To header invalid for mirror messages")]
      public void TestMirrorMultipleRecipients()
      {
         // Create a test account
         // Fetch the default domain
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mirror1@example.test", "test");
         var account2 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mirror2@example.test", "test");
         var account3 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mirror3@example.test", "test");

         var mirrorAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mirror-test@example.test",
            "test");

         _settings.MirrorEMailAddress = "mirror-test@example.test";
         _settings.AddDeliveredToHeader = true;

         // Send 5 messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("test@example.test",
            new List<string> { account1.Address, account2.Address, account3.Address },
            "INBOX", "Mirror test message");

         Pop3ClientSimulator.AssertMessageCount(mirrorAccount.Address, "test", 1);

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(mirrorAccount.Address, "test");

         Assert.IsTrue(
            message.Contains("Delivered-To: mirror1@example.test,mirror2@example.test,mirror3@example.test"));

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      [Test]
      [Description("Issue 248, Delivered-To header invalid for mirror messages")]
      public void TestMirrorMultipleRecipientsOver255Chars()
      {
         // Create a test account
         // Fetch the default domain
         var recipients = new List<string>();
         for (var i = 0; i < 20; i++)
         {
            var address = string.Format("mirror{0}@example.test", i);
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");
            recipients.Add(address);
         }

         var mirrorAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mirror-test@example.test",
            "test");

         _settings.MirrorEMailAddress = "mirror-test@example.test";
         _settings.AddDeliveredToHeader = true;

         // Send 1 messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("test@example.test", recipients, "INBOX", "Mirror test message");

         Pop3ClientSimulator.AssertMessageCount(mirrorAccount.Address, "test", 1);

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(mirrorAccount.Address, "test");

         Assert.IsTrue(
            message.Contains(
               "Delivered-To: mirror0@example.test,mirror1@example.test,mirror2@example.test,mirror3@example.test,mirror4@example.test,mirror5@example.test,mirror6@example.test,mirror7@example.test,mirror8@example.test,mirror9@example.test,mirror10@example.test,mirror11@example.test,m\r\n"));

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      [Test]
      public void DeliveryShouldSucceedAfterClearingDeliveryQueue()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         _application.GlobalObjects.DeliveryQueue.Clear();

         Assert.IsTrue(LogHandler.DefaultLogContains("Delivery queue cleared."));

         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send("test@example.test", "test@example.test", "INBOX", "Mirror test message");

         Pop3ClientSimulator.AssertMessageCount("test@example.test", "test", 1);
      }
   }
}