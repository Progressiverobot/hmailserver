// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "..\Common\Threading\Task.h"

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
   };
}