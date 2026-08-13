// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// RFC 3207 section 4: a client must not start TLS inside an established TLS
   /// session, and the server must refuse if one tries.
   ///
   /// The server already withheld the STARTTLS keyword from the EHLO response once
   /// the connection was encrypted, but it still ACTED on the command. That was not
   /// a cosmetic breach: it answered 220, put the session into the STARTTLS state
   /// (which enqueues no read) and began a second handshake inside the first. The
   /// handshake fails - the client's next bytes are application data, not a
   /// ClientHello - and SMTPConnection::OnHandshakeFailed is empty, so nothing
   /// re-arms a read and nothing disconnects. One command therefore parked a socket
   /// and its session slot until the idle timeout, which is minutes.
   /// </summary>
   [TestFixture]
   public class StartTlsAlreadyActive : TestFixtureBase
   {
      [OneTimeSetUp]
      public new void TestFixtureSetUp()
      {
         // 25002 is the STARTTLS-optional SMTP port this helper creates.
         SslSetup.SetupSSLPorts(_application);

         Thread.Sleep(1000);
      }

      [Test]
      [Description("A second STARTTLS inside an established TLS session is refused with 503 and " +
                   "the session remains usable.")]
      public void SecondStartTlsInsideTlsIsRefused()
      {
         using (var socket = new TcpConnection())
         {
            Assert.IsTrue(socket.Connect(25002));
            Assert.IsTrue(socket.Receive().StartsWith("220"));

            var capabilities = socket.SendAndReceive("EHLO example.test\r\n");
            Assert.IsTrue(capabilities.Contains("STARTTLS"), capabilities);

            StringAssert.Contains("220 Ready to start TLS", socket.SendAndReceive("STARTTLS\r\n"));
            socket.HandshakeAsClient();

            // Confirm we really are inside TLS: the keyword is gone from EHLO now.
            var encryptedCapabilities = socket.SendAndReceive("EHLO example.test\r\n");
            Assert.IsFalse(encryptedCapabilities.Contains("STARTTLS"), encryptedCapabilities);

            // The command itself must be refused too. Before the fix this returned
            // "220 Ready to start TLS" a second time and then the session went silent.
            var second = socket.SendAndReceive("STARTTLS\r\n");
            Assert.IsTrue(second.StartsWith("503"),
               "A second STARTTLS must be refused with 503 (RFC 3207 section 4). Got: " + second);

            // The refusal must leave the session alive rather than wedged: this is the
            // half that the old behaviour lost, and it is what a badly behaved client
            // could turn into held connections.
            Assert.IsTrue(socket.SendAndReceive("NOOP\r\n").StartsWith("250"),
               "The session must still be usable after the refused STARTTLS.");
         }
      }
   }
}
