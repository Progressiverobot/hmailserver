// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Singleton.h"

#include <deque>
#include <map>

namespace HM
{
   // The effective per-account outbound sending ceiling. Both counts are "per
   // period"; 0 means unlimited, which is the shipped default for both, so the
   // whole mechanism is inert until an administrator configures it.
   class AccountSendingLimits
   {
   public:
      int max_messages = 0;
      int max_recipients = 0;
      int period_hours = 24;

      bool IsEnabled() const { return max_messages > 0 || max_recipients > 0; }
   };

   // Outcome of an account-quota consume. Deliberately not a bool: the caller
   // needs to say which ceiling was reached, since the two get different
   // enhanced status codes.
   enum class AccountQuotaResult
   {
      QuotaAllowed = 0,
      QuotaMessageLimitReached = 1,
      QuotaRecipientLimitReached = 2
   };

   // Thread-safe, in-memory sliding-window rate limiter.
   //
   // Two independent facilities live here, with separate locks so the SMTP accept
   // path and the outbound delivery threads do not contend with each other:
   //
   // 1. TryConsume / Clear - the original per-minute sliding window keyed by an
   //    opaque bucket name (a source IP address or an outbound destination
   //    domain). Each bucket keeps the timestamps of recent events; TryConsume
   //    returns false when registering a new event would exceed maxPerMinute
   //    events within the trailing 60-second window.
   //
   //    A maxPerMinute of 0 (or negative) means "unlimited" and always succeeds
   //    without recording anything, so the limiter is a no-op until configured.
   //
   // 2. The per-account outbound sending quota (TryConsumeAccountMessage /
   //    TryConsumeAccountRecipients), which counts messages and envelope
   //    recipients per authenticated account over a period measured in hours -
   //    a day by default. This is a security control rather than a feature: a
   //    single compromised account with no ceiling is the usual route to a
   //    blacklisted IP address, and the blast radius is otherwise unbounded.
   //
   //    Counters are bucketed (the period is divided into equal sub-buckets)
   //    rather than storing one timestamp per event, so a large recipient count
   //    cannot grow memory without bound. The effective window is therefore
   //    between one period and one period plus one bucket - slightly stricter
   //    than nominal, which is the safe direction for a ceiling.
   //
   //    Counters are persisted to a small state file in the data directory so an
   //    ordinary restart - a nightly reboot, a service recycle - does not hand
   //    back a fresh budget. No database schema change is involved.
   //
   //    Be precise about how much that buys: the file is rewritten at most once
   //    per StateSaveIntervalSeconds (ten by default) and only from a consume
   //    call, so an abrupt stop loses up to one interval's worth of counting.
   //    FlushAccountUsage exists to close that at a clean shutdown but is not
   //    wired to one yet. The persistence therefore blunts a restart; it does
   //    not make one free of cost to the limiter, and this is deliberately not
   //    hardened against an attacker who can restart the service at will -
   //    anybody who can do that has better options than sending more mail.
   //
   //    The state file also has a size ceiling, because it is read whole into
   //    memory on the first MAIL FROM after a start. The writer stops at the same
   //    ceiling, so a file this code wrote is always a file it can read back; on
   //    an installation large enough to reach it, the accounts past the ceiling
   //    lose their counters across a restart and the truncation is reported. The
   //    in-memory counters are never affected by it.
   class RateLimiter : public Singleton<RateLimiter>
   {
   public:
      RateLimiter();
      virtual ~RateLimiter();

      bool TryConsume(const String &key, int maxPerMinute);

      // TryConsume with the clock supplied rather than read from time(0). This is
      // the whole of TryConsume - the same map, the same lock, the same pruning -
      // and exists because the window's behaviour across a clock step cannot be
      // tested otherwise, which is how a bucket that could never expire went
      // unnoticed. Production code calls TryConsume.
      bool TryConsumeAt(const String &key, int maxPerMinute, time_t now);

      // Removes all per-minute buckets (used by self-tests and on configuration
      // reload). Deliberately does NOT touch the per-account quota counters:
      // those are persistent state, and wiping them would hand a compromised
      // account a fresh budget.
      void Clear();

      // Effective limits for an authenticated account: the global values from
      // [SendingLimits] in hMailServer.ini, overridden per address by an entry in
      // [SendingLimitsOverrides]. The parsed settings are cached and only re-read
      // when the ini file's timestamp changes; the timestamp is checked by stat'ing
      // the file, at most once every couple of seconds. So: cheap per call, but not
      // free, and it does touch the file system.
      AccountSendingLimits GetAccountLimits(const String &accountAddress);

      // Records one message for the account. Call at MAIL FROM, once the
      // authenticated account is known.
      //
      // Every member of the per-account quota API - GetAccountLimits,
      // TryConsumeAccountMessage, TryConsumeAccountRecipients, GetAccountUsage,
      // ClearAccountUsage, FlushAccountUsage - is reached from an SMTP connection
      // thread and none of them throws: an unexpected failure is reported once and
      // the command is allowed. Refusing mail because of an internal error - or
      // worse, letting an exception reach TCPConnection, which reports HM5136,
      // drops the session and rethrows so the worker writes a minidump - would be
      // a worse outcome than not counting one message. Keep it that way when
      // editing; these members read files and allocate.
      //
      // TryConsume and Clear above are older and are not wrapped this way.
      AccountQuotaResult TryConsumeAccountMessage(const String &accountAddress, const AccountSendingLimits &limits);

      // Records recipientCount further envelope recipients for the account. Call
      // as recipients accumulate at RCPT TO, so one message addressed to
      // thousands of recipients cannot slip past a message-count ceiling.
      AccountQuotaResult TryConsumeAccountRecipients(const String &accountAddress, const AccountSendingLimits &limits, int recipientCount);

      // Current usage within the period. Returns false when the account has no
      // counters at all. Used for logging.
      bool GetAccountUsage(const String &accountAddress, int &messages, int &recipients);

      // Drops all per-account counters, including the persisted state file.
      // Administrative / self-test use only.
      void ClearAccountUsage();

      // Writes the counters out now rather than waiting for the next interval.
      // This is the one caller allowed to bypass the write throttle, so it must
      // stay a shutdown / administrative call: never invoke it from a connection
      // thread. Not yet wired to service shutdown.
      void FlushAccountUsage();

   private:

      // One sub-bucket of the sliding period.
      class UsageBucket
      {
      public:
         time_t start = 0;
         int messages = 0;
         int recipients = 0;
      };

      // Oldest bucket first.
      typedef std::deque<UsageBucket> UsageBuckets;

      void PruneExpired_(std::deque<time_t> &events, time_t now) const;

      static String NormalizeAccountKey_(const String &accountAddress);
      static time_t GetPeriodSeconds_(const AccountSendingLimits &limits);
      static time_t GetBucketSeconds_(time_t periodSeconds);
      static void PruneUsage_(UsageBuckets &buckets, time_t now, time_t periodSeconds);
      static void SumUsage_(const UsageBuckets &buckets, int &messages, int &recipients);
      static int SaturatingAdd_(int current, int addition);

      // account_mutex_ must be held for every one of these.
      void EnsureUsageLoaded_();
      void LoadUsageFile_();   // the body of EnsureUsageLoaded_; may throw, the caller is the barrier
      String SerializeUsage_(time_t now, bool &truncated) const;
      AccountQuotaResult CheckAndConsume_(const String &key, int addMessages, int addRecipients, const AccountSendingLimits &limits, time_t now);
      void DropDeadAccounts_(time_t now);
      void QueueReport_(int code, const String &source, const String &text);

      // Takes account_mutex_ itself, briefly, and must therefore be called without
      // it: it emits whatever QueueReport_ recorded, and reporting runs script.
      // See the comment on pending_report_code_.
      void EmitQueuedReport_();

      // Serializes under account_mutex_ and then writes the file with the lock
      // released, under a try-lock. Be precise about what that buys: the thread
      // that wins the try-lock does do the write itself, synchronously; what the
      // try-lock prevents is a second connection thread queueing behind it. The
      // interval throttle is what keeps that to one thread per interval.
      //
      // force is for FlushAccountUsage only; a connection thread must always pass
      // false so the write interval is respected.
      void SaveUsage_(bool force);
      static String GetStateFileName_();

      // 64-bit file size, or false when the file does not exist. Deliberately not
      // FileUtilities::FileSize, which narrows the size to int.
      static bool GetFileSize64_(const String &fileName, unsigned __int64 &size);

      // Last-resort handler for the catch(...) around each public entry point.
      // Reported once, then only logged. Never throws.
      void ReportSwallowedException_(const char *source);

      // Re-reads [SendingLimits] when the ini file has changed. Does its file I/O
      // without holding settings_mutex_.
      void MaybeRefreshSettings_();
      void LoadSettings_();
      static bool ParseOverrideValue_(const String &value, AccountSendingLimits &limits);

      boost::recursive_mutex mutex_;
      std::map<String, std::deque<time_t>> buckets_;

      // Per-account quota state.
      boost::mutex account_mutex_;
      std::map<String, UsageBuckets> account_usage_;
      bool usage_loaded_ = false;
      time_t last_save_time_ = 0;

      // Set when a counter actually changed. A refusal changes nothing, so it
      // must never cause the map to be serialized: that is a full walk of every
      // tracked account under account_mutex_, and an over-quota credential would
      // otherwise buy one of those per rejected MAIL FROM / RCPT TO.
      bool usage_dirty_ = false;

      bool account_map_full_reported_ = false;
      bool state_file_reported_ = false;
      bool state_file_truncated_reported_ = false;

      // ErrorManager::ReportError fires the OnError script event synchronously -
      // arbitrary administrator code - so it is never called with account_mutex_
      // held on a connection thread. Code under the lock records the diagnostic
      // here and the entry point drains it afterwards. All three fields are
      // guarded by account_mutex_; only the first queued report is kept, which is
      // enough because every producer is already once-per-process.
      int pending_report_code_ = 0;
      String pending_report_source_;
      String pending_report_text_;

      // Cached [SendingLimits] configuration. settings_mutex_ guards the cached
      // values and is only ever held for a map lookup; settings_refresh_mutex_ is
      // held across the ini read, so at most one thread reads the file and the
      // others carry on with the values they already have.
      boost::mutex settings_mutex_;
      boost::mutex settings_refresh_mutex_;
      AccountSendingLimits global_limits_;
      std::map<String, AccountSendingLimits> overrides_;
      int state_save_interval_seconds_ = 10;
      bool settings_loaded_ = false;
      time_t settings_checked_time_ = 0;
      ULONGLONG settings_file_write_time_ = 0;
      bool override_parse_error_reported_ = false;

      // Held only while the state file is written; never held together with
      // account_mutex_, in either order. save_failure_reported_ is only touched
      // under it.
      boost::mutex save_mutex_;
      bool save_failure_reported_ = false;

      // Guards internal_error_reported_ only. Never held with any other lock.
      boost::mutex report_mutex_;
      bool internal_error_reported_ = false;
   };
}
