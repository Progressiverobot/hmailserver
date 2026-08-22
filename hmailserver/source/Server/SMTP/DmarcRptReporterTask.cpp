// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// See DmarcRptReporterTask.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "DmarcRptReporterTask.h"
#include "RecipientParser.h"

#include "../Common/AntiSpam/DMARC/DMARC.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/MessageRecipients.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/TCPIP/DNSResolver.h"
#include "../Common/Util/Time.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DmarcRptReporterTask::DmarcRptReporterTask()
   {
      // Same reasoning, wording and placement as TlsRptReporterTask: said once
      // per service start because the task is constructed once, in the
      // application log because an empty DmarcRptFromAddress is the shipped
      // default and a default must not be reported as an error.
      if (IniFileSettings::Instance()->GetDmarcRptFromAddress().IsEmpty())
      {
         LOG_APPLICATION(_T("DMARCRPT: DMARC aggregate reporting (RFC 7489) is collecting statistics but will never send a report: DmarcRptFromAddress is empty in [Settings] of hMailServer.ini, which is the default, so each completed day of data is discarded unsent. Set DmarcRptFromAddress to a mailbox here to start sending reports to the domains that request them with a rua= tag."));
      }
   }

   DmarcRptReporterTask::~DmarcRptReporterTask()
   {

   }

   void
   DmarcRptReporterTask::DoWork()
   {
      SendReportsNow(false);
   }

   int
   DmarcRptReporterTask::SendReportsNow(bool includeCurrentDay)
   {
      std::vector<AnsiString> days = DmarcRptStore::Instance()->GetCompletedDays();

      // Today's bucket is deliberately absent from GetCompletedDays - a day is
      // reported once it can no longer grow. The on-demand caller asks for it
      // anyway; see the header.
      if (includeCurrentDay)
         days.push_back(DmarcRptStore::GetCurrentDayKey());

      if (days.empty())
         return 0;

      String fromAddress = IniFileSettings::Instance()->GetDmarcRptFromAddress();

      int reportsSent = 0;

      for (const AnsiString &dayKey : days)
      {
         std::map<AnsiString, DmarcRptStore::DomainBucket> domains = DmarcRptStore::Instance()->PopDay(dayKey);

         // Without a configured sender address, the data is discarded -
         // reporting is disabled but memory must not grow.
         if (fromAddress.IsEmpty())
            continue;

         for (auto iter = domains.begin(); iter != domains.end(); ++iter)
         {
            if (SendReportForDomain_(dayKey, iter->second))
               reportsSent++;
         }
      }

      return reportsSent;
   }

   bool
   DmarcRptReporterTask::ParseRuaTargets(const AnsiString &record, std::vector<String> &addresses)
   {
      AnsiString narrow = record;
      narrow.Trim();

      if (!narrow.StartsWith("v=DMARC1"))
         return false;

      std::vector<AnsiString> parts = StringParser::SplitString(narrow, ";");
      for (AnsiString part : parts)
      {
         part.Trim();
         if (!part.StartsWith("rua="))
            continue;

         std::vector<AnsiString> uris = StringParser::SplitString(part.Mid(4), ",");
         for (AnsiString uri : uris)
         {
            uri.Trim();

            if (!uri.StartsWith("mailto:"))
               continue; // https endpoints are not supported.

            AnsiString address = uri.Mid(7);

            // RFC 7489 6.2: a URI may carry a maximum-size suffix after '!'
            // ("mailto:rep@x.test!10m"). This reporter does not enforce
            // sizes - a day of aggregate rows is small - but the suffix is
            // not part of the mailbox.
            int sizePosition = address.Find("!");
            if (sizePosition >= 0)
               address = address.Mid(0, sizePosition);

            // Strip any URI parameters.
            int parameterPosition = address.Find("?");
            if (parameterPosition >= 0)
               address = address.Mid(0, parameterPosition);

            address.Trim();
            if (!address.IsEmpty())
               addresses.push_back(String(address));
         }
      }

      return true;
   }

   bool
   DmarcRptReporterTask::IsExternalDestination(const String &policyDomain, const String &targetDomain)
   {
      // RFC 7489 7.1 draws the line at the organizational domain: a report
      // address at mail.example.com is internal to a policy at example.com.
      return DMARC::GetOrganizationalDomain(policyDomain) != DMARC::GetOrganizationalDomain(targetDomain);
   }

   bool
   DmarcRptReporterTask::ExternalDestinationAuthorized_(const String &policyDomain, const String &targetDomain)
   {
      DNSResolver resolver;

      String query = policyDomain + "._report._dmarc." + targetDomain;

      std::vector<String> txtRecords;
      if (!resolver.GetTXTRecords(query, txtRecords))
         return false;

      for (String record : txtRecords)
      {
         if (record.Trim().StartsWith(_T("v=DMARC1")))
            return true;
      }

      return false;
   }

   bool
   DmarcRptReporterTask::GetReportingAddresses_(const String &policyDomain, std::vector<String> &addresses)
   {
      DNSResolver resolver;

      std::vector<String> txtRecords;
      if (!resolver.GetTXTRecords("_dmarc." + policyDomain, txtRecords))
         return false;

      std::vector<String> requested;

      bool policySeen = false;
      for (const String &record : txtRecords)
      {
         if (ParseRuaTargets(AnsiString(record), requested))
         {
            policySeen = true;
            break;
         }
      }

      if (!policySeen)
         return false;

      for (const String &address : requested)
      {
         String targetDomain = StringParser::ExtractDomain(address);
         if (targetDomain.IsEmpty())
            continue;

         if (IsExternalDestination(policyDomain, targetDomain) &&
             !ExternalDestinationAuthorized_(policyDomain, targetDomain))
         {
            // The mail-cannon control (RFC 7489 7.1): anyone can publish a rua
            // naming anyone's mailbox, so a target outside the policy's own
            // organizational domain is only used when the target's DNS says it
            // wants these reports. Skipped silently otherwise, which is what
            // the RFC asks for - the policy domain's owner is the one who
            // misconfigured it, and they are not reachable by definition.
            LOG_DEBUG("DMARCRPT: rua target " + AnsiString(address) + " for " + AnsiString(policyDomain) +
                      " is external and unverified (no " + AnsiString(policyDomain) + "._report._dmarc." +
                      AnsiString(targetDomain) + " record). Skipping this target.");
            continue;
         }

         addresses.push_back(address);
      }

      return !addresses.empty();
   }

   bool
   DmarcRptReporterTask::SendReportForDomain_(const AnsiString &dayKey, const DmarcRptStore::DomainBucket &bucket)
   {
      if (bucket.rows.empty())
         return false;

      String policyDomain = bucket.policy_domain;

      std::vector<String> reportingAddresses;
      if (!GetReportingAddresses_(policyDomain, reportingAddresses))
         return false; // Domain does not (verifiably) request reports.

      if (bucket.dropped_rows > 0)
      {
         // The schema has nowhere to say "and N more" - an under-counting
         // report looks complete. Saying so here is the only honest option.
         String message;
         message.Format(_T("DMARCRPT: the aggregate report for %s covering %s under-counts: %d unique source/verdict combinations were dropped at the per-domain row cap."),
            policyDomain.c_str(), String(dayKey).c_str(), bucket.dropped_rows);
         LOG_APPLICATION(message);
      }

      String fromAddress = IniFileSettings::Instance()->GetDmarcRptFromAddress();
      String submitter = StringParser::ExtractDomain(fromAddress);

      const int schemaVersion = IniFileSettings::Instance()->GetDmarcRptSchemaVersion();

      // One clock reading for the whole report. The report_id, the Message-ID
      // built from it and - in the 9990 form - the filename's unique-id all have
      // to agree, and three separate time(nullptr) calls can straddle a second
      // boundary.
      const __int64 generatedAt = static_cast<__int64>(time(nullptr));

      // The policy domain is part of the id, not just the day and the second.
      // Two domains reported in the same second otherwise get identical
      // report_id values - and identical Message-IDs, since that is built from
      // the same string - so a shared report processor that keys duplicate
      // suppression on org_name + report_id (RFC 7489 7.2.1.1 requires the id to
      // be unique) discards all but the first, while the log says both were sent.
      //
      // The shape is unchanged for 9990: 3.5.1 types the Report-ID as
      // dot-atom-text with an optional "@" domain, which this already is.
      AnsiString reportId;
      reportId.Format("%hs.%hs.%I64d@%hs", dayKey.c_str(), bucket.policy_domain.c_str(),
         generatedAt, AnsiString(submitter).c_str());

      // RFC 9990 3.1.1.3's generator element: who to go to about a malformed
      // report. Empty for the 7489 form, whose schema has no such element.
      AnsiString generator;
      if (schemaVersion == SchemaRfc9990)
         generator = "hMailServer " + AnsiString(Application::Instance()->GetVersionNumber());

      AnsiString reportXml = BuildReportXml(dayKey, bucket, reportId, fromAddress,
         IniFileSettings::Instance()->GetDmarcRptOrganizationName(), schemaVersion, generator);

      AnsiString narrowDomain = bucket.policy_domain;
      AnsiString narrowSubmitter = submitter;

      AnsiString boundary;
      boundary.Format("dmarcrpt-%I64d", generatedAt);

      // receiver "!" policy-domain "!" begin "!" end [ "!" unique-id ] "."
      // extension (RFC 7489 7.2.1.1, restated as RFC 9990 3.5.2). Plain .xml
      // rather than .xml.gz: this server links no compression library, and the
      // gzip is a SHOULD in both. The media type is text/xml for an uncompressed
      // report in both too (9990 3.5.2 says so explicitly), so the MIME part
      // below is the same whichever schema was built.
      __int64 rangeBegin = 0;
      {
         tm dayStart = {};
         if (sscanf_s(dayKey.c_str(), "%4d-%2d-%2d", &dayStart.tm_year, &dayStart.tm_mon, &dayStart.tm_mday) == 3)
         {
            dayStart.tm_year -= 1900;
            dayStart.tm_mon -= 1;
            rangeBegin = _mkgmtime64(&dayStart);
         }
      }
      const __int64 rangeEnd = rangeBegin + 86399;

      AnsiString attachmentName;
      if (schemaVersion == SchemaRfc9990)
      {
         // The optional unique-id, and it earns its place rather than being
         // decoration. SendReportsNow(true) can report the SAME day for the SAME
         // policy domain more than once - that is exactly what the on-demand
         // diagnostic is for - and 3.5.2 tells a receiver that a repeated
         // filename means "overwrite the original or discard this one", because
         // a repeated filename is how a re-send is signalled. Without a unique
         // id the second report of a day is legitimately thrown away, and the
         // two reports are not duplicates: each covers only what was recorded
         // since the previous destructive pop.
         //
         // 3.1.4 asks the Report-ID and the unique-id to be identical, which
         // their two grammars make impossible in general - ridfmt is
         // dot-atom-text with an optional "@" domain, unique-id is
         // 1*(ALPHA / DIGIT) and admits neither "." nor "@". The seconds-since-
         // epoch the Report-ID is built from satisfies both, so the same value
         // appears in each, spelled the way each grammar allows.
         attachmentName.Format("%hs!%hs!%I64d!%I64d!%I64d.xml", narrowSubmitter.c_str(), narrowDomain.c_str(),
            rangeBegin, rangeEnd, generatedAt);
      }
      else
      {
         // Left exactly as it was. The 7489 form is what receivers parse today,
         // and a filename is one of the two things a report processor keys on -
         // changing it for every existing installation to fix a case only the
         // on-demand diagnostic reaches is not a trade worth making.
         attachmentName.Format("%hs!%hs!%I64d!%I64d.xml", narrowSubmitter.c_str(), narrowDomain.c_str(), rangeBegin, rangeEnd);
      }

      String recipientList;
      for (size_t i = 0; i < reportingAddresses.size(); i++)
      {
         if (i > 0)
            recipientList += ", ";
         recipientList += reportingAddresses[i];
      }

      AnsiString mailContent;
      mailContent += "From: <" + AnsiString(fromAddress) + ">\r\n";
      mailContent += "To: " + AnsiString(recipientList) + "\r\n";
      mailContent += "Subject: Report Domain: " + narrowDomain + " Submitter: " + narrowSubmitter + " Report-ID: <" + reportId + ">\r\n";
      mailContent += "Message-ID: <" + reportId + ">\r\n";
      mailContent += "Date: " + AnsiString(Time::GetCurrentMimeDate()) + "\r\n";
      mailContent += "MIME-Version: 1.0\r\n";
      mailContent += "Content-Type: multipart/mixed; boundary=\"" + boundary + "\"\r\n";
      mailContent += "\r\n";
      mailContent += "--" + boundary + "\r\n";
      mailContent += "Content-Type: text/plain; charset=\"us-ascii\"\r\n";
      mailContent += "\r\n";
      mailContent += "This is a DMARC aggregate report for " + narrowDomain + " from " + narrowSubmitter + ".\r\n";
      mailContent += "\r\n";
      mailContent += "--" + boundary + "\r\n";
      mailContent += "Content-Type: text/xml\r\n";
      mailContent += "Content-Disposition: attachment; filename=\"" + attachmentName + "\"\r\n";
      mailContent += "\r\n";
      mailContent += reportXml;
      mailContent += "\r\n";
      mailContent += "--" + boundary + "--\r\n";

      std::shared_ptr<Message> reportMessage = std::shared_ptr<Message>(new Message());
      reportMessage->SetState(Message::Delivering);
      reportMessage->SetFromAddress(fromAddress);

      const String fileName = PersistentMessage::GetFileName(reportMessage);

      if (!FileUtilities::WriteToFile(fileName, mailContent))
      {
         LOG_APPLICATION("DMARCRPT: Failed to write report message file for domain " + policyDomain + ".");
         return false;
      }

      reportMessage->SetSize(FileUtilities::FileSize(fileName));

      RecipientParser recipientParser;
      for (const String &address : reportingAddresses)
      {
         bool recipientOk = false;
         recipientParser.CreateMessageRecipientList(address, reportMessage->GetRecipients(), recipientOk);
      }

      if (reportMessage->GetRecipients()->GetCount() == 0)
      {
         FileUtilities::DeleteFile(fileName);
         return false;
      }

      // Checked, and for the same reason the TLS-RPT save is: recording "sent"
      // for a report that was never queued is the wrong way round for a
      // reporting feature.
      if (!PersistentMessage::SaveObject(reportMessage))
      {
         FileUtilities::DeleteFile(fileName);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6112, "DmarcRptReporterTask::SendReportForDomain_",
            "DMARCRPT: the aggregate DMARC report for " + policyDomain + " could not be saved and has NOT been sent.");

         return false;
      }

      Application::Instance()->SubmitPendingEmail();

      LOG_APPLICATION("DMARCRPT: Sent aggregate DMARC report for " + policyDomain + " covering " + String(dayKey) + ".");

      return true;
   }

   AnsiString
   DmarcRptReporterTask::BuildReportXml(const AnsiString &dayKey,
                                        const DmarcRptStore::DomainBucket &bucket,
                                        const AnsiString &reportId,
                                        const String &contactEmail,
                                        const AnsiString &organizationName,
                                        int schemaVersion,
                                        const AnsiString &generator)
   {
      // Only an exact 2 selects the newer schema. Everything else - including a
      // value that somehow got past the ini validation - is the 7489 form,
      // because that is the one every report processor in the field parses and
      // an unparseable report is indistinguishable from no report at all.
      const bool rfc9990 = (schemaVersion == SchemaRfc9990);

      __int64 rangeBegin = 0;
      {
         tm dayStart = {};
         if (sscanf_s(dayKey.c_str(), "%4d-%2d-%2d", &dayStart.tm_year, &dayStart.tm_mon, &dayStart.tm_mday) == 3)
         {
            dayStart.tm_year -= 1900;
            dayStart.tm_mon -= 1;
            rangeBegin = _mkgmtime64(&dayStart);
         }
      }
      const __int64 rangeEnd = rangeBegin + 86399;

      AnsiString xml;
      xml += "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n";

      if (rfc9990)
      {
         // RFC 9990 6.1 registers the namespace and Appendix A declares it as the
         // schema's targetNamespace with elementFormDefault="qualified" - so every
         // element in the document belongs to it, which a default xmlns on the root
         // achieves without having to prefix any of them. RFC 7489's schema had no
         // target namespace at all, which is why the form below carries none: the
         // namespace IS how a consumer tells the two documents apart.
         xml += "<feedback xmlns=\"urn:ietf:params:xml:ns:dmarc-2.0\">\r\n";

         // 1.0, NOT 2.0, and this is the one thing in this schema that is easy to
         // get wrong. RFC 9990 3.1.1.2 says the version element "MUST have the
         // value 1.0", and the Appendix B sample - inside the dmarc-2.0 namespace -
         // says 1.0 too. The 2.0 belongs to the namespace, which versions the
         // SCHEMA; this element versions the report format, and it did not change.
         xml += "  <version>1.0</version>\r\n";
      }
      else
         xml += "<feedback>\r\n";

      xml += "  <report_metadata>\r\n";
      xml += "    <org_name>" + XmlEscape_(organizationName) + "</org_name>\r\n";
      xml += "    <email>" + XmlEscape_(AnsiString(contactEmail)) + "</email>\r\n";
      xml += "    <report_id>" + XmlEscape_(reportId) + "</report_id>\r\n";

      AnsiString range;
      range.Format("    <date_range><begin>%I64d</begin><end>%I64d</end></date_range>\r\n", rangeBegin, rangeEnd);
      xml += range;

      // report_metadata/generator is new in 9990 (3.1.1.3): "the name and version
      // of the report generator; this can help the Report Consumer find out where
      // to report bugs". Last in the element, which is the order of the RFC's own
      // table - ReportMetadataType is an xs:all so any order validates, but a
      // consumer written by reading the table down the page will expect this one.
      if (rfc9990 && !generator.IsEmpty())
         xml += "    <generator>" + XmlEscape_(generator) + "</generator>\r\n";

      xml += "  </report_metadata>\r\n";
      xml += "  <policy_published>\r\n";
      xml += "    <domain>" + XmlEscape_(bucket.policy_domain) + "</domain>\r\n";

      if (rfc9990)
      {
         // PolicyPublishedType is an xs:all, so ordering does not affect
         // validation - which is just as well, because the RFC gives three
         // different orders (Table 5, the Appendix A XSD, and the Appendix B
         // sample). The XSD's declaration order is used here on the reasoning
         // that a hand-written parser that assumes an order was most likely
         // written from the schema.
         xml += "    <p>" + XmlEscape_(NormalizeDisposition_(bucket.p)) + "</p>\r\n";

         if (!bucket.sp.IsEmpty())
            xml += "    <sp>" + XmlEscape_(NormalizeDisposition_(bucket.sp)) + "</sp>\r\n";

         // np= has no home at all in the 7489 schema, so this is the first form of
         // the report that can tell a domain owner what this server saw them
         // publish for subdomains that do not exist. Omitted rather than defaulted
         // when absent: unlike adkim/aspf, "no np" is not the same statement as
         // "np=none" - the first means sp= or p= governed, the second means the
         // owner deliberately chose the weakest policy for invented subdomains.
         if (!bucket.np.IsEmpty())
            xml += "    <np>" + XmlEscape_(NormalizeDisposition_(bucket.np)) + "</np>\r\n";

         xml += "    <adkim>" + XmlEscape_(NormalizeAlignment_(bucket.adkim)) + "</adkim>\r\n";
         xml += "    <aspf>" + XmlEscape_(NormalizeAlignment_(bucket.aspf)) + "</aspf>\r\n";

         // Which mechanism ANSWERED, recorded per domain when the day's first
         // message arrived. Omitted when the store holds neither value, which is
         // what a bucket recorded by an older build or constructed by hand looks
         // like - an absent optional element is honest, an invented one is not.
         const AnsiString discoveryMethod = NormalizeDiscoveryMethod_(bucket.discovery_method);
         if (!discoveryMethod.IsEmpty())
            xml += "    <discovery_method>" + discoveryMethod + "</discovery_method>\r\n";

         // testing is the t= tag. Emitted even when the record carried none,
         // because 3.1.1.5 says unspecified tags have their default values and the
         // default is n - the same reasoning that makes adkim/aspf explicit above.
         xml += "    <testing>" + NormalizeTesting_(bucket.testing) + "</testing>\r\n";

         // No <pct>. RFC 9990 removed it from the schema outright, along with the
         // pct= tag itself in RFC 9989 - emitting it here would fail validation
         // against the very namespace this document declares. bucket.pct is still
         // recorded and still reported in the 7489 form below.
      }
      else
      {
         xml += "    <adkim>" + XmlEscape_(NormalizeAlignment_(bucket.adkim)) + "</adkim>\r\n";
         xml += "    <aspf>" + XmlEscape_(NormalizeAlignment_(bucket.aspf)) + "</aspf>\r\n";
         xml += "    <p>" + XmlEscape_(NormalizeDisposition_(bucket.p)) + "</p>\r\n";

         // sp is optional in the schema, so an absent one is omitted rather than
         // invented - but a PRESENT one still has to be a legal value.
         if (!bucket.sp.IsEmpty())
            xml += "    <sp>" + XmlEscape_(NormalizeDisposition_(bucket.sp)) + "</sp>\r\n";

         AnsiString pct;
         pct.Format("    <pct>%d</pct>\r\n", bucket.pct);
         xml += pct;
      }

      xml += "  </policy_published>\r\n";

      for (const DmarcRptStore::Row &row : bucket.rows)
      {
         xml += "  <record>\r\n";
         xml += "    <row>\r\n";
         xml += "      <source_ip>" + XmlEscape_(row.source_ip) + "</source_ip>\r\n";

         AnsiString count;
         count.Format("      <count>%d</count>\r\n", row.count);
         xml += count;

         AnsiString disposition = row.disposition;
         if (rfc9990)
            disposition = ActionDisposition_(row.disposition, row.dkim == "pass", row.spf == "pass");

         xml += "      <policy_evaluated>\r\n";
         xml += "        <disposition>" + XmlEscape_(disposition) + "</disposition>\r\n";
         xml += "        <dkim>" + XmlEscape_(row.dkim) + "</dkim>\r\n";
         xml += "        <spf>" + XmlEscape_(row.spf) + "</spf>\r\n";
         xml += "      </policy_evaluated>\r\n";
         xml += "    </row>\r\n";
         xml += "    <identifiers>\r\n";
         xml += "      <header_from>" + XmlEscape_(row.header_from) + "</header_from>\r\n";

         // identifiers/envelope_from is optional in both schemas and has never
         // been emitted in the 7489 form; adding it there would change what
         // existing receivers get from a form that is not supposed to change.
         // In the 9990 form it is emitted because the data is already in hand
         // and the Appendix B sample carries it - it is what lets a reader see
         // WHICH envelope the auth_results/spf domain below belongs to.
         if (rfc9990 && !row.envelope_from_domain.IsEmpty())
            xml += "      <envelope_from>" + XmlEscape_(row.envelope_from_domain) + "</envelope_from>\r\n";

         xml += "    </identifiers>\r\n";
         xml += "    <auth_results>\r\n";

         for (size_t i = 0; i < row.dkim_passing_domains.size(); i++)
         {
            xml += "      <dkim><domain>" + XmlEscape_(row.dkim_passing_domains[i]) + "</domain>";

            AnsiString selector;
            if (i < row.dkim_passing_selectors.size())
               selector = row.dkim_passing_selectors[i];

            // The selector is what turns "something of yours signed this" into
            // "this key signed this", which is the difference between a report a
            // domain owner can read and one they can act on - during a key
            // rotation, or when working out which key a forger has got hold of.
            //
            // The two schemas differ on what to do when the signature's s= could
            // not be read. 7489 makes the element optional, so it is left out
            // rather than emitted empty, which a parser would read as a key named
            // "". 9990 Appendix A makes it minOccurs="1" - REQUIRED, and Appendix
            // C lists that as one of the deliberate changes - so leaving it out is
            // not available: the choice is between an empty element and dropping
            // the whole <dkim>, and dropping it would throw away the d= domain,
            // which is the more useful half. An empty selector is at least
            // schema-valid and says plainly that none was recoverable.
            if (rfc9990 || !selector.IsEmpty())
               xml += "<selector>" + XmlEscape_(selector) + "</selector>";

            xml += "<result>pass</result></dkim>\r\n";
         }

         // The raw SPF result, distinct from the ALIGNED verdict in
         // policy_evaluated: SPF can pass for an envelope domain that does not
         // align with the From header, and the report schema keeps the two
         // apart for exactly that case.
         //
         // scope is unchanged between the two forms and did not need to be:
         // "mfrom" is the only value 9990's SPFDomainScope admits (7489 also
         // allowed "helo", which this server never emitted because DMARC only
         // ever evaluates the MAIL FROM identity).
         if (!row.envelope_from_domain.IsEmpty())
         {
            xml += "      <spf><domain>" + XmlEscape_(row.envelope_from_domain) + "</domain><scope>mfrom</scope><result>";
            xml += row.spf_passed ? "pass" : "fail";
            xml += "</result></spf>\r\n";
         }
         else
         {
            // A message with no envelope sender (a bounce) evaluated SPF as none.
            // 7489 required an spf element in auth_results and 9990 made it
            // optional, so this is mandatory in one form and merely accurate in
            // the other - which is not a reason to start withholding it.
            xml += "      <spf><domain></domain><scope>mfrom</scope><result>none</result></spf>\r\n";
         }

         xml += "    </auth_results>\r\n";
         xml += "  </record>\r\n";
      }

      xml += "</feedback>\r\n";

      return xml;
   }

   AnsiString
   DmarcRptReporterTask::NormalizeDisposition_(const AnsiString &value)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 7489 Appendix C types p and sp as DispositionType - a closed, lowercase
   // enumeration of none/quarantine/reject. The value here comes from somebody
   // else's DNS record, so it arrives in whatever case they typed and is not
   // guaranteed to be one of the three at all: a record with no p= tag at all
   // reaches the store, and "p=None" is common in the wild.
   //
   // Emitted verbatim, either produced an empty <p></p> or a value outside the
   // enumeration, and a report that fails schema validation is discarded whole by
   // every receiver that validates - so the feature looked like it was working
   // (the mail was sent, the log said so) while the reports were being thrown
   // away at the far end.
   //
   // Anything unrecognised becomes "none", which is the schema's own default and
   // the weakest claim: it says this server made no assertion about what the
   // domain asked for, rather than inventing a stricter one.
   //---------------------------------------------------------------------------()
   {
      AnsiString normalized = value;
      normalized.Trim();
      normalized.MakeLower();

      if (normalized == "quarantine" || normalized == "reject" || normalized == "none")
         return normalized;

      return "none";
   }

   AnsiString
   DmarcRptReporterTask::NormalizeAlignment_(const AnsiString &value)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The same for adkim and aspf, which the schema types as AlignmentType: "r" or
   // "s" and nothing else. "relaxed"/"strict" spelled out, or an empty tag when
   // the record omitted it, both fail validation. Relaxed is the RFC's default
   // for both, so that is what an unrecognised value becomes.
   //---------------------------------------------------------------------------()
   {
      AnsiString normalized = value;
      normalized.Trim();
      normalized.MakeLower();

      if (normalized == "s" || normalized == "strict")
         return "s";

      return "r";
   }

   AnsiString
   DmarcRptReporterTask::NormalizeTesting_(const AnsiString &value)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 9990 Appendix A types policy_published/testing as TestingType: "y" or "n"
   // and nothing else. The value is whatever the domain owner typed after t=, so
   // "Y", "yes" and a t= tag that was never published all arrive here.
   //
   // Only an explicit yes is reported as yes. RFC 9989 5.5.6 makes n the default,
   // so an absent or unreadable tag is n - and erring that way is the safe
   // direction to err: reporting "this domain is only testing" about a domain that
   // is enforcing would tell its owner that failures they are seeing are harmless.
   //---------------------------------------------------------------------------()
   {
      AnsiString normalized = value;
      normalized.Trim();
      normalized.MakeLower();

      if (normalized == "y" || normalized == "yes")
         return "y";

      return "n";
   }

   AnsiString
   DmarcRptReporterTask::NormalizeDiscoveryMethod_(const AnsiString &value)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 9990 Appendix A types discovery_method as DiscoveryType: "psl" or
   // "treewalk". Anything else - including the empty string a bucket recorded
   // before this field existed carries - returns empty, and the caller then omits
   // the element, which the schema allows (minOccurs="0").
   //
   // Omitting beats guessing here in a way it does not for adkim or aspf: those
   // have RFC-defined defaults, so an explicit "r" restates the spec. Discovery
   // has no default, and naming a mechanism this server cannot confirm was used
   // would be inventing an answer to the one question this element exists to ask.
   //---------------------------------------------------------------------------()
   {
      AnsiString normalized = value;
      normalized.Trim();
      normalized.MakeLower();

      if (normalized == "psl" || normalized == "treewalk")
         return normalized;

      return "";
   }

   AnsiString
   DmarcRptReporterTask::ActionDisposition_(const AnsiString &disposition, bool dkimAligned, bool spfAligned)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 9990 Appendix A widens policy_evaluated/disposition from 7489's
   // DispositionType to ActionDispositionType, whose extra member is "pass": "No
   // action, passing DMARC w/enforcing policy".
   //
   // 7489 could not express that. It reused "none" both for a message that PASSED
   // DMARC and for one that FAILED under p=none, and a report consumer had to
   // infer which by reading the dkim and spf sub-elements - so the headline number
   // in every 7489 report, "how many messages got disposition none", conflates a
   // domain's own legitimate mail with the forgeries its policy is not yet strict
   // enough to stop.
   //
   // This server can tell them apart without recording anything new, because the
   // two aligned verdicts are already in the row and DMARC passes exactly when one
   // of them passed (RFC 9989 4.4). Derived rather than stored deliberately: a
   // second stored field could disagree with the verdicts beside it, and this
   // cannot.
   //
   // A quarantine or reject disposition is never rewritten - by definition an
   // action was taken - and a failure under p=none stays "none", which is what it
   // has always meant when the alignment verdicts beside it are both fail.
   //---------------------------------------------------------------------------()
   {
      // Normalized on the way out for the same reason p and sp are: the enumeration
      // is closed, and an unexpected value fails the whole document at a validating
      // receiver rather than just that record.
      const AnsiString normalized = NormalizeDisposition_(disposition);

      if (normalized != "none")
         return normalized;

      if (dkimAligned || spfAligned)
         return "pass";

      return "none";
   }

   AnsiString
   DmarcRptReporterTask::XmlEscape_(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength() + 8);

      for (int i = 0; i < value.GetLength(); i++)
      {
         char character = value[i];

         switch (character)
         {
         case '&':
            result += "&amp;";
            break;
         case '<':
            result += "&lt;";
            break;
         case '>':
            result += "&gt;";
            break;
         case '\"':
            result += "&quot;";
            break;
         case '\'':
            result += "&apos;";
            break;
         default:
            if (static_cast<unsigned char>(character) >= 0x20)
               result += character;
            break;
         }
      }

      return result;
   }
}
