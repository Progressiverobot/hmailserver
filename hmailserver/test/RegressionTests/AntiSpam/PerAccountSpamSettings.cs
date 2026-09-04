// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// Per-account spam settings: an account can opt out of spam filtering, and can
// override the mark and delete thresholds for ITS OWN copy of a message at
// local delivery. The SMTP conversation - refusal, quarantine, greylisting -
// stays governed by the global settings, because a conversation shared by
// several recipients cannot be refused per-recipient. What survives from the
// conversation to delivery is the spam flag and, when "add reason headers" is
// on, the recorded X-hMailServer-Reason-Score header; the per-account logic
// re-judges those and never re-runs a test. A message the global filter did not
// classify carries neither, so per-account settings deliberately do nothing to
// it - several tests below pin exactly that honesty.
//
// NOTE: this fixture uses the Account properties AntiSpamEnabled,
// SpamMarkThreshold and SpamDeleteThreshold, which exist in the server but not
// yet in the checked-in COM interop. It will not compile until
// Interop.hMailServer.dll is regenerated from the updated IDL.
//
// The controlled score source is the same one Basics.cs uses: the SURBL
// project's permanent test point, served by a local fake resolver so the
// lookup is deterministic and the score is exactly the SURBL server's
// configured score.

using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Legacy;   // StringAssert and the other classic asserts
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   [TestFixture]
   public class PerAccountSpamSettings : TestFixtureBase
   {
      private const string SurblTestPoint = "surbl-org-permanent-test-point.com.multi.surbl.org";

      // A body containing the listed URL scores exactly the SURBL server's
      // configured score; a body without it scores 0.
      private const string SpamBody =
         "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-";

      private const string CleanBody = "This is a clean test message.";

      private FakeDnsServer dns_;

      private hMailServer.AntiSpam _antiSpam;

      [OneTimeSetUp]
      public void PointTheServerAtALocalResolver()
      {
         dns_ = new FakeDnsServer()
            .WithA(SurblTestPoint, "127.0.0.2");

         ServerIniFile.SetSetting("DNSServer", "127.0.0.1");

         RestartServerAndReacquireCom();
      }

      [OneTimeTearDown]
      public void RestoreTheSystemResolver()
      {
         // The fake resolver stays up until the server is back on the system one, so
         // the restart never runs against a dead resolver.
         using (dns_)
         {
            ServerIniFile.SetSetting("DNSServer", null);
            RestartServerAndReacquireCom();
         }
      }

      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _settings.AntiSpam;
      }

      /// <summary>
      ///   Global thresholds that classify (mark) a test-point message but never
      ///   refuse it at SMTP time, with the marking machinery on so both the
      ///   headers and the subject tag are observable, and the recorded score
      ///   header available for the per-account logic to read back.
      /// </summary>
      private hMailServer.SURBLServer ConfigureGlobalScoring(int surblScore, int globalMarkThreshold)
      {
         _antiSpam.SpamMarkThreshold = globalMarkThreshold;
         _antiSpam.SpamDeleteThreshold = 100;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "[SPAM]";
         _antiSpam.MaximumMessageSize = 0;

         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = surblScore;
         surblServer.Save();

         return surblServer;
      }

      private static void Deactivate(hMailServer.SURBLServer surblServer)
      {
         surblServer.Active = false;
         surblServer.Save();
      }

      [Test]
      [Description("An account with a lower delete threshold loses its copy of a message the global config delivers - and only its copy.")]
      public void AccountDeleteThresholdRemovesOnlyThatAccountsCopy()
      {
         var surblServer = ConfigureGlobalScoring(5, 5);

         try
         {
            var aggressive = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "aggressive@example.test", "test");
            var normal = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "normal@example.test", "test");

            aggressive.SpamDeleteThreshold = 5;
            aggressive.Save();

            var recipients = new List<string> {aggressive.Address, normal.Address};
            new SmtpClientSimulator().Send("sender@example.com", recipients, "SpamTest", SpamBody);

            // The default account receives the shared message, marked - which also
            // proves the message really was classified, so the absence below is
            // the delete threshold acting and not a delivery failure.
            var normalCopy = Pop3ClientSimulator.AssertGetFirstMessageText(normal.Address, "test");
            StringAssert.Contains("X-hMailServer-Spam", normalCopy);

            // AssertMessageCount(0) first waits for the delivery queue to drain,
            // so this is "the delivery pass finished and produced nothing", not a
            // race against it.
            Pop3ClientSimulator.AssertMessageCount(aggressive.Address, "test", 0);
         }
         finally
         {
            Deactivate(surblServer);
         }
      }

      [Test]
      [Description("An opted-out account receives a message scoring above the global mark threshold entirely unmarked; a second recipient keeps the global behaviour.")]
      public void OptedOutAccountReceivesHighScoringMailUnmarked()
      {
         var surblServer = ConfigureGlobalScoring(5, 5);

         try
         {
            var optedOut = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "optedout@example.test", "test");
            var normal = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "normal@example.test", "test");

            optedOut.AntiSpamEnabled = false;
            optedOut.Save();

            var recipients = new List<string> {optedOut.Address, normal.Address};
            new SmtpClientSimulator().Send("sender@example.com", recipients, "SpamTest", SpamBody);

            var normalCopy = Pop3ClientSimulator.AssertGetFirstMessageText(normal.Address, "test");
            StringAssert.Contains("X-hMailServer-Spam", normalCopy);
            StringAssert.Contains("[SPAM] SpamTest", normalCopy);

            var optedOutCopy = Pop3ClientSimulator.AssertGetFirstMessageText(optedOut.Address, "test");
            Assert.IsFalse(optedOutCopy.Contains("X-hMailServer-Spam"), optedOutCopy);
            Assert.IsFalse(optedOutCopy.Contains("X-hMailServer-Reason"), optedOutCopy);
            Assert.IsFalse(optedOutCopy.Contains("[SPAM]"), optedOutCopy);
            StringAssert.Contains("Subject: SpamTest", optedOutCopy);
         }
         finally
         {
            Deactivate(surblServer);
         }
      }

      [Test]
      [Description("Negative control: an account with no overrides behaves exactly as before the feature existed, and a new account reads back the no-override defaults.")]
      public void AbsentOverridesBehaveExactlyAsBefore()
      {
         var surblServer = ConfigureGlobalScoring(5, 5);

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "plain@example.test", "test");

            // The defaults ARE the negative control: -1 means "no override", and
            // anything else here means the feature leaked into accounts that
            // never asked for it.
            Assert.IsTrue(account.AntiSpamEnabled);
            Assert.AreEqual(-1, account.SpamMarkThreshold);
            Assert.AreEqual(-1, account.SpamDeleteThreshold);

            // A classified message is delivered, marked - not deleted, not
            // unmarked.
            new SmtpClientSimulator().Send("sender@example.com", account.Address, "SpamTest", SpamBody);
            var spamCopy = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
            StringAssert.Contains("X-hMailServer-Spam", spamCopy);
            StringAssert.Contains("[SPAM] SpamTest", spamCopy);

            // And a clean message is delivered clean. The expected count is 1,
            // not 2: AssertGetFirstMessageText above downloads via
            // GetFirstMessageText, which RETRs AND DELEs what it reads - so the
            // spam copy is gone by now and the clean message stands alone. This
            // test first shipped expecting 2, and the half-hour spent proving
            // the server had not eaten a message ended at the helper's DELE.
            new SmtpClientSimulator().Send("sender@example.com", account.Address, "CleanTest", CleanBody);
            Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

            var cleanCopy = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
            StringAssert.DoesNotContain("X-hMailServer-Spam", cleanCopy);
            StringAssert.Contains("CleanTest", cleanCopy);
         }
         finally
         {
            Deactivate(surblServer);
         }
      }

      [Test]
      [Description("Boundary: a delete override equal to the recorded score fires; one point above it does not.")]
      public void DeleteOverrideBoundaryAtTheRecordedScore()
      {
         var surblServer = ConfigureGlobalScoring(5, 5);

         try
         {
            var equal = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "equal@example.test", "test");
            var above = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "above@example.test", "test");

            equal.SpamDeleteThreshold = 5;   // score 5 >= 5: deleted
            equal.Save();
            above.SpamDeleteThreshold = 6;   // score 5 < 6: kept, marked
            above.Save();

            var recipients = new List<string> {equal.Address, above.Address};
            new SmtpClientSimulator().Send("sender@example.com", recipients, "SpamTest", SpamBody);

            var aboveCopy = Pop3ClientSimulator.AssertGetFirstMessageText(above.Address, "test");
            StringAssert.Contains("X-hMailServer-Spam", aboveCopy);

            Pop3ClientSimulator.AssertMessageCount(equal.Address, "test", 0);
         }
         finally
         {
            Deactivate(surblServer);
         }
      }

      [Test]
      [Description("A mark override above the recorded score unmarks that account's copy; at the score, the mark stands. Boundary on the mark side.")]
      public void MarkOverrideAboveTheRecordedScoreUnmarksTheCopy()
      {
         var surblServer = ConfigureGlobalScoring(5, 5);

         try
         {
            var raised = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "raised@example.test", "test");
            var atScore = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "atscore@example.test", "test");

            raised.SpamMarkThreshold = 6;    // recorded score 5 < 6: unmarked
            raised.Save();
            atScore.SpamMarkThreshold = 5;   // recorded score 5 >= 5: stays marked
            atScore.Save();

            var recipients = new List<string> {raised.Address, atScore.Address};
            new SmtpClientSimulator().Send("sender@example.com", recipients, "SpamTest", SpamBody);

            var atScoreCopy = Pop3ClientSimulator.AssertGetFirstMessageText(atScore.Address, "test");
            StringAssert.Contains("X-hMailServer-Spam", atScoreCopy);
            StringAssert.Contains("[SPAM] SpamTest", atScoreCopy);

            var raisedCopy = Pop3ClientSimulator.AssertGetFirstMessageText(raised.Address, "test");
            Assert.IsFalse(raisedCopy.Contains("X-hMailServer-Spam"), raisedCopy);
            Assert.IsFalse(raisedCopy.Contains("[SPAM]"), raisedCopy);
         }
         finally
         {
            Deactivate(surblServer);
         }
      }

      [Test]
      [Description("Honest scope: without the recorded score header, a delete override fires only when the flag alone proves it - never on a guess.")]
      public void DeleteOverrideRequiresProof()
      {
         // Reason headers OFF: the server records no score, so nothing about this
         // message's score is provable at delivery.
         //
         // This test used to assert that the spam flag alone proved "score >= the
         // global mark threshold" and that a delete override at or below that bound
         // fired. That bound was withdrawn, for two reasons found in review: the
         // threshold is read at DELIVERY and the message was judged at RECEPTION, so
         // an administrator raising it while mail sat queued could destroy a copy
         // whose real score never reached the account's threshold; and with reason
         // headers off the score in the file is not the server's at all - it is
         // whatever the SENDER put there.
         //
         // So the contract is now the narrower, honest one: with no recorded score,
         // NEITHER override acts and the account keeps the server-wide behaviour.
         var surblServer = ConfigureGlobalScoring(6, 5);
         _antiSpam.AddHeaderReason = false;

         try
         {
            var atTheBound = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "unprovable@example.test", "test");
            var belowTheBound = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "provable@example.test", "test");

            // 6 is the message's actual score; 5 is what the old lower bound would
            // have claimed. Neither may delete, because neither is proven.
            atTheBound.SpamDeleteThreshold = 6;
            atTheBound.Save();
            belowTheBound.SpamDeleteThreshold = 5;
            belowTheBound.Save();

            var recipients = new List<string> {atTheBound.Address, belowTheBound.Address};
            new SmtpClientSimulator().Send("sender@example.com", recipients, "SpamTest", SpamBody);

            StringAssert.Contains("X-hMailServer-Spam",
               Pop3ClientSimulator.AssertGetFirstMessageText(atTheBound.Address, "test"));

            // The negative control for the withdrawal: against the old code this
            // copy was deleted, so a count of 1 here is the whole assertion.
            StringAssert.Contains("X-hMailServer-Spam",
               Pop3ClientSimulator.AssertGetFirstMessageText(belowTheBound.Address, "test"));
         }
         finally
         {
            // Restored explicitly: leaving this off leaks into any later test that
            // does not call ConfigureGlobalScoring first.
            _antiSpam.AddHeaderReason = true;
            Deactivate(surblServer);
         }
      }

      /// <summary>
      /// With the score recorded, the delete override does fire - the other half of
      /// the contract above, so "requires proof" cannot be satisfied by a feature
      /// that simply never acts.
      /// </summary>
      [Test]
      public void DeleteOverrideFiresWhenTheScoreIsRecorded()
      {
         var surblServer = ConfigureGlobalScoring(6, 5);   // leaves AddHeaderReason on

         try
         {
            var deleted = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "proven@example.test", "test");
            var kept = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "kept@example.test", "test");

            deleted.SpamDeleteThreshold = 6;   // the recorded score is exactly 6
            deleted.Save();
            kept.SpamDeleteThreshold = 7;      // one above it
            kept.Save();

            var recipients = new List<string> {deleted.Address, kept.Address};
            new SmtpClientSimulator().Send("sender@example.com", recipients, "SpamTest", SpamBody);

            StringAssert.Contains("X-hMailServer-Spam",
               Pop3ClientSimulator.AssertGetFirstMessageText(kept.Address, "test"));

            Pop3ClientSimulator.AssertMessageCount(deleted.Address, "test", 0);
         }
         finally
         {
            Deactivate(surblServer);
         }
      }
      /// <summary>
      /// A sender must not be able to supply the evidence the override reads.
      ///
      /// The per-account overrides judge a copy by the score this server recorded
      /// in X-hMailServer-Reason-Score. That header is only WRITTEN while "add
      /// reason to header" is on, and inbound mail is never stripped of
      /// X-hMailServer-* fields - so with the setting off, the value in the file is
      /// whatever the sender chose to put there.
      ///
      /// A spammer could therefore attach a score of 1 to genuinely spammy mail and
      /// have a recipient's own mark threshold un-mark it: the attacker supplying
      /// the proof that overrules the server's own classification. The fix is that
      /// the score is not read at all unless the server wrote it.
      ///
      /// This test fails against the unfixed build, where the forged header wins.
      /// </summary>
      [Test]
      [Description("A sender-supplied X-hMailServer-Reason-Score must never steer a per-account override.")]
      public void AForgedScoreHeaderCannotUnmarkSpam()
      {
         var surblServer = ConfigureGlobalScoring(5, 5);

         try
         {
            // The setting that decides whether the server writes its own score. With
            // it off nothing overwrites what the sender sent.
            _antiSpam.AddHeaderReason = false;

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "forged@example.test", "test");

            // A mark threshold the forged score sits comfortably below.
            account.SpamMarkThreshold = 4;
            account.Save();

            // Genuinely spammy body, carrying the sender's own claim that it scored 1.
            var forged = "X-hMailServer-Reason-Score: 1" + "\r\n" +
                         "Subject: ForgedScore" + "\r\n\r\n" + SpamBody;

            SmtpClientSimulator.StaticSendRaw("sender@example.com", account.Address, forged);

            var delivered = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

            // The server's own classification must stand.
            StringAssert.Contains("X-hMailServer-Spam", delivered,
               "A sender-supplied score header un-marked genuine spam - the attacker " +
               "supplied the evidence the override trusted.");
         }
         finally
         {
            _antiSpam.AddHeaderReason = true;
            Deactivate(surblServer);
         }
      }
   }
}
