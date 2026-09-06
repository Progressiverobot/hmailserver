// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.ExternalAccounts
{
   /// <summary>
   ///    An external IMAP account with MirrorFolders on is a migration rather than a
   ///    collection: every mailbox the remote server lists is collected into a local
   ///    folder of the same name - the message verbatim, its flags and its internal
   ///    date kept, filed straight into the folder rather than delivered - and each
   ///    mailbox remembers what it has collected on its own, so the same UID in two
   ///    mailboxes is two messages and a second poll takes only what is new. The far
   ///    side is a ScriptedImapServer handed the hierarchy to mirror.
   /// </summary>
   [TestFixture]
   public class ImapMirror : TestFixtureBase
   {
      // One port per test, inside this fixture's assigned range, so a socket still in
      // TIME_WAIT from one test cannot be what the next one accepts on.
      private const int PortHierarchy = 9491;
      private const int PortFlags = 9492;
      private const int PortFlagsAfterBody = 9493;
      private const int PortSecondPoll = 9494;
      private const int PortDelete = 9495;
      private const int PortRefused = 9496;
      private const int PortMirrorOff = 9497;

      private const string Password = "test";
      private const string InternalDate = "17-Jul-1996 02:44:25 -0700";

      private Account _account;

      [SetUp]
      public void SetUpTest()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "migrated@example.test", Password);
      }

      private static string MessageText(string subject)
      {
         return "From: sender@dummy-example.com\r\n" +
                "To: migrated@example.test\r\n" +
                "Subject: " + subject + "\r\n" +
                "\r\n" +
                "Body of " + subject + ".";
      }

      private static ScriptedImapServer.RemoteMailbox Mailbox(string name, params string[] subjects)
      {
         var mailbox = new ScriptedImapServer.RemoteMailbox(name);
         for (var i = 0; i < subjects.Length; i++)
            mailbox.Add(new ScriptedImapServer.RemoteMessage(101 + i, MessageText(subjects[i])));
         return mailbox;
      }

      private FetchAccount CreateFetchAccount(int port, int daysToKeepMessages, bool mirror)
      {
         var fetchAccount = _account.FetchAccounts.Add();
         fetchAccount.Enabled = true;
         fetchAccount.MinutesBetweenFetch = 60;
         fetchAccount.Name = "Mirror";
         fetchAccount.ServerType = 1;
         fetchAccount.Username = "remote@dummy-example.com";
         fetchAccount.Password = "far-side-secret";
         fetchAccount.UseSSL = false;
         fetchAccount.ServerAddress = "localhost";
         fetchAccount.Port = port;
         fetchAccount.ProcessMIMERecipients = false;
         fetchAccount.DaysToKeepMessages = daysToKeepMessages;
         fetchAccount.UseAntiSpam = false;
         fetchAccount.UseAntiVirus = false;
         fetchAccount.MirrorFolders = mirror;
         fetchAccount.Save();
         return fetchAccount;
      }

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

      /// <summary>How many messages the local folder holds, or -1 when there is no such folder.</summary>
      private int CountIn(string folder)
      {
         var client = new ImapClientSimulator();
         Assert.IsTrue(client.ConnectAndLogon(_account.Address, Password));
         try
         {
            string text;
            if (!client.SelectFolder(folder, out text))
               return -1;

            var match = Regex.Match(text, @"\* (\d+) EXISTS");
            Assert.IsTrue(match.Success, "SELECT " + folder + " answered without EXISTS: " + text);
            return int.Parse(match.Groups[1].Value);
         }
         finally
         {
            client.Disconnect();
         }
      }

      /// <summary>A FETCH of the first message in the local folder, with the items asked for.</summary>
      private string FetchFirst(string folder, string items)
      {
         var client = new ImapClientSimulator();
         Assert.IsTrue(client.ConnectAndLogon(_account.Address, Password));
         try
         {
            Assert.IsTrue(client.SelectFolder(folder), "No local folder " + folder);
            return client.Fetch("1 " + items);
         }
         finally
         {
            client.Disconnect();
         }
      }

      [Test]
      [Description("Every mailbox LIST returns is collected into a local folder of the same name, the remote delimiter mapped to the local one, a \\Noselect node skipped, and each message stored verbatim - no header added, nothing delivered.")]
      public void EveryFolderIsMirroredWithItsHierarchy()
      {
         var inbox = Mailbox("INBOX", "Inbox one", "Inbox two");
         var archive = Mailbox("Archive", "Archived");
         var year = Mailbox("Archive/2025", "From last year");
         var node = new ScriptedImapServer.RemoteMailbox("Trash") { Selectable = false };
         var fetchAccount = CreateFetchAccount(PortHierarchy, 30, true);

         using (var server = new ScriptedImapServer(PortHierarchy, new[] { inbox, archive, year, node }))
         {
            Poll(server, fetchAccount);

            Assert.AreEqual(1, server.ListCount, "The mirror asks for the hierarchy once per session.");
            CollectionAssert.AreEqual(new[] { "INBOX", "Archive", "Archive/2025" }, server.SelectedMailboxes,
               "Every selectable mailbox in LIST order; the \\Noselect node is never selected.");
            CollectionAssert.AreEqual(new[] { "INBOX:101", "INBOX:102", "Archive:101", "Archive/2025:101" }, server.FetchedMessages);
            CollectionAssert.IsEmpty(server.StoredDeletedUids, "Days to keep is 30, so nothing is deleted on the far side.");
         }

         Assert.AreEqual(2, CountIn("INBOX"));
         Assert.AreEqual(1, CountIn("Archive"));
         Assert.AreEqual(1, CountIn("Archive.2025"), "The remote '/' hierarchy is the local '.' hierarchy.");
         Assert.AreEqual(-1, CountIn("Trash"), "A node that cannot be selected has nothing to mirror and is not created.");

         string copy = FetchFirst("Archive.2025", "BODY.PEEK[]");
         StringAssert.Contains(MessageText("From last year"), copy, "The message is stored as the remote server sent it.");
         StringAssert.DoesNotContain("X-hMailServer-ExternalAccount", copy,
            "A mirrored message is a copy, not a delivery; the collection header is not added.");

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      [Test]
      [Description("The remote FLAGS and INTERNALDATE come with the copy: what was read stays read, what was flagged stays flagged, and the date is the remote's, not today's.")]
      public void FlagsAndTheInternalDateArePreserved()
      {
         var inbox = new ScriptedImapServer.RemoteMailbox("INBOX");
         inbox.Add(new ScriptedImapServer.RemoteMessage(101, MessageText("Read and flagged"), "\\Seen \\Flagged", InternalDate));
         inbox.Add(new ScriptedImapServer.RemoteMessage(102, MessageText("Untouched"), "", "12-Feb-2021 10:00:00 +0000"));
         var fetchAccount = CreateFetchAccount(PortFlags, 30, true);

         using (var server = new ScriptedImapServer(PortFlags, inbox))
            Poll(server, fetchAccount);

         Assert.AreEqual(2, CountIn("INBOX"));

         string first = FetchFirst("INBOX", "(FLAGS INTERNALDATE)");
         StringAssert.Contains("\\Seen", first);
         StringAssert.Contains("\\Flagged", first);
         StringAssert.Contains("INTERNALDATE \"17-Jul-1996 02:44:25", first,
            "The wall-clock time of the remote internal date, as an APPEND with a date keeps it.");

         var client = new ImapClientSimulator();
         Assert.IsTrue(client.ConnectAndLogon(_account.Address, Password));
         Assert.IsTrue(client.SelectFolder("INBOX"));
         string second = client.Fetch("2 (FLAGS INTERNALDATE)");
         client.Disconnect();
         StringAssert.DoesNotContain("\\Seen", second, "A message the remote had not marked read is unread here.");
         StringAssert.Contains("INTERNALDATE \"12-Feb-2021 10:00:00", second);
      }

      [Test]
      [Description("A server that sends FLAGS and INTERNALDATE after the body literal is read just the same.")]
      public void AttributesAfterTheBodyAreReadToo()
      {
         var inbox = new ScriptedImapServer.RemoteMailbox("INBOX");
         inbox.Add(new ScriptedImapServer.RemoteMessage(101, MessageText("Trailing attributes"), "\\Answered", InternalDate));
         var fetchAccount = CreateFetchAccount(PortFlagsAfterBody, 30, true);

         using (var server = new ScriptedImapServer(PortFlagsAfterBody, inbox) { AttributesAfterBody = true })
            Poll(server, fetchAccount);

         Assert.AreEqual(1, CountIn("INBOX"));
         string first = FetchFirst("INBOX", "(FLAGS INTERNALDATE)");
         StringAssert.Contains("\\Answered", first);
         StringAssert.Contains("INTERNALDATE \"17-Jul-1996 02:44:25", first);
      }

      [Test]
      [Description("Each mailbox keeps its own record of what has been collected: a second poll asks only for what is new, in the folder it is new in, and the same UID in two mailboxes is two messages.")]
      public void ASecondPollCollectsOnlyWhatIsNewInEachFolder()
      {
         var inbox = Mailbox("INBOX", "First");
         var archive = Mailbox("Archive", "Kept");
         var fetchAccount = CreateFetchAccount(PortSecondPoll, 30, true);

         using (var server = new ScriptedImapServer(PortSecondPoll, new[] { inbox, archive }))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { "INBOX:101", "Archive:101" }, server.FetchedMessages,
               "UID 101 in INBOX and UID 101 in Archive are different messages.");
         }
         Assert.AreEqual(1, CountIn("INBOX"));
         Assert.AreEqual(1, CountIn("Archive"));

         archive.Add(new ScriptedImapServer.RemoteMessage(102, MessageText("Newly archived")));

         using (var server = new ScriptedImapServer(PortSecondPoll, new[] { inbox, archive }))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { "Archive:102" }, server.FetchedMessages,
               "Only the new message, and only in the folder it appeared in.");
         }
         Assert.AreEqual(1, CountIn("INBOX"));
         Assert.AreEqual(2, CountIn("Archive"));
      }

      [Test]
      [Description("With DaysToKeepMessages 0 every collected message is flagged and expunged in its own mailbox - a move, folder by folder - and the local copies stay.")]
      public void DaysToKeepZeroDeletesFromEveryFolderAfterCollection()
      {
         var inbox = Mailbox("INBOX", "Move me");
         var archive = Mailbox("Archive", "And me", "Me too");
         var fetchAccount = CreateFetchAccount(PortDelete, 0, true);

         using (var server = new ScriptedImapServer(PortDelete, new[] { inbox, archive }))
         {
            Poll(server, fetchAccount);
            CollectionAssert.AreEquivalent(new[] { 101, 101, 102 }, server.StoredDeletedUids);
            Assert.AreEqual(2, server.ExpungeCount, "One EXPUNGE per mailbox that had something to delete.");
         }

         Assert.AreEqual(0, inbox.Count);
         Assert.AreEqual(0, archive.Count);
         Assert.AreEqual(1, CountIn("INBOX"));
         Assert.AreEqual(2, CountIn("Archive"));
      }

      [Test]
      [Description("A mailbox whose SELECT is refused is reported and skipped; the mailboxes after it are still collected.")]
      public void AFolderThatCannotBeSelectedIsSkippedAndTheRestCollected()
      {
         var inbox = Mailbox("INBOX", "Fine");
         var broken = Mailbox("Broken", "Unreachable");
         var after = Mailbox("After", "Still collected");
         var fetchAccount = CreateFetchAccount(PortRefused, 30, true);

         using (var server = new ScriptedImapServer(PortRefused, new[] { inbox, broken, after }))
         {
            server.RefuseSelectOf.Add("Broken");
            Poll(server, fetchAccount);
            CollectionAssert.AreEqual(new[] { "INBOX", "Broken", "After" }, server.SelectedMailboxes);
            CollectionAssert.AreEqual(new[] { "INBOX:101", "After:101" }, server.FetchedMessages);
         }

         Assert.AreEqual(1, CountIn("INBOX"));
         Assert.AreEqual(-1, CountIn("Broken"));
         Assert.AreEqual(1, CountIn("After"));
      }

      [Test]
      [Description("The negative control: with the mirror off the fetcher never asks for LIST, collects the INBOX alone, and delivers it - the collection header and all.")]
      public void WithTheMirrorOffOnlyTheInboxIsCollectedAndDelivered()
      {
         var inbox = Mailbox("INBOX", "Delivered");
         var archive = Mailbox("Archive", "Never seen");
         var fetchAccount = CreateFetchAccount(PortMirrorOff, 30, false);

         using (var server = new ScriptedImapServer(PortMirrorOff, new[] { inbox, archive }))
         {
            Poll(server, fetchAccount);
            Assert.AreEqual(0, server.ListCount);
            CollectionAssert.AreEqual(new[] { "INBOX" }, server.SelectedMailboxes);
            CollectionAssert.AreEqual(new[] { "INBOX:101" }, server.FetchedMessages);
         }

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
         Assert.AreEqual(1, CountIn("INBOX"));
         Assert.AreEqual(-1, CountIn("Archive"));

         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText(_account.Address, Password);
         StringAssert.Contains("X-hMailServer-ExternalAccount: Mirror", delivered,
            "Without the mirror a collected message is delivered, and carries the collection header.");
      }

      [Test]
      [Description("The switch is a property of the fetch account: saved, read back, and off by default.")]
      public void TheSwitchRoundTripsOverCom()
      {
         var fetchAccount = _account.FetchAccounts.Add();
         fetchAccount.Name = "Switch";
         fetchAccount.ServerAddress = "localhost";
         fetchAccount.Port = PortHierarchy;
         fetchAccount.ServerType = 1;
         fetchAccount.Username = "u";
         fetchAccount.Password = "p";
         Assert.IsFalse(fetchAccount.MirrorFolders, "Off by default: a fetch account is a collection unless it is told otherwise.");

         fetchAccount.MirrorFolders = true;
         fetchAccount.Save();

         var reread = _account.FetchAccounts.get_ItemByDBID(fetchAccount.ID);
         Assert.IsTrue(reread.MirrorFolders);

         reread.MirrorFolders = false;
         reread.Save();
         Assert.IsFalse(_account.FetchAccounts.get_ItemByDBID(fetchAccount.ID).MirrorFolders);
      }
   }
}
