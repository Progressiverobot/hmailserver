// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandClose.h"
#include "IMAPConnection.h"

#include "MessagesContainer.h"

#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolder.h"

#include "../Common/Tracking/ChangeNotification.h"
#include "../Common/Tracking/NotificationServer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandCLOSE::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");
      
      // Iterate through mail boxes and delete messages marked for deletion.
      std::shared_ptr<IMAPFolder> pCurFolder = pConnection->GetCurrentFolder();   

      if (!pCurFolder)
         return IMAPResult(IMAPResult::ResultBad, "No folder selected.");

      // Check if user has permission to expunge. If so, do that.
      if (!pConnection->GetCurrentFolderReadOnly() &&
          pConnection->CheckPermission(pConnection->GetCurrentFolder(), ACLPermission::PermissionExpunge))
      {
         std::vector<__int64> expunged_messages_index;

         std::function<bool(int, std::shared_ptr<Message>)> filter = [&expunged_messages_index](int index, std::shared_ptr<Message> message)
            {
               if (message->GetFlagDeleted())
               {
                  expunged_messages_index.push_back(index);
                  return true;
               }

               return false;
            };

         auto messages = MessagesContainer::Instance()->GetMessages(pCurFolder->GetAccountID(), pCurFolder->GetID());
         messages->DeleteMessages(filter);

         if (!expunged_messages_index.empty())
         {
            // CLOSE expunges silently for the closing client (RFC 3501), but the
            // other sessions on this mailbox still have to be told, or their
            // sequence numbers silently drift and they act on the wrong message.
            std::shared_ptr<ChangeNotification> pNotification =
               std::shared_ptr<ChangeNotification>(new ChangeNotification(pCurFolder->GetAccountID(), pCurFolder->GetID(), ChangeNotification::NotificationMessageDeleted, expunged_messages_index));

            Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pNotification);
         }
      }

      pConnection->CloseCurrentFolder();

      String sResponse = pArgument->Tag() + " OK CLOSE completed\r\n";
      pConnection->SendAsciiData(sResponse);  

      return IMAPResult();

   }
}