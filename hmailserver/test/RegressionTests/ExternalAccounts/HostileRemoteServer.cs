// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.ExternalAccounts
{
   /// <summary>
   ///    The external account fetcher downloads mail from a server the administrator does
   ///    not run, so every one of these tests asks the same question: what does a remote
   ///    server that is broken, overloaded or hostile make hMailServer do to the mail?
   ///    <para />
   ///    Each test states, at the top, what it did against the code before the fix.
   /// </summary>
   [TestFixture]
   public class HostileRemoteServer : TestFixtureBase
   {
      // Ports 9470-9479 are reserved for this fixture.
      private const int PortRetrRefused = 9470;
      private const int PortMalformedListing = 9471;
      private const int PortTruncatedDownload = 9472;
      private const int PortInterruptedCleanup = 9473;
      private const int PortRefusedDeletion = 9474;
      private const int PortOverlongUid = 9475;
      private const int PortUnterminatedListing = 9476;

      private Account _account;

      [SetUp]
      public void SetUpTest()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "user@example.test", "test");
      }

      private static string MessageText(string subject)
      {
         return "From: sender@dummy-example.com\r\n" +
                "To: user@example.test\r\n" +
                "Subject: " + subject + "\r\n" +
                "\r\n" +
                "Body of " + subject + ".";
      }

      private FetchAccount CreateFetchAccount(int port, int daysToKeepMessages)
      {
         var fetchAccount = _account.FetchAccounts.Add();

         fetchAccount.Enabled = true;

         // Long enough that the scheduler never starts a fetch of its own during a test:
         // every fetch here is driven by DownloadNow.
         fetchAccount.MinutesBetweenFetch = 60;
         fetchAccount.Name = "Hostile";
         fetchAccount.Username = "remote@dummy-example.com";
         fetchAccount.Password = "remote";
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

      /// <summary>
      ///    The application log is written by a logger of its own, so a line can lag the
      ///    event that produced it by a moment. Every log assertion in the suite retries for
      ///    that reason.
      /// </summary>
      private static void AssertLogContains(string expected)
      {
         RetryHelper.TryAction(TimeSpan.FromSeconds(15), () =>
         {
            var log = LogHandler.ReadCurrentDefaultLog();

            if (!log.Contains(expected))
               throw new Exception("The default log does not contain '" + expected + "'. Log:\r\n" + log);
         });
      }

      /// <summary>
      ///    Waits for everything a fetch delivered to leave the delivery queue, then asserts
      ///    the mailbox holds exactly this many messages.
      ///    <para />
      ///    Draining first is what makes the duplicate-delivery tests honest. A fetch saves
      ///    the message and hands it to the deliverer, so without the drain the count could be
      ///    taken while a second copy is still queued - and a test that asserts "still only
      ///    one" would pass against the very bug it exists to catch.
      /// </summary>
      private void AssertDeliveredMessageCount(int expectedCount)
      {
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);

         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", expectedCount);
      }

      private static List<ScriptedPop3Server.RemoteMessage> Mailbox(params string[] subjects)
      {
         var messages = new List<ScriptedPop3Server.RemoteMessage>();

         for (var i = 0; i < subjects.Length; i++)
            messages.Add(new ScriptedPop3Server.RemoteMessage("remote-uid-" + (i + 1), MessageText(subjects[i])));

         return messages;
      }

      [Test]
      [Description(
         "A message the remote server lists and then refuses to send must not stop the rest of the mailbox from being collected. " +
         "Before the fix a refused RETR abandoned the whole session, and because the listing is walked in message-number order, " +
         "a message that always fails and happens to be first meant nothing was ever collected from that account again: this test " +
         "delivered 0 of 3 messages instead of 2.")]
      public void RefusedRetrSkipsOnlyThatMessage()
      {
         var messages = Mailbox("First", "Second", "Third");

         using (var pop3Server = new ScriptedPop3Server(PortRetrRefused, messages))
         {
            pop3Server.RefuseRetrForMessages.Add(1);
            pop3Server.StartListen();

            var fetchAccount = CreateFetchAccount(PortRetrRefused, -1);
            fetchAccount.DownloadNow();

            pop3Server.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            // The two messages the server was willing to hand over are delivered.
            AssertDeliveredMessageCount(2);

            // ... and only those two are deleted from the remote server. The one it
            // refused to send is still there, so a later fetch can try again.
            Assert.IsTrue(pop3Server.DeletedMessages.Contains(2), "Message 2 should have been deleted from the remote server.");
            Assert.IsTrue(pop3Server.DeletedMessages.Contains(3), "Message 3 should have been deleted from the remote server.");
            Assert.IsFalse(pop3Server.DeletedMessages.Contains(1), "The message the server refused to send must not be deleted.");

            AssertLogContains("refused to send message 1 of external account Hostile");

            fetchAccount.Delete();
         }
      }

      [Test]
      [Description(
         "One unparseable line in the UIDL listing must not cost the whole mailbox. Before the fix a line with no space in it " +
         "became the unique-id of 'message 0' - Find returned -1 and Mid range-checked its way to an empty message number - and " +
         "because uidlresponse_ is ordered by message number, RETR 0 was the first thing asked for. The server refused it and the " +
         "session was abandoned: this test delivered 0 of 2 messages instead of 2.")]
      public void MalformedListingLineDoesNotCostTheMailbox()
      {
         var messages = Mailbox("First", "Second");

         using (var pop3Server = new ScriptedPop3Server(PortMalformedListing, messages))
         {
            pop3Server.UidlListingOverride = new List<string>
            {
               "1 " + messages[0].Uid,
               "this-line-has-no-space-in-it",
               "2 " + messages[1].Uid
            };

            pop3Server.StartListen();

            var fetchAccount = CreateFetchAccount(PortMalformedListing, -1);
            fetchAccount.DownloadNow();

            pop3Server.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            AssertDeliveredMessageCount(2);

            // The junk line is reported rather than silently swallowed: it means a
            // message the administrator can see on the server that hMailServer will not
            // collect, which is not something to discover by counting mail.
            AssertLogContains("unusable line(s) in the UIDL listing");

            // Nothing was asked for as "message 0".
            Assert.IsFalse(pop3Server.RetrievedMessages.Contains(0), "No RETR should ever be issued for message number 0.");

            fetchAccount.Delete();
         }
      }

      [Test]
      [Description(
         "A download cut off part way through must not leave its fragment in the data directory. Before the fix the partly " +
         "written spool file stayed there for ever with no database row referring to it, one per interrupted fetch: this test " +
         "found a leftover .eml file in the data directory root.")]
      public void InterruptedDownloadLeavesNoFileBehind()
      {
         var dataDirectory = _settings.Directories.DataDirectory;
         var filesBefore = new HashSet<string>(Directory.GetFiles(dataDirectory, "*.eml"));

         // Big enough that the cut-off point is past the buffer's own flush threshold, so
         // the fragment left behind has real content in it rather than being an empty file.
         var body = new StringBuilder();
         for (var i = 0; i < 900; i++)
            body.AppendLine("This line is here to make the message long enough to be cut in half.");

         var messages = new List<ScriptedPop3Server.RemoteMessage>
         {
            new ScriptedPop3Server.RemoteMessage("remote-uid-truncated", MessageText("Truncated") + body)
         };

         using (var pop3Server = new ScriptedPop3Server(PortTruncatedDownload, messages))
         {
            // Enough to make hMailServer create the spool file and write to it, nowhere
            // near enough to reach the end-of-message marker.
            pop3Server.TruncateRetrAfterBytes = 45000;
            pop3Server.StartListen();

            var fetchAccount = CreateFetchAccount(PortTruncatedDownload, -1);
            fetchAccount.DownloadNow();

            pop3Server.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            // Half a message must never be delivered.
            AssertDeliveredMessageCount(0);

            // The teardown that discards the fragment happens as the connection object is
            // released, which is not necessarily the instant the fetch account unlocks.
            RetryHelper.TryAction(TimeSpan.FromSeconds(15), () =>
            {
               var leftOver = Directory.GetFiles(dataDirectory, "*.eml")
                  .Where(file => !filesBefore.Contains(file))
                  .ToList();

               if (leftOver.Count > 0)
                  throw new Exception("The interrupted download left " + leftOver.Count +
                                      " file(s) in the data directory: " + string.Join(", ", leftOver));
            });

            // And the message is still on the remote server, because no DELE was sent for
            // something that never arrived in one piece.
            Assert.AreEqual(0, pop3Server.DeletedMessages.Count, "Nothing should have been deleted from the remote server.");

            fetchAccount.Delete();
         }
      }

      [Test]
      [Description(
         "A session that dies between the delivery and the deletion must not deliver the message a second time on the next " +
         "check. Before the fix nothing was recorded locally for a message that was about to be deleted, so an unacknowledged " +
         "DELE left no trace of the delivery at all: this test delivered the same message twice.")]
      public void InterruptedCleanupDoesNotDeliverTwice()
      {
         var messages = Mailbox("Interrupted cleanup");

         FetchAccount fetchAccount;

         using (var firstSession = new ScriptedPop3Server(PortInterruptedCleanup, messages))
         {
            firstSession.DisconnectOnDele = true;
            firstSession.StartListen();

            fetchAccount = CreateFetchAccount(PortInterruptedCleanup, -1);
            fetchAccount.DownloadNow();

            firstSession.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            AssertDeliveredMessageCount(1);

            // The message is still on the server: the deletion was never confirmed.
            Assert.AreEqual(1, messages.Count);
         }

         // Second check, against the same mailbox. The first listener is closed before the
         // second one opens on the same port.
         using (var secondSession = new ScriptedPop3Server(PortInterruptedCleanup, messages))
         {
            secondSession.StartListen();

            fetchAccount.DownloadNow();

            secondSession.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            // Delivered once in total, and this time the deletion went through.
            AssertDeliveredMessageCount(1);
            Assert.AreEqual(0, secondSession.RetrievedMessages.Count, "The second check must not download the message again.");
            Assert.AreEqual(0, messages.Count);
         }

         fetchAccount.Delete();
      }

      [Test]
      [Description(
         "A deletion the remote server refuses must not lead to a second delivery either. Before the fix the local record was " +
         "removed the moment the DELE was written to the socket and the response was never even looked at, so a refused " +
         "deletion left the message on the server with nothing to say it had been collected: this test delivered it twice.")]
      public void RefusedDeletionDoesNotDeliverTwice()
      {
         var messages = Mailbox("Refused deletion");

         FetchAccount fetchAccount;

         using (var firstSession = new ScriptedPop3Server(PortRefusedDeletion, messages))
         {
            firstSession.RefuseDele = true;
            firstSession.StartListen();

            fetchAccount = CreateFetchAccount(PortRefusedDeletion, -1);
            fetchAccount.DownloadNow();

            firstSession.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            AssertDeliveredMessageCount(1);

            AssertLogContains("refused to delete a message downloaded from external account Hostile");
         }

         using (var secondSession = new ScriptedPop3Server(PortRefusedDeletion, messages))
         {
            secondSession.RefuseDele = true;
            secondSession.StartListen();

            fetchAccount.DownloadNow();

            secondSession.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            AssertDeliveredMessageCount(1);
            Assert.AreEqual(0, secondSession.RetrievedMessages.Count, "The second check must not download the message again.");
         }

         fetchAccount.Delete();
      }

      [Test]
      [Description(
         "A unique-id longer than the 255-character column it is stored in must still be tracked. Before the fix the row was " +
         "refused or truncated by the database, the next session's refresh found no match for the id the server had given, and " +
         "the message was downloaded and delivered again - on every single check, for ever. This test delivered it twice.")]
      public void OverlongRemoteUidIsStillTracked()
      {
         // Three hundred characters: beyond the 255 the column holds, and well beyond the
         // 70 RFC 1939 permits.
         var longUid = new string('u', 290) + "-tail";

         var messages = new List<ScriptedPop3Server.RemoteMessage>
         {
            new ScriptedPop3Server.RemoteMessage(longUid, MessageText("Overlong unique id"))
         };

         FetchAccount fetchAccount;

         // DaysToKeepMessages of 0 leaves the message on the server, so the second check
         // sees the same listing again. That is the configuration in which tracking the id
         // is the only thing preventing a second delivery.
         using (var firstSession = new ScriptedPop3Server(PortOverlongUid, messages))
         {
            firstSession.StartListen();

            fetchAccount = CreateFetchAccount(PortOverlongUid, 0);
            fetchAccount.DownloadNow();

            firstSession.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            AssertDeliveredMessageCount(1);

            AssertLogContains("unique-id(s) longer than 255 characters");

            // Nothing was deleted: the message is left on the server by configuration.
            Assert.AreEqual(0, firstSession.DeletedMessages.Count, "Nothing should have been deleted from the remote server.");
         }

         using (var secondSession = new ScriptedPop3Server(PortOverlongUid, messages))
         {
            secondSession.StartListen();

            fetchAccount.DownloadNow();

            secondSession.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            // Still one copy: the second session recognised the message as one it had
            // already collected.
            AssertDeliveredMessageCount(1);
            Assert.AreEqual(0, secondSession.RetrievedMessages.Count, "The second check must not download the message again.");
         }

         fetchAccount.Delete();
      }

      [Test]
      [Description(
         "A UIDL listing that never ends must be abandoned rather than buffered without limit. Before the fix the listing was " +
         "accumulated in memory for as long as the remote server cared to send it, with no ceiling at all, so a server the " +
         "administrator does not control could exhaust the service's memory. This test made hMailServer buffer every byte sent.")]
      public void UnterminatedListingIsAbandoned()
      {
         var messages = Mailbox("Never collected");

         using (var pop3Server = new ScriptedPop3Server(PortUnterminatedListing, messages))
         {
            // Just past the eight megabyte ceiling.
            pop3Server.UnterminatedUidlBytes = 9 * 1024 * 1024;
            pop3Server.StartListen();

            var fetchAccount = CreateFetchAccount(PortUnterminatedListing, -1);
            fetchAccount.DownloadNow();

            pop3Server.WaitForCompletion();
            WaitForFetchToFinish(fetchAccount);

            AssertLogContains("exceeded 8388608 bytes without ending");

            // Nothing was collected, and - just as important - nothing was deleted from
            // the remote server, so the mail is still there once the server behaves.
            AssertDeliveredMessageCount(0);
            Assert.AreEqual(0, pop3Server.DeletedMessages.Count, "Nothing should have been deleted from the remote server.");
            Assert.AreEqual(1, messages.Count);

            fetchAccount.Delete();
         }
      }
   }
}
