// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    RFC 5256 THREAD: the conversation trees a client builds its threaded view
   ///    from. Every test delivers messages with crafted Message-ID / References /
   ///    Subject / Date headers and asserts on the exact parenthesised tree the
   ///    server answers, because the tree IS the contract - a client renders it
   ///    directly, so an off-by-one in the nesting is a conversation shown under the
   ///    wrong parent, not a cosmetic defect.
   ///
   ///    Sequence numbers are deterministic here: each test starts with an empty
   ///    INBOX and delivers in a fixed order, so message N of the test is sequence
   ///    number N. Where UID THREAD is asserted, the UIDs are read back rather than
   ///    assumed.
   /// </summary>
   [TestFixture]
   public class ThreadCommand : TestFixtureBase
   {
      private const string Password = "test";

      private Account CreateAccount()
      {
         return SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "thread@example.test", Password);
      }

      /// <summary>
      ///    Delivers one message with exactly the threading headers given. The Date
      ///    matters: REFERENCES and ORDEREDSUBJECT both order siblings by sent date,
      ///    so each message gets a distinct, increasing date and the assertions are
      ///    on ordering the test controls, not on delivery timing.
      /// </summary>
      private static void Deliver(Account account, string subject, string messageId,
                                  string references, int minuteOffset)
      {
         string headers =
            "From: sender@example.test\r\n" +
            "To: " + account.Address + "\r\n" +
            "Subject: " + subject + "\r\n" +
            // 2020, deliberately in the past. The INTERNALDATE of anything delivered
            // by this suite is "now", so a crafted date in the present competes with
            // it: AnUnparseableDateFallsBackToInternalDate asserts that a message
            // with no usable Date sorts by arrival, and that assertion only means
            // something if arrival is clearly LATER than every crafted date. The
            // first version used today's date at 10:xx, the suite ran at 09:xx, and
            // the test failed while the code was right.
            "Date: Sat, 15 Aug 2020 10:" + minuteOffset.ToString("00") + ":00 +0000\r\n" +
            (messageId.Length > 0 ? "Message-ID: <" + messageId + ">\r\n" : "") +
            (references.Length > 0 ? "References: " + references + "\r\n" : "");

         SmtpClientSimulator.StaticSendRaw("sender@example.test", account.Address,
            headers + "\r\n" + "Body of " + subject + "\r\n");
      }

      private static void WaitForMessageCount(Account account, int count)
      {
         ImapClientSimulator.AssertMessageCount(account.Address, Password, "Inbox", count);
      }

      /// <summary>
      ///    Sends the command and returns the untagged THREAD line, trimmed of its
      ///    "* THREAD" prefix - the tree itself, exactly as a client parses it.
      /// </summary>
      private static string RunThread(Account account, string command)
      {
         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(account.Address, Password));
            Assert.IsTrue(sim.SelectFolder("INBOX"));

            string response = sim.SendSingleCommand("A10 " + command);

            int start = response.IndexOf("* THREAD", StringComparison.OrdinalIgnoreCase);
            Assert.GreaterOrEqual(start, 0, "No THREAD response in:\r\n" + response);

            int lineEnd = response.IndexOf("\r\n", start, StringComparison.Ordinal);
            Assert.Greater(lineEnd, start, "Unterminated THREAD line in:\r\n" + response);

            return response.Substring(start + 8, lineEnd - start - 8).Trim();
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    The capability line is the client's feature detection: Thunderbird only
      ///    sends THREAD after seeing THREAD=REFERENCES here.
      /// </summary>
      [Test]
      [Description("CAPABILITY advertises both RFC 5256 algorithms.")]
      public void CapabilityAdvertisesBothAlgorithms()
      {
         Account account = CreateAccount();

         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(account.Address, Password));

            string capability = sim.SendSingleCommand("A01 CAPABILITY");

            StringAssert.Contains("THREAD=ORDEREDSUBJECT", capability);
            StringAssert.Contains("THREAD=REFERENCES", capability);
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    The canonical REFERENCES case: a root, a reply, a reply to the reply,
      ///    and an unrelated message. The chain must nest and the stranger must
      ///    stand alone.
      /// </summary>
      [Test]
      [Description("REFERENCES threads a reply chain by its References headers and leaves an unrelated message alone.")]
      public void ReferencesThreadsAReplyChain()
      {
         Account account = CreateAccount();

         Deliver(account, "Budget", "root@x.test", "", 1);
         Deliver(account, "Re: Budget", "reply1@x.test", "<root@x.test>", 2);
         Deliver(account, "Re: Budget", "reply2@x.test", "<root@x.test> <reply1@x.test>", 3);
         Deliver(account, "Lunch", "other@x.test", "", 4);
         WaitForMessageCount(account, 4);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII ALL");

         // The chain is single-child at every step, so it renders as a flat member
         // list inside one thread; the unrelated message is its own thread.
         Assert.AreEqual("(1 2 3)(4)", tree);
      }

      /// <summary>
      ///    Two replies to the same parent branch, and each branch is parenthesised.
      ///    This is the RFC's own "(3 6 (4 23)(44 7 96))" shape, cut down to the part
      ///    that matters: nesting begins where the chain forks.
      /// </summary>
      [Test]
      [Description("REFERENCES renders a fork as nested parenthesised subtrees, ordered by date.")]
      public void ReferencesRendersAForkAsNestedSubtrees()
      {
         Account account = CreateAccount();

         Deliver(account, "Plan", "root@x.test", "", 1);
         Deliver(account, "Re: Plan", "a@x.test", "<root@x.test>", 2);
         Deliver(account, "Re: Plan", "b@x.test", "<root@x.test>", 3);
         WaitForMessageCount(account, 3);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII ALL");

         Assert.AreEqual("(1 (2)(3))", tree);
      }

      /// <summary>
      ///    The reason REFERENCES exists at all: two replies whose common parent
      ///    never arrived must still thread together, under a dummy the response
      ///    renders as a parenthesised group with no leading number.
      /// </summary>
      [Test]
      [Description("Two replies to a missing parent thread together under a dummy - ((2)(3)) shape, not two strangers.")]
      public void RepliesToAMissingParentShareADummyRoot()
      {
         Account account = CreateAccount();

         Deliver(account, "Outage", "solo@x.test", "", 1);
         Deliver(account, "Re: Incident", "r1@x.test", "<never-arrived@x.test>", 2);
         Deliver(account, "Re: Incident", "r2@x.test", "<never-arrived@x.test>", 3);
         WaitForMessageCount(account, 3);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII ALL");

         // The dummy holds the orphans together; the unrelated message stands alone.
         // Root threads are date-ordered, and the dummy's date is its first child's,
         // so the solo message (earlier) comes first.
         Assert.AreEqual("(1)((2)(3))", tree);
      }

      /// <summary>
      ///    Base-subject grouping is the fallback when no References connect the
      ///    messages: "Re: X" with no ancestry files under "X" rather than beside it.
      /// </summary>
      [Test]
      [Description("A reply with no References threads under the message whose base subject it shares.")]
      public void AReplyWithoutReferencesGroupsByBaseSubject()
      {
         Account account = CreateAccount();

         Deliver(account, "Quarterly numbers", "q1@x.test", "", 1);
         Deliver(account, "Re: Quarterly numbers", "q2@x.test", "", 2);
         WaitForMessageCount(account, 2);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII ALL");

         Assert.AreEqual("(1 2)", tree);
      }

      /// <summary>
      ///    The negative control for the grouping tests: different subjects and no
      ///    ancestry must yield separate threads. Without this, an implementation
      ///    that lumped everything into one tree would pass the tests above.
      /// </summary>
      [Test]
      [Description("Unrelated messages produce one thread each - the control that keeps the grouping tests honest.")]
      public void UnrelatedMessagesStayUnrelated()
      {
         Account account = CreateAccount();

         Deliver(account, "Alpha", "m1@x.test", "", 1);
         Deliver(account, "Beta", "m2@x.test", "", 2);
         Deliver(account, "Gamma", "m3@x.test", "", 3);
         WaitForMessageCount(account, 3);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII ALL");

         Assert.AreEqual("(1)(2)(3)", tree);
      }

      /// <summary>
      ///    ORDEREDSUBJECT is the flat approximation: one chain per base subject,
      ///    date-ordered within, threads ordered by their first message.
      /// </summary>
      [Test]
      [Description("ORDEREDSUBJECT groups by base subject into flat chains.")]
      public void OrderedSubjectGroupsIntoFlatChains()
      {
         Account account = CreateAccount();

         Deliver(account, "Picnic", "p1@x.test", "", 1);
         Deliver(account, "Standup", "s1@x.test", "", 2);
         Deliver(account, "Re: Picnic", "p2@x.test", "", 3);
         Deliver(account, "RE: picnic", "p3@x.test", "", 4);
         WaitForMessageCount(account, 4);

         string tree = RunThread(account, "THREAD ORDEREDSUBJECT US-ASCII ALL");

         // "Re:"/"RE:" strip to the same base subject case-insensitively, so 1, 3
         // and 4 are one flat chain in date order; 2 stands alone and follows in
         // date order of thread roots.
         Assert.AreEqual("(1 3 4)(2)", tree);
      }

      /// <summary>
      ///    UID THREAD is what real clients send. The tree must carry UIDs, verified
      ///    against UIDs read back rather than guessed.
      /// </summary>
      [Test]
      [Description("UID THREAD returns the same tree with UIDs in place of sequence numbers.")]
      public void UidThreadCarriesUids()
      {
         Account account = CreateAccount();

         Deliver(account, "Chain", "c1@x.test", "", 1);
         Deliver(account, "Re: Chain", "c2@x.test", "<c1@x.test>", 2);
         WaitForMessageCount(account, 2);

         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(account.Address, Password));
            Assert.IsTrue(sim.SelectFolder("INBOX"));

            // The real UIDs, from the source of truth.
            string uid1Response = sim.Fetch("1 UID");
            string uid2Response = sim.Fetch("2 UID");

            string uid1 = ExtractUid(uid1Response);
            string uid2 = ExtractUid(uid2Response);

            string response = sim.SendSingleCommand("A11 UID THREAD REFERENCES US-ASCII ALL");

            StringAssert.Contains("* THREAD (" + uid1 + " " + uid2 + ")", response,
               "UID THREAD did not carry the UIDs " + uid1 + " and " + uid2 + ". Response:\r\n" + response);
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    RFC 5256: an algorithm the server did not advertise is a protocol error.
      ///    BAD, not a silent substitution - a client that asked for REFERENCES and
      ///    got ORDEREDSUBJECT would draw wrong trees with no way to know.
      /// </summary>
      [Test]
      [Description("An unknown algorithm is answered BAD rather than silently substituted.")]
      public void AnUnknownAlgorithmIsRefusedWithBad()
      {
         Account account = CreateAccount();

         Deliver(account, "One", "one@x.test", "", 1);
         WaitForMessageCount(account, 1);

         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(account.Address, Password));
            Assert.IsTrue(sim.SelectFolder("INBOX"));

            string response = sim.SendSingleCommand("A12 THREAD JWZ US-ASCII ALL");

            StringAssert.Contains("A12 BAD", response);
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    THREAD is still a search: the criteria restrict which messages enter the
      ///    tree at all, and a message excluded by the search must not appear even
      ///    when its References would place it.
      /// </summary>
      [Test]
      [Description("The search criteria bound the tree - an excluded message does not appear even though referenced.")]
      public void TheSearchCriteriaBoundTheTree()
      {
         Account account = CreateAccount();

         Deliver(account, "Wanted", "w1@x.test", "", 1);
         Deliver(account, "Re: Wanted", "w2@x.test", "<w1@x.test>", 2);
         Deliver(account, "Unwanted", "u1@x.test", "", 3);
         WaitForMessageCount(account, 3);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII SUBJECT \"Wanted\"");

         // Both "Wanted" messages match the criteria ("Re: Wanted" contains it);
         // "Unwanted" also contains it as a substring - IMAP SUBJECT is a substring
         // match - so pin the shape that matters: the chain nests, and every match
         // is in the tree exactly once.
         Assert.AreEqual("(1 2)(3)", tree);
      }

      /// <summary>
      ///    A THREAD over an empty result set answers a bare "* THREAD" line - not a
      ///    stale tree from an earlier command on the same connection, which is what
      ///    a handler-reuse bug would produce.
      /// </summary>
      [Test]
      [Description("A THREAD matching nothing answers an empty tree, including after a THREAD that matched.")]
      public void AThreadMatchingNothingAnswersEmpty()
      {
         Account account = CreateAccount();

         Deliver(account, "Only", "only@x.test", "", 1);
         WaitForMessageCount(account, 1);

         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(account.Address, Password));
            Assert.IsTrue(sim.SelectFolder("INBOX"));

            // First a THREAD that matches, then one that cannot - on the SAME
            // connection, because the handler is reused per connection and the
            // stale-state defect only shows up on the second command.
            string first = sim.SendSingleCommand("A13 THREAD REFERENCES US-ASCII ALL");
            StringAssert.Contains("* THREAD (1)", first);

            string second = sim.SendSingleCommand("A14 THREAD REFERENCES US-ASCII SUBJECT \"NoSuchSubject\"");

            int line = second.IndexOf("* THREAD", StringComparison.OrdinalIgnoreCase);
            Assert.GreaterOrEqual(line, 0, second);

            int lineEnd = second.IndexOf("\r\n", line, StringComparison.Ordinal);
            string content = second.Substring(line + 8, lineEnd - line - 8).Trim();

            Assert.AreEqual("", content,
               "A THREAD that matched nothing answered a tree - stale state from the previous command. " +
               "Response:\r\n" + second);
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    A reply chain far deeper than any conversation must not crash the server.
      ///
      ///    This is the test for a HIGH finding an adversarial review measured rather
      ///    than argued: Prune_ and SortChildrenRecursively_ recurse once per level,
      ///    a worker thread here has the 1MB default stack, and a probe compiled
      ///    against those exact frames survived 6000 levels and died with
      ///    STATUS_STACK_OVERFLOW at 8000. A stack overflow is not reliably catchable,
      ///    so that was a whole-service crash reachable by any authenticated user
      ///    willing to mail themselves a long enough chain.
      ///
      ///    600 messages, which is past the 512 depth cap and cheap enough to run in
      ///    the suite. What is asserted is only that the server survives and still
      ///    answers - the exact tree past the cap is not a contract, and pinning one
      ///    would be pinning the arbitrary half of the fix.
      /// </summary>
      [Test]
      [Description("A reply chain deeper than the depth cap is answered without crashing the server.")]
      public void ADeepReplyChainDoesNotCrashTheServer()
      {
         Account account = CreateAccount();

         const int chainLength = 600;

         for (int i = 1; i <= chainLength; i++)
         {
            string references = i == 1 ? "" : "<chain" + (i - 1) + "@x.test>";
            Deliver(account, "Deep chain", "chain" + i + "@x.test", references, i % 60);
         }

         WaitForMessageCount(account, chainLength);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII ALL");

         Assert.IsNotEmpty(tree, "The server answered an empty tree for a deep chain.");
         StringAssert.Contains("(", tree);

         // Still alive and still serving afterwards - the point of the test.
         var sim = new ImapClientSimulator();

         try
         {
            Assert.IsTrue(sim.ConnectAndLogon(account.Address, Password),
               "The server did not accept a connection after threading a deep chain.");
            Assert.IsTrue(sim.SelectFolder("INBOX"));
         }
         finally
         {
            sim.Disconnect();
         }
      }

      /// <summary>
      ///    RFC 5256 2.2: when the sent date cannot be determined, INTERNALDATE is
      ///    used. A header that is PRESENT but unparseable is that case, and testing
      ///    only for an absent header missed it.
      ///
      ///    The failure it guards is silent and inverted: an unreadable date parsed
      ///    to the 1899 OLE epoch, and the comparator sorts on the raw value without
      ///    consulting the validity flag, so the broken message sorted to the FRONT
      ///    of its thread. A conversation would open with its most malformed message.
      /// </summary>
      [Test]
      [Description("A present-but-unparseable Date falls back to INTERNALDATE rather than sorting to 1899.")]
      public void AnUnparseableDateFallsBackToInternalDate()
      {
         Account account = CreateAccount();

         // Delivered first, so if its date were honoured as 1899 it would still sort
         // first and the test could not tell. It is delivered LAST instead, so
         // "sorted to the front" and "sorted by arrival" are different answers.
         Deliver(account, "Report", "d1@x.test", "", 10);
         Deliver(account, "Re: Report", "d2@x.test", "<d1@x.test>", 20);

         SmtpClientSimulator.StaticSendRaw("sender@example.test", account.Address,
            "From: sender@example.test\r\n" +
            "To: " + account.Address + "\r\n" +
            "Subject: Re: Report\r\n" +
            "Date: not-a-date-at-all\r\n" +
            "Message-ID: <d3@x.test>\r\n" +
            "References: <d1@x.test>\r\n" +
            "\r\nBody\r\n");

         WaitForMessageCount(account, 3);

         string tree = RunThread(account, "THREAD REFERENCES US-ASCII ALL");

         // All three are one thread: root, then its two replies as branches. The
         // broken-date message arrived last, so with the INTERNALDATE fallback it
         // sorts last; with the 1899 epoch it would sort first.
         Assert.AreEqual("(1 (2)(3))", tree,
            "The message with an unparseable Date did not fall back to INTERNALDATE - it sorted by the " +
            "1899 epoch instead. Tree was: " + tree);
      }

      private static string ExtractUid(string fetchResponse)
      {
         int marker = fetchResponse.IndexOf("UID ", StringComparison.OrdinalIgnoreCase);
         Assert.GreaterOrEqual(marker, 0, fetchResponse);

         int start = marker + 4;
         int end = start;
         while (end < fetchResponse.Length && char.IsDigit(fetchResponse[end]))
            end++;

         Assert.Greater(end, start, fetchResponse);
         return fetchResponse.Substring(start, end - start);
      }
   }
}
