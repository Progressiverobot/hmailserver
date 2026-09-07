// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    RFC 4978, COMPRESS=DEFLATE. After "COMPRESS DEFLATE" and its OK, both directions
   ///    of the connection are raw DEFLATE: the server inflates commands and literals,
   ///    deflates responses with a flush at the end of each, and the protocol above is
   ///    unchanged.
   /// </summary>
   [TestFixture]
   public class Compress : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public void CreateAccount()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "compress@example.test", "test");
      }

      [Test]
      public void TheCapabilityIsAdvertisedUntilCompressionIsOn()
      {
         using (var client = new DeflateImapClient())
         {
            StringAssert.Contains("COMPRESS=DEFLATE", client.Command("A1 CAPABILITY"), "Offered on a plain connection.");
            client.Login(_account.Address, "test");
            client.Compress();
            string capability = client.Command("A3 CAPABILITY");
            StringAssert.Contains("* CAPABILITY IMAP4", capability, "The response arrived inflated.");
            StringAssert.DoesNotContain("COMPRESS=DEFLATE", capability, "RFC 4978: not a capability of a connection that already compresses.");
         }
      }

      [Test]
      public void CommandsAndResponsesRunCompressedAfterTheOk()
      {
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "compressed subject", "compressed body line one\r\nline two\r\n");
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         using (var client = new DeflateImapClient())
         {
            client.Login(_account.Address, "test");
            client.Compress();

            string select = client.Command("A3 SELECT INBOX");
            StringAssert.Contains("* 1 EXISTS", select);
            StringAssert.Contains("A3 OK", select);

            string fetch = client.Command("A4 FETCH 1 (FLAGS BODY[HEADER.FIELDS (SUBJECT)])");
            StringAssert.Contains("compressed subject", fetch, "The subject header came back inflated.");
            StringAssert.Contains("A4 OK", fetch);

            StringAssert.Contains("A5 OK", client.Command("A5 NOOP"));

            string logout = client.Command("A6 LOGOUT");
            StringAssert.Contains("* BYE", logout);
            StringAssert.Contains("A6 OK", logout);
         }
      }

      [Test]
      public void ALargeResponseRoundTripsInflated()
      {
         // A body well past one DEFLATE block and one socket segment, with markers at
         // both ends, so a lost or duplicated block shows.
         var body = new StringBuilder("BEGIN-MARKER\r\n");
         var random = new Random(2026);
         for (int i = 0; i < 4000; i++)
         {
            var line = new char[60];
            for (int j = 0; j < line.Length; j++)
               line[j] = (char) ('a' + random.Next(26));
            body.Append(new string(line)).Append("\r\n");
         }
         body.Append("END-MARKER\r\n");
         string text = body.ToString();
         SmtpClientSimulator.StaticSend("sender@example.test", _account.Address, "large", text);
         Pop3ClientSimulator.AssertMessageCount(_account.Address, "test", 1);

         using (var client = new DeflateImapClient())
         {
            client.Login(_account.Address, "test");
            client.Compress();
            StringAssert.Contains("A3 OK", client.Command("A3 SELECT INBOX"));

            string fetch = client.Command("A4 FETCH 1 BODY[TEXT]");
            StringAssert.Contains("A4 OK", fetch);
            StringAssert.Contains("BEGIN-MARKER", fetch);
            StringAssert.Contains("END-MARKER", fetch);
            int begin = fetch.IndexOf("BEGIN-MARKER", StringComparison.Ordinal);
            int end = fetch.IndexOf("END-MARKER\r\n", StringComparison.Ordinal) + "END-MARKER\r\n".Length;
            Assert.AreEqual(text, fetch.Substring(begin, end - begin), "The body came back byte for byte through the inflater.");
         }
      }

      [Test]
      public void AnAppendLiteralIsInflatedOnTheWayIn()
      {
         string message = "From: sender@example.com\r\nTo: compress@example.test\r\nSubject: appended under compression\r\n\r\nThe literal travelled as DEFLATE.\r\n";

         using (var client = new DeflateImapClient())
         {
            client.Login(_account.Address, "test");
            client.Compress();

            string continuation = client.Command("A3 APPEND INBOX {" + message.Length + "}", "+");
            StringAssert.StartsWith("+", continuation, "The server asks for the literal.");
            string appended = client.Command(message, "A3");
            StringAssert.Contains("A3 OK", appended);

            StringAssert.Contains("* 1 EXISTS", client.Command("A4 SELECT INBOX"));
            string fetch = client.Command("A5 FETCH 1 BODY[]");
            StringAssert.Contains("Subject: appended under compression", fetch);
            StringAssert.Contains("The literal travelled as DEFLATE.", fetch);
            StringAssert.Contains("A5 OK", fetch);
         }
      }

      [Test]
      public void ASecondCompressIsRefusedWithCompressionActive()
      {
         using (var client = new DeflateImapClient())
         {
            client.Login(_account.Address, "test");
            client.Compress();
            string again = client.Command("A3 COMPRESS DEFLATE");
            StringAssert.Contains("A3 NO [COMPRESSIONACTIVE]", again);
            StringAssert.Contains("A4 OK", client.Command("A4 NOOP"), "And the connection carries on, compressed.");
         }
      }

      [Test]
      public void AnAlgorithmOtherThanDeflateIsBad()
      {
         using (var client = new DeflateImapClient())
         {
            client.Login(_account.Address, "test");
            StringAssert.Contains("A2 BAD", client.Command("A2 COMPRESS LZMA"));
            StringAssert.Contains("A3 BAD", client.Command("A3 COMPRESS"));
            StringAssert.Contains("A4 OK", client.Command("A4 NOOP"), "Still plain, still working.");
         }
      }

      [Test]
      public void CompressWorksBeforeLoginToo()
      {
         // RFC 4978 allows COMPRESS in any state.
         using (var client = new DeflateImapClient())
         {
            client.Compress("A1");
            StringAssert.Contains("A2 OK", client.Command("A2 LOGIN " + _account.Address + " test"));
            StringAssert.Contains("A3 OK", client.Command("A3 SELECT INBOX"));
         }
      }

      [Test]
      public void StartTlsIsRefusedOnceCompressed()
      {
         using (var client = new DeflateImapClient())
         {
            if (!client.Command("A1 CAPABILITY").Contains("STARTTLS"))
               Assert.Ignore("STARTTLS is not offered on the plain IMAP port in this environment.");
            client.Compress("A2");
            StringAssert.Contains("A3 BAD", client.Command("A3 STARTTLS"), "RFC 4978 section 3: no TLS under compression.");
         }
      }

      [Test]
      public void CompressionCanBeSwitchedOff()
      {
         IniFileSetting.Write("IMAPCompressionEnabled", "0");
         _application.Reinitialize();
         try
         {
            using (var client = new DeflateImapClient())
            {
               StringAssert.DoesNotContain("COMPRESS=DEFLATE", client.Command("A1 CAPABILITY"));
               StringAssert.Contains("A2 BAD", client.Command("A2 COMPRESS DEFLATE"));
            }
         }
         finally
         {
            IniFileSetting.Write("IMAPCompressionEnabled", "1");
            _application.Reinitialize();
         }
      }

      /// <summary>
      ///    An IMAP client on port 143 that can switch to DEFLATE. Plain reads come off
      ///    the socket directly; once compression is on, a background thread accumulates
      ///    the raw compressed bytes and every read re-inflates the whole run from the
      ///    start with a fresh DeflateStream over a MemoryStream. That is deliberate:
      ///    .NET Framework's DeflateStream never returns output at a sync-flush boundary
      ///    on a live socket - it reads on for the next block and blocks - but it does
      ///    return everything when the underlying stream ends, and replaying from byte
      ///    zero rebuilds the LZ77 window the server's persistent deflater relies on
      ///    across flushes. O(n^2), which is nothing for a test. Writes are stored
      ///    DEFLATE blocks (RFC 1951 section 3.2.4: valid, uncompressed).
      /// </summary>
      private sealed class DeflateImapClient : IDisposable
      {
         private readonly TcpClient _client = new TcpClient();
         private readonly NetworkStream _network;
         private volatile bool _compressed;
         private volatile bool _closed;
         private volatile Exception _readerError;
         private Thread _readerThread;
         private readonly object _lock = new object();
         private readonly List<byte> _rawCompressed = new List<byte>();
         private int _consumed;   // decoded characters already returned by ReadUntil

         public DeflateImapClient()
         {
            _client.ReceiveTimeout = 30000;
            _client.SendTimeout = 30000;
            _client.Connect("127.0.0.1", 143);
            _network = _client.GetStream();
            StringAssert.Contains("* OK", ReadUntil("* OK"));
         }

         public void Login(string address, string password)
         {
            StringAssert.Contains("A2 OK", Command("A2 LOGIN " + address + " " + password));
         }

         public void Compress(string tag = "A2")
         {
            // The OK is the last plain line; only after it is read does the raw-byte
            // collector start, so no compressed byte is ever read as plain.
            StringAssert.Contains(tag + " OK DEFLATE active", Command(tag + " COMPRESS DEFLATE"));
            _compressed = true;
            _readerThread = new Thread(CollectRaw) {IsBackground = true, Name = "DeflateImapClient"};
            _readerThread.Start();
         }

         private void CollectRaw()
         {
            var buffer = new byte[16384];
            try
            {
               while (!_closed)
               {
                  int read = _network.Read(buffer, 0, buffer.Length);
                  if (read <= 0)
                     break;
                  lock (_lock)
                     for (int i = 0; i < read; i++)
                        _rawCompressed.Add(buffer[i]);
               }
            }
            catch (Exception ex)
            {
               if (!_closed)
                  _readerError = ex;
            }
         }

         // The whole compressed run inflated from the start, as an ISO-8859-1 string.
         private string InflateAll()
         {
            byte[] raw;
            lock (_lock)
               raw = _rawCompressed.ToArray();
            if (raw.Length == 0)
               return "";
            using (var input = new MemoryStream(raw))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
               deflate.CopyTo(output);
               return Encoding.GetEncoding("ISO-8859-1").GetString(output.ToArray());
            }
         }

         // Sends a line and returns everything up to and including the line that
         // starts with the tag (the command's own tag unless another is given).
         public string Command(string line, string tag = null)
         {
            tag = tag ?? line.Substring(0, line.IndexOf(' '));
            Send(line + "\r\n");
            return ReadUntil("\r\n" + tag + " ", tag + " ");
         }

         private void Send(string text)
         {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            if (!_compressed)
            {
               _network.Write(bytes, 0, bytes.Length);
               return;
            }
            byte[] stored = StoredDeflate(bytes);
            _network.Write(stored, 0, stored.Length);
         }

         private string ReadUntil(params string[] markers)
         {
            var plainBuffer = new byte[16384];
            var plain = new StringBuilder();
            DateTime deadline = DateTime.Now.AddSeconds(30);
            while (true)
            {
               // What has arrived since the last line was returned: the tail of the
               // inflated run when compressed, the plain buffer before that.
               string available = _compressed ? InflateAll().Substring(_consumed) : plain.ToString();

               foreach (string marker in markers)
               {
                  int at = available.StartsWith(marker, StringComparison.Ordinal) ? 0 : available.IndexOf(marker, StringComparison.Ordinal);
                  if (at < 0)
                     continue;
                  int lineEnd = available.IndexOf("\r\n", at + marker.Length, StringComparison.Ordinal);
                  if (lineEnd < 0)
                     continue;
                  string result = available.Substring(0, lineEnd + 2);
                  if (_compressed)
                     _consumed += lineEnd + 2;
                  else
                     plain.Remove(0, lineEnd + 2);
                  return result;
               }

               if (_readerError != null)
                  Assert.Fail("The reader failed: " + _readerError);
               if (DateTime.Now > deadline)
                  Assert.Fail("No line with " + string.Join(" or ", markers) + " within 30 seconds. Received so far:\n" + available);

               if (_compressed)
               {
                  Thread.Sleep(20);
                  continue;
               }

               int n = _network.Read(plainBuffer, 0, plainBuffer.Length);
               if (n <= 0)
                  Assert.Fail("The server closed the connection. Received so far:\n" + plain);
               plain.Append(Encoding.GetEncoding("ISO-8859-1").GetString(plainBuffer, 0, n));
            }
         }

         // RFC 1951 stored blocks: a zero header byte (not final, type 00), the length
         // and its complement little-endian, the bytes. An empty payload still emits
         // one empty block, which is a valid flush.
         private static byte[] StoredDeflate(byte[] data)
         {
            using (var stream = new MemoryStream())
            {
               int offset = 0;
               do
               {
                  int length = Math.Min(65535, data.Length - offset);
                  int complement = ~length & 0xFFFF;
                  stream.WriteByte(0x00);
                  stream.WriteByte((byte) (length & 0xFF));
                  stream.WriteByte((byte) (length >> 8));
                  stream.WriteByte((byte) (complement & 0xFF));
                  stream.WriteByte((byte) (complement >> 8));
                  if (length > 0)
                     stream.Write(data, offset, length);
                  offset += length;
               } while (offset < data.Length);
               return stream.ToArray();
            }
         }

         public void Dispose()
         {
            _closed = true;
            try
            {
               _client.Close();
            }
            catch (Exception)
            {
            }
            if (_readerThread != null)
               _readerThread.Join(2000);
         }
      }
   }
}
