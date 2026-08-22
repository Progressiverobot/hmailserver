// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    The full-text term index behind SEARCH BODY and SEARCH TEXT
   ///    (hm_messageindexterms, IndexerFullText in hMailServer.ini, off by default).
   ///
   ///    The contract these tests pin is that the index is a FILTER and never the
   ///    answer. IMAP BODY search is substring search - "ell" matches "hello" - and a
   ///    term index cannot answer that on its own, so the server only ever uses the
   ///    index to prove a message CANNOT contain the text and skip reading it;
   ///    everything else is read and scanned by the same code as before. The
   ///    observable consequence, and the thing that matters: search results are
   ///    IDENTICAL with the index on and off, at every stage of the backfill, and the
   ///    substring-inside-a-word query is asserted explicitly because it is precisely
   ///    where an index-only implementation would silently return nothing.
   ///
   ///    Whether the filter actually filters is observable through the byte ceiling
   ///    (IMAPSearchMaxMegabytes): a search whose needle is absent from an indexed
   ///    mailbox succeeds without reading a byte, where the bare scan would go over
   ///    budget and fail - and a search whose needle is present must still read the
   ///    candidates and hit the ceiling, which is the "never the answer" half.
   ///
   ///    IndexerFullText is an ini setting cached at process start;
   ///    Application.Reinitialize() is what re-reads it (SearchLimits establishes the
   ///    pattern). No test here restarts the service, so no COM proxy goes stale, and
   ///    no search uses a literal, so there are no literal sizes to get wrong.
   /// </summary>
   [TestFixture]
   public class FullTextSearch : TestFixtureBase
   {
      private const string FillerNeedle = "FullTextFillerNeedle";

      /// <summary>
      ///    Every key this fixture may write, returned to its default (absent), and
      ///    the message indexer switched back off, so later fixtures see stock
      ///    behaviour. Runs before the base TearDown.
      /// </summary>
      [TearDown]
      public void TearDownFullText()
      {
         IniFileSetting.Delete("IndexerFullText");
         IniFileSetting.Delete("IMAPSearchTimeout");
         IniFileSetting.Delete("IMAPSearchMaxMegabytes");
         _application.Reinitialize();

         _settings.MessageIndexing.Enabled = false;
      }

      private void EnableFullTextIndex()
      {
         IniFileSetting.Write("IndexerFullText", "1");
         _application.Reinitialize();

         _settings.MessageIndexing.Enabled = true;
         _settings.MessageIndexing.Index();
      }

      /// <summary>
      ///    Waits until the metadata indexer has caught up, which is the observable
      ///    proxy for "the indexer thread has run": the full-text pass runs in the
      ///    same loop, immediately after the metadata pass, over the same messages.
      ///    Mirrors MessageIndexing.AssertAllMessagesIndexed.
      /// </summary>
      private void WaitForIndexer()
      {
         var indexing = _settings.MessageIndexing;
         indexing.Index();

         for (var i = 0; i < 1000; i++)
         {
            if (indexing.TotalIndexedCount == indexing.TotalMessageCount)
            {
               // The same wake that finished the metadata also runs the
               // full-text batch; one more prod covers a message that landed
               // between the two.
               indexing.Index();
               return;
            }

            Thread.Sleep(20);
         }

         Assert.Fail("The message indexer did not catch up. Message count: " + indexing.TotalMessageCount +
                     ", indexed count: " + indexing.TotalIndexedCount);
      }

      private string[] RunSearches(string address, string[] queries)
      {
         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         var results = new string[queries.Length];
         for (var i = 0; i < queries.Length; i++)
            results[i] = simulator.Search(queries[i]);

         simulator.Close();
         return results;
      }

      [Test]
      [Description("The load-bearing assertion: the same searches return the same sequence numbers with " +
                   "the index off (the shipped default) and on - including a substring inside a word, a " +
                   "header-only TEXT match, a multi-word phrase, a needle shorter than the index's minimum " +
                   "token length (which must fall back to the scan), and a needle that matches nothing.")]
      public void SearchResultsAreIdenticalWithTheIndexOnAndOff()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "fulltext-identity@example.test", "test");

         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send(account.Address, account.Address, "Alpha subject", "say hello world today");
         smtpClientSimulator.Send(account.Address, account.Address, "Beta subject", "the othello festival programme");
         smtpClientSimulator.Send(account.Address, account.Address, "GammaNeedle subject", "entirely unrelated content");
         smtpClientSimulator.Send(account.Address, account.Address, "Delta subject", "the quick brown fox jumps over the lazy dog");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 4);

         string[] queries =
         {
            "BODY \"hello\"",         // a whole word in 1, and inside "othello" in 2
            "BODY \"ell\"",           // ONLY inside words - where an index-only implementation silently differs
            "BODY \"othello\"",       // a whole word in 2 only
            "TEXT \"othello\"",       // TEXT over the same needle
            "TEXT \"GammaNeedle\"",   // present in a HEADER only (the subject of 3)
            "BODY \"absentneedle\"",  // in nothing at all
            "BODY \"HELLO world\"",   // case folding plus a multi-word phrase
            "BODY \"o\"",             // below the minimum token length: must fall back to the scan
         };

         string[] expected =
         {
            "1 2",
            "1 2",
            "2",
            "2",
            "3",
            "",
            "1",
            "1 2 3 4",
         };

         // The control run, with the feature at its shipped default (off). Asserted
         // against explicit expectations first, so the identity comparison below
         // cannot pass by both runs being wrong the same way.
         var disabledResults = RunSearches(account.Address, queries);

         for (var i = 0; i < queries.Length; i++)
            Assert.AreEqual(expected[i], disabledResults[i],
               "With the index off, " + queries[i] + " returned the wrong result.");

         EnableFullTextIndex();
         WaitForIndexer();

         // The same searches with the index on. Identical results is the whole
         // contract - and it must hold whatever the backfill has reached, because
         // an unindexed message is simply scanned, so no retry loop belongs here:
         // a difference at ANY indexing stage is a real defect.
         var enabledResults = RunSearches(account.Address, queries);

         for (var i = 0; i < queries.Length; i++)
            Assert.AreEqual(disabledResults[i], enabledResults[i],
               "Enabling the full-text index changed the result of " + queries[i] +
               " - the index may only ever narrow the scan, never alter its answer.");
      }

      [Test]
      [Description("A deleted and expunged message stops matching while the index is on. Its term rows " +
                   "may outlive it until the maintenance sweep, but staleness there must never resurrect " +
                   "a message: a search can only return what is in the folder.")]
      public void ADeletedMessageStopsMatchingWhileTheIndexIsOn()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "fulltext-delete@example.test", "test");

         var smtpClientSimulator = new SmtpClientSimulator();
         smtpClientSimulator.Send(account.Address, account.Address, "First", "carries uniqueAlphaNeedle within");
         smtpClientSimulator.Send(account.Address, account.Address, "Second", "carries uniqueBetaNeedle within");
         ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 2);

         EnableFullTextIndex();
         WaitForIndexer();

         var simulator = new ImapClientSimulator();
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, "test"));
         Assert.IsTrue(simulator.SelectFolder("INBOX"));

         Assert.AreEqual("1", simulator.Search("BODY \"uniqueAlphaNeedle\""));
         Assert.AreEqual("2", simulator.Search("BODY \"uniqueBetaNeedle\""));

         Assert.IsTrue(simulator.SetDeletedFlag(1));
         Assert.IsTrue(simulator.Expunge());

         Assert.AreEqual("", simulator.Search("BODY \"uniqueAlphaNeedle\""),
            "The expunged message must stop matching immediately, whatever the index still holds for it.");
         Assert.AreEqual("1", simulator.Search("BODY \"uniqueBetaNeedle\""),
            "The surviving message must keep matching under its new sequence number.");

         simulator.Close();
      }

      /// <summary>
      ///    A body of roughly the requested size carrying the filler needle, built
      ///    from short plain lines exactly as SearchLimits builds its bodies.
      /// </summary>
      private static string BuildLargeBody(int approximateBytes, int ordinal)
      {
         var body = new StringBuilder(approximateBytes + 128);
         body.Append("Message ");
         body.Append(ordinal);
         body.Append(" contains ");
         body.Append(FillerNeedle);
         body.Append("\r\n");

         while (body.Length < approximateBytes)
            body.Append("0123456789abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvw\r\n");

         return body.ToString();
      }

      [Test]
      [Description("Proof the filter filters, and only filters. With a 1 MB examine ceiling over 2.8 MB " +
                   "of messages, a needle absent from the index answers OK and empty without reading a " +
                   "byte - the bare scan could only fail that search with NO [LIMIT]. And a needle " +
                   "present in every message must still be verified by the real scan, so the same " +
                   "ceiling still fails THAT search: the index narrows candidates, it never asserts a " +
                   "match. Both halves ride the deterministic byte ceiling, not timing.")]
      public void TheIndexNarrowsTheScanButNeverAnswersForIt()
      {
         int originalMaxMessageSize = _settings.MaxMessageSize;

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "fulltext-ceiling@example.test", "test", 0);

            _settings.MaxMessageSize = 0;
            _domain.MaxMessageSize = 0;
            _domain.Save();

            var smtpClientSimulator = new SmtpClientSimulator();
            for (var i = 1; i <= 4; i++)
               smtpClientSimulator.Send(account.Address, account.Address, "Full text ceiling " + i,
                  BuildLargeBody(700 * 1024, i));
            ImapClientSimulator.AssertMessageCount(account.Address, "test", "INBOX", 4);

            IniFileSetting.Write("IMAPSearchTimeout", "60");
            IniFileSetting.Write("IMAPSearchMaxMegabytes", "1");
            IniFileSetting.Write("IndexerFullText", "1");
            _application.Reinitialize();

            _settings.MessageIndexing.Enabled = true;
            _settings.MessageIndexing.Index();

            // Until the backfill reaches these four messages the search falls back
            // to the scan and correctly fails on the ceiling, so this converges to
            // OK-and-empty exactly when the index is in place - which is the point
            // being proven.
            RetryHelper.TryAction(TimeSpan.FromSeconds(30), () =>
            {
               _settings.MessageIndexing.Index();

               var absentSimulator = new ImapClientSimulator();
               Assert.IsTrue(absentSimulator.ConnectAndLogon(account.Address, "test"));
               Assert.IsTrue(absentSimulator.SelectFolder("INBOX"));

               string absent = absentSimulator.SendSingleCommand("A01 SEARCH BODY \"AbsentFullTextNeedle\"");
               absentSimulator.Close();

               Assert.IsTrue(absent.Contains("* SEARCH\r\n"),
                  "An indexed mailbox must answer an absent needle with an empty untagged SEARCH. " + absent);
               Assert.IsTrue(absent.Contains("A01 OK"),
                  "An indexed mailbox must answer an absent needle under the byte ceiling, since nothing " +
                  "needs reading. " + absent);
            });

            // The needle every message carries: all four are candidates, the scan
            // must truly read them, and the ceiling fires just as it would with the
            // index off. If this ever returns OK, the index answered instead of
            // filtering - the precise defect this fixture exists to forbid.
            var presentSimulator = new ImapClientSimulator();
            Assert.IsTrue(presentSimulator.ConnectAndLogon(account.Address, "test"));
            Assert.IsTrue(presentSimulator.SelectFolder("INBOX"));

            string present = presentSimulator.SendSingleCommand("A02 SEARCH BODY \"" + FillerNeedle + "\"");
            presentSimulator.Close();

            Assert.IsTrue(present.Contains("A02 NO"),
               "Candidates must still be read and verified by the scan, so the byte ceiling must still " +
               "fail a search that matches everything. " + present);
            Assert.IsTrue(present.Contains("[LIMIT]"),
               "The over-budget search should carry the LIMIT response code. " + present);
            Assert.IsFalse(present.Contains("* SEARCH"),
               "An abandoned search must send no untagged SEARCH data at all. " + present);
         }
         finally
         {
            _settings.MaxMessageSize = originalMaxMessageSize;
         }
      }
   }
}
