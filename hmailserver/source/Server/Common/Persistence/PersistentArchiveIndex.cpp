// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "PersistentArchiveIndex.h"

#include "../SQL/SQLStatement.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "../Application/Application.h"

namespace HM
{
   namespace
   {
      // A search term is used inside a LIKE, so its own wildcards are literal.
      String Contains(const String &term)
      {
         String escaped = term;
         escaped.Replace(_T("%"), _T("[%]"));
         escaped.Replace(_T("_"), _T("[_]"));
         return _T("%") + escaped + _T("%");
      }

      String Truncate(const String &value, int maxLength)
      {
         return value.GetLength() > maxLength ? value.Mid(0, maxLength) : value;
      }

      AnsiString JsonEscape(const AnsiString &value)
      {
         AnsiString result;
         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value[i];
            switch (c)
            {
            case '\"': result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
               if (static_cast<unsigned char>(c) >= 0x20)
                  result += c;
               break;
            }
         }
         return result;
      }
   }

   __int64
   PersistentArchiveIndex::Record(int direction, const String &domain, const String &mailbox, const String &sender,
                                  const String &recipients, const String &subject, const String &messageId,
                                  const String &path, __int64 size)
   {
      SQLStatement statement;
      statement.SetStatementType(SQLStatement::STInsert);
      statement.SetTable("hm_archiveindex");
      statement.SetIdentityColumn("archiveid");
      statement.AddColumnCommand("archivetime", SQLStatement::GetCurrentTimestamp());
      statement.AddColumn("archivedomain", Truncate(domain, 255).ToLower());
      statement.AddColumn("archivemailbox", Truncate(mailbox, 255).ToLower());
      statement.AddColumn("archivedirection", (long) direction);
      statement.AddColumn("archivesender", Truncate(sender, 255));
      statement.AddColumn("archiverecipients", Truncate(recipients, 1000));
      statement.AddColumn("archivesubject", Truncate(subject, 255));
      statement.AddColumn("archivemessageid", Truncate(messageId, 255));
      statement.AddColumn("archivepath", Truncate(path, 1000));
      statement.AddColumnInt64("archivesize", size);
      statement.AddColumn("archivehold", (long) 0);

      __int64 id = 0;
      if (!Application::Instance()->GetDBManager()->Execute(statement, &id))
         return 0;
      return id;
   }

   bool
   PersistentArchiveIndex::Read_(std::shared_ptr<DALRecordset> recordset, Entry &entry)
   {
      entry.id = recordset->GetInt64Value("archiveid");
      entry.time = recordset->GetStringValue("archivetime");
      entry.domain = recordset->GetStringValue("archivedomain");
      entry.mailbox = recordset->GetStringValue("archivemailbox");
      entry.direction = recordset->GetLongValue("archivedirection");
      entry.sender = recordset->GetStringValue("archivesender");
      entry.recipients = recordset->GetStringValue("archiverecipients");
      entry.subject = recordset->GetStringValue("archivesubject");
      entry.messageId = recordset->GetStringValue("archivemessageid");
      entry.path = recordset->GetStringValue("archivepath");
      entry.size = recordset->GetInt64Value("archivesize");
      entry.hold = recordset->GetLongValue("archivehold") != 0;
      return true;
   }

   bool
   PersistentArchiveIndex::Search(const Criteria &criteria, std::vector<Entry> &entries)
   {
      entries.clear();

      int maxRows = criteria.maxRows;
      if (maxRows < 1)
         maxRows = 1;
      if (maxRows > 1000)
         maxRows = 1000;

      // Every user-supplied value goes in as a parameter; the SQL text holds
      // only column names and the shape of the query.
      String sql = _T("select archiveid, archivetime, archivedomain, archivemailbox, archivedirection, archivesender, ")
                   _T("archiverecipients, archivesubject, archivemessageid, archivepath, archivesize, archivehold ")
                   _T("from hm_archiveindex where 1 = 1");
      SQLCommand command;

      if (!criteria.domain.IsEmpty())
         sql += _T(" and archivedomain = @DOMAIN");
      if (!criteria.mailbox.IsEmpty())
         sql += _T(" and archivemailbox = @MAILBOX");
      if (!criteria.sender.IsEmpty())
         sql += _T(" and archivesender like @SENDER");
      if (!criteria.recipient.IsEmpty())
         sql += _T(" and archiverecipients like @RECIPIENT");
      if (!criteria.subject.IsEmpty())
         sql += _T(" and archivesubject like @SUBJECT");
      if (!criteria.since.IsEmpty())
         sql += _T(" and archivetime >= @SINCE");
      if (!criteria.until.IsEmpty())
         sql += _T(" and archivetime <= @UNTIL");
      if (criteria.holdOnly)
         sql += _T(" and archivehold = 1");
      sql += _T(" order by archivetime desc, archiveid desc");

      command.SetQueryString(sql);
      if (!criteria.domain.IsEmpty())
         command.AddParameter("@DOMAIN", String(criteria.domain).ToLower());
      if (!criteria.mailbox.IsEmpty())
         command.AddParameter("@MAILBOX", String(criteria.mailbox).ToLower());
      if (!criteria.sender.IsEmpty())
         command.AddParameter("@SENDER", Contains(criteria.sender));
      if (!criteria.recipient.IsEmpty())
         command.AddParameter("@RECIPIENT", Contains(criteria.recipient));
      if (!criteria.subject.IsEmpty())
         command.AddParameter("@SUBJECT", Contains(criteria.subject));
      if (!criteria.since.IsEmpty())
         command.AddParameter("@SINCE", criteria.since);
      if (!criteria.until.IsEmpty())
         command.AddParameter("@UNTIL", criteria.until);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return false;

      while (!recordset->IsEOF() && (int) entries.size() < maxRows)
      {
         Entry entry;
         Read_(recordset, entry);
         entries.push_back(entry);
         recordset->MoveNext();
      }
      return true;
   }

   bool
   PersistentArchiveIndex::Get(__int64 id, Entry &entry)
   {
      SQLCommand command(_T("select archiveid, archivetime, archivedomain, archivemailbox, archivedirection, archivesender, ")
                         _T("archiverecipients, archivesubject, archivemessageid, archivepath, archivesize, archivehold ")
                         _T("from hm_archiveindex where archiveid = @ID"));
      command.AddParameter("@ID", id);
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return false;
      return Read_(recordset, entry);
   }

   bool
   PersistentArchiveIndex::SetHold(__int64 id, bool hold)
   {
      SQLCommand command(_T("update hm_archiveindex set archivehold = @HOLD where archiveid = @ID"));
      command.AddParameter("@HOLD", hold ? 1 : 0);
      command.AddParameter("@ID", id);
      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   PersistentArchiveIndex::IsHeld(const String &path)
   {
      SQLCommand command(_T("select archivehold from hm_archiveindex where archivepath = @PATH and archivehold = 1"));
      command.AddParameter("@PATH", Truncate(path, 1000));
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      return recordset && !recordset->IsEOF();
   }

   bool
   PersistentArchiveIndex::RemoveByPath(const String &path)
   {
      SQLCommand command(_T("delete from hm_archiveindex where archivepath = @PATH"));
      command.AddParameter("@PATH", Truncate(path, 1000));
      return Application::Instance()->GetDBManager()->Execute(command);
   }

   int
   PersistentArchiveIndex::RemoveByAddress(const String &address)
   {
      // Rows whose sender is the address, or whose recipient list names it.
      // The count is what the eraser reports; rows for held copies are kept,
      // because a hold is a promise that nothing removes the record.
      String lowered = address;
      lowered.ToLower();

      SQLCommand count(_T("select count(*) as c from hm_archiveindex where archivehold = 0 and (archivesender = @ADDRESS or archiverecipients like @CONTAINS)"));
      count.AddParameter("@ADDRESS", lowered);
      count.AddParameter("@CONTAINS", Contains(lowered));
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(count);
      int rows = (recordset && !recordset->IsEOF()) ? recordset->GetLongValue("c") : 0;
      if (rows == 0)
         return 0;

      SQLCommand remove(_T("delete from hm_archiveindex where archivehold = 0 and (archivesender = @ADDRESS or archiverecipients like @CONTAINS)"));
      remove.AddParameter("@ADDRESS", lowered);
      remove.AddParameter("@CONTAINS", Contains(lowered));
      if (!Application::Instance()->GetDBManager()->Execute(remove))
         return 0;
      return rows;
   }

   AnsiString
   PersistentArchiveIndex::ToJson(const Entry &entry)
   {
      const char *direction = entry.direction == DirectionSent ? "sent"
                            : entry.direction == DirectionReceived ? "received" : "inbound";
      AnsiString json;
      json.Format("{\"id\":%I64d,\"time\":\"%hs\",\"domain\":\"%hs\",\"mailbox\":\"%hs\",\"direction\":\"%hs\",\"sender\":\"%hs\",",
         entry.id,
         JsonEscape(AnsiString(entry.time)).c_str(),
         JsonEscape(AnsiString(entry.domain)).c_str(),
         JsonEscape(AnsiString(entry.mailbox)).c_str(),
         direction,
         JsonEscape(AnsiString(entry.sender)).c_str());
      AnsiString rest;
      rest.Format("\"recipients\":\"%hs\",\"subject\":\"%hs\",\"message_id\":\"%hs\",\"path\":\"%hs\",\"size\":%I64d,\"hold\":%hs}",
         JsonEscape(AnsiString(entry.recipients)).c_str(),
         JsonEscape(AnsiString(entry.subject)).c_str(),
         JsonEscape(AnsiString(entry.messageId)).c_str(),
         JsonEscape(AnsiString(entry.path)).c_str(),
         entry.size,
         entry.hold ? "true" : "false");
      return json + rest;
   }

   AnsiString
   PersistentArchiveIndex::ToJson(const std::vector<Entry> &entries)
   {
      AnsiString json = "[";
      for (size_t i = 0; i < entries.size(); i++)
      {
         if (i > 0)
            json += ",";
         json += ToJson(entries[i]);
      }
      json += "]";
      return json;
   }
}
