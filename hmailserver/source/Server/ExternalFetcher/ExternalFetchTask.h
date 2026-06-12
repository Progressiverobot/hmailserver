// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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