// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The free-space precondition on the volume holding the message store
   ///    (hMailServer.ini [Settings] MinimumFreeDiskSpaceMB and
   ///    DiskSpaceWarningThresholdMB).
   ///
   ///    What is being pinned is a DIRECTION rather than a spelling. A server that
   ///    cannot store a message must say so in a way the sender will come back
   ///    from, because a disk that is full at 09:00 is usually not full at 09:05
   ///    and a permanent rejection destroys mail that a five-minute cleanup would
   ///    have delivered. So the assertions below care that the reply is 4xx and
   ///    not 5xx far more than they care that it is 452 exactly, and the IMAP one
   ///    cares that APPEND fails at all - IMAP has no temporary/permanent
   ///    distinction in its status, which is why the refusal carries
   ///    [UNAVAILABLE] and says "try again later" in words.
   ///
   ///    The disk is not actually filled. MinimumFreeDiskSpaceMB set larger than
   ///    any real volume makes the server behave exactly as though it were, which
   ///    is the whole reason the floor is expressed as a number rather than
   ///    inferred.
   /// </summary>
   [TestFixture]
   public class DiskSpaceGuard : TestFixtureBase
   {
      private const string FloorSetting = "MinimumFreeDiskSpaceMB";
      private const string WarningSetting = "DiskSpaceWarningThresholdMB";

      /// <summary>
      ///    Roughly 95 TB, expressed in megabytes. Larger than any volume this
      ///    will run on, and small enough that multiplying it by a megabyte
      ///    cannot come near overflowing the 64-bit byte count it becomes.
      /// </summary>
      private const string LargerThanAnyVolume = "100000000";

      /// <summary>
      ///    Both keys are removed rather than set back to a number: absent is the
      ///    shipped state, and a test that leaves an explicit value behind changes
      ///    what every fixture after it is running against. The restart is what
      ///    makes the removal take effect - IniFileSettings caches the file for
      ///    the life of the process.
      /// </summary>
      private void RestoreDefaultsAndRestart()
      {
         ServerIniFile.SetSetting(FloorSetting, null);
         ServerIniFile.SetSetting(WarningSetting, null);

         RestartServerAndReacquireCom();
      }

      /// <summary>
      ///    Opens an SMTP session, gets as far as MAIL FROM and returns whatever
      ///    the server said to it. Deliberately raw rather than through
      ///    SmtpClientSimulator: the simulator throws on anything that is not a
      ///    250, and the reply text is the thing under test.
      /// </summary>
      private static string MailFromReply(string fromAddress)
      {
         using (var connection = new TcpConnection())
         {
            Assert.IsTrue(connection.Connect(25), "Could not open an SMTP session on port 25.");

            StringAssert.StartsWith("220", connection.Receive());
            StringAssert.StartsWith("250", connection.SendAndReceive("HELO example.test\r\n"));

            return connection.SendAndReceive("MAIL FROM:<" + fromAddress + ">\r\n");
         }
      }

      [Test]
      [Description("Below the free-space floor, SMTP refuses the transaction with a TEMPORARY failure and delivers nothing")]
      public void SmtpRefusesMailTemporarilyWhenFreeSpaceIsBelowTheFloor()
      {
         const string address = "diskfull@example.test";
         const string password = "diskfull-password";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         // Read from COM BEFORE the restart below. _application, _settings,
         // _domain and every object reached through them are proxies into the
         // service's process; the restart disconnects them, and a property read
         // afterwards talks to a process that no longer exists.
         var accountAddress = account.Address;

         ServerIniFile.SetSetting(FloorSetting, LargerThanAnyVolume);

         try
         {
            RestartServerAndReacquireCom();

            var reply = MailFromReply("sender@example.test");

            // The direction, first and most importantly. A 5xx here would tell
            // the sending server to bounce the message to its author and never
            // try again - for a condition that clears the moment somebody empties
            // a folder.
            Assert.IsTrue(reply.Length >= 3, "The server said nothing to MAIL FROM: '" + reply + "'");
            Assert.AreEqual('4', reply[0],
               "A full disk is a temporary condition and must be refused with a 4xx so the sender retries. " +
               "The server said: " + reply);

            // And the spelling, which is 452 4.3.1 - "insufficient system
            // storage" / "mail system full" (RFC 5321 4.1.1.2, RFC 3463). Checked
            // second because it is the less important half.
            StringAssert.StartsWith("452", reply);

            // The refusal reached the CLIENT, not just the log: the assertions
            // above are made entirely from what came back down the socket.
            StringAssert.Contains("storage", reply.ToLowerInvariant());

            // Nothing was accepted, so nothing can be delivered. This also
            // asserts an empty delivery queue on the way through.
            Pop3ClientSimulator.AssertMessageCount(accountAddress, password, 0);

            // The administrator is told, once, at the moment mail starts being
            // refused - rather than being left to work out why senders are
            // retrying. AssertReportedError also clears the ERROR log, which the
            // next fixture's setup insists is empty.
            CustomAsserts.AssertReportedError("HM6230");
         }
         finally
         {
            RestoreDefaultsAndRestart();
         }
      }

      [Test]
      [Description("Below the free-space floor, IMAP APPEND is refused and the message is not stored")]
      public void ImapAppendIsRefusedWhenFreeSpaceIsBelowTheFloor()
      {
         const string address = "diskfullimap@example.test";
         const string password = "diskfullimap-password";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         // As above: captured before the restart, not after it.
         var accountAddress = account.Address;

         ServerIniFile.SetSetting(FloorSetting, LargerThanAnyVolume);

         try
         {
            RestartServerAndReacquireCom();

            var simulator = new ImapClientSimulator();
            simulator.Connect();
            Assert.IsTrue(simulator.Logon(accountAddress, password), "Could not log on to IMAP.");

            var result = simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");

            StringAssert.Contains("A01 NO", result,
               "APPEND must fail when the server has no room for the message. It said: " + result);

            // The client is told it may come back, which is the IMAP equivalent
            // of the 4xx above: a client that reads this as permanent throws away
            // a draft or a Sent copy it may hold nowhere else.
            StringAssert.Contains("UNAVAILABLE", result);
            StringAssert.Contains("try again later", result);

            // And nothing was stored.
            Assert.AreEqual(0, simulator.GetMessageCount("INBOX"),
               "A refused APPEND must leave the mailbox as it was.");

            simulator.Disconnect();

            CustomAsserts.AssertReportedError("HM6230");
         }
         finally
         {
            RestoreDefaultsAndRestart();
         }
      }

      [Test]
      [Description("A floor of 0 disables the check, and the warning threshold warns in the log without refusing anything")]
      public void AFloorOfZeroChangesNothingAndTheWarningRefusesNothing()
      {
         const string address = "diskspaceok@example.test";
         const string password = "diskspaceok-password";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);
         var accountAddress = account.Address;

         // 0 is the documented "no check" value and this is the compatibility
         // promise: an installation that sets it back to 0 behaves exactly as it
         // did before this feature existed, whatever the volume looks like.
         ServerIniFile.SetSetting(FloorSetting, "0");

         // Meanwhile the warning is made certain to fire, so that the OTHER half
         // of the promise is pinned in the same run: warning about free space is
         // not the same as refusing mail, and must never become it.
         ServerIniFile.SetSetting(WarningSetting, LargerThanAnyVolume);

         try
         {
            RestartServerAndReacquireCom();

            // Mail flows. If the floor of 0 were treated as a floor, or the
            // warning band refused anything, this is where it would show.
            var smtp = new SmtpClientSimulator();
            smtp.Send("sender@example.test", accountAddress, "Below the warning line",
               "The warning threshold must not refuse mail.");

            Pop3ClientSimulator.AssertMessageCount(accountAddress, password, 1);

            // The warning itself. It is written by a scheduled task that runs once
            // shortly after start-up, so it is polled for rather than assumed to
            // be there the moment COM starts answering.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var log = string.Empty;

            while (DateTime.UtcNow < deadline)
            {
               log = LogHandler.ReadCurrentDefaultLog();

               if (log.Contains("below the warning threshold of"))
                  break;

               Thread.Sleep(500);
            }

            StringAssert.Contains("below the warning threshold of", log,
               "The administrator warning is the half of this feature worth the most and it never appeared in the application log.");

            // LOG_APPLICATION and not an error: nothing is wrong, and an ERROR
            // record here would fail every fixture that asserts a clean error log.
            CustomAsserts.AssertNoReportedError();
         }
         finally
         {
            RestoreDefaultsAndRestart();
         }
      }

      [Test]
      [Description("Below the floor, external account fetching pauses rather than downloading onto a full disk - the one refusal that is free, because the mail stays on the remote server")]
      public void ExternalFetchingPausesBelowTheFloor()
      {
         const string address = "diskfetch@example.test";
         const string password = "diskfetch-password";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         // Read from COM BEFORE the restart: the proxies die with the service.
         string accountAddress = account.Address;

         var messages = new System.Collections.Generic.List<RegressionTests.ExternalAccounts.ScriptedPop3Server.RemoteMessage>
         {
            new RegressionTests.ExternalAccounts.ScriptedPop3Server.RemoteMessage("UID-DISKFULL-1",
               "From: sender@dummy-example.com\r\n" +
               "To: " + address + "\r\n" +
               "Subject: should stay remote\r\n" +
               "\r\n" +
               "A message that must not be fetched while the disk is full.\r\n")
         };

         using (var remoteServer = new RegressionTests.ExternalAccounts.ScriptedPop3Server(11390, messages))
         {
            remoteServer.StartListen();

            hMailServer.FetchAccount fetchAccount = null;
            try
            {
               ServerIniFile.SetSetting(FloorSetting, LargerThanAnyVolume);
               RestartServerAndReacquireCom();

               var restartedAccount = SingletonProvider<TestSetup>.Instance.GetApp()
                  .Domains.get_ItemByName("example.test").Accounts.get_ItemByAddress(accountAddress);

               fetchAccount = restartedAccount.FetchAccounts.Add();
               fetchAccount.Enabled = true;
               fetchAccount.MinutesBetweenFetch = 60;
               fetchAccount.Name = "DiskFullPause";
               fetchAccount.Username = "remote@dummy-example.com";
               fetchAccount.Password = "remote";
               fetchAccount.UseSSL = false;
               fetchAccount.ServerAddress = "localhost";
               fetchAccount.Port = 11390;
               fetchAccount.ProcessMIMERecipients = false;
               fetchAccount.DaysToKeepMessages = -1;
               fetchAccount.UseAntiSpam = false;
               fetchAccount.UseAntiVirus = false;
               fetchAccount.Save();

               fetchAccount.DownloadNow();

               // The pause is announced once in the application log - that line, plus
               // the message never arriving, is the whole contract.
               RetryHelper.TryAction(TimeSpan.FromSeconds(15), () =>
               {
                  var log = LogHandler.ReadCurrentDefaultLog();

                  if (!log.Contains("External account fetching is paused"))
                     throw new Exception("The pause was not announced in the application log. Log:\r\n" + log);
               });

               // Deterministic failure signal for a broken precondition: had the fetch
               // run, this message would be in the mailbox.
               Pop3ClientSimulator.AssertMessageCount(accountAddress, password, 0);

               Assert.AreEqual(0, remoteServer.RetrievedMessages.Count,
                  "The remote server should never have been asked for a message while the disk is below the floor.");

               // Crossing below the floor reports HM6230 once, by design. Consume
               // it - AssertReportedError asserts AND deletes - or the next
               // fixture's clean-log precondition fails on this test's leavings.
               CustomAsserts.AssertReportedError("HM6230");
            }
            finally
            {
               // The paused fetch left the account due, and the restart that lifts the
               // floor would run it at once - against a scripted server about to be
               // disposed, delivering "should stay remote" to a mailbox the next test's
               // setup is about to delete. That delivery reported HM5165 into the next
               // setup's clean-log check in the 6.2.25 release gate. Off before the
               // restart, so nothing is fetched once the floor is gone; then the queue
               // is drained, in case a fetch raced the switch.
               try
               {
                  if (fetchAccount != null)
                  {
                     fetchAccount.Enabled = false;
                     fetchAccount.Save();
                  }
               }
               catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
               {
                  // Deliberately ignored: the account proxy may already be gone, and the restart below reads the ini afresh either way.
               }

               RestoreDefaultsAndRestart();
               CustomAsserts.AssertRecipientsInDeliveryQueue(0);
            }
         }
      }
   }
}
