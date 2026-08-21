// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "BlockedSenderCache.h"
#include "../../Common/BO/BlockedSenders.h"

#include <boost/thread/shared_mutex.hpp>
#include <boost/thread/locks.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   boost::shared_mutex blockedSenderAccessMutex_;

   std::vector<std::shared_ptr<BlockedSender>> BlockedSenderCache::blockedSenderItems_;
   bool BlockedSenderCache::needRefresh_ = true;

   BlockedSenderCache::BlockedSenderCache(void)
   {
   }

   void
   BlockedSenderCache::SetNeedRefresh()
   {
      needRefresh_ = true;
   }

   std::shared_ptr<BlockedSender>
   BlockedSenderCache::GetBlockingEntry(const String &fromAddress)
   {
      // Create a lock for shared operations
      boost::upgrade_lock<boost::shared_mutex> lock(blockedSenderAccessMutex_);

      if (needRefresh_)
      {
         // We need exclusive access to be able to update the cache
         boost::upgrade_to_unique_lock<boost::shared_mutex> uniqueLock(lock);

         Refresh();
      }

      std::shared_ptr<BlockedSender> bestMatch;

      for (std::shared_ptr<BlockedSender> pBlockedSender : blockedSenderItems_)
      {
         if (pBlockedSender->Matches(fromAddress))
         {
            if (!bestMatch || pBlockedSender->GetScore() > bestMatch->GetScore())
               bestMatch = pBlockedSender;
         }
      }

      return bestMatch;
   }

   void
   BlockedSenderCache::Refresh()
   {
      BlockedSenders senders;
      if (!senders.Refresh())
      {
         // The database could not be read. Keep whatever the cache already
         // holds and leave needRefresh_ set, so the next message tries
         // again. Replacing the contents with an empty list here would
         // silently unblock every listed sender for the rest of the
         // process lifetime.
         return;
      }

      blockedSenderItems_ = senders.GetConstVector();
      needRefresh_ = false;
   }
}
