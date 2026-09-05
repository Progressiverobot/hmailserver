// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Common/Tracking/NotificationClient.h"

namespace HM
{
   class IMAPConnection;
   class ChangeNotification;

   class IMAPNotificationClient : public NotificationClient,
                                  public std::enable_shared_from_this<IMAPNotificationClient>
   {
   public:

      IMAPNotificationClient();
      virtual ~IMAPNotificationClient();

      void SetConnection(std::weak_ptr<IMAPConnection> connection);
      virtual void OnNotification(std::shared_ptr<ChangeNotification> notification);

      void SendCachedNotifications(bool send_expunge);

      static String GenerateRecentString(int recentMessages);
      static String GenerateExistsString(int recentMessages);

      // The untagged responses for messages just removed from a session's view: one
      // "* n EXPUNGE" per sequence number, or under QRESYNC a single "* VANISHED" with
      // the UIDs. Shared by EXPUNGE, UID EXPUNGE, MOVE, REPLACE and the notifications,
      // so every path that shrinks a view reports it the same way.
      static String FormatExpungeResponses(std::shared_ptr<IMAPConnection> connection, const std::vector<int> &sequences, const std::vector<unsigned int> &uids);

      void SubscribeMessageChanges(__int64 accountID, __int64 folderID);

      void UnsubscribeMessageChanges();

   private:

      void CacheChangeNotification_(std::shared_ptr<ChangeNotification> pChangeNotification);
      void SendChangeNotification_(std::shared_ptr<ChangeNotification> pChangeNotification);

      void SendEXISTS_(int iExists);
      void SendRECENT_(int recent);
      void SendEXPUNGE_(const std::vector<__int64> & message_ids);
      void SendFLAGS_(const std::set<__int64> & vecMessages);

      boost::recursive_mutex mutex_;
      std::vector<std::shared_ptr<ChangeNotification> > cached_changes_;

      std::weak_ptr<IMAPConnection> parent_connection_;

      __int64 account_id_;
      __int64 folder_id_;
      __int64 message_change_subscription_id_;
      __int64 folder_list_change_subscription_id_;

   };


}