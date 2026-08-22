// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// A client that closes its connection part way through a binary transfer -
   /// an SMTP DATA body with no terminating dot, or a BDAT chunk shorter than
   /// the octet count it announced. End-of-stream is tolerated while a binary
   /// transfer is in progress, because a peer can close its side with data still
   /// unread; the danger is treating that as "carry on reading", since a read
   /// re-armed on a closed socket completes immediately and forever. These tests
   /// pin the two things that must hold: the server stays responsive, and a
   /// truncated transfer is never delivered as if it were complete.
   /// </summary>
   [TestFixture]
   public class AbortedTransfers : TestFixtureBase
   {
      private static TcpConnection ConnectAndEhlo()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         socket.Send("EHLO example.test\r\n");
         string ehlo = socket.ReadUntil("250 HELP");
         Assert.IsTrue(ehlo.Contains("250"), "EHLO was not accepted. Got: " + ehlo);
         return socket;
      }

      private void AbortDuringData(string address)
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         socket.Send("HELO example.test\r\n");
         socket.Receive();

         Assert.IsTrue(socket.SendAndReceive("MAIL FROM:<aborter@example.test>\r\n").StartsWith("250"));
         Assert.IsTrue(socket.SendAndReceive("RCPT TO:<" + address + ">\r\n").StartsWith("250"));
         Assert.IsTrue(socket.SendAndReceive("DATA\r\n").StartsWith("354"));

         // Part of a message, and then nothing: no CRLF.CRLF ever arrives.
         socket.Send("Subject: aborted\r\n\r\nThis body is never terminated.\r\n");

         // Half-close so the server sees a genuine end-of-stream. Closing outright
         // would make Windows send RST, which takes a different path entirely.
         socket.HalfCloseSend();
         socket.Disconnect();
      }

      [Test]
      [Description("A client that vanishes mid-DATA must not leave the session reading a closed socket")]
      public void AbortedDataTransfersLeaveTheServerResponsive()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         // Several, because the symptom of getting this wrong is a worker thread
         // pinned per abandoned session: one alone might not be noticed.
         for (var i = 0; i < 5; i++)
            AbortDuringData(account.Address);

         // The real evidence: a session that keeps re-arming a read on a socket
         // the peer has closed is never torn down, so it stays counted forever.
         CustomAsserts.AssertSessionCount(eSessionType.eSTSMTP, 0);

         // The server must still take mail, and none of the abandoned transfers
         // may have been delivered.
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "After the aborts", "Body");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         Assert.IsTrue(message.Contains("After the aborts"), message);
         Assert.IsFalse(message.Contains("never terminated"), message);
      }

      [Test]
      [Description("A BDAT chunk cut short must not be padded out and accepted as a whole message")]
      public void TruncatedBdatChunkIsNotDelivered()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var socket = ConnectAndEhlo();

         Assert.IsTrue(socket.SendAndReceive("MAIL FROM:<aborter@example.test>\r\n").StartsWith("250"));
         Assert.IsTrue(socket.SendAndReceive("RCPT TO:<" + account.Address + ">\r\n").StartsWith("250"));

         // Announce far more than is actually sent, then disappear.
         var partial = "Subject: truncated\r\n\r\nOnly the beginning of the body.\r\n";
         socket.Send("BDAT " + (Encoding.ASCII.GetByteCount(partial) + 40000) + " LAST\r\n");
         socket.Send(partial);

         socket.Disconnect();

         // The transaction never completed, so nothing may be delivered - least of
         // all the fragment padded out to the announced length.
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "After the truncated chunk", "Body");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");
         Assert.IsTrue(message.Contains("After the truncated chunk"), message);
         Assert.IsFalse(message.Contains("Only the beginning"), message);
      }
   }
}
