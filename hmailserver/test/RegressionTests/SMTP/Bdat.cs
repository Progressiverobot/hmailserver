// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// B4 CHUNKING/BDAT (RFC 3030). Message data may be submitted as one or more
   /// byte-counted BDAT chunks instead of a DATA dot-terminated stream. Each "BDAT
   /// chunk-size [LAST]" command is followed by exactly chunk-size octets of message
   /// content, transmitted verbatim (no dot-stuffing, no "&lt;CRLF&gt;.&lt;CRLF&gt;" terminator).
   /// The chunks of a transaction are concatenated into a single message; "LAST"
   /// completes it. CHUNKING is advertised in the EHLO response.
   /// </summary>
   [TestFixture]
   public class Bdat : TestFixtureBase
   {
      private static int ByteLen(string s)
      {
         return Encoding.ASCII.GetByteCount(s);
      }

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

      private static void StartTransaction(TcpConnection socket, string from, string to)
      {
         Assert.IsTrue(socket.SendAndReceive("MAIL FROM:<" + from + ">\r\n").StartsWith("250"));
         Assert.IsTrue(socket.SendAndReceive("RCPT TO:<" + to + ">\r\n").StartsWith("250"));
      }

      [Test]
      [Description("The EHLO response advertises the CHUNKING extension (RFC 3030).")]
      public void TestEhloAdvertisesChunking()
      {
         using (var socket = ConnectAndEhlo())
         {
            socket.Send("EHLO example.test\r\n");
            string ehlo = socket.ReadUntil("250 HELP");
            Assert.IsTrue(ehlo.Contains("CHUNKING"),
               "EHLO response did not advertise CHUNKING. Got: " + ehlo);
            socket.Send("QUIT\r\n");
         }
      }

      [Test]
      [Description("A message submitted as two BDAT chunks is delivered as the verbatim " +
                   "concatenation of the chunk payloads.")]
      public void TestTwoChunkDelivery()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bdat@example.test", "test");

         using (var socket = ConnectAndEhlo())
         {
            StartTransaction(socket, "sender@external.example", "bdat@example.test");

            string chunk1 = "Subject: BDAT two-chunk\r\n" +
                            "From: <sender@external.example>\r\n" +
                            "To: <bdat@example.test>\r\n" +
                            "\r\n" +
                            "Hello ";
            string chunk2 = "World marker-XYZ-737\r\n";

            socket.Send("BDAT " + ByteLen(chunk1) + "\r\n");
            socket.Send(chunk1);
            Assert.IsTrue(socket.Receive().StartsWith("250"), "First BDAT chunk was not accepted.");

            socket.Send("BDAT " + ByteLen(chunk2) + " LAST\r\n");
            socket.Send(chunk2);
            Assert.IsTrue(socket.Receive().StartsWith("250"), "Final BDAT chunk was not accepted.");

            socket.Send("QUIT\r\n");
         }

         string message = Pop3ClientSimulator.AssertGetFirstMessageText("bdat@example.test", "test");
         Assert.IsTrue(message.Contains("Hello World marker-XYZ-737"),
            "The delivered message body was not the concatenation of the BDAT chunks. Got: " + message);
      }

      [Test]
      [Description("BDAT is byte-transparent: a body line that is a single \".\" does not " +
                   "terminate the message (unlike DATA), so content after it is preserved.")]
      public void TestByteTransparencyPreservesDotLines()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bdat@example.test", "test");

         using (var socket = ConnectAndEhlo())
         {
            StartTransaction(socket, "sender@external.example", "bdat@example.test");

            string message = "Subject: BDAT transparency\r\n" +
                             "From: <sender@external.example>\r\n" +
                             "To: <bdat@example.test>\r\n" +
                             "\r\n" +
                             "BEFOREDOT-marker-alpha\r\n" +
                             ".\r\n" +
                             "AFTERDOT-marker-omega\r\n";

            socket.Send("BDAT " + ByteLen(message) + " LAST\r\n");
            socket.Send(message);
            Assert.IsTrue(socket.Receive().StartsWith("250"), "BDAT LAST chunk was not accepted.");

            socket.Send("QUIT\r\n");
         }

         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText("bdat@example.test", "test");
         Assert.IsTrue(delivered.Contains("BEFOREDOT-marker-alpha"),
            "Content before the dot line was lost. Got: " + delivered);
         Assert.IsTrue(delivered.Contains("AFTERDOT-marker-omega"),
            "A single-dot line wrongly terminated the BDAT message (DATA semantics applied). Got: " + delivered);
      }

      [Test]
      [Description("Once BDAT has been used in a transaction, a DATA command is rejected (RFC 3030).")]
      public void TestDataAfterBdatRejected()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bdat@example.test", "test");

         using (var socket = ConnectAndEhlo())
         {
            StartTransaction(socket, "sender@external.example", "bdat@example.test");

            const string chunk = "Hello";
            socket.Send("BDAT " + ByteLen(chunk) + "\r\n");
            socket.Send(chunk);
            Assert.IsTrue(socket.Receive().StartsWith("250"), "BDAT chunk was not accepted.");

            string dataResponse = socket.SendAndReceive("DATA\r\n");
            Assert.IsTrue(dataResponse.StartsWith("503"),
               "DATA after BDAT should be rejected with 503. Got: " + dataResponse);

            socket.Send("QUIT\r\n");
         }
      }

      [Test]
      [Description("A BDAT command with a non-numeric chunk-size is rejected with a 501 syntax error.")]
      public void TestInvalidBdatSizeRejected()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bdat@example.test", "test");

         using (var socket = ConnectAndEhlo())
         {
            StartTransaction(socket, "sender@external.example", "bdat@example.test");

            string response = socket.SendAndReceive("BDAT notanumber\r\n");
            Assert.IsTrue(response.StartsWith("501"),
               "An invalid BDAT chunk-size should be rejected with 501. Got: " + response);

            socket.Send("QUIT\r\n");
         }
      }
   }
}
