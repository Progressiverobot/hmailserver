// Copyright (c) 2005 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Util/Singleton.h"
#include "../BO/Messages.h"

namespace HM
{

   class CachedMessages
   {
   public:

      CachedMessages(std::shared_ptr<Messages> messages) :
         messages_(messages),
         refresh_needed_(true)
      {

      }

      __int64 GetID()
      {
         return messages_->GetFolderID();
      }

      String GetName()
      {
         String name = Formatter::Format("{0}", messages_->GetAccountID());

         return name;
      }

      size_t GetEstimatedCachingSize()
      {
         // 64-bit unsigned throughout. As int arithmetic this overflowed past
         // ~2.1M messages in one folder and, converted to size_t, became a value
         // near 2^64 - which as a cache decrement clamps the whole total to zero
         // and switches the size cap off until the next clear.
         const int count = messages_->GetCount();

         return count > 0 ? (size_t) 1024 * (size_t) count : 0;
      }

      std::shared_ptr<Messages> GetMessages(bool update_recent_flags)
      {
         // Held across the refresh, so a second session asking for the same folder
         // waits for it rather than being handed a collection that is half loaded.
         boost::lock_guard<boost::recursive_mutex> guard(refresh_mutex_);

         if (refresh_needed_)
         {
            // Cleared only once the refresh succeeded, so a failed load is retried
            // instead of leaving the folder looking up to date. A SetRefreshNeeded
            // made while the refresh runs waits on the mutex, so it cannot be
            // swallowed by this either (upstream #566).
            if (messages_->Refresh(update_recent_flags))
               refresh_needed_ = false;
         }

         return messages_;
      }

      void SetRefreshNeeded()
      {
         boost::lock_guard<boost::recursive_mutex> guard(refresh_mutex_);

         refresh_needed_ = true;
      }

   private:

      std::shared_ptr<Messages> messages_;
      bool refresh_needed_;
      boost::recursive_mutex refresh_mutex_;
   };
}