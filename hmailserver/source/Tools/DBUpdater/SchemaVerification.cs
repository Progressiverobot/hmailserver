// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Collections.Generic;

namespace DBUpdater
{
   /// <summary>
   /// One statement that proves a schema object an upgrade step was supposed to
   /// create is really there.
   /// </summary>
   internal sealed class SchemaProbe
   {
      public SchemaProbe(int version, string describes, string statement)
      {
         Version = version;
         Describes = describes;
         Statement = statement;
      }

      /// <summary>The database version this probe proves has been reached.</summary>
      public int Version { get; private set; }

      /// <summary>Human-readable name of the object, e.g. "hm_imapfolders.folderspecialuse".</summary>
      public string Describes { get; private set; }

      public string Statement { get; private set; }
   }

   /// <summary>
   /// Proof that an upgrade step changed the schema, rather than merely not
   /// throwing.
   ///
   /// Why this exists. Three of the four dialects mark their ALTER TABLE
   /// statements with the DAL's [IGNORE-ERRORS] marker so that re-running a step
   /// against an already-upgraded database is harmless. But the marker is matched
   /// anywhere in the statement text and then discards *every* error, not just
   /// "column already exists" - see MySQLConnection::TryExecute
   /// ("bool bIgnoreErrors = SQL.Find(_T(\"[IGNORE-ERRORS]\")) >= 0"),
   /// ADOConnection::Execute and SQLCEConnection::Execute, which all do the same
   /// thing. The "update hm_dbversion set value = N" statement that follows in
   /// the same script carries no marker, so it always runs.
   ///
   /// The consequence is the worst shape a schema upgrade has: the ALTER fails
   /// for a real reason - insufficient rights, a row-size limit, a syntax form
   /// the backend rejects - the failure is discarded, hm_dbversion is stamped
   /// with the new version anyway, and DBUpdater reports success. The server then
   /// starts happily, because the version it requires is the version it finds,
   /// and IMAPFolders::LoadFolders - which selects folderspecialuse by name -
   /// fails for every account on the system. Nothing anywhere says why.
   ///
   /// A probe is a statement that cannot fail for any reason other than the
   /// object being absent, and costs nothing to run:
   /// "update <table> set <column> = <column> where 1 = 0". All four backends
   /// resolve column names when they prepare a statement, so an empty table
   /// proves a column exists just as well as a populated one, and "where 1 = 0"
   /// matches nothing - the statement writes no rows on any backend.
   ///
   /// It is deliberately an UPDATE rather than the more obvious
   /// "select <column> from <table> where 1 = 0". Both prove the same thing, but
   /// InterfaceDatabase::ExecuteSQLWithReturn always passes a non-null insert-id
   /// pointer, and on a statement that returns tuples that sends
   /// PGConnection::TryExecute into "PQgetvalue(pResult, 0, 0)" against an empty
   /// result - an out-of-range read libpq answers with NULL and an internal
   /// notice, harmless but not something a verification path should be the first
   /// caller to reach routinely. An UPDATE returns a command status instead, which
   /// is the same path every upgrade script already takes. DBUpdater holds rights
   /// to ALTER the table a line earlier, so it certainly holds rights to update
   /// nothing in it.
   ///
   /// Registering a probe is not optional for a new upgrade step whose script
   /// carries [IGNORE-ERRORS]: build\check-schema-versions.ps1 fails the release
   /// if a marked script has no probe here.
   /// </summary>
   internal static class SchemaVerification
   {
      private static readonly SchemaProbe[] Probes =
      {
         // Upgrade5001to5002{MSSQL,MySQL,MSSQLCE}.sql - marked [IGNORE-ERRORS].
         new SchemaProbe(5002, "hm_rule_actions.actionrouteid",
                         "update hm_rule_actions set actionrouteid = actionrouteid where 1 = 0"),

         // Upgrade6005to6006{MSSQL,MySQL,MSSQLCE}.sql - marked [IGNORE-ERRORS].
         // The PGSQL script for this step uses "add column if not exists" and so
         // reports genuine failures on its own; the probe covers it regardless,
         // because a probe that only runs on some backends is a probe that gets
         // out of step with the scripts.
         new SchemaProbe(6006, "hm_imapfolders.folderspecialuse",
                         "update hm_imapfolders set folderspecialuse = folderspecialuse where 1 = 0")
      };

      /// <summary>
      /// The probes that must succeed once the database has reached
      /// <paramref name="version"/>. Empty for a step that has none registered -
      /// most of the historical chain predates this check.
      /// </summary>
      public static IList<SchemaProbe> GetProbesFor(int version)
      {
         List<SchemaProbe> result = new List<SchemaProbe>();

         foreach (SchemaProbe probe in Probes)
         {
            if (probe.Version == version)
               result.Add(probe);
         }

         return result;
      }
   }
}
