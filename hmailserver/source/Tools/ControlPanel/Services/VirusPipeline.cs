using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// One scanner, as the server will actually run it.
   ///
   /// "Configured" and "will run" are not the same thing here, and the gap between
   /// them is the reason this page exists: a scanner that is switched on with a
   /// piece of its configuration missing does not fail the message, it reports an
   /// error to the log and the message is delivered as though it were clean.
   /// </summary>
   public sealed class VirusScannerEntry
   {
      internal VirusScannerEntry(string key, int order, string name, bool enabled, bool usable,
         string detail, string problem, string page)
      {
         Key = key;
         Order = order;
         Name = name;
         Enabled = enabled;
         Usable = usable;
         Detail = detail;
         Problem = problem;
         Page = page;
      }

      /// <summary>Stable identifier, so a test can name a scanner without matching its wording.</summary>
      public string Key { get; }

      /// <summary>
      /// Position in the order VirusScanner::ScanFile_ tries them, 1-based. The
      /// order is fixed in the source - ClamWin, then the custom scanner, then
      /// ClamAV - and it matters because the first scanner to find something ends
      /// the scan, so a slow scanner in front of a fast one costs every message.
      /// </summary>
      public int Order { get; }

      public string Name { get; }

      /// <summary>Whether the administrator has switched it on.</summary>
      public bool Enabled { get; }

      /// <summary>
      /// Whether it can actually run: switched on AND with the settings it needs.
      /// An enabled scanner that is not usable is the dangerous state, because
      /// nothing about the configuration looks wrong from the settings page.
      /// </summary>
      public bool Usable { get; }

      /// <summary>Where it scans, in one line - the host and port, or the executable.</summary>
      public string Detail { get; }

      /// <summary>What is missing, when <see cref="Usable"/> is false and <see cref="Enabled"/> is true. Otherwise null.</summary>
      public string Problem { get; }

      /// <summary>Navigation key of the page that owns these settings.</summary>
      public string Page { get; }

      /// <summary>"On", "Off" or "On, but cannot run" - the word a reader gets when the colour is not available.</summary>
      public string StateText =>
         !Enabled ? "Off" : Usable ? "On" : "On, but cannot run";
   }

   /// <summary>
   /// Something worth saying about the virus configuration as a whole, carrying a
   /// <see cref="StatusLevel"/> so the page presents it with the shared colour,
   /// shape and severity word rather than inventing a third vocabulary.
   /// </summary>
   public sealed class VirusPipelineNote
   {
      internal VirusPipelineNote(StatusLevel level, string text)
      {
         Level = level;
         Text = text;
      }

      public StatusLevel Level { get; }

      /// <summary>The whole note, in a sentence or two. Never empty.</summary>
      public string Text { get; }
   }

   /// <summary>What the server does with a message a scanner has condemned.</summary>
   public enum VirusAction
   {
      /// <summary>eAntivirusAction.hDeleteEmail - the message is not delivered.</summary>
      DeleteMessage = 0,

      /// <summary>eAntivirusAction.hDeleteAttachments - the attachments are stripped and the message IS delivered.</summary>
      StripAttachments = 1
   }

   /// <summary>
   /// A snapshot of the anti-virus configuration, read from the COM settings tree.
   ///
   /// Plain properties with no behaviour, so <see cref="VirusPipeline"/> can be
   /// tested without a server: every interesting case - a scanner enabled with no
   /// host, a size limit that silently skips large messages, notifications sent to
   /// a forged sender - is a state a live server can be in and a tedious one to
   /// produce against a real one.
   /// </summary>
   public sealed class VirusPipelineConfig
   {
      public bool ClamAvEnabled { get; set; }
      public string ClamAvHost { get; set; } = "";
      public int ClamAvPort { get; set; }

      public bool ClamWinEnabled { get; set; }
      public string ClamWinExecutable { get; set; } = "";
      public string ClamWinDatabaseFolder { get; set; } = "";

      public bool CustomScannerEnabled { get; set; }
      public string CustomScannerExecutable { get; set; } = "";
      public int CustomScannerVirusReturnValue { get; set; }

      /// <summary>
      /// AntiVirus.MaximumMessageSize, in KB. **0 means no limit** - the comparison
      /// in VirusScanner::Scan is `limit > 0 && messageKB > limit`, so zero is the
      /// safe value here and any positive number is a hole. That is the opposite
      /// way round from most size limits, which is exactly why it is worth a page.
      /// </summary>
      public int MaxScanKilobytes { get; set; }

      public VirusAction Action { get; set; }
      public bool NotifySender { get; set; }
      public bool NotifyRecipient { get; set; }

      public bool AttachmentBlockingEnabled { get; set; }
      public int BlockedAttachmentPatterns { get; set; }

      /// <summary>External POP3 fetch accounts that have their own anti-virus switch turned off.</summary>
      public int FetchAccountsWithScanningOff { get; set; }

      /// <summary>How many external POP3 fetch accounts exist at all.</summary>
      public int FetchAccountsTotal { get; set; }

      /// <summary>
      /// True when the fetch-account walk gave up before it had seen every account.
      ///
      /// There is no server-wide collection of fetch accounts - they hang off each
      /// account - so counting them means walking every account on the server, and a
      /// page that does that unbounded stops responding on a large installation. The
      /// walk is capped, and when it is capped the numbers above are a floor rather
      /// than a total, which the wording has to reflect: a page that says "2 accounts"
      /// when it means "at least 2 of the first thousand" is worse than one that
      /// admits it stopped looking.
      /// </summary>
      public bool FetchAccountScanIncomplete { get; set; }
   }

   /// <summary>
   /// What the anti-virus configuration will actually do to a message, derived from
   /// the settings rather than from what the settings are called.
   ///
   /// No WPF here on purpose: every judgement this page makes is a pure function of
   /// a <see cref="VirusPipelineConfig"/> and is tested directly. The view reads the
   /// COM settings into the snapshot and draws what it is told.
   ///
   /// The behaviour being described lives in `VirusScanner::ScanFile_`,
   /// `VirusScanner::Scan` and `SMTPDeliverer::RunVirusProtection_`, and the one
   /// thing worth carrying away from all three is this: **a scanner that errors is
   /// not a message that fails**. ScanFile_ reports the error through ErrorManager
   /// at Medium and carries on to the next scanner; if every scanner errors, the
   /// result is `NoVirusFound` and the message is delivered exactly as if it had
   /// been examined and found clean. So "enabled" is never the question. "Can it
   /// run" is.
   /// </summary>
   public static class VirusPipeline
   {
      /// <summary>
      /// The concurrency cap in VirusScanner::MaxRunningScanners. Named here so the
      /// page can state it rather than describing "a limit".
      /// </summary>
      public const int MaxConcurrentScans = 10;

      /// <summary>
      /// Every scanner, in the order VirusScanner::ScanFile_ tries them.
      /// </summary>
      public static IReadOnlyList<VirusScannerEntry> Scanners(VirusPipelineConfig config)
      {
         var scanners = new List<VirusScannerEntry>();

         if (config == null)
            return scanners;

         // 1. ClamWin. Two paths, and both are needed: ClamWinVirusScanner passes
         //    the database folder to the executable on the command line.
         bool clamWinUsable = !IsBlank(config.ClamWinExecutable) && !IsBlank(config.ClamWinDatabaseFolder);
         scanners.Add(new VirusScannerEntry(
            "clamwin", 1, "ClamWin (local executable)",
            config.ClamWinEnabled,
            config.ClamWinEnabled && clamWinUsable,
            IsBlank(config.ClamWinExecutable) ? "No executable set" : config.ClamWinExecutable,
            !config.ClamWinEnabled ? null
               : IsBlank(config.ClamWinExecutable) && IsBlank(config.ClamWinDatabaseFolder)
                  ? "Neither the executable nor the signature database folder is set."
                  : IsBlank(config.ClamWinExecutable) ? "No executable path is set."
                  : IsBlank(config.ClamWinDatabaseFolder) ? "No signature database folder is set."
                  : null,
            "antivirus"));

         // 2. The custom scanner. Only the executable is required; the return value
         //    that means "infected" is a number and 0 is a legitimate choice, so it
         //    is never treated as missing.
         scanners.Add(new VirusScannerEntry(
            "custom", 2, "Custom scanner (external program)",
            config.CustomScannerEnabled,
            config.CustomScannerEnabled && !IsBlank(config.CustomScannerExecutable),
            IsBlank(config.CustomScannerExecutable)
               ? "No executable set"
               : config.CustomScannerExecutable + " — exit code " + config.CustomScannerVirusReturnValue + " means infected",
            config.CustomScannerEnabled && IsBlank(config.CustomScannerExecutable)
               ? "No executable path is set."
               : null,
            "antivirus"));

         // 3. ClamAV over the network (clamd INSTREAM). A port of 0 is not a
         //    default, it is an address nothing listens on.
         bool clamAvUsable = !IsBlank(config.ClamAvHost) && config.ClamAvPort > 0 && config.ClamAvPort <= 65535;
         scanners.Add(new VirusScannerEntry(
            "clamav", 3, "ClamAV (clamd, over TCP)",
            config.ClamAvEnabled,
            config.ClamAvEnabled && clamAvUsable,
            IsBlank(config.ClamAvHost)
               ? "No host set"
               : config.ClamAvHost + ":" + config.ClamAvPort,
            !config.ClamAvEnabled ? null
               : IsBlank(config.ClamAvHost) ? "No host name or address is set."
               : config.ClamAvPort <= 0 ? "The port is 0, which nothing can listen on."
               : config.ClamAvPort > 65535 ? "The port is outside the range 1-65535."
               : null,
            "antivirus"));

         return scanners;
      }

      /// <summary>
      /// One sentence answering "what will happen to an infected message", written
      /// for somebody who has just arrived at the page.
      /// </summary>
      public static string Verdict(VirusPipelineConfig config)
      {
         if (config == null)
            return "The anti-virus configuration could not be read.";

         int usable = CountUsable(config);

         if (usable == 0)
         {
            return config.AttachmentBlockingEnabled && config.BlockedAttachmentPatterns > 0
               ? "No message is scanned for viruses. Attachment blocking is still stripping "
                 + Count(config.BlockedAttachmentPatterns, "file-name pattern", "file-name patterns")
                 + ", but nothing examines the contents of a message."
               : "No message is scanned for viruses, and no attachment is blocked. Everything is delivered as it arrives.";
         }

         string found = config.Action == VirusAction.DeleteMessage
            ? "the whole message is deleted and never delivered"
            : "the attachments are stripped and the message is still delivered";

         string scanned = config.MaxScanKilobytes > 0
            ? "Messages up to " + Kilobytes(config.MaxScanKilobytes) + " are scanned by "
              + Count(usable, "scanner", "scanners") + "; anything larger is delivered without being scanned at all"
            : "Every message is scanned by " + Count(usable, "scanner", "scanners");

         return scanned + ". If a virus is found, " + found + ".";
      }

      /// <summary>
      /// What the server does about an infected message, in the words of the two
      /// settings that decide it.
      /// </summary>
      public static string ActionSummary(VirusPipelineConfig config)
      {
         if (config == null)
            return "";

         if (config.Action == VirusAction.StripAttachments)
         {
            // Deliberately says what does NOT happen: this action is the one people
            // choose expecting the notifications below it to apply, and they do not.
            return "The attachments are replaced and the message is delivered. Neither notification setting applies to this "
                   + "action — the server only sends the \"message deleted\" notice when the action is to delete the message.";
         }

         if (!config.NotifySender && !config.NotifyRecipient)
            return "The message is deleted and nobody is told. The deletion is recorded in the application log.";

         var told = new List<string>();
         if (config.NotifyRecipient)
            told.Add("every recipient");
         if (config.NotifySender)
            told.Add("the envelope sender");

         return "The message is deleted and a notice is sent to " + string.Join(" and ", told) + ".";
      }

      /// <summary>
      /// Everything about this configuration that an administrator would want told
      /// to them rather than left to be discovered from a log.
      /// </summary>
      public static IReadOnlyList<VirusPipelineNote> Notes(VirusPipelineConfig config)
      {
         var notes = new List<VirusPipelineNote>();

         if (config == null)
            return notes;

         IReadOnlyList<VirusScannerEntry> scanners = Scanners(config);
         int usable = CountUsable(config);

         // ---- cannot work ------------------------------------------------------

         foreach (VirusScannerEntry scanner in scanners.Where(s => s.Enabled && !s.Usable))
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Critical,
               scanner.Name + " is switched on but cannot run. " + scanner.Problem
               + " Every scan it is asked for fails, and a failed scan is not a failed message: the error is "
               + "logged and the message is delivered as though it had been examined and found clean."));
         }

         if (usable == 0 && !config.AttachmentBlockingEnabled)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Critical,
               "Nothing examines incoming mail. No scanner can run and attachment blocking is off, so an infected "
               + "message is delivered to the mailbox exactly as it arrived."));
         }
         else if (usable == 0)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Critical,
               "No virus scanner can run. Attachment blocking still strips the file-name patterns on its own page, "
               + "but nothing looks inside a message or inside an archive."));
         }

         // ---- works, but leaves a gap -----------------------------------------

         if (usable > 0 && config.MaxScanKilobytes > 0)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Warning,
               "Messages larger than " + Kilobytes(config.MaxScanKilobytes) + " are not scanned. They are delivered, "
               + "not refused, and nothing is written to the log when it happens — so this limit is invisible in "
               + "normal operation. 0 means \"no limit\" and is the setting that closes the gap."));
         }

         if (usable > 0 && config.Action == VirusAction.StripAttachments)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Warning,
               "Infected messages are delivered with their attachments stripped rather than deleted. The recipient "
               + "still receives the message body, which for most malicious mail is the part carrying the "
               + "instructions telling them what to do next."));
         }

         if (usable > 0 && config.Action == VirusAction.DeleteMessage && config.NotifySender)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Warning,
               "A notice is sent to the envelope sender of every infected message. Virus senders forge that address "
               + "almost without exception, so this mostly delivers mail to people who did not send anything — which "
               + "is backscatter, and it is what gets a server onto a block list."));
         }

         if (config.AttachmentBlockingEnabled && config.BlockedAttachmentPatterns == 0)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Warning,
               "Attachment blocking is switched on with no patterns to block, so it does nothing at all."));
         }

         if (config.FetchAccountsWithScanningOff > 0)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Warning,
               (config.FetchAccountScanIncomplete ? "At least " : "")
               + Count(config.FetchAccountsWithScanningOff, "external POP3 fetch account has", "external POP3 fetch accounts have")
               + " anti-virus turned off, so mail collected by "
               + (config.FetchAccountsWithScanningOff == 1 ? "it is" : "them is")
               + " delivered unscanned no matter what is set here. The switch is on the fetch account, not on this page."
               + (config.FetchAccountScanIncomplete
                  ? " There are more accounts on this server than this page walks, so there may be others."
                  : "")));
         }

         // ---- worth knowing ----------------------------------------------------

         if (!config.AttachmentBlockingEnabled && config.BlockedAttachmentPatterns > 0)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Information,
               Count(config.BlockedAttachmentPatterns, "blocked-attachment pattern is", "blocked-attachment patterns are")
               + " configured but attachment blocking is switched off, so none of them is applied."));
         }

         if (usable > 1)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Information,
               "Every message goes through " + Count(usable, "scanner", "scanners") + " in turn, and each is asked "
               + "about the whole message and then about each attachment separately. That is thorough and it is not "
               + "free: the scan happens while the sender is waiting for its \"250 OK\"."));
         }

         if (config.ClamWinEnabled && config.ClamAvEnabled)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Information,
               "ClamWin and ClamAV both use the same signature database. Running both scans every message twice "
               + "against the same definitions."));
         }

         if (usable > 0)
         {
            notes.Add(new VirusPipelineNote(StatusLevel.Information,
               "At most " + MaxConcurrentScans + " scans run at once. Beyond that, delivery threads wait — and after "
               + "60 seconds of waiting the server gives up waiting and scans anyway, so a slow scanner shows up as "
               + "slow mail rather than as unscanned mail."));
         }

         return notes;
      }

      /// <summary>The single worst note, for a one-line summary. Normal when there is nothing to say.</summary>
      public static StatusLevel WorstLevel(VirusPipelineConfig config)
      {
         StatusLevel worst = StatusLevel.Normal;

         foreach (VirusPipelineNote note in Notes(config))
         {
            if (note.Level > worst)
               worst = note.Level;
         }

         return worst;
      }

      /// <summary>How many scanners are both switched on and able to run.</summary>
      public static int CountUsable(VirusPipelineConfig config)
         => Scanners(config).Count(scanner => scanner.Usable);

      private static bool IsBlank(string value) => string.IsNullOrWhiteSpace(value);

      private static string Count(int n, string singular, string plural) =>
         n + " " + (n == 1 ? singular : plural);

      /// <summary>
      /// The size limit in the units the setting is stored in, with a megabyte
      /// figure beside it once it is large enough for KB to stop being readable.
      /// </summary>
      public static string Kilobytes(int kilobytes)
      {
         if (kilobytes >= 1024)
            return kilobytes.ToString("N0") + " KB (" + Math.Round(kilobytes / 1024.0, 1) + " MB)";

         return kilobytes.ToString("N0") + " KB";
      }
   }
}
