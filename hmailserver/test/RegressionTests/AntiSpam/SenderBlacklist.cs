// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using NUnit.Framework;
using NUnit.Framework.Legacy;   // StringAssert and the other classic asserts
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    The sender blacklist: entries in Settings.AntiSpam.BlockedSenders are
   ///    matched against the SMTP envelope sender (MAIL FROM). An entry that
   ///    contains '@' matches exactly one address; an entry without '@' matches
   ///    that domain and every subdomain of it. A match contributes the entry's
   ///    score (default 100) to the spam score, so at the default score the
   ///    message is refused with a 550 where the pre-transmission pipeline
   ///    runs (at MAIL FROM with the default DNSBLChecksAfterMailFrom=1, at
   ///    the first RCPT TO otherwise), and at a score between the mark and
   ///    delete thresholds it is delivered but marked as spam.
   ///
   ///    No fake DNS server is needed here, unlike the whitelisting fixture:
   ///    the blacklist match is a pure in-memory comparison and never touches
   ///    the network.
   /// </summary>
   [TestFixture]
   internal class SenderBlacklist : TestFixtureBase
   {
      private hMailServer.AntiSpam _antiSpam;

      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _settings.AntiSpam;

         // The same thresholds the whitelisting fixture uses: a default-score
         // entry (100) crosses the delete threshold, a score of 2 reaches
         // only the mark threshold.
         _antiSpam.SpamDeleteThreshold = 5;
         _antiSpam.SpamMarkThreshold = 2;

         _antiSpam.AddHeaderSpam = true;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.PrependSubject = false;

         // Nothing else in the suite clears this collection yet (TestSetup
         // clears WhiteListAddresses centrally; BlockedSenders should join it
         // once the interop knows the property). Until then this fixture
         // cleans up after itself in SetUp and TearDown both, so a failed
         // test cannot leak an entry into the next test or the next fixture.
         _antiSpam.BlockedSenders.Clear();

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "recipient@example.test", "test");
      }

      [TearDown]
      public void ClearBlockedSendersAfterTest()
      {
         try
         {
            _settings.AntiSpam.BlockedSenders.Clear();
         }
         catch
         {
            // If the server died the base TearDown will report it; failing
            // here would only replace that report with a less useful one.
         }
      }

      private hMailServer.BlockedSender AddBlockedSender(string address, int score)
      {
         var entry = _antiSpam.BlockedSenders.Add();
         entry.Address = address;
         entry.Score = score;
         entry.Description = "SenderBlacklist fixture";
         entry.Save();
         return entry;
      }

      /// <summary>
      ///    The negative control for the whole fixture: the identical send is
      ///    delivered before the entry exists and refused after, so this test
      ///    fails if the feature is inert. The refusal must be a 550 - a
      ///    permanent, pre-DATA rejection during the envelope - not a 554
      ///    after the body has been paid for, and not a 4xx the sender would
      ///    retry.
      /// </summary>
      [Test]
      public void TestExactAddressIsRejectedWithPermanent550()
      {
         SmtpClientSimulator.StaticSend("spammer@blacklisted.example", "recipient@example.test",
            "Before the entry exists", "This message must be delivered.");
         Pop3ClientSimulator.AssertMessageCount("recipient@example.test", "test", 1);

         AddBlockedSender("spammer@blacklisted.example", 100);

         // Raw SMTP for the refusal itself, because the exact reply is the
         // assertion: a permanent 550 5.7.1 during the envelope, before any
         // message body has been transferred. The high-level simulator does
         // not check the MAIL FROM reply at all, so a MAIL FROM-stage
         // refusal would surface through it as the follow-on 503 at RCPT -
         // an exception, but not evidence of the right refusal.
         using (var sock = new TcpConnection())
         {
            sock.Connect(25);
            Assert.IsTrue(sock.Receive().StartsWith("220"));

            // EHLO, not HELO, and the assertion below depends on it: RFC 2034
            // enhanced status codes are an ESMTP capability, and the server
            // correctly withholds "5.7.1" from a session that opened with HELO.
            // This test first shipped with HELO and failed on exactly that -
            // the refusal was right, and the test was asserting a decoration
            // the client had declined to negotiate.
            sock.Send("EHLO test.com\r\n");
            Assert.IsTrue(sock.Receive().StartsWith("250"));

            sock.Send("MAIL FROM:<spammer@blacklisted.example>\r\n");
            var mailFromReply = sock.Receive();

            string refusal;
            if (mailFromReply.StartsWith("250"))
            {
               // The pre-transmission pipeline was deferred to RCPT
               // (DNSBLChecksAfterMailFrom=0); the refusal arrives there.
               sock.Send("RCPT TO:<recipient@example.test>\r\n");
               refusal = sock.Receive();
            }
            else
            {
               // The default: the pipeline runs at MAIL FROM.
               refusal = mailFromReply;
            }

            StringAssert.StartsWith("550", refusal, refusal);
            StringAssert.Contains("5.7.1", refusal, refusal);
            StringAssert.Contains("Sender address is blocked", refusal, refusal);

            sock.Send("QUIT\r\n");
         }

         Pop3ClientSimulator.AssertMessageCount("recipient@example.test", "test", 1);
      }

      [Test]
      public void TestDomainEntryBlocksEveryAddressInTheDomainAndItsSubdomains()
      {
         AddBlockedSender("blacklisted.example", 100);

         // Any local part within the domain.
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("anyone@blacklisted.example", "recipient@example.test",
               "Domain entry", "Must be refused."));

         // A subdomain of the listed domain.
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("anyone@mail.blacklisted.example", "recipient@example.test",
               "Subdomain", "Must be refused."));

         // "@domain" is the tolerated alternative spelling of "domain".
         AddBlockedSender("@also-blacklisted.example", 100);
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("anyone@also-blacklisted.example", "recipient@example.test",
               "At-spelling", "Must be refused."));

         // The server is refusing those senders, not refusing everything.
         SmtpClientSimulator.StaticSend("anyone@unlisted.example", "recipient@example.test",
            "Control", "Must be delivered.");

         Pop3ClientSimulator.AssertMessageCount("recipient@example.test", "test", 1);
      }

      [Test]
      public void TestNearMissesAreNotBlocked()
      {
         AddBlockedSender("spammer@addr.example", 100);
         AddBlockedSender("dom.example", 100);

         // Both entries prove they are live before the near-misses mean anything.
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("spammer@addr.example", "recipient@example.test",
               "Listed address", "Must be refused."));
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("user@dom.example", "recipient@example.test",
               "Listed domain", "Must be refused."));

         // A different local part at the listed address's domain. The address
         // entry is exact; no wildcard semantics.
         SmtpClientSimulator.StaticSend("notspammer@addr.example", "recipient@example.test",
            "Near miss 1", "Must be delivered.");

         // A domain that merely ends with the listed domain's text. The domain
         // match is anchored at a label boundary.
         SmtpClientSimulator.StaticSend("user@notdom.example", "recipient@example.test",
            "Near miss 2", "Must be delivered.");

         // The listed domain as a prefix of a longer, different domain.
         SmtpClientSimulator.StaticSend("user@dom.example.attacker.example", "recipient@example.test",
            "Near miss 3", "Must be delivered.");

         Pop3ClientSimulator.AssertMessageCount("recipient@example.test", "test", 3);
      }

      /// <summary>
      ///    The off state is an empty list; there is no separate switch to
      ///    forget. With no entries, nothing changes: mail is delivered and no
      ///    spam verdict of any kind is attached to it.
      /// </summary>
      [Test]
      public void TestEmptyBlacklistChangesNothing()
      {
         SmtpClientSimulator.StaticSend("anyone@anywhere.example", "recipient@example.test",
            "Empty list", "Must be delivered untouched.");

         var message = Pop3ClientSimulator.AssertGetFirstMessageText("recipient@example.test", "test");

         Assert.IsFalse(message.Contains("X-hMailServer-Spam"), message);
         Assert.IsFalse(message.Contains("X-hMailServer-Reason-Score"), message);
      }

      /// <summary>
      ///    Deleting an entry must take effect on the very next message: the
      ///    server-side cache is invalidated on delete as well as on save.
      /// </summary>
      [Test]
      public void TestDeletingAnEntryStopsTheBlocking()
      {
         var entry = AddBlockedSender("spammer@blacklisted.example", 100);

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSend("spammer@blacklisted.example", "recipient@example.test",
               "While listed", "Must be refused."));

         _antiSpam.BlockedSenders.DeleteByDBID(entry.ID);

         SmtpClientSimulator.StaticSend("spammer@blacklisted.example", "recipient@example.test",
            "After removal", "Must be delivered.");

         Pop3ClientSimulator.AssertMessageCount("recipient@example.test", "test", 1);
      }

      /// <summary>
      ///    An entry's score below the delete threshold does not refuse the
      ///    message - it feeds the ordinary spam scoring, so the message
      ///    arrives marked. This is the honest expression of what a
      ///    MAIL FROM blacklist is: a score-based nuisance filter, with
      ///    refusal being the effect of a score high enough to cross the
      ///    delete threshold.
      /// </summary>
      [Test]
      public void TestScoreBelowDeleteThresholdMarksAsSpamInsteadOfRejecting()
      {
         AddBlockedSender("softspammer@blacklisted.example", 2);

         SmtpClientSimulator.StaticSend("softspammer@blacklisted.example", "recipient@example.test",
            "Soft entry", "Must be delivered, marked as spam.");

         var message = Pop3ClientSimulator.AssertGetFirstMessageText("recipient@example.test", "test");

         StringAssert.Contains("X-hMailServer-Spam: YES", message, message);
         StringAssert.Contains("X-hMailServer-Reason-Score: 2", message, message);
         StringAssert.Contains("Sender address is blocked", message, message);
      }

      /// <summary>
      ///    The null sender carries bounces and delivery status notifications
      ///    and can never be listed, not even by a domain entry.
      /// </summary>
      [Test]
      public void TestNullSenderIsNeverBlocked()
      {
         AddBlockedSender("blacklisted.example", 100);
         AddBlockedSender("spammer@blacklisted.example", 100);

         SmtpClientSimulator.StaticSend("", "recipient@example.test",
            "Null sender", "A bounce must always be deliverable.");

         Pop3ClientSimulator.AssertMessageCount("recipient@example.test", "test", 1);
      }
   }
}
