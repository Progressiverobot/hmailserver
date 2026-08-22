// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   /// Clients that vanish in the middle of an operation - a half-closed socket
   /// during a transfer, an abandoned command, a connection dropped between the
   /// announcement of data and the data itself. The regression suite is dense on
   /// completed protocol exchanges and was historically silent on abandoned ones,
   /// which is where both 6.2.15 review blockers lived. Every test here asserts
   /// the same two things: the abandoned session is actually torn down (the
   /// session count returns to zero, so no worker is left owning a dead socket),
   /// and the server still serves the next client correctly.
   /// </summary>
   [TestFixture]
   public class AbortedSessions : TestFixtureBase
   {
      private hMailServer.Account account_;

      [SetUp]
      public void CreateAccount()
      {
         account_ = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
      }

      private static TcpConnection ConnectTo(int port, string expectedGreeting)
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(port));
         Assert.IsTrue(socket.Receive().StartsWith(expectedGreeting),
            "Unexpected greeting on port " + port);
         return socket;
      }

      private void AssertServerRecovered(eSessionType sessionType)
      {
         CustomAsserts.AssertSessionCount(sessionType, 0);

         // The strongest recovery evidence: a complete, ordinary transaction
         // succeeds after the aborted ones.
         SmtpClientSimulator.StaticSend(account_.Address, account_.Address, "After the aborts", "Body");
         Pop3ClientSimulator.AssertMessageCount(account_.Address, "test", 1);
      }

      [Test]
      [Description("IMAP APPEND literal cut short by a half-close must not leave the session alive or store a message")]
      public void ImapAppendLiteralCutShort()
      {
         for (var i = 0; i < 3; i++)
         {
            var socket = ConnectTo(143, "* OK");
            socket.Send("A01 LOGIN " + account_.Address + " test\r\n");
            socket.ReadUntil("A01 OK");
            socket.Send("A02 APPEND INBOX {50000}\r\n");
            socket.ReadUntil("+");

            // A fragment of the promised literal, then a clean end-of-stream.
            socket.Send("Subject: cut short\r\n\r\nOnly the beginning.");
            socket.HalfCloseSend();
            socket.Disconnect();
         }

         CustomAsserts.AssertSessionCount(eSessionType.eSTIMAP, 0);
         ImapClientSimulator.AssertMessageCount(account_.Address, "test", "INBOX", 0);

         AssertServerRecovered(eSessionType.eSTSMTP);
      }

      [Test]
      [Description("IMAP client that half-closes mid-command line must be torn down")]
      public void ImapCommandLineCutShort()
      {
         for (var i = 0; i < 3; i++)
         {
            var socket = ConnectTo(143, "* OK");

            // A command with no terminating CRLF, then end-of-stream.
            socket.Send("A01 LOGIN " + account_.Address);
            socket.HalfCloseSend();
            socket.Disconnect();
         }

         CustomAsserts.AssertSessionCount(eSessionType.eSTIMAP, 0);
         AssertServerRecovered(eSessionType.eSTSMTP);
      }

      [Test]
      [Description("POP3 client that vanishes while the server is sending RETR data must be torn down")]
      public void Pop3ClientVanishesDuringRetr()
      {
         // A message large enough that RETR cannot complete in one send. Built as
         // many lines: a single long line would be rejected at the line limit.
         var line = new string('x', 76) + "\r\n";
         var body = string.Concat(System.Linq.Enumerable.Repeat(line, 6000));
         SmtpClientSimulator.StaticSend(account_.Address, account_.Address, "Large", body);
         Pop3ClientSimulator.AssertMessageCount(account_.Address, "test", 1);

         for (var i = 0; i < 3; i++)
         {
            var socket = ConnectTo(110, "+OK");
            Assert.IsTrue(socket.SendAndReceive("USER " + account_.Address + "\r\n").StartsWith("+OK"));
            Assert.IsTrue(socket.SendAndReceive("PASS test\r\n").StartsWith("+OK"));
            socket.Send("RETR 1\r\n");

            // Read a fragment of the response, then disappear without QUIT. The
            // mailbox lock must be released, or the next iteration cannot log on.
            socket.Receive();
            socket.Disconnect();
         }

         CustomAsserts.AssertSessionCount(eSessionType.eSTPOP3, 0);

         // The lock must be free for a normal client.
         var pop3 = new Pop3ClientSimulator();
         pop3.ConnectAndLogon(account_.Address, "test");
         Assert.IsTrue(pop3.LIST().Contains("1 "), "Mailbox not usable after aborted RETR sessions");
         pop3.QUIT();
      }

      [Test]
      [Description("SMTP client that half-closes between MAIL and RCPT must be torn down without a stored message")]
      public void SmtpAbortedBetweenCommands()
      {
         for (var i = 0; i < 3; i++)
         {
            var socket = ConnectTo(25, "220");
            socket.Send("HELO example.test\r\n");
            socket.Receive();
            socket.Send("MAIL FROM:<someone@example.test>\r\n");
            socket.Receive();
            socket.HalfCloseSend();
            socket.Disconnect();
         }

         CustomAsserts.AssertSessionCount(eSessionType.eSTSMTP, 0);
         AssertServerRecovered(eSessionType.eSTSMTP);
      }

      [Test]
      [Description("A connection opened and immediately half-closed, repeatedly, must not accumulate sessions")]
      public void ConnectAndImmediateHalfClose()
      {
         foreach (var port in new[] {25, 110, 143})
         {
            for (var i = 0; i < 5; i++)
            {
               var socket = new TcpConnection();
               Assert.IsTrue(socket.Connect(port));
               socket.HalfCloseSend();
               socket.Disconnect();
            }
         }

         CustomAsserts.AssertSessionCount(eSessionType.eSTSMTP, 0);
         CustomAsserts.AssertSessionCount(eSessionType.eSTPOP3, 0);
         CustomAsserts.AssertSessionCount(eSessionType.eSTIMAP, 0);

         AssertServerRecovered(eSessionType.eSTSMTP);
      }
   }
}
