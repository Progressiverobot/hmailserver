// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// BINARYMIME (RFC 3030). MAIL FROM may declare BODY=BINARYMIME, meaning the
   /// message content is raw binary: bare CR, bare LF and NUL octets are all legal
   /// in it. Such a message MUST be transmitted with BDAT (byte-counted,
   /// byte-transparent); a DATA command in the transaction is answered 503.
   ///
   /// Scope, stated plainly: this server accepts BINARYMIME for LOCAL delivery
   /// only. Its delivery client transmits via DATA, which binary content does not
   /// survive, and down-conversion is not implemented - so a recipient requiring
   /// onward relay is refused at RCPT TO with 554 5.6.3 (conversion required but
   /// not supported), and an indirect route outward (e.g. a distribution list
   /// with an external member) is refused at delivery time with the same code as
   /// a DSN.
   ///
   /// Retrieval choice for the fidelity tests: IMAP FETCH BODY[], not POP3 RETR.
   /// POP3 is line-oriented (dot-stuffed, terminated by CRLF.CRLF), so it cannot
   /// prove byte fidelity of content whose whole point is bare CR/LF; an IMAP
   /// BODY[] literal is a byte-counted copy of the stored file.
   ///
   /// Every BDAT chunk size and IMAP literal size in this fixture is computed
   /// from the actual byte arrays - a hand-counted length that is off by one
   /// hangs the session (the server waits for octets that never come) instead of
   /// failing an assertion.
   /// </summary>
   [TestFixture]
   public class BinaryMime : TestFixtureBase
   {
      /// <summary>
      /// A byte-level TCP client. The shared TcpConnection helper encodes sends
      /// as UTF-8 and decodes receives as ASCII, either of which silently rewrites
      /// bytes >= 0x80 - unusable for content whose exact bytes are the assertion.
      /// </summary>
      private sealed class RawSocketClient : IDisposable
      {
         private readonly TcpClient _client;
         private readonly NetworkStream _stream;

         public RawSocketClient(int port)
         {
            _client = new TcpClient(AddressFamily.InterNetwork);
            _client.Connect(IPAddress.Parse("127.0.0.1"), port);
            _stream = _client.GetStream();
            _stream.ReadTimeout = 30000;
            _stream.WriteTimeout = 30000;
         }

         public void SendAscii(string text)
         {
            SendBytes(Encoding.ASCII.GetBytes(text));
         }

         public void SendBytes(byte[] bytes)
         {
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
         }

         /// <summary>
         /// Reads one line, up to and including LF. Bytes are widened 1:1 into
         /// chars (Latin-1 style), never decoded, so nothing is rewritten.
         /// </summary>
         public string ReadLine()
         {
            var sb = new StringBuilder();

            while (true)
            {
               int b = _stream.ReadByte();

               if (b == -1)
               {
                  if (sb.Length == 0)
                     throw new IOException("The connection was closed while waiting for a line.");
                  return sb.ToString();
               }

               sb.Append((char) b);

               if (b == '\n')
                  return sb.ToString();
            }
         }

         /// <summary>
         /// Reads a complete SMTP reply: lines until one carrying the final-line
         /// form "NNN text". Returns all lines concatenated.
         /// </summary>
         public string ReadSmtpResponse()
         {
            var sb = new StringBuilder();

            while (true)
            {
               string line = ReadLine();
               sb.Append(line);

               if (line.Length >= 4 &&
                   char.IsDigit(line[0]) && char.IsDigit(line[1]) && char.IsDigit(line[2]) &&
                   line[3] == ' ')
                  return sb.ToString();
            }
         }

         /// <summary>
         /// Reads until the line tagged with the given IMAP tag, returning
         /// everything read (the tagged line last).
         /// </summary>
         public string ReadUntilTagged(string tag)
         {
            var sb = new StringBuilder();
            string prefix = tag + " ";

            while (true)
            {
               string line = ReadLine();
               sb.Append(line);

               if (line.StartsWith(prefix))
                  return sb.ToString();
            }
         }

         public byte[] ReadExact(int count)
         {
            var buffer = new byte[count];
            int offset = 0;

            while (offset < count)
            {
               int read = _stream.Read(buffer, offset, count - offset);

               if (read <= 0)
                  throw new IOException("The connection was closed after " + offset + " of " + count + " expected bytes.");

               offset += read;
            }

            return buffer;
         }

         public void Dispose()
         {
            try
            {
               _stream.Dispose();
            }
            catch
            {
            }

            _client.Close();
         }
      }

      private static byte[] Ascii(string s)
      {
         return Encoding.ASCII.GetBytes(s);
      }

      /// <summary>
      /// Genuinely binary message content: a NUL octet, a bare CR, a bare LF,
      /// every octet value 0x00-0xFF, and an embedded CRLF.CRLF - the DATA
      /// end-of-message sequence, which BDAT must carry as ordinary content.
      /// </summary>
      private static byte[] BuildBinaryMessage()
      {
         var stream = new MemoryStream();

         byte[] headers = Ascii(
            "Subject: BINARYMIME fidelity\r\n" +
            "From: <sender@external.example>\r\n" +
            "To: <binarymime-user@example.test>\r\n" +
            "\r\n");
         stream.Write(headers, 0, headers.Length);

         byte[] marker1 = Ascii("BINARY-START\r\n");
         stream.Write(marker1, 0, marker1.Length);

         stream.WriteByte(0x00); // NUL

         byte[] afterNul = Ascii("after-nul ");
         stream.Write(afterNul, 0, afterNul.Length);

         stream.WriteByte(0x0D); // bare CR: the next byte is not LF

         byte[] afterCr = Ascii("after-bare-cr ");
         stream.Write(afterCr, 0, afterCr.Length);

         stream.WriteByte(0x0A); // bare LF: the previous byte is not CR

         byte[] afterLf = Ascii("after-bare-lf\r\n");
         stream.Write(afterLf, 0, afterLf.Length);

         for (int value = 0; value <= 255; value++)
            stream.WriteByte((byte) value);

         // The DATA terminator, mid-message. In a BDAT transmission this is four
         // content octets like any others.
         byte[] dotSequence = Ascii("\r\n.\r\n");
         stream.Write(dotSequence, 0, dotSequence.Length);

         byte[] endMarker = Ascii("BINARY-END-MARKER\r\n");
         stream.Write(endMarker, 0, endMarker.Length);

         return stream.ToArray();
      }

      private static RawSocketClient ConnectAndEhloRaw()
      {
         var smtp = new RawSocketClient(25);
         Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("220"), "No SMTP greeting.");
         smtp.SendAscii("EHLO example.test\r\n");
         string ehlo = smtp.ReadSmtpResponse();
         Assert.IsTrue(ehlo.Contains("250"), "EHLO was not accepted. Got: " + ehlo);
         return smtp;
      }

      private static void SendBdatChunk(RawSocketClient smtp, byte[] chunk, bool last)
      {
         // The size is computed from the array being sent - never hand-counted.
         smtp.SendAscii("BDAT " + chunk.Length + (last ? " LAST" : "") + "\r\n");
         smtp.SendBytes(chunk);

         string response = smtp.ReadSmtpResponse();
         Assert.IsTrue(response.StartsWith("250"),
            "The BDAT chunk (" + chunk.Length + " octets" + (last ? ", LAST" : "") + ") was not accepted. Got: " + response);
      }

      /// <summary>
      /// Retrieves the first INBOX message as raw bytes via an IMAP BODY[]
      /// literal - the retrieval that can prove byte fidelity (see the fixture
      /// comment for why POP3 cannot).
      /// </summary>
      private static byte[] FetchFirstMessageRawViaImap(string account, string password)
      {
         using (var imap = new RawSocketClient(143))
         {
            imap.ReadLine(); // "* OK ..." greeting

            imap.SendAscii("A1 LOGIN " + account + " " + password + "\r\n");
            string login = imap.ReadUntilTagged("A1");
            Assert.IsTrue(login.Contains("A1 OK"), "IMAP login failed: " + login);

            imap.SendAscii("A2 SELECT INBOX\r\n");
            string select = imap.ReadUntilTagged("A2");
            Assert.IsTrue(select.Contains("A2 OK"), "SELECT INBOX failed: " + select);

            imap.SendAscii("A3 FETCH 1 (BODY.PEEK[])\r\n");

            // The FETCH data line ends with the literal announcement "{N}".
            string fetchLine;
            while (true)
            {
               fetchLine = imap.ReadLine();

               if (fetchLine.StartsWith("A3 "))
                  Assert.Fail("FETCH returned no literal: " + fetchLine);

               if (fetchLine.Contains("{"))
                  break;
            }

            Match literalSize = Regex.Match(fetchLine, @"\{(\d+)\}");
            Assert.IsTrue(literalSize.Success, "Could not parse the literal size from: " + fetchLine);

            byte[] literal = imap.ReadExact(int.Parse(literalSize.Groups[1].Value));

            imap.ReadUntilTagged("A3");
            imap.SendAscii("A4 LOGOUT\r\n");

            return literal;
         }
      }

      private static void AssertEndsWithBytes(byte[] whole, byte[] expectedSuffix, string description)
      {
         Assert.GreaterOrEqual(whole.Length, expectedSuffix.Length,
            description + ": the delivered message (" + whole.Length + " bytes) is shorter than the sent content (" +
            expectedSuffix.Length + " bytes).");

         int offset = whole.Length - expectedSuffix.Length;

         for (int i = 0; i < expectedSuffix.Length; i++)
         {
            if (whole[offset + i] != expectedSuffix[i])
            {
               Assert.Fail(string.Format(
                  "{0}: byte {1} of the sent content differs. Sent 0x{2:X2}, delivered 0x{3:X2}.",
                  description, i, expectedSuffix[i], whole[offset + i]));
            }
         }
      }

      [Test]
      [Description("The EHLO response advertises BINARYMIME, and CHUNKING with it (RFC 3030 requires the pair).")]
      public void TestEhloAdvertisesBinaryMimeAndChunking()
      {
         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));

         try
         {
            Assert.IsTrue(socket.Receive().StartsWith("220"));

            socket.Send("EHLO example.test\r\n");
            string ehlo = socket.ReadUntil("250 HELP");

            Assert.IsTrue(ehlo.Contains("BINARYMIME"),
               "EHLO response did not advertise BINARYMIME. Got: " + ehlo);
            Assert.IsTrue(ehlo.Contains("CHUNKING"),
               "EHLO advertises BINARYMIME without CHUNKING, which RFC 3030 forbids. Got: " + ehlo);

            socket.Send("QUIT\r\n");
         }
         finally
         {
            socket.Disconnect();
         }
      }

      [Test]
      [Description("BODY=BINARYMIME is accepted, and a message holding NUL, bare CR, bare LF, all 256 octet " +
                   "values and an embedded CRLF.CRLF is delivered to a local mailbox byte-for-byte (verified " +
                   "via an IMAP BODY[] literal).")]
      public void TestBinaryMessageDeliveredByteForByteViaBdat()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "binarymime-user@example.test", "test");

         byte[] message = BuildBinaryMessage();

         using (var smtp = ConnectAndEhloRaw())
         {
            smtp.SendAscii("MAIL FROM:<sender@external.example> BODY=BINARYMIME\r\n");
            string mailFrom = smtp.ReadSmtpResponse();
            Assert.IsTrue(mailFrom.StartsWith("250"), "BODY=BINARYMIME was not accepted: " + mailFrom);

            smtp.SendAscii("RCPT TO:<binarymime-user@example.test>\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "The local recipient was not accepted.");

            // Two chunks, split inside the binary body, both sizes computed.
            int splitAt = message.Length / 2;
            var chunk1 = new byte[splitAt];
            var chunk2 = new byte[message.Length - splitAt];
            Array.Copy(message, 0, chunk1, 0, chunk1.Length);
            Array.Copy(message, splitAt, chunk2, 0, chunk2.Length);

            SendBdatChunk(smtp, chunk1, false);
            SendBdatChunk(smtp, chunk2, true);

            smtp.SendAscii("QUIT\r\n");
         }

         // Wait for local delivery to complete (POP3 STAT only - no content is
         // retrieved over the line-oriented protocol).
         Pop3ClientSimulator.AssertMessageCount("binarymime-user@example.test", "test", 1);

         byte[] delivered = FetchFirstMessageRawViaImap("binarymime-user@example.test", "test");

         // The server prepends its trace headers (Received etc.); everything sent
         // must follow them unchanged, down to the last octet.
         string deliveredStart = Encoding.ASCII.GetString(delivered, 0, Math.Min(delivered.Length, 200));
         // Local delivery writes Return-Path first, then Delivered-To, then
         // Received - this test first shipped expecting Received: at the top and
         // failed against a correctly delivered message. What matters here is
         // only that the server's textual trace block leads and the binary
         // payload after it is untouched; which trace header comes first is the
         // delivery code's business.
         Assert.IsTrue(deliveredStart.StartsWith("Return-Path:") || deliveredStart.StartsWith("Received:"),
            "The delivered message does not begin with the server's trace headers. Got: " + deliveredStart);

         AssertEndsWithBytes(delivered, message, "BINARYMIME fidelity");
      }

      [Test]
      [Description("A DATA command in a BODY=BINARYMIME transaction is refused with 503 (RFC 3030 section 3), " +
                   "and the transaction remains usable with BDAT.")]
      public void TestDataAfterBinaryMimeRefusedWith503()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "binarymime-user@example.test", "test");

         using (var smtp = ConnectAndEhloRaw())
         {
            smtp.SendAscii("MAIL FROM:<sender@external.example> BODY=BINARYMIME\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "BODY=BINARYMIME was not accepted.");

            smtp.SendAscii("RCPT TO:<binarymime-user@example.test>\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "The local recipient was not accepted.");

            smtp.SendAscii("DATA\r\n");
            string dataResponse = smtp.ReadSmtpResponse();
            Assert.IsTrue(dataResponse.StartsWith("503"),
               "DATA in a BINARYMIME transaction must be answered 503 (RFC 3030). Got: " + dataResponse);

            // The refusal must not have wrecked the transaction: BDAT completes it.
            byte[] message = Ascii(
               "Subject: after refused DATA\r\n" +
               "From: <sender@external.example>\r\n" +
               "To: <binarymime-user@example.test>\r\n" +
               "\r\n" +
               "marker-after-503-Q9\r\n");
            SendBdatChunk(smtp, message, true);

            smtp.SendAscii("QUIT\r\n");
         }

         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText("binarymime-user@example.test", "test");
         Assert.IsTrue(delivered.Contains("marker-after-503-Q9"),
            "The transaction did not survive the refused DATA command. Got: " + delivered);
      }

      [Test]
      [Description("A recipient requiring onward relay is refused at RCPT TO with 554 5.6.3 when the " +
                   "transaction declared BODY=BINARYMIME - and accepted without the declaration (the control).")]
      public void TestRelayRecipientRefusedAtRcptWith554_5_6_3()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "binarymime-sender@example.test", "test");

         using (var smtp = ConnectAndEhloRaw())
         {
            // Control first: the same relay recipient is acceptable when the
            // message is not declared binary. Nothing is queued - the
            // transaction is abandoned with RSET before any content is sent.
            smtp.SendAscii("MAIL FROM:<binarymime-sender@example.test>\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "The control MAIL FROM was not accepted.");

            smtp.SendAscii("RCPT TO:<someone@external-relay.example>\r\n");
            string controlRcpt = smtp.ReadSmtpResponse();
            Assert.IsTrue(controlRcpt.StartsWith("250"),
               "The control (non-binary) relay recipient was refused, so the test cannot prove the refusal is " +
               "specific to BINARYMIME. Got: " + controlRcpt);

            smtp.SendAscii("RSET\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"));

            // The same recipient with BODY=BINARYMIME: refused, synchronously,
            // with the RFC's own permanent code.
            smtp.SendAscii("MAIL FROM:<binarymime-sender@example.test> BODY=BINARYMIME\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "BODY=BINARYMIME was not accepted.");

            smtp.SendAscii("RCPT TO:<someone@external-relay.example>\r\n");
            string binaryRcpt = smtp.ReadSmtpResponse();

            Assert.IsTrue(binaryRcpt.StartsWith("554"),
               "A relay recipient in a BINARYMIME transaction must be refused with 554. Got: " + binaryRcpt);
            Assert.IsTrue(binaryRcpt.Contains("5.6.3"),
               "The refusal must carry the RFC 3463 enhanced code 5.6.3 (conversion required but not " +
               "supported). Got: " + binaryRcpt);

            smtp.SendAscii("QUIT\r\n");
         }

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      [Test]
      [Description("A BINARYMIME message accepted for a local distribution list with an external member is " +
                   "refused at delivery time - no MAIL FROM reaches the remote, and the sender receives a DSN " +
                   "with status 5.6.3.")]
      public void TestDistributionListExternalMemberBouncesWith554_5_6_3()
      {
         Account sender =
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "binarymime-sender@example.test", "test");

         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "binarymime-list@example.test",
            new List<string> {"list-member@dummy-example.com"});

         var deliveryResults = new Dictionary<string, int> {["list-member@dummy-example.com"] = 250};

         int smtpServerPort = TestSetup.GetNextFreePort();
         using (var server = new SmtpServerSimulator(1, smtpServerPort))
         {
            server.AddRecipientResult(deliveryResults);
            server.StartListen();

            TestSetup.AddRoutePointingAtLocalhost(1, smtpServerPort, false);

            byte[] message = BuildBinaryMessage();

            using (var smtp = ConnectAndEhloRaw())
            {
               smtp.SendAscii("MAIL FROM:<" + sender.Address + "> BODY=BINARYMIME\r\n");
               Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "BODY=BINARYMIME was not accepted.");

               // The list address is local, so RCPT accepts it - the external
               // member only surfaces when the list is expanded.
               smtp.SendAscii("RCPT TO:<binarymime-list@example.test>\r\n");
               Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"),
                  "The local distribution-list recipient was not accepted.");

               SendBdatChunk(smtp, message, true);

               smtp.SendAscii("QUIT\r\n");
            }

            // The delivery client connects and reads EHLO, then must refuse
            // locally: no envelope, no message octets.
            server.WaitForCompletion();

            Assert.IsEmpty(server.MailFromCommand,
               "MAIL FROM was sent for a BINARYMIME message although the remote never advertised BINARYMIME " +
               "and this server cannot transmit binary content via DATA.");
            Assert.IsEmpty(server.MessageData,
               "Message octets were transmitted down a DATA path for a BINARYMIME message.");

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);

            string bounce = Pop3ClientSimulator.AssertGetFirstMessageText(sender.Address, "test");
            Assert.IsTrue(bounce.Contains("5.6.3"),
               "The DSN does not carry status 5.6.3 (conversion required but not supported): " + bounce);
            Assert.IsTrue(bounce.Contains("BINARYMIME"),
               "The DSN does not tell the sender that BINARYMIME was the reason: " + bounce);
         }
         // The relay refusal deliberately reports HM6340 to the error log so an
         // administrator learns WHY mail bounced. Consumed here, or the next
         // test's setup fails on an error log this test fully expected to fill.
         CustomAsserts.AssertReportedError("HM6340");
      }

      [Test]
      [Description("The negative control: BODY=8BITMIME neither refuses DATA nor blocks relay recipients - " +
                   "the BINARYMIME restrictions key on the BINARYMIME declaration alone.")]
      public void TestBody8BitMimeIsUnaffected()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "binarymime-user@example.test", "test");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "binarymime-sender@example.test", "test");

         using (var smtp = ConnectAndEhloRaw())
         {
            smtp.SendAscii("MAIL FROM:<binarymime-sender@example.test> BODY=8BITMIME\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "BODY=8BITMIME was not accepted.");

            // A relay recipient stays acceptable under 8BITMIME.
            smtp.SendAscii("RCPT TO:<someone@external-relay.example>\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"),
               "A relay recipient was refused in an 8BITMIME transaction.");

            smtp.SendAscii("RSET\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"));

            // And DATA stays available under 8BITMIME.
            smtp.SendAscii("MAIL FROM:<binarymime-sender@example.test> BODY=8BITMIME\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"));

            smtp.SendAscii("RCPT TO:<binarymime-user@example.test>\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"));

            smtp.SendAscii("DATA\r\n");
            string dataResponse = smtp.ReadSmtpResponse();
            Assert.IsTrue(dataResponse.StartsWith("354"),
               "DATA was refused in an 8BITMIME transaction. Got: " + dataResponse);

            smtp.SendAscii(
               "Subject: 8BITMIME control\r\n" +
               "From: <binarymime-sender@example.test>\r\n" +
               "To: <binarymime-user@example.test>\r\n" +
               "\r\n" +
               "marker-8bitmime-control-K4\r\n" +
               ".\r\n");
            Assert.IsTrue(smtp.ReadSmtpResponse().StartsWith("250"), "The 8BITMIME control message was not accepted.");

            smtp.SendAscii("QUIT\r\n");
         }

         string delivered = Pop3ClientSimulator.AssertGetFirstMessageText("binarymime-user@example.test", "test");
         Assert.IsTrue(delivered.Contains("marker-8bitmime-control-K4"),
            "The 8BITMIME control message did not deliver. Got: " + delivered);

         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }
   }
}
