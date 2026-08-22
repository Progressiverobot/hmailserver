// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "Pop3LoginDelay.h"

#include "../Application/IniFileSettings.h"
#include "../Application/Logger.h"
#include "../Application/ErrorManager.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The table holds one entry per account that has logged in inside the delay
      // window. The cap is a backstop, not an expectation: the sweep below keeps
      // the size proportional to active clients rather than to accounts.
      const size_t kMaxEntries = 100000;

      // The sweep walks every tracked account while holding the mutex that every
      // successful POP3 logon takes, so it runs at most this often rather than on
      // every login.
      const time_t kMinSweepIntervalSeconds = 60;
   }

   String
   Pop3LoginDelay::NormalizeKey_(const String &accountAddress)
   {
      String key = accountAddress;
      key.Trim();
      key.ToLower();

      // No alias or default-domain resolution, unlike AccountLockout: this is only
      // reached with an account that authentication has already resolved, so the
      // address is canonical by construction.
      return key;
   }

   void
   Pop3LoginDelay::ReportSwallowedException_(const char *source)
   {
      // Called from a catch block on a connection thread. Anything that throws in
      // here would replace the exception just contained, so it is all inside its
      // own barrier.
      try
      {
         bool report = false;
         {
            boost::lock_guard<boost::mutex> guard(report_mutex_);
            report = !internal_error_reported_;
            internal_error_reported_ = true;
         }

         String message = "An unexpected error occurred while applying the POP3 login delay. "
                          "The login was allowed. Source: ";
         message += source;

         if (report)
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6197, "Pop3LoginDelay", message);
         else
            LOG_APPLICATION("Pop3LoginDelay - " + message);
      }
      catch (...)
      {
      }
   }

   void
   Pop3LoginDelay::Prune_(time_t now, int delaySeconds)
   {
      for (auto iter = last_login_.begin(); iter != last_login_.end(); )
      {
         // A timestamp ahead of the clock is a backwards clock step, not a login
         // from the future. Kept rather than erased, because SecondsRemainingAt
         // clamps it - erasing here would let a client through early on exactly
         // the step the clamp exists to survive.
         if (iter->second <= now && now - iter->second >= (time_t) delaySeconds)
            iter = last_login_.erase(iter);
         else
            ++iter;
      }
   }

   int
   Pop3LoginDelay::SecondsRemaining(const String &accountAddress)
   {
      // Runs inside a logon. An exception escaping to the protocol's parse loop
      // would drop the session and write a minidump; skipping one rate limit is a
      // far smaller problem, so the fallback is always to allow.
      try
      {
         int delaySeconds = IniFileSettings::Instance()->GetPop3LoginDelaySeconds();

         if (delaySeconds <= 0)
            return 0;

         return SecondsRemainingAt(accountAddress, time(nullptr), delaySeconds);
      }
      catch (...)
      {
         ReportSwallowedException_("SecondsRemaining");
         return 0;
      }
   }

   int
   Pop3LoginDelay::SecondsRemainingAt(const String &accountAddress, time_t now, int delaySeconds)
   {
      if (delaySeconds <= 0)
         return 0;

      String key = NormalizeKey_(accountAddress);
      if (key.IsEmpty())
         return 0;

      boost::lock_guard<boost::mutex> guard(mutex_);

      auto iter = last_login_.find(key);
      if (iter == last_login_.end())
         return 0;

      // A timestamp stamped ahead of the clock - a backwards step since the last
      // login - must not hold the account out for longer than the delay itself.
      if (iter->second > now)
         iter->second = now;

      time_t elapsed = now - iter->second;

      if (elapsed >= (time_t) delaySeconds)
         return 0;

      return (int) (delaySeconds - elapsed);
   }

   void
   Pop3LoginDelay::RecordLogin(const String &accountAddress)
   {
      try
      {
         if (IniFileSettings::Instance()->GetPop3LoginDelaySeconds() <= 0)
            return;

         RecordLoginAt(accountAddress, time(nullptr));
      }
      catch (...)
      {
         ReportSwallowedException_("RecordLogin");
      }
   }

   void
   Pop3LoginDelay::RecordLoginAt(const String &accountAddress, time_t now)
   {
      String key = NormalizeKey_(accountAddress);
      if (key.IsEmpty())
         return;

      bool reportTableFull = false;

      {
         boost::lock_guard<boost::mutex> guard(mutex_);

         auto iter = last_login_.find(key);

         if (iter != last_login_.end())
         {
            iter->second = now;
         }
         else
         {
            if (last_sweep_time_ > now || now - last_sweep_time_ >= kMinSweepIntervalSeconds)
            {
               last_sweep_time_ = now;
               Prune_(now, IniFileSettings::Instance()->GetPop3LoginDelaySeconds());
            }

            if (last_login_.size() >= kMaxEntries)
            {
               // Fail open. Refusing to record a login means that account is not
               // rate limited, which is a far better outcome than refusing the
               // login itself - but the operator needs to know the control has
               // stopped applying to new accounts.
               reportTableFull = !table_full_reported_;
               table_full_reported_ = true;
            }
            else
            {
               last_login_[key] = now;
            }
         }
      }

      if (reportTableFull)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6198, "Pop3LoginDelay::RecordLoginAt",
            "The POP3 login-delay table is full, so the delay is no longer being applied to accounts that "
            "are not already in it. Existing entries are unaffected.");
      }
   }
}
