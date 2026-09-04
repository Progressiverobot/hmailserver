// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam.DKIM
{
   [TestFixture]
   public class Verification : TestFixtureBase
   {
      // Both captured Outlook messages sign as d=outlook.com; s=selector1, and this is
      // that selector's public key, served locally for the life of this fixture rather
      // than fetched.
      //
      // The live record is a CNAME into protection.outlook.com, and it measures anywhere
      // between 60 ms and 4 seconds depending on nothing a test can control. Against the
      // server's 10-second query timeout that is a coin toss under load, and losing it is
      // silent: a key that cannot be fetched is a TEMPORARY error, so the message is
      // accepted and a test asserting it is rejected fails while reporting nothing about
      // DKIM. On 19 August 2026 all three did exactly that, taking 24-35 seconds each.
      //
      // Serving it locally also removes a second dependency nobody had written down: the
      // "valid signature" message verifies against whatever key Microsoft publishes
      // today, so a key rotation there would break this suite for a reason that has
      // nothing to do with this server.
      //
      // 437 characters, so it does not fit one DNS character-string. That is deliberate:
      // RFC 6376 3.6.2 requires a verifier to concatenate the strings of a TXT record,
      // and until now nothing here proved this one does.
      private const string OutlookSelectorName = "selector1._domainkey.outlook.com";

      private const string OutlookSelectorKey =
         "v=DKIM1;k=rsa;p=MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAvWyktrIL8DO/+UGvMbv7cPd/Xogpbs7pgVw8" +
         "y9ldO6AAMmg8+ijENl/c7Fb1MfKM7uG3LMwAr0dVVKyM+mbkoX2k5L7lsROQr0Z9gGSpu7xrnZOa58+/pIhd2Xk/DFPpa5+T" +
         "KbWodbsSZPRN8z0RY5x59jdzSclXlEyN9mEZdmOiKTsOP6A7vQxfSya9jg5N81dfNNvP7HnWejMMsKyIMrXptxOhIBuEYH67" +
         "JDe98QgX14oHvGM2Uz53if/SW8MF09rYh9sp4ZsaWLIg6T343JzlbtrsGRGCDJ9JPpxRWZimtz+Up/BlKzT6sCCrBihb/Bi3" +
         "pZiEBB4Ui/vruL5RCQIDAQAB;n=2048,1452627113,1468351913";

      private FakeDnsServer dns_;

      [OneTimeSetUp]
      public void PointTheServerAtALocalResolver()
      {
         dns_ = new FakeDnsServer().WithTxt(OutlookSelectorName, OutlookSelectorKey);

         // Every other name answers NODATA, which is what the rest of this fixture wants:
         // "no key for signature" (RFC 6376 6.1.2) is a PERMANENT failure, so a selector
         // that does not exist is a definite verdict rather than a lookup to wait for.
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

         // The header this test reads is written only when AddHeaderReason is on,
         // and nothing in this fixture turned it on: the test passed because
         // AntiSpam.Basics runs earlier in a full suite and leaves it set. Run
         // alone it failed, and the delivered message came back with no spam
         // headers at all - which reads as "the DKIM check did not fire" and sent
         // one investigation after a DKIM change that was not responsible.
         _antiSpam.AddHeaderReason = true;

         var account1 = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
         SmtpClientSimulator.StaticSendRaw(account1.Address, account1.Address, TestResources.MessageWithInvalidDkim);
         var text = Pop3ClientSimulator.AssertGetFirstMessageText(account1.Address, "test");

         Assert.IsTrue(text.Contains("Rejected by DKIM. - (Score: 6)"),
            "The delivered message should carry the DKIM spam mark. Got: " + text);
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