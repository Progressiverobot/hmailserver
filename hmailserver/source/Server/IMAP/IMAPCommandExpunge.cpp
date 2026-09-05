// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandExpunge.h"
#include "IMAPConnection.h"

#include "MessagesContainer.h"
#include "IMAPFolderView.h"
#include "IMAPNotificationClient.h"

#include "../Common/BO/Messages.h"
#include "../Common/BO/Message.h"

#include "../Common/BO/IMAPFolder.h"

#include "../Common/Tracking/ChangeNotification.h"
#include "../Common/Tracking/NotificationServer.h"
#include "../Common/BO/ACLPermission.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandEXPUNGE::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      if (pConnection->GetCurrentFolderReadOnly())
      {
         return IMAPResult(IMAPResult::ResultNo, "Expunge command on read-only folder.");
      }

      // Iterate through mail boxes and delete messages marked for deletion.
      std::shared_ptr<IMAPFolder> pCurFolder = pConnection->GetCurrentFolder();

      if (!pCurFolder)
         return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

      if (!pConnection->CheckPermission(pCurFolder, ACLPermission::PermissionExpunge))
         return IMAPResult(IMAPResult::ResultBad, "ACL: Expunge permission denied (Required for EXPUNGE command).");

      std::shared_ptr<IMAPFolderView> view = pConnection->GetCurrentFolderView();

      if (!view)
         return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

      auto messages = MessagesContainer::Instance()->GetMessages(pCurFolder->GetAccountID(), pCurFolder->GetID());

      // EXPUNGE may report new messages as well, so take them into the view first.
      view->AppendNewMessages(messages);

      // Only messages this session knows about are expunged here: the client has not
      // been told about the others, so their sequence numbers would mean nothing to it.
      std::vector<std::pair<int, IMAPViewEntry> > entries = view->GetAllEntries();

      std::set<__int64> view_message_ids;
      for (const std::pair<int, IMAPViewEntry> &entry : entries)
         view_message_ids.insert(entry.second.message_id);

      std::map<__int64, std::shared_ptr<Message> > view_messages = messages->GetCopyByIds(view_message_ids);

      std::set<__int64> messages_to_delete;

      for (const std::pair<int, IMAPViewEntry> &entry : entries)
      {
         auto iter = view_messages.find(entry.second.message_id);

         if (iter == view_messages.end())
         {
            // Expunged by another session. Reported below, together with this one's.
            view->MarkVanished(entry.second.message_id);
            continue;
         }

         if ((*iter).second->GetFlagDeleted())
            messages_to_delete.insert(entry.second.message_id);
      }

      std::vector<__int64> deleted_message_ids = messages->DeleteMessagesById(messages_to_delete);

      // EXPUNGE is a point where an untagged EXPUNGE may be sent, so whatever earlier
      // commands found gone joins this one: the client learns of both at once.
      std::vector<__int64> ids_to_report = deleted_message_ids;
      std::vector<__int64> vanished_elsewhere = view->TakeVanished();
      ids_to_report.insert(ids_to_report.end(), vanished_elsewhere.begin(), vanished_elsewhere.end());

      std::vector<unsigned int> expunged_uids;
      std::vector<int> expunged_sequences = view->RemoveMessages(ids_to_report, &expunged_uids);

      pConnection->RemoveRecentMessages(ids_to_report);

      String sResponse = IMAPNotificationClient::FormatExpungeResponses(pConnection, expunged_sequences, expunged_uids);

      if (!sResponse.IsEmpty())
         pConnection->SendAsciiData(sResponse);

      if (!deleted_message_ids.empty())
      {
         // Messages have been expunged. Notify the mailbox notifier that the mailbox
         // contents have changed - with message ids, which every other session turns
         // into its own numbers. The view is updated first, and no connection lock is
         // held: the notification is delivered synchronously, into the other sessions.
         std::shared_ptr<ChangeNotification> pNotification =
            std::shared_ptr<ChangeNotification>(new ChangeNotification(pCurFolder->GetAccountID(), pCurFolder->GetID(), ChangeNotification::NotificationMessageDeleted, deleted_message_ids));

         Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pNotification);
      }

      // We're done.
      sResponse = pArgument->Tag() + " OK EXPUNGE Completed\r\n";
      pConnection->SendAsciiData(sResponse);

      return IMAPResult();
   }
}