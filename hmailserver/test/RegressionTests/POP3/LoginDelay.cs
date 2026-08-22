// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.POP3
{
   /// <summary>
   ///    RFC 2449 EXPIRE and LOGIN-DELAY.
   ///
   ///    EXPIRE is answered NEVER, which is not a placeholder: nothing in this server
   ///    deletes a delivered message because of its age, so NEVER is the true retention
   ///    policy, and a "leave mail on server" client is entitled to know that what it
   ///    leaves will still be there.
   ///
   ///    LOGIN-DELAY is the standard way to tell a client to poll less often. POP3 has
   ///    no idle notification, so a client that wants to look responsive polls, and each
   ///    poll costs a connection, a TLS handshake and an Argon2id verification. It is
   ///    off by default and advertised only when enforced, because RFC 2449 section 5
   ///    forbids advertising a capability the server will not honour.
   /// </summary>
   [TestFixture]
   public class LoginDelay : TestFixtureBase
   {
      private const int DelaySeconds = 4;

      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         // One account per TEST, not per fixture. The delay is tracked in memory and
         // keyed on the address, and deleting an account does not forget it - so a
         // fixture that reused one address would carry the previous test's login into
         // the next one, and "a failed login does not start the clock" would fail
         // because a DIFFERENT test had started it. Naming the account after the test
         // is what makes each one independent.
         string address = TestContext.CurrentContext.Test.Name.ToLowerInvariant() + "@example.test";

         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");
      }

      [TearDown]
      public new void TearDown()
      {
         ServerIniFile.SetSetting("Pop3LoginDelaySeconds", null);
         _application.Reinitialize();
      }

      private void EnableDelay()
      {
         ServerIniFile.SetSetting("Pop3LoginDelaySeconds", DelaySeconds.ToString());
         _application.Reinitialize();
      }

      private static string Capabilities()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(110), "Could not connect to POP3 on 110.");
         socket.ReadUntil("+OK");

         socket.Send("CAPA\r\n");
         var capabilities = socket.ReadUntil("\r\n.\r\n");

         socket.Send("QUIT\r\n");
         socket.Disconnect();

         return capabilities;
      }

      /// <summary>A full USER/PASS exchange, returning the reply to PASS.</summary>
      private static string Logon(string user, string password)
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(110));
         socket.ReadUntil("+OK");

         socket.Send("USER " + user + "\r\n");
         socket.ReadUntil("\r\n");

         socket.Send("PASS " + password + "\r\n");
         var reply = socket.ReadUntil("\r\n");

         socket.Send("QUIT\r\n");
         socket.Disconnect();

         return reply;
      }

      [Test]
      [Description("EXPIRE NEVER is advertised unconditionally: it is this server's actual retention policy")]
      public void ExpireIsAdvertisedAsNever()
      {
         StringAssert.Contains("EXPIRE NEVER", Capabilities(),
            "Nothing here deletes a message because of its age, and a client that leaves mail on " +
            "the server is entitled to be told that.");
      }

      [Test]
      [Description("LOGIN-DELAY is absent by default and appears with its value once configured - RFC 2449 5 forbids advertising what is not honoured")]
      public void LoginDelayIsAdvertisedOnlyWhenEnforced()
      {
         ClassicAssert.IsFalse(Capabilities().Contains("LOGIN-DELAY"),
            "Off by default, so it must not be advertised by default.");

         EnableDelay();

         StringAssert.Contains("LOGIN-DELAY " + DelaySeconds, Capabilities(),
            "Once configured it must be advertised, with the value a client is expected to honour.");
      }

      [Test]
      [Description("A second login inside the window is refused with [LOGIN-DELAY], not as a bad password")]
      public void ASecondLoginInsideTheWindowIsRefused()
      {
         EnableDelay();

         StringAssert.Contains("+OK", Logon(_account.Address, "test"),
            "The first login must be allowed.");

         var second = Logon(_account.Address, "test");

         StringAssert.StartsWith("-ERR", second, "The second login must be refused. Got: " + second);
         StringAssert.Contains("[LOGIN-DELAY]", second,
            "The code is what makes this a rate limit rather than a rejected password: without it " +
            "the client asks its user to retype a password that was never wrong. Got: " + second);
      }

      [Test]
      [Description("The delay is per account - one client polling hard must not lock everyone else out")]
      public void ADifferentAccountIsUnaffected()
      {
         var other = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "other@example.test", "test");

         EnableDelay();

         StringAssert.Contains("+OK", Logon(_account.Address, "test"));
         StringAssert.Contains("+OK", Logon(other.Address, "test"),
            "A different account has its own clock.");
      }

      [Test]
      [Description("A failed login does not start the clock: the delay must not become a way to lock an account out with wrong passwords")]
      public void AFailedLoginDoesNotStartTheClock()
      {
         EnableDelay();

         StringAssert.StartsWith("-ERR", Logon(_account.Address, "wrong-password"));

         StringAssert.Contains("+OK", Logon(_account.Address, "test"),
            "The wrong password was refused before the delay was ever consulted, so it must not " +
            "have recorded a login - otherwise an attacker could hold an account out with guesses.");
      }

      [Test]
      [Description("Once the window has passed the account may log in again")]
      public void TheWindowExpires()
      {
         EnableDelay();

         StringAssert.Contains("+OK", Logon(_account.Address, "test"));
         StringAssert.Contains("[LOGIN-DELAY]", Logon(_account.Address, "test"));

         Thread.Sleep((DelaySeconds + 1) * 1000);

         StringAssert.Contains("+OK", Logon(_account.Address, "test"),
            "A rate limit that never lifts is an outage.");
      }

      [Test]
      [Description("With the delay off, back-to-back logins are both allowed - the negative control for every test above")]
      public void WithTheDelayOffNothingIsRefused()
      {
         StringAssert.Contains("+OK", Logon(_account.Address, "test"));
         StringAssert.Contains("+OK", Logon(_account.Address, "test"),
            "Off by default means off: the suite itself logs in far faster than any delay would allow.");
      }
   }
}
