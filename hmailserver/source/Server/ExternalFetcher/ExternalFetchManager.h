// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "..\Common\Threading\Task.h"
#include "../Common/Util/Event.h"

namespace HM
{
   class ClientInfo;
   class FetchAccount;
   class FetchAccounts;

   class ExternalFetchManager : public Task
   {
   public:
      ExternalFetchManager(void);
      ~ExternalFetchManager(void);

      void DoWork();
      
      void SetCheckNow();
   private:

      bool FetchIsAllowed_(std::shared_ptr<FetchAccount> pFA);

      std::shared_ptr<FetchAccounts> fetch_accounts_;

      size_t queue_id_;
      const String queue_name_;

      Event check_now_;

      // Whether fetching is currently paused for lack of disk space, so the
      // pause and the resumption are each logged once rather than once a minute
      // for as long as the condition lasts.
      bool fetch_paused_for_disk_space_ = false;
   };
}