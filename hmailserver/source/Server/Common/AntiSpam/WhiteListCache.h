// Copyright (c) 2013 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class WhiteListAddress;

   class WhiteListCache
   {
   public:
      WhiteListCache(void);

      
      static void SetNeedRefresh();

      bool IsWhitelisted(const String &fromAddress, const IPAddress &address);
   

   private:

      static void Refresh();
      static std::vector<std::shared_ptr<WhiteListAddress>> whitelistItems_;
      static bool needRefresh_;
   };
}