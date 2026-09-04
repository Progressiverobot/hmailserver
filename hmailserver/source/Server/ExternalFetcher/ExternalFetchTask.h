// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "..\Common\Threading\Task.h"
#include ".\FetchAccountLock.h"

namespace HM
{
   class FetchAccount;

   class ExternalFetchTask : public Task
   {
   public:
      ExternalFetchTask(std::shared_ptr<FetchAccount> pFA);
      ~ExternalFetchTask(void);

      virtual void DoWork();

   private:

      std::shared_ptr<FetchAccount> fetch_account_;

      // Declared last, so that the account stays locked until everything else the
      // task owns has been destroyed. See FetchAccountLock.h for why the task, and
      // not the manager, holds the lock.
      FetchAccountLock lock_;
   };
}