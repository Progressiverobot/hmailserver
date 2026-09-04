// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    There used to be no ceiling at all on what one authenticated account could
   ///    send. RateLimiter shaped submissions per source IP and deliveries per
   ///    destination domain, both per minute, but nothing counted per account - so a
   ///    single compromised mailbox could relay for as long as nobody noticed, which
   ///    is the standard route to a blacklisted sending IP. The blast radius was
   ///    unbounded.
   ///
   ///    These tests pin the ceiling that now exists (hMailServer.ini
   ///    [SendingLimits]), and just as importantly they pin the three things that
   ///    would make the cure worse than the disease:
   ///
   ///    * the refusal is a 452, never a 5xx - a legitimate user who reaches a daily
   ///      cap must have their client retry when the period rolls over, not have the
   ///      mail bounced back at them;
   ///    * inbound mail from unauthenticated peers is not counted or limited at all;
   ///    * with no configuration present the ceiling does not exist, so an upgrade
   ///      cannot start refusing anybody's mail.
   ///
   ///    Every test uses a freshly minted account address. The counters are
   ///    deliberately persistent - they are written to a state file in the data
   ///    directory, at most once per StateSaveIntervalSeconds, so that an ordinary
   ///    restart does not hand a compromised account a fresh budget - which means a
   ///    fixed address would inherit the previous run's usage and the suite would
   ///    stop being repeatable. Note the throttle: nothing here may assume that a
   ///    given command wrote the file, and one test exists specifically to pin that
   ///    a refusal does not.
   /// </summary>
   [TestFixture]
   public class AccountSendingLimits : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private const string SettingsSection = "SendingLimits";

      // The limiter notices a changed hMailServer.ini within a couple of seconds
      // (it compares the file's timestamp rather than re-reading it per message),
      // so a write needs a moment to take effect. No service restart is involved.
      private const int SettingsPickupMs = 4000;

      private static int _addressCounter;

      // DateTime.Now has a resolution of milliseconds at best, so a bare timestamp
      // can repeat within one test. The counter guarantees distinctness.
      private static string UniqueAddress(string prefix)
      {
         return prefix
                + DateTime.Now.Ticks
                + "x"
                + Interlocked.Increment(ref _addressCounter)
                + "@example.test";
      }

      private static string EncodeBase64(string s)
      {
         return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
      }

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString(SettingsSection, key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      // The shipped default. Tests that shorten it have to put it back.
      private const int DefaultSaveIntervalSeconds = 10;

      private void ClearLimits()
      {
         WriteSetting("MaxMessagesPerAccountPerPeriod", "0");
         WriteSetting("MaxRecipientsPerAccountPerPeriod", "0");
         WriteSetting("PeriodHours", "24");
         WriteSetting("StateSaveIntervalSeconds", DefaultSaveIntervalSeconds.ToString());
         Thread.Sleep(SettingsPickupMs);
      }

      private static TcpConnection ConnectAndEhlo()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         Assert.IsTrue(socket.SendAndReceive("EHLO example.test\r\n").StartsWith("250"));
         return socket;
      }

      private static TcpConnection ConnectAndAuthenticate(string address, string password)
      {
         var socket = ConnectAndEhlo();

         string challenge = socket.SendAndReceive("AUTH LOGIN " + EncodeBase64(address) + "\r\n");
         Assert.IsTrue(challenge.StartsWith("334"), challenge);

         string authResult = socket.SendAndReceive(EncodeBase64(password) + "\r\n");
         Assert.IsTrue(authResult.StartsWith("235"), authResult);

         return socket;
      }

      [Test]
      [Description("A third message is refused temporarily once the per-account message ceiling of two is reached")]
      public void MessageCeilingIsRefusedWithATemporaryFailure()
      {
         string address = UniqueAddress("msglimit");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         try
         {
            WriteSetting("MaxMessagesPerAccountPerPeriod", "2");
            WriteSetting("MaxRecipientsPerAccountPerPeriod", "0");
            WriteSetting("PeriodHours", "24");
            Thread.Sleep(SettingsPickupMs);

            var socket = ConnectAndAuthenticate(address, "test");

            string first = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
            Assert.IsTrue(first.StartsWith("250"), first);
            socket.SendAndReceive("RSET\r\n");

            string second = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
            Assert.IsTrue(second.StartsWith("250"), second);
            socket.SendAndReceive("RSET\r\n");

            // Before the change there was no per-account counter of any kind, so
            // this third transaction was answered 250 like the two before it.
            string third = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
            socket.Disconnect();

            Assert.IsTrue(third.StartsWith("452"), third);

            // The whole point of the reply code: a 5xx here would bounce a
            // legitimate user's mail back at them instead of having the client
            // retry once the period rolls over.
            Assert.IsFalse(third.StartsWith("5"), third);

            // RFC 3463 4.7.0 - "other or undefined security status". A sending
            // ceiling is a policy control and there is no narrower 4.7.x code.
            StringAssert.Contains("4.7.0", third);
         }
         finally
         {
            ClearLimits();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("Recipients are counted as they accumulate at RCPT TO, so one message cannot bypass the ceiling")]
      public void RecipientCeilingIsAppliedPerRecipientNotPerMessage()
      {
         string sender = UniqueAddress("rcptlimit");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, sender, "test");

         string first = UniqueAddress("rcptone");
         string second = UniqueAddress("rcpttwo");
         string third = UniqueAddress("rcptthree");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, first, "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, second, "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, third, "test");

         try
         {
            // Messages unlimited, recipients capped at two: this is the case the
            // ceiling has to survive - a single message addressed to thousands of
            // recipients costs only one message from a message-count budget.
            WriteSetting("MaxMessagesPerAccountPerPeriod", "0");
            WriteSetting("MaxRecipientsPerAccountPerPeriod", "2");
            WriteSetting("PeriodHours", "24");
            Thread.Sleep(SettingsPickupMs);

            var socket = ConnectAndAuthenticate(sender, "test");

            string mailFrom = socket.SendAndReceive("MAIL FROM:<" + sender + ">\r\n");
            Assert.IsTrue(mailFrom.StartsWith("250"), mailFrom);

            string rcptOne = socket.SendAndReceive("RCPT TO:<" + first + ">\r\n");
            Assert.IsTrue(rcptOne.StartsWith("250"), rcptOne);

            string rcptTwo = socket.SendAndReceive("RCPT TO:<" + second + ">\r\n");
            Assert.IsTrue(rcptTwo.StartsWith("250"), rcptTwo);

            // Before the change nothing counted recipients per account, so this was
            // a 250 and the message went on to be delivered to all three.
            string rcptThree = socket.SendAndReceive("RCPT TO:<" + third + ">\r\n");
            socket.Disconnect();

            Assert.IsTrue(rcptThree.StartsWith("452"), rcptThree);
            Assert.IsFalse(rcptThree.StartsWith("5"), rcptThree);

            // RFC 3463 4.5.3 - "too many recipients", which is exactly this case.
            StringAssert.Contains("4.5.3", rcptThree);
         }
         finally
         {
            ClearLimits();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("The counters reach the state file within the configured save interval, so a restart does not hand back a fresh budget")]
      public void CountersArePersistedToDisk()
      {
         string address = UniqueAddress("persistlimit");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         try
         {
            // One second rather than the default ten, so the test does not have to
            // wait out a whole interval per message.
            WriteSetting("StateSaveIntervalSeconds", "1");
            WriteSetting("MaxMessagesPerAccountPerPeriod", "4");
            WriteSetting("MaxRecipientsPerAccountPerPeriod", "0");
            WriteSetting("PeriodHours", "24");
            Thread.Sleep(SettingsPickupMs);

            var socket = ConnectAndAuthenticate(address, "test");

            // Note what this deliberately does NOT do: it does not expect the
            // refusal to write the file. Persistence comes from counting, and only
            // an accepted command changes a counter.
            for (int attempt = 1; attempt <= 4; attempt++)
            {
               string reply = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
               Assert.IsTrue(reply.StartsWith("250"), "Attempt " + attempt + ": " + reply);
               socket.SendAndReceive("RSET\r\n");

               // Longer than the save interval, so the next command's write is due.
               Thread.Sleep(1300);
            }

            string refused = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
            socket.Disconnect();
            Assert.IsTrue(refused.StartsWith("452"), refused);

            // The file is what makes the limit survive a restart. Before the change
            // it did not exist, and there was nothing to survive.
            string contents = WaitForStateFileContaining(address);
            StringAssert.Contains(address.ToLowerInvariant(), contents.ToLowerInvariant());
         }
         finally
         {
            ClearLimits();
            LogHandler.DeleteErrorLog();
         }
      }

      /// <summary>
      ///    Regression test for the reason the forced write was removed. A refusal
      ///    changes no counter, so it has nothing durable to say - but the first cut
      ///    of this feature wrote the state file on every refusal, bypassing the
      ///    interval throttle. Serializing every tracked account under the limiter's
      ///    lock is not cheap and it happened on the SMTP connection thread, so one
      ///    over-quota credential could turn each rejected MAIL FROM into a full walk
      ///    of the account map, as fast as it cared to send them. This asserts that a
      ///    stream of refusals leaves the file alone.
      /// </summary>
      [Test]
      [Description("A stream of refused commands does not rewrite the state file")]
      public void RefusalsDoNotRewriteTheStateFile()
      {
         string address = UniqueAddress("refusalwrite");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         string stateFile = Path.Combine(
            _application.Settings.Directories.DataDirectory,
            "hmailserver_sendinglimits.dat");

         try
         {
            WriteSetting("StateSaveIntervalSeconds", "1");
            WriteSetting("MaxMessagesPerAccountPerPeriod", "1");
            WriteSetting("MaxRecipientsPerAccountPerPeriod", "0");
            WriteSetting("PeriodHours", "24");
            Thread.Sleep(SettingsPickupMs);

            var socket = ConnectAndAuthenticate(address, "test");

            string accepted = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
            Assert.IsTrue(accepted.StartsWith("250"), accepted);
            socket.SendAndReceive("RSET\r\n");

            // Settle first: the one accepted command above is still pending a write,
            // and a refusal is allowed to flush that - what it must not do is write
            // once per refusal. Spend a few seconds being refused, then take the
            // reading.
            for (int attempt = 0; attempt < 6; attempt++)
            {
               string reply = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
               Assert.IsTrue(reply.StartsWith("452"), reply);
               socket.SendAndReceive("RSET\r\n");
               Thread.Sleep(500);
            }

            Assert.IsTrue(File.Exists(stateFile), "No sending-limit state file at " + stateFile);
            DateTime before = File.GetLastWriteTimeUtc(stateFile);

            // Well over the one-second save interval, and 40 refusals inside it.
            for (int attempt = 0; attempt < 40; attempt++)
            {
               string reply = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
               Assert.IsTrue(reply.StartsWith("452"), reply);
               socket.SendAndReceive("RSET\r\n");
               Thread.Sleep(100);
            }

            socket.Disconnect();

            DateTime after = File.GetLastWriteTimeUtc(stateFile);

            Assert.AreEqual(before, after,
               "The state file was rewritten while nothing but refusals were happening. "
               + "Each of those is a full serialization of every tracked account, under the "
               + "limiter's lock, on an SMTP connection thread.");
         }
         finally
         {
            ClearLimits();
            LogHandler.DeleteErrorLog();
         }
      }

      /// <summary>
      ///    Negative control. Passes both before and after the change, and that is
      ///    the point: this is the regression the ceiling could plausibly cause. An
      ///    unauthenticated peer delivering inbound mail must never be counted
      ///    against anybody's sending budget - the limit is on what our accounts
      ///    send, not on what we accept.
      /// </summary>
      [Test]
      [Description("Negative control: unauthenticated inbound mail is never limited")]
      public void UnauthenticatedInboundMailIsNotLimited()
      {
         string recipient = UniqueAddress("inbound");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, recipient, "test");

         try
         {
            WriteSetting("MaxMessagesPerAccountPerPeriod", "1");
            WriteSetting("MaxRecipientsPerAccountPerPeriod", "1");
            WriteSetting("PeriodHours", "24");
            Thread.Sleep(SettingsPickupMs);

            var socket = ConnectAndEhlo();

            for (int attempt = 1; attempt <= 4; attempt++)
            {
               string mailFrom = socket.SendAndReceive("MAIL FROM:<outsider@example.com>\r\n");
               Assert.IsTrue(mailFrom.StartsWith("250"), "Attempt " + attempt + ": " + mailFrom);

               string rcptTo = socket.SendAndReceive("RCPT TO:<" + recipient + ">\r\n");
               Assert.IsTrue(rcptTo.StartsWith("250"), "Attempt " + attempt + ": " + rcptTo);

               socket.SendAndReceive("RSET\r\n");
            }

            socket.Disconnect();
         }
         finally
         {
            ClearLimits();
            LogHandler.DeleteErrorLog();
         }
      }

      /// <summary>
      ///    Negative control, and the one that matters most on upgrade. Also passes
      ///    both before and after: with no [SendingLimits] configuration the ceiling
      ///    must not exist. A limit that silently switched itself on would be a worse
      ///    defect than the one being fixed.
      /// </summary>
      [Test]
      [Description("Negative control: with no configuration an account may send without a ceiling")]
      public void DefaultConfigurationImposesNoCeiling()
      {
         string address = UniqueAddress("nolimit");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         try
         {
            ClearLimits();

            var socket = ConnectAndAuthenticate(address, "test");

            for (int attempt = 1; attempt <= 6; attempt++)
            {
               string mailFrom = socket.SendAndReceive("MAIL FROM:<" + address + ">\r\n");
               Assert.IsTrue(mailFrom.StartsWith("250"), "Attempt " + attempt + ": " + mailFrom);

               string rcptTo = socket.SendAndReceive("RCPT TO:<" + address + ">\r\n");
               Assert.IsTrue(rcptTo.StartsWith("250"), "Attempt " + attempt + ": " + rcptTo);

               socket.SendAndReceive("RSET\r\n");
            }

            socket.Disconnect();
         }
         finally
         {
            LogHandler.DeleteErrorLog();
         }
      }

      // The write is throttled and happens on whichever connection thread notices
      // that the interval has elapsed, so it is not synchronous with any one
      // command. Poll rather than assume.
      private string WaitForStateFileContaining(string address)
      {
         string stateFile = Path.Combine(
            _application.Settings.Directories.DataDirectory,
            "hmailserver_sendinglimits.dat");

         string contents = "";

         for (int attempt = 0; attempt < 100; attempt++)
         {
            if (File.Exists(stateFile))
            {
               contents = ReadWithRetry(stateFile);
               if (contents.ToLowerInvariant().Contains(address.ToLowerInvariant()))
                  return contents;
            }

            Thread.Sleep(200);
         }

         Assert.Fail("The sending-limit state file at " + stateFile
                     + " never mentioned " + address + ". Contents: " + contents);
         return contents;
      }

      // The server may be replacing the state file at the moment we read it.
      private static string ReadWithRetry(string fileName)
      {
         for (int attempt = 0; attempt < 20; attempt++)
         {
            try
            {
               return File.ReadAllText(fileName);
            }
            catch (IOException)
            {
               Thread.Sleep(100);
            }
         }

         return File.ReadAllText(fileName);
      }
   }
}
