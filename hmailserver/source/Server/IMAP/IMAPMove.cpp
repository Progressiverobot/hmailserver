// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPMove.h"
#include "IMAPConnection.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "IMAPSimpleCommandParser.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/Tracking/ChangeNotification.h"
#include "../Common/Tracking/NotificationServer.h"

#include "MessagesContainer.h"
#include "IMAPFolderView.h"
#include "IMAPNotificationClient.h"

#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPMove::IMAPMove()
   {

   }

   IMAPResult
   IMAPMove::DoAction(std::shared_ptr<IMAPConnection> pConnection, int messageIndex, std::shared_ptr<Message> pOldMessage, const std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pArgument || !pOldMessage)
         return IMAPResult(IMAPResult::ResultBad, "Invalid parameters");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);

      if (pParser->WordCount() <= 0)
         return IMAPResult(IMAPResult::ResultNo, "The command requires parameters.");

      String sFolderName;
      if (pParser->Word(0)->Clammerized())
         sFolderName = pArgument->Literal(0);
      else
      {
         sFolderName = pParser->Word(0)->Value();
         IMAPFolder::UnescapeFolderString(sFolderName);
      }

      std::shared_ptr<IMAPFolder> pFolder = pConnection->GetFolderByFullPath(sFolderName);
      if (!pFolder)
         return IMAPResult(IMAPResult::ResultBad, "The folder could not be found.");

      // The quota that matters is the DESTINATION folder owner's, because that
      // is the mailbox the copy will occupy - for an ordinary folder that is the
      // caller, and for a delegated one it is the owner. Charging the caller let
      // a delegate fill somebody else's mailbox past its limit while being
      // refused at their own.
      std::shared_ptr<const Account> pDestinationOwner = pConnection->GetAccountOwningFolder(pFolder);

      if (!pFolder->IsPublicFolder())
      {
         if (!pDestinationOwner)
            return IMAPResult(IMAPResult::ResultNo, "The destination folder could not be resolved.");

         if (!pDestinationOwner->SpaceAvailable(pOldMessage->GetSize()))
            return IMAPResult(IMAPResult::ResultNo, "[OVERQUOTA] Your quota has been exceeded.");
      }

      // Check if the user has permission to insert into the destination folder.
      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionInsert))
         return IMAPResult(IMAPResult::ResultBad, "ACL: Insert permission denied (Required for MOVE command).");

      std::shared_ptr<Message> pNewMessage = PersistentMessage::CopyToIMAPFolder(pOldMessage, pFolder);

      if (!pNewMessage)
         return IMAPResult(IMAPResult::ResultBad, "Failed to move message");

      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionWriteSeen))
         pNewMessage->SetFlagSeen(false);

      if (!PersistentMessage::SaveObject(pNewMessage))
         return IMAPResult(IMAPResult::ResultBad, "Failed to save moved message.");

      // RFC 4315/6851 (UIDPLUS): remember the source/destination UIDs for the COPYUID response.
      RecordCopyUid(pOldMessage->GetUID(), pNewMessage->GetUID(), (unsigned int) pFolder->GetCreationTime().ToInt());

      MessagesContainer::Instance()->SetFolderNeedsRefresh(pFolder->GetID());

      // Notify any IMAP idle client watching the target folder.
      std::shared_ptr<ChangeNotification> pNotification =
         std::shared_ptr<ChangeNotification>(new ChangeNotification(pFolder->GetAccountID(), pFolder->GetID(), ChangeNotification::NotificationMessageAdded));

      pConnection->SetDelayedChangeNotification(pNotification);

      // Remember the message so that it can be expunged from the source folder.
      moved_message_uids_.push_back(pOldMessage->GetUID());

      return IMAPResult();
   }

   void
   IMAPMove::ExpungeMovedMessages(std::shared_ptr<IMAPConnection> pConnection)
   {
      if (moved_message_uids_.empty())
         return;

      std::shared_ptr<IMAPFolder> pCurFolder = pConnection->GetCurrentFolder();
      if (!pCurFolder)
         return;

      std::shared_ptr<IMAPFolderView> view = pConnection->GetCurrentFolderView();
      if (!view)
         return;

      std::set<unsigned int> moved_uids(moved_message_uids_.begin(), moved_message_uids_.end());

      std::function<bool(std::shared_ptr<Message>)> filter = [&moved_uids](std::shared_ptr<Message> message)
      {
         return moved_uids.find(message->GetUID()) != moved_uids.end();
      };

      auto messages = MessagesContainer::Instance()->GetMessages(pCurFolder->GetAccountID(), pCurFolder->GetID());
      std::vector<__int64> deleted_message_ids = messages->DeleteMessages(filter);

      // RFC 6851: the untagged EXPUNGE responses for the source folder - numbered by this
      // session's view, or as one VANISHED under QRESYNC (RFC 6851 3.3).
      std::vector<unsigned int> expunged_uids;
      std::vector<int> expunged_sequences = view->RemoveMessages(deleted_message_ids, &expunged_uids);

      pConnection->RemoveRecentMessages(deleted_message_ids);

      String sResponse = IMAPNotificationClient::FormatExpungeResponses(pConnection, expunged_sequences, expunged_uids);

      if (!sResponse.IsEmpty())
         pConnection->SendAsciiData(sResponse);

      if (!deleted_message_ids.empty())
      {
         // Notify the mailbox notifier that the source folder contents changed.
         std::shared_ptr<ChangeNotification> pNotification =
            std::shared_ptr<ChangeNotification>(new ChangeNotification(pCurFolder->GetAccountID(), pCurFolder->GetID(), ChangeNotification::NotificationMessageDeleted, deleted_message_ids));

         Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pNotification);
      }
   }
}
