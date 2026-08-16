// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    MULTIAPPEND (RFC 3502). Several messages travel in one APPEND - the
   ///    shape migration tools want when filling a mailbox - and the command is
   ///    atomic: either every message is stored or none is, which is the entire
   ///    difference from issuing N separate APPENDs. With UIDPLUS the single
   ///    APPENDUID response carries the whole uid-set.
   /// </summary>
   [TestFixture]
   public class MultiAppend : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "multiappend@example.test", "test");
      }

      [Test]
      [Description("Two messages in one APPEND: one tagged OK, an APPENDUID uid-set, both messages present.")]
      public void TwoMessagesArriveInOneCommand()
      {
         const string first = "Subject: first\r\n\r\nFirst body.\r\n";
         const string second = "Subject: second\r\n\r\nSecond body.\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A01 APPEND INBOX {" + first.Length + "}\r\n");
         var continuation = imapSim.ReceiveUntil("+ ");
         StringAssert.Contains("+ Ready", continuation, "No continuation for the first literal. Got: " + continuation);

         imapSim.SendRaw(first + " (\\Seen) {" + second.Length + "}\r\n");
         continuation = imapSim.ReceiveUntil("+ ");
         StringAssert.Contains("+ Ready", continuation, "No continuation for the second literal. Got: " + continuation);

         imapSim.SendRaw(second + "\r\n");
         var response = imapSim.ReceiveUntil("A01 ");

         StringAssert.Contains("A01 OK", response,
            "The two-message APPEND must succeed with a single tagged OK. Got: " + response);

         var appendUid = Regex.Match(response, @"\[APPENDUID \d+ (\d+)[:,](\d+)\]");
         ClassicAssert.IsTrue(appendUid.Success,
            "RFC 4315 with MULTIAPPEND: APPENDUID must carry the uid-set of BOTH messages. Got: " + response);

         imapSim.SendRaw("A02 STATUS INBOX (MESSAGES)\r\n");
         var status = imapSim.ReceiveUntil("A02 OK");
         StringAssert.Contains("MESSAGES 2", status,
            "Both messages must be in the mailbox. Got: " + status);

         imapSim.Disconnect();
      }

      /// <summary>
      ///    The point of the extension: atomicity. The second message is above
      ///    the server's maximum size, so the FIRST - already fully received and
      ///    written - must be thrown away too. An implementation that saved
      ///    per-message would pass every other test here and fail this one.
      /// </summary>
      [Test]
      [Description("A failing later message discards the whole command - nothing is stored.")]
      public void AFailingLaterMessageDiscardsEverything()
      {
         var originalMaxSizeKB = _settings.MaxMessageSize;
         _settings.MaxMessageSize = 10; // KB

         try
         {
            const string first = "Subject: survivor?\r\n\r\nSmall and valid.\r\n";
            var oversized = 50 * 1024; // 50 KB declared, over the 10 KB limit

            var imapSim = new ImapClientSimulator();
            imapSim.Connect();
            imapSim.Logon(_account.Address, "test");

            imapSim.SendRaw("A01 APPEND INBOX {" + first.Length + "}\r\n");
            var continuation = imapSim.ReceiveUntil("+ ");
            StringAssert.Contains("+ Ready", continuation);

            // The second message's size is refused at its declaration, before
            // any of its octets move - the client is waiting for a continuation
            // and receives the tagged refusal instead.
            imapSim.SendRaw(first + " {" + oversized + "}\r\n");
            var response = imapSim.ReceiveUntil("A01 ");

            StringAssert.Contains("A01 NO", response,
               "The oversized second message must fail the command. Got: " + response);
            StringAssert.Contains("[TOOBIG]", response,
               "The refusal names the reason with the TOOBIG response code. Got: " + response);

            imapSim.SendRaw("A02 STATUS INBOX (MESSAGES)\r\n");
            var status = imapSim.ReceiveUntil("A02 OK");
            StringAssert.Contains("MESSAGES 0", status,
               "RFC 3502: refusing any message means storing none - the first, already " +
               "received in full, must be discarded too. Got: " + status);

            imapSim.Disconnect();
         }
         finally
         {
            _settings.MaxMessageSize = originalMaxSizeKB;
         }
      }

      [Test]
      [Description("Non-synchronizing literals throughout: the whole multi-message APPEND in one write.")]
      public void NonSynchronizingLiteralsNeedOneRoundTrip()
      {
         const string first = "Subject: one\r\n\r\nBody one.\r\n";
         const string second = "Subject: two\r\n\r\nBody two.\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         imapSim.SendRaw("A01 APPEND INBOX {" + first.Length + "+}\r\n" + first +
                         " {" + second.Length + "+}\r\n" + second + "\r\n");
         var response = imapSim.ReceiveUntil("A01 ");

         StringAssert.Contains("A01 OK", response,
            "MULTIAPPEND combined with LITERAL- must complete in a single round trip. Got: " + response);
         ClassicAssert.IsFalse(response.Contains("+ Ready"),
            "No continuation may be sent for non-synchronizing literals. Got: " + response);

         imapSim.SendRaw("A02 STATUS INBOX (MESSAGES)\r\n");
         var status = imapSim.ReceiveUntil("A02 OK");
         StringAssert.Contains("MESSAGES 2", status,
            "Both messages must be in the mailbox. Got: " + status);

         imapSim.Disconnect();
      }

      [Test]
      [Description("Flags and a date on a later message are applied to that message.")]
      public void FlagsOnALaterMessageApplyToIt()
      {
         const string first = "Subject: unflagged\r\n\r\nPlain.\r\n";
         const string second = "Subject: flagged\r\n\r\nSeen and flagged.\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");

         imapSim.SendRaw("A01 APPEND INBOX {" + first.Length + "+}\r\n" + first +
                         " (\\Seen \\Flagged) {" + second.Length + "+}\r\n" + second + "\r\n");
         var response = imapSim.ReceiveUntil("A01 ");
         StringAssert.Contains("A01 OK", response, "The APPEND must succeed. Got: " + response);

         imapSim.SelectFolder("INBOX");
         imapSim.SendRaw("A02 FETCH 1:2 (FLAGS)\r\n");
         var flags = imapSim.ReceiveUntil("A02 OK");

         StringAssert.Contains("* 2 FETCH (FLAGS (\\Flagged \\Seen))", flags,
            "The second message carries the flags given with it. Got: " + flags);
         ClassicAssert.IsFalse(flags.Contains("* 1 FETCH (FLAGS (\\Flagged"),
            "The first message was appended without flags and must have none. Got: " + flags);

         imapSim.Disconnect();
      }

      [Test]
      [Description("CAPABILITY advertises MULTIAPPEND.")]
      public void CapabilityAdvertisesMultiAppend()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains("MULTIAPPEND", capabilities,
            "CAPABILITY must advertise MULTIAPPEND. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
