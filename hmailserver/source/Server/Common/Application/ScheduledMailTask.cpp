// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "ScheduledMailTask.h"
#include "Application.h"
#include "FolderManager.h"
#include "../BO/Account.h"
#include "../BO/Message.h"
#include "../BO/MessageData.h"
#include "../BO/MessageRecipients.h"
#include "../BO/Messages.h"
#include "../BO/IMAPFolders.h"
#include "../BO/IMAPFolder.h"
#include "../Cache/CacheContainer.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/DALRecordset.h"
#include "../Persistence/PersistentMessage.h"
#include "../Util/Time.h"
#include "../Util/FileUtilities.h"
#include "../Util/MessageUtilities.h"
#include "../Tracking/ChangeNotification.h"
#include "../Tracking/NotificationServer.h"
#include "../../IMAP/IMAPFolderContainer.h"
#include "../../IMAP/MessagesContainer.h"
#include "../../IMAP/IMAPSpecialUse.h"
#include "../../SMTP/RecipientParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      void RemoveRow(__int64 schedId)
      {
         SQLCommand command("delete from hm_scheduled where schedid = @ID");
         command.AddParameter("@ID", schedId);
         Application::Instance()->GetDBManager()->Execute(command);
      }

      // "a@x, Name <b@y>; c@z" -> the three addresses.
      void AddressesOf(const String &list, std::vector<String> &addresses)
      {
         std::vector<String> parts = StringParser::SplitString(list, _T(","));
         for (size_t i = 0; i < parts.size(); i++)
         {
            std::vector<String> more = StringParser::SplitString(parts[i], _T(";"));
            for (size_t j = 0; j < more.size(); j++)
            {
               String entry = more[j];
               int open = entry.Find(_T("<"));
               int close = entry.Find(_T(">"));
               if (open >= 0 && close > open)
                  entry = entry.Mid(open + 1, close - open - 1);
               entry.TrimLeft();
               entry.TrimRight();
               if (!entry.IsEmpty())
                  addresses.push_back(entry);
            }
         }
      }

      void Notify(std::shared_ptr<IMAPFolder> folder, ChangeNotification::NotificationType type, const std::vector<__int64> &ids)
      {
         std::shared_ptr<ChangeNotification> notification =
            std::shared_ptr<ChangeNotification>(new ChangeNotification(folder->GetAccountID(), folder->GetID(), type, ids));
         Application::Instance()->GetNotificationServer()->SendNotification(notification);
      }

      // The row and the file of a message in the account's own tree, gone,
      // and every session told - what EXPUNGE does.
      bool Remove(std::shared_ptr<IMAPFolder> folder, std::shared_ptr<Message> message)
      {
         std::shared_ptr<Messages> messages = MessagesContainer::Instance()->GetMessages(folder->GetAccountID(), folder->GetID());
         if (!messages)
            return false;
         std::set<__int64> ids;
         ids.insert(message->GetID());
         std::vector<__int64> deleted = messages->DeleteMessagesById(ids);
         if (deleted.empty())
            return false;
         Notify(folder, ChangeNotification::NotificationMessageDeleted, deleted);
         return true;
      }
   }

   ScheduledMailTask::ScheduledMailTask()
   {
   }

   ScheduledMailTask::~ScheduledMailTask()
   {
   }

   void
   ScheduledMailTask::DoWork()
   {
      RunDue();
   }

   int
   ScheduledMailTask::RunDue()
   {
      // The rows are few and the time is compared as the text both sides are
      // stored in, "YYYY-MM-DD HH:MM:SS", which orders as time does - so no
      // backend is asked to compare a bound value with a datetime column.
      const String now = Time::GetCurrentDateTime();

      struct Due
      {
         __int64 id;
         __int64 accountId;
         __int64 messageId;
         int action;
         __int64 folderId;
      };
      std::vector<Due> due;

      SQLCommand command("select schedid, schedaccountid, schedmessageid, schedaction, schedat, schedfolderid from hm_scheduled order by schedat");
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return 0;
      while (!recordset->IsEOF())
      {
         String at = recordset->GetStringValue("schedat");
         if (at.GetLength() >= 19 && at.Mid(0, 19) <= now)
         {
            Due one;
            one.id = recordset->GetInt64Value("schedid");
            one.accountId = recordset->GetInt64Value("schedaccountid");
            one.messageId = recordset->GetInt64Value("schedmessageid");
            one.action = (int) recordset->GetLongValue("schedaction");
            one.folderId = recordset->GetInt64Value("schedfolderid");
            due.push_back(one);
         }
         recordset->MoveNext();
      }

      int ran = 0;
      for (size_t i = 0; i < due.size(); i++)
      {
         std::shared_ptr<const Account> account = CacheContainer::Instance()->GetAccount(due[i].accountId);
         std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
         bool present = account && PersistentMessage::ReadObject(message, due[i].messageId) && message->GetID() != 0;
         if (present)
         {
            if (due[i].action == ActionSend)
               SendDraft(account, message);
            else if (due[i].action == ActionReturn)
               ReturnSnoozed(account, message, due[i].folderId);
         }
         // The row goes whether the action succeeded or its message is gone:
         // a draft the reader deleted is not sent, and a row that cannot act
         // must not be retried every minute for ever.
         RemoveRow(due[i].id);
         ran++;
      }
      return ran;
   }

   // The draft becomes a message in the queue, as the send route would have
   // made it: the recipients from its To, Cc and Bcc, each put through the
   // checks a send makes, the Bcc dropped from the copy that goes, a copy in
   // the folder designated \Sent, the draft itself gone.
   bool
   ScheduledMailTask::SendDraft(std::shared_ptr<const Account> account, std::shared_ptr<Message> draft)
   {
      const String draftFile = PersistentMessage::GetFileName(account, draft);
      MessageData data;
      if (!data.LoadFromMessage(draftFile, draft))
         return false;

      std::vector<String> addresses;
      AddressesOf(data.GetTo(), addresses);
      AddressesOf(data.GetCC(), addresses);
      AddressesOf(data.GetBCC(), addresses);
      if (addresses.empty())
         return false;

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      message->SetFromAddress(account->GetAddress());

      RecipientParser parser;
      for (size_t i = 0; i < addresses.size(); i++)
      {
         if (!StringParser::IsValidEmailAddress(addresses[i]))
            continue;
         String reason;
         bool treatSecurityAsLocal = false;
         if (parser.CheckDeliveryPossibility(true, account->GetAddress(), addresses[i], reason, treatSecurityAsLocal, 0, true) != RecipientParser::DP_Possible)
            continue;
         bool recipientOK = false;
         parser.CreateMessageRecipientList(addresses[i], message->GetRecipients(), recipientOK);
      }
      if (message->GetRecipients()->GetCount() == 0)
         return false;

      const String fileName = PersistentMessage::GetFileName(message);
      data.DeleteField("Bcc");
      data.SetSentTime(Time::GetCurrentMimeDate());
      if (!data.Write(fileName))
         return false;

      message->SetSize((int) FileUtilities::FileSize(fileName));
      message->SetState(Message::Delivering);
      if (!PersistentMessage::SaveObject(message))
      {
         FileUtilities::DeleteFile(fileName);
         return false;
      }

      // A copy for \Sent, marked read, as the send route keeps one.
      std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (folders)
      {
         std::map<__int64, int> designations;
         IMAPSpecialUse::Resolve(folders, designations);
         for (std::map<__int64, int>::const_iterator it = designations.begin(); it != designations.end(); ++it)
         {
            if ((it->second & IMAPSpecialUse::DesignationSent) == 0)
               continue;
            std::shared_ptr<IMAPFolder> sent = folders->GetItemByDBIDRecursive(it->first);
            if (!sent)
               continue;
            std::shared_ptr<Message> copy = PersistentMessage::CopyToIMAPFolder(message, sent);
            if (copy)
            {
               copy->SetFlagSeen(true);
               if (PersistentMessage::SaveObject(copy))
               {
                  sent->GetMessages()->Refresh(false);
                  std::vector<__int64> added;
                  added.push_back(copy->GetID());
                  Notify(sent, ChangeNotification::NotificationMessageAdded, added);
               }
               else
                  FileUtilities::DeleteFile(PersistentMessage::GetFileName(account, copy));
            }
            break;
         }

         std::shared_ptr<IMAPFolder> draftsFolder = folders->GetItemByDBIDRecursive(draft->GetFolderID());
         if (draftsFolder)
            Remove(draftsFolder, draft);
      }

      Application::Instance()->SubmitPendingEmail();
      return true;
   }

   // The snoozed message back where it came from - or the inbox when that
   // folder is gone - and unread, so it is new again.
   bool
   ScheduledMailTask::ReturnSnoozed(std::shared_ptr<const Account> account, std::shared_ptr<Message> message, __int64 folderId)
   {
      std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!folders)
         return false;
      std::shared_ptr<IMAPFolder> target = folders->GetItemByDBIDRecursive(folderId);
      if (!target)
         target = folders->GetFolderByName(_T("INBOX"));
      if (!target)
         return false;

      std::shared_ptr<Message> returned = message;
      if (target->GetID() != message->GetFolderID())
      {
         std::shared_ptr<IMAPFolder> source = folders->GetItemByDBIDRecursive(message->GetFolderID());
         __int64 newMessageId = 0;
         if (!MessageUtilities::CopyToIMAPFolder(message, (int) target->GetID(), newMessageId))
            return false;
         if (source)
            Remove(source, message);
         returned = std::shared_ptr<Message>(new Message());
         if (!PersistentMessage::ReadObject(returned, newMessageId) || returned->GetID() == 0)
            return true;
      }

      returned->SetFlagSeen(false);
      if (Application::Instance()->GetFolderManager()->UpdateMessageFlags((int) account->GetID(), (int) target->GetID(), returned->GetID(), returned->GetFlags()))
      {
         std::vector<__int64> changed;
         changed.push_back(returned->GetID());
         Notify(target, ChangeNotification::NotificationMessageFlagsChanged, changed);
      }
      return true;
   }
}
