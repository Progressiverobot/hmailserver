// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Later, for the signed-in account: a draft sent at a time (POST and DELETE
// /api/v1/me/drafts/{id}/schedule), a message snoozed until a time (POST
// /api/v1/me/messages/{id}/snooze - it waits in a Snoozed folder and comes
// back unread), what is scheduled (GET /api/v1/me/scheduled) and its cancelling
// (DELETE /api/v1/me/scheduled/{id}); the administrator's run-now
// (POST /api/v1/scheduled/run); and a folder exported as mbox (GET
// /api/v1/me/folders/{id}/export) or a message imported into one (POST
// /api/v1/me/folders/{id}/messages, the file as the body).

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "Time.h"
#include "FileUtilities.h"
#include "MessageUtilities.h"
#include "../BO/Account.h"
#include "../BO/Message.h"
#include "../BO/Messages.h"
#include "../BO/IMAPFolders.h"
#include "../BO/IMAPFolder.h"
#include "../Mime/Mime.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include "../Persistence/PersistentMessage.h"
#include "../Application/Application.h"
#include "../Application/ScheduledMailTask.h"
#include "../BO/ACLPermission.h"
#include "../Application/ACLManager.h"
#include "../Tracking/ChangeNotification.h"
#include "../Tracking/NotificationServer.h"
#include "../../IMAP/IMAPFolderContainer.h"
#include "../../IMAP/MessagesContainer.h"
#include <fstream>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const __int64 MaxExportBytes = 200 * 1024 * 1024;
      const size_t MaxImportBytes = 25 * 1024 * 1024;
      const int MaxScheduledPerAccount = 200;

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      // "YYYY-MM-DD HH:MM" or "YYYY-MM-DD HH:MM:SS" -> "YYYY-MM-DD HH:MM:SS",
      // or empty when it is not a time.
      String NormalizeTime(const String &given)
      {
         String text = given;
         text.TrimLeft();
         text.TrimRight();
         text.Replace(_T('T'), _T(' '));
         if (text.GetLength() == 16)
            text += _T(":00");
         if (text.GetLength() != 19)
            return String();
         static const wchar_t pattern[] = L"0000-00-00 00:00:00";
         for (int i = 0; i < 19; i++)
         {
            wchar_t c = text[i];
            if (pattern[i] == '0' ? !(c >= '0' && c <= '9') : c != pattern[i])
               return String();
         }
         int month = _wtoi(text.Mid(5, 2).c_str()), day = _wtoi(text.Mid(8, 2).c_str());
         int hour = _wtoi(text.Mid(11, 2).c_str()), minute = _wtoi(text.Mid(14, 2).c_str()), second = _wtoi(text.Mid(17, 2).c_str());
         if (month < 1 || month > 12 || day < 1 || day > 31 || hour > 23 || minute > 59 || second > 59)
            return String();
         return text;
      }

      // Now plus a year, in the same text, for the far bound.
      String AYearFromNow()
      {
         String now = Time::GetCurrentDateTime();
         int year = _wtoi(now.Mid(0, 4).c_str()) + 1;
         String text;
         text.Format(_T("%04d%s"), year, now.Mid(4).c_str());
         return text;
      }

      void RemoveRowsFor(__int64 accountId, __int64 messageId)
      {
         SQLCommand command("delete from hm_scheduled where schedaccountid = @ACCOUNTID and schedmessageid = @MESSAGEID");
         command.AddParameter("@ACCOUNTID", accountId);
         command.AddParameter("@MESSAGEID", messageId);
         Application::Instance()->GetDBManager()->Execute(command);
      }

      int CountRows(__int64 accountId)
      {
         SQLCommand command("select count(*) as schedcount from hm_scheduled where schedaccountid = @ACCOUNTID");
         command.AddParameter("@ACCOUNTID", accountId);
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         return (recordset && !recordset->IsEOF()) ? (int) recordset->GetLongValue("schedcount") : 0;
      }

      bool InsertRow(__int64 accountId, __int64 messageId, int action, const String &at, __int64 folderId, __int64 &schedId)
      {
         SQLStatement statement;
         statement.SetTable("hm_scheduled");
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetIdentityColumn("schedid");
         statement.AddColumnInt64("schedaccountid", accountId);
         statement.AddColumnInt64("schedmessageid", messageId);
         statement.AddColumnInt64("schedaction", action);
         statement.AddColumnDate("schedat", Time::GetDateFromSystemDate(at));
         statement.AddColumnInt64("schedfolderid", folderId);
         statement.AddColumnDate("schedcreated", Time::GetDateFromSystemDate(Time::GetCurrentDateTime()));
         schedId = 0;
         return Application::Instance()->GetDBManager()->Execute(statement, &schedId) && schedId > 0;
      }

      String SubjectOf(const String &fileName)
      {
         AnsiString header = PersistentMessage::LoadHeader(fileName, false);
         if (header.IsEmpty())
            return String();
         MimeHeader mimeHeader;
         mimeHeader.Load(header.c_str(), header.GetLength(), true);
         return mimeHeader.GetUnicodeFieldValue("Subject");
      }

      bool ReadWholeFile(const String &fileName, AnsiString &bytes)
      {
         std::ifstream in(fileName.c_str(), std::ios::binary);
         if (!in)
            return false;
         std::string contents((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
         bytes = AnsiString(contents);
         return true;
      }
   }

   HttpResponse
   RestApiServer::HandleMeDraftSchedule_(const Caller &caller, __int64 messageId, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> draft = FindOwnMessage_(account, messageId, folder);
      if (!draft)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");
      if (!draft->GetFlagDraft())
         return BuildResponse_(400, "{\"error\":\"only a draft can be sent later; save the message as a draft first\"}");

      String at = NormalizeTime(JsonUtf8Value_(requestBody, "send_at"));
      if (at.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"send_at is a time, YYYY-MM-DD HH:MM, in the server's local time\"}");
      if (at <= Time::GetCurrentDateTime())
         return BuildResponse_(400, "{\"error\":\"send_at is in the past\"}");
      if (at > AYearFromNow())
         return BuildResponse_(400, "{\"error\":\"send_at is more than a year away\"}");
      if (CountRows(account->GetID()) >= MaxScheduledPerAccount)
         return BuildResponse_(400, "{\"error\":\"at most 200 scheduled items\"}");

      RemoveRowsFor(account->GetID(), draft->GetID());
      __int64 schedId = 0;
      if (!InsertRow(account->GetID(), draft->GetID(), ScheduledMailTask::ActionSend, at, folder->GetID(), schedId))
         return BuildResponse_(500, "{\"error\":\"the schedule could not be saved\"}");

      return BuildResponse_(201, "{\"id\":" + Int64Text(schedId) + ",\"message_id\":" + Int64Text(draft->GetID()) + ",\"send_at\":\"" + JsonEscape_(Utf8_(at)) + "\"}");
   }

   HttpResponse
   RestApiServer::HandleMeDraftUnschedule_(const Caller &caller, __int64 messageId)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      SQLCommand command("select count(*) as schedcount from hm_scheduled where schedaccountid = @ACCOUNTID and schedmessageid = @MESSAGEID");
      command.AddParameter("@ACCOUNTID", account->GetID());
      command.AddParameter("@MESSAGEID", messageId);
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      int count = (recordset && !recordset->IsEOF()) ? (int) recordset->GetLongValue("schedcount") : 0;
      if (count == 0)
         return BuildResponse_(404, "{\"error\":\"nothing is scheduled for that message\"}");

      RemoveRowsFor(account->GetID(), messageId);
      return BuildResponse_(200, "{\"cancelled\":" + Int64Text(count) + "}");
   }

   // The message waits in a folder named Snoozed, made when the account has
   // none, and comes back to the folder it left, unread, at the time.
   HttpResponse
   RestApiServer::HandleMeMessageSnooze_(const Caller &caller, __int64 messageId, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> source;
      std::shared_ptr<Message> message = FindOwnMessage_(account, messageId, source);
      if (!message)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      String until = NormalizeTime(JsonUtf8Value_(requestBody, "until"));
      if (until.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"until is a time, YYYY-MM-DD HH:MM, in the server's local time\"}");
      if (until <= Time::GetCurrentDateTime())
         return BuildResponse_(400, "{\"error\":\"until is in the past\"}");
      if (until > AYearFromNow())
         return BuildResponse_(400, "{\"error\":\"until is more than a year away\"}");
      if (CountRows(account->GetID()) >= MaxScheduledPerAccount)
         return BuildResponse_(400, "{\"error\":\"at most 200 scheduled items\"}");

      std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!folders)
         return BuildResponse_(500, "{\"error\":\"the folders could not be read\"}");
      std::shared_ptr<IMAPFolder> snoozed = folders->GetFolderByName(_T("Snoozed"));
      if (!snoozed)
      {
         std::vector<String> path;
         path.push_back(_T("Snoozed"));
         folders->CreatePath(folders, path, true);
         snoozed = folders->GetFolderByName(_T("Snoozed"));
         if (!snoozed)
            return BuildResponse_(500, "{\"error\":\"the Snoozed folder could not be made\"}");
      }
      if (snoozed->GetID() == source->GetID())
         return BuildResponse_(400, "{\"error\":\"the message is snoozed already\"}");

      if (!RightOn_(account, source, ACLPermission::PermissionWriteDeleted) || !RightOn_(account, source, ACLPermission::PermissionExpunge))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to remove messages\"}");

      __int64 newMessageId = 0;
      if (!MessageUtilities::CopyToIMAPFolder(message, (int) snoozed->GetID(), newMessageId))
         return BuildResponse_(500, "{\"error\":\"the message could not be moved to the Snoozed folder\"}");
      if (!DeleteOwnMessage_(account, message, source))
         return BuildResponse_(500, "{\"error\":\"the message was copied to the Snoozed folder but the original could not be removed\"}");

      __int64 schedId = 0;
      if (!InsertRow(account->GetID(), newMessageId, ScheduledMailTask::ActionReturn, until, source->GetID(), schedId))
         return BuildResponse_(500, "{\"error\":\"the message is in the Snoozed folder but its return could not be saved\"}");

      return BuildResponse_(200, "{\"id\":" + Int64Text(schedId) + ",\"message_id\":" + Int64Text(newMessageId) + ",\"until\":\"" + JsonEscape_(Utf8_(until)) + "\",\"folder_id\":" + Int64Text(snoozed->GetID()) + "}");
   }

   HttpResponse
   RestApiServer::HandleMeScheduled_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      SQLCommand command("select schedid, schedmessageid, schedaction, schedat, schedfolderid from hm_scheduled where schedaccountid = @ACCOUNTID order by schedat");
      command.AddParameter("@ACCOUNTID", account->GetID());
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return BuildResponse_(500, "{\"error\":\"the schedule could not be read\"}");

      AnsiString json = "{\"scheduled\":[";
      bool first = true;
      while (!recordset->IsEOF())
      {
         __int64 messageId = recordset->GetInt64Value("schedmessageid");
         std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
         String subject;
         if (PersistentMessage::ReadObject(message, messageId) && message->GetID() != 0)
            subject = SubjectOf(MessageFile_(message));
         if (!first)
            json += ",";
         first = false;
         int action = (int) recordset->GetLongValue("schedaction");
         json += "{\"id\":" + Int64Text(recordset->GetInt64Value("schedid")) +
                 ",\"action\":\"" + (action == ScheduledMailTask::ActionSend ? "send" : "return") +
                 "\",\"message_id\":" + Int64Text(messageId) +
                 ",\"at\":\"" + JsonEscape_(Utf8_(recordset->GetStringValue("schedat").Mid(0, 16))) +
                 "\",\"folder_id\":" + Int64Text(recordset->GetInt64Value("schedfolderid")) +
                 ",\"subject\":\"" + JsonEscape_(Utf8_(subject)) + "\"}";
         recordset->MoveNext();
      }
      json += "]}";
      return BuildResponse_(200, json);
   }

   // A cancelled send leaves the draft where it is; a cancelled snooze brings
   // the message back now.
   HttpResponse
   RestApiServer::HandleMeScheduledCancel_(const Caller &caller, __int64 schedId)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      SQLCommand command("select schedmessageid, schedaction, schedfolderid from hm_scheduled where schedid = @ID and schedaccountid = @ACCOUNTID");
      command.AddParameter("@ID", schedId);
      command.AddParameter("@ACCOUNTID", account->GetID());
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset || recordset->IsEOF())
         return BuildResponse_(404, "{\"error\":\"nothing scheduled by that id\"}");

      __int64 messageId = recordset->GetInt64Value("schedmessageid");
      int action = (int) recordset->GetLongValue("schedaction");
      __int64 folderId = recordset->GetInt64Value("schedfolderid");

      bool returned = false;
      if (action == ScheduledMailTask::ActionReturn)
      {
         std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
         if (PersistentMessage::ReadObject(message, messageId) && message->GetID() != 0)
            returned = ScheduledMailTask::ReturnSnoozed(account, message, folderId);
      }

      SQLCommand remove("delete from hm_scheduled where schedid = @ID");
      remove.AddParameter("@ID", schedId);
      Application::Instance()->GetDBManager()->Execute(remove);

      return BuildResponse_(200, AnsiString("{\"cancelled\":true,\"returned\":") + (returned ? "true" : "false") + "}");
   }

   HttpResponse
   RestApiServer::HandleScheduledRun_()
   {
      int ran = ScheduledMailTask::RunDue();
      int files = SweepExpiredFiles();
      return BuildResponse_(200, "{\"ran\":" + Int64Text(ran) + ",\"files_removed\":" + Int64Text(files) + "}");
   }

   // The folder as one mbox: every message with a From_ line before it and a
   // line of its own that begins "From " made ">From ", which is what mbox
   // readers undo.
   HttpResponse
   RestApiServer::HandleMeFolderExport_(const Caller &caller, __int64 folderId)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder = FindReadableFolder_(account, folderId);
      if (!folder)
         return BuildResponse_(404, "{\"error\":\"folder not found\"}");

      std::shared_ptr<Messages> messages = folder->GetMessages();
      if (!messages)
         return BuildResponse_(500, "{\"error\":\"the folder could not be read\"}");

      std::vector<std::shared_ptr<Message>> snapshot = messages->GetCopy();
      __int64 total = 0;
      for (size_t i = 0; i < snapshot.size(); i++)
         if (snapshot[i] && !snapshot[i]->GetFlagDeleted())
            total += snapshot[i]->GetSize();
      if (total > MaxExportBytes)
         return BuildResponse_(413, "{\"error\":\"the folder is larger than 200 MB; export it with a mail client\"}");

      AnsiString mbox;
      for (size_t i = 0; i < snapshot.size(); i++)
      {
         std::shared_ptr<Message> message = snapshot[i];
         if (!message || message->GetFlagDeleted())
            continue;
         AnsiString bytes;
         if (!ReadWholeFile(MessageFile_(message), bytes))
            continue;
         AnsiString from = Utf8_(message->GetFromAddress());
         if (from.IsEmpty())
            from = "MAILER-DAEMON";
         mbox += "From " + from + " " + Utf8_(message->GetCreateTime()) + "\r\n";
         // A body line that begins "From " is quoted; the first line is a header.
         AnsiString quoted;
         int start = 0;
         while (start <= bytes.GetLength())
         {
            int end = bytes.Find("\n", start);
            AnsiString line = end < 0 ? bytes.Mid(start) : bytes.Mid(start, end - start + 1);
            if (line.Find("From ") == 0)
               quoted += ">";
            quoted += line;
            if (end < 0)
               break;
            start = end + 1;
         }
         mbox += quoted;
         if (mbox.GetLength() < 2 || mbox.Mid(mbox.GetLength() - 2) != "\r\n")
            mbox += "\r\n";
         mbox += "\r\n";
      }

      HttpResponse response;
      response.status = 200;
      response.content_type = "application/mbox";
      response.body = mbox;
      response.extra_headers =
         "Content-Disposition: attachment; filename=\"folder-" + Int64Text(folder->GetID()) + ".mbox\"\r\n"
         "X-Content-Type-Options: nosniff\r\n"
         "Content-Security-Policy: sandbox\r\n"
         "Cache-Control: no-store\r\n";
      return response;
   }

   // One message, the request body, into a folder of the account's own -
   // what IMAP APPEND does, for a .eml a reader saved elsewhere.
   HttpResponse
   RestApiServer::HandleMeFolderImport_(const Caller &caller, __int64 folderId, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder = FindReadableFolder_(account, folderId);
      if (!folder || folder->GetAccountID() != account->GetID())
         return BuildResponse_(404, "{\"error\":\"folder not found\"}");
      if (!RightOn_(account, folder, ACLPermission::PermissionInsert))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to add messages\"}");

      if (requestBody.size() == 0)
         return BuildResponse_(400, "{\"error\":\"the body is the message, as a .eml file\"}");
      if (requestBody.size() > MaxImportBytes)
         return BuildResponse_(413, "{\"error\":\"a message is at most 25 MB here\"}");
      int colon = requestBody.Find(":");
      int lineEnd = requestBody.Find("\n");
      if (colon < 0 || (lineEnd >= 0 && colon > lineEnd))
         return BuildResponse_(400, "{\"error\":\"the body does not begin with a header line\"}");

      // Lines end in CRLF in a stored message; a file that came with bare
      // LF is given them.
      AnsiString text;
      for (int i = 0; i < requestBody.GetLength(); i++)
      {
         char c = requestBody[i];
         if (c == '\n' && (i == 0 || requestBody[i - 1] != '\r'))
            text += '\r';
         text += c;
      }
      if (text.GetLength() < 2 || text.Mid(text.GetLength() - 2) != "\r\n")
         text += "\r\n";

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      message->SetAccountID(folder->GetAccountID());
      message->SetFolderID(folder->GetID());
      const String fileName = PersistentMessage::GetFileName(account, message);
      // An account that has never had a message on disk has no directory yet;
      // APPEND makes it on the way, and so does this.
      String directory = FileUtilities::GetFilePath(fileName);
      if (!FileUtilities::Exists(directory) && !FileUtilities::CreateDirectory(directory))
         return BuildResponse_(500, "{\"error\":\"the account's directory could not be made\"}");
      if (!FileUtilities::WriteToFile(fileName, text))
         return BuildResponse_(500, "{\"error\":\"the message could not be written\"}");
      message->SetSize((int) FileUtilities::FileSize(fileName));
      message->SetFlagSeen(false);
      // A message in a folder is a delivered one; the store refuses to save one
      // still marked as created, as it would a half-written delivery.
      message->SetState(Message::Delivered);
      if (!PersistentMessage::SaveObject(message))
      {
         FileUtilities::DeleteFile(fileName);
         return BuildResponse_(500, "{\"error\":\"the message could not be stored\"}");
      }

      MessagesContainer::Instance()->SetFolderNeedsRefresh(folder->GetID());
      std::vector<__int64> added;
      added.push_back(message->GetID());
      std::shared_ptr<ChangeNotification> notification =
         std::shared_ptr<ChangeNotification>(new ChangeNotification(folder->GetAccountID(), folder->GetID(), ChangeNotification::NotificationMessageAdded, added));
      Application::Instance()->GetNotificationServer()->SendNotification(notification);

      return BuildResponse_(201, "{\"id\":" + Int64Text(message->GetID()) + ",\"folder_id\":" + Int64Text(folder->GetID()) + "}");
   }
}
