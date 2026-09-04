// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   [Description("RFC 3501 sequence sets: \"*\" on either side of a colon, and ranges given in either order")]
   public class SequenceSets : TestFixtureBase
   {
      private hMailServer.Account CreateAccountWithMessages(int messageCount)
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         for (var i = 1; i <= messageCount; i++)
         {
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test " + i, "Body " + i);
            ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", i);
         }

         return account;
      }

      private ImapClientSimulator LogonAndSelectInbox(hMailServer.Account account)
      {
         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         return simulator;
      }

      private static int CountUntaggedResponses(string response, string responseName)
      {
         return response.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith("* ") && line.Contains(" " + responseName + " "));
      }

      private static int CountOccurrences(string text, string value)
      {
         var count = 0;
         var position = text.IndexOf(value, StringComparison.Ordinal);

         while (position >= 0)
         {
            count++;
            position = text.IndexOf(value, position + value.Length, StringComparison.Ordinal);
         }

         return count;
      }

      [Test]
      [Description("FETCH * addresses the last message, not message 0")]
      public void FetchStarAddressesTheLastMessage()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);
         var result = simulator.SendSingleCommand("A01 FETCH * (UID)");
         simulator.Disconnect();

         Assert.AreEqual(1, CountUntaggedResponses(result, "FETCH"), result);
         Assert.IsTrue(result.Contains("* 3 FETCH"), result);
      }

      [Test]
      [Description("UID FETCH * addresses the message with the largest UID")]
      public void UidFetchStarAddressesTheLargestUid()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);
         var result = simulator.SendSingleCommand("A01 UID FETCH * (UID)");
         simulator.Disconnect();

         Assert.AreEqual(1, CountUntaggedResponses(result, "FETCH"), result);
         Assert.IsTrue(result.Contains("UID 3"), result);
      }

      [Test]
      [Description("A descending range means the same as the ascending one")]
      public void DescendingSequenceRangeMatchesTheWholeRange()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);
         var result = simulator.SendSingleCommand("A01 FETCH 3:1 (UID)");
         simulator.Disconnect();

         Assert.AreEqual(3, CountUntaggedResponses(result, "FETCH"), result);
      }

      [Test]
      [Description("A descending UID range means the same as the ascending one")]
      public void DescendingUidRangeMatchesTheWholeRange()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);
         var result = simulator.SendSingleCommand("A01 UID FETCH 3:1 (UID)");
         simulator.Disconnect();

         Assert.AreEqual(3, CountUntaggedResponses(result, "FETCH"), result);
      }

      [Test]
      [Description("*:1 is the entire mailbox, because \"*\" is valid as the start of a range")]
      public void StarAsRangeStartMatchesTheEntireMailbox()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);
         var result = simulator.SendSingleCommand("A01 FETCH *:1 (UID)");
         simulator.Disconnect();

         Assert.AreEqual(3, CountUntaggedResponses(result, "FETCH"), result);
      }

      [Test]
      [Description("UID *:1 is the entire mailbox")]
      public void StarAsUidRangeStartMatchesTheEntireMailbox()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);
         var result = simulator.SendSingleCommand("A01 UID FETCH *:1 (UID)");
         simulator.Disconnect();

         Assert.AreEqual(3, CountUntaggedResponses(result, "FETCH"), result);
      }

      [Test]
      [Description("UID STORE *:* must flag only the last message, never the whole mailbox")]
      public void StarColonStarStoreAffectsOnlyTheLastMessage()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);
         simulator.SendSingleCommand("A01 UID STORE *:* +FLAGS (\\Deleted)");
         var result = simulator.SendSingleCommand("A02 FETCH 1:* (FLAGS)");
         simulator.Disconnect();

         Assert.AreEqual(3, CountUntaggedResponses(result, "FETCH"), result);
         Assert.AreEqual(1, CountOccurrences(result, "\\Deleted"), result);
         Assert.IsTrue(result.Contains("* 3 FETCH (FLAGS (\\Deleted"), result);
      }

      [Test]
      [Description("\"*\" in an empty mailbox matches nothing, and the command still succeeds")]
      public void StarInAnEmptyMailboxMatchesNothing()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var simulator = LogonAndSelectInbox(account);

         var result = simulator.SendSingleCommand("A01 FETCH * (UID)");
         Assert.AreEqual(0, CountUntaggedResponses(result, "FETCH"), result);
         Assert.IsTrue(result.Contains("A01 OK"), result);

         result = simulator.SendSingleCommand("A02 UID FETCH * (UID)");
         Assert.AreEqual(0, CountUntaggedResponses(result, "FETCH"), result);
         Assert.IsTrue(result.Contains("A02 OK"), result);

         result = simulator.SendSingleCommand("A03 UID FETCH 1:* (UID)");
         Assert.AreEqual(0, CountUntaggedResponses(result, "FETCH"), result);
         Assert.IsTrue(result.Contains("A03 OK"), result);

         simulator.Disconnect();
      }

      [Test]
      [Description("SEARCH uses its own sequence-set parser, which must agree with FETCH")]
      public void SearchResolvesStarAndDescendingRanges()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);

         var byStar = simulator.SendSingleCommand("A01 SEARCH *");
         var descending = simulator.SendSingleCommand("A02 SEARCH 3:1");
         var starFirst = simulator.SendSingleCommand("A03 SEARCH *:1");

         simulator.Disconnect();

         Assert.IsTrue(byStar.Contains("* SEARCH 3\r\n"), byStar);
         Assert.IsTrue(descending.Contains("* SEARCH 1 2 3"), descending);
         Assert.IsTrue(starFirst.Contains("* SEARCH 1 2 3"), starFirst);
      }

      [Test]
      [Description("A UID search key carrying \"*\" resolves to the largest UID")]
      public void UidSearchKeyResolvesStar()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);

         var byStar = simulator.SendSingleCommand("A01 UID SEARCH UID *");
         var descending = simulator.SendSingleCommand("A02 UID SEARCH UID 3:1");

         simulator.Disconnect();

         Assert.IsTrue(byStar.Contains("* SEARCH 3\r\n"), byStar);
         Assert.IsTrue(descending.Contains("* SEARCH 1 2 3"), descending);
      }

      [Test]
      [Description("UID EXPUNGE * must expunge only the highest UID, not every deleted message")]
      public void UidExpungeStarAffectsOnlyTheLastMessage()
      {
         var account = CreateAccountWithMessages(3);

         var simulator = LogonAndSelectInbox(account);

         simulator.SendSingleCommand("A01 STORE 1:3 +FLAGS (\\Deleted)");
         var expunge = simulator.SendSingleCommand("A02 UID EXPUNGE *");

         simulator.Disconnect();

         Assert.IsTrue(expunge.Contains("A02 OK"), expunge);
         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 2);
      }

      [Test]
      [Description("In a single message mailbox, \"*\" and \"1\" address the same message")]
      public void StarAndFirstAreTheSameMessageInASingleMessageMailbox()
      {
         var account = CreateAccountWithMessages(1);

         var simulator = LogonAndSelectInbox(account);
         var byStar = simulator.SendSingleCommand("A01 FETCH * (UID)");
         var byNumber = simulator.SendSingleCommand("A02 FETCH 1 (UID)");
         simulator.Disconnect();

         Assert.AreEqual(1, CountUntaggedResponses(byStar, "FETCH"), byStar);
         Assert.IsTrue(byStar.Contains("* 1 FETCH"), byStar);
         Assert.IsTrue(byNumber.Contains("* 1 FETCH"), byNumber);
      }
   }
}
