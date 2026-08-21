// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   [TestFixture]
   public class Combinations : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         CustomAsserts.AssertSpamAssassinIsRunning();
      }

      [Test]
      [Description(
         "Confirm that if you have a delete threshold lower than the mark threshhold, spam tests are run until" +
         "the mark threshold is reached.")]
      public void TestDeleteThresholdLowerThanMarkThreshold()
      {
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "multihit@example.test", "test");

         var antiSpam = _settings.AntiSpam;

         antiSpam.SpamMarkThreshold = 15;
         antiSpam.SpamDeleteThreshold = 0;

         antiSpam.AddHeaderReason = true;
         antiSpam.AddHeaderSpam = true;
         antiSpam.PrependSubject = true;
         antiSpam.PrependSubjectText = "ThisIsSpam";

         antiSpam.CheckHostInHelo = true;
         antiSpam.CheckHostInHeloScore = 10;

         // Enable SURBL.
         var surblServer = antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 10;
         surblServer.Save();

         // Send a messages to this account, containing both incorrect MX records an SURBL-hits.
         // We should only detect one of these two:
         // Connect via the machine's LAN address: the HELO host check is by design
         // skipped for loopback connections (SpamTestHeloHost), so a 127.0.0.1
         // client would never produce the expected score.
         var smtpClientSimulator = new SmtpClientSimulator(false, 25, TestSetup.GetLocalIpAddress());

         // Should not be possible to send this email since it's results in a spam
         // score over the delete threshold.
         smtpClientSimulator.Send("test@example.com", account1.Address, "INBOX",
            "Test http://surbl-org-permanent-test-point.com/ Test 2");

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         Assert.IsTrue(message.Contains("X-hMailServer-Reason-1:"));
         Assert.IsTrue(message.Contains("X-hMailServer-Reason-2:"));
      }

      [Test]
      [Description("Test that only one result header is added if one test passes and one fails.")]
      public void TestOneFailOnePass()
      {
         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "multihit@example.test", "test");

         _settings.AntiSpam.SpamMarkThreshold = 1;
         _settings.AntiSpam.SpamDeleteThreshold = 100;

         _settings.AntiSpam.AddHeaderReason = true;
         _settings.AntiSpam.AddHeaderSpam = true;
         _settings.AntiSpam.PrependSubject = true;
         _settings.AntiSpam.PrependSubjectText = "ThisIsSpam";

         _settings.AntiSpam.CheckHostInHelo = true;
         _settings.AntiSpam.CheckHostInHeloScore = 5;

         // Enable SURBL.
         var surblServer = _settings.AntiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account, containing both incorrect MX records an SURBL-hits.
         // We should only detect one of these two:
         // Connect via the machine's LAN address: the HELO host check is by design
         // skipped for loopback connections (SpamTestHeloHost).
         var smtpClientSimulator = new SmtpClientSimulator(false, 25, TestSetup.GetLocalIpAddress());

         // Should not be possible to send this email since it's results in a spam
         // score over the delete threshold.
         smtpClientSimulator.Send("test@domain.without.mxrecords.example.com", account1.Address, "INBOX",
            "This is a test message.");

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         Assert.IsTrue(message.Contains("X-hMailServer-Reason-1:"));
         Assert.IsFalse(message.Contains("X-hMailServer-Reason-2:"));
      }

      [Test]
      public void TestSpamMultipleHits()
      {
         CustomAsserts.AssertSpamAssassinIsRunning();

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mult'ihit@example.test", "test");

         _settings.AntiSpam.SpamMarkThreshold = 1;
         _settings.AntiSpam.SpamDeleteThreshold = 2;

         _settings.AntiSpam.AddHeaderReason = true;
         _settings.AntiSpam.AddHeaderSpam = true;
         _settings.AntiSpam.PrependSubject = true;
         _settings.AntiSpam.PrependSubjectText = "ThisIsSpam";

         // Enable SpamAssassin
         _settings.AntiSpam.SpamAssassinEnabled = true;
         _settings.AntiSpam.SpamAssassinHost = "localhost";
         _settings.AntiSpam.SpamAssassinPort = 783;
         _settings.AntiSpam.SpamAssassinMergeScore = false;
         _settings.AntiSpam.SpamAssassinScore = 5;


         // Enable SURBL.
         var surblServer = _settings.AntiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account, containing both incorrect MX records an SURBL-hits.
         // We should only detect one of these two:
         var smtpClientSimulator = new SmtpClientSimulator();

         _settings.Logging.LogSMTP = true;
         _settings.Logging.LogDebug = true;
         _settings.Logging.Enabled = true;
         _settings.Logging.EnableLiveLogging(true);

         // Access the log once to make sure it's cleared.
         var liveLog = _settings.Logging.LiveLog;

         // Should not be possible to send this email since it's results in a spam
         // score over the delete threshold.
         CustomAsserts.Throws<DeliveryFailedException>(() => smtpClientSimulator.Send(
            "test@domain.without.mxrecords.example.com", account1.Address, "INBOX",
            "This is a test message. It contains incorrect MX records and a SURBL string: http://surbl-org-permanent-test-point.com/ SpamAssassinString: XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X"));

         liveLog = _settings.Logging.LiveLog;

         _settings.Logging.EnableLiveLogging(false);

         // The property under test is that the pipeline STOPS as soon as the score
         // crosses the delete threshold: with the threshold at 2 and every enabled
         // test worth 5, the first test that matches must also be the last that
         // runs.
         //
         // This used to assert that only one "Spam test:" line was logged at all,
         // which was a proxy rather than the property. SpamTestRunner logs a line
         // for EVERY test it runs, matched or not, so the proxy quietly assumed the
         // first test in the pipeline would be one that matches. The sender
         // blacklist is registered ahead of the others (it is the cheapest test, and
         // matching there avoids a DNS lookup), scores zero on an empty list, and
         // broke the proxy while leaving the property untouched - same final score,
         // same refusal, one more debug line.
         //
         // So count the tests that actually SCORED. Exactly one may, because the
         // one that does ends the pipeline.
         var scored = System.Text.RegularExpressions.Regex.Matches(
            liveLog, @"Spam test: [^,]+, Score: (-?\d+)");

         Assert.AreNotEqual(0, scored.Count, "no spam test ran at all");

         var withScore = new System.Collections.Generic.List<string>();
         foreach (System.Text.RegularExpressions.Match m in scored)
         {
            if (int.Parse(m.Groups[1].Value) != 0)
               withScore.Add(m.Value);
         }

         Assert.AreEqual(1, withScore.Count,
            "exactly one test should have scored before the pipeline stopped, but these did: " +
            string.Join(" | ", withScore));
      }
   }
}