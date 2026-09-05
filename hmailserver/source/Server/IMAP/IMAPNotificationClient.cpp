// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "IMAPNotificationClient.h"
#include "IMAPConnection.h"
#include "IMAPStore.h"
#include "IMAPFolderView.h"

#include "../Common/Tracking/ChangeNotification.h"
#include "../common/Tracking/NotificationServer.h"

#include "../Common/BO/Messages.h"
#include "../Common/BO/IMAPFolder.h"

#include "../Common/TCPIP/DisconnectedException.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPNotificationClient::IMAPNotificationClient() :
      message_change_subscription_id_(0),
      folder_list_change_subscription_id_(0),
      account_id_(0),
      folder_id_(0)
   {

   }

   IMAPNotificationClient::~IMAPNotificationClient()
   {
      try
      {
         if (folder_list_change_subscription_id_ > 0)
         {
            std::shared_ptr<NotificationServer> notificationServer = Application::Instance()->GetNotificationServer();
            notificationServer->UnsubscribeFolderListChanges(account_id_, folder_list_change_subscription_id_);
         }
      }
      catch (...)
      {

      }
   }

   void
   IMAPNotificationClient::SubscribeMessageChanges(__int64 accountID, __int64 folderID)
   {
      assert(accountID >= 0);
      assert(folderID > 0);

      account_id_ = accountID;
      folder_id_ = folderID;

      std::shared_ptr<NotificationServer> notificationServer = Application::Instance()->GetNotificationServer();
      message_change_subscription_id_ = notificationServer->SubscribeMessageChanges(account_id_, folder_id_, shared_from_this());
   }

   void
   IMAPNotificationClient::UnsubscribeMessageChanges()
   {
      assert(account_id_ >= 0);
      assert(folder_id_ > 0);
      assert(message_change_subscription_id_ > 0);

      // Since we don't want to look at he folder any more,
      // we're not interested in any updates.
      std::shared_ptr<NotificationServer> notificationServer = Application::Instance()->GetNotificationServer();
      notificationServer->UnsubscribeMessageChanges(account_id_, folder_id_, message_change_subscription_id_);

      // If there are cached updates for this folder but the client
      // don't want to look at the folder any more, the cached updates
      // will be gone.
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      cached_changes_.clear();
   }

   void
   IMAPNotificationClient::SetConnection(std::weak_ptr<IMAPConnection> connection)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Called by the mailbox change notifier when something has happened to the mailbox.
   //---------------------------------------------------------------------------()
   {
      parent_connection_ = connection;
   }

   void
   IMAPNotificationClient::OnNotification(std::shared_ptr<ChangeNotification> notification)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Called by the mailbox change notifier when something has happened to the mailbox.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<IMAPConnection> parentConnection = parent_connection_.lock();

      if (!parentConnection)
         return;

      // This is not the connection's own thread. Hold its state lock across the
      // idling check and the send, so the folder cannot be closed in between.
      IMAPConnection::StateLock lock(parentConnection->GetStateMutex());

      if (parentConnection->GetIsIdling())
      {
         try
         {
            SendChangeNotification_(notification);
         }
         catch (DisconnectedException&)
         {
            // We were unable to send the notifications to the client, because he has disconnected.
            // This is normal behavior, and not an error we want to log.
         }
      }
      else
         CacheChangeNotification_(notification);
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Cache this change. We'll send a notification later on.
   //---------------------------------------------------------------------------()
   void
   IMAPNotificationClient::CacheChangeNotification_(std::shared_ptr<ChangeNotification> pChangeNotification)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      cached_changes_.push_back(pChangeNotification);
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Send a summary of all changes to the client...
   //---------------------------------------------------------------------------()
   void
   IMAPNotificationClient::SendCachedNotifications(bool send_expunge)
   {
      std::shared_ptr<IMAPConnection> connection = parent_connection_.lock();

      if (!connection)
         return;

      // Lock order is always the connection's state lock before mutex_;
      // OnNotification takes them in that order too (through
      // CacheChangeNotification_). Reversing it here would deadlock against a
      // notification arriving on another thread.
      IMAPConnection::StateLock stateLock(connection->GetStateMutex());
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      int lastExists = -1;
      int lastRecent = -1;

      std::shared_ptr<IMAPFolderView> view = connection->GetCurrentFolderView();

      std::set<__int64> flagMessages;

      for(std::shared_ptr<ChangeNotification> changeNotification : cached_changes_)
      {
         switch (changeNotification->GetType())
         {
         case ChangeNotification::NotificationMessageAdded:
            {
               // The folder can be closed by the connection's own thread while a
               // notification for it is still being delivered on another; a null
               // folder here is that race, not an error (upstream #580).
               std::shared_ptr<IMAPFolder> currentFolder = connection->GetCurrentFolder();
               if (!currentFolder)
                  break;

               std::shared_ptr<Messages> pMessages = currentFolder->GetMessages();
               pMessages->Refresh(false);

               // New messages go at the end, so telling the client about them never
               // renumbers the ones it already knows.
               if (view)
                  view->AppendNewMessages(pMessages);

               lastExists = view ? view->GetMessageCount() : pMessages->GetCount();
               lastRecent = (int)connection->GetRecentMessageCount();
               break;
            }
         case ChangeNotification::NotificationMessageDeleted:
            {
               // Not allowed to say EXPUNGE yet: the notification stays cached and this
               // session keeps its numbering until the next command that permits it.
               if (!send_expunge)
                  break;

               // This is what removes the messages from this session's view.
               SendEXPUNGE_(changeNotification->GetAffectedMessageIds());

               if (view)
                  lastExists = view->GetMessageCount();

               lastRecent = (int)connection->GetRecentMessageCount();

               break;
            }
         case ChangeNotification::NotificationMessageFlagsChanged:
            {
               // Send flag notification
               for(__int64 messageID : changeNotification->GetAffectedMessageIds())
               {
                  if (flagMessages.find(messageID) == flagMessages.end())
                     flagMessages.insert(messageID);
               }

               break;
            }
         }
      }

      if (send_expunge && view)
      {
         // Messages a command found missing from the folder. Expunging them here means
         // the session recovers even if it never received the delete notification.
         std::vector<__int64> vanished = view->TakeVanished();

         if (!vanished.empty())
         {
            SendEXPUNGE_(vanished);

            lastExists = view->GetMessageCount();
            lastRecent = (int)connection->GetRecentMessageCount();
         }
      }

      if (flagMessages.size() > 0)
         SendFLAGS_(flagMessages);

      if (lastExists >= 0)
         SendEXISTS_(lastExists);

      if (lastRecent >= 0)
         SendRECENT_(lastRecent);

      std::vector<std::shared_ptr<ChangeNotification> >::iterator iter = cached_changes_.begin();

      for (; iter != cached_changes_.end();)
      {
         std::shared_ptr<ChangeNotification> changeNotification = (*iter);

         switch (changeNotification->GetType())
         {
         case ChangeNotification::NotificationMessageDeleted:
            if (!send_expunge)
            {
               iter++;
               continue;
            }
         }

         iter = cached_changes_.erase(iter);
      }
   }

   void
   IMAPNotificationClient::SendChangeNotification_(std::shared_ptr<ChangeNotification> pChangeNotification)
   {
      std::shared_ptr<IMAPConnection> connection = parent_connection_.lock();
      if (!connection)
         return;

      // Delivered on the notifying session's thread, while this connection may
      // be closing its folder on its own. A folder that is gone by the time the
      // notification arrives has nothing to be told about it (upstream #580).
      std::shared_ptr<IMAPFolder> currentFolder = connection->GetCurrentFolder();
      if (!currentFolder)
         return;

      switch (pChangeNotification->GetType())
      {
      case ChangeNotification::NotificationMessageAdded:
         {
            std::shared_ptr<Messages> pMessages = currentFolder->GetMessages();

            std::shared_ptr<IMAPFolderView> view = connection->GetCurrentFolderView();
            if (view)
               view->AppendNewMessages(pMessages);

            SendEXISTS_(view ? view->GetMessageCount() : pMessages->GetCount());
            SendRECENT_((int)connection->GetRecentMessageCount());
            break;
         }
      case ChangeNotification::NotificationMessageDeleted:
         {
            // This is what removes the messages from this session's view.
            SendEXPUNGE_(pChangeNotification->GetAffectedMessageIds());

            std::shared_ptr<IMAPFolderView> view = connection->GetCurrentFolderView();
            if (view)
               SendEXISTS_(view->GetMessageCount());

            SendRECENT_((int)connection->GetRecentMessageCount());

            break;
         }
      case ChangeNotification::NotificationMessageFlagsChanged:
         {
            // Send flag notification
            std::set<__int64> affectedMessages;
               for(__int64 messageID : pChangeNotification->GetAffectedMessageIds())
            {
               affectedMessages.insert(messageID);
            }

            SendFLAGS_(affectedMessages);

            break;
         }
      }
   }

   String
   IMAPNotificationClient::FormatExpungeResponses(std::shared_ptr<IMAPConnection> connection, const std::vector<int> &sequences, const std::vector<unsigned int> &uids)
   {
      String sResponse;

      if (sequences.empty())
         return sResponse;

      if (connection->GetQResyncEnabled() && !uids.empty())
      {
         // RFC 7162 (QRESYNC): one "* VANISHED" carrying the UID set, instead of one
         // "* n EXPUNGE" per message.
         std::vector<__int64> vanished(uids.begin(), uids.end());
         sResponse.Format(_T("* VANISHED %s\r\n"), IMAPConnection::CompactUidSet(vanished).c_str());
         return sResponse;
      }

      for (int sequence : sequences)
         sResponse.AppendFormat(_T("* %d EXPUNGE\r\n"), sequence);

      return sResponse;
   }

   void
   IMAPNotificationClient::SendEXPUNGE_(const std::vector<__int64> & message_ids)
   {
      std::shared_ptr<IMAPConnection> connection = parent_connection_.lock();
      if (!connection)
         return;

      std::shared_ptr<IMAPFolderView> view = connection->GetCurrentFolderView();
      if (!view)
         return;

      // The sequence numbers are this session's own, and the view shrinks as they are
      // produced - this is the one place a notification may renumber the session.
      std::vector<unsigned int> uids;
      std::vector<int> sequences = view->RemoveMessages(message_ids, &uids);

      connection->RemoveRecentMessages(message_ids);

      String sResponse = FormatExpungeResponses(connection, sequences, uids);

      if (sResponse.IsEmpty())
         return;

      connection->SendAsciiData(sResponse);
   }

   void
   IMAPNotificationClient::SendFLAGS_(const std::set<__int64> & vecMessages)
   {
      std::shared_ptr<IMAPConnection> connection = parent_connection_.lock();
      if (!connection)
         return;

      std::shared_ptr<IMAPFolder> currentFolder = connection->GetCurrentFolder();
      if (!currentFolder)
         return;

      std::shared_ptr<IMAPFolderView> view = connection->GetCurrentFolderView();
      if (!view)
         return;

      for (__int64 messageID : vecMessages)
      {
         int sequence = 0;

         // A message this session does not know about, or one that has been expunged.
         // Skipped rather than abandoning the loop: the messages after it still have
         // flags worth reporting.
         if (!view->GetSequenceByMessageID(messageID, sequence))
            continue;

         std::shared_ptr<Message> pMessage = currentFolder->GetMessages()->GetItemByDBID(messageID);

         if (!pMessage)
            continue;

         connection->SendAsciiData(IMAPStore::GetMessageFlags(pMessage, sequence, connection->GetCondstoreEnabled()));
      }
   }

   void
   IMAPNotificationClient::SendEXISTS_(int iExists)
   {
      std::shared_ptr<IMAPConnection> connection = parent_connection_.lock();
      if (!connection)
         return;

      String sResponse = GenerateExistsString(iExists);
      connection->SendAsciiData(sResponse);
   }

   void
   IMAPNotificationClient::SendRECENT_(int recent)
   {
      std::shared_ptr<IMAPConnection> connection = parent_connection_.lock();
      if (!connection)
         return;

      String sResponse = GenerateRecentString(recent);

      connection->SendAsciiData(sResponse);
   }

   String
   IMAPNotificationClient::GenerateRecentString(int recent)
   {
      return Formatter::Format("* {0} RECENT\r\n", recent);
   }

   String
   IMAPNotificationClient::GenerateExistsString(int exists)
   {
      return Formatter::Format("* {0} EXISTS\r\n", exists);
   }
}
