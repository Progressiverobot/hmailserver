// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Legacy;   // StringAssert moved here in NUnit 4
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The DAL's error handling, exercised through IInterfaceDatabase.ExecuteSQL -
   ///    the one route from a test into DALConnection::Execute that reports back
   ///    whether the statement worked.
   ///
   ///    Two questions are asked here, and neither had an answer before:
   ///
   ///    1. Can a *value* inside a statement turn off that statement's error
   ///       handling? The DAL's [IGNORE-ERRORS] marker exists so that re-running an
   ///       upgrade step whose column already exists is harmless, and it used to be
   ///       looked for with a plain substring search over the whole statement -
   ///       including inside quoted string literals. hm_message_metadata and the SQL
   ///       log device both write attacker-supplied text, so that was reachable from
   ///       outside the server, and what it bought was a silently discarded write.
   ///
   ///    2. Is a statement that failed distinguishable from one that matched no
   ///       rows? They come back through the same bool, so the answer had better be
   ///       yes.
   /// </summary>
   [TestFixture]
   public class SqlLayerErrorHandling : TestFixtureBase
   {
      // No such table exists in any of the four schemas, so every backend rejects a
      // statement naming it, and none of them can touch data on the way to doing so.
      // The WHERE clause is belt and braces: even if a table of this name were ever
      // added, nothing would match.
      private const string MissingTable = "hm_no_such_table_regressiontests";

      [TearDown]
      public void ConsumeExpectedErrorReports()
      {
         // Two of the tests below run a statement the database is meant to reject,
         // and a rejected statement is reported to the ERROR log by
         // DALConnection::Execute as HM5032. TestSetup.PerformBasicSetup - which runs
         // in every test's SetUp, in every fixture - fails outright if an ERROR log
         // exists, so a report left behind here would fail the *next* test rather
         // than this one and bury the real cause. It is consumed and printed instead.
         //
         // The tests that must NOT produce a report assert its absence themselves,
         // before this runs, so consuming unconditionally here cannot hide a failure.
         if (File.Exists(LogHandler.GetErrorLogFileName()))
         {
            var errors = LogHandler.ReadAndDeleteErrorLog();

            Console.WriteLine("hMailServer error log (expected for this fixture):");
            Console.WriteLine(errors);
         }
      }

      private Exception TryExecuteSql(string sql)
      {
         try
         {
            SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(sql);
            return null;
         }
         catch (Exception ex)
         {
            return ex;
         }
      }

      /// <summary>
      ///    A statement that fails must be reported as failing even when one of its
      ///    values happens to contain the text [IGNORE-ERRORS].
      ///
      ///    Against the UNFIXED build this test fails: ADOConnection::TryExecute,
      ///    SQLCEConnection::TryExecute, MySQLConnection::TryExecute and
      ///    PGConnection::TryExecute each did queryString.Find("[IGNORE-ERRORS]") over
      ///    the whole statement, found it inside the quoted value, and returned
      ///    DALSuccess. ExecuteSQL therefore returned S_OK for a statement the
      ///    database had rejected, no exception reached here, and the assertion below
      ///    that one was thrown fails.
      /// </summary>
      [Test]
      [Description("A value containing [IGNORE-ERRORS] must not switch off error handling for the statement it appears in.")]
      public void TestMarkerInsideAStringLiteralDoesNotSuppressTheFailure()
      {
         var sql = string.Format(
            "update {0} set somecolumn = '[IGNORE-ERRORS]' where 1 = 0", MissingTable);

         var thrown = TryExecuteSql(sql);

         Assert.IsNotNull(thrown,
            "A statement against a table that does not exist was reported as having succeeded. " +
            "The only thing distinguishing it from a working statement is the text [IGNORE-ERRORS] " +
            "inside one of its values, which is not a place that marker can legitimately appear.");

         StringAssert.Contains("Execution of SQL statement failed", thrown.Message);
      }

      /// <summary>
      ///    The negative control for the test above, and the reason this fixture is
      ///    worth having rather than just the tightening: the marker must go on doing
      ///    its job where it is genuinely written, which in this tree is always a
      ///    trailing SQL comment. Six upgrade scripts and every CREATE TABLE the SQL
      ///    log device issues depend on it; if the tightening had over-reached, a
      ///    re-run of DBUpdater against an already-upgraded database would start
      ///    failing on "column already exists".
      ///
      ///    Against the UNFIXED build this test also passes - which is the point. It
      ///    is here to fail if someone removes the marker handling rather than
      ///    narrowing it.
      /// </summary>
      [Test]
      [Description("The [IGNORE-ERRORS] marker in a trailing comment still discards the failure, and reports nothing.")]
      public void TestMarkerInATrailingCommentStillSuppressesTheFailure()
      {
         var sql = string.Format(
            "update {0} set somecolumn = 1 where 1 = 0 --- [IGNORE-ERRORS]", MissingTable);

         var thrown = TryExecuteSql(sql);

         Assert.IsNull(thrown,
            "A statement carrying the [IGNORE-ERRORS] marker in a trailing comment should have had its " +
            "failure discarded, which is what makes re-running an upgrade step harmless. Exception was: " +
            (thrown == null ? string.Empty : thrown.ToString()));

         // A discarded failure is discarded completely: it must not reach the ERROR
         // log either, or every DBUpdater re-run would leave one behind.
         Assert.IsFalse(File.Exists(LogHandler.GetErrorLogFileName()),
            "A statement whose failure was discarded by the [IGNORE-ERRORS] marker still wrote to the ERROR log.");
      }

      /// <summary>
      ///    A statement that the database rejected and a statement that matched no
      ///    rows must not look the same to the caller. They travel back through the
      ///    same bool, so this pins the two ends of it.
      ///
      ///    Passes against the unfixed build as well: it documents the contract the
      ///    rest of this fixture depends on rather than testing a repair. If it ever
      ///    fails, every "if (!Execute(...)) return false" in the persistence layer is
      ///    reading something other than what it thinks.
      /// </summary>
      [Test]
      [Description("A rejected statement raises; a statement that matched no rows does not.")]
      public void TestFailedStatementIsDistinguishableFromZeroRowsAffected()
      {
         // Matches nothing - hm_domains uses positive identities - and so writes
         // nothing, but it is a statement the database is happy to run.
         var zeroRows = TryExecuteSql(
            "update hm_domains set domainname = domainname where domainid = -1");

         Assert.IsNull(zeroRows,
            "A valid UPDATE that matched no rows was reported as a failure. Exception was: " +
            (zeroRows == null ? string.Empty : zeroRows.ToString()));

         // No ERROR log yet: nothing has gone wrong so far, and the assertion below
         // would be worthless if something already had.
         Assert.IsFalse(File.Exists(LogHandler.GetErrorLogFileName()),
            "An UPDATE that matched no rows wrote to the ERROR log.");

         var rejected = TryExecuteSql(
            string.Format("update {0} set somecolumn = 1 where 1 = 0", MissingTable));

         Assert.IsNotNull(rejected,
            "A statement naming a table that does not exist was reported as having succeeded.");
      }
   }
}
