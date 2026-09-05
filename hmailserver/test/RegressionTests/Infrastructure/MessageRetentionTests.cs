// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Message retention: delivered mail older than a mailbox's policy is removed.
   ///
   ///    The policy is Domain.MessageRetentionDays (0 = none) with an account override
   ///    (Account.MessageRetentionDays: 0 = the domain's, -1 = keep forever). The sweep
   ///    is a scheduled task; Utilities.RunMessageRetention runs it on demand and
   ///    returns what it removed, which is what these tests call. Messages are aged by
   ///    rewriting their stored creation time through the database, because the only
   ///    other way to have a thirty-day-old message is to wait thirty days.
   ///
   ///    The most important test here is the one with no policy anywhere, which is
   ///    every installation on the day this ships: the sweep removes nothing.
   /// </summary>
   [TestFixture]
   public class MessageRetentionTests : TestFixtureBase
   {
      private Account _account;
      private int _delivered;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "retention@" + _domain.Name, "test");
         _delivered = 0;
         _domain.MessageRetentionDays = 0;
         _domain.Save();
      }

      [TearDown]
      public new void TearDown()
      {
         _domain.MessageRetentionDays = 0;
         _domain.Save();
      }

      [Test]
      public void ADomainPolicyRemovesOnlyTheMessagesOlderThanIt()
      {
         _domain.MessageRetentionDays = 30;
         _domain.Save();

         Deliver("old one");
         AgeEveryMessage(40);
         Deliver("recent one");

         Assert.AreEqual(1, _application.Utilities.RunMessageRetention());

         // The count first: the text helper deletes what it retrieves.
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);
         var remaining = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, "test");
         Assert.That(remaining, Does.Contain("recent one"));
      }

      [Test]
      public void AnAccountOverrideShorterThanTheDomainsApplies()
      {
         _domain.MessageRetentionDays = 365;
         _domain.Save();
         _account.MessageRetentionDays = 7;
         _account.Save();

         Deliver("ten days old");
         AgeEveryMessage(10);

         Assert.AreEqual(1, _application.Utilities.RunMessageRetention());
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 0);
      }

      [Test]
      public void AnAccountMarkedKeepForeverIsLeftAloneWhateverTheDomainSays()
      {
         _domain.MessageRetentionDays = 1;
         _domain.Save();
         _account.MessageRetentionDays = -1;
         _account.Save();

         Deliver("ancient");
         AgeEveryMessage(400);

         Assert.AreEqual(0, _application.Utilities.RunMessageRetention());
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);
      }

      [Test]
      public void AnAccountPolicyAppliesWithoutADomainPolicy()
      {
         _account.MessageRetentionDays = 3;
         _account.Save();

         Deliver("a week old");
         AgeEveryMessage(7);
         Deliver("today");

         Assert.AreEqual(1, _application.Utilities.RunMessageRetention());
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);
      }

      [Test]
      public void WithNoPolicyAnywhereTheSweepRemovesNothing()
      {
         Deliver("ancient");
         AgeEveryMessage(4000);

         Assert.AreEqual(0, _application.Utilities.RunMessageRetention());
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);
      }

      [Test]
      public void ASelectedImapSessionIsToldWhatTheSweepRemoved()
      {
         _domain.MessageRetentionDays = 30;
         _domain.Save();

         Deliver("old one");
         AgeEveryMessage(40);
         Deliver("recent one");

         var imap = new ImapClientSimulator();
         try
         {
            Assert.IsTrue(imap.ConnectAndLogon(_account.Address, "test"));
            Assert.IsTrue(imap.SelectFolder("INBOX"));

            Assert.AreEqual(1, _application.Utilities.RunMessageRetention());

            // NOOP is where an untagged EXPUNGE may be sent; after it the session's
            // count is the folder's.
            var response = imap.SendSingleCommand("A34 NOOP");
            Assert.That(response, Does.Contain("EXPUNGE"), response);
            Assert.AreEqual(1, imap.GetMessageCount("INBOX"));
         }
         finally
         {
            imap.Disconnect();
         }
      }

      [Test]
      public void ThePropertiesRoundTripThroughTheApi()
      {
         _domain.MessageRetentionDays = 90;
         _domain.Save();
         _account.MessageRetentionDays = -1;
         _account.Save();

         _settings.Cache.Clear();

         Assert.AreEqual(90, _application.Domains.get_ItemByName(_domain.Name).MessageRetentionDays);
         Assert.AreEqual(-1, _application.Domains.get_ItemByName(_domain.Name).Accounts.get_ItemByAddress(_account.Address).MessageRetentionDays);
      }

      // ----------------------------------------------------------------------

      private void Deliver(string subject)
      {
         var smtp = new SmtpClientSimulator();
         smtp.Send("sender@example.com", _account.Address, subject, "Body of " + subject);
         _delivered++;
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", _delivered);
      }

      /// <summary>
      ///    Moves the stored creation time of every message the account holds so far
      ///    back by the given number of days. Called between deliveries, so the messages
      ///    delivered before it are old and the ones after it are not.
      /// </summary>
      private void AgeEveryMessage(int days)
      {
         var created = DateTime.Now.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
         _application.Database.ExecuteSQL("update hm_messages set messagecreatetime = '" + created + "' where messageaccountid = " + _account.ID);
      }
   }
}
