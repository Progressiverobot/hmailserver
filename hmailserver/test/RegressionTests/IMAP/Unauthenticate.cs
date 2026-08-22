// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    UNAUTHENTICATE (RFC 8437). An authenticated session can return to the
   ///    not-authenticated state and be reused - the pattern proxies and
   ///    multi-account clients want, previously only possible by reconnecting.
   ///    Everything about the user is discarded: the account, the folder trees,
   ///    the selected mailbox (released without expunge). The per-connection
   ///    authentication-failure counter deliberately survives, so the command
   ///    cannot be used to reset the brute-force cap.
   /// </summary>
   [TestFixture]
   public class Unauthenticate : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "unauth@example.test", "test");
      }

      [Test]
      [Description("The full round trip: login, select, unauthenticate, verify the state is gone, log in again.")]
      public void TheSessionReturnsToNotAuthenticatedAndCanBeReused()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         ClassicAssert.IsTrue(imapSim.SelectFolder("INBOX"));

         imapSim.SendRaw("A02 UNAUTHENTICATE\r\n");
         var response = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("A02 OK", response,
            "UNAUTHENTICATE must succeed in the selected state. Got: " + response);

         // The user's state must be gone: commands needing authentication are
         // refused again, exactly as on a fresh connection.
         imapSim.SendRaw("A03 LIST \"\" %\r\n");
         response = imapSim.ReceiveUntil("A03 ");
         StringAssert.Contains("A03 NO Authenticate first", response,
            "After UNAUTHENTICATE the session must be back to not-authenticated. Got: " + response);

         imapSim.SendRaw("A04 CLOSE\r\n");
         response = imapSim.ReceiveUntil("A04 ");
         StringAssert.Contains("A04 NO Authenticate first", response,
            "No mailbox may remain selected after UNAUTHENTICATE. Got: " + response);

         // And the whole point: the same connection authenticates again.
         imapSim.SendRaw("A05 LOGIN unauth@example.test test\r\n");
         response = imapSim.ReceiveUntil("A05 ");
         StringAssert.Contains("A05 OK", response,
            "The connection must be reusable for a second login. Got: " + response);

         ClassicAssert.IsTrue(imapSim.SelectFolder("INBOX"),
            "The second session must be fully functional.");

         imapSim.Disconnect();
      }

      /// <summary>
      ///    The control: in the not-authenticated state the command is BAD (RFC
      ///    8437 section 3) - an implementation that reset state unconditionally
      ///    would pass the test above and hand an unauthenticated client a state
      ///    transition it has no business making.
      /// </summary>
      [Test]
      [Description("Before authentication the command is refused BAD - the control.")]
      public void BeforeAuthenticationTheCommandIsRefused()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 UNAUTHENTICATE\r\n");
         var response = socket.ReadUntil("A01 ");

         StringAssert.Contains("A01 BAD", response,
            "UNAUTHENTICATE in the not-authenticated state must be refused BAD. Got: " + response);

         socket.Disconnect();
      }

      [Test]
      [Description("The capability appears once authenticated, and not before.")]
      public void TheCapabilityAppearsOnceAuthenticated()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var preAuth = socket.ReadUntil("A01 OK");
         ClassicAssert.IsFalse(preAuth.Contains("UNAUTHENTICATE"),
            "The capability is advertised for the states where the command is valid - " +
            "authenticated and selected - not before. Got: " + preAuth);

         socket.Send("A02 LOGIN unauth@example.test test\r\n");
         socket.ReadUntil("A02 OK");

         socket.Send("A03 CAPABILITY\r\n");
         var postAuth = socket.ReadUntil("A03 OK");
         StringAssert.Contains("UNAUTHENTICATE", postAuth,
            "CAPABILITY must advertise UNAUTHENTICATE once authenticated. Got: " + postAuth);

         socket.Disconnect();
      }
   }
}
