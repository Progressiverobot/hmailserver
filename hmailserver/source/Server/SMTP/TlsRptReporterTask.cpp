// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// See TlsRptReporterTask.h.

#include "StdAfx.h"

#include "TlsRptReporterTask.h"
#include "RecipientParser.h"

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
   TlsRptReporterTask::TlsRptReporterTask()
   {
      // TlsRptFromAddress is empty in the shipped configuration, and with no
      // sender address DoWork pops every completed day bucket and discards it
      // unsent. Not sending by default is the right default - a mail server
      // should not start mailing third parties about its own TLS failures
      // because someone enabled MTA-STS - but it is completely invisible:
      // statistics accumulate, the task runs hourly, nothing is ever sent, and
      // an administrator who has published a _smtp._tls record and is waiting
      // for reports has nothing to look at.
      //
      // Said here rather than in DoWork, and that is the whole point: the task
      // is constructed exactly once, by Application::CreateScheduledTasks_, so
      // this is one line per service start. DoWork runs every hour, and a
      // notice there would be a notice per scheduled run for the lifetime of
      // the server.
      //
      // LOG_APPLICATION and deliberately not ErrorManager: this describes the
      // default configuration, so an error entry would appear in the ERROR log
      // of every stock install and report a correctly working server as broken.
      // The same reasoning as WebServicesServer::ReportUnreachableFeatures.
      if (IniFileSettings::Instance()->GetTlsRptFromAddress().IsEmpty())
      {
         // Kept short enough to survive the MaxLogLineLen truncation that the
         // logger applies to long lines: a diagnostic that names a setting is
         // useless if the setting's name is what gets cut off.
         LOG_APPLICATION(_T("TLSRPT: SMTP TLS reporting (RFC 8460) is collecting statistics but will never send a report: TlsRptFromAddress is empty in [Settings] of hMailServer.ini, which is the default, so each completed day of data is discarded unsent. Set TlsRptFromAddress to a mailbox here to start sending reports to the domains that request them with a _smtp._tls rua= record."));
      }
   }

   TlsRptReporterTask::~TlsRptReporterTask()
   {

   }

   void
   TlsRptReporterTask::DoWork()
   {
      SendReportsNow(false);
   }

   int
   TlsRptReporterTask::SendReportsNow(bool includeCurrentDay)
   {
      // Collect the day buckets that are complete (yesterday and older).
      std::vector<AnsiString> days = TlsRptStore::Instance()->GetCompletedDays();

      // Today's bucket is deliberately absent from GetCompletedDays - a day is
      // reported once it can no longer grow. The on-demand caller asks for it
      // anyway: an administrator verifying their setup has sessions from today
      // and none from yesterday, and RFC 8460 allows a day to be covered by
      // more than one report.
      if (includeCurrentDay)
         days.push_back(TlsRptStore::GetCurrentDayKey());

      if (days.empty())
         return 0;

      String fromAddress = IniFileSettings::Instance()->GetTlsRptFromAddress();

      int reportsSent = 0;

      for (const AnsiString &dayKey : days)
      {
         std::map<String, TlsRptStore::DomainBucket> domains = TlsRptStore::Instance()->PopDay(dayKey);

         // Without a configured sender address, the data is discarded -
         // reporting is disabled but memory must not grow.
         if (fromAddress.IsEmpty())
            continue;

         for (auto iter = domains.begin(); iter != domains.end(); ++iter)
         {
            if (SendReportForDomain_(dayKey, iter->first, iter->second))
               reportsSent++;
         }
      }

      return reportsSent;
   }

   bool
   TlsRptReporterTask::ParseTlsRptRecord(const AnsiString &record, std::vector<String> &addresses)
   {
      AnsiString narrow = record;
      narrow.Trim();

      if (!narrow.StartsWith("v=TLSRPTv1"))
         return false;

      // Locate the rua= field and extract mailto: addresses.
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
               continue; // https reporting endpoints are not supported.

            AnsiString address = uri.Mid(7);

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
   TlsRptReporterTask::GetReportingAddresses_(const String &domain, std::vector<String> &addresses)
   {
      DNSResolver resolver;

      std::vector<String> txtRecords;
      if (!resolver.GetTXTRecords("_smtp._tls." + domain, txtRecords))
         return false;

      for (const String &record : txtRecords)
      {
         if (ParseTlsRptRecord(AnsiString(record), addresses))
            return !addresses.empty();
      }

      return false;
   }

   bool
   TlsRptReporterTask::SendReportForDomain_(const AnsiString &dayKey, const String &domain, const TlsRptStore::DomainBucket &bucket)
   {
      std::vector<String> reportingAddresses;
      if (!GetReportingAddresses_(domain, reportingAddresses))
         return false; // Domain does not request TLS reports.

      String fromAddress = IniFileSettings::Instance()->GetTlsRptFromAddress();
      String submitter = StringParser::ExtractDomain(fromAddress);

      AnsiString reportId;
      reportId.Format("%hs.%I64d@%hs", dayKey.c_str(), static_cast<__int64>(time(nullptr)), AnsiString(submitter).c_str());

      AnsiString reportJson = BuildReportJson(dayKey, domain, bucket, reportId, fromAddress,
         IniFileSettings::Instance()->GetTlsRptOrganizationName());

      // Build the report mail (multipart/report, RFC 8460 section 5.3).
      AnsiString narrowDomain = domain;
      AnsiString narrowSubmitter = submitter;

      AnsiString boundary;
      boundary.Format("tlsrpt-%I64d", static_cast<__int64>(time(nullptr)));

      AnsiString attachmentName;
      attachmentName.Format("%hs!%hs!%hs.json", narrowSubmitter.c_str(), narrowDomain.c_str(), dayKey.c_str());

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
      mailContent += "TLS-Report-Domain: " + narrowDomain + "\r\n";
      mailContent += "TLS-Report-Submitter: " + narrowSubmitter + "\r\n";
      mailContent += "MIME-Version: 1.0\r\n";
      mailContent += "Content-Type: multipart/report; report-type=\"tlsrpt\"; boundary=\"" + boundary + "\"\r\n";
      mailContent += "\r\n";
      mailContent += "--" + boundary + "\r\n";
      mailContent += "Content-Type: text/plain; charset=\"us-ascii\"\r\n";
      mailContent += "\r\n";
      mailContent += "This is an aggregate TLS report for " + narrowDomain + " from " + narrowSubmitter + ".\r\n";
      mailContent += "\r\n";
      mailContent += "--" + boundary + "\r\n";
      mailContent += "Content-Type: application/tlsrpt+json\r\n";
      mailContent += "Content-Disposition: attachment; filename=\"" + attachmentName + "\"\r\n";
      mailContent += "\r\n";
      mailContent += reportJson;
      mailContent += "\r\n";
      mailContent += "--" + boundary + "--\r\n";

      // Create and submit the message.
      std::shared_ptr<Message> reportMessage = std::shared_ptr<Message>(new Message());
      reportMessage->SetState(Message::Delivering);
      reportMessage->SetFromAddress(fromAddress);

      const String fileName = PersistentMessage::GetFileName(reportMessage);

      if (!FileUtilities::WriteToFile(fileName, mailContent))
      {
         LOG_APPLICATION("TLSRPT: Failed to write report message file for domain " + domain + ".");
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

      // Unchecked, and the log line below ran either way - so a failed save wrote
      // "Sent aggregate TLS report for <domain>" for a report that was never queued,
      // and left its file behind. The report is the evidence a recipient domain uses
      // to see that its own TLS policy is working, so silently not sending one while
      // recording that it was sent is the wrong way round for a reporting feature.
      if (!PersistentMessage::SaveObject(reportMessage))
      {
         FileUtilities::DeleteFile(fileName);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6111, "TlsRptReporterTask::SendReportForDomain_",
            "TLSRPT: the aggregate TLS report for " + domain + " could not be saved and has NOT been sent.");

         return false;
      }

      Application::Instance()->SubmitPendingEmail();

      LOG_APPLICATION("TLSRPT: Sent aggregate TLS report for " + domain + " covering " + String(dayKey) + ".");

      return true;
   }

   AnsiString
   TlsRptReporterTask::BuildReportJson(const AnsiString &dayKey, const String &domain,
                                       const TlsRptStore::DomainBucket &bucket, const AnsiString &reportId,
                                       const String &contactInfo, const AnsiString &organizationName)
   {
      AnsiString narrowDomain = domain;

      int totalFailures = 0;
      for (const TlsRptStore::FailureDetail &failure : bucket.failures)
         totalFailures += failure.count;

      AnsiString json;
      json += "{";
      json += "\"organization-name\":\"" + JsonEscape_(organizationName) + "\",";
      json += "\"date-range\":{\"start-datetime\":\"" + dayKey + "T00:00:00Z\",\"end-datetime\":\"" + dayKey + "T23:59:59Z\"},";
      json += "\"contact-info\":\"" + JsonEscape_(AnsiString(contactInfo)) + "\",";
      json += "\"report-id\":\"" + JsonEscape_(reportId) + "\",";
      json += "\"policies\":[{";
      json += "\"policy\":{";
      json += "\"policy-type\":\"" + JsonEscape_(bucket.policy_type) + "\",";

      if (!bucket.policy_string.IsEmpty())
         json += "\"policy-string\":[\"" + JsonEscape_(bucket.policy_string) + "\"],";

      json += "\"policy-domain\":\"" + JsonEscape_(narrowDomain) + "\"";
      json += "},";
      json += "\"summary\":{";

      AnsiString counts;
      counts.Format("\"total-successful-session-count\":%d,\"total-failure-session-count\":%d",
         bucket.successful_sessions, totalFailures);
      json += counts;
      json += "}";

      if (!bucket.failures.empty())
      {
         json += ",\"failure-details\":[";

         bool first = true;
         for (const TlsRptStore::FailureDetail &failure : bucket.failures)
         {
            if (!first)
               json += ",";
            first = false;

            json += "{\"result-type\":\"" + JsonEscape_(failure.result_type) + "\"";

            if (!failure.receiving_mx.IsEmpty())
               json += ",\"receiving-mx-hostname\":\"" + JsonEscape_(failure.receiving_mx) + "\"";

            AnsiString failureCount;
            failureCount.Format(",\"failed-session-count\":%d}", failure.count);
            json += failureCount;
         }

         json += "]";
      }

      json += "}]";
      json += "}";

      return json;
   }

   AnsiString
   TlsRptReporterTask::JsonEscape_(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength() + 8);

      for (int i = 0; i < value.GetLength(); i++)
      {
         char character = value[i];

         switch (character)
         {
         case '\"':
            result += "\\\"";
            break;
         case '\\':
            result += "\\\\";
            break;
         case '\r':
         case '\n':
         case '\t':
            result += " ";
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
