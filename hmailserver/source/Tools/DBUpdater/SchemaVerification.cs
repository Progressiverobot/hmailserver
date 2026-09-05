// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;

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
                         "update hm_imapfolders set folderspecialuse = folderspecialuse where 1 = 0"),

         // Upgrade6006to6007* - two columns, and therefore two probes rather than one.
         // SQL CE commits each ALTER implicitly and cannot roll back, so this step can
         // half-apply: the first column lands, the second fails, and the version row is
         // never reached. A single probe on either column alone would then either miss
         // the failure or misreport a database that is genuinely fine.
         new SchemaProbe(6007, "hm_domains.domaindkimsecondaryselector",
                         "update hm_domains set domaindkimsecondaryselector = domaindkimsecondaryselector where 1 = 0"),
         new SchemaProbe(6007, "hm_domains.domaindkimsecondaryprivatekeyfile",
                         "update hm_domains set domaindkimsecondaryprivatekeyfile = domaindkimsecondaryprivatekeyfile where 1 = 0"),

         // Upgrade6007to6008* - two columns again, so two probes, for the same
         // half-apply reason as the step above.
         new SchemaProbe(6008, "hm_tcpipports.portclientcertificatepolicy",
                         "update hm_tcpipports set portclientcertificatepolicy = portclientcertificatepolicy where 1 = 0"),
         new SchemaProbe(6008, "hm_tcpipports.portclientcertificatecafile",
                         "update hm_tcpipports set portclientcertificatecafile = portclientcertificatecafile where 1 = 0"),

         // Upgrade6008to6009* - one column, so one probe. The half-apply hazard that
         // forced two probes on the steps above cannot arise with a single ALTER, but the
         // probe is still needed: the MSSQL/CE/MySQL scripts carry [IGNORE-ERRORS], so
         // without it a failed ALTER would be swallowed and the version stamped anyway.
         new SchemaProbe(6009, "hm_sslcertificates.sslprivatekeypassword",
                         "update hm_sslcertificates set sslprivatekeypassword = sslprivatekeypassword where 1 = 0"),

         // Upgrade6010to6011* - a whole new table, hm_inisettings, which holds the
         // [Settings] section of hMailServer.INI so that it reaches backups and can be
         // administered remotely.
         //
         // One probe per COLUMN even though a CREATE TABLE cannot half-apply the way a
         // multi-column ALTER can. Two reasons it is still right here. The step is not
         // one statement: on MSSQL and SQL CE the table is created and then two
         // constraints are added by separate ALTERs, and SQL CE commits each of those as
         // it executes, so "the table exists" does not mean "the step finished". And a
         // probe naming a column is what rule 7 of check-schema-versions.ps1 uses to
         // confirm the same column exists in all three CreateTables scripts - which is
         // the check that catches a new table added to the upgrade path but forgotten in
         // the fresh-install path, leaving new installations without it.
         //
         // There is deliberately NO probe for inisettingid. Every probe is an UPDATE of
         // a column to itself, and inisettingid is an identity column: SQL Server and
         // SQL Server Compact reject "cannot update identity column" when the statement
         // is compiled, before "where 1 = 0" is ever evaluated. The probe would fail on
         // a perfectly correct database, VerifyUpgradedSchema would read that as the
         // step having failed, and the upgrade would roll back - on the two backends
         // most installations use. Every pre-existing probe names a plain data column
         // for the same reason.
         new SchemaProbe(6011, "hm_inisettings.inisettingname",
                         "update hm_inisettings set inisettingname = inisettingname where 1 = 0"),
         new SchemaProbe(6011, "hm_inisettings.inisettingvalue",
                         "update hm_inisettings set inisettingvalue = inisettingvalue where 1 = 0"),
         new SchemaProbe(6011, "hm_inisettings.inisettingfilevalue",
                         "update hm_inisettings set inisettingfilevalue = inisettingfilevalue where 1 = 0"),

         // Upgrade6011to6012* - the rule-criteria value column widened to 2000
         // (probing the column proves it exists, not its width; the width is the
         // upgrade script's to get right) and the vacation begin date on
         // accounts. One probe per column, as ever: SQL CE commits each ALTER
         // implicitly, so a two-column step can half-apply.
         new SchemaProbe(6012, "hm_rule_criterias.criteriamatchvalue",
                         "update hm_rule_criterias set criteriamatchvalue = criteriamatchvalue where 1 = 0"),
         new SchemaProbe(6012, "hm_accounts.accountvacationbegindate",
                         "update hm_accounts set accountvacationbegindate = accountvacationbegindate where 1 = 0"),

         // Upgrade6012to6013* - the message save date (RFC 8514 SAVEDATE): when
         // the message was saved into its current mailbox, distinct from
         // messagecreatetime, which IMAP COPY must preserve as INTERNALDATE.
         new SchemaProbe(6013, "hm_messages.messagesavedate",
                         "update hm_messages set messagesavedate = messagesavedate where 1 = 0"),

         // Upgrade6013to6014* - the RFC 5464 METADATA store: annotations on
         // mailboxes (accountid + folderid) and on the server (0 + 0).
         new SchemaProbe(6014, "hm_imap_metadata.metadatavalue",
                         "update hm_imap_metadata set metadatavalue = metadatavalue where 1 = 0"),

         // Upgrade6014to6015* - the RFC 8474 OBJECTID email id: stable for a
         // message across COPY, unlike the row id, which is why it is its own
         // column stamped at first save and carried by copies.
         new SchemaProbe(6015, "hm_messages.messageemailid",
                         "update hm_messages set messageemailid = messageemailid where 1 = 0"),

         // Upgrade6015to6016* - app passwords: a per-account credential that can
         // be revoked on its own, which is what makes per-account 2FA possible for
         // clients that have nowhere to type a code. One probe per column, because
         // SQL CE commits each statement implicitly and a multi-column step can
         // half-apply.
         new SchemaProbe(6016, "hm_apppasswords.aphash",
                         "update hm_apppasswords set aphash = aphash where 1 = 0"),
         new SchemaProbe(6016, "hm_apppasswords.aplastused",
                         "update hm_apppasswords set aplastused = aplastused where 1 = 0"),
         new SchemaProbe(6016, "hm_apppasswords.apactive",
                         "update hm_apppasswords set apactive = apactive where 1 = 0"),

         // Upgrade6016to6017* - the per-account TOTP secret. Empty means the account
         // has no second factor, which is every account until an administrator enrols
         // one, so the column is the whole feature's on/off switch.
         new SchemaProbe(6017, "hm_accounts.accounttotpsecret",
                         "update hm_accounts set accounttotpsecret = accounttotpsecret where 1 = 0"),

         // Upgrade6017to6018* - the quarantine store. One probe per column that the
         // server writes, because SQL CE commits each statement implicitly and a
         // multi-column step can half-apply.
         new SchemaProbe(6018, "hm_quarantine.quarantinefilename",
                         "update hm_quarantine set quarantinefilename = quarantinefilename where 1 = 0"),
         new SchemaProbe(6018, "hm_quarantine.quarantinerecipients",
                         "update hm_quarantine set quarantinerecipients = quarantinerecipients where 1 = 0"),
         new SchemaProbe(6018, "hm_quarantine.quarantinecreated",
                         "update hm_quarantine set quarantinecreated = quarantinecreated where 1 = 0"),

         // Upgrade6018to6019* - password age and reuse history. The column is on an
         // existing table and the history is a new one, so a half-applied step is
         // exactly the shape these probes exist to catch.
         new SchemaProbe(6019, "hm_accounts.accountpasswordchanged",
                         "update hm_accounts set accountpasswordchanged = accountpasswordchanged where 1 = 0"),
         new SchemaProbe(6019, "hm_passwordhistory.phhash",
                         "update hm_passwordhistory set phhash = phhash where 1 = 0"),
         new SchemaProbe(6019, "hm_passwordhistory.phchanged",
                         "update hm_passwordhistory set phchanged = phchanged where 1 = 0"),

         // Upgrade6019to6020* - the message trace.
         new SchemaProbe(6020, "hm_messagetrace.mtqueueid",
                         "update hm_messagetrace set mtqueueid = mtqueueid where 1 = 0"),
         new SchemaProbe(6020, "hm_messagetrace.mtevent",
                         "update hm_messagetrace set mtevent = mtevent where 1 = 0"),
         new SchemaProbe(6020, "hm_messagetrace.mtoccurred",
                         "update hm_messagetrace set mtoccurred = mtoccurred where 1 = 0"),

         // Upgrade6020to6021* - the per-domain outbound relay. One probe per column,
         // because a partially-applied ALTER leaves a database that opens fine and
         // then fails on the one domain somebody configured.
         new SchemaProbe(6021, "hm_domains.domainrelayhost",
                         "update hm_domains set domainrelayhost = domainrelayhost where 1 = 0"),

         new SchemaProbe(6021, "hm_domains.domainrelayport",
                         "update hm_domains set domainrelayport = domainrelayport where 1 = 0"),

         new SchemaProbe(6021, "hm_domains.domainrelayrequiresauth",
                         "update hm_domains set domainrelayrequiresauth = domainrelayrequiresauth where 1 = 0"),

         new SchemaProbe(6021, "hm_domains.domainrelayusername",
                         "update hm_domains set domainrelayusername = domainrelayusername where 1 = 0"),

         new SchemaProbe(6021, "hm_domains.domainrelaypassword",
                         "update hm_domains set domainrelaypassword = domainrelaypassword where 1 = 0"),

         new SchemaProbe(6021, "hm_domains.domainrelayconnectionsecurity",
                         "update hm_domains set domainrelayconnectionsecurity = domainrelayconnectionsecurity where 1 = 0"),

         // Upgrade6021to6022* - the quota warning notice. This step inserts ROWS
         // rather than altering a table, so the probe asks whether the row is there:
         // a step that half-ran leaves a server that starts fine and then reports
         // "Server message 'QUOTA_WARNING' could not be found" to the one user whose
         // mailbox filled up.
         new SchemaProbe(6022, "hm_servermessages.QUOTA_WARNING",
                         "update hm_servermessages set smtext = smtext where smname = 'QUOTA_WARNING' and 1 = 0"),

         // Upgrade6022to6023* - the full-text index. One probe per column across
         // both tables: a half-applied step here leaves a server that starts, indexes
         // happily into a table missing the column the search then reads, and answers
         // SEARCH with silence rather than an error - which is the worst shape a
         // failure can take, because nobody reports mail they cannot find.
         new SchemaProbe(6023, "hm_messageindexterms.mitmessageid",
                         "update hm_messageindexterms set mitmessageid = mitmessageid where 1 = 0"),

         new SchemaProbe(6023, "hm_messageindexterms.mitaccountid",
                         "update hm_messageindexterms set mitaccountid = mitaccountid where 1 = 0"),

         new SchemaProbe(6023, "hm_messageindexterms.mitterm",
                         "update hm_messageindexterms set mitterm = mitterm where 1 = 0"),

         new SchemaProbe(6023, "hm_messageindexstate.misaccountid",
                         "update hm_messageindexstate set misaccountid = misaccountid where 1 = 0"),

         new SchemaProbe(6023, "hm_messageindexstate.mishighwatermark",
                         "update hm_messageindexstate set mishighwatermark = mishighwatermark where 1 = 0"),

         // Upgrade6023to6024* - the domain-wide out-of-office reply and the blocked
         // sender list. Two features in one step because they landed together, and
         // both fail quietly if half-applied: a missing vacation column means the
         // domain reads back as "no auto-reply" and simply never answers, and a
         // missing blocked-senders table means every listed sender is silently
         // delivered again. Neither raises anything an administrator would see, so
         // each column is probed rather than trusting the step's own success.
         new SchemaProbe(6024, "hm_domains.domainvacationmessageon",
                         "update hm_domains set domainvacationmessageon = domainvacationmessageon where 1 = 0"),
         new SchemaProbe(6024, "hm_domains.domainvacationsubject",
                         "update hm_domains set domainvacationsubject = domainvacationsubject where 1 = 0"),
         new SchemaProbe(6024, "hm_domains.domainvacationmessage",
                         "update hm_domains set domainvacationmessage = domainvacationmessage where 1 = 0"),
         new SchemaProbe(6024, "hm_domains.domainvacationinternalsubject",
                         "update hm_domains set domainvacationinternalsubject = domainvacationinternalsubject where 1 = 0"),
         new SchemaProbe(6024, "hm_domains.domainvacationinternalmessage",
                         "update hm_domains set domainvacationinternalmessage = domainvacationinternalmessage where 1 = 0"),
         new SchemaProbe(6024, "hm_domains.domainvacationexternaloverride",
                         "update hm_domains set domainvacationexternaloverride = domainvacationexternaloverride where 1 = 0"),
         new SchemaProbe(6024, "hm_blocked_senders.bsaddress",
                         "update hm_blocked_senders set bsaddress = bsaddress where 1 = 0"),
         new SchemaProbe(6024, "hm_blocked_senders.bsscore",
                         "update hm_blocked_senders set bsscore = bsscore where 1 = 0"),
         new SchemaProbe(6024, "hm_blocked_senders.bsdescription",
                         "update hm_blocked_senders set bsdescription = bsdescription where 1 = 0"),

         // Upgrade6024to6025* - per-account spam overrides, list moderation, and
         // the messageflags widening BINARYMIME's ninth flag bit needs. The
         // widening cannot be probed by a column-exists trick (the column was
         // always there); what CAN be probed is the five new columns, and a
         // half-applied step fails on one of them.
         new SchemaProbe(6025, "hm_accounts.accountantispamenabled",
                         "update hm_accounts set accountantispamenabled = accountantispamenabled where 1 = 0"),
         new SchemaProbe(6025, "hm_accounts.accountspammarkthreshold",
                         "update hm_accounts set accountspammarkthreshold = accountspammarkthreshold where 1 = 0"),
         new SchemaProbe(6025, "hm_accounts.accountspamdeletethreshold",
                         "update hm_accounts set accountspamdeletethreshold = accountspamdeletethreshold where 1 = 0"),
         new SchemaProbe(6025, "hm_distributionlists.distributionlistmoderatoraddress",
                         "update hm_distributionlists set distributionlistmoderatoraddress = distributionlistmoderatoraddress where 1 = 0"),
         new SchemaProbe(6025, "hm_distributionlists.distributionlistbounceaddress",
                         "update hm_distributionlists set distributionlistbounceaddress = distributionlistbounceaddress where 1 = 0"),

         // Upgrade6027to6028* - the metric history. One probe per column the
         // sampler writes: a half-applied step fails on the first missing one.
         new SchemaProbe(6028, "hm_metricsamples.metricsampletime",
                         "update hm_metricsamples set metricsampletime = metricsampletime where 1 = 0"),
         new SchemaProbe(6028, "hm_metricsamples.metricsamplename",
                         "update hm_metricsamples set metricsamplename = metricsamplename where 1 = 0"),
         new SchemaProbe(6028, "hm_metricsamples.metricsamplevalue",
                         "update hm_metricsamples set metricsamplevalue = metricsamplevalue where 1 = 0"),

         // Upgrade6028to6029* - the archive index. One probe per column the
         // archiver writes and the sweep reads.
         new SchemaProbe(6029, "hm_archiveindex.archivepath",
                         "update hm_archiveindex set archivepath = archivepath where 1 = 0"),
         new SchemaProbe(6029, "hm_archiveindex.archivehold",
                         "update hm_archiveindex set archivehold = archivehold where 1 = 0"),
         new SchemaProbe(6029, "hm_archiveindex.archivemessageid",
                         "update hm_archiveindex set archivemessageid = archivemessageid where 1 = 0")
      };

      /// <summary>
      /// The probes that must succeed once the database has reached
      /// <paramref name="version"/>. Empty for a step that has none registered -
      /// most of the historical chain predates this check.
      /// </summary>
      public static IList<SchemaProbe> GetProbesFor(int version)
      {
         return Probes.Where(probe => probe.Version == version).ToList();
      }
   }
}
