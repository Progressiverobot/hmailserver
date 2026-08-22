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
         if (refresh_needed_)
         {
            refresh_needed_ = false;

            messages_->Refresh(update_recent_flags);
         }

         return messages_; 
      }

      void SetRefreshNeeded()
      {
         refresh_needed_ = true;
      }

   private:

      std::shared_ptr<Messages> messages_;
      bool refresh_needed_;
   };
}