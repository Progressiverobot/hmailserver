// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// See AccountLockout.h.

#include "StdAfx.h"

#include "AccountLockout.h"

#include "../Application/DefaultDomain.h"
#include "../Application/ObjectCache.h"
#include "../BO/DomainAliases.h"

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // Usernames are attacker-chosen, so the map is capped. On a full map new
   // names are not tracked - the lockout fails open for them - because the
   // alternative, evicting tracked names, would let an attacker spray random
   // names to flush the very entry they are attacking.
   static const size_t kMaxEntries = 100000;

   // Bound on the timestamps kept per name, however the threshold is set.
   static const size_t kMaxFailuresPerName = 1000;

   // The retirement sweep walks every tracked name while holding the mutex that
   // every authentication on every protocol must take, so it is rate-limited
   // rather than run once per failed attempt. See its call site.
   static const time_t kMinSweepIntervalSeconds = 60;

   String
   AccountLockout::NormalizeKey_(const String &username)
   {
      String key = username;
      key.Trim();
      key.ToLower();

      if (key.IsEmpty())
         return key;

      // Canonicalize exactly as the authentication paths do before they match an
      // account - aliases first, then the default domain - so that one mailbox is
      // one bucket however the client spelled it. Without this the lock is evaded
      // by alternating "victim" and "victim@example.com", each spelling carrying
      // its own untouched threshold. See the header for why this stays free of an
      // existence oracle: neither transformation asks whether an account exists.
      try
      {
         std::shared_ptr<DomainAliases> aliases = ObjectCache::Instance()->GetDomainAliases();
         if (aliases)
            key = aliases->ApplyAliasesOnAddress(key);

         key = DefaultDomain::ApplyDefaultDomain(key);
         key.ToLower();
      }
      catch (...)
      {
         // The caches are not available - very early startup, or a failure inside
         // the alias collection. The plain lowercased name is the safe fallback:
         // it costs strictness for the spellings that would have canonicalized,
         // and can never count a failure against a different mailbox.
      }

      return key;
   }

   void
   AccountLockout::Prune_(Entry &entry, time_t now, time_t windowSeconds) const
   {
      // A failure stamped in the future can never expire; pull it back to now
      // - the same clock-step guard every sibling store carries.
      for (time_t &failure : entry.failures)
      {
         if (failure > now)
            failure = now;
      }

      while (!entry.failures.empty() && entry.failures.front() <= now - windowSeconds)
         entry.failures.pop_front();

      if (entry.locked_until > 0 && entry.locked_until <= now)
      {
         entry.locked_until = 0;

         // The window starts again from zero when a lock expires. Left standing,
         // failures recorded before the lock are still inside the window, so the
         // very next failure re-crosses the threshold and the name locks again on
         // a single guess - which is how a trickle of attempts could hold a
         // mailbox locked indefinitely. A re-lock must cost a fresh threshold.
         entry.failures.clear();
      }
   }

   void
   AccountLockout::ReportSwallowedException_(const char *source)
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

         String message = "An unexpected error occurred while applying the per-account "
                          "authentication lockout. The lockout was not applied to this attempt. Source: ";
         message += source;

         if (report)
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6191, "AccountLockout", message);
         else
            LOG_APPLICATION("AccountLockout - " + message);
      }
      catch (...)
      {
      }
   }

   bool
   AccountLockout::IsLockedOut(const String &username)
   {
      // Runs inside a logon. An exception escaping to the protocol's parse loop
      // would drop the session and write a minidump; not locking one name is a
      // far smaller problem, so the fallback is always to allow.
      try
      {
         if (IniFileSettings::Instance()->GetAccountLockoutThreshold() <= 0)
            return false;

         return IsLockedOutAt(username, time(nullptr),
            (time_t) IniFileSettings::Instance()->GetAccountLockoutMinutes() * 60);
      }
      catch (...)
      {
         ReportSwallowedException_("IsLockedOut");
         return false;
      }
   }

   bool
   AccountLockout::IsLockedOutAt(const String &username, time_t now, time_t lockoutSeconds)
   {
      String key = NormalizeKey_(username);
      if (key.IsEmpty())
         return false;

      boost::lock_guard<boost::mutex> guard(mutex_);

      auto iter = entries_.find(key);
      if (iter == entries_.end())
         return false;

      // A lockout stamped ahead of the clock (a backwards step after it was
      // set) must not outlive its nominal duration by the size of the step.
      if (iter->second.locked_until > now + lockoutSeconds)
         iter->second.locked_until = now + lockoutSeconds;

      return iter->second.locked_until > now;
   }

   void
   AccountLockout::RecordFailure(const String &username)
   {
      try
      {
         int threshold = IniFileSettings::Instance()->GetAccountLockoutThreshold();
         if (threshold <= 0)
            return;

         RecordFailureAt(username, time(nullptr), threshold,
            (time_t) IniFileSettings::Instance()->GetAccountLockoutWindowMinutes() * 60,
            (time_t) IniFileSettings::Instance()->GetAccountLockoutMinutes() * 60);
      }
      catch (...)
      {
         // Contained rather than propagated for the reason IsLockedOut states -
         // and one more: this runs at the top of AccountLogon::RegisterFailedLogin,
         // so an exception here would also skip the per-IP auto-ban accounting
         // that follows it, disabling the older control as well as this one.
         ReportSwallowedException_("RecordFailure");
      }
   }

   void
   AccountLockout::RecordFailureAt(const String &username, time_t now, int threshold,
                                   time_t windowSeconds, time_t lockoutSeconds)
   {
      if (threshold <= 0)
         return;

      String key = NormalizeKey_(username);
      if (key.IsEmpty())
         return;

      bool locked = false;
      bool reportTableFull = false;

      {
         boost::lock_guard<boost::mutex> guard(mutex_);

         auto iter = entries_.find(key);
         if (iter == entries_.end())
         {
            if (entries_.size() >= kMaxEntries)
            {
               // Retire names whose window has emptied - but at most once a
               // minute. This walk is O(tracked names) and holds the mutex every
               // authentication has to take, and the keys are attacker-chosen: an
               // attacker who has filled the table would otherwise make every
               // further failed attempt walk 100,000 entries with all
               // authentication on the server queued behind it. Throttled, the
               // worst case is one walk per minute.
               //
               // windowSeconds is the caller's, which is safe here only because
               // the window is a single global setting; if it ever becomes
               // per-account, sweep with the longest supported window instead, or
               // this will retire counters that are still inside someone else's.
               if (last_sweep_time_ > now || now - last_sweep_time_ >= kMinSweepIntervalSeconds)
               {
                  last_sweep_time_ = now;

                  for (auto cleanup = entries_.begin(); cleanup != entries_.end(); )
                  {
                     Prune_(cleanup->second, now, windowSeconds);

                     if (cleanup->second.failures.empty() && cleanup->second.locked_until == 0)
                        cleanup = entries_.erase(cleanup);
                     else
                        ++cleanup;
                  }
               }
            }

            if (entries_.size() >= kMaxEntries)
            {
               // Fail open: refusing authentication because an internal table is
               // full would be a worse outcome than not locking one name. Not
               // silently, though - a full table means either an attack in
               // progress or an installation this cap does not fit, and in both
               // cases the operator needs to know the control has stopped
               // applying to new names.
               reportTableFull = !table_full_reported_;
               table_full_reported_ = true;
            }
            else
            {
               iter = entries_.insert(std::make_pair(key, Entry())).first;
            }
         }

         if (iter != entries_.end())
         {
            Entry &entry = iter->second;
            Prune_(entry, now, windowSeconds);

            // An attempt against an already-locked name was refused without the
            // password being checked, so it measures nothing and is not counted.
            // This is the other half of the expiry fix in Prune_: counting these
            // is what let an attacker maintain a permanent lock with one guess a
            // minute, and it is what makes the claim in AccountLogon::Logon true -
            // hammering a locked name really does extend nothing.
            if (entry.locked_until <= now)
            {
               entry.failures.push_back(now);

               while (entry.failures.size() > kMaxFailuresPerName)
                  entry.failures.pop_front();

               if ((int) entry.failures.size() >= threshold)
               {
                  entry.locked_until = now + lockoutSeconds;
                  entry.failures.clear();
                  locked = true;
               }
            }
         }
      }

      // Diagnostics with the lock RELEASED. LOG_APPLICATION takes the
      // process-wide logger mutex and stats, opens, writes and closes the log
      // file; ErrorManager::ReportError fires the OnError script event
      // synchronously, which runs arbitrary administrator code. Neither belongs
      // on the critical path of every authentication, and the second must never
      // run under a lock a script event could re-enter. RateLimiter separates
      // them for the same reasons.
      if (locked)
      {
         // APPLICATION rather than an error record: reaching a ceiling the
         // administrator configured is an event, not a server fault - and on a
         // server under distributed attack, one line per locked name is the
         // observability the per-IP ban never had.
         LOG_APPLICATION("AccountLockout - Authentication for '" + NormalizeKey_(username) + "' is locked for " +
            StringParser::IntToString((__int64) (lockoutSeconds / 60)) +
            " minute(s) after repeated failures. The lockout is per-name and independent of source address.");
      }

      if (reportTableFull)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6190, "AccountLockout::RecordFailureAt",
            "The per-account authentication lockout table is full; the lockout is no longer being applied to "
            "further names. This is either a brute-force attack spraying user names or an installation larger "
            "than the table's fixed capacity. The per-IP auto-ban is unaffected.");
      }
   }

   void
   AccountLockout::RecordSuccess(const String &username)
   {
      // Wrapped like the others, and this one matters most: it runs on the
      // SUCCESS path, so an exception escaping here would fail an authentication
      // that had already been proved correct.
      try
      {
         String key = NormalizeKey_(username);
         if (key.IsEmpty())
            return;

         boost::lock_guard<boost::mutex> guard(mutex_);
         entries_.erase(key);
      }
      catch (...)
      {
         ReportSwallowedException_("RecordSuccess");
      }
   }
}
