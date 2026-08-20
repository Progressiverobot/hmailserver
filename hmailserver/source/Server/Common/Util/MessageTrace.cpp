// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include "MessageTrace.h"

#include "../Application/Application.h"
#include "../Application/IniFileSettings.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "Time.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const wchar_t *MessageTrace::EventAccepted = L"accepted";
   const wchar_t *MessageTrace::EventDelivered = L"delivered";
   const wchar_t *MessageTrace::EventFailed = L"failed";
   const wchar_t *MessageTrace::EventQuarantined = L"quarantined";

   bool
   MessageTrace::GetEnabled()
   {
      return IniFileSettings::Instance()->GetMessageTraceEnabled();
   }

   void
   MessageTrace::Record(__int64 queueID, const String &eventName, const String &sender,
                        const String &recipient, const String &sourceIP, int statusCode,
                        const String &detail)
   {
      try
      {
         if (!GetEnabled())
            return;

         SQLStatement statement;
         statement.SetTable("hm_messagetrace");
         statement.AddColumnInt64("mtqueueid", queueID);
         statement.AddColumnDate("mtoccurred", Time::GetDateFromSystemDate(Time::GetCurrentDateTime()));
         statement.AddColumn("mtevent", eventName.Mid(0, 32));
         statement.AddColumn("mtsender", sender.Mid(0, 255));
         statement.AddColumn("mtrecipient", recipient.Mid(0, 255));
         statement.AddColumn("mtsourceip", sourceIP.Mid(0, 64));
         statement.AddColumnInt64("mtstatuscode", statusCode);
         statement.AddColumn("mtdetail", detail.Mid(0, 255));
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetIdentityColumn("mtid");

         __int64 dbid = 0;

         Application::Instance()->GetDBManager()->Execute(statement, &dbid);
      }
      catch (...)
      {
         // Swallowed on purpose, and not even reported: this runs on the delivery
         // path, and a trace that can interrupt a delivery is worse than no trace -
         // the failure would be invisible in precisely the tool built to make
         // deliveries visible. A missing row is a gap in a diagnostic; a thrown
         // exception here is undelivered mail.
      }
   }

   bool
   MessageTrace::ReadRow_(std::shared_ptr<DALRecordset> recordset, MessageTraceEvent &out_event)
   {
      if (!recordset || recordset->IsEOF())
         return false;

      out_event.id = recordset->GetInt64Value("mtid");
      out_event.queue_id = recordset->GetInt64Value("mtqueueid");
      out_event.occurred = recordset->GetStringValue("mtoccurred");
      out_event.event_name = recordset->GetStringValue("mtevent");
      out_event.sender = recordset->GetStringValue("mtsender");
      out_event.recipient = recordset->GetStringValue("mtrecipient");
      out_event.source_ip = recordset->GetStringValue("mtsourceip");
      out_event.status_code = (int) recordset->GetLongValue("mtstatuscode");
      out_event.detail = recordset->GetStringValue("mtdetail");

      return true;
   }

   std::vector<MessageTraceEvent>
   MessageTrace::Search(const String &address, int maxCount)
   {
      std::vector<MessageTraceEvent> result;

      // Ordered by mtid rather than mtoccurred: the timestamp has one-second
      // resolution and a message's accepted/delivered pair frequently falls inside
      // one second, so ordering by it would show the story out of order. The
      // identity column is monotonic by construction.
      SQLCommand command(address.IsEmpty()
         ? "select * from hm_messagetrace order by mtid desc"
         : "select * from hm_messagetrace where mtsender like @ADDRESS or mtrecipient like @ADDRESS order by mtid desc");

      if (!address.IsEmpty())
         command.AddParameter("@ADDRESS", "%" + address + "%");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return result;

      while (!recordset->IsEOF() && (int) result.size() < maxCount)
      {
         MessageTraceEvent traceEvent;

         if (ReadRow_(recordset, traceEvent))
            result.push_back(traceEvent);

         recordset->MoveNext();
      }

      return result;
   }

   std::vector<MessageTraceEvent>
   MessageTrace::GetByQueueID(__int64 queueID)
   {
      std::vector<MessageTraceEvent> result;

      // Oldest first here, because this is the one query that reads as a narrative:
      // accepted, then delivered or failed, in the order it happened.
      SQLCommand command("select * from hm_messagetrace where mtqueueid = @QUEUEID order by mtid asc");
      command.AddParameter("@QUEUEID", queueID);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return result;

      while (!recordset->IsEOF())
      {
         MessageTraceEvent traceEvent;

         if (ReadRow_(recordset, traceEvent))
            result.push_back(traceEvent);

         recordset->MoveNext();
      }

      return result;
   }

   int
   MessageTrace::GetCount()
   {
      SQLCommand command("select count(*) as mtcount from hm_messagetrace");

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!recordset)
         return 0;

      return (int) recordset->GetLongValue("mtcount");
   }

   int
   MessageTrace::DeleteExpired()
   {
      int retentionDays = IniFileSettings::Instance()->GetMessageTraceRetentionDays();

      if (retentionDays <= 0)
         return 0;

      std::time_t cutoffTime = std::time(0) - (static_cast<std::time_t>(retentionDays) * 24 * 60 * 60);

      struct tm cutoffTm;

      if (localtime_s(&cutoffTm, &cutoffTime) != 0)
         return 0;

      char cutoffText[32];

      if (strftime(cutoffText, sizeof(cutoffText), "%Y-%m-%d %H:%M:%S", &cutoffTm) == 0)
         return 0;

      // Counted first so the caller can report what happened, then deleted in one
      // statement - unlike the quarantine, no row here owns a file, so there is
      // nothing a set-based delete could strand.
      SQLCommand countCommand("select count(*) as mtcount from hm_messagetrace where mtoccurred < @CUTOFF");
      countCommand.AddParameter("@CUTOFF", String(cutoffText));

      std::shared_ptr<DALRecordset> recordset =
         Application::Instance()->GetDBManager()->OpenRecordset(countCommand);

      int expired = recordset ? (int) recordset->GetLongValue("mtcount") : 0;

      if (expired == 0)
         return 0;

      SQLCommand deleteCommand("delete from hm_messagetrace where mtoccurred < @CUTOFF");
      deleteCommand.AddParameter("@CUTOFF", String(cutoffText));

      if (!Application::Instance()->GetDBManager()->Execute(deleteCommand))
         return 0;

      return expired;
   }
}
