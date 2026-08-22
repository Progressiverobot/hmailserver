// Copyright (c) 2013  Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "WhiteListCache.h"
#include "../../Common/BO/WhiteListAddresses.h"

#include <boost/thread/shared_mutex.hpp>
#include <boost/thread/locks.hpp>

namespace HM
{
   boost::shared_mutex whitelistAccessMutex_;

   std::vector<std::shared_ptr<WhiteListAddress>> WhiteListCache::whitelistItems_;
   bool WhiteListCache::needRefresh_ = true;

   WhiteListCache::WhiteListCache(void)
   {
      
   }

   void
   WhiteListCache::SetNeedRefresh()
   {
      needRefresh_ = true;
   }


   bool 
   WhiteListCache::IsWhitelisted(const String &fromAddress, const IPAddress &address)
   {
      // Create a lock for shared operations
      boost::upgrade_lock< boost::shared_mutex > lock(whitelistAccessMutex_);

      if (needRefresh_)
      {
         // We need exclusive access to be able to upade the cache
         boost::upgrade_to_unique_lock< boost::shared_mutex > uniqueLock(lock);

         Refresh();
      }

      auto iter = whitelistItems_.begin();
      auto iterEnd = whitelistItems_.end();

      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<WhiteListAddress> pWhiteAddress = (*iter);

         IPAddress iLowerIP = pWhiteAddress->GetLowerIPAddress();
         IPAddress iUpperIP = pWhiteAddress->GetUpperIPAddress();

         if (address.WithinRange(iLowerIP, iUpperIP))
         {
            String sWhiteEmailAddr = pWhiteAddress->GetEmailAddress();

            if (sWhiteEmailAddr.IsEmpty() || sWhiteEmailAddr == _T("*"))
            {
               // White listed
               return true;
            }

            // Check if the senders email address matches
            if (StringParser::WildcardMatchNoCase(sWhiteEmailAddr, fromAddress))
            {
               // White listed
               return true;
            }
         }
      }

      return false;

   }


   void
   WhiteListCache::Refresh()
   {

      WhiteListAddresses addresses;
      if (!addresses.Refresh())
      {
         // The database could not be read. Keep the previous contents and
         // leave needRefresh_ set so the next message retries; replacing
         // them with an empty list would silently stop whitelisting.
         return;
      }

      whitelistItems_ = addresses.GetConstVector();

      // Cleared only here, and only after a successful load. This line was
      // missing, so the flag stayed true forever and every message paid a
      // full hm_whitelist read - the cache existed but never cached. It is
      // also why the missing invalidation in PersistentWhiteListAddress::
      // DeleteObject went unnoticed: a cache that reloads every time cannot
      // be stale.
      needRefresh_ = false;
   }
}