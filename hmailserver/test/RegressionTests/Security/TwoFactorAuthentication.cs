// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Per-account two-factor authentication.
   ///
   ///    TOTP has existed in this product for years and protected exactly one thing:
   ///    the admin tool's own logon, checked in C# by the Control Panel after the
   ///    server had already accepted the password. The server had no idea the feature
   ///    existed, which is why no mailbox account could ever have a second factor.
   ///
   ///    The obstacle was never the algorithm. IMAP, POP3 and SMTP have nowhere to
   ///    type a code, so an account required to present one could not be opened by any
   ///    client at all - and that is what app passwords were built to solve. The rule
   ///    here is the payoff: once a secret is enrolled, the account password stops
   ///    being a mailbox credential, and an app password becomes the only one.
   ///
   ///    The RFC 6238 vectors are pinned in the server's own self-tests, where
   ///    algorithm vectors belong. What this fixture proves is the part those cannot:
   ///    that the code is computed over the wire by an INDEPENDENT implementation
   ///    (.NET's HMACSHA1, below) and accepted - which is what "works with a real
   ///    authenticator app" actually means - and that the enforcement lands on every
   ///    protocol rather than on whichever one was checked first.
   /// </summary>
   [TestFixture]
   public class TwoFactorAuthentication : TestFixtureBase
   {
      private const string Password = "an-ordinary-account-password";

      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "secondfactor@example.test", Password);
      }

      /// <summary>Enrols, and returns the base32 secret out of the otpauth URI.</summary>
      private string Enrol()
      {
         string uri = _account.EnrolTOTP();
         _account.Save();

         StringAssert.StartsWith("otpauth://totp/", uri, "Got: " + uri);
         StringAssert.Contains("secondfactor%40example.test", uri,
            "The address must be percent-encoded or an authenticator app rejects the QR code. Got: " + uri);

         var secret = Regex.Match(uri, "secret=([A-Z2-7]+)");
         ClassicAssert.IsTrue(secret.Success, "No base32 secret in the URI. Got: " + uri);

         return secret.Groups[1].Value;
      }

      /// <summary>
      ///    RFC 6238 in the test, deliberately not calling anything the server owns.
      ///    A code the server computed itself would only prove the server agrees with
      ///    itself; this is what an authenticator app would produce.
      /// </summary>
      private static string CurrentCode(string base32Secret, int stepOffset = 0)
      {
         byte[] key = Base32Decode(base32Secret);

         long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30 + stepOffset;

         byte[] counterBytes = BitConverter.GetBytes(counter);
         if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

         byte[] hash;
         using (var hmac = new HMACSHA1(key))
            hash = hmac.ComputeHash(counterBytes);

         int offset = hash[hash.Length - 1] & 0x0F;

         int binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

         return (binary % 1000000).ToString("000000");
      }

      private static byte[] Base32Decode(string input)
      {
         const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

         var output = new System.Collections.Generic.List<byte>();
         int buffer = 0, bitsLeft = 0;

         foreach (int index in input.Select(c => alphabet.IndexOf(char.ToUpperInvariant(c))).Where(index => index >= 0))
         {
            buffer = (buffer << 5) | index;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
               bitsLeft -= 8;
               output.Add((byte) ((buffer >> bitsLeft) & 0xFF));
            }
         }

         return output.ToArray();
      }

      private static bool AuthenticatesOverCom(string address, string password, string code)
      {
         var application = new hMailServer.Application();

         return code == null
            ? application.Authenticate(address, password) != null
            : application.AuthenticateWithCode(address, password, code) != null;
      }

      [Test]
      [Description("Nothing is enrolled by default: an ordinary account is untouched by any of this")]
      public void NoSecondFactorByDefault()
      {
         ClassicAssert.IsFalse(_account.TOTPEnabled, "A new account must have no second factor.");

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3.ConnectAndLogon(_account.Address, Password),
            "...and its password must work exactly as it always has.");
         pop3.Disconnect();
      }

      [Test]
      [Description("A code from an independent RFC 6238 implementation is accepted - which is what 'works with an authenticator app' means")]
      public void ACodeComputedElsewhereIsAccepted()
      {
         string secret = Enrol();

         ClassicAssert.IsTrue(_account.TOTPEnabled);

         ClassicAssert.IsTrue(AuthenticatesOverCom(_account.Address, Password, CurrentCode(secret)),
            "The server must accept a code this test computed with .NET's own HMACSHA1. If it does not, " +
            "no authenticator app will work either, however self-consistent the server is.");

         // The adjacent steps are accepted, because a client clock a second out would
         // otherwise fail at the boundary of every window.
         ClassicAssert.IsTrue(AuthenticatesOverCom(_account.Address, Password, CurrentCode(secret, -1)),
            "The previous step must be accepted.");
         ClassicAssert.IsTrue(AuthenticatesOverCom(_account.Address, Password, CurrentCode(secret, 1)),
            "The next step must be accepted.");

         // Two steps away is not.
         ClassicAssert.IsFalse(AuthenticatesOverCom(_account.Address, Password, CurrentCode(secret, 5)),
            "A code five steps out must be refused, or the window is not a window.");
      }

      [Test]
      [Description("Both factors are required: neither the password nor the code is sufficient alone")]
      public void BothFactorsAreRequired()
      {
         string secret = Enrol();

         ClassicAssert.IsFalse(AuthenticatesOverCom(_account.Address, Password, "000000"),
            "The right password with a wrong code must be refused.");

         ClassicAssert.IsFalse(AuthenticatesOverCom(_account.Address, "wrong-password", CurrentCode(secret)),
            "A valid code with the wrong password must be refused - otherwise the code IS the credential.");

         ClassicAssert.IsFalse(AuthenticatesOverCom(_account.Address, Password, null),
            "And the password alone, through the path that cannot present a code, must be refused.");
      }

      [Test]
      [Description("THE point of the chain: the account password stops working in mail clients, and an app password takes over")]
      public void TheAccountPasswordStopsWorkingInMailClientsAndAnAppPasswordTakesOver()
      {
         Enrol();

         // Every protocol, not just the one that happened to be checked first. This is
         // the assertion that catches enforcement landing in a single handler.
         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsFalse(pop3.ConnectAndLogon(_account.Address, Password),
            "POP3 must refuse the account password once a second factor is enrolled.");
         pop3.Disconnect();

         var imap = new ImapClientSimulator();
         ClassicAssert.IsFalse(imap.ConnectAndLogon(_account.Address, Password),
            "IMAP must refuse it too.");
         imap.Disconnect();

         // The simulator throws rather than reporting a refused AUTH through its out
         // parameter, which is what a refusal looks like from here.
         var smtp = new SmtpClientSimulator();
         string smtpError;
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtp.Send(false, _account.Address, Password, _account.Address, _account.Address,
               "denied", "This must not be accepted.", out smtpError));

         // ...and the credential that exists precisely so the mailbox is still usable.
         var appPassword = _account.AppPasswords.Add();
         appPassword.Name = "Phone";
         string issued = appPassword.Generate();
         appPassword.Save();

         var pop3WithApp = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3WithApp.ConnectAndLogon(_account.Address, issued),
            "An app password must still open the mailbox. Without this the second factor is " +
            "not two-factor authentication, it is an outage.");
         pop3WithApp.Disconnect();

         var imapWithApp = new ImapClientSimulator();
         ClassicAssert.IsTrue(imapWithApp.ConnectAndLogon(_account.Address, issued), "...over IMAP as well.");
         imapWithApp.Disconnect();
      }

      [Test]
      [Description("Removing the second factor restores the account password, and deliberately leaves app passwords alone")]
      public void DisablingRestoresTheAccountPassword()
      {
         Enrol();

         var appPassword = _account.AppPasswords.Add();
         appPassword.Name = "Laptop";
         string issued = appPassword.Generate();
         appPassword.Save();

         _account.DisableTOTP();
         _account.Save();

         ClassicAssert.IsFalse(_account.TOTPEnabled);

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3.ConnectAndLogon(_account.Address, Password),
            "The account password must work again.");
         // Released, or the app-password logon below is refused with [IN-USE] and this
         // test reports a revocation that never happened.
         pop3.Disconnect();

         // The app password was a separately revocable credential before the second
         // factor existed and remains one after it goes. Deleting it here would break
         // every client the account holder had set up, as a side effect of an
         // unrelated administrative action.
         var pop3WithApp = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3WithApp.ConnectAndLogon(_account.Address, issued),
            "Disabling the second factor must not revoke app passwords.");
         pop3WithApp.Disconnect();
      }

      [Test]
      [Description("The secret is not readable after enrolment - possessing the account must not yield the factor")]
      public void TheSecretIsNotReadableAfterwards()
      {
         string first = Enrol();

         // There is no property that returns it: the only way to see a secret is to
         // enrol a new one, which replaces the old.
         var type = _account.GetType();
         foreach (string name in new[] { "TOTPSecret", "TotpSecret", "Secret" })
         {
            ClassicAssert.IsNull(type.GetProperty(name),
               "The COM surface must not expose a way to read the secret back (found: " + name + ").");
         }

         string second = _account.EnrolTOTP();
         _account.Save();

         ClassicAssert.IsFalse(second.Contains(first),
            "Re-enrolling must mint a NEW secret rather than returning the existing one.");
      }
   }
}
