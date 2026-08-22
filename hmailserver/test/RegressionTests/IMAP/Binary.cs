// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    BINARY (RFC 3516). FETCH BINARY[section] hands the client a part's
   ///    content with its Content-Transfer-Encoding already decoded - what an
   ///    attachment save has always done, applied to FETCH - and BINARY.SIZE
   ///    answers the decoded size without shipping the bytes. APPEND accepts the
   ///    literal8 form (~{n}), whose point is content that may contain NUL
   ///    octets; the binary receive path has always stored bytes as bytes.
   /// </summary>
   [TestFixture]
   public class Binary : TestFixtureBase
   {
      private Account _account;

      private const string DecodedText = "Hello binary world, decoded server-side!";

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "binary@example.test", "test");
      }

      private ImapClientSimulator LogonWithBase64Message()
      {
         string encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(DecodedText));

         string message =
            "From: binary@example.test\r\n" +
            "Subject: binary test\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"bb\"\r\n" +
            "\r\n" +
            "--bb\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "Plain part.\r\n" +
            "--bb\r\n" +
            "Content-Type: application/octet-stream\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "\r\n" +
            encoded + "\r\n" +
            "--bb--\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A01 APPEND INBOX {" + message.Length + "+}\r\n" + message + "\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response, "The test message must append. Got: " + response);

         return imapSim;
      }

      [Test]
      [Description("BINARY[2] returns the decoded bytes; BODY[2] still returns the encoded form - the contrast.")]
      public void BinaryDecodesWhereBodyDoesNot()
      {
         var imapSim = LogonWithBase64Message();

         imapSim.SendRaw("A02 FETCH 1 (BINARY.PEEK[2])\r\n");
         var binary = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("BINARY[2] ~{" + DecodedText.Length + "}", binary,
            "The literal must carry the DECODED size, announced with literal8 (~{n}) as RFC 3516 4.3 requires - decoded bytes may contain NUL, which a plain literal cannot carry. Got: " + binary);
         StringAssert.Contains(DecodedText, binary,
            "The content must arrive decoded. Got: " + binary);

         imapSim.SendRaw("A03 FETCH 1 (BODY.PEEK[2])\r\n");
         var body = imapSim.ReceiveUntil("A03 ");

         ClassicAssert.IsFalse(body.Contains(DecodedText),
            "BODY[2] must still return the base64 form - decoding is BINARY's whole point. Got: " + body);

         imapSim.Disconnect();
      }

      [Test]
      [Description("BINARY.SIZE answers the decoded size as a number, without the bytes.")]
      public void BinarySizeAnswersTheDecodedSize()
      {
         var imapSim = LogonWithBase64Message();

         imapSim.SendRaw("A02 FETCH 1 (BINARY.SIZE[2])\r\n");
         var response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("BINARY.SIZE[2] " + DecodedText.Length, response,
            "The decoded size, not the encoded one. Got: " + response);
         ClassicAssert.IsFalse(response.Contains(DecodedText),
            "SIZE must not ship the content. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("A partial range applies to the decoded bytes and echoes the origin.")]
      public void APartialRangeAppliesToDecodedBytes()
      {
         var imapSim = LogonWithBase64Message();

         imapSim.SendRaw("A02 FETCH 1 (BINARY.PEEK[2]<0.5>)\r\n");
         var response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("BINARY[2]<0> ~{5}", response,
            "Five decoded bytes from the origin, the origin echoed, in a literal8. Got: " + response);
         StringAssert.Contains("Hello", response,
            "And they are the DECODED first five. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("APPEND accepts the literal8 form ~{n}.")]
      public void AppendAcceptsLiteral8()
      {
         const string message = "Subject: literal8\r\n\r\nStored via literal8.\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A01 APPEND INBOX ~{" + message.Length + "}\r\n");
         var continuation = imapSim.ReceiveUntil("+ ");
         StringAssert.Contains("+ Ready", continuation,
            "The literal8 marker must be answered like any literal. Got: " + continuation);

         imapSim.SendRaw(message + "\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response,
            "The literal8 APPEND must complete. Got: " + response);

         imapSim.SendRaw("A02 FETCH 1 (BODY.PEEK[TEXT])\r\n");
         var fetch = imapSim.ReceiveUntil("A02 ");
         StringAssert.Contains("Stored via literal8.", fetch,
            "And the message must be intact. Got: " + fetch);

         imapSim.Disconnect();
      }

      [Test]
      [Description("BINARY[] returns the whole message, not an empty literal - the composite-section defect shipped in 6.2.22-pre2")]
      public void BinaryOnTheWholeMessageReturnsTheWholeMessage()
      {
         var imapSim = LogonWithBase64Message();

         imapSim.SendRaw("A02 FETCH 1 (BINARY.PEEK[])\r\n");
         var response = imapSim.ReceiveUntil("A02 ");

         // The defect this pins: a multipart entity's own text stops at the first
         // boundary, so serving it as "the section's content" answered with the
         // MIME preamble - nothing. A client asking for the message was told the
         // message was empty.
         ClassicAssert.IsFalse(Regex.IsMatch(response, @"BINARY\[\] ~?\{0\}"),
            "BINARY[] must not answer with an empty literal. Got: " + response);
         StringAssert.Contains("Subject: binary test", response,
            "BINARY[] is the entire message, headers included. Got: " + response);
         StringAssert.Contains("Plain part.", response,
            "...and its parts. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("BINARY on a multipart section returns that part's sub-parts rather than an empty literal")]
      public void BinaryOnAMultipartSectionReturnsItsSubParts()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         // Part 1 is itself a multipart/alternative, so BINARY[1] names a
         // composite section - the shape that used to come back empty.
         string message =
            "From: binary@example.test\r\n" +
            "Subject: nested\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed; boundary=\"outer\"\r\n" +
            "\r\n" +
            "--outer\r\n" +
            "Content-Type: multipart/alternative; boundary=\"inner\"\r\n" +
            "\r\n" +
            "--inner\r\n" +
            "Content-Type: text/plain\r\n" +
            "\r\n" +
            "The plain alternative.\r\n" +
            "--inner\r\n" +
            "Content-Type: text/html\r\n" +
            "\r\n" +
            "<p>The html alternative.</p>\r\n" +
            "--inner--\r\n" +
            "--outer--\r\n";

         imapSim.SendRaw("A01 APPEND INBOX {" + message.Length + "+}\r\n" + message + "\r\n");
         StringAssert.Contains("A01 OK", imapSim.ReceiveUntil("A01 "));

         imapSim.SendRaw("A02 SELECT INBOX\r\n");
         imapSim.ReceiveUntil("A02 ");

         imapSim.SendRaw("A03 FETCH * (BINARY.PEEK[1])\r\n");
         var response = imapSim.ReceiveUntil("A03 ");

         ClassicAssert.IsFalse(Regex.IsMatch(response, @"BINARY\[1\] ~?\{0\}"),
            "A multipart section must not answer with an empty literal. Got: " + response);
         StringAssert.Contains("The plain alternative.", response,
            "A multipart part's content is its sub-parts, raw - there is no transfer encoding to undo. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("BINARY.SIZE agrees with the octets BINARY delivers - a client sizes its buffer from it")]
      public void BinarySizeAgreesWithTheOctetsDelivered()
      {
         var imapSim = LogonWithBase64Message();

         imapSim.SendRaw("A02 FETCH 1 (BINARY.SIZE[])\r\n");
         var sizeResponse = imapSim.ReceiveUntil("A02 ");
         var size = Regex.Match(sizeResponse, @"BINARY\.SIZE\[\] (\d+)");
         ClassicAssert.IsTrue(size.Success, "No BINARY.SIZE[] in the response. Got: " + sizeResponse);

         imapSim.SendRaw("A03 FETCH 1 (BINARY.PEEK[])\r\n");
         var contentResponse = imapSim.ReceiveUntil("A03 ");
         var literal = Regex.Match(contentResponse, @"BINARY\[\] ~?\{(\d+)\}");
         ClassicAssert.IsTrue(literal.Success, "No BINARY[] literal in the response. Got: " + contentResponse);

         ClassicAssert.AreEqual(size.Groups[1].Value, literal.Groups[1].Value,
            "BINARY.SIZE and the BINARY literal must agree: a chunking client computes its last " +
            "request as size-minus-offset, so a disagreement truncates the final chunk.");

         imapSim.Disconnect();
      }

      [Test]
      [Description("CAPABILITY advertises BINARY.")]
      public void CapabilityAdvertisesBinary()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains(" BINARY", capabilities,
            "CAPABILITY must advertise BINARY. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
