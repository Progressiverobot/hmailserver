// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam.DKIM
{
   /// <summary>
   /// ARC results used for inbound filtering (RFC 8617) - the negative controls.
   ///
   /// The feature under test lets a valid ARC chain from an ADMINISTRATOR-TRUSTED
   /// sealer offset the DMARC failure score that forwarding caused. The security
   /// property that makes it safe is that everything else is a no-op: with no
   /// trusted-sealer configuration (the shipping default), or with a chain that is
   /// fabricated, failed (cv=fail) or malformed, the message must be scored exactly
   /// as if it carried no chain at all - a chain must never reduce a score on its
   /// own, and never add one either.
   ///
   /// These tests pin that inert default. Each sends a message carrying an ARC
   /// chain alongside a deterministic score source (a DKIM signature whose selector
   /// does not exist - a permanent failure needing no live key, same technique as
   /// Verification.cs) and asserts the verdict is bit-for-bit what a chainless
   /// message gets. DMARC verification is switched on so the ARC test actually
   /// runs and walks its refusal path rather than being skipped outright.
   ///
   /// What is deliberately NOT tested here: the positive path, where a trusted,
   /// fully-validated chain offsets a DMARC failure. That needs (a) the
   /// ASArcFilteringEnabled / ASArcTrustedSealers settings rows and their COM
   /// accessors, which are owned by the settings work and do not exist yet, and
   /// (b) a sealer whose DKIM public key the test can publish in DNS, which this
   /// suite has no way to do. When (a) lands, a positive test still needs (b);
   /// until then the offset path is covered by the refusal ordering in
   /// SpamTestArc::RunTest (trust gate before any DNS work) and by review.
   ///
   /// The TestFixtureBase crash oracle doubles as the memory-safety assertion for
   /// the ARC chain parser against the hostile inputs below.
   /// </summary>
   [TestFixture]
   public class ArcInboundFiltering : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _application.Settings.AntiSpam;

         // A deterministic post-transmission penalty: an invalid DKIM signature
         // scores 6 against a mark threshold of 5, so the message is marked as
         // spam - unless something wrongly credits it a negative score, which
         // is precisely the failure these tests exist to catch.
         _antiSpam.SpamMarkThreshold = 5;
         _antiSpam.SpamDeleteThreshold = 1000;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.AddHeaderReason = true;
         _antiSpam.DKIMVerificationEnabled = true;
         _antiSpam.DKIMVerificationFailureScore = 6;

         // With DMARC off the ARC test is disabled outright (there is no DMARC
         // score to offset) and these tests would pass vacuously. On, the ARC
         // test runs and must refuse for the right reasons.
         _antiSpam.DMARCEnabled = true;
      }

      private hMailServer.AntiSpam _antiSpam;

      // A DKIM signature whose selector does not exist: the DNS answer is "no
      // such name", which RFC 6376 6.1.2 makes a permanent failure. No key
      // material and no live zone required.
      private const string PermanentlyFailingDkimSignature =
         "DKIM-Signature: v=1; a=rsa-sha256; c=relaxed/relaxed; d=example.test;\r\n" +
         "\ts=nosuchselector; h=from:subject;\r\n" +
         "\tbh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=; b=AAAAAAAAAAAA\r\n";

      // A complete, syntactically valid ARC set that claims the message passed
      // everything at the first hop. Anyone can write one of these; that is the
      // entire reason a chain must be worthless without configured trust.
      private const string FabricatedAllPassChain =
         "ARC-Seal: i=1; a=rsa-sha256; t=1755000000; cv=none; d=forwarder.example;\r\n" +
         "\ts=arcsel; b=AAAAAAAAAAAA\r\n" +
         "ARC-Message-Signature: i=1; a=rsa-sha256; c=relaxed/relaxed; d=forwarder.example;\r\n" +
         "\ts=arcsel; h=from:to:subject:date;\r\n" +
         "\tbh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=; b=AAAAAAAAAAAA\r\n" +
         "ARC-Authentication-Results: i=1; forwarder.example;\r\n" +
         "\tdkim=pass header.d=example.test; spf=pass smtp.mailfrom=example.test; dmarc=pass\r\n";

      // A two-hop chain whose newest seal records cv=fail: a previous hop's own
      // statement that the chain was already broken (RFC 8617 5.1.2).
      private const string CvFailChain =
         "ARC-Seal: i=2; a=rsa-sha256; t=1755000200; cv=fail; d=forwarder.example;\r\n" +
         "\ts=arcsel; b=BBBBBBBBBBBB\r\n" +
         "ARC-Message-Signature: i=2; a=rsa-sha256; c=relaxed/relaxed; d=forwarder.example;\r\n" +
         "\ts=arcsel; h=from:to:subject:date;\r\n" +
         "\tbh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=; b=BBBBBBBBBBBB\r\n" +
         "ARC-Authentication-Results: i=2; forwarder.example; dkim=fail; spf=fail\r\n" +
         "ARC-Seal: i=1; a=rsa-sha256; t=1755000000; cv=none; d=origin.example;\r\n" +
         "\ts=arcsel; b=AAAAAAAAAAAA\r\n" +
         "ARC-Message-Signature: i=1; a=rsa-sha256; c=relaxed/relaxed; d=origin.example;\r\n" +
         "\ts=arcsel; h=from:to:subject:date;\r\n" +
         "\tbh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=; b=AAAAAAAAAAAA\r\n" +
         "ARC-Authentication-Results: i=1; origin.example; dmarc=pass\r\n";

      // Structurally broken on purpose: a duplicate instance 1 seal, an instance
      // that is not a number, and an instance outside the RFC 8617 range. The
      // parser must refuse all of it without touching the verdict - and without
      // touching invalid memory, which the crash oracle checks.
      private const string MalformedChain =
         "ARC-Seal: i=1; a=rsa-sha256; t=1755000000; cv=none; d=forwarder.example;\r\n" +
         "\ts=arcsel; b=AAAAAAAAAAAA\r\n" +
         "ARC-Seal: i=1; a=rsa-sha256; t=1755000001; cv=none; d=forwarder.example;\r\n" +
         "\ts=arcsel; b=BBBBBBBBBBBB\r\n" +
         "ARC-Seal: i=banana; cv=none; d=forwarder.example; s=arcsel; b=CCCC\r\n" +
         "ARC-Seal: i=9999; cv=pass; d=forwarder.example; s=arcsel; b=DDDD\r\n" +
         "ARC-Message-Signature: i=1; a=rsa-sha256; d=forwarder.example; s=arcsel;\r\n" +
         "\th=from; bh=AAAA; b=AAAA\r\n" +
         "ARC-Authentication-Results: i=1; forwarder.example; dmarc=pass\r\n";

      private static string BuildMessage(string address, string prefixHeaders)
      {
         return prefixHeaders +
                "From: <" + address + ">\r\n" +
                "To: <" + address + ">\r\n" +
                "Subject: ARC inbound filtering\r\n" +
                "Date: Thu, 13 Aug 2026 10:00:00 +0000\r\n" +
                "\r\n" +
                "Test body\r\n";
      }

      [Test]
      [Description(
         "The shipping default: no trusted sealers are configured, so a fabricated ARC chain " +
         "claiming dmarc=pass at the first hop must change nothing. The failing DKIM signature " +
         "scores 6 against a mark threshold of 5; if the chain were honoured without configured " +
         "trust, the DMARC-failure offset would pull the total under the threshold and the spam " +
         "header would disappear - which is exactly the laundering RFC 8617 7.1 warns about.")]
      public void WhenNoTrustIsConfigured_AFabricatedAllPassChainMustNotReduceTheSpamScore()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "arcfilter1@example.test", "test");

         var message = BuildMessage(account.Address, FabricatedAllPassChain + PermanentlyFailingDkimSignature);

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, message);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         Assert.IsTrue(text.Contains("X-hMailServer-Spam: YES"),
            "The message must be marked as spam exactly as it would be without the ARC chain.\r\n" + text);
         Assert.IsTrue(text.Contains("Rejected by DKIM. - (Score: 6)"),
            "The DKIM failure score must stand at its full value.\r\n" + text);
      }

      [Test]
      [Description(
         "A chain whose newest seal records cv=fail is a hop's own statement that the chain is " +
         "broken. Its defined outcome is 'as if there were no chain': the DKIM failure score " +
         "stands and the message is marked, the same as the fabricated-chain case.")]
      public void WhenTheChainRecordsCvFail_ScoringIsUnchanged()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "arcfilter2@example.test", "test");

         var message = BuildMessage(account.Address, CvFailChain + PermanentlyFailingDkimSignature);

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, message);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         Assert.IsTrue(text.Contains("X-hMailServer-Spam: YES"),
            "A cv=fail chain must not change the verdict in either direction.\r\n" + text);
         Assert.IsTrue(text.Contains("Rejected by DKIM. - (Score: 6)"),
            "The DKIM failure score must stand at its full value.\r\n" + text);
      }

      [Test]
      [Description(
         "A malformed chain - duplicate instances, non-numeric and out-of-range i= values - must " +
         "be exactly as if the message carried no chain: delivered, and not scored in EITHER " +
         "direction. There is no DKIM signature on this one, so any spam marking at all would " +
         "mean the broken chain itself was penalized, which no failure mode is allowed to do. " +
         "The fixture's crash oracle asserts the hostile input also corrupted no memory.")]
      public void WhenTheChainIsMalformed_TheMessageIsHandledAsIfItHadNoChain()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "arcfilter3@example.test", "test");

         var message = BuildMessage(account.Address, MalformedChain);

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, message);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         Assert.IsFalse(text.Contains("X-hMailServer-Spam"),
            "A malformed chain must not be scored as spam evidence.\r\n" + text);
      }
   }
}
