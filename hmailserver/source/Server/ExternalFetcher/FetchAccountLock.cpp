// Copyright (c) 2026 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include ".\FetchAccountLock.h"

#include "..\Common\BO\FetchAccount.h"
#include "../Common/Persistence/PersistentFetchAccount.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   FetchAccountLock::FetchAccountLock(std::shared_ptr<FetchAccount> fetch_account) :
      fetch_account_(fetch_account)
   {
      PersistentFetchAccount::Lock(fetch_account_->GetID());
   }

   FetchAccountLock::~FetchAccountLock()
   {
      // The next try time is set first, so that an account whose fetch failed backs
      // off by its normal interval instead of being retried on the next sweep. Each
      // call is guarded on its own: a destructor must not throw, and a failure to
      // record the next try time must not be allowed to skip the unlock, which is
      // the one thing this object exists to guarantee.
      try
      {
         PersistentFetchAccount::SetNextTryTime(fetch_account_);
      }
      catch (...)
      {
      }

      try
      {
         PersistentFetchAccount::Unlock(fetch_account_->GetID());
      }
      catch (...)
      {
      }
   }
}
