// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    REPLACE (RFC 8508). Saving a draft used to be APPEND + STORE \Deleted +
   ///    EXPUNGE - three commands with a window where both versions, or neither,
   ///    were visible. REPLACE is the same result in one command: the new
   ///    message is stored first, and only then does the old one leave the
   ///    mailbox, so a failed append leaves the original untouched. Both the
   ///    sequence-number form and UID REPLACE are supported.
   /// </summary>
   [TestFixture]
   public class Replace : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "replace@example.test", "test");
      }

      private ImapClientSimulator LogonWithOneDraft(out string response)
      {
         const string original = "Subject: draft v1\r\n\r\nFirst version.\r\n";

         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A01 APPEND INBOX {" + original.Length + "+}\r\n" + original + "\r\n");
         response = imapSim.ReceiveUntil("A01 ");
         return imapSim;
      }

      [Test]
      [Description("REPLACE swaps the message in one command: new stored, old expunged, one tagged OK.")]
      public void ReplaceSwapsTheMessageInOneCommand()
      {
         var imapSim = LogonWithOneDraft(out var setup);
         StringAssert.Contains("A01 OK", setup);

         const string replacement = "Subject: draft v2\r\n\r\nSecond version.\r\n";

         imapSim.SendRaw("A02 REPLACE 1 INBOX {" + replacement.Length + "+}\r\n" + replacement + "\r\n");
         var response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("A02 OK", response,
            "REPLACE must succeed. Got: " + response);
         StringAssert.Contains("EXPUNGE", response,
            "The replaced message's removal is reported before the tagged OK. Got: " + response);
         StringAssert.Contains("[APPENDUID", response,
            "UIDPLUS: the replacement's UID travels in APPENDUID. Got: " + response);

         imapSim.SendRaw("A03 STATUS INBOX (MESSAGES)\r\n");
         var status = imapSim.ReceiveUntil("A03 OK");
         StringAssert.Contains("MESSAGES 1", status,
            "Exactly one message - the replacement - remains. Got: " + status);

         imapSim.SendRaw("A04 FETCH 1 (BODY.PEEK[HEADER.FIELDS (Subject)])\r\n");
         var fetch = imapSim.ReceiveUntil("A04 OK");
         StringAssert.Contains("draft v2", fetch,
            "The remaining message must be the replacement. Got: " + fetch);
         ClassicAssert.IsFalse(fetch.Contains("draft v1"),
            "The original must be gone. Got: " + fetch);

         imapSim.Disconnect();
      }

      [Test]
      [Description("UID REPLACE addresses the target by UID.")]
      public void UidReplaceAddressesTheTargetByUid()
      {
         var imapSim = LogonWithOneDraft(out var setup);
         StringAssert.Contains("A01 OK", setup);

         var appendUid = Regex.Match(setup, @"\[APPENDUID \d+ (\d+)\]");
         ClassicAssert.IsTrue(appendUid.Success, "No APPENDUID for the original. Got: " + setup);
         var uid = appendUid.Groups[1].Value;

         const string replacement = "Subject: via uid\r\n\r\nReplaced by UID.\r\n";

         imapSim.SendRaw("A02 UID REPLACE " + uid + " INBOX {" + replacement.Length + "+}\r\n" + replacement + "\r\n");
         var response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("A02 OK", response,
            "UID REPLACE must succeed. Got: " + response);

         imapSim.SendRaw("A03 FETCH 1 (BODY.PEEK[HEADER.FIELDS (Subject)])\r\n");
         var fetch = imapSim.ReceiveUntil("A03 OK");
         StringAssert.Contains("via uid", fetch,
            "The remaining message must be the replacement. Got: " + fetch);

         imapSim.Disconnect();
      }

      /// <summary>
      ///    The safety property that makes REPLACE worth having: when the append
      ///    half fails, the original must survive. An implementation that
      ///    removed first and appended second would pass everything else and
      ///    fail here.
      /// </summary>
      [Test]
      [Description("A failed append leaves the original untouched - the control.")]
      public void AFailedAppendLeavesTheOriginalUntouched()
      {
         var originalMaxSizeKB = _settings.MaxMessageSize;
         _settings.MaxMessageSize = 10; // KB

         try
         {
            var imapSim = LogonWithOneDraft(out var setup);
            StringAssert.Contains("A01 OK", setup);

            imapSim.SendRaw("A02 REPLACE 1 INBOX {" + 50 * 1024 + "}\r\n");
            var response = imapSim.ReceiveUntil("A02 ");

            StringAssert.Contains("A02 NO", response,
               "The oversized replacement must be refused. Got: " + response);

            imapSim.SendRaw("A03 STATUS INBOX (MESSAGES)\r\n");
            var status = imapSim.ReceiveUntil("A03 OK");
            StringAssert.Contains("MESSAGES 1", status,
               "The original must still be there after the failed REPLACE. Got: " + status);

            imapSim.SendRaw("A04 FETCH 1 (BODY.PEEK[HEADER.FIELDS (Subject)])\r\n");
            var fetch = imapSim.ReceiveUntil("A04 OK");
            StringAssert.Contains("draft v1", fetch,
               "And it must still be the original. Got: " + fetch);

            imapSim.Disconnect();
         }
         finally
         {
            _settings.MaxMessageSize = originalMaxSizeKB;
         }
      }

      [Test]
      [Description("REPLACE with a target that does not exist is refused NO.")]
      public void AMissingTargetIsRefused()
      {
         var imapSim = new ImapClientSimulator();
         imapSim.Connect();
         imapSim.Logon(_account.Address, "test");
         imapSim.SelectFolder("INBOX");

         imapSim.SendRaw("A02 REPLACE 5 INBOX {10}\r\n");
         var response = imapSim.ReceiveUntil("A02 ");

         StringAssert.Contains("A02 NO", response,
            "A target sequence number with no message must be refused. Got: " + response);
         StringAssert.Contains("No such message", response,
            "And the refusal says why. Got: " + response);

         imapSim.Disconnect();
      }

      [Test]
      [Description("CAPABILITY advertises REPLACE.")]
      public void CapabilityAdvertisesReplace()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains(" REPLACE", capabilities,
            "CAPABILITY must advertise REPLACE. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
