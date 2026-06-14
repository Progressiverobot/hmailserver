// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Security.Cryptography;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   public class Basics : TestFixtureBase
   {
      [Test]
      public void TestAppendBadLiteral()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.LogonWithLiteral("check@example.test", "test");
         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {TEST}", "ABCD");
         Assert.AreEqual(0, simulator.GetMessageCount("INBOX"));
         simulator.Disconnect();
      }

      [Test]
      [Description("Reproducer: an APPEND with an absurd octet count must be rejected before the server enters literal mode (no unbounded buffering / integer overflow), and the connection must remain usable.")]
      public void TestAppendOversizedLiteralRejected()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "huge@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.Logon("huge@example.test", "test");

         // ~100 GB declared literal. Must be rejected, never buffered.
         string response = simulator.Send("A01 APPEND INBOX {99999999999}");
         Assert.IsFalse(response.StartsWith("+"), "Server must not enter literal mode for an oversized APPEND. Got: " + response);
         Assert.IsTrue(response.Contains("A01 NO") || response.Contains("A01 BAD"), "Expected a rejection. Got: " + response);

         Assert.AreEqual(0, simulator.GetMessageCount("INBOX"));

         // The connection must still be usable after the rejection.
         string noop = simulator.Send("A02 NOOP");
         Assert.IsTrue(noop.Contains("A02 OK"), "Connection should remain usable after rejection. Got: " + noop);

         simulator.Disconnect();
      }

      [Test]
      [Description("Reproducer: an oversized command-level literal (here on LOGIN, pre-auth) must not be requested/buffered; the server rejects it and stays responsive.")]
      public void TestOversizedCommandLiteralRejected()
      {
         var simulator = new ImapClientSimulator();
         simulator.Connect();

         // Pre-auth: an absurd literal length must not cause the server to buffer
         // unbounded data (it must not answer with a "+" continuation).
         string response = simulator.Send("A01 LOGIN {99999999999}");
         Assert.IsFalse(response.StartsWith("+"), "Server must not request an oversized literal. Got: " + response);

         // The connection must still respond to a normal command.
         string caps = simulator.Send("A02 CAPABILITY");
         Assert.IsTrue(caps.Contains("A02 OK") || caps.Contains("CAPABILITY"), "Connection should remain usable. Got: " + caps);

         simulator.Disconnect();
      }

      [Test]
      [Description("Security: with the per-IP auto-ban disabled, a single IMAP connection must still be disconnected after the per-connection authentication-failure cap (defense-in-depth brute-force protection).")]
      public void TestPerConnectionLoginFailureCapDisconnects()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         bool originalAutoBan = settings.AutoBanOnLogonFailure;
         settings.AutoBanOnLogonFailure = false;
         settings.ClearLogonFailureList();

         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "capimap@example.test", "secret");

            var simulator = new ImapClientSimulator();
            simulator.Connect();

            string last = "";
            bool disconnected = false;
            for (int i = 1; i <= 12; i++)
            {
               try
               {
                  last = simulator.Send("A" + i + " LOGIN capimap@example.test wrongpassword");
               }
               catch (Exception)
               {
                  disconnected = true;
                  break;
               }

               if (last.Contains("Too many invalid logon attempts") || last.Contains("Goodbye"))
               {
                  disconnected = true;
                  break;
               }
            }

            Assert.IsTrue(disconnected,
               "The IMAP connection should have been disconnected after the per-connection authentication-failure cap. Last response: " + last);

            simulator.Disconnect();
         }
         finally
         {
            settings.AutoBanOnLogonFailure = originalAutoBan;
            settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("RFC 3691 UNSELECT: closes the selected mailbox WITHOUT the implicit EXPUNGE that CLOSE performs, " +
                   "so messages marked \\Deleted are retained. Also asserts UNSELECT is advertised in CAPABILITY.")]
      public void TestUnselectKeepsDeletedMessages()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "unselect@example.test", "test");

         var smtp = new SmtpClientSimulator();
         smtp.Send(account.Address, account.Address, "UNSELECT test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));

         Assert.IsTrue(simulator.GetCapabilities().Contains("UNSELECT"),
            "The CAPABILITY response should advertise the UNSELECT extension.");

         Assert.IsTrue(simulator.SelectFolder("INBOX"));
         Assert.IsTrue(simulator.SetDeletedFlag(1), "The message should have been marked \\Deleted.");

         string response = simulator.SendSingleCommand("A50 UNSELECT");
         Assert.IsTrue(response.Contains("A50 OK"), "UNSELECT should succeed. Response: " + response);

         // RFC 3691: UNSELECT must not expunge the \Deleted message (unlike CLOSE).
         Assert.AreEqual(1, simulator.GetMessageCount("INBOX"),
            "UNSELECT must not expunge messages marked \\Deleted.");

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 4315 (UIDPLUS): APPEND returns an [APPENDUID validity uid] response code and " +
                   "UIDPLUS is advertised in CAPABILITY.")]
      public void TestAppendReturnsAppendUid()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "uidplus@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("uidplus@example.test", "test");

         Assert.IsTrue(simulator.GetCapabilities().Contains("UIDPLUS"),
            "The CAPABILITY response should advertise the UIDPLUS extension.");

         string result = simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         Assert.IsTrue(result.Contains("[APPENDUID"),
            "APPEND should return an APPENDUID response code. Response: " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 4315 (UIDPLUS): COPY returns a [COPYUID ...] response code.")]
      public void TestCopyReturnsCopyUid()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "uidpluscopy@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("uidpluscopy@example.test", "test");

         Assert.IsTrue(simulator.CreateFolder("Target"));
         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SelectFolder("INBOX");

         string result = simulator.SendSingleCommand("A02 COPY 1 Target");
         Assert.IsTrue(result.Contains("[COPYUID"),
            "COPY should return a COPYUID response code. Response: " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 4315 (UIDPLUS): UID EXPUNGE removes only the \\Deleted messages whose UID is in the " +
                   "supplied set, leaving other \\Deleted messages intact.")]
      public void TestUidExpungeOnlyRemovesMatchingUids()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "uidexpunge@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("uidexpunge@example.test", "test");

         // Append two messages; they receive UIDs 1 and 2.
         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "EFGH");

         // GetMessageCount selects INBOX and leaves it selected.
         Assert.AreEqual(2, simulator.GetMessageCount("INBOX"));

         // Mark both messages \Deleted (by sequence number).
         simulator.SetDeletedFlag(1);
         simulator.SetDeletedFlag(2);

         // Expunge only the message with UID 1.
         string result = simulator.SendSingleCommand("A03 UID EXPUNGE 1");
         Assert.IsTrue(result.Contains("A03 OK"),
            "UID EXPUNGE should succeed. Response: " + result);

         // The message with UID 2 must remain.
         Assert.AreEqual(1, simulator.GetMessageCount("INBOX"),
            "UID EXPUNGE must remove only the message with the matching UID.");

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 5161 (ENABLE): ENABLE is advertised in CAPABILITY and the command returns a tagged OK.")]
      public void TestEnableReturnsOk()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "enable@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("enable@example.test", "test");

         Assert.IsTrue(simulator.GetCapabilities().Contains("ENABLE"),
            "The CAPABILITY response should advertise the ENABLE extension.");

         string result = simulator.SendSingleCommand("A01 ENABLE CONDSTORE");
         Assert.IsTrue(result.Contains("A01 OK"),
            "ENABLE should return a tagged OK. Response: " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 8438 (STATUS=SIZE): STATUS returns the total mailbox SIZE and STATUS=SIZE is " +
                   "advertised in CAPABILITY.")]
      public void TestStatusReturnsMailboxSize()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "statussize@example.test", "test");

         var smtp = new SmtpClientSimulator();
         smtp.Send(account.Address, account.Address, "Size test", "Body");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 1);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));

         Assert.IsTrue(simulator.GetCapabilities().Contains("STATUS=SIZE"),
            "The CAPABILITY response should advertise STATUS=SIZE.");

         string result = simulator.SendSingleCommand("A01 STATUS INBOX (MESSAGES SIZE)");

         int sizePos = result.IndexOf("SIZE ");
         Assert.Greater(sizePos, 0, "STATUS should return a SIZE item. Response: " + result);

         string after = result.Substring(sizePos + 5);
         int end = after.IndexOfAny(new[] { ' ', ')' });
         int size = int.Parse(after.Substring(0, end));
         Assert.Greater(size, 0, "STATUS SIZE should be greater than zero for a non-empty mailbox. Response: " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 4731 (ESEARCH): SEARCH RETURN (...) yields a * ESEARCH response carrying MIN/MAX/COUNT/ALL " +
                   "and ESEARCH is advertised in CAPABILITY.")]
      public void TestEsearchReturnsExtendedResponse()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "esearch@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("esearch@example.test", "test");

         Assert.IsTrue(simulator.GetCapabilities().Contains("ESEARCH"),
            "The CAPABILITY response should advertise the ESEARCH extension.");

         // Append three messages; they receive sequence numbers / UIDs 1..3.
         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "AAAA");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "BBBB");
         simulator.SendSingleCommandWithLiteral("A03 APPEND INBOX {4}", "CCCC");

         simulator.SelectFolder("INBOX");

         string result = simulator.SendSingleCommand("A04 SEARCH RETURN (MIN MAX COUNT ALL) ALL");
         Assert.IsTrue(result.Contains("* ESEARCH"),
            "SEARCH RETURN must produce an ESEARCH response. Response: " + result);
         Assert.IsTrue(result.Contains("(TAG \"A04\")"),
            "The ESEARCH response must echo the command tag. Response: " + result);
         Assert.IsTrue(result.Contains("MIN 1"),
            "ESEARCH MIN should be 1. Response: " + result);
         Assert.IsTrue(result.Contains("MAX 3"),
            "ESEARCH MAX should be 3. Response: " + result);
         Assert.IsTrue(result.Contains("COUNT 3"),
            "ESEARCH COUNT should be 3. Response: " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162 (CONDSTORE/QRESYNC): CONDSTORE and QRESYNC are advertised in CAPABILITY and " +
                   "ENABLE CONDSTORE echoes an * ENABLED CONDSTORE response.")]
      public void TestEnableCondstoreEchoesEnabled()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "condstore@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("condstore@example.test", "test");

         var caps = simulator.GetCapabilities();
         Assert.IsTrue(caps.Contains("CONDSTORE"), "CAPABILITY should advertise CONDSTORE. " + caps);
         Assert.IsTrue(caps.Contains("QRESYNC"), "CAPABILITY should advertise QRESYNC. " + caps);

         string result = simulator.SendSingleCommand("A01 ENABLE CONDSTORE");
         Assert.IsTrue(result.Contains("* ENABLED CONDSTORE"),
            "ENABLE CONDSTORE should echo an ENABLED response. " + result);
         Assert.IsTrue(result.Contains("A01 OK"), result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162: after ENABLE CONDSTORE, SELECT reports the mailbox HIGHESTMODSEQ; " +
                   "STATUS reports the HIGHESTMODSEQ attribute.")]
      public void TestSelectAndStatusReportHighestModSeq()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "highestmodseq@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("highestmodseq@example.test", "test");

         simulator.SendSingleCommand("A01 ENABLE CONDSTORE");

         string select = simulator.SendSingleCommand("A02 SELECT INBOX");
         Assert.IsTrue(select.Contains("[HIGHESTMODSEQ"),
            "SELECT should report HIGHESTMODSEQ once CONDSTORE is enabled. " + select);

         string status = simulator.SendSingleCommand("A03 STATUS INBOX (HIGHESTMODSEQ)");
         Assert.IsTrue(status.Contains("HIGHESTMODSEQ"),
            "STATUS should report the HIGHESTMODSEQ attribute. " + status);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162: FETCH MODSEQ returns a per-message mod-sequence, and a flag change must " +
                   "increase that message's mod-sequence (persisted).")]
      public void TestFetchModSeqIncrementsOnFlagChange()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "modseqfetch@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("modseqfetch@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommand("A02 ENABLE CONDSTORE");
         simulator.SelectFolder("INBOX");

         string before = simulator.SendSingleCommand("A03 FETCH 1 (MODSEQ)");
         Assert.IsTrue(before.Contains("MODSEQ ("), "FETCH should return a MODSEQ data item. " + before);
         long modseqBefore = ParseModSeq(before);

         // Changing a flag is a metadata change and must bump the message mod-sequence.
         simulator.SendSingleCommand("A04 STORE 1 +FLAGS (\\Seen)");

         string after = simulator.SendSingleCommand("A05 FETCH 1 (MODSEQ)");
         long modseqAfter = ParseModSeq(after);

         Assert.Greater(modseqAfter, modseqBefore,
            "A flag change must increase the message MODSEQ. before=" + before + " after=" + after);

         simulator.Disconnect();
      }

      private static long ParseModSeq(string fetchResponse)
      {
         const string marker = "MODSEQ (";
         int p = fetchResponse.IndexOf(marker);
         Assert.Greater(p, -1, "Response did not contain a MODSEQ item: " + fetchResponse);
         p += marker.Length;
         int e = fetchResponse.IndexOf(")", p);
         return long.Parse(fetchResponse.Substring(p, e - p));
      }

      [Test]
      [Description("RFC 7162: FETCH (CHANGEDSINCE n) returns only messages whose mod-sequence is " +
                   "greater than n, and implicitly includes MODSEQ in the response.")]
      public void TestFetchChangedSinceFiltersByModSeq()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "changedsince@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("changedsince@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommand("A02 ENABLE CONDSTORE");
         simulator.SelectFolder("INBOX");

         long modseq = ParseModSeq(simulator.SendSingleCommand("A03 FETCH 1 (MODSEQ)"));

         // CHANGEDSINCE equal to the current mod-sequence excludes the message.
         string notChanged = simulator.SendSingleCommand("A04 FETCH 1 (FLAGS) (CHANGEDSINCE " + modseq + ")");
         Assert.IsFalse(notChanged.Contains("* 1 FETCH"),
            "CHANGEDSINCE == current modseq should not return the message. " + notChanged);

         // CHANGEDSINCE below the current mod-sequence includes it, with MODSEQ.
         string changed = simulator.SendSingleCommand("A05 FETCH 1 (FLAGS) (CHANGEDSINCE " + (modseq - 1) + ")");
         Assert.IsTrue(changed.Contains("* 1 FETCH"),
            "CHANGEDSINCE below current modseq should return the message. " + changed);
         Assert.IsTrue(changed.Contains("MODSEQ ("),
            "CHANGEDSINCE implicitly enables MODSEQ in the FETCH response. " + changed);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162: a conditional STORE (UNCHANGEDSINCE n) leaves messages changed since n " +
                   "untouched and reports them in a [MODIFIED set] response code.")]
      public void TestStoreUnchangedSinceRejectsModified()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "unchangedsince@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("unchangedsince@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommand("A02 ENABLE CONDSTORE");
         simulator.SelectFolder("INBOX");

         long modseq = ParseModSeq(simulator.SendSingleCommand("A03 FETCH 1 (MODSEQ)"));

         // Bump the message mod-sequence past the value we will use for UNCHANGEDSINCE.
         simulator.SendSingleCommand("A04 STORE 1 +FLAGS (\\Seen)");

         string conditional = simulator.SendSingleCommand("A05 STORE 1 (UNCHANGEDSINCE " + modseq + ") +FLAGS (\\Flagged)");
         Assert.IsTrue(conditional.Contains("[MODIFIED 1]"),
            "A stale conditional STORE should report the message in [MODIFIED]. " + conditional);

         // The rejected store must not have applied the flag.
         string flags = simulator.SendSingleCommand("A06 FETCH 1 (FLAGS)");
         Assert.IsFalse(flags.Contains("\\Flagged"),
            "Rejected conditional STORE must not change the flag. " + flags);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162: a satisfied conditional STORE (UNCHANGEDSINCE n) applies the change and " +
                   "returns the new MODSEQ without a MODIFIED code.")]
      public void TestStoreUnchangedSinceSucceedsAndReturnsModSeq()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "unchangedok@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("unchangedok@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommand("A02 ENABLE CONDSTORE");
         simulator.SelectFolder("INBOX");

         long modseq = ParseModSeq(simulator.SendSingleCommand("A03 FETCH 1 (MODSEQ)"));

         string conditional = simulator.SendSingleCommand("A04 STORE 1 (UNCHANGEDSINCE " + modseq + ") +FLAGS (\\Seen)");
         Assert.IsFalse(conditional.Contains("MODIFIED"),
            "A satisfied conditional STORE should not report MODIFIED. " + conditional);
         Assert.IsTrue(conditional.Contains("MODSEQ ("),
            "A CONDSTORE STORE must return the new MODSEQ. " + conditional);
         Assert.Greater(ParseModSeq(conditional), modseq,
            "The successful store must bump the mod-sequence. " + conditional);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162: SEARCH MODSEQ n matches messages with mod-sequence >= n and appends the " +
                   "highest matched mod-sequence as (MODSEQ n).")]
      public void TestSearchModSeqReportsHighest()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "searchmodseq@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("searchmodseq@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "EFGH");
         simulator.SendSingleCommand("A03 ENABLE CONDSTORE");
         simulator.SelectFolder("INBOX");

         string result = simulator.SendSingleCommand("A04 SEARCH MODSEQ 1");
         Assert.IsTrue(result.Contains("* SEARCH"), "Expected a SEARCH response. " + result);
         Assert.IsTrue(result.Contains("(MODSEQ "),
            "A MODSEQ search should append the highest mod-sequence. " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162 (QRESYNC): once QRESYNC is enabled, EXPUNGE reports removed messages " +
                   "as a single * VANISHED UID set instead of per-message * n EXPUNGE lines.")]
      public void TestExpungeWithQResyncReturnsVanished()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "vanished@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("vanished@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommand("A02 ENABLE QRESYNC");
         simulator.SelectFolder("INBOX");
         simulator.SetDeletedFlag(1);

         string result = simulator.SendSingleCommand("A05 EXPUNGE");
         Assert.IsTrue(result.Contains("* VANISHED 1"),
            "QRESYNC EXPUNGE should emit * VANISHED with the message UID. " + result);
         Assert.IsFalse(result.Contains("* 1 EXPUNGE"),
            "QRESYNC EXPUNGE must not also emit a * n EXPUNGE line. " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162 (QRESYNC): SELECT (QRESYNC (uidvalidity modseq)) replays flag/MODSEQ " +
                   "changes since the supplied mod-sequence as untagged FETCH responses.")]
      public void TestSelectQResyncReplaysChanges()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "qresyncsel@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("qresyncsel@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommand("A02 ENABLE QRESYNC");
         simulator.SelectFolder("INBOX");

         long modseq = ParseModSeq(simulator.SendSingleCommand("A03 FETCH 1 (MODSEQ)"));

         // Change a flag to bump the message mod-sequence past the value we will resync from.
         simulator.SendSingleCommand("A04 STORE 1 +FLAGS (\\Seen)");

         // Re-select with QRESYNC referencing the earlier mod-sequence; the change must be replayed.
         string result = simulator.SendSingleCommand("A05 SELECT INBOX (QRESYNC (1 " + modseq + "))");
         Assert.IsTrue(result.Contains("* 1 FETCH"),
            "QRESYNC SELECT should replay the changed message. " + result);
         Assert.IsTrue(result.Contains("MODSEQ ("),
            "A replayed QRESYNC FETCH should include MODSEQ. " + result);
         Assert.IsTrue(result.Contains("HIGHESTMODSEQ"),
            "QRESYNC SELECT should report HIGHESTMODSEQ. " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162 (QRESYNC): SELECT (QRESYNC (uidvalidity modseq)) reports messages " +
                   "expunged since the supplied mod-sequence via * VANISHED (EARLIER), using the " +
                   "persisted expunge tombstones.")]
      public void TestSelectQResyncReportsVanishedEarlier()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "vanishedearlier@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("vanishedearlier@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "EFGH");
         simulator.SendSingleCommand("A03 ENABLE QRESYNC");
         simulator.SelectFolder("INBOX");

         long modseq = ParseModSeq(simulator.SendSingleCommand("A04 FETCH 1 (MODSEQ)"));

         // Expunge the first message; this records a persistent tombstone.
         simulator.SetDeletedFlag(1);
         simulator.SendSingleCommand("A05 EXPUNGE");

         // Re-select with QRESYNC referencing the earlier mod-sequence; the expunge must be reported.
         string result = simulator.SendSingleCommand("A06 SELECT INBOX (QRESYNC (1 " + modseq + "))");
         Assert.IsTrue(result.Contains("* VANISHED (EARLIER) 1"),
            "QRESYNC SELECT should report the expunged UID via * VANISHED (EARLIER). " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7162 (QRESYNC): UID FETCH <set> (CHANGEDSINCE n VANISHED) reports UIDs in " +
                   "the requested set that were expunged since mod-sequence n via * VANISHED (EARLIER).")]
      public void TestUidFetchVanishedReportsEarlier()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "uidfetchvanished@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("uidfetchvanished@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "EFGH");
         simulator.SendSingleCommand("A03 ENABLE QRESYNC");
         simulator.SelectFolder("INBOX");

         long modseq = ParseModSeq(simulator.SendSingleCommand("A04 FETCH 1 (MODSEQ)"));

         // Expunge the first message; this records a persistent tombstone.
         simulator.SetDeletedFlag(1);
         simulator.SendSingleCommand("A05 EXPUNGE");

         string result = simulator.SendSingleCommand(
            "A06 UID FETCH 1:* (FLAGS) (CHANGEDSINCE " + modseq + " VANISHED)");
         Assert.IsTrue(result.Contains("* VANISHED (EARLIER) 1"),
            "UID FETCH VANISHED should report the expunged UID via * VANISHED (EARLIER). " + result);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 5182 (SEARCHRES): UID SEARCH RETURN (SAVE) stores the result, which a " +
                   "subsequent UID FETCH can reference via the \"$\" marker.")]
      public void TestSearchResSaveAndFetch()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "searchres@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("searchres@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "EFGH");
         simulator.SendSingleCommandWithLiteral("A03 APPEND INBOX {4}", "IJKL");
         simulator.SelectFolder("INBOX");

         // Flag the second message \Seen, then save the SEEN search result.
         simulator.SendSingleCommand("A04 STORE 2 +FLAGS (\\Seen)");

         string save = simulator.SendSingleCommand("A05 UID SEARCH RETURN (SAVE) SEEN");
         Assert.IsTrue(save.Contains("A05 OK"), "SEARCH RETURN (SAVE) should succeed. " + save);

         // "$" must now resolve to the single saved message (UID 2).
         string fetch = simulator.SendSingleCommand("A06 UID FETCH $ (UID)");
         Assert.IsTrue(fetch.Contains("UID 2"),
            "UID FETCH $ should resolve to the saved message UID 2. " + fetch);
         Assert.IsFalse(fetch.Contains("UID 1"),
            "UID FETCH $ should not include unsaved messages. " + fetch);
         Assert.IsFalse(fetch.Contains("UID 3"),
            "UID FETCH $ should not include unsaved messages. " + fetch);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 5182 (SEARCHRES): the saved \"$\" result can be referenced by a UID STORE, " +
                   "applying flags to exactly the saved messages.")]
      public void TestSearchResSaveAndStore()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "searchresstore@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("searchresstore@example.test", "test");

         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         simulator.SendSingleCommandWithLiteral("A02 APPEND INBOX {4}", "EFGH");
         simulator.SelectFolder("INBOX");

         // Save all messages, then flag the saved set \Flagged via "$".
         simulator.SendSingleCommand("A04 UID SEARCH RETURN (SAVE) ALL");
         simulator.SendSingleCommand("A05 UID STORE $ +FLAGS (\\Flagged)");

         // Both messages must now carry \Flagged.
         string flagged = simulator.Search("FLAGGED");
         Assert.AreEqual("1 2", flagged,
            "Both saved messages should be \\Flagged after UID STORE $. Got: " + flagged);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 5182 (SEARCHRES): SEARCH RETURN (SAVE) advertised in CAPABILITY.")]
      public void TestSearchResCapability()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "searchrescap@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral("searchrescap@example.test", "test");

         string caps = simulator.SendSingleCommand("A01 CAPABILITY");
         Assert.IsTrue(caps.Contains("SEARCHRES"),
            "CAPABILITY should advertise SEARCHRES. " + caps);

         simulator.Disconnect();
      }

      [Test]
      [Description("RFC 7677 (SCRAM-SHA-256): AUTH=SCRAM-SHA-256 is advertised in CAPABILITY when " +
                   "IMAP SASL AUTHENTICATE is enabled.")]
      public void TestScramSha256Capability()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         application.Settings.IMAPSASLPlainEnabled = true;
         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scramcap@example.test", "test");

            var simulator = new ImapClientSimulator();
            simulator.Connect();
            simulator.LogonWithLiteral("scramcap@example.test", "test");

            string caps = simulator.SendSingleCommand("A01 CAPABILITY");
            Assert.IsTrue(caps.Contains("AUTH=SCRAM-SHA-256"),
               "CAPABILITY should advertise AUTH=SCRAM-SHA-256. " + caps);

            simulator.Disconnect();
         }
         finally
         {
            application.Settings.IMAPSASLPlainEnabled = false;
         }
      }

      [Test]
      [Description("RFC 5802/7677 (SCRAM-SHA-256): a full SCRAM exchange authenticates a PBKDF2-hashed " +
                   "account, and the server proves it knows the key by returning a valid ServerSignature.")]
      public void TestScramSha256Authenticates()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         application.Settings.IMAPSASLPlainEnabled = true;
         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scramok@example.test", "SeC-r3t Pass!");

            using (var con = new TcpConnection())
            {
               Assert.IsTrue(con.Connect(143), "Could not connect to IMAP.");
               con.Receive(); // banner

               string final = ScramTestClient.Authenticate(con, "A01", "scramok@example.test", "SeC-r3t Pass!");
               Assert.IsTrue(final.Contains("A01 OK"),
                  "SCRAM-SHA-256 authentication should succeed. Got: " + final);

               // The connection must be authenticated and usable afterwards.
               string noop = con.SendAndReceive("A02 NOOP\r\n");
               Assert.IsTrue(noop.Contains("A02 OK"),
                  "Connection should be usable after SCRAM logon. Got: " + noop);
            }
         }
         finally
         {
            application.Settings.IMAPSASLPlainEnabled = false;
         }
      }

      [Test]
      [Description("RFC 5802/7677 (SCRAM-SHA-256): a wrong password produces an invalid client proof " +
                   "and the server rejects the exchange without authenticating.")]
      public void TestScramSha256WrongPasswordFails()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         application.Settings.IMAPSASLPlainEnabled = true;
         // Disable auto-ban so a single bad attempt does not ban the loopback address.
         bool autoBan = application.Settings.AutoBanOnLogonFailure;
         application.Settings.AutoBanOnLogonFailure = false;
         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrambad@example.test", "correct horse");

            using (var con = new TcpConnection())
            {
               Assert.IsTrue(con.Connect(143), "Could not connect to IMAP.");
               con.Receive(); // banner

               string final = ScramTestClient.Authenticate(con, "A01", "scrambad@example.test", "wrong password");
               Assert.IsTrue(final.Contains("A01 NO") || final.Contains("A01 BAD"),
                  "SCRAM-SHA-256 with a wrong password must be rejected. Got: " + final);
            }
         }
         finally
         {
            application.Settings.AutoBanOnLogonFailure = autoBan;
            application.Settings.IMAPSASLPlainEnabled = false;
         }
      }

      [Test]
      [Description("RFC 5802/7677 (SCRAM-SHA-256) anti-enumeration: an unknown account returns a stable, " +
                   "per-identity salt on every probe (a fresh random salt would reveal that the account " +
                   "does not exist), while different unknown identities receive different salts.")]
      public void TestScramSha256UnknownAccountSaltIsStable()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         application.Settings.IMAPSASLPlainEnabled = true;
         // No exchange is completed (we stop at server-first), but disable auto-ban anyway.
         bool autoBan = application.Settings.AutoBanOnLogonFailure;
         application.Settings.AutoBanOnLogonFailure = false;
         try
         {
            const string ghost = "ghost-nonexistent@example.test";

            string salt1 = ProbeSalt(ghost);
            string salt2 = ProbeSalt(ghost);
            string otherSalt = ProbeSalt("another-ghost@example.test");

            Assert.IsFalse(string.IsNullOrEmpty(salt1), "Server-first must offer a salt.");
            Assert.AreEqual(salt1, salt2,
               "An unknown account must return the same salt on every probe, otherwise the " +
               "changing salt leaks that the account does not exist.");
            Assert.AreNotEqual(salt1, otherSalt,
               "Different unknown identities must receive different salts.");
         }
         finally
         {
            application.Settings.AutoBanOnLogonFailure = autoBan;
            application.Settings.IMAPSASLPlainEnabled = false;
         }
      }

      private static string ProbeSalt(string username)
      {
         using (var con = new TcpConnection())
         {
            Assert.IsTrue(con.Connect(143), "Could not connect to IMAP.");
            con.Receive(); // banner
            return ScramTestClient.ProbeServerSalt(con, "A01", username);
         }
      }

      [Test]
      public void TestAppendDeletedMessage()

      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("check@example.test", "test");
         simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX (\\Deleted) {4}", "ABCD");
         Assert.AreEqual(1, simulator.GetMessageCount("INBOX"));

         Assert.AreEqual("1", simulator.Search("DELETED"));

         simulator.Disconnect();
      }

      [Test]
      [Description("Test that one can use APPEND and specify the folder name separately using {}-notation")]
      public void TestAppendFolderNameInOctet()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("check@example.test", "test");
         simulator.SelectFolder("INBOX");
         simulator.CreateFolder("MONK");
         simulator.SendRaw("A01 APPEND {4}\r\n");
         var result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for additional command text."));

         simulator.SendRaw("MONK (\\Seen) \"20-Jan-2009 12:59:50 +0100\" {5}\r\n");
         result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for literal data"));

         simulator.SendRaw("WOOOT\r\n");
         result = simulator.Receive();

         StringAssert.StartsWith("A01 OK [APPENDUID ", result);
         StringAssert.EndsWith("APPEND completed\r\n", result);
      }

      [Test]
      [Description(
         "Test that one can use APPEND and specify the folder name separately using {}-notation. Do not include flag list (it's optional)"
      )]
      public void TestAppendFolderNameInOctetNoFlagList()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("check@example.test", "test");
         simulator.SelectFolder("INBOX");
         simulator.CreateFolder("MONK");
         simulator.SendRaw("A01 APPEND {4}\r\n");
         var result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for additional command text."));

         simulator.SendRaw("MONK  \"12-Jan-2009 12:12:12 +0100\" {5}\r\n");
         result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for literal data"));

         simulator.SendRaw("WOOOT\r\n");
         result = simulator.Receive();

         StringAssert.StartsWith("A01 OK [APPENDUID ", result);
         StringAssert.EndsWith("APPEND completed\r\n", result);

         var date = Convert.ToDateTime(account.IMAPFolders.get_ItemByName("MONK").Messages[0].InternalDate);

         Assert.AreEqual(2009, date.Year);
         Assert.AreEqual(12, date.Day);
         Assert.AreEqual(1, date.Month);
      }

      [Test]
      [Description(
         "Test that one can use APPEND and specify the folder name separately using {}-notation. Do not include flag list or timestamp (it's optional)"
      )]
      public void TestAppendFolderNameInOctetNoFlagListOrDate()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("check@example.test", "test");
         simulator.SelectFolder("INBOX");
         simulator.CreateFolder("MONK");
         simulator.SendRaw("A01 APPEND {4}\r\n");
         var result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for additional command text."));

         simulator.SendRaw("MONK {5}\r\n");
         result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for literal data"));

         simulator.SendRaw("WOOOT\r\n");
         result = simulator.Receive();

         StringAssert.StartsWith("A01 OK [APPENDUID ", result);
         StringAssert.EndsWith("APPEND completed\r\n", result);
      }

      [Test]
      [Description(
         "Test that one can use APPEND and specify the folder name separately using {}-notation. Do not include timestamp but set deleted flag."
      )]
      public void TestAppendFolderNameInOctetSetDeletedFlag()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("check@example.test", "test");
         simulator.SelectFolder("INBOX");
         simulator.CreateFolder("MONK");
         simulator.SendRaw("A01 APPEND {4}\r\n");
         var result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for additional command text."));

         simulator.SendRaw("MONK (\\Seen \\Deleted) {5}\r\n");
         result = simulator.Receive();
         Assert.IsTrue(result.StartsWith("+ Ready for literal data"));

         simulator.SendRaw("WOOOT\r\n");
         result = simulator.Receive();

         StringAssert.StartsWith("A01 OK [APPENDUID ", result);
         StringAssert.EndsWith("APPEND completed\r\n", result);
      }

      [Test]
      [Description("Issue 247: IMAP: Untagged EXISTS not sent after APPEND completion.")]
      public void TestAppendResponseContainsExists()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.LogonWithLiteral("check@example.test", "test");
         Assert.IsTrue(simulator.SelectFolder("Inbox"));
         var response1 = simulator.SendSingleCommandWithLiteral("A01 APPEND INBOX {4}", "ABCD");
         Assert.IsTrue(response1.Contains("* 1 EXISTS"), response1);
         Assert.IsTrue(response1.Contains("* 1 RECENT"), response1);
         Assert.AreEqual(1, simulator.GetMessageCount("INBOX"));
         simulator.Disconnect();
      }

      [Test]
      public void TestAuthenticate()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "literal@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         var result = simulator.SendSingleCommand("A01 AUTHENTICATE");
         Assert.IsTrue(result.Contains("NO IMAP AUTHENTICATE is not enabled."));
         simulator.Disconnect();
      }

      [Test]
      public void TestBeforeLogon()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delete@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();

         Assert.IsTrue(simulator.ExamineFolder("NonexistantFolder").Contains("NO Authenticate first"));
         Assert.IsFalse(simulator.SelectFolder("NonexistantFolder"));
         Assert.IsFalse(simulator.Copy(1, "SomeFolder"));
         Assert.IsFalse(simulator.CheckFolder("SomeFolder"));
         Assert.IsTrue(simulator.Fetch("123 a").Contains("NO Authenticate first"));
         Assert.IsTrue(simulator.List().Contains("NO Authenticate first"));
         Assert.IsTrue(simulator.LSUB().Contains("NO Authenticate first"));
         Assert.IsTrue(simulator.GetMyRights("APA").Contains("NO Authenticate first"));
         Assert.IsFalse(simulator.RenameFolder("A", "B"));
         Assert.IsFalse(simulator.Status("SomeFolder", "MESSAGES").Contains("A01 OK"));
      }

      [Test]
      [Description("Issue 218, IMAP: Problem with file name containing non-latin chars. In this test they are encoded"
      )]
      public void TestBodyStructureWithNonLatinCharacterInAttachmentHeader()
      {
         var messageText =
            "From: \"Test\" <test@example.test>" + "\r\n" +
            "To: \"Test\" <test@example.test>" + "\r\n" +
            "Subject: test" + "\r\n" +
            "MIME-Version: 1.0" + "\r\n" +
            "Content-Type: multipart/mixed;" + "\r\n" +
            "   boundary=\"----=_NextPart_000_000C_01C9EEB2.08D2EC80\"" + "\r\n" +
            "X-Priority: 3" + "\r\n" +
            "" + "\r\n" +
            "This is a multi-part message in MIME format." + "\r\n" +
            "" + "\r\n" +
            "------=_NextPart_000_000C_01C9EEB2.08D2EC80" + "\r\n" +
            "Content-Type: text/plain;" + "\r\n" +
            "  format=flowed;" + "\r\n" +
            "	charset=\"iso-8859-1\";" + "\r\n" +
            "	reply-type=original" + "\r\n" +
            "Content-Transfer-Encoding: 7bit" + "\r\n" +
            "" + "\r\n" +
            "" + "\r\n" +
            "------=_NextPart_000_000C_01C9EEB2.08D2EC80" + "\r\n" +
            "Content-Type: application/octet-stream;" + "\r\n" +
            "	name=\"=?iso-8859-1?B?beT2LnppcA==?=\"" + "\r\n" +
            "Content-Transfer-Encoding: base64" + "\r\n" +
            "Content-Disposition: attachment;" + "\r\n" +
            "	filename=\"=?iso-8859-1?B?beT2LnppcA==?=\"" + "\r\n" +
            "" + "\r\n" +
            "iVBORw0KGgoAAAANSUhEUgAAAqgAAAH4CAIAAAAJvIhhAAAAAXNSR0IArs4c6QAAAARnQU1BAACx" + "\r\n" +
            "uIDgj5MrSIAAAQIEzgkI/nP2KhMgQIAAgbiA4I+TK0iAAAECBM4JCP5z9ioTIECAAIG4wP8ChvJS" + "\r\n" +
            "wXUaKVoAAAAASUVORK5CYII=" + "\r\n" +
            "" + "\r\n" +
            "------=_NextPart_000_000C_01C9EEB2.08D2EC80--" + "\r\n";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         Assert.IsTrue(result.Contains("(\"NAME\" \"=?iso-8859-1?B?beT2LnppcA==?=\")"));
         Assert.IsTrue(result.Contains("(\"FILENAME\" \"=?iso-8859-1?B?beT2LnppcA==?=\")"));

         var fileName = account.IMAPFolders.get_ItemByName("INBOX").Messages[0].Attachments[0].Filename;
         Assert.AreEqual("mäö.zip", fileName);
      }

      [Test]
      [Description("Issue 218, IMAP: Problem with file name containing non-latin chars (RFC 2184 compliance)")]
      public void TestBodyStructureWithNonLatinCharacterMultiLine()
      {
         var messageText =
            "To: test@example.test\r\n" +
            "Content-Type: multipart/mixed;\r\n" +
            " boundary=\"------------000008080307000003010005\"\r\n" +
            "\r\n" +
            "This is a multi-part message in MIME format.\r\n" +
            "--------------000008080307000003010005\r\n" +
            "Content-Type: text/plain; charset=ISO-8859-1; format=flowed\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "\r\n" +
            "Test\r\n" +
            "\r\n" +
            "--------------000008080307000003010005\r\n" +
            "Content-Type: image/png;\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-Disposition: inline;\r\n" +
            " filename*0*=ISO-8859-1''%F6%50%C4%C9%CD%C1%D6%60%F6%F6%E4%27%20%31%20%F6;\r\n" +
            " filename*1*=\"%50%C4%C9%CD%C1%D6%60%F6%F6%E4%27%20%32%20%F6%50%C4%C9%CD%C1\";\r\n" +
            " filename*2*=%D6%60%F6%F6%E4%27%20%33%2E%70%6E%67;\r\n" +
            "\r\n" +
            "iVBORw0KGgoAAAANSUhEUgAAAqgAAAH4CAIAAAAJvIhhAAAAAXNSR0IArs4c6QAAAARnQU1B\r\n" +
            "AACxjwv8YQUAAAAgY0hSTQAAeiYAAICEAAD6AAAAgOgAAHUwAADqYAAAOpgAABdwnLpRPAAA\r\n" +
            "--------------000008080307000003010005--\r\n";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         Assert.IsTrue(
            result.Contains(
               "\"FILENAME\" \"=?ISO-8859-1?Q?=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=20=31=20=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=20=32=20=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=20=33=2E=70=6E=67?=\""));
         Assert.IsTrue(
            result.Contains(
               "\"NAME\" \"=?ISO-8859-1?Q?=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=20=31=20=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=20=32=20=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=20=33=2E=70=6E=67?=\""));
      }

      [Test]
      [Description(
         "Issue 218, IMAP: Problem with file name containing non-latin chars (RFC 2184 compliance). In this test, there's only one line of 2184-encoded data."
      )]
      public void TestBodyStructureWithNonLatinCharacterSingleLine()
      {
         var messageText =
            "Return-Path: martin@hmailserver.com\r\n" +
            "Delivered-To: martin@hmailserver.com\r\n" +
            "Received: from www.hmailserver.com ([127.0.0.1])\r\n" +
            "	by mail.hmailserver.com\r\n" +
            "	; Tue, 16 Jun 2009 21:39:18 +0200\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Date: Tue, 16 Jun 2009 21:39:18 +0200\r\n" +
            "From: <martin@hmailserver.com>\r\n" +
            "To: <martin@hmailserver.com>\r\n" +
            "Subject: sdafsda\r\n" +
            "Message-ID: <96aee740f2abe8450648c1752a9a987b@localhost>\r\n" +
            "X-Sender: martin@hmailserver.com\r\n" +
            "User-Agent: RoundCube Webmail/0.2.2\r\n" +
            "Content-Type: multipart/mixed;\r\n" +
            "	boundary=\"=_b63968892a76b1a5be17f4d37b085f54\"\r\n" +
            "\r\n" +
            "--=_b63968892a76b1a5be17f4d37b085f54\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "Content-Type: text/plain; charset=\"UTF-8\"\r\n" +
            "\r\n" +
            "--=_b63968892a76b1a5be17f4d37b085f54\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-Type: application/x-zip; charset=\"UTF-8\";\r\n" +
            " name*=\"UTF-8''m%C3%A4%C3%B6.zip\"; \r\n" +
            "Content-Disposition: attachment;\r\n" +
            " filename*=\"UTF-8''m%C3%A4%C3%B6.zip\"; \r\n" +
            "\r\n" +
            "iVBORw0KGgoAAAANSUhEUgAAAqgAAAH4CAIAAAAJvIhhAAAAAXNSR0IArs4c6QAAAARnQU1BAACx\r\n" +
            "jwv8YQUAAAAgY0hSTQAAeiYAAICEAAD6AAAAgOgAAHUwAADqYAAAOpgAABdwnLpRPAAAIr1JREFU\r\n" +
            "eF7t29GSYzluBND1/3/0ejb6wY6emK2WrpQEkaefdQvAAcn02OH/+fe///0v/wgQIECAAIESgb+C\r\n" +
            "3z8CBAgQIECgROBfJXMakwABAgQIEPjP/5qfAgECBAgQINAjIPh7dm1SAgQIECDgv/idAQIECBAg\r\n" +
            "--=_b63968892a76b1a5be17f4d37b085f54--\r\n" +
            "";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);


         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         Assert.IsFalse(result.Contains("''"), result);
         Assert.IsTrue(result.Contains("\"FILENAME\" \"=?UTF-8?Q?m=C3=A4=C3=B6.zip?=\""), result);
         Assert.IsTrue(result.Contains("\"NAME\" \"=?UTF-8?Q?m=C3=A4=C3=B6.zip?=\""), result);
      }

      [Test]
      [Description("Issue 218, IMAP: Problem with file name containing non-latin chars. In this test they are encoded"
      )]
      public void TestBodyStructureWithNonLatinCharacterSingleLineEncoded()
      {
         var messageText =
            "Message-ID: <1d11306c5648497247447e1073c3b0e2.squirrel@www.*******.**>\r\n" +
            "Date: Fri, 29 May 2009 11:53:03 +0200\r\n" +
            "Subject: attachment's name test\r\n" +
            "From: martin@hmailserver.com\r\n" +
            "To: martin@hmailserver.com\r\n" +
            "User-Agent: SquirrelMail/1.4.19\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/mixed;boundary=\"----=_20090529115303_60479\"\r\n" +
            "X-Priority: 3 (Normal)\r\n" +
            "Importance: Normal\r\n" +
            "\r\n" +
            "------=_20090529115303_60479\r\n" +
            "Content-Type: text/plain; charset=\"iso-8859-2\"\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "\r\n" +
            "test.±æê³ñó¶¼¿.txt\r\n" +
            "------=_20090529115303_60479\r\n" +
            "Content-Type: text/plain; name=\r\n" +
            "    =?iso-8859-2?Q?test.=B1=E6=EA=B3=F1=F3=B6=BC=BF.txt?=\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "Content-Disposition: attachment; filename=\"\r\n" +
            "    =?iso-8859-2?Q?test.=B1=E6=EA=B3=F1=F3=B6=BC=BF.txt?=\"\r\n" +
            "\r\n" +
            "1234\r\n" +
            "------=_20090529115303_60479--\r\n";


         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         Assert.IsTrue(result.Contains("(\"NAME\" \"=?iso-8859-2?Q?test.=B1=E6=EA=B3=F1=F3=B6=BC=BF.txt?=\")"));
         Assert.IsTrue(result.Contains("(\"FILENAME\" \"=?iso-8859-2?Q?test.=B1=E6=EA=B3=F1=F3=B6=BC=BF.txt?=\")"));
      }

      [Test]
      [Description(
         "Issue 218, IMAP: Problem with file name containing non-latin chars (RFC 2184 compliance). In this test, there are spaces in the 2184-encoded data."
      )]
      public void TestBodyStructureWithNonLatinCharacterSingleLineWithSpace()
      {
         var messageText =
            "Return-Path: martin@hmailserver.com\r\n" +
            "Delivered-To: martin@hmailserver.com\r\n" +
            "Received: from www.hmailserver.com ([127.0.0.1])\r\n" +
            "	by mail.hmailserver.com\r\n" +
            "	; Tue, 16 Jun 2009 21:39:18 +0200\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Date: Tue, 16 Jun 2009 21:39:18 +0200\r\n" +
            "From: <martin@hmailserver.com>\r\n" +
            "To: <martin@hmailserver.com>\r\n" +
            "Subject: sdafsda\r\n" +
            "Message-ID: <96aee740f2abe8450648c1752a9a987b@localhost>\r\n" +
            "X-Sender: martin@hmailserver.com\r\n" +
            "User-Agent: RoundCube Webmail/0.2.2\r\n" +
            "Content-Type: multipart/mixed;\r\n" +
            "	boundary=\"=_b63968892a76b1a5be17f4d37b085f54\"\r\n" +
            "\r\n" +
            "--=_b63968892a76b1a5be17f4d37b085f54\r\n" +
            "Content-Transfer-Encoding: 8bit\r\n" +
            "Content-Type: text/plain; charset=\"UTF-8\"\r\n" +
            "\r\n" +
            "--=_b63968892a76b1a5be17f4d37b085f54\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-Type: application/x-zip; charset=\"UTF-8\";\r\n" +
            " name*=\"UTF-8''m%C3%A4%C3%B6 m%C3%A4%C3%B6.zip\"; \r\n" +
            "Content-Disposition: attachment;\r\n" +
            " filename*=\"UTF-8''m%C3%A4%C3%B6 m%C3%A4%C3%B6.zip\"; \r\n" +
            "\r\n" +
            "iVBORw0KGgoAAAANSUhEUgAAAqgAAAH4CAIAAAAJvIhhAAAAAXNSR0IArs4c6QAAAARnQU1BAACx\r\n" +
            "jwv8YQUAAAAgY0hSTQAAeiYAAICEAAD6AAAAgOgAAHUwAADqYAAAOpgAABdwnLpRPAAAIr1JREFU\r\n" +
            "eF7t29GSYzluBND1/3/0ejb6wY6emK2WrpQEkaefdQvAAcn02OH/+fe///0v/wgQIECAAIESgb+C\r\n" +
            "3z8CBAgQIECgROBfJXMakwABAgQIEPjP/5qfAgECBAgQINAjIPh7dm1SAgQIECDgv/idAQIECBAg\r\n" +
            "--=_b63968892a76b1a5be17f4d37b085f54--\r\n" +
            "";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);


         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         Assert.IsFalse(result.Contains("''"), result);
         Assert.IsTrue(result.Contains("\"FILENAME\" \"=?UTF-8?Q?m=C3=A4=C3=B6 m=C3=A4=C3=B6.zip?=\""), result);
         Assert.IsTrue(result.Contains("\"NAME\" \"=?UTF-8?Q?m=C3=A4=C3=B6 m=C3=A4=C3=B6.zip?=\""), result);
      }

      [Test]
      [Description("Issue 218, IMAP: Problem with file name containing non-latin chars (RFC 2184 compliance)")]
      public void TestBodyStructureWithNonLatinCharacterTest2()
      {
         var messageText =
            "To: test@example.test\r\n" +
            "Content-Type: multipart/mixed;\r\n" +
            " boundary=\"------------000008080307000003010005\"\r\n" +
            "\r\n" +
            "This is a multi-part message in MIME format.\r\n" +
            "--------------000008080307000003010005\r\n" +
            "Content-Type: text/plain; charset=ISO-8859-1; format=flowed\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "\r\n" +
            "Test\r\n" +
            "\r\n" +
            "--------------000008080307000003010005\r\n" +
            "Content-Type: image/png;\r\n" +
            " name=\"=?ISO-8859-1?Q?=E9=2Epng?=\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-Disposition: inline;\r\n" +
            " filename*=ISO-8859-1''%E9%2E%70%6E%67\r\n" +
            "\r\n" +
            "iVBORw0KGgoAAAANSUhEUgAAAqgAAAH4CAIAAAAJvIhhAAAAAXNSR0IArs4c6QAAAARnQU1B\r\n" +
            "AACxjwv8YQUAAAAgY0hSTQAAeiYAAICEAAD6AAAAgOgAAHUwAADqYAAAOpgAABdwnLpRPAAA\r\n" +
            "--------------000008080307000003010005--\r\n";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         Assert.IsTrue(result.Contains("\"FILENAME\" \"=?ISO-8859-1?Q?=E9=2E=70=6E=67?=\""));
         Assert.IsTrue(result.Contains("\"NAME\" \"=?ISO-8859-1?Q?=E9=2E=70=6E=67?=\""));
      }

      [Test]
      [Description("Issue 218, IMAP: Problem with file name containing non-latin chars (RFC 2184 compliance)")]
      public void TestBodyStructureWithNonLatinCharacterTest3()
      {
         var messageText =
            "To: test@example.test\r\n" +
            "Content-Type: multipart/mixed;\r\n" +
            " boundary=\"------------000008080307000003010005\"\r\n" +
            "\r\n" +
            "This is a multi-part message in MIME format.\r\n" +
            "--------------000008080307000003010005\r\n" +
            "Content-Type: text/plain; charset=ISO-8859-1; format=flowed\r\n" +
            "Content-Transfer-Encoding: 7bit\r\n" +
            "\r\n" +
            "Test\r\n" +
            "\r\n" +
            "--------------000008080307000003010005\r\n" +
            "Content-Type: image/png;\r\n" +
            " name=\"=?ISO-8859-1?Q?=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=2E=70=6E=67?=\"\r\n" +
            "Content-Transfer-Encoding: base64\r\n" +
            "Content-Disposition: inline;\r\n" +
            " filename*=ISO-8859-1''%F6%50%C4%C9%CD%C1%D6%60%F6%F6%E4%27%2E%70%6E%67\r\n" +
            "\r\n" +
            "iVBORw0KGgoAAAANSUhEUgAAAqgAAAH4CAIAAAAJvIhhAAAAAXNSR0IArs4c6QAAAARnQU1B\r\n" +
            "AACxjwv8YQUAAAAgY0hSTQAAeiYAAICEAAD6AAAAgOgAAHUwAADqYAAAOpgAABdwnLpRPAAA\r\n" +
            "--------------000008080307000003010005--\r\n";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address, messageText);

         CustomAsserts.AssertFolderMessageCount(account.IMAPFolders[0], 1);

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         simulator.SelectFolder("INBOX");
         var result = simulator.Fetch("1 BODYSTRUCTURE");
         simulator.Disconnect();

         Assert.IsTrue(
            result.Contains("\"FILENAME\" \"=?ISO-8859-1?Q?=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=2E=70=6E=67?=\""));
         Assert.IsTrue(
            result.Contains("\"NAME\" \"=?ISO-8859-1?Q?=F6=50=C4=C9=CD=C1=D6=60=F6=F6=E4=27=2E=70=6E=67?=\""));
      }

      [Test]
      public void TestCapability()
      {
         var settings = _settings;

         settings.IMAPIdleEnabled = true;
         settings.IMAPQuotaEnabled = true;
         settings.IMAPSortEnabled = true;

         var simulator = new ImapClientSimulator();
         simulator.Connect();

         var sCapabilities = simulator.GetCapabilities();

         if (sCapabilities.IndexOf(" IDLE") == -1 ||
             sCapabilities.IndexOf(" QUOTA") == -1 ||
             sCapabilities.IndexOf(" SORT") == -1)
            throw new Exception("ERROR - Wrong IMAP CAPABILITY.");

         settings.IMAPIdleEnabled = false;
         settings.IMAPQuotaEnabled = true;
         settings.IMAPSortEnabled = true;
         sCapabilities = simulator.GetCapabilities();

         if (sCapabilities.IndexOf(" IDLE") != -1 ||
             sCapabilities.IndexOf(" QUOTA") == -1 ||
             sCapabilities.IndexOf(" SORT") == -1)
            throw new Exception("ERROR - Wrong IMAP CAPABILITY.");

         settings.IMAPIdleEnabled = false;
         settings.IMAPQuotaEnabled = false;
         settings.IMAPSortEnabled = true;
         sCapabilities = simulator.GetCapabilities();

         if (sCapabilities.IndexOf(" IDLE") != -1 ||
             sCapabilities.IndexOf(" QUOTA") != -1 ||
             sCapabilities.IndexOf(" SORT") == -1)
            throw new Exception("ERROR - Wrong IMAP CAPABILITY.");

         settings.IMAPIdleEnabled = false;
         settings.IMAPQuotaEnabled = false;
         settings.IMAPSortEnabled = false;
         sCapabilities = simulator.GetCapabilities();

         if (sCapabilities.IndexOf(" IDLE") != -1 ||
             sCapabilities.IndexOf(" QUOTA") != -1 ||
             sCapabilities.IndexOf(" SORT") != -1)
            throw new Exception("ERROR - Wrong IMAP CAPABILITY.");

         settings.IMAPIdleEnabled = true;
         settings.IMAPQuotaEnabled = false;
         settings.IMAPSortEnabled = false;
         sCapabilities = simulator.GetCapabilities();

         if (sCapabilities.IndexOf(" IDLE") == -1 ||
             sCapabilities.IndexOf(" QUOTA") != -1 ||
             sCapabilities.IndexOf(" SORT") != -1)
            throw new Exception("ERROR - Wrong IMAP CAPABILITY.");

         settings.IMAPIdleEnabled = true;
         settings.IMAPQuotaEnabled = true;
         settings.IMAPSortEnabled = false;
         sCapabilities = simulator.GetCapabilities();

         if (sCapabilities.IndexOf(" IDLE") == -1 ||
             sCapabilities.IndexOf(" QUOTA") == -1 ||
             sCapabilities.IndexOf(" SORT") != -1)
            throw new Exception("ERROR - Wrong IMAP CAPABILITY.");

         settings.IMAPACLEnabled = true;

         sCapabilities = simulator.GetCapabilities();
         Assert.IsTrue(sCapabilities.Contains(" ACL"));

         settings.IMAPACLEnabled = false;

         sCapabilities = simulator.GetCapabilities();
         Assert.IsFalse(sCapabilities.Contains(" ACL"));
      }

      [Test]
      public void TestCheck()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "check@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("check@example.test", "test");
         Assert.IsTrue(simulator.CreateFolder("TestFolder"));
         Assert.IsTrue(simulator.CheckFolder("TestFolder"));
         simulator.Disconnect();
      }

      [Test]
      public void TestClose()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "close@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("close@example.test", "test");
         Assert.IsFalse(simulator.Close());
         Assert.IsTrue(simulator.CreateFolder("TestFolder.Sub1"));
         Assert.IsTrue(simulator.SelectFolder("TestFolder.Sub1"));
         Assert.IsTrue(simulator.Close());
         simulator.Disconnect();
      }

      [Test]
      [Description(
         "Make sure that the connection object is released after folder is selected, idle mode is switched on and connection is closed."
      )]
      public void TestConnectionObjectRelease()
      {
         LogHandler.DeleteCurrentDefaultLog();

         _settings.IMAPIdleEnabled = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "idleaccount@example.test", "test");

         var simulator = new ImapClientSimulator();

         string data;

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));
         Assert.IsTrue(simulator.StartIdle());
         Assert.IsTrue(simulator.EndIdle(true, out data));
         Assert.IsTrue(simulator.Logout());

         var logData = LogHandler.ReadCurrentDefaultLog();

         Assert.IsTrue(LogHandler.DefaultLogContains("Ending session"));
      }

      [Test]
      public void TestDelete()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delete@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("delete@example.test", "test");
         Assert.IsFalse(simulator.DeleteFolder("DoesNotExist"));
         Assert.IsTrue(simulator.CreateFolder("DoesExist"));
         Assert.IsTrue(simulator.SelectFolder("DoesExist"));
         simulator.Close();
         Assert.IsTrue(simulator.DeleteFolder("DoesExist"));
         Assert.IsFalse(simulator.SelectFolder("DoesExist"));
      }

      [Test]
      [Description("Test that deleting an IMAP folder does not stop notifications from working. (5.0 Build 315 Bug)")]
      public void TestDeleteIMAPFolderNotifications()
      {
         _settings.IMAPIdleEnabled = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "idleaccount@example.test", "test");

         var imapClientSimulator = new ImapClientSimulator();
         var simulator2 = new ImapClientSimulator();
         imapClientSimulator.ConnectAndLogon(account.Address, "test");
         simulator2.ConnectAndLogon(account.Address, "test");

         imapClientSimulator.SelectFolder("Inbox");
         simulator2.CreateFolder("Mailbox");
         simulator2.DeleteFolder("Mailbox");

         SmtpClientSimulator.StaticSend("test@example.test", account.Address, "Test", "test");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var noopResponse = imapClientSimulator.NOOP() + imapClientSimulator.NOOP();

         // confirm that the client is notified about this message even though another
         // folder has been dropped by another client.
         Assert.IsTrue(noopResponse.Contains(@"* 1 EXISTS"), noopResponse);
      }


      [Test]
      public void TestExamineNonexistantFolder()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delete@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("delete@example.test", "test");
         var result = simulator.ExamineFolder("NonexistantFolder");

         Assert.IsTrue(result.Contains("BAD Folder could not be found."));
      }

      [Test]
      [Description("Issue 294, IMAP: Incomplete SELECT response")]
      public void TestExamineResponse()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "testselect@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Test");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("testselect@example.test", "test");
         var result = simulator.ExamineFolder("Inbox");
         simulator.Disconnect();

         Assert.IsTrue(result.Contains("* FLAGS"), result);
         Assert.IsTrue(result.Contains("* 1 EXISTS"), result);
         Assert.IsTrue(result.Contains("* 1 RECENT"), result);
         Assert.IsTrue(result.Contains("* OK [UNSEEN 1]"), result);
         Assert.IsTrue(result.Contains("* OK [PERMANENTFLAGS"), result);
         Assert.IsTrue(result.Contains("* OK [UIDNEXT 2]"), result);
         Assert.IsTrue(result.Contains("* OK [UIDVALIDITY"), result);
         Assert.IsTrue(result.Contains("OK [READ-ONLY]"), result);
      }

      [Test]
      [Description("Assert that the EXPUNGE command works")]
      public void TestExpunge()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "ExpungeAccount@example.test",
            "test");

         for (var i = 0; i < 3; i++)
            SmtpClientSimulator.StaticSend("test@example.test", account.Address, "Test", "test");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("Inbox"));

         Assert.IsTrue(simulator.SetFlagOnMessage(1, true, @"\Deleted"));
         Assert.IsTrue(simulator.SetFlagOnMessage(3, true, @"\Deleted"));

         string result;
         Assert.IsTrue(simulator.Expunge(out result));

         // Messages 1 and 2 should be deleted. 2, because when the first message
         // is deleted, the index of the message which was originally 3, is now 2.
         Assert.IsTrue(result.Contains("* 1 EXPUNGE\r\n* 2 EXPUNGE"));
      }

      [Test]
      [Description("Assert that when one client deletes a message, others are notified - even if IDLE isn't used.")]
      public void TestExpungeNotification()
      {
         _settings.IMAPIdleEnabled = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "idleaccount@example.test", "test");

         for (var i = 0; i < 5; i++)
            SmtpClientSimulator.StaticSend("test@example.test", account.Address, "Test", "test");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 5);

         var imapClientSimulator = new ImapClientSimulator();
         var simulator2 = new ImapClientSimulator();
         imapClientSimulator.ConnectAndLogon(account.Address, "test");
         simulator2.ConnectAndLogon(account.Address, "test");

         imapClientSimulator.SelectFolder("Inbox");
         simulator2.SelectFolder("Inbox");

         for (var i = 1; i <= 5; i++) Assert.IsTrue(imapClientSimulator.SetFlagOnMessage(i, true, @"\Deleted"));

         var noopResponse = simulator2.NOOP() + simulator2.NOOP();

         Assert.IsTrue(noopResponse.Contains(@"* 1 FETCH (FLAGS (\Deleted)") &&
                       noopResponse.Contains(@"* 1 FETCH (FLAGS (\Deleted)") &&
                       noopResponse.Contains(@"* 1 FETCH (FLAGS (\Deleted)") &&
                       noopResponse.Contains(@"* 1 FETCH (FLAGS (\Deleted)") &&
                       noopResponse.Contains(@"* 1 FETCH (FLAGS (\Deleted)"), noopResponse);

         var result = imapClientSimulator.Expunge();

         var expungeResult = simulator2.NOOP() + simulator2.NOOP();

         Assert.IsTrue(
            expungeResult.Contains("* 1 EXPUNGE\r\n* 1 EXPUNGE\r\n* 1 EXPUNGE\r\n* 1 EXPUNGE\r\n* 1 EXPUNGE"),
            expungeResult);
      }

      [Test]
      public void TestFolderExpungeNotification()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "shared@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "TestSubject", "TestBody");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var simulator1 = new ImapClientSimulator();
         var simulator2 = new ImapClientSimulator();

         simulator1.ConnectAndLogon(account.Address, "test");
         simulator2.ConnectAndLogon(account.Address, "test");

         simulator1.SelectFolder("Inbox");
         simulator2.SelectFolder("Inbox");

         var result = simulator2.NOOP();
         Assert.IsFalse(result.Contains("Deleted"));
         Assert.IsFalse(result.Contains("Seen"));

         simulator1.SetDeletedFlag(1);
         simulator1.Expunge();

         // the result may (should) come after the first NOOP response stream so do noop twice.
         result = simulator2.NOOP() + simulator2.NOOP();
         Assert.IsTrue(result.Contains("* 1 EXPUNGE"));

         simulator1.Disconnect();
         simulator2.Disconnect();
      }

      [Test]
      public void TestFolderNamesWithUnicodeChars()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.LogonWithLiteral("folder@example.test", "test");
         simulator.Send("A50 CREATE &AMQAxADEAMQAxADEAMQAxADEAMQAxADEAMQ-\r\n");
         simulator.Disconnect();

         var s = account.IMAPFolders[1].Name;
         Assert.AreEqual("ÄÄÄÄÄÄÄÄÄÄÄÄÄ", s);
      }

      [Test]
      public void TestFolderUpdateNotification()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "shared@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "TestSubject", "TestBody");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);


         var simulator1 = new ImapClientSimulator();
         var simulator2 = new ImapClientSimulator();

         simulator1.ConnectAndLogon(account.Address, "test");
         simulator2.ConnectAndLogon(account.Address, "test");

         simulator1.SelectFolder("Inbox");
         simulator2.SelectFolder("Inbox");

         var result = simulator2.NOOP() + simulator2.NOOP();
         Assert.IsFalse(result.Contains("Deleted"));
         Assert.IsFalse(result.Contains("Seen"));

         simulator1.SetDeletedFlag(1);
         simulator1.SetSeenFlag(1);

         result = simulator2.NOOP() + simulator2.NOOP();
         Assert.IsTrue(result.Contains("Deleted"));
         Assert.IsTrue(result.Contains("Seen"));

         simulator1.Disconnect();
         simulator2.Disconnect();
      }

      [Test]
      public void TestGetQuota()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "imapaccount@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("imapaccount@example.test", "test");
         var result = simulator.GetQuota("Inbox");
         Assert.IsTrue(result.Contains("A09 OK"));
         simulator.Disconnect();
      }

      [Test]
      public void TestIdle()
      {
         _settings.IMAPIdleEnabled = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "idleaccount@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         simulator.StartIdle();

         if (simulator.GetPendingDataExists())
            throw new Exception("Unexpected data exists");

         // Send a message to this account.
         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send(account.Address, account.Address, "IDLE Test", "This is a test of IDLE");

         string data;
         Assert.IsTrue(simulator.EndIdle(false, out data));
      }

      [Test]
      public void TestLSUB()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "list@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.LogonWithLiteral("list@example.test", "test");
         var result = simulator.List().Substring(0, 1);
         Assert.AreEqual("*", result);
      }

      [Test]
      public void TestList()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "list@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("list@example.test", "test");
         var result = simulator.Send("A01 LIST \"\" \"*\"\r\n").Substring(0, 1);
         Assert.AreEqual("*", result);
      }

      [Test]
      public void TestLiteralSupport()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "literal@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.LogonWithLiteral("literal@example.test", "test");
         simulator.Disconnect();
      }

      [Test]
      public void TestLiterals()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("folder@example.test", "test");
         var result = simulator.Send("A01 CREATE {4}");
         result = simulator.Send("HEJS");

         simulator.Disconnect();
      }


      [Test]
      public void TestLogin()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "imapaccount@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("imapaccount@example.test", "test");
         simulator.Disconnect();
      }

      [Test]
      public void TestNamespace()
      {
         var imapFolderName = _settings.IMAPPublicFolderName;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delete@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("delete@example.test", "test");
         var result = simulator.Send("A01 NAMESPACE");

         var correctNamespaceSetting = "* NAMESPACE ((\"\" \".\")) NIL ((\"" + imapFolderName + "\" \".\"))";

         if (!result.Contains(correctNamespaceSetting)) Assert.Fail("Namespace failed");
      }


      /// <summary>
      ///    Test that when you delete a message using POP3, IMAP notifications are sent.
      /// </summary>
      [Test]
      public void TestNotificationOnPOP3Deletion()
      {
         _settings.IMAPIdleEnabled = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "idleaccount@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Message 1", "Body 1");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Message 1", "Body 1");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 2);

         var imapSimulator = new ImapClientSimulator();
         var sWelcomeMessage = imapSimulator.Connect();
         Assert.IsTrue(imapSimulator.Logon("idleaccount@example.test", "test"));
         Assert.IsTrue(imapSimulator.SelectFolder("INBOX"));
         Assert.IsTrue(imapSimulator.StartIdle());

         var sim = new Pop3ClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(sim.DELE(1));
         sim.QUIT();

         // After a delete, the following should be sent tot he IMAP client:
         //  - EXPUNGE
         //  - EXISTS
         //  - RECENT
         Assert.IsTrue(imapSimulator.AssertPendingDataExists(), "No pending data exist");

         var deadline = DateTime.Now.AddSeconds(10);
         var message = new StringBuilder();

         while (DateTime.Now < deadline)
         {
            if (imapSimulator.GetPendingDataExists())
               message.Append(imapSimulator.Receive());

            var str = message.ToString();

            if (str.Contains("* 1 EXPUNGE") &&
                str.Contains("EXISTS") &&
                str.Contains("RECENT"))
               return;
         }

         var receivedText = message.ToString();
         Assert.IsTrue(receivedText.Contains("* 1 EXPUNGE"), receivedText);
         Assert.IsTrue(receivedText.Contains("EXISTS"), receivedText);
         Assert.IsTrue(receivedText.Contains("RECENT"), receivedText);
      }


      [Test]
      public void TestOutOfBounds()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "outofbounds@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("outofbounds@example.test", "test");

         var s = simulator.Send("A01 RENAME TEST");
         if (s.StartsWith("A01 BAD") == false)
            throw new Exception("ERROR - Out of bounds test failed");
      }

      [Test]
      public void TestPublicFolderUpdateNotification()
      {
         var folders = _application.Settings.PublicFolders;
         var folder = folders.Add("Share");
         folder.Save();

         var permission = folder.Permissions.Add();
         permission.PermissionType = eACLPermissionType.ePermissionTypeAnyone;
         permission.set_Permission(eACLPermission.ePermissionLookup, true);
         permission.set_Permission(eACLPermission.ePermissionRead, true);
         permission.set_Permission(eACLPermission.ePermissionWriteOthers, true);
         permission.set_Permission(eACLPermission.ePermissionWriteSeen, true);
         permission.set_Permission(eACLPermission.ePermissionWriteDeleted, true);
         permission.set_Permission(eACLPermission.ePermissionInsert, true);
         permission.Save();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "shared@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "TestSubject", "TestBody");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var simulator1 = new ImapClientSimulator();
         var simulator2 = new ImapClientSimulator();

         simulator1.ConnectAndLogon(account.Address, "test");
         simulator2.ConnectAndLogon(account.Address, "test");

         simulator1.SelectFolder("Inbox");
         simulator2.SelectFolder("Inbox");

         Assert.IsTrue(simulator1.Copy(1, "#Public.Share"));

         simulator1.SelectFolder("#Public.Share");
         simulator2.SelectFolder("#Public.Share");

         var result = simulator2.NOOP() + simulator2.NOOP();
         Assert.IsFalse(result.Contains("Deleted"));
         Assert.IsFalse(result.Contains("Seen"));

         simulator1.SetDeletedFlag(1);
         simulator1.SetSeenFlag(1);

         result = simulator2.NOOP() + simulator2.NOOP();
         Assert.IsTrue(result.Contains("Deleted"));
         Assert.IsTrue(result.Contains("Seen"));

         simulator1.Disconnect();
         simulator2.Disconnect();
      }

      [Test]
      [Description("Issue 244, Recent flag not removed properly.")]
      public void TestRecentRemovedOnMailboxChange()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestMessage");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(sim.SelectFolder("Inbox"));
         Assert.IsTrue(sim.CreateFolder("Dummy"));
         Assert.IsTrue(sim.Copy(1, "Dummy"));
         var result = sim.SendSingleCommand("a01 select Dummy");
         Assert.IsTrue(result.Contains("* 1 EXISTS\r\n* 1 RECENT"), result);
         Assert.IsTrue(sim.SelectFolder("Inbox"));

         // Make sure that when we switched back to the Inbox, the Recent flag was removed.
         result = sim.SendSingleCommand("a01 select Dummy");
         Assert.IsFalse(result.Contains("* 1 EXISTS\r\n* 1 RECENT"), result);
         Assert.IsTrue(sim.Logout());
      }

      [Test]
      [Description("Issue 244, Recent flag not removed properly")]
      public void TestRecentRemovedOnMailboxClose()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestMessage");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(sim.SelectFolder("Inbox"));
         Assert.IsTrue(sim.CreateFolder("Dummy"));
         Assert.IsTrue(sim.Copy(1, "Dummy"));
         var result = sim.SendSingleCommand("a01 select Dummy");
         Assert.IsTrue(result.Contains("* 1 EXISTS\r\n* 1 RECENT"), result);
         Assert.IsTrue(sim.Logout());

         sim = new ImapClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         result = sim.SendSingleCommand("a01 select Dummy");
         Assert.IsFalse(result.Contains("* 1 EXISTS\r\n* 1 RECENT"), result);
         Assert.IsTrue(sim.Logout());
      }

      [Test]
      public void TestRename()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("folder@example.test", "test");

         Assert.IsTrue(simulator.CreateFolder("Root1"));
         Assert.IsTrue(simulator.CreateFolder("Root1.Sub1"));
         Assert.IsTrue(simulator.CreateFolder("Root1.Sub2"));
         Assert.IsTrue(simulator.CreateFolder("Root1.Sub3"));
         Assert.IsTrue(simulator.SelectFolder("Root1"));
         Assert.IsTrue(simulator.SelectFolder("Root1.Sub1"));
         Assert.IsTrue(simulator.SelectFolder("Root1.Sub2"));
         Assert.IsTrue(simulator.SelectFolder("Root1.Sub3"));
         Assert.IsTrue(simulator.RenameFolder("Root1", "Root2"));
         Assert.IsTrue(simulator.SelectFolder("Root2"));
         Assert.IsTrue(simulator.SelectFolder("Root2.Sub1"));
         Assert.IsTrue(simulator.SelectFolder("Root2.Sub2"));
         Assert.IsTrue(simulator.SelectFolder("Root2.Sub3"));
         simulator.Disconnect();
      }

      [Test]
      public void TestRenameAndList()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("folder@example.test", "test");
         Assert.IsTrue(simulator.CreateFolder("Root1"));
         Assert.IsTrue(simulator.CreateFolder("Root2"));
         Assert.IsTrue(simulator.RenameFolder("Root2", "Root1.Root2"));

         var result = simulator.List();

         Assert.IsTrue(result.Contains("Root1.Root2"));

         simulator.Disconnect();
      }


      [Test]
      public void TestRenameAndList2()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("folder@example.test", "test");
         simulator.CreateFolder("Root1");
         simulator.CreateFolder("Root2");
         simulator.CreateFolder("Root3");

         simulator.RenameFolder("Root2", "Root1.Root2");
         simulator.RenameFolder("Root3", "Root1.Root2.Root3");

         var result = simulator.List();

         Assert.IsTrue(result.Contains("Root1\"\r\n"));
         Assert.IsTrue(result.Contains("Root1.Root2\"\r\n"));
         Assert.IsTrue(result.Contains("Root1.Root2.Root3\"\r\n"));

         simulator.Disconnect();
      }

      [Test]
      public void TestRenameAndList3()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("folder@example.test", "test");
         simulator.CreateFolder("Root1");
         simulator.CreateFolder("Root2");
         simulator.CreateFolder("Root3");

         simulator.RenameFolder("Root2", "Root1.Root2");
         simulator.RenameFolder("Root3", "Root1.Root2.Root3");
         simulator.RenameFolder("Root1.Root2.Root3", "Test");

         var result = simulator.List();

         Assert.IsTrue(result.Contains("Root1\"\r\n"));
         Assert.IsTrue(result.Contains("Root1.Root2\"\r\n"));
         Assert.IsTrue(result.Contains("Test\"\r\n"));

         simulator.Disconnect();
      }

      [Test]
      public void TestRenameAndList4()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("folder@example.test", "test");
         simulator.CreateFolder("Root1");
         simulator.CreateFolder("Root2.Root3");

         simulator.RenameFolder("Root2.Root3", "Root2.Root4");

         var result = simulator.List();

         Assert.IsTrue(result.Contains("Root1\"\r\n"));
         Assert.IsTrue(result.Contains("Root2.Root4\"\r\n"));

         simulator.Disconnect();
      }

      [Test]
      public void TestRenameAndList5()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "folder@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("folder@example.test", "test");
         simulator.CreateFolder("Root1");
         simulator.CreateFolder("Root2.Root3");

         simulator.RenameFolder("Root2.Root3", "Root2.Root4");
         simulator.RenameFolder("Root1", "Root2.Root4.Root1");

         var result = simulator.List();

         Assert.IsFalse(result.Contains(" Root1\r\n"));
         Assert.IsTrue(result.Contains("Root2.Root4.Root1\"\r\n"));

         Assert.IsFalse(simulator.SelectFolder("Root1"));
         Assert.IsTrue(simulator.SelectFolder("Root2.Root4.Root1"));

         simulator.Disconnect();
      }

      [Test]
      [Description("Test that renaming an IMAP folder does not stop notifications from working.")]
      public void TestRenameIMAPFolderNotifications()
      {
         _settings.IMAPIdleEnabled = true;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "idleaccount@example.test", "test");

         var imapClientSimulator = new ImapClientSimulator();
         var simulator2 = new ImapClientSimulator();
         imapClientSimulator.ConnectAndLogon(account.Address, "test");
         simulator2.ConnectAndLogon(account.Address, "test");

         imapClientSimulator.SelectFolder("Inbox");
         simulator2.CreateFolder("Mailbox");
         simulator2.RenameFolder("Mailbox", "Mailbox2");

         SmtpClientSimulator.StaticSend("test@example.test", account.Address, "Test", "test");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var noopResponse = imapClientSimulator.NOOP() + imapClientSimulator.NOOP();

         // confirm that the client is notified about this message even though another
         // folder has been dropped by another client.
         Assert.IsTrue(noopResponse.Contains(@"* 1 EXISTS"), noopResponse);
      }

      [Test]
      [Description("Issue 294, IMAP: Incomplete SELECT response")]
      public void TestSelectResponse()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "testselect@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "Test");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("testselect@example.test", "test");
         var result = string.Empty;
         simulator.SelectFolder("Inbox", out result);
         simulator.Disconnect();

         Assert.IsTrue(result.Contains("* FLAGS"), result);
         Assert.IsTrue(result.Contains("* 1 EXISTS"), result);
         Assert.IsTrue(result.Contains("* 1 RECENT"), result);
         Assert.IsTrue(result.Contains("* OK [UNSEEN 1]"), result);
         Assert.IsTrue(result.Contains("* OK [PERMANENTFLAGS"), result);
         Assert.IsTrue(result.Contains("* OK [UIDNEXT 2]"), result);
         Assert.IsTrue(result.Contains("* OK [UIDVALIDITY"), result);
         Assert.IsTrue(result.Contains("OK [READ-WRITE]"), result);
      }

      [Test]
      public void TestStatus()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "imapaccount@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("imapaccount@example.test", "test");
         Assert.IsTrue(simulator.Status("Inbox", "MESSAGES").Contains("A08 OK"));
         simulator.Disconnect();
      }

      [Test]
      public void TestSubscribe()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delete@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("delete@example.test", "test");
         Assert.IsTrue(simulator.CreateFolder("TestFolder1"));
         Assert.IsTrue(simulator.CreateFolder("TestFolder2"));
         Assert.IsTrue(simulator.CreateFolder("TestFolder3"));

         if (simulator.Subscribe("Vaffe"))
            Assert.Fail("Subscribe on non-existent folder succeeded");

         if (!simulator.Subscribe("TestFolder1"))
            Assert.Fail("Subscribe on existent folder failed");
         if (!simulator.Subscribe("TestFolder2"))
            Assert.Fail("Subscribe on existent folder failed");
         if (!simulator.Subscribe("TestFolder3"))
            Assert.Fail("Subscribe on existent folder failed");
      }


      [Test]
      [Description("Test that the SELECT response gives the correct unseen count.")]
      public void TestUnseenResponseInSelect()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestMessage");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(sim.SelectFolder("Inbox"));
         Assert.IsTrue(sim.CreateFolder("Dummy"));
         Assert.IsTrue(sim.Copy(1, "Dummy"));

         var result = sim.SendSingleCommand("a01 select Dummy");
         Assert.IsTrue(result.Contains("* 1 EXISTS\r\n* 1 RECENT"), result);

         var searchResponse = sim.SendSingleCommand("srch1 SEARCH ALL UNSEEN");

         // We should have at least one message here.
         Assert.IsTrue(searchResponse.Contains("* SEARCH 1\r\n"), searchResponse);

         // Now fetch the body.
         var bodyText = sim.Fetch("1 BODY[TEXT]");

         // Now the message is no longer unseen. Confirm this.
         searchResponse = sim.SendSingleCommand("srch1 SEARCH ALL UNSEEN");
         Assert.IsTrue(searchResponse.Contains("* SEARCH\r\n"), searchResponse);

         // Close the messages to mark them as no longer recent.
         Assert.IsTrue(sim.Close());

         result = sim.SendSingleCommand("a01 select Dummy");
         Assert.IsTrue(result.Contains("* 1 EXISTS\r\n* 0 RECENT"), result);
      }

      [Test]
      public void TestUnsubscribe()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delete@example.test", "test");

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();
         simulator.Logon("delete@example.test", "test");
         Assert.IsTrue(simulator.CreateFolder("TestFolder1"));
         Assert.IsTrue(simulator.CreateFolder("TestFolder2"));

         if (!simulator.Subscribe("TestFolder1"))
            Assert.Fail("Subscribe on existent folder failed");
         if (!simulator.Subscribe("TestFolder2"))
            Assert.Fail("Subscribe on existent folder failed");

         if (!simulator.Unsubscribe("TestFolder1"))
            Assert.Fail("Unsubscribe on existent folder failed");
         if (!simulator.Unsubscribe("TestFolder2"))
            Assert.Fail("Unsubscribe on existent folder failed");
      }

      [Test]
      public void TestWelcomeMessage()
      {
         _settings.WelcomeIMAP = "HOWDYHO IMAP";

         var simulator = new ImapClientSimulator();

         var sWelcomeMessage = simulator.Connect();

         simulator.Disconnect();

         if (sWelcomeMessage != "* OK HOWDYHO IMAP\r\n")
            throw new Exception("ERROR - Wrong welcome message.");
      }
   }

   /// <summary>
   ///    A minimal SCRAM-SHA-256 (RFC 5802 / RFC 7677) client used to exercise the
   ///    server's AUTHENTICATE SCRAM-SHA-256 implementation over a raw connection.
   /// </summary>
   internal static class ScramTestClient
   {
      /// <summary>
      ///    Runs a full SCRAM-SHA-256 exchange (no SASL-IR) on an already-connected,
      ///    post-banner IMAP connection. Returns the server's final response line:
      ///    the tagged OK on success, or the tagged NO/BAD on a rejected proof.
      /// </summary>
      public static string Authenticate(TcpConnection con, string tag, string username, string password)
      {
         var nonceBytes = new byte[18];
         using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(nonceBytes);
         string clientNonce = Convert.ToBase64String(nonceBytes);

         string clientFirstBare = "n=" + SaslName(username) + ",r=" + clientNonce;
         string clientFirst = "n,," + clientFirstBare;

         // Mechanism selection -> empty server challenge.
         con.Send(tag + " AUTHENTICATE SCRAM-SHA-256\r\n");
         con.ReadUntil("+");

         // client-first -> server-first.
         con.Send(Base64(clientFirst) + "\r\n");
         string serverFirst = DecodeContinuation(con.ReadUntil("\r\n"));

         string combinedNonce = Attribute(serverFirst, "r");
         byte[] salt = Convert.FromBase64String(Attribute(serverFirst, "s"));
         int iterations = int.Parse(Attribute(serverFirst, "i"));
         Assert.IsTrue(combinedNonce.StartsWith(clientNonce),
            "Server nonce must start with the client nonce. Got: " + combinedNonce);

         byte[] saltedPassword;
         using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            saltedPassword = pbkdf2.GetBytes(32);

         byte[] clientKey = Hmac(saltedPassword, "Client Key");
         byte[] storedKey = Sha256(clientKey);

         string clientFinalWithoutProof = "c=biws,r=" + combinedNonce;
         string authMessage = clientFirstBare + "," + serverFirst + "," + clientFinalWithoutProof;
         byte[] clientSignature = Hmac(storedKey, authMessage);
         byte[] clientProof = Xor(clientKey, clientSignature);

         string clientFinal = clientFinalWithoutProof + ",p=" + Convert.ToBase64String(clientProof);
         con.Send(Base64(clientFinal) + "\r\n");

         string afterFinal = con.ReadUntil("\r\n");
         if (!afterFinal.TrimStart().StartsWith("+"))
            return afterFinal; // rejected proof: tagged NO/BAD

         // Verify the server proved knowledge of the key (ServerSignature).
         string serverFinal = DecodeContinuation(afterFinal);
         byte[] serverKey = Hmac(saltedPassword, "Server Key");
         byte[] serverSignature = Hmac(serverKey, authMessage);
         Assert.AreEqual("v=" + Convert.ToBase64String(serverSignature), serverFinal,
            "Server signature (v=) did not verify.");

         // Empty client response acknowledges the server-final; server completes auth.
         con.Send("\r\n");
         return con.ReadUntil(tag);
      }

      /// <summary>
      ///    Sends only the SCRAM client-first message and returns the base64 salt (s=)
      ///    the server offers in its server-first reply, then abandons the exchange.
      ///    Used to verify the anti-enumeration salt for an unknown account is stable.
      /// </summary>
      public static string ProbeServerSalt(TcpConnection con, string tag, string username)
      {
         var nonceBytes = new byte[18];
         using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(nonceBytes);
         string clientNonce = Convert.ToBase64String(nonceBytes);

         string clientFirstBare = "n=" + SaslName(username) + ",r=" + clientNonce;
         string clientFirst = "n,," + clientFirstBare;

         con.Send(tag + " AUTHENTICATE SCRAM-SHA-256\r\n");
         con.ReadUntil("+");

         con.Send(Base64(clientFirst) + "\r\n");
         string serverFirst = DecodeContinuation(con.ReadUntil("\r\n"));
         return Attribute(serverFirst, "s");
      }

      /// <summary>
      ///    Runs a full SCRAM-SHA-256-PLUS exchange (RFC 5802 / RFC 5929) on an
      ///    already-connected, post-banner TLS IMAP connection. channelBindingData is
      ///    the tls-server-end-point hash of the server certificate. Returns the
      ///    server's final response line: the tagged OK on success, or the tagged
      ///    NO/BAD on a rejected proof or channel-binding mismatch.
      /// </summary>
      public static string AuthenticatePlus(TcpConnection con, string tag, string username, string password,
         byte[] channelBindingData)
      {
         var nonceBytes = new byte[18];
         using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(nonceBytes);
         string clientNonce = Convert.ToBase64String(nonceBytes);

         const string gs2Header = "p=tls-server-end-point,,";
         string clientFirstBare = "n=" + SaslName(username) + ",r=" + clientNonce;
         string clientFirst = gs2Header + clientFirstBare;

         con.Send(tag + " AUTHENTICATE SCRAM-SHA-256-PLUS\r\n");
         con.ReadUntil("+");

         con.Send(Base64(clientFirst) + "\r\n");
         string serverFirst = DecodeContinuation(con.ReadUntil("\r\n"));

         string combinedNonce = Attribute(serverFirst, "r");
         byte[] salt = Convert.FromBase64String(Attribute(serverFirst, "s"));
         int iterations = int.Parse(Attribute(serverFirst, "i"));
         Assert.IsTrue(combinedNonce.StartsWith(clientNonce),
            "Server nonce must start with the client nonce. Got: " + combinedNonce);

         byte[] saltedPassword;
         using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
            saltedPassword = pbkdf2.GetBytes(32);

         byte[] clientKey = Hmac(saltedPassword, "Client Key");
         byte[] storedKey = Sha256(clientKey);

         // c= is base64( gs2-header-bytes || channel-binding-data ).
         byte[] gs2Bytes = Encoding.UTF8.GetBytes(gs2Header);
         var cbindInput = new byte[gs2Bytes.Length + channelBindingData.Length];
         Buffer.BlockCopy(gs2Bytes, 0, cbindInput, 0, gs2Bytes.Length);
         Buffer.BlockCopy(channelBindingData, 0, cbindInput, gs2Bytes.Length, channelBindingData.Length);
         string cbind = Convert.ToBase64String(cbindInput);

         string clientFinalWithoutProof = "c=" + cbind + ",r=" + combinedNonce;
         string authMessage = clientFirstBare + "," + serverFirst + "," + clientFinalWithoutProof;
         byte[] clientSignature = Hmac(storedKey, authMessage);
         byte[] clientProof = Xor(clientKey, clientSignature);

         string clientFinal = clientFinalWithoutProof + ",p=" + Convert.ToBase64String(clientProof);
         con.Send(Base64(clientFinal) + "\r\n");

         string afterFinal = con.ReadUntil("\r\n");
         if (!afterFinal.TrimStart().StartsWith("+"))
            return afterFinal; // rejected proof / channel-binding mismatch: tagged NO/BAD

         // Verify the server proved knowledge of the key (ServerSignature).
         string serverFinal = DecodeContinuation(afterFinal);
         byte[] serverKey = Hmac(saltedPassword, "Server Key");
         byte[] serverSignature = Hmac(serverKey, authMessage);
         Assert.AreEqual("v=" + Convert.ToBase64String(serverSignature), serverFinal,
            "Server signature (v=) did not verify.");

         // Empty client response acknowledges the server-final; server completes auth.
         con.Send("\r\n");
         return con.ReadUntil(tag);
      }

      private static string SaslName(string name)
      {
         // saslname: '=' must be escaped before ',' so an escaped '=' is not re-encoded.
         return name.Replace("=", "=3D").Replace(",", "=2C");
      }

      private static string Base64(string s)
      {
         return Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
      }

      private static string DecodeContinuation(string line)
      {
         string t = line.Trim();
         if (t.StartsWith("+"))
            t = t.Substring(1).Trim();
         return Encoding.UTF8.GetString(Convert.FromBase64String(t));
      }

      private static string Attribute(string message, string key)
      {
         foreach (var part in message.Split(','))
            if (part.StartsWith(key + "="))
               return part.Substring(key.Length + 1);
         return "";
      }

      private static byte[] Hmac(byte[] key, string data)
      {
         using (var h = new HMACSHA256(key))
            return h.ComputeHash(Encoding.ASCII.GetBytes(data));
      }

      private static byte[] Sha256(byte[] data)
      {
         using (var sha = SHA256.Create())
            return sha.ComputeHash(data);
      }

      private static byte[] Xor(byte[] a, byte[] b)
      {
         var r = new byte[a.Length];
         for (int i = 0; i < a.Length; i++)
            r[i] = (byte) (a[i] ^ b[i]);
         return r;
      }
   }
}