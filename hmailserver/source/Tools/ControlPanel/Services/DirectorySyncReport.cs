using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// What one line of a synchronisation report says about one mailbox.
   ///
   /// The names are the server's, not this page's. DirectorySync::DescribeAction
   /// writes them, and matching on them here rather than re-deriving a category from
   /// the wording is what keeps the two ends from drifting: if the server ever adds
   /// an action, this lands in <see cref="Other"/> and shows the server's own text,
   /// rather than being silently sorted into the wrong bucket.
   /// </summary>
   public enum DirectorySyncRowKind
   {
      Create,
      Update,
      Unchanged,
      Skip,
      Disable,
      ReportMissing,
      Other
   }

   /// <summary>
   /// What became of a row: what the server's fourth column says.
   ///
   /// The distinction that matters is Done versus Failed. Before this column existed
   /// the action word was all a caller had, and after an apply "create" meant both
   /// "created" and "was going to be created and the database refused" - so a run in
   /// which nothing was created read exactly like a run in which everything was.
   /// </summary>
   public enum DirectorySyncOutcome
   {
      /// <summary>A preview: nothing has happened yet.</summary>
      Planned,

      /// <summary>An apply carried this row out.</summary>
      Done,

      /// <summary>An apply tried and could not. The detail carries the server's reason.</summary>
      Failed,

      /// <summary>
      /// An apply that had nothing to do for this row - it was unchanged, skipped, or
      /// missing-and-left-alone. Not a failure; the plan never asked for an action.
      /// </summary>
      NoAction,

      /// <summary>An older server that does not write the fourth column.</summary>
      Unknown
   }

   /// <summary>One mailbox, and what the run did or would do to it.</summary>
   public sealed class DirectorySyncRow
   {
      internal DirectorySyncRow(string address, string action, string detail, DirectorySyncRowKind kind,
         DirectorySyncOutcome outcome)
      {
         Address = address;
         Action = action;
         Detail = detail;
         Kind = kind;
         Outcome = outcome;
      }

      public DirectorySyncOutcome Outcome { get; }

      /// <summary>The mailbox address, or "(no address)" for an entry that had none.</summary>
      public string Address { get; }

      /// <summary>The server's own word for what happens, shown as it was received.</summary>
      public string Action { get; }

      /// <summary>The server's sentence explaining it. This is the useful column.</summary>
      public string Detail { get; }

      public DirectorySyncRowKind Kind { get; }

      /// <summary>
      /// Whether this row is one an administrator has to read.
      ///
      /// Before an apply: a skip is a mailbox somebody expects and will not get, and a
      /// disable takes a working one away. Creates and updates are what was asked for.
      ///
      /// After an apply: a row that FAILED is added, whatever its action was. A failed
      /// create is the single most important line on the page - it is a mailbox the
      /// preview promised and the run did not deliver - and leaving it out was how the
      /// page could print "nothing here needs a decision" over a run that created
      /// nothing.
      /// </summary>
      public bool NeedsAttention =>
         Outcome == DirectorySyncOutcome.Failed ||
         Kind == DirectorySyncRowKind.Skip ||
         Kind == DirectorySyncRowKind.Disable;
   }

   /// <summary>
   /// A parsed synchronisation report.
   ///
   /// The server hands back one string: a summary sentence, a blank line, then one
   /// tab-separated row per mailbox. That is exactly the right shape for a log and
   /// the wrong one for a screen - four hundred rows of equal weight, with the six
   /// that matter somewhere in the middle. This turns it into something a page can
   /// sort and count without inventing any facts of its own.
   ///
   /// Deliberately free of COM and of WPF, so the parsing is testable on its own. The
   /// input is a string the server produced; every test in ControlPanel.Tests feeds it
   /// one and asserts on what comes out, which is the whole of the logic worth
   /// covering here.
   /// </summary>
   public sealed class DirectorySyncReport
   {
      private DirectorySyncReport(bool succeeded, string summary, IList<DirectorySyncRow> rows,
         IList<string> skipBreakdown)
      {
         Succeeded = succeeded;
         Summary = summary;
         Rows = rows;
         SkipBreakdown = skipBreakdown;
      }

      /// <summary>
      /// The server's aggregate of why entries were skipped, one line per reason, kept
      /// apart from <see cref="Rows"/> so it can be shown as what it is. Empty when
      /// nothing was skipped.
      /// </summary>
      public IList<string> SkipBreakdown { get; }

      /// <summary>
      /// What the COM call returned, carried through unchanged. False means the run
      /// did not happen - not that individual mailboxes failed - and in that case
      /// <see cref="Summary"/> is the reason and <see cref="Rows"/> is empty.
      /// </summary>
      public bool Succeeded { get; }

      /// <summary>The server's first line: the sentence to put at the top of the page.</summary>
      public string Summary { get; }

      public IList<DirectorySyncRow> Rows { get; }

      public int Count(DirectorySyncRowKind kind) => Rows.Count(row => row.Kind == kind);

      /// <summary>
      /// How many rows an administrator ought to read before applying. Zero is the
      /// answer that means "this is safe to press", and it is worth being able to say
      /// that in one number.
      /// </summary>
      public int AttentionCount => Rows.Count(row => row.NeedsAttention);

      /// <summary>
      /// Parses what Settings.PreviewDirectorySync or ApplyDirectorySync returned.
      ///
      /// Tolerant by design. A report that this cannot parse is still a report the
      /// administrator needs to see, so anything unrecognised becomes a row of kind
      /// Other carrying the original text rather than being dropped. Losing a line
      /// because its shape was unexpected is the one failure mode that would make
      /// this worse than showing the raw string.
      /// </summary>
      public static DirectorySyncReport Parse(bool succeeded, string reportText)
      {
         var rows = new List<DirectorySyncRow>();

         if (string.IsNullOrEmpty(reportText))
            return new DirectorySyncReport(succeeded, string.Empty, rows, new List<string>());

         string[] lines = reportText.Replace("\r\n", "\n").Split('\n');

         string summary = string.Empty;
         bool haveSummary = false;
         bool inSkipBreakdown = false;
         var breakdown = new List<string>();

         foreach (string line in lines.Where(line => line.Length != 0))
         {
            if (!haveSummary)
            {
               summary = line.Trim();
               haveSummary = true;
               continue;
            }

            // The server appends an aggregate at the end - "Skipped, by reason:" and
            // then "  22<tab>there is no domain of that name on this server" per
            // reason - to the SAME string as the per-mailbox rows.
            //
            // Everything from here on is that aggregate, and it must not be parsed as
            // mailboxes. It used to be: the header line has no tabs and each count line
            // has one, so both fell into the "fewer than three fields" branch below and
            // were rendered as rows headed "(no address)" - which is not merely untidy,
            // because "(no address)" is the exact literal the server writes for a
            // genuine directory entry that carries no mail attribute. The aggregate was
            // indistinguishable from real address-less entries, on the one page whose
            // job is making a bulk change legible before it is applied.
            if (string.Equals(line.Trim(), SkipBreakdownHeader, StringComparison.Ordinal))
            {
               inSkipBreakdown = true;
               continue;
            }

            if (inSkipBreakdown)
            {
               breakdown.Add(line.Trim());
               continue;
            }

            string[] parts = line.Split('\t');

            if (parts.Length < 3)
            {
               rows.Add(new DirectorySyncRow(string.Empty, string.Empty, line.Trim(),
                  DirectorySyncRowKind.Other, DirectorySyncOutcome.Unknown));
               continue;
            }

            // The fourth field is the outcome and is optional: a server older than
            // 15 August 2026 writes three. When it is absent the row is Unknown rather
            // than assumed successful, because assuming success is precisely the bug
            // this column was added to fix.
            DirectorySyncOutcome outcome = parts.Length >= 4
               ? OutcomeOf(parts[parts.Length - 1].Trim())
               : DirectorySyncOutcome.Unknown;

            // Everything between the action and the outcome is the detail. Joined back
            // rather than taken as parts[2] alone, because a directory attribute is free
            // text and nothing stops one containing a tab - and a sentence truncated at
            // that tab would be a sentence that stopped mid-explanation.
            int detailFields = (parts.Length >= 4 ? parts.Length - 1 : parts.Length) - 2;
            string detail = string.Join("\t", parts, 2, detailFields).Trim();

            rows.Add(new DirectorySyncRow(parts[0].Trim(), parts[1].Trim(), detail,
               KindOf(parts[1].Trim()), outcome));
         }

         return new DirectorySyncReport(succeeded, summary, rows, breakdown);
      }

      /// <summary>The literal the server writes above the aggregate.</summary>
      private const string SkipBreakdownHeader = "Skipped, by reason:";

      private static DirectorySyncOutcome OutcomeOf(string state)
      {
         if (string.Equals(state, "planned", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncOutcome.Planned;

         if (string.Equals(state, "done", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncOutcome.Done;

         if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncOutcome.Failed;

         if (string.Equals(state, "noop", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncOutcome.NoAction;

         return DirectorySyncOutcome.Unknown;
      }

      /// <summary>
      /// Maps the server's action word onto a kind. The strings are the ones
      /// DirectorySync::DescribeAction writes; an unknown one is Other rather than a
      /// guess.
      /// </summary>
      private static DirectorySyncRowKind KindOf(string action)
      {
         if (string.Equals(action, "create", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncRowKind.Create;

         if (string.Equals(action, "update", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncRowKind.Update;

         if (string.Equals(action, "unchanged", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncRowKind.Unchanged;

         if (string.Equals(action, "skip", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncRowKind.Skip;

         if (string.Equals(action, "disable", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncRowKind.Disable;

         if (string.Equals(action, "no longer in the directory", StringComparison.OrdinalIgnoreCase))
            return DirectorySyncRowKind.ReportMissing;

         return DirectorySyncRowKind.Other;
      }
   }
}
