// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class ChangeNotification
   {
   public:

      enum NotificationType
      {
         NotificationMessageAdded = 1,
         NotificationMessageDeleted = 2,
         NotificationMessageFlagsChanged = 3,
      };


      ChangeNotification(NotificationType type);
      ChangeNotification(__int64 accountID, __int64 folderID, NotificationType type);
      // affectedMessageIds always holds hm_messages.messageid values - never sequence
      // numbers, which mean different messages to different sessions. Each session's
      // IMAPFolderView turns them into that session's own numbers.
      ChangeNotification(__int64 accountID, __int64 folderID, NotificationType type, const std::vector<__int64>& affectedMessageIds);
      ChangeNotification(__int64 accountID, __int64 folderID, NotificationType type, __int64 data);

      virtual ~ChangeNotification();

      const std::vector<__int64>& GetAffectedMessageIds() const;
      __int64 GetFolderID() const;
      __int64 GetAccountID() const;
      NotificationType GetType() const;
      __int64 GetData() const;

   private:

      __int64 account_id_;
      __int64 folder_id_;
      __int64 data_;
      std::vector<__int64> affected_message_ids_;
      NotificationType type_;

   };
}
