// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Text;
using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   /// The IMAP master user: SASL PLAIN with an authzid, where one account authenticates
   /// with its own password and acts as another. It is the only impersonation path in
   /// the server, and before 13 August 2026 it had no test of any kind — TestSetup only
   /// ever cleared IMAPMasterUser — which is how three defects survived in it.
   ///
   /// All three failed closed, so this was a security feature that did not work rather
   /// than a hole. That is not much comfort: a master user who cannot log in looks
   /// exactly like one whose password is wrong.
   ///
   /// 1. The two branches resolved a bare master name against different domains — the
   ///    server default when the authzid arrived without one, the authenticating
   ///    account's domain when it arrived with one. Which account held the privilege
   ///    depended on how the client formatted its input, and on a multi-domain server
   ///    those are different accounts. They agree only on a single-domain install.
   /// 2. The qualified branch appended a domain without checking whether the configured
   ///    master user already had one, so "admin@example.test" became
   ///    "admin@example.test@example.test" and could never match.
   /// 3. The comparison was case-sensitive, where every other address comparison in the
   ///    tree uses CompareNoCase.
   ///
   /// The tests that pin the refusals matter as much as the ones that pin the success:
   /// widening this by accident is the only way it becomes a real hole.
   /// </summary>
   [TestFixture]
   public class MasterUserImpersonation : TestFixtureBase
   {
      private const string MasterAddress = "master@example.test";
      private const string TargetAddress = "target@example.test";
      private const string Password = "test";

      private static string EncodeBase64(string s)
      {
         return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
      }

      /// <summary>
      /// Runs AUTHENTICATE PLAIN with an authzid and returns the tagged response.
      /// </summary>
      private static string AuthenticateAs(string authzid, string authcid, string password)
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 AUTHENTICATE PLAIN\r\n");

         // Receive rather than ReadUntil("+"): the continuation is a single short line
         // ("+ "), and ReadUntil would stop ON the plus and leave the rest of the line
         // in the buffer for the next read to trip over.
         var continuation = socket.Receive();

         StringAssert.StartsWith("+", continuation,
            "The server did not ask for the SASL response. Got: " + continuation);

         // RFC 4616: authzid NUL authcid NUL password.
         socket.Send(EncodeBase64(authzid + "\0" + authcid + "\0" + password) + "\r\n");
         var result = socket.ReadUntil("A01 ");

         socket.Disconnect();

         return result;
      }

      private bool _originalSasl;

      [SetUp]
      public void EnableSasl()
      {
         // Off in the shipped configuration, so AUTHENTICATE answers "not enabled" and
         // every test below would pass or fail for the wrong reason. Restored in
         // TearDown rather than left on: nothing else in the suite expects it enabled,
         // and a setting one fixture turns on and never turns off is how a test starts
         // depending on the order it runs in.
         _originalSasl = _settings.IMAPSASLPlainEnabled;
         _settings.IMAPSASLPlainEnabled = true;

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, MasterAddress, Password);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, TargetAddress, Password);
      }

      [TearDown]
      public void ClearMasterUser()
      {
         _settings.IMAPMasterUser = string.Empty;
         _settings.IMAPSASLPlainEnabled = _originalSasl;
      }

      [Test]
      [Description("A master user configured as a full email address can impersonate. Before the fix the " +
                   "qualified branch appended a domain unconditionally, making it admin@d@d, so this " +
                   "configuration never worked at all.")]
      public void AMasterUserConfiguredAsAFullAddressCanImpersonate()
      {
         _settings.IMAPMasterUser = MasterAddress;

         var result = AuthenticateAs(TargetAddress, MasterAddress, Password);

         StringAssert.Contains("A01 OK", result,
            "A master user configured as a full address must be able to act as another account. Got: " + result);
      }

      [Test]
      [Description("A master user configured as a bare name still works, resolved against the domain of " +
                   "the account authenticating. This is the case that worked before and must keep working.")]
      public void AMasterUserConfiguredAsABareNameCanImpersonate()
      {
         _settings.IMAPMasterUser = "master";

         var result = AuthenticateAs(TargetAddress, MasterAddress, Password);

         StringAssert.Contains("A01 OK", result,
            "A bare master user name must resolve against the authenticating account's domain. Got: " + result);
      }

      [Test]
      [Description("The master user name is compared case-insensitively, as every other address " +
                   "comparison in the server is. Before the fix std::wstring::compare refused this.")]
      public void TheMasterUserNameIsComparedCaseInsensitively()
      {
         _settings.IMAPMasterUser = "MASTER@EXAMPLE.TEST";

         var result = AuthenticateAs(TargetAddress, MasterAddress, Password);

         StringAssert.Contains("A01 OK", result,
            "The master user comparison must be case-insensitive. Got: " + result);
      }

      [Test]
      [Description("Security: with no master user configured, an authzid is refused outright rather than " +
                   "treated as an ordinary login.")]
      public void ImpersonationIsRefusedWhenNoMasterUserIsConfigured()
      {
         _settings.IMAPMasterUser = string.Empty;

         var result = AuthenticateAs(TargetAddress, MasterAddress, Password);

         StringAssert.Contains("No master user defined", result,
            "An authzid with no master user configured must be refused. Got: " + result);
      }

      [Test]
      [Description("Security: an account that is not the master user cannot impersonate, even with its " +
                   "own correct password. This is the assertion that keeps the fix from being a widening.")]
      public void AnAccountThatIsNotTheMasterCannotImpersonate()
      {
         _settings.IMAPMasterUser = MasterAddress;

         // target authenticates correctly as itself, and asks to act as the master.
         var result = AuthenticateAs(MasterAddress, TargetAddress, Password);

         StringAssert.Contains("Invalid master user", result,
            "Only the configured master user may impersonate. Got: " + result);
      }

      [Test]
      [Description("Security: being the master user is not enough - the password presented is still the " +
                   "master's own and is still verified.")]
      public void TheMasterUsersPasswordIsStillRequired()
      {
         _settings.IMAPMasterUser = MasterAddress;

         var result = AuthenticateAs(TargetAddress, MasterAddress, "not-the-password");

         ClassicAssert.IsFalse(result.Contains("A01 OK"),
            "Impersonation must still require the master user's correct password. Got: " + result);
      }
   }
}
