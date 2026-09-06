// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Runtime.InteropServices;
using DBUpdater;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    DBUpdater proves every upgrade step with a probe - SchemaVerification.cs, compiled
   ///    into this assembly as a linked file, so the statements tested here are the
   ///    statements the installer runs. A probe has one job: fail only when the object it
   ///    names is missing. Against the bench database, which is at the current schema and
   ///    has everything, every probe must therefore succeed; one that fails is a statement
   ///    the backend cannot execute, and in the field that reads as a failed upgrade of a
   ///    database that is fine.
   ///
   ///    That is what 6.2.25 shipped for SQL Server Compact (issue #114). The four probes
   ///    for schema 6030 used "case when exists (subquery)", which takes the Compact
   ///    Edition OLE DB provider down with an access violation on a correct database; the
   ///    server caught it as HM10045 "Unknown error", DBUpdater read the failed probe as a
   ///    missing foreign key, and every Compact Edition upgrade through 6030 was reported
   ///    as failed after it had, in fact, succeeded. The bench runs Compact Edition, so this
   ///    fixture runs the probes on exactly the provider that crashed, through exactly the
   ///    path DBUpdater takes - hMailServer.Database.ExecuteSQL over COM - and would have
   ///    failed on the shipped statement. build\check-db-scripts.ps1 runs the same probes
   ///    against a freshly created database before a release.
   /// </summary>
   [TestFixture]
   public class SchemaProbes : TestFixtureBase
   {
      private static Database Database
      {
         get { return SingletonProvider<TestSetup>.Instance.GetApp().Database; }
      }

      [Test]
      [Description("Every probe DBUpdater would run after an upgrade succeeds on a database that has everything the probes name.")]
      public void EveryProbeSucceedsOnACorrectDatabase()
      {
         Database database = Database;
         Assert.AreEqual(database.RequiredVersion, database.CurrentVersion,
            "The bench database is not at the schema this build requires, so the probes would name objects it does not have yet.");

         var probes = SchemaVerification.All;
         Assert.IsTrue(probes.Count > 0, "SchemaVerification.cs registers no probes - the linked file is not what DBUpdater compiles.");

         foreach (SchemaProbe probe in probes)
         {
            try
            {
               database.ExecuteSQL(probe.Statement);
            }
            catch (COMException e)
            {
               Assert.Fail("The probe for " + probe.Describes + " (schema " + probe.Version + ") fails on a correct database, so " +
                           "DBUpdater would report an upgrade to " + probe.Version + " as failed after it succeeded.\r\n" +
                           probe.Statement + "\r\n" + e.Message);
            }
         }
      }

      [Test]
      [Description("The constraint probe fails for a constraint that does not exist - the half without which the probes prove nothing - and leaves hm_dbversion as it was.")]
      public void TheConstraintProbeFailsForAnAbsentConstraint()
      {
         SchemaProbe probe = SchemaVerification.GetProbesFor(6030).First(p => p.Describes == "hm_accounts.fk_hm_accounts_domain");
         string absent = probe.Statement.Replace("constraint_name = 'fk_hm_accounts_domain'", "constraint_name = 'fk_no_such_constraint'");
         Assert.AreNotEqual(probe.Statement, absent, "The probe no longer names the constraint the way this test rewrites it.");

         Database database = Database;
         int before = database.CurrentVersion;

         var refused = Assert.Throws<COMException>(() => database.ExecuteSQL(absent),
            "The probe succeeded for a constraint that does not exist; it would pass a database the upgrade left without its foreign keys.");

         // The failure must be the division, which is the probe working, and not the
         // backend refusing the statement's shape, which is the probe broken - the
         // shipped 6.2.25 probe failed too, for the second reason. HM10044 is the
         // connection reporting the provider's own error; HM10045 is the catch-all that
         // the provider's access violation reached.
         StringAssert.Contains("HM10044", refused.Message, "The probe failed for a reason other than the backend evaluating it.");
         StringAssert.DoesNotContain("HM10045", refused.Message);

         Assert.AreEqual(before, database.CurrentVersion, "A failed probe must not have written hm_dbversion.");

         // The refusal is reported through the API error and also, by the connection
         // that saw it, to the error log; that line is this test's, not a fault.
         LogHandler.ReadAndDeleteErrorLog();
      }

      [Test]
      [Description("The probe distinguishes a foreign key from a primary key of the same name: the constraint type is part of what it asks.")]
      public void TheConstraintProbeAsksForTheType()
      {
         SchemaProbe probe = SchemaVerification.GetProbesFor(6030).First(p => p.Describes == "hm_accounts.fk_hm_accounts_domain");
         string primaryKeyNamedLikeAForeignKey = probe.Statement.Replace("constraint_name = 'fk_hm_accounts_domain'", "constraint_name = 'hm_domains_pk'");

         Assert.Throws<COMException>(() => Database.ExecuteSQL(primaryKeyNamedLikeAForeignKey),
            "hm_domains_pk is a primary key, not a foreign key; a probe that accepts it is not checking the type.");

         LogHandler.ReadAndDeleteErrorLog();
      }
   }
}
