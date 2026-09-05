// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "MailboxRetentionTask.h"
#include "Application.h"

#include "../BO/Domains.h"
#include "../BO/Domain.h"
#include "../BO/Accounts.h"
#include "../BO/Account.h"
#include "../BO/Messages.h"
#include "../BO/Message.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "../SQL/DALRecordset.h"
#include "../SQL/SQLCommand.h"
#include "../Tracking/ChangeNotification.h"
#include "../Tracking/NotificationServer.h"
#include "../../IMAP/MessagesContainer.h"

#include <map>
#include <set>
#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The policy that applies to an account: its own when it has one, else the
      // domain's. 0 means none; -1 on the account means keep forever whatever the
      // domain says.
      int EffectiveRetentionDays_(int domainDays, int accountDays)
      {
         if (accountDays < 0)
            return 0;

         if (accountDays > 0)
            return accountDays;

         return domainDays > 0 ? domainDays : 0;
      }

      // "YYYY-MM-DD HH:MM:SS", local time, days ago - the format and the clock the
      // stored messagecreatetime uses (Time::GetCurrentDateTime), so the comparison
      // is between like and like.
      bool CutoffText_(int days, String &cutoff)
      {
         const std::time_t cutoffTime = std::time(nullptr) - (static_cast<std::time_t>(days) * 24 * 60 * 60);

         struct tm cutoffTm;
         if (localtime_s(&cutoffTm, &cutoffTime) != 0)
            return false;

         char text[32];
         if (strftime(text, sizeof(text), "%Y-%m-%d %H:%M:%S", &cutoffTm) == 0)
            return false;

         cutoff = text;
         return true;
      }

      int SweepAccount_(std::shared_ptr<const Account> account, int days)
      {
         String cutoff;
         if (!CutoffText_(days, cutoff))
            return 0;

         // The rows are read and the cutoff applied here rather than in the WHERE
         // clause; see the class comment. Only delivered messages: a message still in
         // the queue is not in a mailbox yet.
         SQLCommand command("select messageid, messagefolderid, messagecreatetime from hm_messages where messageaccountid = @ACCOUNTID and messagetype = @TYPE");
         command.AddParameter("@ACCOUNTID", account->GetID());
         command.AddParameter("@TYPE", (int) Message::Delivered);

         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset)
            return 0;

         std::map<__int64, std::set<__int64> > expiredByFolder;
         while (!recordset->IsEOF())
         {
            const String created = recordset->GetStringValue("messagecreatetime");

            // An unreadable stamp is left alone: deleting the only copy of a message on
            // the strength of a date that cannot be read is the wrong way to resolve it.
            if (created.GetLength() >= 19 && created.Compare(cutoff) < 0)
               expiredByFolder[recordset->GetInt64Value("messagefolderid")].insert(recordset->GetInt64Value("messageid"));

            recordset->MoveNext();
         }

         int deletedTotal = 0;

         for (const auto &folderEntry : expiredByFolder)
         {
            const __int64 folderID = folderEntry.first;

            // Through the folder's live collection, so the deletion is the one an
            // EXPUNGE performs: the file goes, the quota moves, the QRESYNC record is
            // written, and the collection every IMAP session shares is the one updated.
            // Refreshed first, because the sweep may be the first thing to touch this
            // folder since the service started and the collection is loaded lazily.
            std::shared_ptr<Messages> messages = MessagesContainer::Instance()->GetMessages(account->GetID(), folderID);
            if (!messages)
               continue;

            messages->Refresh(false);

            const std::vector<__int64> deleted = messages->DeleteMessagesById(folderEntry.second);
            if (deleted.empty())
               continue;

            deletedTotal += (int) deleted.size();

            // A session with the folder selected is told the way it is told about any
            // other expunge; its view decides when it may say so.
            std::shared_ptr<ChangeNotification> notification = std::shared_ptr<ChangeNotification>(
               new ChangeNotification(account->GetID(), folderID, ChangeNotification::NotificationMessageDeleted, deleted));
            Application::Instance()->GetNotificationServer()->SendNotification(notification);
         }

         if (deletedTotal > 0)
         {
            String message;
            message.Format(_T("Message retention: removed %d message(s) older than %d day(s) from %s."), deletedTotal, days, account->GetAddress().c_str());
            LOG_APPLICATION(message);
         }

         return deletedTotal;
      }
   }

   MailboxRetentionTask::MailboxRetentionTask(void)
   {
   }

   MailboxRetentionTask::~MailboxRetentionTask(void)
   {
   }

   void
   MailboxRetentionTask::DoWork()
   {
      Sweep();
   }

   int
   MailboxRetentionTask::Sweep()
   {
      int deleted = 0;

      // Read afresh rather than from the object cache: the sweep runs a few times a
      // day, and a policy changed since the cache was filled is exactly the change it
      // must not miss.
      Domains domains;
      domains.Refresh();

      for (int i = 0; i < domains.GetCount(); i++)
      {
         std::shared_ptr<Domain> domain = domains.GetItem(i);
         if (!domain)
            continue;

         std::shared_ptr<Accounts> accounts = domain->GetAccounts();
         if (!accounts)
            continue;

         accounts->Refresh();

         for (int j = 0; j < accounts->GetCount(); j++)
         {
            std::shared_ptr<Account> account = accounts->GetItem(j);
            if (!account)
               continue;

            const int days = EffectiveRetentionDays_(domain->GetMessageRetentionDays(), account->GetMessageRetentionDays());
            if (days <= 0)
               continue;

            deleted += SweepAccount_(account, days);
         }
      }

      return deleted;
   }
}
