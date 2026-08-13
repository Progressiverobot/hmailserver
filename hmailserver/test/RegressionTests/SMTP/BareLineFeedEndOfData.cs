// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Net.Sockets;
using System.Text;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    End-of-data detection when the message body uses bare LF line endings.
   ///
   ///    This fixture exists because of discussion #18, a report of inbound mail from a
   ///    Proxmox Mail Gateway (Postfix) never being received: the session logged
   ///    "354 OK, send.", the data arrived, and then nothing - no reply, no error, a spool
   ///    file of zero bytes, and Postfix eventually giving up with "timed out while
   ///    sending end of data". Two rounds of fixes for plausible-looking causes did not
   ///    touch it, which is what makes this fixture worth its length: the reporter's debug
   ///    log showed 7984 bytes arriving in sixteen reads with
   ///    "end-of-data not yet seen" on every single one, and that is not a slow server or
   ///    a busy queue. It is a terminator the server could not recognise.
   ///
   ///    The cause was the literal test for the terminator:
   ///
   ///        buffer[size-5] == '\r' &amp;&amp; buffer[size-4] == '\n' &amp;&amp; buffer[size-3] == '.' ...
   ///
   ///    which requires CRLF immediately before the dot. A body whose last line ends with
   ///    a bare LF arrives as "...text\n.\r\n", where the byte five from the end is 't'.
   ///    The only other test fires when the dot is the FIRST byte in the buffer, which
   ///    happens only when the preceding flush ended exactly on a line boundary. So with
   ///    bare-LF input the terminator was invisible, and the server waited for something
   ///    that had already arrived - for ever, since nothing in that path times out.
   ///
   ///    The server has had a setting for exactly this class of sender for years
   ///    ("Allow incorrect line endings"), honoured everywhere except the one place where
   ///    ignoring it hangs the connection instead of merely being strict.
   ///
   ///    These tests speak raw SMTP rather than using SmtpClientSimulator, because the
   ///    defect is in byte-level framing: a helper that writes well-formed CRLF data
   ///    cannot reproduce it, which is precisely why the existing suite of over a thousand
   ///    tests did not.
   /// </summary>
   [TestFixture]
   public class BareLineFeedEndOfData : TestFixtureBase
   {
      private bool originalAllowIncorrectLineEndings_;

      [SetUp]
      public new void SetUp()
      {
         originalAllowIncorrectLineEndings_ = _settings.AllowIncorrectLineEndings;
      }

      [TearDown]
      public new void TearDown()
      {
         _settings.AllowIncorrectLineEndings = originalAllowIncorrectLineEndings_;
      }

      // A deliberately literal SMTP client. Every byte written is stated in the test, and
      // the terminator is written separately from the body so that the segmentation the
      // reporter saw - body tail and terminator arriving in the same read - is what
      // actually happens on the wire.
      private static string SendRaw(string recipient, string bodyEndingAndTerminator, int replyTimeoutSeconds)
      {
         using (var client = new TcpClient())
         {
            client.Connect("localhost", 25);
            client.ReceiveTimeout = replyTimeoutSeconds * 1000;

            using (var stream = client.GetStream())
            {
               ReadReply(stream);                                   // 220

               WriteAscii(stream, "HELO test\r\n");   ReadReply(stream);
               WriteAscii(stream, "MAIL FROM:<sender@external.test>\r\n"); ReadReply(stream);
               WriteAscii(stream, "RCPT TO:<" + recipient + ">\r\n");     ReadReply(stream);
               WriteAscii(stream, "DATA\r\n");        ReadReply(stream);  // 354

               // Headers, correctly formed - the defect is in the body's last line, and
               // making everything malformed would leave it unclear which part mattered.
               WriteAscii(stream,
                  "From: sender@external.test\r\n" +
                  "To: " + recipient + "\r\n" +
                  "Subject: bare LF before the end of data\r\n" +
                  "\r\n" +
                  "First line of the body.\r\n");

               // The part under test, written in one go so the terminator lands in the
               // same read as the preceding line ending.
               WriteAscii(stream, bodyEndingAndTerminator);

               return ReadReply(stream);
            }
         }
      }

      private static void WriteAscii(NetworkStream stream, string text)
      {
         var bytes = Encoding.ASCII.GetBytes(text);
         stream.Write(bytes, 0, bytes.Length);
         stream.Flush();
      }

      // Reads one reply line. A SocketException on timeout is the failure this fixture is
      // about - a server that never answers - so it is allowed to propagate with a
      // message that says so rather than being turned into a silent false.
      private static string ReadReply(NetworkStream stream)
      {
         var reply = new StringBuilder();
         var one = new byte[1];

         try
         {
            while (true)
            {
               int read = stream.Read(one, 0, 1);
               if (read == 0) break;

               reply.Append((char) one[0]);

               if (reply.Length >= 2 && reply[reply.Length - 2] == '\r' && reply[reply.Length - 1] == '\n')
                  break;
            }
         }
         catch (Exception exception)
         {
            Assert.Fail("The server never replied (" + exception.GetType().Name + "). Received so far: \"" +
                        reply.ToString().Replace("\r", "\\r").Replace("\n", "\\n") + "\". " +
                        "This is the discussion #18 symptom: the message data has been delivered and the " +
                        "server is still waiting for a terminator that has already arrived.");
         }

         return reply.ToString().TrimEnd('\r', '\n');
      }

      [Test]
      [Description("A body whose last line ends with a bare LF is still terminated by .CRLF")]
      public void ABodyEndingWithABareLineFeedIsAccepted()
      {
         // The reported case, reduced to the smallest thing that reproduces it. Without
         // the fix this does not fail an assertion - it hangs until the read timeout,
         // which is why the timeout is short and its expiry is reported as the defect.
         _settings.AllowIncorrectLineEndings = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "barelf@example.test", "test");

         var reply = SendRaw(account.Address, "Last line ends with a bare LF\n.\r\n", 20);

         StringAssert.StartsWith("250", reply,
            "The message was not accepted: " + reply);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         // The terminator must be removed and nothing else. The old code always removed
         // three bytes, which for a two-byte ".\n" terminator would have eaten the last
         // character of the body - a silent corruption that no count-based assertion
         // would catch.
         var message = Pop3ClientSimulator.AssertGetFirstMessageText(account.Address, "test");

         StringAssert.Contains("Last line ends with a bare LF", message,
            "The body's final line was damaged while removing the end-of-data marker.");
      }

      [Test]
      [Description("The bare-LF spellings of the terminator itself are accepted when the setting allows it")]
      public void TheBareLineFeedSpellingsOfTheTerminatorAreAccepted()
      {
         // A sender with bare-LF line endings will not reliably send the standard
         // terminator either, so the three other spellings are covered. They are separate
         // cases in the code and would otherwise be one accident away from regressing.
         _settings.AllowIncorrectLineEndings = true;

         var cases = new[]
         {
            new { Name = "CRLF . LF",  Data = "Body line\r\n.\n" },
            new { Name = "LF . CRLF",  Data = "Body line\n.\r\n" },
            new { Name = "LF . LF",    Data = "Body line\n.\n" },
         };

         for (int index = 0; index < cases.Length; index++)
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(
               _domain, "barelf" + index + "@example.test", "test");

            var reply = SendRaw(account.Address, cases[index].Data, 20);

            StringAssert.StartsWith("250", reply,
               "Terminator spelling \"" + cases[index].Name + "\" was not accepted: " + reply);

            Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);
         }
      }

      [Test]
      [Description("A bare-LF marker with commands pipelined behind it does not smuggle a second message")]
      public void CommandsBehindABareLineFeedMarkerAreNotSmuggledIn()
      {
         // The security half of accepting a non-standard end-of-data marker, and the
         // reason the tolerance is not simply "accept \n.\r\n and carry on".
         //
         // CVE-2023-51764 is this shape: a relay that does NOT treat "\n.\r\n" as
         // end-of-data forwards the whole thing as one message body, and a server that DOES
         // treat it as end-of-data, and then parses the following lines as SMTP commands,
         // has been made to accept a second message nobody authorised - with an envelope
         // sender of the attacker's choosing.
         //
         // The protection being pinned here is that a marker is recognised only when it is
         // at the very end of everything received so far. The injected commands arrive in
         // the same write, so the bare-LF marker is not at the end, is not recognised, and
         // the injected lines become body text - which is what they are.
         _settings.AllowIncorrectLineEndings = true;

         var victim = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "smuggle@example.test", "test");

         // Everything after the bare-LF marker is the attempted injection. It is written in
         // the same call as the marker deliberately: that is the case an attacker upstream
         // of a relay can actually produce, and it must be inert.
         var payload =
            "Body line followed by a bare-LF marker\n" +
            ".\r\n" +
            "MAIL FROM:<attacker@evil.invalid>\r\n" +
            "RCPT TO:<" + victim.Address + ">\r\n" +
            "DATA\r\n" +
            "Subject: smuggled\r\n" +
            "\r\n" +
            "This message was never authorised by the relay.\r\n" +
            ".\r\n";

         var reply = SendRaw(victim.Address, payload, 20);

         StringAssert.StartsWith("250", reply, "The session did not complete normally: " + reply);

         // One message, not two. If the marker had been honoured mid-stream, the injected
         // envelope would have produced a second delivery to the same account, and this is
         // the assertion that would catch it.
         Pop3ClientSimulator.AssertMessageCount(victim.Address, "test", 1);

         var message = Pop3ClientSimulator.AssertGetFirstMessageText(victim.Address, "test");

         // And the injected commands are present as TEXT, which is the positive proof that
         // they were treated as body content rather than executed. Asserting only "one
         // message" would also pass if the whole transaction had been rejected.
         StringAssert.Contains("MAIL FROM:<attacker@evil.invalid>", message,
            "The injected commands are not in the message body, so it cannot be shown that they were treated as data.");
      }

      [Test]
      [Description("With the setting off, a standards-conformant terminator still works and nothing else changes")]
      public void TheStandardTerminatorIsUnaffectedWhenBareLineFeedsAreNotAllowed()
      {
         // The negative control, and the reason the fix is gated on a setting rather than
         // applied unconditionally: RFC 5321 says the terminator is <CRLF>.<CRLF>, and a
         // server that was not asked to be lenient should not become lenient because a
         // bug was fixed elsewhere. This also pins that the fix did not break the ordinary
         // path that every other test in the suite depends on.
         _settings.AllowIncorrectLineEndings = false;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "strictlf@example.test", "test");

         var reply = SendRaw(account.Address, "A properly terminated line\r\n.\r\n", 20);

         StringAssert.StartsWith("250", reply, "A conformant message was not accepted: " + reply);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);
      }
   }
}
