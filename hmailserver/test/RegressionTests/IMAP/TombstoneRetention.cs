// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    Capping hm_imapexpunged, the table of QRESYNC expunge records.
   ///
   ///    One row goes in there for every message ever expunged from an IMAP folder,
   ///    and until now the only thing that ever took one out was deleting the whole
   ///    folder. So the table grew for the life of the installation and the SELECT
   ///    that reads it got slower forever. IMAPExpungeRetentionRecords caps it per
   ///    mailbox, oldest first, which is the strategy RFC 7162 section 5.3 itself
   ///    recommends.
   ///
   ///    What has to be pinned here is NOT that rows were deleted. Rows being deleted
   ///    proves the sweep ran; it says nothing about whether it was safe to run, and
   ///    the unsafe version of this feature passes that test with flying colours.
   ///
   ///    The dangerous outcome is a client that resynchronises from a mod-sequence
   ///    older than anything the server still remembers and is answered from the
   ///    records that happen to be left. It is then told that SOME messages vanished,
   ///    concludes that everything else it knew about is still there, and never asks
   ///    again - so a deleted message stays in that client's mailbox until somebody
   ///    rebuilds the account. RFC 7162 section 3.2.6 forbids exactly this:
   ///
   ///       "Note: A server that receives a mod-sequence smaller than &lt;minmodseq&gt;,
   ///       where &lt;minmodseq&gt; is the value of the smallest expunged mod-sequence it
   ///       remembers minus one, MUST behave as if it was requested to report all
   ///       expunged messages from the provided UID set parameter."
   ///
   ///    So the two tests below are the two sides of that boundary, and the fixture is
   ///    built so that the two answers are DIFFERENT STRINGS. A message is expunged
   ///    before the client's mod-sequence is taken, which means the precise answer
   ///    excludes it ("2:4") and the complete answer required by section 3.2.6
   ///    includes it ("1:4"). Without that, both paths would produce the same text and
   ///    the test would prove nothing about which one ran.
   /// </summary>
   [TestFixture]
   public class TombstoneRetention : TestFixtureBase
   {
      private const string RetentionSetting = "IMAPExpungeRetentionRecords";

      private bool settingChanged_;

      /// <summary>
      ///    Puts the setting back and restarts, because IniFileSettings caches
      ///    hMailServer.ini for the life of the process: leaving it at 1 would run
      ///    every fixture after this one against a server that remembers a single
      ///    expunge per mailbox.
      ///
      ///    A separate name rather than an override, so the base fixture's TearDown -
      ///    which is where the crash oracle is checked - still runs. NUnit runs the
      ///    derived one first.
      /// </summary>
      [TearDown]
      public void RestoreTheRetentionSetting()
      {
         if (!settingChanged_)
            return;

         settingChanged_ = false;

         ServerIniFile.SetSetting(RetentionSetting, null);
         RestartServerAndReacquireCom();
      }

      /// <summary>
      ///    Five messages, UIDs 1 to 5. UID 1 is expunged, the mailbox's HIGHESTMODSEQ
      ///    is taken - that is the mod-sequence the imaginary client last synchronised
      ///    at - and then UIDs 2, 3 and 4 are expunged too, leaving UID 5.
      ///
      ///    Expunging one message BEFORE taking the mod-sequence is the whole trick.
      ///    A server answering from its records reports only what went AFTER that
      ///    mod-sequence, which is 2:4. A server answering under RFC 7162 section
      ///    3.2.6 reports every UID in the mailbox's range that is no longer there,
      ///    which is 1:4 - it includes the message the client already knew about, and
      ///    that redundancy is precisely the harmless cost of the safe answer.
      ///
      ///    Returns the client's mod-sequence.
      /// </summary>
      private static long BuildMailboxWithExpunges(ImapClientSimulator simulator)
      {
         for (int i = 1; i <= 5; i++)
            simulator.SendSingleCommandWithLiteral("A0" + i + " APPEND INBOX {4}", "ABCD");

         simulator.SendSingleCommand("B01 ENABLE QRESYNC");
         simulator.SelectFolder("INBOX");

         simulator.SendSingleCommand("B02 UID STORE 1 +FLAGS (\\Deleted)");
         simulator.SendSingleCommand("B03 EXPUNGE");

         long clientModSeq = ParseHighestModSeq(simulator.SendSingleCommand("B04 STATUS INBOX (HIGHESTMODSEQ)"));

         // The STORE moves the mailbox mod-sequence on before any of these three
         // expunges is recorded, so the newest record - the one the cap always keeps -
         // ends up more than one above clientModSeq. That matters because the boundary
         // section 3.2.6 draws is "smallest remembered minus one": a client sitting
         // exactly on it is still entitled to the precise answer.
         simulator.SendSingleCommand("B05 UID STORE 2:4 +FLAGS (\\Deleted)");
         simulator.SendSingleCommand("B06 EXPUNGE");

         return clientModSeq;
      }

      private static long ParseHighestModSeq(string response)
      {
         var match = Regex.Match(response, @"HIGHESTMODSEQ\s+(\d+)");

         Assert.IsTrue(match.Success, "Response carried no HIGHESTMODSEQ: " + response);

         return long.Parse(match.Groups[1].Value);
      }

      private static string ResyncFrom(string address, long modSeq, string tagPrefix)
      {
         var simulator = new ImapClientSimulator();

         try
         {
            simulator.Connect();
            simulator.LogonWithLiteral(address, "test");
            simulator.SendSingleCommand(tagPrefix + "1 ENABLE QRESYNC");

            return simulator.SendSingleCommand(tagPrefix + "2 SELECT INBOX (QRESYNC (1 " + modSeq + "))");
         }
         finally
         {
            simulator.Disconnect();
         }
      }

      [Test]
      [Description("RFC 7162 (QRESYNC): a client resynchronising from a mod-sequence the server still holds records for is told exactly which UIDs vanished, and no more")]
      public void InsideTheWindowTheServerNamesExactlyWhatVanished()
      {
         const string address = "tombstonewithin@example.test";

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral(address, "test");

         long clientModSeq = BuildMailboxWithExpunges(simulator);

         simulator.Disconnect();

         // Nothing has been pruned - the shipped cap is 5000 records per mailbox and
         // this one has four - so the records answer the client precisely.
         string result = ResyncFrom(address, clientModSeq, "C0");

         Assert.IsTrue(result.Contains("* VANISHED (EARLIER) 2:4"),
            "With every expunge record still held, QRESYNC must report exactly the UIDs expunged after the " +
            "client's mod-sequence - 2:4, not the whole range. " + result);

         Assert.IsFalse(result.Contains("* VANISHED (EARLIER) 1:4"),
            "UID 1 was expunged BEFORE the client's mod-sequence, so a server that can still answer precisely " +
            "must not include it. Reporting 1:4 here would mean the precise path is not being taken at all. " +
            result);
      }

      [Test]
      [Description("RFC 7162 3.2.6: once the records covering the client's mod-sequence have been pruned, the server reports every UID the mailbox no longer holds rather than the shorter list it still happens to have")]
      public void OutsideTheWindowTheServerNamesEverythingThatVanished()
      {
         const string address = "tombstonebeyond@example.test";

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral(address, "test");

         long clientModSeq = BuildMailboxWithExpunges(simulator);

         // Read BEFORE the restart, deliberately. The restart disconnects this
         // connection and every COM proxy the fixture holds, and a value fetched
         // afterwards would be fetched from a dead process.
         long currentModSeq = ParseHighestModSeq(simulator.SendSingleCommand("C01 STATUS INBOX (HIGHESTMODSEQ)"));

         simulator.Disconnect();

         // One record per mailbox. The sweep then has to drop the oldest three and
         // keep the newest - and keeping the newest is what lets the server tell
         // "nothing was ever expunged here" apart from "I have forgotten".
         settingChanged_ = true;
         ServerIniFile.SetSetting(RetentionSetting, "1");

         // The sweep runs once promptly at startup, so a restart is the trigger. It
         // is a scheduled task rather than something the restart waits for, so poll
         // on the observable rather than sleeping a fixed time.
         RestartServerAndReacquireCom();

         string result = "";

         for (int attempt = 0; attempt < 60; attempt++)
         {
            try
            {
               result = ResyncFrom(address, clientModSeq, "D0");

               if (result.Contains("* VANISHED (EARLIER) 1:4"))
                  break;
            }
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
            {
               // IMAP may not be accepting connections for the first moment after the
               // restart. Anything still failing at the deadline shows up in the
               // assertion below with the last response attached.
               result = ex.Message;
            }

            Thread.Sleep(500);
         }

         Assert.IsTrue(result.Contains("* VANISHED (EARLIER) 1:4"),
            "The records covering this client's mod-sequence have been pruned, so RFC 7162 section 3.2.6 " +
            "requires every UID in the requested set that the mailbox no longer holds - 1:4 - rather than " +
            "the remaining records. " + result);

         Assert.IsFalse(result.Contains("* VANISHED (EARLIER) 2:4"),
            "2:4 is the answer built from whatever records survived the prune. It is the dangerous one: the " +
            "client would conclude UID 1 is still in the mailbox and never ask again. " + result);

         // And the complete-list answer is reserved for clients that are actually out
         // of range. A client that is up to date is still answered from the records,
         // which for it means no VANISHED response at all - not a list of every gap in
         // the mailbox on every SELECT.
         string uptodate = ResyncFrom(address, currentModSeq, "E0");

         Assert.IsFalse(uptodate.Contains("VANISHED"),
            "A client resynchronising from the current HIGHESTMODSEQ has missed nothing, and must not be sent " +
            "a VANISHED response merely because older records were pruned. " + uptodate);
      }

      [Test]
      [Description("RFC 7162 3.2.6: VANISHED (EARLIER) is sent before any FETCH response - the order is a MUST, because the client renumbers by it")]
      public void VanishedEarlierComesBeforeTheFetchResponses()
      {
         const string address = "tombstoneorder@example.test";

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "test");

         var simulator = new ImapClientSimulator();
         simulator.Connect();
         simulator.LogonWithLiteral(address, "test");

         long clientModSeq = BuildMailboxWithExpunges(simulator);

         // Change the survivor AFTER the client's sync point, so the UID FETCH below
         // has a FETCH response to send as well as a VANISHED one - a response with
         // only one of the two proves nothing about their order.
         simulator.SendSingleCommand("F01 UID STORE 5 +FLAGS (\\Answered)");

         string result = simulator.SendSingleCommand(
            "F02 UID FETCH 1:* (FLAGS) (CHANGEDSINCE " + clientModSeq + " VANISHED)");

         simulator.Disconnect();

         int vanishedAt = result.IndexOf("* VANISHED (EARLIER)");
         int fetchAt = result.IndexOf("FETCH (");

         Assert.IsTrue(vanishedAt >= 0, "The expunges after the client's mod-sequence must be reported. " + result);
         Assert.IsTrue(fetchAt >= 0, "The flagged survivor must produce a FETCH response. " + result);

         // RFC 7162 3.2.6: "Any VANISHED (EARLIER) responses MUST be returned before
         // any FETCH responses, otherwise the client might get confused about how
         // message numbers map to UIDs." The client shrinks its model by the vanished
         // set first; a FETCH that arrives before it is renumbering a mailbox the
         // client still believes is larger.
         Assert.IsTrue(vanishedAt < fetchAt,
            "VANISHED (EARLIER) must precede the FETCH responses (RFC 7162 3.2.6 MUST). " + result);
      }
   }
}
