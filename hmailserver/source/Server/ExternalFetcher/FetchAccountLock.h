// Copyright (c) 2026 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class FetchAccount;

   // Locks a fetch account for the lifetime of this object. A locked account is
   // skipped by ExternalFetchManager's pending list, so an account that is never
   // unlocked is never fetched again until the service restarts and UnlockAll runs -
   // silently, because the last session in the POP3 log usually looks complete.
   //
   // Until upstream #603 the lock was taken by the manager before the fetch task was
   // queued and released by the last statement of ExternalFetchTask::DoWork, with
   // nothing owning it in between: an exception inside the fetch, a database error
   // in SetNextTryTime (which ran before the unlock) or a work queue torn down with
   // the task still queued each left the account locked. The task now holds one of
   // these as its last member, so the account is unlocked whether DoWork returns,
   // throws or never runs at all.
   class FetchAccountLock
   {
   public:
      explicit FetchAccountLock(std::shared_ptr<FetchAccount> fetch_account);
      ~FetchAccountLock();

      FetchAccountLock(const FetchAccountLock &) = delete;
      FetchAccountLock &operator=(const FetchAccountLock &) = delete;

   private:

      std::shared_ptr<FetchAccount> fetch_account_;
   };
}
