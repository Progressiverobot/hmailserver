// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "InboxIDCache.h"

#include "../Persistence/PersistentIMAPFolder.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   InboxIDCache::InboxIDCache() :
      enabled_ (true)
   {

   }

   InboxIDCache::~InboxIDCache(void)
   {
   }

   void 
   InboxIDCache::Clear()
   {
      if (!enabled_)
         return;
   
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      inbox_id_.clear();
   }

   __int64
   InboxIDCache::GetUserInboxFolder(__int64 accountID)
   {
      if (enabled_)
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);
         auto iter = inbox_id_.find(accountID);

         if (iter != inbox_id_.end())
         {
            __int64 result = (*iter).second;
            return result;
         }
      }

      __int64 inboxID = PersistentIMAPFolder::GetUserInboxFolder(accountID);

      if (enabled_)
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);
         inbox_id_[accountID] = inboxID;
      }

      return inboxID;
   }
}