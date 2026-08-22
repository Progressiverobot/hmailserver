// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam.DKIM
{
   /// <summary>
   ///    RFC 8301 section 3.1 removed rsa-sha1 from DKIM: a signer MUST NOT use it,
   ///    and a verifier MUST treat a signature that does as invalid.
   ///
   ///    That is not housekeeping. A DKIM signature is an assertion of identity, DMARC
   ///    alignment is built on it, and SHA-1 chosen-prefix collisions have been
   ///    practical since 2020 and have only got cheaper. A signature nobody can forge
   ///    is the whole value of the mechanism, so one that somebody can is worth less
   ///    than no signature at all: it carries a domain's name into an
   ///    Authentication-Results header and past an aligned DMARC check, which is
   ///    exactly the outcome an attacker wants and a receiver cannot tell apart.
   ///
   ///    The message here is signed for real - rsa-sha1 over simple/simple
   ///    canonicalization, with the key whose public half this fixture publishes - so
   ///    the refusal has to be a refusal. A test that fed the verifier a signature
   ///    that was merely malformed would pass against a server that had never heard of
   ///    RFC 8301, which is why the control below matters: with DkimAcceptSha1=1 the
   ///    same bytes verify and report dkim=pass, proving the signature is sound and
   ///    that only the policy changed.
   /// </summary>
   [TestFixture]
   public class Sha1SignatureRejection : TestFixtureBase
   {
      private const string SignerDomain = "sha1-signer.test";
      private const string Selector = "TestSelector";

      private const string PublicKey =
         "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAit1HZshVeIm3Yu3dBqKzIAQDM+k5hPu+S9RzJeaFnPQw88" +
         "8jfvuBQkVTinZWn65X4TLhcEjsV7iDgWzVhcEKUUphhpR9i+JgOjncOSxs7zvv2xOpFuYweOqVrWV9brr8DEt3f+Md" +
         "fYUiz62toL82Za447DOhNI/YAVEJqCmgbeSycN2emmZC6Z8dXV7fxKM3IeJ6G8hVLbvWhZe8fHkJ0+tJXeARBHhowF" +
         "W1VXgkOGOHFtPjpmNrJRbbDKf8+IqyUk9uV51y3GEIunovr1Yc3vvExpXwWLZIdqKtvGVFBxyvTtuAmtw7Ebmz0evN" +
         "41wH7vTWgui0VgsZqNIIUwz+fQIDAQAB";

      private const string SignedMessage =
         "DKIM-Signature: v=1; a=rsa-sha1; c=simple/simple; d=sha1-signer.test; s=TestSelector; h=from:to:subject:date; bh=Np/rnrzmsZyWZUSVdOvFH+pEnFU=; b=M6M6xVT44WExmoRXOvpx2Tqn+PBV2QEGWGX5TgqteI5rylFNgSGBDxVoRNXEuv/qsr8FIMQmdSztnqaGraHJWE9t1FZczzwVio1pFMSOJFzjkswKhNAjb9EaZ9QOefxpMSzfFEdUmEXFB1ZkO+JbOZAmomuo8w+8agwPfxzu2yqbu9poFchNgk2u1gxpvh5MvTG5HtxZwdM4rKb/sgqb5bF3Isd5l3M+bjZ7nGGx3odwmfI3lCjDmjx2T6shRGl0il6jshRNzcbtKtxTV3+4dq9WoEr9hKFTVHJbO1KLs3aNmN7clm6eTaxGelZfZCjzGlWKz7uEf03sw9XY7NhomA==\r\n" +
         "From: <sender@sha1-signer.test>\r\n" +
         "To: <dkimsha1@example.test>\r\n" +
         "Subject: rsa-sha1 signature\r\n" +
         "Date: Thu, 20 Aug 2026 10:00:00 +0000\r\n" +
         "\r\n" +
         "This message is signed with rsa-sha1.\r\n";

      /// <summary>
      ///    Delivers the signed message to a fresh mailbox and returns it as stored,
      ///    Authentication-Results header and all.
      /// </summary>
      private static string DeliverAndFetch(string address)
      {
         SmtpClientSimulator.StaticSendRaw("bounce@" + SignerDomain, address, SignedMessage);

         ImapClientSimulator.AssertMessageCount(address, "test", "Inbox", 1);

         var imap = new ImapClientSimulator();
         imap.ConnectAndLogon(address, "test");
         imap.SelectFolder("Inbox");

         return imap.Fetch("1 RFC822");
      }

      [Test]
      [Description("A genuine rsa-sha1 signature is refused by default per RFC 8301, and the same bytes verify with DkimAcceptSha1=1 - which is what proves the signature was sound and only the policy changed")]
      public void AGenuineRsaSha1SignatureIsRefusedUnlessExplicitlyAllowed()
      {
         var antiSpam = _application.Settings.AntiSpam;
         antiSpam.SpamMarkThreshold = 100;
         antiSpam.SpamDeleteThreshold = 1000;
         antiSpam.DKIMVerificationEnabled = true;

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dkimsha1-off@example.test", "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "dkimsha1-on@example.test", "test");

         ServerIniFile.SetSetting("AuthenticationResultsEnabled", "1");
         ServerIniFile.SetSetting("DNSServer", "127.0.0.1");
         ServerIniFile.SetSetting("DNSQueryTimeout", "5");

         using (new FakeDnsServer()
            .WithTxt(Selector + "._domainkey." + SignerDomain, "v=DKIM1; k=rsa; p=" + PublicKey))
         {
            try
            {
               // The shipped default, made explicit - an aborted fixture could have
               // left the escape hatch open.
               ServerIniFile.SetSetting("DkimAcceptSha1", "0");
               RestartServerAndReacquireCom();

               string refused = DeliverAndFetch("dkimsha1-off@example.test");

               ClassicAssert.IsFalse(refused.Contains("dkim=pass"),
                  "An rsa-sha1 signature must not report dkim=pass. RFC 8301 removed the algorithm " +
                  "from DKIM because a forgeable signature is worse than none - it carries the " +
                  "signer's domain past an aligned DMARC check.\r\n" + refused);

               // ...and the control that makes the assertion above mean something. The
               // same bytes, the same key, the same message: only the setting differs.
               // Without this, a signature that was merely broken would produce the
               // same "not pass" and prove nothing about RFC 8301.
               ServerIniFile.SetSetting("DkimAcceptSha1", "1");
               RestartServerAndReacquireCom();

               string accepted = DeliverAndFetch("dkimsha1-on@example.test");

               StringAssert.Contains("dkim=pass", accepted,
                  "With DkimAcceptSha1=1 the signature must verify. If it does not, the signature " +
                  "itself is wrong and the refusal above was for the wrong reason - which would " +
                  "make this fixture a test of nothing.\r\n" + accepted);
            }
            finally
            {
               ServerIniFile.SetSetting("DkimAcceptSha1", null);
               ServerIniFile.SetSetting("AuthenticationResultsEnabled", null);
               ServerIniFile.SetSetting("DNSServer", null);
               ServerIniFile.SetSetting("DNSQueryTimeout", null);
               RestartServerAndReacquireCom();
            }
         }
      }
   }
}
