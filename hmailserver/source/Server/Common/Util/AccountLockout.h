// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Per-account (strictly: per-username) failed-authentication lockout.
//
// The per-IP auto-ban cannot see a distributed attack: a botnet spending one
// guess per address per account never crosses any single address's threshold,
// so one mailbox absorbs unlimited guesses from a server whose brute-force
// protection is on. This counts by the NAME the attacker is guessing at,
// which is the one thing a distributed attack cannot vary.
//
// The name, deliberately not the account: failures against a nonexistent
// username lock that string exactly as a real account's would. Counting only
// real accounts would make the lockout's behaviour an existence oracle.
//
// It is the CANONICAL name, though. The authentication paths resolve domain
// aliases and apply the default domain before they match an account, so
// keying on the string exactly as it arrived would give one mailbox a
// separate bucket per spelling - "victim", "victim@example.com" and every
// alias of it - and an attacker a fresh threshold for each, plus a trivial
// evasion by rotating them. NormalizeKey_ applies the same two
// transformations. Both are string and configuration lookups that never ask
// whether an account exists, so the key stays clear of the oracle that
// keying on the account itself would be.
//
// A locked name fails authentication without the password ever being checked,
// and the refusal is the ordinary invalid-credentials reply, so nothing an
// attacker READS distinguishes a locked name from a wrong password.
//
// Be honest about what the clock still says. Skipping the check also skips a
// PBKDF2 or Argon2id verification, so a locked name answers in roughly the
// time a nonexistent one does instead of the tens of milliseconds an existing
// one costs: lock state is measurable by timing. That is accepted
// deliberately. Paying the KDF to hide it would hand an attacker a
// memory-hard amplifier - up to 19 MiB of hashing per guess, on demand,
// against a name they locked themselves - and sleeping to equalise would park
// a connection thread, which is precisely why login tarpitting is not
// implemented in this server. The account-existence oracle that predates this
// class (a nonexistent name never reaches the KDF either) is neither widened
// nor narrowed by it.
//
// The cost to legitimate users is deliberate as well: someone arriving
// mid-lockout sees a wrong-password error for up to the lockout duration, and
// cannot clear it by typing the right password - the check runs first. What is
// NOT acceptable is charging that person's address for it, so the refusal is
// kept out of the per-IP auto-ban accounting; see AccountLogon::Logon.
//
// In-memory, like the sibling stores: a restart clears the counters, and an
// attacker who can restart the service has better options than more guesses.
//
// Every public entry point is called from a connection thread and none of them
// throws: the fallback is always to allow, because failing to lock one name is
// a far smaller problem than an exception reaching TCPConnection, which reports
// HM5136, drops the session and rethrows so the worker writes a minidump. It
// would also skip the per-IP auto-ban accounting that follows this class on the
// failed-logon path. Keep it that way when editing; these members allocate.
// (RateLimiter carries the same barrier for the same reason.)
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Singleton.h"

#include <deque>
#include <map>

namespace HM
{
   class AccountLockout : public Singleton<AccountLockout>
   {
   public:
      bool IsLockedOut(const String &username);
      void RecordFailure(const String &username);

      // A successful authentication clears the name's counters: the password
      // was right, so the trailing failures were typos, and leaving them
      // standing would let an attacker's groundwork lock a user out after
      // their own successful logon. Every mechanism that verifies credentials
      // itself must call this on success - SCRAM does, from each protocol's
      // finish handler - or the clearing only happens for password logons.
      void RecordSuccess(const String &username);

      // The clock-and-config-supplied twins, public for the self-tests: expiry
      // across a clock step cannot be tested against time(0), and the /Test
      // process runs on an ini with no lockout settings, so the configuration
      // travels as parameters exactly as RateLimiter::TryConsumeAt's does.
      // Production code calls the pair above, which read the ini and carry the
      // exception barrier.
      bool IsLockedOutAt(const String &username, time_t now, time_t lockoutSeconds);
      void RecordFailureAt(const String &username, time_t now, int threshold,
                           time_t windowSeconds, time_t lockoutSeconds);

   private:

      struct Entry
      {
         std::deque<time_t> failures;
         time_t locked_until = 0;
      };

      static String NormalizeKey_(const String &username);
      void Prune_(Entry &entry, time_t now, time_t windowSeconds) const;

      // Last-resort handler for the catch(...) around each public entry point.
      // Reported once, then only logged. Never throws.
      void ReportSwallowedException_(const char *source);

      boost::mutex mutex_;
      std::map<String, Entry> entries_;

      // Guarded by mutex_. The retirement sweep walks every tracked name, and
      // the mutex is the one every authentication takes, so the sweep is
      // throttled rather than run per failed attempt - see the comment at its
      // call site.
      time_t last_sweep_time_ = 0;
      bool table_full_reported_ = false;

      // Guards internal_error_reported_ only. Never held with mutex_.
      boost::mutex report_mutex_;
      bool internal_error_reported_ = false;
   };
}
