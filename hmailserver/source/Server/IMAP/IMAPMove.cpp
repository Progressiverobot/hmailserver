// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

      std::shared_ptr<const Account> pAccount = pConnection->GetAccount();

      if (!pFolder->IsPublicFolder())
      {
         if (!pAccount->SpaceAvailable(pOldMessage->GetSize()))
            return IMAPResult(IMAPResult::ResultNo, "Your quota has been exceeded.");
      }

      // Check if the user has permission to insert into the destination folder.
      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionInsert))
         return IMAPResult(IMAPResult::ResultBad, "ACL: Insert permission denied (Required for MOVE command).");

      std::shared_ptr<Message> pNewMessage = PersistentMessage::CopyToIMAPFolder(pAccount, pOldMessage, pFolder);

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

      std::vector<__int64> expunged_messages_uid;
      std::vector<__int64> expunged_messages_index;

      std::vector<unsigned int> &moved_uids = moved_message_uids_;
      std::function<bool(int, std::shared_ptr<Message>)> filter = [&expunged_messages_index, &expunged_messages_uid, &moved_uids](int index, std::shared_ptr<Message> message)
      {
         if (std::find(moved_uids.begin(), moved_uids.end(), message->GetUID()) != moved_uids.end())
         {
            expunged_messages_index.push_back(index);
            expunged_messages_uid.push_back(message->GetID());
            return true;
         }

         return false;
      };

      auto messages = MessagesContainer::Instance()->GetMessages(pCurFolder->GetAccountID(), pCurFolder->GetID());
      messages->DeleteMessages(filter);

      String sResponse;
      for (__int64 index : expunged_messages_index)
      {
         String sTemp;
         sTemp.Format(_T("* %d EXPUNGE\r\n"), (int) index);
         sResponse += sTemp;
      }

      if (!sResponse.IsEmpty())
         pConnection->SendAsciiData(sResponse);

      if (!expunged_messages_uid.empty())
      {
         auto recent_messages = pConnection->GetRecentMessages();

         for (__int64 messageUid : expunged_messages_uid)
         {
            auto recent_messages_it = recent_messages.find(messageUid);
            if (recent_messages_it != recent_messages.end())
               recent_messages.erase(recent_messages_it);
         }

         // Notify the mailbox notifier that the source folder contents changed.
         std::shared_ptr<ChangeNotification> pNotification = 
            std::shared_ptr<ChangeNotification>(new ChangeNotification(pCurFolder->GetAccountID(), pCurFolder->GetID(), ChangeNotification::NotificationMessageDeleted, expunged_messages_index));

         Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pNotification);
      }
   }
}
