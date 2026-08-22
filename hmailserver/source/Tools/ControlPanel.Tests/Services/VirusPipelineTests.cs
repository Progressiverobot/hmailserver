// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The virus scanning overview: the page that answers "will an infected message
   /// actually be caught here".
   ///
   /// The anti-virus settings page is a correct editor for each value and cannot
   /// answer that, because the answer is the interaction of four of them. Every
   /// behaviour pinned below was read out of the server rather than assumed, and
   /// every one of them is a way the configuration silently does less than it looks
   /// like it does:
   ///
   ///   - VirusScanner::ScanFile_ reports a scanner error through ErrorManager at
   ///     Medium and carries on. When every enabled scanner errors, the result is
   ///     NoVirusFound - so a scanner switched on with a missing host or executable
   ///     delivers mail exactly as though it had been examined and found clean;
   ///   - VirusScanner::Scan skips the message when `limit > 0 && messageKB > limit`,
   ///     so 0 is "no limit" and any positive number is a hole. That is the opposite
   ///     way round from every other size limit in the product;
   ///   - SMTPDeliverer::HandleInfectedMessage_ only sends the notifications on the
   ///     DELETE branch, so choosing "strip attachments" silently disables both
   ///     notification settings while they still read as on;
   ///   - SMTPDeliverer::RunVirusProtection_ blocks attachments BEFORE it checks the
   ///     virus-scan flag or whether any scanner is enabled, so attachment blocking
   ///     is independent of all of it;
   ///   - and a message only reaches the scanners at all when its virus-scan flag is
   ///     set, which for POP3-fetched mail comes from the fetch account rather than
   ///     from anything on the anti-virus page.
   /// </summary>
   public class VirusPipelineTests
   {
      /// <summary>
      /// hMailServer's own defaults, from DBScripts\CreateTables*.sql: every scanner
      /// off, ClamAV pre-filled with localhost:3310 but not enabled, avaction 0
      /// (delete), avmaxmsgsize 0, both notifications off, and - the interesting one
      /// - enableattachmentblocking 0 with roughly twenty *.bat/*.exe/... patterns
      /// seeded into hm_blocked_attachments beside it.
      /// </summary>
      private static VirusPipelineConfig Defaults()
      {
         return new VirusPipelineConfig
         {
            ClamAvEnabled = false,
            ClamAvHost = "localhost",
            ClamAvPort = 3310,
            ClamWinEnabled = false,
            CustomScannerEnabled = false,
            MaxScanKilobytes = 0,
            Action = VirusAction.DeleteMessage,
            NotifySender = false,
            NotifyRecipient = false,
            AttachmentBlockingEnabled = false,
            BlockedAttachmentPatterns = 20
         };
      }

      /// <summary>A server that is actually scanning: clamd reachable, delete on find.</summary>
      private static VirusPipelineConfig Scanning()
      {
         VirusPipelineConfig config = Defaults();
         config.ClamAvEnabled = true;
         config.AttachmentBlockingEnabled = true;
         return config;
      }

      private static bool HasNote(VirusPipelineConfig config, StatusLevel level, string fragment) =>
         VirusPipeline.Notes(config).Any(n => n.Level == level && n.Text.Contains(fragment));

      // ---- the shipped default ------------------------------------------------

      [Fact]
      public void AStockInstallationScansNothingAndSaysSo()
      {
         VirusPipelineConfig config = Defaults();

         Assert.Equal(0, VirusPipeline.CountUsable(config));

         // The single most important sentence on the page, for the configuration
         // the largest number of servers are actually in.
         Assert.Contains("No message is scanned for viruses", VirusPipeline.Verdict(config));
         Assert.Equal(StatusLevel.Critical, VirusPipeline.WorstLevel(config));
         Assert.True(HasNote(config, StatusLevel.Critical, "Nothing examines incoming mail"));
      }

      [Fact]
      public void PatternsConfiguredWithBlockingOffAreReportedAsUnapplied()
      {
         // The shipped state, and it looks like protection from the settings page:
         // twenty file-name patterns are listed on Blocked attachments and not one
         // of them is applied, because the switch that turns the feature on is
         // seeded off.
         VirusPipelineConfig config = Defaults();

         Assert.True(HasNote(config, StatusLevel.Information, "configured but attachment blocking is switched off"));
      }

      // ---- enabled but unable to run ------------------------------------------

      [Fact]
      public void AnEnabledScannerWithNoHostIsReportedAsUnableToRun()
      {
         VirusPipelineConfig config = Defaults();
         config.ClamAvEnabled = true;
         config.ClamAvHost = "";

         VirusScannerEntry clamAv = VirusPipeline.Scanners(config).Single(s => s.Key == "clamav");

         Assert.True(clamAv.Enabled);
         Assert.False(clamAv.Usable);
         Assert.Equal("On, but cannot run", clamAv.StateText);
         Assert.Equal(0, VirusPipeline.CountUsable(config));

         // The consequence, not just the fact - this is the sentence that explains
         // why a broken scanner is worse than no scanner.
         Assert.True(HasNote(config, StatusLevel.Critical, "delivered as though it had been examined and found clean"));
      }

      [Fact]
      public void APortOfZeroCountsAsUnableToRun()
      {
         VirusPipelineConfig config = Defaults();
         config.ClamAvEnabled = true;
         config.ClamAvPort = 0;

         Assert.False(VirusPipeline.Scanners(config).Single(s => s.Key == "clamav").Usable);
         Assert.Equal(0, VirusPipeline.CountUsable(config));
      }

      [Fact]
      public void ClamWinNeedsBothTheExecutableAndTheDatabaseFolder()
      {
         // ClamWinVirusScanner builds `--database="<folder>"` into the command line,
         // so a missing folder is not a scanner running with a default - it is a
         // scanner invoked with an empty argument.
         VirusPipelineConfig config = Defaults();
         config.ClamWinEnabled = true;
         config.ClamWinExecutable = @"C:\Program Files\ClamWin\bin\clamscan.exe";
         config.ClamWinDatabaseFolder = "";

         VirusScannerEntry clamWin = VirusPipeline.Scanners(config).Single(s => s.Key == "clamwin");

         Assert.False(clamWin.Usable);
         Assert.Contains("signature database folder", clamWin.Problem);
      }

      [Fact]
      public void ACustomScannerReturnValueOfZeroIsNotTreatedAsMissing()
      {
         // The return code that means "infected" is a number, and 0 is a legitimate
         // choice of number. Only the executable is required.
         VirusPipelineConfig config = Defaults();
         config.CustomScannerEnabled = true;
         config.CustomScannerExecutable = @"C:\scanner.exe";
         config.CustomScannerVirusReturnValue = 0;

         Assert.True(VirusPipeline.Scanners(config).Single(s => s.Key == "custom").Usable);
      }

      // ---- the order --------------------------------------------------------

      [Fact]
      public void ScannersAreListedInTheOrderTheServerTriesThem()
      {
         // VirusScanner::ScanFile_ asks ClamWin, then the custom scanner, then
         // ClamAV. The page states an order, so the order has to be the real one.
         string[] keys = VirusPipeline.Scanners(Defaults()).Select(s => s.Key).ToArray();

         Assert.Equal(new[] { "clamwin", "custom", "clamav" }, keys);
         Assert.Equal(new[] { 1, 2, 3 }, VirusPipeline.Scanners(Defaults()).Select(s => s.Order).ToArray());
      }

      // ---- the size limit, which is backwards -------------------------------

      [Fact]
      public void ASizeLimitOfZeroMeansEverythingIsScanned()
      {
         VirusPipelineConfig config = Scanning();
         config.MaxScanKilobytes = 0;

         Assert.Contains("Every message is scanned", VirusPipeline.Verdict(config));
         Assert.False(HasNote(config, StatusLevel.Warning, "are not scanned"));
      }

      [Fact]
      public void APositiveSizeLimitIsReportedAsAGapRatherThanAsProtection()
      {
         VirusPipelineConfig config = Scanning();
         config.MaxScanKilobytes = 1024;

         Assert.Contains("delivered without being scanned", VirusPipeline.Verdict(config));

         // Named as a warning, and the note has to say the message is delivered
         // rather than refused - that is the part an administrator gets wrong.
         Assert.True(HasNote(config, StatusLevel.Warning, "are not scanned"));
         Assert.True(HasNote(config, StatusLevel.Warning, "delivered, not refused"));
      }

      [Fact]
      public void TheSizeLimitIsShownInMegabytesOnceKilobytesStopBeingReadable()
      {
         Assert.Equal("512 KB", VirusPipeline.Kilobytes(512));
         Assert.Equal("10,240 KB (10 MB)", VirusPipeline.Kilobytes(10240));
      }

      // ---- what happens on a find -------------------------------------------

      [Fact]
      public void StrippingAttachmentsSaysTheMessageIsStillDelivered()
      {
         VirusPipelineConfig config = Scanning();
         config.Action = VirusAction.StripAttachments;

         Assert.Contains("still delivered", VirusPipeline.Verdict(config));
         Assert.True(HasNote(config, StatusLevel.Warning, "still receives the message body"));
      }

      [Fact]
      public void TheNotificationsAreReportedAsInertWhenTheActionIsToStrip()
      {
         // HandleInfectedMessage_ sends the notifications only on the delete branch.
         // Both switches still read as on in the settings page, so the overview has
         // to be the thing that says they do nothing.
         VirusPipelineConfig config = Scanning();
         config.Action = VirusAction.StripAttachments;
         config.NotifySender = true;
         config.NotifyRecipient = true;

         Assert.Contains("Neither notification setting applies", VirusPipeline.ActionSummary(config));

         // And the backscatter warning must NOT fire, because nothing is sent.
         Assert.False(HasNote(config, StatusLevel.Warning, "backscatter"));
      }

      [Fact]
      public void NotifyingTheSenderOfAVirusIsCalledOutAsBackscatter()
      {
         VirusPipelineConfig config = Scanning();
         config.Action = VirusAction.DeleteMessage;
         config.NotifySender = true;

         Assert.Contains("the envelope sender", VirusPipeline.ActionSummary(config));
         Assert.True(HasNote(config, StatusLevel.Warning, "backscatter"));
      }

      [Fact]
      public void DeletingWithNoNotificationsSaysNobodyIsTold()
      {
         VirusPipelineConfig config = Scanning();

         Assert.Contains("nobody is told", VirusPipeline.ActionSummary(config));
      }

      // ---- the ways round the whole page --------------------------------------

      [Fact]
      public void FetchAccountsWithScanningOffAreReportedBecauseTheSwitchIsElsewhere()
      {
         VirusPipelineConfig config = Scanning();
         config.FetchAccountsTotal = 3;
         config.FetchAccountsWithScanningOff = 2;

         Assert.True(HasNote(config, StatusLevel.Warning, "delivered unscanned no matter what is set here"));
      }

      [Fact]
      public void ATruncatedFetchAccountWalkSaysAtLeastRatherThanStatingATotal()
      {
         // The walk is one COM round trip per account and is capped so the page stays
         // responsive on a large server. A capped count presented as a total would be
         // a number that reads as reassuring precisely when it is least reliable.
         VirusPipelineConfig config = Scanning();
         config.FetchAccountsTotal = 1000;
         config.FetchAccountsWithScanningOff = 2;
         config.FetchAccountScanIncomplete = true;

         Assert.True(HasNote(config, StatusLevel.Warning, "At least 2 external POP3 fetch accounts have"));
         Assert.True(HasNote(config, StatusLevel.Warning, "there may be others"));

         config.FetchAccountScanIncomplete = false;

         Assert.False(HasNote(config, StatusLevel.Warning, "At least 2"));
         Assert.False(HasNote(config, StatusLevel.Warning, "there may be others"));
      }

      [Fact]
      public void AttachmentBlockingOnWithNoPatternsIsReportedAsDoingNothing()
      {
         VirusPipelineConfig config = Scanning();
         config.BlockedAttachmentPatterns = 0;

         Assert.True(HasNote(config, StatusLevel.Warning, "no patterns to block"));
      }

      [Fact]
      public void BlockingStillCountsWhenNoScannerCanRun()
      {
         // RunVirusProtection_ blocks attachments before it looks at the scanners at
         // all, so "no scanner" and "nothing at all" are different states and the
         // page must not collapse them.
         VirusPipelineConfig config = Defaults();
         config.AttachmentBlockingEnabled = true;

         Assert.Contains("Attachment blocking is still stripping", VirusPipeline.Verdict(config));
         Assert.False(HasNote(config, StatusLevel.Critical, "Nothing examines incoming mail"));
         Assert.True(HasNote(config, StatusLevel.Critical, "No virus scanner can run"));
      }

      // ---- the shared vocabulary ---------------------------------------------

      [Fact]
      public void EveryNoteCarriesTextAndAUsableLevel()
      {
         // The page renders the level through StatusSemantics, which has a
         // presentation for every level; a note with no text would render as a bare
         // severity word and say nothing.
         var configs = new List<VirusPipelineConfig>
         {
            Defaults(),
            Scanning(),
            new() { ClamAvEnabled = true, ClamAvHost = "", MaxScanKilobytes = 500, NotifySender = true }
         };

         foreach (VirusPipelineConfig config in configs)
         {
            foreach (VirusPipelineNote note in VirusPipeline.Notes(config))
            {
               Assert.False(string.IsNullOrWhiteSpace(note.Text));
               Assert.NotNull(StatusSemantics.For(note.Level));
            }
         }
      }

      [Fact]
      public void ANullConfigurationIsSurvivedRatherThanThrown()
      {
         // ReadConfig hands back whatever it managed to read when COM fails part-way,
         // and the page draws it. Nothing here may throw on the degraded case.
         Assert.Empty(VirusPipeline.Scanners(null));
         Assert.Empty(VirusPipeline.Notes(null));
         Assert.Equal(0, VirusPipeline.CountUsable(null));
         Assert.Equal(StatusLevel.Normal, VirusPipeline.WorstLevel(null));
         Assert.False(string.IsNullOrWhiteSpace(VirusPipeline.Verdict(null)));
      }
   }
}
