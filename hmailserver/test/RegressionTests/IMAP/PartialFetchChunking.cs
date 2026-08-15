// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    Fetches a whole message the way Thunderbird and Apple Mail do - a run of
   ///    <c>BODY[]&lt;offset.length&gt;</c> requests - and asserts the reassembled
   ///    result is the message, byte for byte.
   ///
   ///    Written for issue #26, reported against 6.2.18-B20 by an operator evaluating
   ///    this fork for a ten-year production estate, and pointing at upstream #334
   ///    which was closed with no linked fix. The reported symptom is mishandling when
   ///    the FINAL chunk is shorter than the requested length - which is not an edge
   ///    case at all, it is every message whose size is not an exact multiple of the
   ///    client's chunk size.
   ///
   ///    `Fetch.PartialFetch_RequestedSizeExceedingRemainderTruncates` already covers
   ///    a single over-long request against BODY[HEADER]. This covers what the report
   ///    actually describes and that test does not: BODY[] rather than a section, and
   ///    a SEQUENCE of chunks rather than one, reassembled and compared. Those two
   ///    differences are the whole point - a server can answer any single partial
   ///    request correctly and still hand a client a corrupt message if the last one
   ///    is short, or if the announced literal and the octets sent disagree.
   ///
   ///    Every assertion is on the bytes, not on the response text. A test that
   ///    checked only that "{N}" appeared would pass on a server that announced the
   ///    right size and sent the wrong bytes, which is the failure a client actually
   ///    sees.
   ///
   ///    One limit, stated so this is not read as wider coverage than it is: the test
   ///    client decodes the wire as UTF-8, so the octet arithmetic below is only sound
   ///    for ASCII content, and the bodies here are ASCII on purpose. A character-
   ///    versus-octet defect on 8-bit content would be distorted by the simulator
   ///    before it could be observed, and needs a byte-exact client to test.
   /// </summary>
   [TestFixture]
   public class PartialFetchChunking : TestFixtureBase
   {
      private const string Password = "test";

      /// <summary>
      ///    Pulls the literal payload out of a FETCH response.
      ///
      ///    The response is "* 1 FETCH (BODY[]&lt;0&gt; {N}\r\n" then exactly N octets
      ///    then ")\r\n" and the tagged OK. N is read and used, rather than scanning
      ///    for a terminator, because the announced count IS the thing under test: if
      ///    the server announces a count it does not honour, taking it at its word is
      ///    what surfaces that.
      /// </summary>
      private static string LiteralPayload(string response)
      {
         int brace = response.IndexOf('{');

         if (brace < 0)
         {
            // No literal. That is legal for an empty partial - the server answers
            // BODY[]<n> "" - and the caller distinguishes the two.
            return string.Empty;
         }

         int close = response.IndexOf('}', brace);
         Assert.Greater(close, brace, "Malformed literal header in: " + response);

         int announced = int.Parse(response.Substring(brace + 1, close - brace - 1));

         // The octets begin after the CRLF that ends the literal header.
         int start = response.IndexOf("\r\n", close, StringComparison.Ordinal);
         Assert.Greater(start, 0, "No CRLF after the literal header in: " + response);
         start += 2;

         Assert.LessOrEqual(start + announced, response.Length,
            "The server announced {" + announced + "} octets and the response does not contain that many - the "
            + "literal count and the payload disagree, which is exactly what corrupts a chunked download. "
            + "Response was:\r\n" + response);

         return response.Substring(start, announced);
      }

      private static int AnnouncedSize(string response)
      {
         int brace = response.IndexOf('{');

         if (brace < 0)
            return 0;

         int close = response.IndexOf('}', brace);
         return int.Parse(response.Substring(brace + 1, close - brace - 1));
      }

      /// <summary>
      ///    A message large enough that a realistic client chunk size needs several
      ///    requests and does NOT divide the total exactly - which is the condition
      ///    the report is about.
      /// </summary>
      private Account DeliverLargeMessage(out string address)
      {
         address = "partialfetch@example.test";
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, Password);

         var body = new StringBuilder();
         for (int i = 0; i < 400; i++)
            body.Append("Line ").Append(i).Append(" of a message being fetched in pieces.\r\n");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Chunked fetch", body.ToString());
         ImapClientSimulator.AssertMessageCount(account.Address, Password, "Inbox", 1);

         return account;
      }

      /// <summary>
      ///    The report's case, end to end: fetch the message in fixed-size pieces and
      ///    require the concatenation to equal the whole message.
      /// </summary>
      [Test]
      [Description("Issue #26: a message fetched as a run of BODY[]<offset.length> chunks reassembles byte-for-byte, " +
                   "including when the final chunk is shorter than the requested length.")]
      public void AChunkedBodyFetchReassemblesIntoTheWholeMessage()
      {
         Account account = DeliverLargeMessage(out string address);
         Assert.IsNotNull(account);

         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(address, Password));
            Assert.IsTrue(sim.SelectFolder("INBOX"));

            string whole = LiteralPayload(sim.Fetch("1 BODY.PEEK[]"));
            Assert.Greater(whole.Length, 0, "The message body came back empty.");

            // Deliberately not a divisor of the total, so the last chunk is short -
            // which is the case the report says is mishandled and the normal case for
            // any real message.
            const int chunk = 1000;

            var reassembled = new StringBuilder();
            int offset = 0;
            int requests = 0;

            while (offset < whole.Length)
            {
               string response = sim.Fetch("1 BODY.PEEK[]<" + offset + "." + chunk + ">");
               string piece = LiteralPayload(response);

               requests++;
               Assert.Less(requests, 200, "The fetch loop is not terminating; the server is returning empty "
                                          + "chunks before the end of the message.");

               Assert.Greater(piece.Length, 0,
                  "The server returned no octets for offset " + offset + " of a " + whole.Length
                  + "-octet message, so a client walking the message would stall here. Response was:\r\n"
                  + response);

               int expected = Math.Min(chunk, whole.Length - offset);
               Assert.AreEqual(expected, piece.Length,
                  "Chunk at offset " + offset + " of a " + whole.Length + "-octet message: expected " + expected
                  + " octets" + (expected < chunk ? " (the short final chunk)" : "") + " and got " + piece.Length
                  + ".");

               reassembled.Append(piece);
               offset += piece.Length;
            }

            Assert.AreEqual(whole.Length, reassembled.Length,
               "The reassembled message is a different length from the message itself.");
            Assert.AreEqual(whole, reassembled.ToString(),
               "The reassembled message differs from the whole message. A client downloading in chunks would "
               + "store a corrupt copy.");
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    The same walk over UID FETCH, which is what the clients named in the report
      ///    actually send.
      ///
      ///    Thunderbird and Apple Mail address messages by UID, not by sequence number,
      ///    for the good reason that a sequence number changes underneath them when
      ///    anything is expunged. So a defect that lived only on the UID path would be
      ///    invisible to every sequence-number test in this suite and would still hit
      ///    every real user of those two clients - which is a large share of the people
      ///    the report is about.
      /// </summary>
      [Test]
      [Description("Issue #26: the same chunked walk over UID FETCH, which is what Thunderbird and Apple Mail " +
                   "actually send, reassembles byte-for-byte.")]
      public void AChunkedUidFetchReassemblesIntoTheWholeMessage()
      {
         Account account = DeliverLargeMessage(out string address);
         Assert.IsNotNull(account);

         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(address, Password));
            Assert.IsTrue(sim.SelectFolder("INBOX"));

            // The UID of the only message in the folder.
            string uidResponse = sim.Fetch("1 UID");
            int uidStart = uidResponse.IndexOf("UID ", StringComparison.Ordinal) + 4;
            int uidEnd = uidStart;
            while (uidEnd < uidResponse.Length && char.IsDigit(uidResponse[uidEnd]))
               uidEnd++;

            string uid = uidResponse.Substring(uidStart, uidEnd - uidStart);
            Assert.Greater(uid.Length, 0, "Could not read the message UID from: " + uidResponse);

            string whole = LiteralPayload(sim.SendSingleCommand("A20 UID FETCH " + uid + " BODY.PEEK[]"));
            Assert.Greater(whole.Length, 0, "The message body came back empty over UID FETCH.");

            const int chunk = 1000;

            var reassembled = new StringBuilder();
            int offset = 0;
            int requests = 0;

            while (offset < whole.Length)
            {
               string response = sim.SendSingleCommand(
                  "A21 UID FETCH " + uid + " BODY.PEEK[]<" + offset + "." + chunk + ">");
               string piece = LiteralPayload(response);

               requests++;
               Assert.Less(requests, 200, "The UID fetch loop is not terminating.");

               int expected = Math.Min(chunk, whole.Length - offset);
               Assert.AreEqual(expected, piece.Length,
                  "UID chunk at offset " + offset + " of a " + whole.Length + "-octet message: expected "
                  + expected + " octets and got " + piece.Length + ".");

               reassembled.Append(piece);
               offset += piece.Length;
            }

            Assert.AreEqual(whole, reassembled.ToString(),
               "The message reassembled from UID FETCH chunks differs from the whole message.");
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    The final chunk on its own, asserted directly rather than as part of a
      ///    loop, so a failure names the exact condition in the report.
      /// </summary>
      [Test]
      [Description("Issue #26: a final BODY[] chunk whose requested length overruns the end returns exactly the " +
                   "remainder, and announces exactly what it returns.")]
      public void TheFinalChunkReturnsTheRemainderAndAnnouncesItHonestly()
      {
         Account account = DeliverLargeMessage(out string address);
         Assert.IsNotNull(account);

         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(address, Password));
            Assert.IsTrue(sim.SelectFolder("INBOX"));

            string whole = LiteralPayload(sim.Fetch("1 BODY.PEEK[]"));

            // Ask for far more than is left, from a point near the end - what a client
            // does on its last request without knowing the exact remainder.
            int start = whole.Length - 137;
            string response = sim.Fetch("1 BODY.PEEK[]<" + start + ".100000>");

            Assert.AreEqual(137, AnnouncedSize(response),
               "The literal count on the final chunk is not the remainder. Response head was:\r\n"
               + response.Substring(0, Math.Min(200, response.Length)));

            string piece = LiteralPayload(response);
            Assert.AreEqual(137, piece.Length, "The final chunk is the wrong length.");
            Assert.AreEqual(whole.Substring(start), piece,
               "The final chunk is the wrong 137 octets - the offset is being applied incorrectly.");

            // RFC 3501: a partial response echoes the origin octet only, never the
            // length. A client that saw "<start.size>" echoed back would mis-key its
            // reassembly buffer.
            StringAssert.Contains("<" + start + ">", response);
            StringAssert.DoesNotContain("<" + start + ".100000>", response);
         }
         finally
         {
            sim.Disconnect();
         }
      }
   }
}
