// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// See DmarcRptReporterTask.h.

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

      AnsiString reportId;
      reportId.Format("%hs.%I64d@%hs", dayKey.c_str(), static_cast<__int64>(time(nullptr)), AnsiString(submitter).c_str());

      AnsiString reportXml = BuildReportXml(dayKey, bucket, reportId, fromAddress,
         IniFileSettings::Instance()->GetDmarcRptOrganizationName());

      AnsiString narrowDomain = bucket.policy_domain;
      AnsiString narrowSubmitter = submitter;

      AnsiString boundary;
      boundary.Format("dmarcrpt-%I64d", static_cast<__int64>(time(nullptr)));

      // receiver "!" policy-domain "!" begin "!" end "." extension
      // (RFC 7489 7.2.1.1). Plain .xml rather than .xml.gz: this server links
      // no compression library, and the RFC's gzip is a SHOULD.
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
      attachmentName.Format("%hs!%hs!%I64d!%I64d.xml", narrowSubmitter.c_str(), narrowDomain.c_str(), rangeBegin, rangeEnd);

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
                                        const AnsiString &organizationName)
   {
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
      xml += "<feedback>\r\n";
      xml += "  <report_metadata>\r\n";
      xml += "    <org_name>" + XmlEscape_(organizationName) + "</org_name>\r\n";
      xml += "    <email>" + XmlEscape_(AnsiString(contactEmail)) + "</email>\r\n";
      xml += "    <report_id>" + XmlEscape_(reportId) + "</report_id>\r\n";

      AnsiString range;
      range.Format("    <date_range><begin>%I64d</begin><end>%I64d</end></date_range>\r\n", rangeBegin, rangeEnd);
      xml += range;

      xml += "  </report_metadata>\r\n";
      xml += "  <policy_published>\r\n";
      xml += "    <domain>" + XmlEscape_(bucket.policy_domain) + "</domain>\r\n";
      xml += "    <adkim>" + XmlEscape_(bucket.adkim.IsEmpty() ? AnsiString("r") : bucket.adkim) + "</adkim>\r\n";
      xml += "    <aspf>" + XmlEscape_(bucket.aspf.IsEmpty() ? AnsiString("r") : bucket.aspf) + "</aspf>\r\n";
      xml += "    <p>" + XmlEscape_(bucket.p) + "</p>\r\n";

      if (!bucket.sp.IsEmpty())
         xml += "    <sp>" + XmlEscape_(bucket.sp) + "</sp>\r\n";

      AnsiString pct;
      pct.Format("    <pct>%d</pct>\r\n", bucket.pct);
      xml += pct;

      xml += "  </policy_published>\r\n";

      for (const DmarcRptStore::Row &row : bucket.rows)
      {
         xml += "  <record>\r\n";
         xml += "    <row>\r\n";
         xml += "      <source_ip>" + XmlEscape_(row.source_ip) + "</source_ip>\r\n";

         AnsiString count;
         count.Format("      <count>%d</count>\r\n", row.count);
         xml += count;

         xml += "      <policy_evaluated>\r\n";
         xml += "        <disposition>" + XmlEscape_(row.disposition) + "</disposition>\r\n";
         xml += "        <dkim>" + XmlEscape_(row.dkim) + "</dkim>\r\n";
         xml += "        <spf>" + XmlEscape_(row.spf) + "</spf>\r\n";
         xml += "      </policy_evaluated>\r\n";
         xml += "    </row>\r\n";
         xml += "    <identifiers>\r\n";
         xml += "      <header_from>" + XmlEscape_(row.header_from) + "</header_from>\r\n";
         xml += "    </identifiers>\r\n";
         xml += "    <auth_results>\r\n";

         for (const AnsiString &dkimDomain : row.dkim_passing_domains)
         {
            xml += "      <dkim><domain>" + XmlEscape_(dkimDomain) + "</domain><result>pass</result></dkim>\r\n";
         }

         // The raw SPF result, distinct from the ALIGNED verdict in
         // policy_evaluated: SPF can pass for an envelope domain that does not
         // align with the From header, and the report schema keeps the two
         // apart for exactly that case.
         if (!row.envelope_from_domain.IsEmpty())
         {
            xml += "      <spf><domain>" + XmlEscape_(row.envelope_from_domain) + "</domain><result>";
            xml += row.spf_passed ? "pass" : "fail";
            xml += "</result></spf>\r\n";
         }
         else
         {
            // The schema requires at least one auth_results element; a message
            // with no envelope sender (a bounce) evaluated SPF as none.
            xml += "      <spf><domain></domain><result>none</result></spf>\r\n";
         }

         xml += "    </auth_results>\r\n";
         xml += "  </record>\r\n";
      }

      xml += "</feedback>\r\n";

      return xml;
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
