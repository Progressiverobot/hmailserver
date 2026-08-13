// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam.DKIM
{
   [TestFixture]
   public class Verification : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _application.Settings.AntiSpam;

         _antiSpam.SpamDeleteThreshold = 5;
      }

      private hMailServer.AntiSpam _antiSpam;

      [Test]
      [Description("Test that a message with an invalid body hash is blocked.")]
      public void TestInvalidBodyHash()
      {
         _antiSpam.DKIMVerificationEnabled = true;
         _antiSpam.DKIMVerificationFailureScore = 100;


         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address,
               TestResources.MessageWithInvalidDkim));
      }

      [Test]
      [Description("Test that tagging of spam works.")]
      public void TestInvalidBodyHashMark()
      {
         _antiSpam.SpamDeleteThreshold = 1000;
         _antiSpam.SpamMarkThreshold = 5;
         _antiSpam.DKIMVerificationEnabled = true;
         _antiSpam.DKIMVerificationFailureScore = 6;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address, TestResources.MessageWithInvalidDkim);
         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         Assert.IsTrue(text.Contains("Rejected by DKIM. - (Score: 6)"));
      }

      [Test]
      [Description("Test that a message with an invalid signature is not blocked.")]
      public void TestInvalidSignature()
      {
         _antiSpam.DKIMVerificationEnabled = true;
         _antiSpam.DKIMVerificationFailureScore = 100;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address,
               TestResources.MessageWithInvalidDkim));
      }

      [Test]
      [Description("Test that a message with a valid SHA1 signature is not blocked.")]
      public void TestValidSignatureSHA256()
      {
         _antiSpam.DKIMVerificationEnabled = true;
         _antiSpam.DKIMVerificationFailureScore = 100;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address, TestResources.MessageWithValidDkim);
         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
      }

      // A signature whose selector does not exist in DNS is a permanent failure:
      // the lookup succeeds and answers "no such name", which RFC 6376 6.1.2 calls
      // "no key for signature". It needs no key of our own and no live zone.
      private const string PermanentlyFailingSignature =
         "DKIM-Signature: v=1; a=rsa-sha256; c=relaxed/relaxed; d=example.test;\r\n" +
         "\ts=nosuchselector; h=from:subject;\r\n" +
         "\tbh=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=; b=AAAAAAAAAAAA\r\n";

      // v=2 is not a version this implementation supports, so this signature is
      // ignored and asserts nothing either way. It is also free: it is refused
      // before any DNS lookup happens.
      private const string UnparseableSignature =
         "DKIM-Signature: v=2; a=rsa-sha256; c=relaxed/relaxed; d=example.test;\r\n" +
         "\ts=whatever; h=from:subject; bh=AAAA; b=AAAA\r\n";

      private static string BuildSignedMessage(string address, string signatureHeaders)
      {
         return signatureHeaders +
                "From: <" + address + ">\r\n" +
                "To: <" + address + ">\r\n" +
                "Subject: DKIM verdict precedence\r\n" +
                "Date: Wed, 12 Aug 2026 10:00:00 +0000\r\n" +
                "\r\n" +
                "Test body\r\n";
      }

      [Test]
      [Description(
         "A second signature which asserts nothing must not erase the failure of the first. " +
         "The message-level verdict used to be whichever signature came last in the header, so " +
         "appending one unparseable DKIM-Signature after a failing one removed the failure score.")]
      public void TestFailingSignatureIsNotErasedByLaterUnparseableSignature()
      {
         _antiSpam.DKIMVerificationEnabled = true;
         _antiSpam.DKIMVerificationFailureScore = 100;
         _antiSpam.SpamDeleteThreshold = 5;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var message = BuildSignedMessage(account1.Address,
            PermanentlyFailingSignature + UnparseableSignature);

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address, message));
      }

      [Test]
      [Description(
         "Negative control for the test above: a signature which asserts nothing must not become " +
         "a failure on its own either. Without this, ranking the outcomes could be 'fixed' by " +
         "scoring every signature that cannot be parsed.")]
      public void TestUnparseableSignatureAloneIsNotScored()
      {
         _antiSpam.DKIMVerificationEnabled = true;
         _antiSpam.DKIMVerificationFailureScore = 100;
         _antiSpam.SpamDeleteThreshold = 5;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var message = BuildSignedMessage(account1.Address, UnparseableSignature);

         SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address, message);

         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");
         Assert.IsFalse(text.Contains("Rejected by DKIM."), text);
      }
   }
}