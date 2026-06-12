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

   }

   TlsRptReporterTask::~TlsRptReporterTask()
   {

   }

   void
   TlsRptReporterTask::DoWork()
   {
      // Collect the day buckets that are complete (yesterday and older).
      std::vector<AnsiString> completedDays = TlsRptStore::Instance()->GetCompletedDays();
      if (completedDays.empty())
         return;

      String fromAddress = IniFileSettings::Instance()->GetTlsRptFromAddress();

      for (const AnsiString &dayKey : completedDays)
      {
         std::map<String, TlsRptStore::DomainBucket> domains = TlsRptStore::Instance()->PopDay(dayKey);

         // Without a configured sender address, the data is discarded -
         // reporting is disabled but memory must not grow.
         if (fromAddress.IsEmpty())
            continue;

         for (auto iter = domains.begin(); iter != domains.end(); ++iter)
         {
            SendReportForDomain_(dayKey, iter->first, iter->second);
         }
      }
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
         AnsiString narrow = record;
         narrow.Trim();

         if (!narrow.StartsWith("v=TLSRPTv1"))
            continue;

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

         return !addresses.empty();
      }

      return false;
   }

   void
   TlsRptReporterTask::SendReportForDomain_(const AnsiString &dayKey, const String &domain, const TlsRptStore::DomainBucket &bucket)
   {
      std::vector<String> reportingAddresses;
      if (!GetReportingAddresses_(domain, reportingAddresses))
         return; // Domain does not request TLS reports.

      String fromAddress = IniFileSettings::Instance()->GetTlsRptFromAddress();
      String submitter = StringParser::ExtractDomain(fromAddress);

      AnsiString reportId;
      reportId.Format("%hs.%I64d@%hs", dayKey.c_str(), static_cast<__int64>(time(nullptr)), AnsiString(submitter).c_str());

      AnsiString reportJson = BuildReportJson_(dayKey, domain, bucket, reportId, fromAddress);

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
         return;
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
         return;
      }

      PersistentMessage::SaveObject(reportMessage);
      Application::Instance()->SubmitPendingEmail();

      LOG_APPLICATION("TLSRPT: Sent aggregate TLS report for " + domain + " covering " + String(dayKey) + ".");
   }

   AnsiString
   TlsRptReporterTask::BuildReportJson_(const AnsiString &dayKey, const String &domain,
                                        const TlsRptStore::DomainBucket &bucket, const AnsiString &reportId,
                                        const String &contactInfo)
   {
      AnsiString narrowDomain = domain;
      AnsiString organizationName = IniFileSettings::Instance()->GetTlsRptOrganizationName();

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
