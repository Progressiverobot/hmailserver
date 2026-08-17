// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.POP3
{
   /// <summary>
   ///    PIPELINING (RFC 2449 section 6.6), advertised only after it was proved
   ///    rather than assumed: these tests drive batched commands through single
   ///    TCP segments and the parse loop drains its buffer correctly. The one
   ///    dangerous interaction - cleartext commands pipelined ahead of STLS -
   ///    was already closed before this capability was claimed: the receive
   ///    buffer is cleared when the handshake completes and parsed credentials
   ///    are wiped, so an injected prefix neither executes nor lingers.
   /// </summary>
   [TestFixture]
   public class Pipelining : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "pipeline@example.test", "test");
         SmtpClientSimulator.StaticSend(_account.Address, _account.Address, "pipelined", "Body.");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);
      }

      [Test]
      [Description("CAPA advertises PIPELINING")]
      public void CapaAdvertisesPipelining()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(110));
         socket.ReadUntil("+OK");

         socket.Send("CAPA\r\n");
         var capabilities = socket.ReadUntil("\r\n.\r\n");

         StringAssert.Contains("PIPELINING", capabilities,
            "RFC 2449 6.6 - a client may not batch unless this is advertised. Got: " + capabilities);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      [Description("USER+PASS+STAT+QUIT in one TCP segment - four responses come back in order, across the authentication boundary")]
      public void BatchedAuthorizationAndTransactionCommands()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(110));
         socket.ReadUntil("+OK");

         socket.Send("USER pipeline@example.test\r\nPASS test\r\nSTAT\r\nQUIT\r\n");

         var all = socket.ReadUntil("saying goodbye");

         StringAssert.Contains("+OK Send your password", all, "USER response missing. Got: " + all);
         StringAssert.Contains("+OK Mailbox locked and ready", all, "PASS response missing. Got: " + all);
         StringAssert.Contains("+OK 1 ", all, "STAT response missing. Got: " + all);

         socket.Disconnect();
      }

      [Test]
      [Description("Batched TRANSACTION commands including a multi-line response in the middle")]
      public void BatchedListRetrDeleQuit()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(110));
         socket.ReadUntil("+OK");

         socket.Send("USER pipeline@example.test\r\nPASS test\r\n");
         socket.ReadUntil("Mailbox locked and ready");

         socket.Send("LIST\r\nRETR 1\r\nDELE 1\r\nQUIT\r\n");
         var all = socket.ReadUntil("saying goodbye");

         StringAssert.Contains("+OK 1 messages", all, "LIST response missing. Got: " + all);
         StringAssert.Contains("pipelined", all, "RETR body missing. Got: " + all);
         StringAssert.Contains("+OK msg deleted", all, "DELE response missing. Got: " + all);

         socket.Disconnect();
      }
   }
}
