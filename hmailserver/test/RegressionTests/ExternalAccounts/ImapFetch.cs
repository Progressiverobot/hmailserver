// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.ExternalAccounts
{
   /// <summary>
   ///    An external account of ServerType 1 (IMAP) collects a remote INBOX into a local
   ///    account: every message once, by UID, remembered across polls as
   ///    "&lt;UIDVALIDITY&gt;:&lt;UID&gt;"; deleted on the far side straight after collection
   ///    when DaysToKeepMessages is 0 and left there otherwise. The far side is a
   ///    ScriptedImapServer, one session per poll, so a test can see exactly what the
   ///    fetcher asked for and make the server misbehave on purpose.
   /// </summary>
   [TestFixture]
   public class ImapFetch : TestFixtureBase
   {
      // One port per test, inside this fixture's assigned range, so a socket still in
      // TIME_WAIT from one test cannot be what the next one accepts on.
      private const int PortCollectedOnce = 9485;
      private const int PortDeletedWhenAsked = 9486;
      private const int PortRefusedLogon = 9487;
      private const int PortUidValidity = 9488;
      private const int PortTruncated = 9489;
      private const int PortVanished = 9490;

      private const string Password = "test";
      private const string RemotePassword = "far-side-secret-2026";

      private Account _account;

      [SetUp]
      public void SetUpTest()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "collector@example.test", Password);
      }

      private static string MessageText(string subject)
      {
         return "From: sender@dummy-example.com\r\n" +
                "To: collector@example.test\r\n" +
                "Subject: " + subject + "\r\n" +
                "\r\n" +
                "Body of " + subject + ".";
      }

      private static ScriptedImapServer.RemoteMailbox Mailbox(params string[] subjects)
      {
         var mailbox = new ScriptedImapServer.RemoteMailbox();
         for (var i = 0; i < subjects.Length; i++)
            mailbox.Add(new ScriptedImapServer.RemoteMessage(101 + i, MessageText(subjects[i])));
         return mailbox;
      }

      private FetchAccount CreateImapFetchAccount(int port, int daysToKeepMessages)
      {
         var fetchAccount = _account.FetchAccounts.Add();
         fetchAccount.Enabled = true;
         // Long enough that the scheduler never starts a fetch of its own during a test:
         // every fetch here is driven by DownloadNow.
         fetchAccount.MinutesBetweenFetch = 60;
         fetchAccount.Name = "Scripted IMAP";
         fetchAccount.ServerType = 1;
         fetchAccount.Username = "remote@dummy-example.com";
         fetchAccount.Password = RemotePassword;
         fetchAccount.UseSSL = false;
         fetchAccount.ServerAddress = "localhost";
         fetchAccount.Port = port;
         fetchAccount.ProcessMIMERecipients = false;
         fetchAccount.DaysToKeepMessages = daysToKeepMessages;
         fetchAccount.UseAntiSpam = false;
         fetchAccount.UseAntiVirus = false;
         fetchAccount.Save();
         return fetchAccount;
      }

      /// <summary>
      ///    A fetch is asynchronous: DownloadNow only queues it. The account stays locked
      ///    for the duration, so waiting for the lock to clear is what tells us the whole
      ///    session - including the UID bookkeeping - has finished.
      /// </summary>
      private static void WaitForFetchToFinish(FetchAccount fetchAccount)
      {
         var timeoutTime = DateTime.Now.Add(TimeSpan.FromSeconds(60));
         while (DateTime.Now < timeoutTime)
         {
            if (!fetchAccount.IsLocked)
               return;
            Thread.Sleep(100);
         }

         Assert.Fail("The external account was still locked after 60 seconds. Log:\r\n" +
                     LogHandler.ReadCurrentDefaultLog());
      }

      /// <summary>One poll: the scripted server answers one session, the fetcher runs it to the end.</summary>
      private static void Poll(ScriptedImapServer server, FetchAccount fetchAccount)
      {
         server.StartListen();
         fetchAccount.DownloadNow();
         WaitForFetchToFinish(fetchAccount);
         server.WaitForCompletion();
      }

      /// <summary>
      ///    Waits for everything a fetch delivered to leave the delivery queue, then asserts
      ///    the mailbox holds exactly this many messages - so a count is never taken while a
      ///    copy is still queued.
      /// </summary>
      private void AssertDeliveredMessageCount(int expectedCount)
      {
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         Pop3ClientSimulator.AssertMessageCount(_account.Address, Password, expectedCount);
      }

      [Test]
      [Description("Two messages in the remote INBOX are collected once; a second poll asks for nothing; a third message arriving later is collected alone; the copies carry the external account's name.")]
      public void MessagesAreCollectedOnceByUid()
      {
         var mailbox = Mailbox("First", "Second");
         var fetchAccount = CreateImapFetchAccount(PortCollectedOnce, 30);

         using (var server = new ScriptedImapServer(PortCollectedOnce, mailbox))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101, 102 }, server.FetchedUids);
            CollectionAssert.IsEmpty(server.StoredDeletedUids, "Days to keep is 30, so nothing is deleted on the far side.");
         }
         AssertDeliveredMessageCount(2);

         using (var server = new ScriptedImapServer(PortCollectedOnce, mailbox))
         {
            Poll(server, fetchAccount);
            CollectionAssert.IsEmpty(server.FetchedUids, "Both messages are on record; the second poll must ask for neither.");
         }
         AssertDeliveredMessageCount(2);

         mailbox.Add(new ScriptedImapServer.RemoteMessage(103, MessageText("Third")));
         using (var server = new ScriptedImapServer(PortCollectedOnce, mailbox))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 103 }, server.FetchedUids);
         }
         AssertDeliveredMessageCount(3);

         var delivered = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, Password);
         StringAssert.Contains("X-hMailServer-ExternalAccount: Scripted IMAP", delivered,
            "A collected message carries the external account's name, as a POP3 collection does.");
      }

      [Test]
      [Description("With DaysToKeepMessages 0 the collected messages are flagged \\Deleted and expunged in the same session; the local copies stay.")]
      public void CollectedMessagesAreDeletedFromTheFarSideWhenAsked()
      {
         var mailbox = Mailbox("Take me", "And me");
         var fetchAccount = CreateImapFetchAccount(PortDeletedWhenAsked, 0);

         using (var server = new ScriptedImapServer(PortDeletedWhenAsked, mailbox))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101, 102 }, server.FetchedUids);
            CollectionAssert.AreEqual(new[] { 101, 102 }, server.StoredDeletedUids);
            Assert.AreEqual(1, server.ExpungeCount, "One EXPUNGE at the end of the session, not one per message.");
         }

         Assert.AreEqual(0, mailbox.Count, "The far side is empty after the expunge.");
         AssertDeliveredMessageCount(2);
      }

      [Test]
      [Description("A refused logon collects nothing, is reported as an error, and the password does not reach the log.")]
      public void ARefusedLogonCollectsNothing()
      {
         var mailbox = Mailbox("Unreachable");
         var fetchAccount = CreateImapFetchAccount(PortRefusedLogon, 30);

         using (var server = new ScriptedImapServer(PortRefusedLogon, mailbox) { RefuseLogin = true })
         {
            Poll(server, fetchAccount);
            CollectionAssert.IsEmpty(server.FetchedUids);
            StringAssert.Contains("\"remote@dummy-example.com\" \"" + RemotePassword + "\"", server.LoginLine,
               "LOGIN sends both credentials as quoted strings.");
         }

         AssertDeliveredMessageCount(0);
         Assert.AreEqual(1, mailbox.Count, "Nothing was deleted on the far side.");

         CustomAsserts.AssertReportedError("refused the logon");

         var log = LogHandler.ReadCurrentDefaultLog();
         StringAssert.DoesNotContain(RemotePassword, log, "The LOGIN line is logged with the credentials masked.");
      }

      [Test]
      [Description("A mailbox whose UIDVALIDITY changes is a different mailbox: the same UIDs are collected again.")]
      public void AChangedUidValidityCollectsAfresh()
      {
         var mailbox = Mailbox("Before", "And before");
         var fetchAccount = CreateImapFetchAccount(PortUidValidity, 30);

         using (var server = new ScriptedImapServer(PortUidValidity, mailbox) { UidValidity = 7 })
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101, 102 }, server.FetchedUids);
         }
         AssertDeliveredMessageCount(2);

         using (var server = new ScriptedImapServer(PortUidValidity, mailbox) { UidValidity = 8 })
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101, 102 }, server.FetchedUids,
               "UID 101 under UIDVALIDITY 8 is not the UID 101 that was collected under 7.");
         }
         AssertDeliveredMessageCount(4);
      }

      [Test]
      [Description("A download the far side cuts short is not delivered and not recorded; the next poll collects the message whole.")]
      public void ATruncatedDownloadIsCollectedWholeOnTheNextPoll()
      {
         var mailbox = Mailbox("Cut short", "Never asked for");
         var fetchAccount = CreateImapFetchAccount(PortTruncated, 30);

         using (var server = new ScriptedImapServer(PortTruncated, mailbox) { TruncateFetchAfterBytes = 40 })
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101 }, server.FetchedUids, "The session dies on the first download.");
         }
         AssertDeliveredMessageCount(0);

         using (var server = new ScriptedImapServer(PortTruncated, mailbox))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101, 102 }, server.FetchedUids, "Neither message was recorded, so both are asked for.");
         }
         AssertDeliveredMessageCount(2);
      }

      [Test]
      [Description("A message that is gone between the SEARCH and its FETCH is skipped, the rest are collected, and it is collected if it is back on a later poll.")]
      public void AMessageGoneBeforeItsFetchIsSkipped()
      {
         var mailbox = Mailbox("Vanishes", "Stays");
         var fetchAccount = CreateImapFetchAccount(PortVanished, 30);

         using (var server = new ScriptedImapServer(PortVanished, mailbox))
         {
            server.VanishedUids.Add(101);
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101, 102 }, server.FetchedUids, "Both were asked for; the first came back empty.");
         }
         AssertDeliveredMessageCount(1);

         using (var server = new ScriptedImapServer(PortVanished, mailbox))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { 101 }, server.FetchedUids, "Not recorded when it came back empty, so asked for again; 102 is on record.");
         }
         AssertDeliveredMessageCount(2);
      }
   }
}
