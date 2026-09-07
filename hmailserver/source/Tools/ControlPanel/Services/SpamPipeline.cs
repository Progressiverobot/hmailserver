// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Which half of the SMTP conversation a check runs in. The distinction is not
   /// cosmetic: it decides what the check can look at, what the sender is told when
   /// it refuses, and whether the message size limit applies to it.
   /// </summary>
   public enum SpamCheckPhase
   {
      /// <summary>
      /// At RCPT TO, before the body has been transferred. These checks see only
      /// the connection: the connecting address, the HELO name and the envelope
      /// sender. A refusal here is a 550 and the body is never sent.
      /// </summary>
      BeforeTheBody,

      /// <summary>
      /// After the body has been received. These checks read the message, so they
      /// are the ones the "max message size to spam-scan" limit skips. A refusal
      /// here is a 554 - the sender has already paid for the transfer.
      /// </summary>
      AfterTheBody
   }

   /// <summary>One spam check, as the server will actually run it.</summary>
   public sealed class SpamCheck
   {
      internal SpamCheck(string key, int order, string name, SpamCheckPhase phase, bool enabled,
         int? score, string scoreText, string detail, string page)
      {
         Key = key;
         Order = order;
         Name = name;
         Phase = phase;
         Enabled = enabled;
         Score = score;
         ScoreText = scoreText;
         Detail = detail;
         Page = page;
      }

      /// <summary>
      /// Stable identifier, so a test can name a check without depending on its
      /// wording and the view can key on it without matching a display string.
      /// </summary>
      public string Key { get; }

      /// <summary>
      /// Position in the server's own list, 1-based. SpamTestRunner::LoadSpamTests
      /// pushes the tests in a fixed order and RunSpamTest walks that order, so
      /// this is the order they run in - which matters because the run stops early
      /// (see <see cref="SpamPipeline.StopScore"/>).
      /// </summary>
      public int Order { get; }

      /// <summary>What the check is called in the interface, not the C++ class name.</summary>
      public string Name { get; }

      public SpamCheckPhase Phase { get; }

      /// <summary>
      /// Whether the server will run it. This is the check's own GetIsEnabled, not
      /// a guess: for DNSBL and SURBL there is no on/off switch at all and the
      /// answer is "at least one list entry is active".
      /// </summary>
      public bool Enabled { get; }

      /// <summary>
      /// What a failure adds to the score, or null when the number is not a single
      /// value we can state - a per-entry list score, or SpamAssassin's own score
      /// when merging is on.
      /// </summary>
      public int? Score { get; }

      /// <summary>The score as it should be shown, including the null cases.</summary>
      public string ScoreText { get; }

      /// <summary>One line: what the check looks at, and anything surprising about it.</summary>
      public string Detail { get; }

      /// <summary>Navigation key of the page that owns the settings behind it.</summary>
      public string Page { get; }
   }

   /// <summary>
   /// Something worth saying about the configuration as a whole, carrying a
   /// <see cref="StatusLevel"/> so the page can present it with the shared colour,
   /// shape and severity word rather than inventing a third vocabulary.
   /// </summary>
   public sealed class SpamPipelineNote
   {
      internal SpamPipelineNote(StatusLevel level, string text)
      {
         Level = level;
         Text = text;
      }

      public StatusLevel Level { get; }

      /// <summary>The whole note, in one sentence or two. Never empty.</summary>
      public string Text { get; }
   }

   /// <summary>
   /// A snapshot of the anti-spam configuration, read from the COM settings tree.
   ///
   /// Plain properties with no behaviour so that <see cref="SpamPipeline"/> can be
   /// tested without a server: every interesting case here - a threshold of zero,
   /// a delete threshold below the mark threshold, a check that scores nothing - is
   /// a state a live server can be in and a state that is tedious and slow to
   /// produce against one.
   ///
   /// Property names match the COM paths the settings pages use
   /// ("AntiSpam.SpamMarkThreshold" -> MarkThreshold is the one exception, because
   /// "SpamMarkThreshold" on a class already called spam configuration stutters).
   /// </summary>
   public sealed class SpamPipelineConfig
   {
      // Thresholds. Both are compared with > 0 before they are used at all, which
      // is the single most surprising thing in this whole area.
      public int MarkThreshold { get; set; }
      public int DeleteThreshold { get; set; }

      /// <summary>AntiSpam.MaximumMessageSize, in KB. 0 = no limit of our own.</summary>
      public int MaxScanKilobytes { get; set; }

      /// <summary>How many blocked-sender entries exist. The blacklist has no
      /// on/off switch - an empty list IS the off state - so the count is the
      /// enablement.</summary>
      public int BlockedSenderCount { get; set; }

      // What "marked" actually does to the message.
      public bool AddSpamHeader { get; set; }
      public bool AddReasonHeader { get; set; }
      public bool PrependSubject { get; set; }

      // The connection checks.
      public bool CheckHeloHost { get; set; }
      public int HeloHostScore { get; set; }
      public bool CheckPtr { get; set; }
      public int PtrScore { get; set; }
      public bool CheckSenderMx { get; set; }
      public int SenderMxScore { get; set; }
      public bool CheckSpf { get; set; }
      public int SpfScore { get; set; }

      // The message checks.
      public bool VerifyDkim { get; set; }
      public int DkimFailureScore { get; set; }
      public bool EvaluateDmarc { get; set; }
      public int DmarcFailureScore { get; set; }

      public bool SpamAssassinEnabled { get; set; }
      public bool SpamAssassinMergesScore { get; set; }
      public int SpamAssassinScore { get; set; }
      public string SpamAssassinHost { get; set; }

      // The two list-driven checks have no switch of their own.
      public int ActiveDnsBlackLists { get; set; }
      public int TotalDnsBlackLists { get; set; }
      public int ActiveSurblServers { get; set; }
      public int TotalSurblServers { get; set; }

      // Greylisting is not a scoring check; it defers instead. Kept here because
      // the overview has to describe it, and because its two bypasses are the
      // reason an administrator reports that "greylisting is not working".
      public bool GreylistingEnabled { get; set; }
      public bool BypassGreylistingOnSpfPass { get; set; }
      public bool BypassGreylistingOnSenderMx { get; set; }
      public int GreylistWhiteListEntries { get; set; }

      /// <summary>Anti-spam white list entries: senders that skip every check above.</summary>
      public int WhiteListEntries { get; set; }
   }

   /// <summary>
   /// What the spam configuration will do to a message, derived from the settings
   /// rather than described by hand.
   ///
   /// WHY THIS EXISTS. Anti-spam was five pages - thresholds and checks, SURBL
   /// servers, DNS blacklists, a white list and a greylisting white list - and none
   /// of them could answer the question an administrator arrives with, because the
   /// answer is not on any one page. It needs the order the checks run in, what each
   /// one contributes, and the two thresholds that turn a total into a verdict.
   ///
   /// Everything asserted here was read out of the server, and three of the findings
   /// are the reason the page is worth building rather than a nicety:
   ///
   ///   - SMTPConnection::DoSpamProtection_ guards both thresholds with "> 0". A
   ///     mark threshold of zero does not mean "mark everything", it means "mark
   ///     nothing" - and nothing in the interface said so.
   ///   - SpamTestRunner::RunSpamTest breaks out of the loop as soon as the running
   ///     total reaches max(delete, mark). Checks later in the fixed order simply do
   ///     not run, so the order is load-bearing; and with both thresholds at zero
   ///     that condition is true immediately, so exactly one check runs per phase.
   ///   - the delete threshold is tested before the mark threshold, so a delete
   ///     threshold at or below the mark threshold means nothing is ever marked and
   ///     delivered - it is refused instead.
   ///
   /// No WPF, for the same reason as <see cref="NavigationMap"/> and
   /// <see cref="AccessibleNames"/>: the statements about the server are the part
   /// worth testing, and they must be testable without a live server and without a
   /// dispatcher.
   /// </summary>
   public static class SpamPipeline
   {
      /// <summary>
      /// The hard ceiling in SpamProtection::RunPostTransmissionTests, in KB: above
      /// it the body checks are skipped whatever the configured limit is, because
      /// MessageData::LoadFromMessage will not load the message.
      /// </summary>
      public const int ParserSizeLimitKilobytes = 1024 * 80;

      /// <summary>
      /// Pages this overview links to. Every spam page in the navigation must be
      /// reachable from here - an overview that leaves one out sends the reader
      /// back to hunting for it, which is the problem it was built to remove.
      /// A test asserts this list against the navigation group, which is how the
      /// quarantine page was caught the moment it was added.
      /// </summary>
      public static IReadOnlyList<string> LinkedPages { get; } = new[]
      {
         "antispam", "blockedsenders", "dnsbl", "quarantine", "surbl", "spamwhitelist", "greylistwhitelist"
      };

      /// <summary>
      /// The ten scoring checks, in the order SpamTestRunner runs them.
      ///
      /// Greylisting is deliberately not one of them: it defers a message instead
      /// of scoring it, and folding a deferral into a score table would be the same
      /// class of untruth this page exists to expose. See
      /// <see cref="GreylistingSummary"/>.
      /// </summary>
      public static IReadOnlyList<SpamCheck> Checks(SpamPipelineConfig config)
      {
         if (config == null)
            return Array.Empty<SpamCheck>();

         var checks = new List<SpamCheck>
         {
            // 1-6: SpamTest::PreTransmission. Run at MAIL FROM by default
            // (DNSBLChecksAfterMailFrom=1), at the first RCPT when that is 0.
            //
            // The blacklist is registered first in SpamTestRunner::LoadSpamTests
            // on purpose: it is the cheapest test, and with the default score a
            // match ends the run before any DNS lookup happens.
            new SpamCheck("blockedsenders", 1, L("Blocked senders"), SpamCheckPhase.BeforeTheBody,
               config.BlockedSenderCount > 0, null,
               config.BlockedSenderCount > 0 ? L("per entry") : "-",
               config.BlockedSenderCount > 0
                  ? F("Matches the envelope sender against {0} blocked address(es) and domain(s); the score comes from the entry that matched.", config.BlockedSenderCount)
                  : L("No blocked senders are defined, so nothing is matched. The list has no on/off switch - an empty list is the off state."),
               "blockedsenders"),

            new SpamCheck("dnsbl", 2, L("DNS blacklists (DNSBL)"), SpamCheckPhase.BeforeTheBody,
               config.ActiveDnsBlackLists > 0, null,
               config.ActiveDnsBlackLists > 0 ? L("per list entry") : "-",
               DescribeList(L("Looks up the connecting address on each active blacklist; the score comes from the entry that matched"),
                  config.ActiveDnsBlackLists, config.TotalDnsBlackLists),
               "dnsbl"),

            new SpamCheck("helo", 3, L("HELO host check"), SpamCheckPhase.BeforeTheBody,
               config.CheckHeloHost, config.HeloHostScore, Score(config.HeloHostScore),
               L("Fails when the name the client gave in HELO/EHLO does not resolve to the address it is connecting from."),
               "antispam"),

            new SpamCheck("ptr", 4, L("PTR record check"), SpamCheckPhase.BeforeTheBody,
               config.CheckPtr, config.PtrScore, Score(config.PtrScore),
               L("Fails when the connecting address has no reverse DNS record."),
               "antispam"),

            new SpamCheck("mx", 5, L("Sender MX check"), SpamCheckPhase.BeforeTheBody,
               config.CheckSenderMx, config.SenderMxScore, Score(config.SenderMxScore),
               L("Fails when the envelope sender's domain publishes no MX record."),
               "antispam"),

            new SpamCheck("spf", 6, L("SPF"), SpamCheckPhase.BeforeTheBody,
               config.CheckSpf, config.SpfScore, Score(config.SpfScore),
               L("Checks the sender's SPF policy against the connecting address. A pass can also bypass greylisting."),
               "antispam"),

            // 6-9: SpamTest::PostTransmission, run once the body has arrived.
            new SpamCheck("surbl", 7, L("SURBL servers"), SpamCheckPhase.AfterTheBody,
               config.ActiveSurblServers > 0, null,
               config.ActiveSurblServers > 0 ? L("per list entry") : "-",
               DescribeList(L("Looks up the domains of links found in the body on each active SURBL server"),
                  config.ActiveSurblServers, config.TotalSurblServers),
               "surbl"),

            new SpamCheck("dkim", 8, L("DKIM verification"), SpamCheckPhase.AfterTheBody,
               config.VerifyDkim, config.DkimFailureScore, Score(config.DkimFailureScore),
               L("Scores only a permanent failure - a signature that is present and wrong. An unsigned message scores nothing."),
               "antispam"),

            new SpamCheck("dmarc", 9, L("DMARC"), SpamCheckPhase.AfterTheBody,
               config.EvaluateDmarc, config.DmarcFailureScore, Score(config.DmarcFailureScore),
               L("Evaluates the From: domain's DMARC policy, using its own SPF and DKIM results."),
               "antispam"),

            new SpamCheck("spamassassin", 10, "SpamAssassin", SpamCheckPhase.AfterTheBody,
               config.SpamAssassinEnabled,
               config.SpamAssassinMergesScore ? (int?) null : config.SpamAssassinScore,
               config.SpamAssassinMergesScore ? L("SpamAssassin's own score") : Score(config.SpamAssassinScore),
               F("Scores only when SpamAssassin itself tags the message (X-Spam-Status: Yes). {0}", (config.SpamAssassinMergesScore ? L("Merging is on, so its own numeric score is added - which can be any size.") : L("Merging is off, so the fixed score is used however sure SpamAssassin was."))),
               "antispam")
         };

         return checks;
      }

      /// <summary>
      /// What the total score does to the message, in one sentence, including the
      /// two cases where the answer is "nothing".
      /// </summary>
      public static string Verdict(SpamPipelineConfig config)
      {
         if (config == null)
            return "";

         bool marks = config.MarkThreshold > 0;
         bool refuses = config.DeleteThreshold > 0;

         if (marks && refuses)
         {
            return F("A message scoring {0} or more is marked as spam and delivered; {1} or more is refused (550 before the body, 554 after it).", config.MarkThreshold, config.DeleteThreshold);
         }

         if (marks)
            return F("A message scoring {0} or more is marked as spam and delivered. Nothing is refused on score.", config.MarkThreshold);

         if (refuses)
            return F("A message scoring {0} or more is refused (550 before the body, 554 after it). Nothing is marked.", config.DeleteThreshold);

         return L("Nothing at all happens on score: both thresholds are 0, and the server only marks or refuses when the threshold is above 0.");
      }

      /// <summary>
      /// The score at which the server stops testing - max(delete, mark), copied
      /// from SpamProtection::RunPreTransmissionTests, which passes it to
      /// RunSpamTest as the maximum. Counted per phase: the total resets between
      /// the connection checks and the message checks, while the verdict is taken
      /// on the sum of both.
      /// </summary>
      public static int StopScore(SpamPipelineConfig config)
         => config == null ? 0 : Math.Max(config.DeleteThreshold, config.MarkThreshold);

      /// <summary>
      /// The most the enabled checks could add between them, or null when that
      /// cannot be stated: an active DNSBL or SURBL carries its score per list
      /// entry, and a merging SpamAssassin contributes whatever score it produced.
      /// Null means "unknown", never "zero" - claiming a ceiling we cannot compute
      /// is how a page starts lying.
      /// </summary>
      public static int? HighestReachableScore(SpamPipelineConfig config)
      {
         if (config == null)
            return null;

         int total = 0;

         foreach (SpamCheck check in Checks(config).Where(c => c.Enabled))
         {
            if (check.Score == null)
               return null;

            total += check.Score.Value;
         }

         return total;
      }

      /// <summary>
      /// Greylisting, described rather than scored. Deliberately says out loud that
      /// the two bypasses exist, because "greylisting is enabled and mail from that
      /// host is not being delayed" is nearly always one of them.
      /// </summary>
      public static string GreylistingSummary(SpamPipelineConfig config)
      {
         if (config == null)
            return "";

         if (!config.GreylistingEnabled)
            return L("Off. No message is deferred on a first attempt.");

         var bypasses = new List<string>();
         if (config.BypassGreylistingOnSpfPass)
            bypasses.Add(L("the sender's SPF check passed"));
         if (config.BypassGreylistingOnSenderMx)
            bypasses.Add(L("the connecting address is one of the sender domain's A or MX records"));
         if (config.GreylistWhiteListEntries > 0)
            bypasses.Add(F("the address is one of the {0} on the greylisting white list", config.GreylistWhiteListEntries));

         string text = L("On. A sender/recipient/address triplet that has not been seen before is answered 451 \"Please try again later.\" at RCPT TO, and accepted when it retries. It runs after the connection checks above and adds no score.");

         if (bypasses.Count > 0)
            text += F(" Skipped when {0}.", Join(bypasses));

         // A per-domain switch as well, which is on the domain and not on any of
         // the anti-spam pages - so an administrator whose greylisting "is not
         // working" for one domain has nowhere else to be told this.
         text += L(" Also skipped for any recipient domain that has greylisting turned off in its own settings.");

         return text;
      }

      /// <summary>
      /// Everything worth saying about the configuration as a whole: first the
      /// arrangements that cannot work, then the ones that waste a lookup, then the
      /// facts an administrator cannot see anywhere in the interface.
      ///
      /// Ordered by severity so that the page can render them in order and a reader
      /// who stops after the first one has read the worst one.
      /// </summary>
      public static IReadOnlyList<SpamPipelineNote> Notes(SpamPipelineConfig config)
      {
         var notes = new List<SpamPipelineNote>();
         if (config == null)
            return notes;

         bool marks = config.MarkThreshold > 0;
         bool refuses = config.DeleteThreshold > 0;

         // ---- cannot work -----------------------------------------------------

         if (!marks && !refuses)
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Critical,
               L("Both thresholds are 0, so no score can mark or refuse anything - the server tests them with \"greater than 0\" before using them. It also stops testing as soon as the running total reaches the higher threshold, which is 0, so exactly one check runs in each phase and its result is discarded.")));
         }
         else if (!marks)
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Warning,
               F("Nothing is ever marked as spam: marking needs a mark threshold above 0, and a 0 there means \"never\" rather than \"always\". Messages reaching {0} are still refused.", config.DeleteThreshold)));
         }

         if (marks && refuses && config.DeleteThreshold <= config.MarkThreshold)
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Critical,
               F("The delete threshold ({0}) is at or below the mark threshold ({1}) and is tested first, so no message is ever marked and delivered - every message that would be marked is refused instead.", config.DeleteThreshold, config.MarkThreshold)));
         }

         int? ceiling = HighestReachableScore(config);
         if (marks && ceiling != null && ceiling.Value < config.MarkThreshold)
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Critical,
               F("The enabled checks can add at most {0} between them, which is below the mark threshold of {1}: no message can reach either threshold, however bad it is.", ceiling.Value, config.MarkThreshold)));
         }

         // ---- runs and cannot change the outcome ------------------------------

         // SpamAssassin is excluded here because it gets a note of its own below:
         // the reason its score is zero is different (merging is off and the fixed
         // score was never set), and so is the fix.
         foreach (SpamCheck check in Checks(config)
            .Where(c => c.Enabled && c.Score == 0 && c.Key != "spamassassin"))
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Warning,
               F("{0} is on but scores 0, so it costs a lookup on every message and can never change the outcome. Give it a score, or turn it off.", check.Name)));
         }

         if (config.SpamAssassinEnabled && !config.SpamAssassinMergesScore && config.SpamAssassinScore <= 0)
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Warning,
               F("SpamAssassin's verdict is thrown away: with merging off, a message it tags as spam is given the fixed score, which is {0}.", config.SpamAssassinScore)));
         }

         if (config.SpamAssassinEnabled && string.IsNullOrWhiteSpace(config.SpamAssassinHost))
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Warning,
               L("SpamAssassin is on with no host set, so every message is accepted without a verdict and the server reports HM5508 each time.")));
         }

         if (marks && !config.AddSpamHeader && !config.AddReasonHeader && !config.PrependSubject)
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Warning,
               L("A marked message is changed in no visible way: no X-hMailServer-Spam header, no reason header and no subject prefix, so a mail client has nothing to file on. The internal spam flag is still set, which suppresses auto-replies and forwarding for accounts configured that way.")));
         }

         // ---- true, invisible, and worth knowing ------------------------------

         int stop = StopScore(config);
         if (stop > 0)
         {
            foreach (SpamCheckPhase phase in new[] { SpamCheckPhase.BeforeTheBody, SpamCheckPhase.AfterTheBody })
            {
               List<SpamCheck> enabled = Checks(config).Where(c => c.Enabled && c.Phase == phase).ToList();

               // Only interesting when something comes after the check that can
               // reach the stop score on its own - otherwise nothing is skipped.
               for (int i = 0; i < enabled.Count - 1; i++)
               {
                  if (enabled[i].Score == null || enabled[i].Score.Value < stop)
                     continue;

                  notes.Add(new SpamPipelineNote(StatusLevel.Information,
                     F("{0} scores {1} on its own, which reaches the {2} at which the server stops testing - so {3} will not run for a message it fails, and will not appear in the log.", enabled[i].Name, enabled[i].Score.Value, stop, Join(enabled.Skip(i + 1).Select(c => c.Name).ToList()))));
                  break;
               }
            }
         }

         if (config.MaxScanKilobytes > 0)
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Information,
               F("Messages larger than {0} KB skip every check that needs the body (SURBL, DKIM, DMARC and SpamAssassin). The connection checks still run, and the message is delivered.", config.MaxScanKilobytes)));
         }
         else
         {
            notes.Add(new SpamPipelineNote(StatusLevel.Information,
               F("There is no scan size limit, but messages over {0} MB still skip the body checks: the MIME parser cannot load them, and scanning without loading them would write an empty body over the message.", (ParserSizeLimitKilobytes / 1024))));
         }

         notes.Add(new SpamPipelineNote(StatusLevel.Information,
            F("None of this applies to an authenticated session - spam protection is skipped entirely for a client that logged on, for connections from an IP range with spam protection turned off, and for the {0} entries on the anti-spam white list.", config.WhiteListEntries)));

         return notes;
      }

      /// <summary>The single worst note, for a one-line summary. Normal when there is nothing to say.</summary>
      public static StatusLevel WorstLevel(SpamPipelineConfig config)
      {
         return Notes(config).Aggregate(StatusLevel.Normal, (worst, note) => note.Level > worst ? note.Level : worst);
      }

      private static string Score(int score) => score.ToString();

      private static string DescribeList(string what, int active, int total)
      {
         if (total == 0)
            return F("{0}. No entries are configured, so the check does not run.", what);

         if (active == 0)
            return F("{0}. {1} configured, none active, so the check does not run.", what, total);

         return F("{0}. {1} of {2} active.", what, active, total);
      }

      /// <summary>"a, b and c" - the notes are sentences, so they need the "and".</summary>
      private static string Join(IReadOnlyList<string> parts)
      {
         if (parts == null || parts.Count == 0)
            return "";
         if (parts.Count == 1)
            return parts[0];

         return F("{0} and {1}", string.Join(", ", parts.Take(parts.Count - 1)), parts[parts.Count - 1]);
      }
   }
}
