// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The spam pipeline overview: the page that answers "what will all of this do to
   /// a message".
   ///
   /// Anti-spam was five pages - thresholds and checks, SURBL servers, DNS
   /// blacklists, a white list and a greylisting white list - and the roadmap's
   /// complaint was that none of them answers that question, because the answer is
   /// not on any one page. Against the unfixed tree every test here fails: there is
   /// no overview page, no model behind it, and nothing anywhere in the interface
   /// that states the four server behaviours pinned below.
   ///
   /// Each of those four was read out of the server rather than assumed, and each is
   /// a way the configuration silently does nothing:
   ///
   ///   - SMTPConnection::DoSpamProtection_ guards both thresholds with "> 0", so a
   ///     mark threshold of 0 means "never mark", not "mark everything";
   ///   - the delete threshold is tested first, so a delete threshold at or below the
   ///     mark threshold means nothing is ever marked and delivered;
   ///   - SpamTestRunner::RunSpamTest stops as soon as the running total reaches
   ///     max(delete, mark), so later checks in the fixed order may never run - and
   ///     with both thresholds at 0 that is true immediately;
   ///   - a check that is on with a score of 0 costs a DNS lookup per message and can
   ///     never change the outcome.
   /// </summary>
   public class SpamPipelineTests
   {
      /// <summary>
      /// hMailServer's own defaults, from DBScripts\CreateTables*.sql: mark 5,
      /// delete 20, DMARC on with a score of 5 and every other check off, spam and
      /// reason headers on, subject prefix off. The scan size limit is not seeded at
      /// all, so it reads as 0.
      /// </summary>
      private static SpamPipelineConfig Defaults()
      {
         return new SpamPipelineConfig
         {
            MarkThreshold = 5,
            DeleteThreshold = 20,
            MaxScanKilobytes = 0,
            AddSpamHeader = true,
            AddReasonHeader = true,
            PrependSubject = false,
            EvaluateDmarc = true,
            DmarcFailureScore = 5,
            SpamAssassinHost = "127.0.0.1",
            SpamAssassinScore = 5,
            BypassGreylistingOnSpfPass = true
         };
      }

      /// <summary>
      /// The negative control. Notes that fire on a correct configuration are worse
      /// than no notes at all: the reader learns to scroll past them, and the one
      /// that matters is then invisible. On the shipped defaults nothing above
      /// Information may be said.
      /// </summary>
      [Fact]
      public void TheShippedDefaults_ProduceNothingWorseThanInformation()
      {
         SpamPipelineConfig config = Defaults();

         List<SpamPipelineNote> loud = SpamPipeline.Notes(config)
            .Where(n => n.Level == StatusLevel.Warning || n.Level == StatusLevel.Critical)
            .ToList();

         Assert.True(loud.Count == 0,
            "The default configuration is being criticised: " + string.Join(" | ", loud.Select(n => n.Text)));
         Assert.Equal(StatusLevel.Information, SpamPipeline.WorstLevel(config));
      }

      /// <summary>
      /// The order is a claim about SpamTestRunner::LoadSpamTests, and the order is
      /// load-bearing because the run stops early. Pinned so that reordering this
      /// table - which would be an easy way to make the page read better - cannot
      /// quietly make it wrong.
      /// </summary>
      [Fact]
      public void TheChecks_AreListedInTheOrderTheServerRunsThem()
      {
         IReadOnlyList<SpamCheck> checks = SpamPipeline.Checks(Defaults());

         Assert.Equal(
            new[] { "blockedsenders", "dnsbl", "helo", "ptr", "mx", "spf", "surbl", "dkim", "dmarc", "spamassassin" },
            checks.Select(c => c.Key));

         Assert.Equal(Enumerable.Range(1, 10), checks.Select(c => c.Order));
      }

      /// <summary>
      /// Which phase each check runs in. It decides what the check can see, whether
      /// the scan size limit skips it, and whether a refusal costs the sender the
      /// whole transfer - and it is invisible on the settings page, where all nine
      /// are just checkboxes in a list.
      /// </summary>
      [Theory]
      [InlineData("dnsbl", SpamCheckPhase.BeforeTheBody)]
      [InlineData("helo", SpamCheckPhase.BeforeTheBody)]
      [InlineData("ptr", SpamCheckPhase.BeforeTheBody)]
      [InlineData("mx", SpamCheckPhase.BeforeTheBody)]
      [InlineData("spf", SpamCheckPhase.BeforeTheBody)]
      [InlineData("surbl", SpamCheckPhase.AfterTheBody)]
      [InlineData("dkim", SpamCheckPhase.AfterTheBody)]
      [InlineData("dmarc", SpamCheckPhase.AfterTheBody)]
      [InlineData("spamassassin", SpamCheckPhase.AfterTheBody)]
      public void EachCheck_RunsInThePhaseTheServerRunsItIn(string key, SpamCheckPhase phase)
      {
         SpamCheck check = SpamPipeline.Checks(Defaults()).Single(c => c.Key == key);

         Assert.Equal(phase, check.Phase);
      }

      /// <summary>
      /// A mark threshold of 0 reads as "mark everything" and means the opposite.
      /// This is the one that costs an administrator the most: they turn on the
      /// checks, watch the score in the log, and no message is ever marked.
      /// </summary>
      [Fact]
      public void AZeroMarkThreshold_IsCalledOutAsMarkingNothing()
      {
         SpamPipelineConfig config = Defaults();
         config.MarkThreshold = 0;

         Assert.Contains("Nothing is marked", SpamPipeline.Verdict(config), StringComparison.OrdinalIgnoreCase);
         Assert.Contains(SpamPipeline.Notes(config),
            n => n.Level == StatusLevel.Warning && n.Text.Contains("ever marked as spam", StringComparison.OrdinalIgnoreCase));
      }

      /// <summary>
      /// Both thresholds at 0 does two things, and the second one is invisible: the
      /// stop score is 0, which RunSpamTest's "total >= max" test satisfies before
      /// any check has run, so exactly one check runs per phase and its result is
      /// thrown away.
      /// </summary>
      [Fact]
      public void BothThresholdsAtZero_AreCalledOutIncludingTheStopAfterOneCheck()
      {
         SpamPipelineConfig config = Defaults();
         config.MarkThreshold = 0;
         config.DeleteThreshold = 0;

         Assert.Equal(0, SpamPipeline.StopScore(config));

         SpamPipelineNote note = SpamPipeline.Notes(config).FirstOrDefault(n => n.Level == StatusLevel.Critical);
         Assert.True(note != null, "Both thresholds at zero is not reported at all.");
         Assert.Contains("one check", note.Text, StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>
      /// The delete threshold is tested first, so putting it at or below the mark
      /// threshold means the marking branch is unreachable - every message that would
      /// have been marked and delivered is refused instead. Nothing in the interface
      /// showed the order of those two tests.
      /// </summary>
      [Theory]
      [InlineData(5, 5)]
      [InlineData(10, 4)]
      public void ADeleteThresholdAtOrBelowTheMark_IsCalledOut(int mark, int delete)
      {
         SpamPipelineConfig config = Defaults();
         config.MarkThreshold = mark;
         config.DeleteThreshold = delete;

         Assert.Contains(SpamPipeline.Notes(config),
            n => n.Level == StatusLevel.Critical && n.Text.Contains("ever marked and delivered", StringComparison.OrdinalIgnoreCase));
      }

      [Fact]
      public void ThresholdsTheRightWayRound_AreNotCriticised()
      {
         SpamPipelineConfig config = Defaults();
         config.MarkThreshold = 5;
         config.DeleteThreshold = 6;

         Assert.DoesNotContain(SpamPipeline.Notes(config), n => n.Level == StatusLevel.Critical);
      }

      /// <summary>
      /// A check that is on and scores nothing. It runs - a DNS lookup on every
      /// message, on the thread that will send the 250 - and cannot change the
      /// outcome, which is invisible on a settings page where the switch and the
      /// score are two separate rows.
      /// </summary>
      [Fact]
      public void AnEnabledCheckScoringZero_IsCalledOutByName()
      {
         SpamPipelineConfig config = Defaults();
         config.CheckPtr = true;
         config.PtrScore = 0;

         Assert.Contains(SpamPipeline.Notes(config),
            n => n.Level == StatusLevel.Warning && n.Text.StartsWith("PTR record check", StringComparison.Ordinal));
      }

      /// <summary>
      /// The strongest finding the page can offer: the enabled checks cannot add up
      /// to the threshold, so the filter is decorative however bad the mail is.
      /// </summary>
      [Fact]
      public void AMarkThresholdNoCheckCanReach_IsCalledOut()
      {
         SpamPipelineConfig config = Defaults();
         config.MarkThreshold = 40;
         config.DeleteThreshold = 60;

         Assert.Equal(5, SpamPipeline.HighestReachableScore(config));
         Assert.Contains(SpamPipeline.Notes(config),
            n => n.Level == StatusLevel.Critical && n.Text.Contains("at most 5", StringComparison.OrdinalIgnoreCase));
      }

      /// <summary>
      /// ...and the same page must not make that claim when it cannot compute the
      /// ceiling. A DNSBL entry carries its own score, so the total is open-ended;
      /// answering "unknown" with a number would be exactly the kind of confident
      /// wrong statement this page exists to remove.
      /// </summary>
      [Fact]
      public void AnActiveBlacklist_MakesTheCeilingUnknownRatherThanZero()
      {
         SpamPipelineConfig config = Defaults();
         config.MarkThreshold = 40;
         config.DeleteThreshold = 60;
         config.ActiveDnsBlackLists = 1;
         config.TotalDnsBlackLists = 1;

         Assert.Null(SpamPipeline.HighestReachableScore(config));
         Assert.DoesNotContain(SpamPipeline.Notes(config),
            n => n.Text.Contains("at most", StringComparison.OrdinalIgnoreCase));
      }

      /// <summary>
      /// The same applies to a merging SpamAssassin: it contributes whatever score
      /// SpamAssassin itself produced, which we cannot know.
      /// </summary>
      [Fact]
      public void AMergingSpamAssassin_MakesTheCeilingUnknown()
      {
         SpamPipelineConfig config = Defaults();
         config.SpamAssassinEnabled = true;
         config.SpamAssassinMergesScore = true;

         Assert.Null(SpamPipeline.HighestReachableScore(config));
         Assert.Null(SpamPipeline.Checks(config).Single(c => c.Key == "spamassassin").Score);
      }

      /// <summary>
      /// SpamAssassin runs, tags the message, and its verdict is discarded: with
      /// merging off the fixed score is used, and that score is zero.
      /// </summary>
      [Fact]
      public void SpamAssassinWithMergingOffAndNoScore_IsCalledOutOnce()
      {
         SpamPipelineConfig config = Defaults();
         config.SpamAssassinEnabled = true;
         config.SpamAssassinMergesScore = false;
         config.SpamAssassinScore = 0;

         List<SpamPipelineNote> notes = SpamPipeline.Notes(config)
            .Where(n => n.Text.Contains("SpamAssassin", StringComparison.OrdinalIgnoreCase)).ToList();

         // One note, not two: the generic "scores 0" rule would otherwise say the
         // same thing in different words, and the fix for this one is different.
         Assert.Single(notes);
         Assert.Equal(StatusLevel.Warning, notes[0].Level);
         Assert.Contains("thrown away", notes[0].Text, StringComparison.OrdinalIgnoreCase);
      }

      [Fact]
      public void SpamAssassinWithNoHost_IsCalledOutWithTheErrorCode()
      {
         SpamPipelineConfig config = Defaults();
         config.SpamAssassinEnabled = true;
         config.SpamAssassinMergesScore = true;
         config.SpamAssassinHost = "";

         Assert.Contains(SpamPipeline.Notes(config),
            n => n.Level == StatusLevel.Warning && n.Text.Contains("HM5508", StringComparison.Ordinal));
      }

      /// <summary>
      /// "Marked" with all three of the header, reason and subject switches off
      /// changes nothing about the message, so a mail client has nothing to file on.
      /// The internal flag still does something, and the note has to say so rather
      /// than claim the marking is useless.
      /// </summary>
      [Fact]
      public void MarkingThatChangesNothingAboutTheMessage_IsCalledOut()
      {
         SpamPipelineConfig config = Defaults();
         config.AddSpamHeader = false;
         config.AddReasonHeader = false;
         config.PrependSubject = false;

         SpamPipelineNote note = SpamPipeline.Notes(config)
            .FirstOrDefault(n => n.Text.Contains("no visible way", StringComparison.OrdinalIgnoreCase));

         Assert.True(note != null, "A configuration that marks a message invisibly is not reported.");
         Assert.Equal(StatusLevel.Warning, note.Level);
         Assert.Contains("spam flag", note.Text, StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>
      /// The early stop, made visible: a check that reaches the stop score on its own
      /// means the checks after it in the same phase do not run for a message that
      /// fails it - so they are absent from the log too, which is what makes this
      /// impossible to work out from the outside.
      /// </summary>
      [Fact]
      public void ACheckThatReachesTheStopScoreAlone_NamesWhatWillNotRun()
      {
         var config = new SpamPipelineConfig
         {
            MarkThreshold = 5,
            DeleteThreshold = 5,
            CheckHeloHost = true,
            HeloHostScore = 5,
            CheckPtr = true,
            PtrScore = 1,
            AddSpamHeader = true
         };

         SpamPipelineNote note = SpamPipeline.Notes(config)
            .FirstOrDefault(n => n.Level == StatusLevel.Information && n.Text.Contains("stops testing", StringComparison.OrdinalIgnoreCase));

         Assert.True(note != null, "The early stop is not explained anywhere.");
         Assert.Contains("HELO host check", note.Text, StringComparison.Ordinal);
         Assert.Contains("PTR record check", note.Text, StringComparison.Ordinal);
      }

      /// <summary>
      /// The scan size limit applies only to the checks that need the body, which the
      /// label on the setting ("Max message size to spam-scan") does not say.
      /// </summary>
      [Fact]
      public void TheScanSizeLimit_SaysWhichChecksItSkips()
      {
         SpamPipelineConfig config = Defaults();
         config.MaxScanKilobytes = 256;

         SpamPipelineNote note = SpamPipeline.Notes(config)
            .FirstOrDefault(n => n.Text.Contains("256 KB", StringComparison.Ordinal));

         Assert.True(note != null, "The scan size limit is not mentioned.");
         Assert.Contains("SURBL", note.Text, StringComparison.Ordinal);
         Assert.Contains("SpamAssassin", note.Text, StringComparison.Ordinal);
      }

      /// <summary>
      /// With no limit set there is still one: RunPostTransmissionTests will not scan
      /// beyond what the MIME parser can load, and scanning without loading the
      /// message would write an empty body over it.
      /// </summary>
      [Fact]
      public void WithNoScanLimit_TheParserCeilingIsStillStated()
      {
         Assert.Contains(SpamPipeline.Notes(Defaults()),
            n => n.Text.Contains("80 MB", StringComparison.Ordinal));
      }

      /// <summary>
      /// The scope of the whole thing. "Spam is getting through from one of my own
      /// users" is answered by this sentence and by nothing else in the interface.
      /// </summary>
      [Fact]
      public void TheScopeOfSpamProtection_IsAlwaysStated()
      {
         Assert.Contains(SpamPipeline.Notes(Defaults()),
            n => n.Text.Contains("authenticated session", StringComparison.OrdinalIgnoreCase)
                 && n.Text.Contains("white list", StringComparison.OrdinalIgnoreCase));
      }

      /// <summary>
      /// A list with entries and none of them active does not run, and neither DNSBL
      /// nor SURBL has a switch of its own to say so - so "3 configured" and "3
      /// active" have to be told apart on the page.
      /// </summary>
      [Fact]
      public void AListWithNoActiveEntries_ReadsAsNotRunning()
      {
         SpamPipelineConfig config = Defaults();
         config.TotalDnsBlackLists = 3;
         config.ActiveDnsBlackLists = 0;

         SpamCheck dnsbl = SpamPipeline.Checks(config).Single(c => c.Key == "dnsbl");

         Assert.False(dnsbl.Enabled);
         Assert.Contains("none active", dnsbl.Detail, StringComparison.OrdinalIgnoreCase);
      }

      [Fact]
      public void AnActiveList_ReadsAsRunningWithAPerEntryScore()
      {
         SpamPipelineConfig config = Defaults();
         config.TotalSurblServers = 2;
         config.ActiveSurblServers = 1;

         SpamCheck surbl = SpamPipeline.Checks(config).Single(c => c.Key == "surbl");

         Assert.True(surbl.Enabled);
         Assert.Null(surbl.Score);
         Assert.Equal("per list entry", surbl.ScoreText);
         Assert.Contains("1 of 2 active", surbl.Detail, StringComparison.Ordinal);
      }

      /// <summary>
      /// Greylisting is not a scoring check and must not be presented as one; the two
      /// bypasses are the usual reason it "does not work", and the per-domain switch
      /// is on the domain, where no anti-spam page mentions it.
      /// </summary>
      [Fact]
      public void Greylisting_IsDescribedAsADeferralWithItsBypasses()
      {
         SpamPipelineConfig config = Defaults();
         config.GreylistingEnabled = true;
         config.BypassGreylistingOnSpfPass = true;
         config.GreylistWhiteListEntries = 4;

         string summary = SpamPipeline.GreylistingSummary(config);

         Assert.DoesNotContain(SpamPipeline.Checks(config), c => c.Name.Contains("reylist", StringComparison.Ordinal));
         Assert.Contains("451", summary, StringComparison.Ordinal);
         Assert.Contains("adds no score", summary, StringComparison.OrdinalIgnoreCase);
         Assert.Contains("SPF check passed", summary, StringComparison.Ordinal);
         Assert.Contains("4", summary, StringComparison.Ordinal);
         Assert.Contains("recipient domain", summary, StringComparison.OrdinalIgnoreCase);
      }

      [Fact]
      public void GreylistingOff_SaysSoAndNothingElse()
      {
         SpamPipelineConfig config = Defaults();
         config.GreylistingEnabled = false;

         Assert.StartsWith("Off.", SpamPipeline.GreylistingSummary(config), StringComparison.Ordinal);
      }

      [Fact]
      public void EveryNote_HasSomethingToSay()
      {
         var configs = new List<SpamPipelineConfig> { Defaults(), new SpamPipelineConfig() };

         foreach (SpamPipelineConfig config in configs)
         {
            foreach (SpamPipelineNote note in SpamPipeline.Notes(config))
            {
               Assert.False(string.IsNullOrWhiteSpace(note.Text), "A note with no text.");
               Assert.Contains(note.Level, StatusSemantics.AllLevels);
            }
         }
      }

      /// <summary>Nothing here may throw on a null configuration - the page builds one before the server answers.</summary>
      [Fact]
      public void ANullConfiguration_IsEmptyRatherThanAnException()
      {
         Assert.Empty(SpamPipeline.Checks(null));
         Assert.Empty(SpamPipeline.Notes(null));
         Assert.Equal("", SpamPipeline.Verdict(null));
         Assert.Equal("", SpamPipeline.GreylistingSummary(null));
         Assert.Equal(0, SpamPipeline.StopScore(null));
         Assert.Null(SpamPipeline.HighestReachableScore(null));
         Assert.Equal(StatusLevel.Normal, SpamPipeline.WorstLevel(null));
      }

      // ---- the page in the navigation -----------------------------------------

      /// <summary>
      /// The overview is the first thing in the group. It is the page an
      /// administrator opening "Spam &amp; virus filtering" should meet first, and
      /// putting it below five editors would leave the group looking exactly as it
      /// did.
      /// </summary>
      [Fact]
      public void TheOverview_IsTheFirstPageOfTheSpamGroup()
      {
         NavNode group = NavigationMap.Groups.Single(g => g.Title == "Spam & virus filtering");

         Assert.Equal("spamoverview", group.Children.First(c => c.IsPage).Key);
         Assert.Equal("Spam & virus filtering", NavigationMap.GroupOf("spamoverview"));
      }

      /// <summary>
      /// Every spam page in the group is reachable from the overview. This is the
      /// consistency that makes it an overview rather than a sixth page: if a page is
      /// added to the group, or renamed, or moved, this fails until the overview
      /// knows about it.
      ///
      /// The three anti-virus pages are deliberately excluded - they are in the same
      /// group but not part of the spam score - and so is the overview itself.
      /// `virusoverview` joined that list when it was added: it is the virus half's
      /// own overview, and a row on the spam overview links to the page that owns a
      /// spam check, which no virus page does.
      /// </summary>
      [Fact]
      public void TheOverview_LinksToEverySpamPageInItsGroup()
      {
         NavNode group = NavigationMap.Groups.Single(g => g.Title == "Spam & virus filtering");

         var spamPages = group.Children
            .Where(c => c.IsPage)
            .Select(c => c.Key)
            .Where(key => key != "spamoverview"
                          && key != "virusoverview" && key != "antivirus" && key != "blockedattachments")
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

         Assert.Equal(spamPages, SpamPipeline.LinkedPages.OrderBy(p => p, StringComparer.Ordinal));
      }

      [Fact]
      public void EveryPageTheOverviewLinksTo_Exists()
      {
         foreach (string page in SpamPipeline.LinkedPages)
            Assert.True(NavigationMap.Find(page) != null, "The overview links to '" + page + "', which is not a page.");

         foreach (SpamCheck check in SpamPipeline.Checks(Defaults()))
         {
            Assert.True(NavigationMap.Find(check.Page) != null,
               check.Name + " links to '" + check.Page + "', which is not a page.");
            Assert.Contains(check.Page, SpamPipeline.LinkedPages);
         }
      }

      /// <summary>
      /// The questions this page exists to answer, typed as they would be typed. Each
      /// one returned nothing at all before it existed.
      /// </summary>
      [Theory]
      [InlineData("what is my spam configuration")]
      [InlineData("which spam checks are running")]
      [InlineData("spam overview")]
      [InlineData("spam pipeline")]
      public void TheOverview_IsFoundByTheQuestionsItAnswers(string query)
      {
         List<string> pages = PaletteSearch.Query(query, null, null)
            .Where(r => r.IsSelectable)
            .Take(3)
            .Select(r => r.Page)
            .ToList();

         Assert.Contains("spamoverview", pages);
      }
   }
}
