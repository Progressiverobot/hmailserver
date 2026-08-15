using System.Collections.Generic;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// Covers the parsing of a synchronisation report.
   ///
   /// The report is the only thing an administrator sees before pressing Apply, so the
   /// failure that matters here is not a crash - it is a row that is dropped, put in the
   /// wrong column, or invented. A skip shown as an unchanged mailbox, a disable that
   /// never reaches the screen, or a summary line rendered as a mailbox, all turn the
   /// preview into something worse than no preview: one that was read and was wrong.
   ///
   /// Every action and outcome string used below is the literal one the server writes -
   /// DirectorySync::DescribeAction for the action, InterfaceSettings::RunDirectorySync_
   /// for the outcome column. They are duplicated here rather than shared, deliberately:
   /// if somebody changes the server's wording, these tests should fail, because the page
   /// would otherwise start sorting real rows into "Other" without saying so.
   /// </summary>
   public class DirectorySyncReportTests
   {
      private const string Summary =
         "Directory synchronisation (preview): 5 directory entries read, 2 to create, 1 to update, "
         + "1 unchanged, 1 skipped.";

      private static string Report(params string[] rows)
      {
         var text = new List<string> { Summary, string.Empty };
         text.AddRange(rows);
         return string.Join("\r\n", text) + "\r\n";
      }

      [Fact]
      public void TheSummarySentenceIsTakenFromTheFirstLine()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true, Report("a@b.test\tcreate\tWill be created."));

         Assert.Equal(Summary, report.Summary);
         Assert.True(report.Succeeded);
      }

      [Fact]
      public void EveryActionTheServerCanWriteIsRecognised()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true, Report(
            "one@b.test\tcreate\tWill be created.",
            "two@b.test\tupdate\tThe display name will be set.",
            "three@b.test\tunchanged\tAlready matches the directory.",
            "four@b.test\tskip\tThe domain is not linked to a directory.",
            "five@b.test\tdisable\tNo longer in the directory.",
            "six@b.test\tno longer in the directory\tLeft alone because disabling was not requested."));

         Assert.Equal(1, report.Count(DirectorySyncRowKind.Create));
         Assert.Equal(1, report.Count(DirectorySyncRowKind.Update));
         Assert.Equal(1, report.Count(DirectorySyncRowKind.Unchanged));
         Assert.Equal(1, report.Count(DirectorySyncRowKind.Skip));
         Assert.Equal(1, report.Count(DirectorySyncRowKind.Disable));
         Assert.Equal(1, report.Count(DirectorySyncRowKind.ReportMissing));

         // An action the server writes that is not recognised would appear here, and
         // would mean a real row was being shown to the administrator as unclassified.
         Assert.Equal(0, report.Count(DirectorySyncRowKind.Other));
      }

      /// <summary>
      /// The number the Apply button's confirmation is built on. Before an apply it
      /// counts the two kinds that cost something - a mailbox that will not appear, and
      /// one that will be taken away - and deliberately not the creates, which are what
      /// the administrator asked for.
      /// </summary>
      [Fact]
      public void AttentionCountsSkipsAndDisablesAndNothingElse()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true, Report(
            "one@b.test\tcreate\tWill be created.\tplanned",
            "two@b.test\tcreate\tWill be created.\tplanned",
            "three@b.test\tunchanged\tAlready matches.\tplanned",
            "four@b.test\tskip\tThe domain does not exist here.\tplanned",
            "five@b.test\tdisable\tNo longer in the directory.\tplanned"));

         Assert.Equal(2, report.AttentionCount);
      }

      /// <summary>
      /// The aggregate the server appends after the mailbox rows must never become
      /// mailbox rows.
      ///
      /// This is the shape the page got wrong. "Skipped, by reason:" has no tab and each
      /// count line has one, so both fell through the row parser into rows with an empty
      /// address - which the view then headed "(no address)", the exact literal the
      /// server writes for a genuine directory entry carrying no mail attribute. Two
      /// summary lines became two indistinguishable fake mailboxes, on the one page whose
      /// job is making a bulk change legible before it is applied.
      /// </summary>
      [Fact]
      public void TheSkippedByReasonAggregateIsNotParsedAsMailboxes()
      {
         string text = Summary + "\r\n\r\n"
            + "one@b.test\tcreate\tWill be created.\tplanned\r\n"
            + "\r\nSkipped, by reason:\r\n"
            + "  22\tthere is no domain of that name on this server\r\n"
            + "  3\tthe directory entry carries no mail address\r\n";

         DirectorySyncReport report = DirectorySyncReport.Parse(true, text);

         Assert.Single(report.Rows);
         Assert.Equal("one@b.test", report.Rows[0].Address);

         Assert.Equal(2, report.SkipBreakdown.Count);
         Assert.Equal("22\tthere is no domain of that name on this server", report.SkipBreakdown[0]);
      }

      /// <summary>
      /// After an apply, a row that FAILED must be one the page insists on showing. The
      /// action word alone cannot say so - a failed create still says "create" - which is
      /// how a run that created nothing could be summarised as needing no attention.
      /// </summary>
      [Fact]
      public void AFailedRowNeedsAttentionEvenThoughItsActionIsACreate()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true, Report(
            "one@b.test\tcreate\tCreated.\tdone",
            "two@b.test\tcreate\tThe database refused to create the account.\tfailed",
            "three@b.test\tunchanged\tAlready matches.\tnoop"));

         Assert.Equal(DirectorySyncOutcome.Done, report.Rows[0].Outcome);
         Assert.Equal(DirectorySyncOutcome.Failed, report.Rows[1].Outcome);
         Assert.Equal(DirectorySyncOutcome.NoAction, report.Rows[2].Outcome);

         Assert.False(report.Rows[0].NeedsAttention);
         Assert.True(report.Rows[1].NeedsAttention);
         Assert.False(report.Rows[2].NeedsAttention);

         Assert.Equal(1, report.AttentionCount);
      }

      /// <summary>
      /// An unchanged or skipped row after an apply is "noop", not a failure. Apply_ has
      /// no case for those actions, so their applied flag stays false; reading that as
      /// failure would report every correctly-skipped entry as an error, which on a
      /// directory where most accounts already match is almost every row on the page.
      /// </summary>
      [Fact]
      public void RowsAnApplyNeverActsOnAreNotReportedAsFailures()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true, Report(
            "one@b.test\tunchanged\tAlready matches.\tnoop",
            "two@b.test\tno longer in the directory\tLeft alone.\tnoop"));

         foreach (DirectorySyncRow row in report.Rows)
            Assert.NotEqual(DirectorySyncOutcome.Failed, row.Outcome);

         Assert.Equal(0, report.AttentionCount);
      }

      /// <summary>
      /// A three-field report comes from a server older than the outcome column. It must
      /// parse, and the outcome must be Unknown rather than assumed successful - assuming
      /// success is the defect the column exists to fix.
      /// </summary>
      [Fact]
      public void AThreeFieldRowFromAnOlderServerStillParses()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true,
            Report("one@b.test\tcreate\tWill be created."));

         Assert.Single(report.Rows);
         Assert.Equal("Will be created.", report.Rows[0].Detail);
         Assert.Equal(DirectorySyncOutcome.Unknown, report.Rows[0].Outcome);
      }

      /// <summary>
      /// A directory attribute is free text and nothing stops one holding a tab. The
      /// detail is everything between the action and the outcome, so an embedded tab must
      /// neither truncate the sentence nor swallow the outcome column.
      /// </summary>
      [Fact]
      public void ADetailContainingATabIsKeptWholeAndDoesNotConsumeTheOutcome()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true,
            Report("one@b.test\tskip\tThe display name is odd:\tit has a tab in it.\tplanned"));

         Assert.Equal("The display name is odd:\tit has a tab in it.", report.Rows[0].Detail);
         Assert.Equal(DirectorySyncOutcome.Planned, report.Rows[0].Outcome);
      }

      /// <summary>
      /// A line this cannot parse must still reach the screen. Dropping it would be the
      /// one failure that makes the parsed view less trustworthy than the raw string it
      /// replaced.
      /// </summary>
      [Fact]
      public void AnUnrecognisedLineIsKeptRatherThanDropped()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true, Report(
            "one@b.test\tcreate\tWill be created.\tplanned",
            "something the parser has never seen"));

         Assert.Equal(2, report.Rows.Count);
         Assert.Equal(DirectorySyncRowKind.Other, report.Rows[1].Kind);
         Assert.Equal("something the parser has never seen", report.Rows[1].Detail);
      }

      /// <summary>
      /// An action the server might add later must not be silently sorted into an
      /// existing bucket. Showing it as unclassified, with its own text, is what keeps a
      /// future server version honest against an older Control Panel.
      /// </summary>
      [Fact]
      public void AnUnknownActionIsNotGuessedAt()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(true,
            Report("one@b.test\tdemolish\tSomething a later server invented.\tplanned"));

         Assert.Equal(DirectorySyncRowKind.Other, report.Rows[0].Kind);
         Assert.Equal("demolish", report.Rows[0].Action);
      }

      /// <summary>
      /// A run that refused returns Result=false, the reason as its whole text, and no
      /// rows at all. The page shows the reason; it must not also show an empty table
      /// that implies nothing needed doing.
      /// </summary>
      [Fact]
      public void ARefusedRunCarriesItsReasonAndNoRows()
      {
         const string reason = "LDAP is switched off (hMailServer.ini [LDAP] Enabled=0). Nothing has been read "
            + "and nothing has been changed.";

         DirectorySyncReport report = DirectorySyncReport.Parse(false, reason);

         Assert.False(report.Succeeded);
         Assert.Equal(reason, report.Summary);
         Assert.Empty(report.Rows);
         Assert.Empty(report.SkipBreakdown);
         Assert.Equal(0, report.AttentionCount);
      }

      [Fact]
      public void AnEmptyReportIsParsedRatherThanThrowing()
      {
         DirectorySyncReport report = DirectorySyncReport.Parse(false, null);

         Assert.Equal(string.Empty, report.Summary);
         Assert.Empty(report.Rows);
         Assert.Empty(report.SkipBreakdown);
      }

      /// <summary>
      /// The server writes CRLF. A report that arrived with bare newlines - through a
      /// transport that normalised them, or a future server built elsewhere - must parse
      /// identically rather than becoming one giant unparsed line.
      /// </summary>
      [Fact]
      public void BareNewlinesParseTheSameAsCarriageReturnPairs()
      {
         string crlf = Summary + "\r\n\r\none@b.test\tcreate\tWill be created.\tplanned\r\n";
         string lf = Summary + "\n\none@b.test\tcreate\tWill be created.\tplanned\n";

         DirectorySyncReport a = DirectorySyncReport.Parse(true, crlf);
         DirectorySyncReport b = DirectorySyncReport.Parse(true, lf);

         Assert.Equal(a.Summary, b.Summary);
         Assert.Equal(a.Rows.Count, b.Rows.Count);
         Assert.Equal(a.Rows[0].Address, b.Rows[0].Address);
         Assert.Equal(a.Rows[0].Kind, b.Rows[0].Kind);
         Assert.Equal(a.Rows[0].Outcome, b.Rows[0].Outcome);
      }
   }
}
