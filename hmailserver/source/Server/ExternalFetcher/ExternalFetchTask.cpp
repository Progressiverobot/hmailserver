// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include ".\externalfetchtask.h"

#include "ExternalFetch.h"
#include "..\Common\BO\FetchAccount.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ExternalFetchTask::ExternalFetchTask(std::shared_ptr<FetchAccount> pFA) : 
      Task("ExternalFetchTask"),
      fetch_account_(pFA),
      lock_(pFA)
   {
   }

   ExternalFetchTask::~ExternalFetchTask(void)
   {
   }

   void 
   ExternalFetchTask::DoWork()
   {
      // Do the actual fetch. The next try time is recorded and the account unlocked
      // by lock_ when this task is destroyed - also when the fetch throws, and also
      // when the task is dropped from the queue without ever running.
      ExternalFetch oFetcher;
      oFetcher.Start(fetch_account_);
   }

}