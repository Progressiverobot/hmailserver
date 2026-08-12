// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "RateLimiter.h"

#include <ctime>
#include <limits>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // Length of the sliding window, in seconds.
   static const time_t kWindowSeconds = 60;

   // Upper bound on the number of distinct buckets retained. The submission and
   // outbound paths are keyed by remote IP / destination domain, both of which
   // are naturally bounded in normal operation; this cap keeps a hostile peer
   // from growing the map without limit.
   static const size_t kMaxBuckets = 100000;

   // --- Per-account sending quota -------------------------------------------

   // The ini section holding the global ceilings, and the section holding
   // per-address overrides ("address=messages:recipients[:hours]").
   static const TCHAR *kSettingsSection = _T("SendingLimits");
   static const TCHAR *kOverridesSection = _T("SendingLimitsOverrides");

   // File in the data directory that carries the counters across a restart.
   static const TCHAR *kStateFileName = _T("hmailserver_sendinglimits.dat");

   // Version marker on the first line of the state file.
   static const TCHAR *kStateFileVersion = _T("hMailServerSendingLimits 1");

   // The period is divided into this many sub-buckets. Counting per bucket rather
   // than storing one timestamp per event keeps memory bounded no matter how many
   // recipients an account sends to.
   static const time_t kBucketsPerPeriod = 24;

   // Never let a bucket be shorter than this, so a very short period cannot turn
   // into a long deque of tiny buckets.
   static const time_t kMinBucketSeconds = 60;

   // Hard cap on buckets kept per account (a period's worth, plus slack for the
   // one straddling the window edge and for a period that was shortened).
   static const size_t kMaxBucketsPerAccount = 40;

   // Accepted range for the configured period.
   static const int kMinPeriodHours = 1;
   static const int kMaxPeriodHours = 168;

   // Cap on the number of accounts tracked. Only authenticated senders get an
   // entry, so an unauthenticated attacker cannot grow this; it is a safety net.
   static const size_t kMaxAccountEntries = 100000;

   // How often the ini file is stat'ed to see whether the settings changed.
   static const time_t kSettingsRecheckSeconds = 2;

   // Bounds on the configured state-file write interval.
   static const int kMinSaveIntervalSeconds = 1;
   static const int kMaxSaveIntervalSeconds = 3600;
   static const int kDefaultSaveIntervalSeconds = 10;

   // A state file larger than this is treated as corrupt rather than read: it is
   // read whole into memory, so the bound is what keeps a hand-placed or damaged
   // file from turning the first MAIL FROM into a huge allocation.
   static const unsigned __int64 kMaxStateFileBytes = 4 * 1024 * 1024;

   // ...and this is the matching bound at the writing end, so that a file we
   // wrote is always a file we can read back. Without it a busy installation
   // would serialize past the cap above and then, on the next start, have every
   // account's counters discarded as "implausibly large" - the state file would
   // silently stop working at exactly the scale where it matters most.
   //
   // The file is UTF-16 with a BOM: two bytes per character plus a little slack.
   static const int kMaxStateFileChars = (int) (kMaxStateFileBytes / 2) - 16;

   RateLimiter::RateLimiter()
   {

   }

   RateLimiter::~RateLimiter()
   {

   }

   void
   RateLimiter::PruneExpired_(std::deque<time_t> &events, time_t now) const
   {
      while (!events.empty() && events.front() <= now - kWindowSeconds)
         events.pop_front();
   }

   bool
   RateLimiter::TryConsume(const String &key, int maxPerMinute)
   {
      if (maxPerMinute <= 0)
         return true;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      time_t now = time(0);

      auto it = buckets_.find(key);
      if (it == buckets_.end())
      {
         if (buckets_.size() >= kMaxBuckets)
         {
            // Drop any already-empty buckets before refusing to add a new one.
            for (auto cleanup = buckets_.begin(); cleanup != buckets_.end(); )
            {
               PruneExpired_(cleanup->second, now);
               if (cleanup->second.empty())
                  cleanup = buckets_.erase(cleanup);
               else
                  ++cleanup;
            }
         }

         it = buckets_.insert(std::make_pair(key, std::deque<time_t>())).first;
      }

      std::deque<time_t> &events = it->second;
      PruneExpired_(events, now);

      if ((int) events.size() >= maxPerMinute)
         return false;

      events.push_back(now);
      return true;
   }

   void
   RateLimiter::Clear()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      buckets_.clear();
   }

   // -------------------------------------------------------------------------
   // Per-account sending quota
   // -------------------------------------------------------------------------

   String
   RateLimiter::NormalizeAccountKey_(const String &accountAddress)
   {
      String key = accountAddress;
      key.Trim();
      key.ToLower();
      return key;
   }

   time_t
   RateLimiter::GetPeriodSeconds_(const AccountSendingLimits &limits)
   {
      int hours = limits.period_hours;
      if (hours < kMinPeriodHours)
         hours = kMinPeriodHours;
      if (hours > kMaxPeriodHours)
         hours = kMaxPeriodHours;

      return (time_t) hours * 3600;
   }

   time_t
   RateLimiter::GetBucketSeconds_(time_t periodSeconds)
   {
      time_t bucketSeconds = periodSeconds / kBucketsPerPeriod;
      if (bucketSeconds < kMinBucketSeconds)
         bucketSeconds = kMinBucketSeconds;

      return bucketSeconds;
   }

   void
   RateLimiter::PruneUsage_(UsageBuckets &buckets, time_t now, time_t periodSeconds)
   {
      // A bucket dated in the future can never satisfy the test below, so it would
      // never be retired and the account's counters would never expire - a 452 for
      // good, which is an account lockout. Pull it back to now instead: the count
      // is kept and it ages out normally one period from here. The only ways to
      // get one are a clock that jumped forward and was then put back, and a state
      // file written before such a jump.
      for (UsageBucket &bucket : buckets)
      {
         if (bucket.start > now)
            bucket.start = now;
      }

      // A bucket is dropped once its start is a whole period behind us, so the
      // effective window is between one period and one period plus one bucket.
      // Slightly longer than nominal is the safe direction for a ceiling.
      while (!buckets.empty() && buckets.front().start + periodSeconds <= now)
         buckets.pop_front();

      // Belt and braces: bound the deque however it was filled.
      while (buckets.size() > kMaxBucketsPerAccount)
         buckets.pop_front();
   }

   void
   RateLimiter::SumUsage_(const UsageBuckets &buckets, int &messages, int &recipients)
   {
      __int64 totalMessages = 0;
      __int64 totalRecipients = 0;

      for (const UsageBucket &bucket : buckets)
      {
         totalMessages += bucket.messages;
         totalRecipients += bucket.recipients;
      }

      const __int64 intMax = (__int64) (std::numeric_limits<int>::max)();

      messages = (int) (totalMessages > intMax ? intMax : totalMessages);
      recipients = (int) (totalRecipients > intMax ? intMax : totalRecipients);
   }

   int
   RateLimiter::SaturatingAdd_(int current, int addition)
   {
      if (addition <= 0)
         return current;

      const int intMax = (std::numeric_limits<int>::max)();

      if (current > intMax - addition)
         return intMax;

      return current + addition;
   }

   String
   RateLimiter::GetStateFileName_()
   {
      return FileUtilities::Combine(IniFileSettings::Instance()->GetDataDirectory(), kStateFileName);
   }

   bool
   RateLimiter::GetFileSize64_(const String &fileName, unsigned __int64 &size)
   {
      size = 0;

      WIN32_FILE_ATTRIBUTE_DATA attributes = {};
      if (!GetFileAttributesEx(fileName.c_str(), GetFileExInfoStandard, &attributes))
         return false;

      size = ((unsigned __int64) attributes.nFileSizeHigh << 32) | (unsigned __int64) attributes.nFileSizeLow;
      return true;
   }

   void
   RateLimiter::QueueReport_(int code, const String &source, const String &text)
   {
      if (pending_report_code_ != 0)
         return;

      pending_report_code_ = code;
      pending_report_source_ = source;
      pending_report_text_ = text;
   }

   void
   RateLimiter::EmitQueuedReport_()
   {
      int code = 0;
      String source;
      String text;

      {
         boost::lock_guard<boost::mutex> guard(account_mutex_);

         if (pending_report_code_ == 0)
            return;

         code = pending_report_code_;
         source = pending_report_source_;
         text = pending_report_text_;

         pending_report_code_ = 0;
         pending_report_source_.Empty();
         pending_report_text_.Empty();
      }

      ErrorManager::Instance()->ReportError(ErrorManager::Medium, code, source, text);
   }

   void
   RateLimiter::EnsureUsageLoaded_()
   {
      if (usage_loaded_)
         return;

      // Set this first: a state file we cannot read must not be re-read on every
      // single message.
      usage_loaded_ = true;
      last_save_time_ = time(0);

      // This runs inside a MAIL FROM. Reading the file allocates the whole of it
      // and the parse allocates per line, so a damaged or hostile file could
      // plausibly throw std::bad_alloc; ReadCompleteTextFile can throw on its own
      // account too. An exception escaping here would reach
      // TCPConnection::AsyncReadCompleted, which reports HM5136, drops the session
      // and rethrows so the worker writes a minidump. Starting the counters from
      // zero is a far better outcome than that, so nothing propagates.
      try
      {
         LoadUsageFile_();
      }
      catch (...)
      {
         if (!state_file_reported_)
         {
            state_file_reported_ = true;
            QueueReport_(5814, "RateLimiter::EnsureUsageLoaded_",
               "The per-account sending-limit state file could not be read. Per-account counters may have started from zero.");
         }
      }
   }

   void
   RateLimiter::LoadUsageFile_()
   {
      String fileName = GetStateFileName_();

      // Deliberately not FileUtilities::FileSize: that narrows the size to int, so
      // a 2GiB file reads back as a negative number and a 4GiB one as zero, and
      // either would sail past the cap below and then be read whole into memory on
      // this connection thread.
      unsigned __int64 fileSize = 0;
      if (!GetFileSize64_(fileName, fileSize) || fileSize == 0)
         return;

      if (fileSize > kMaxStateFileBytes)
      {
         if (!state_file_reported_)
         {
            state_file_reported_ = true;
            QueueReport_(5811, "RateLimiter::LoadUsageFile_",
               "The per-account sending-limit state file is implausibly large and was ignored. Per-account counters have been reset to zero: " + fileName);
         }

         return;
      }

      String contents = FileUtilities::ReadCompleteTextFile(fileName);
      if (contents.IsEmpty())
         return;

      time_t now = time(0);
      const time_t maxPeriodSeconds = (time_t) kMaxPeriodHours * 3600;

      bool versionSeen = false;
      int badLines = 0;

      for (const String &rawLine : StringParser::SplitString(contents, _T("\n")))
      {
         String line = rawLine;
         line.Trim();

         if (line.IsEmpty())
            continue;

         if (!versionSeen)
         {
            versionSeen = true;

            if (line.CompareNoCase(kStateFileVersion) != 0)
            {
               // An unknown format is discarded rather than guessed at. Say so:
               // the counters starting from zero is a security-relevant event.
               if (!state_file_reported_)
               {
                  state_file_reported_ = true;
                  QueueReport_(5811, "RateLimiter::LoadUsageFile_",
                     "The per-account sending-limit state file has an unrecognized format and was ignored. Per-account counters have been reset to zero: " + fileName);
               }

               account_usage_.clear();
               return;
            }

            continue;
         }

         // account <TAB> bucket start <TAB> messages <TAB> recipients
         std::vector<String> fields = StringParser::SplitString(line, _T("\t"));
         if (fields.size() != 4)
         {
            badLines++;
            continue;
         }

         String key = NormalizeAccountKey_(fields[0]);
         if (key.IsEmpty())
         {
            badLines++;
            continue;
         }

         __int64 rawStart = _ttoi64(fields[1]);
         int rawMessages = _ttoi(fields[2]);
         int rawRecipients = _ttoi(fields[3]);

         if (rawStart <= 0 || rawMessages < 0 || rawRecipients < 0)
         {
            badLines++;
            continue;
         }

         // A start in the future has to be pulled down to now, and this is not
         // cosmetic. A bucket dated ahead of the clock never satisfies
         // "start + period <= now", so PruneUsage_ can never retire it: the
         // account's counters would never expire and it would be refused with 452
         // for good, which is an account lockout - worse than the bug this whole
         // facility exists to fix. A start near INT64_MAX would additionally make
         // that same addition overflow. Nothing outside this process is supposed
         // to write the file, but "corrupt", "hand-edited" and "the clock was put
         // back after the last write" all produce it, so it is validated here
         // rather than trusted. The count itself is kept; only the date moves.
         if (rawStart > (__int64) now)
            rawStart = (__int64) now;

         UsageBucket bucket;
         bucket.start = (time_t) rawStart;
         bucket.messages = rawMessages;
         bucket.recipients = rawRecipients;

         // Anything older than the longest period we support can never matter.
         // Safe from overflow now that start is known to be no later than now.
         if (bucket.start + maxPeriodSeconds <= now)
            continue;

         if (account_usage_.size() >= kMaxAccountEntries && account_usage_.find(key) == account_usage_.end())
            continue;

         UsageBuckets &buckets = account_usage_[key];

         // The file is written oldest-first per account; tolerate any order by
         // merging equal starts and refusing to go backwards.
         if (!buckets.empty() && bucket.start <= buckets.back().start)
         {
            bool merged = false;
            for (UsageBucket &existing : buckets)
            {
               if (existing.start == bucket.start)
               {
                  existing.messages = SaturatingAdd_(existing.messages, bucket.messages);
                  existing.recipients = SaturatingAdd_(existing.recipients, bucket.recipients);
                  merged = true;
                  break;
               }
            }

            if (merged)
               continue;

            // Out of order and not a duplicate: fold it into the oldest bucket so
            // the count is never lost.
            buckets.front().messages = SaturatingAdd_(buckets.front().messages, bucket.messages);
            buckets.front().recipients = SaturatingAdd_(buckets.front().recipients, bucket.recipients);
            continue;
         }

         if (buckets.size() >= kMaxBucketsPerAccount)
            buckets.pop_front();

         buckets.push_back(bucket);
      }

      if (badLines > 0)
      {
         String message;
         message.Format(_T("RateLimiter - %d unreadable line(s) in the per-account sending-limit state file were skipped."), badLines);
         LOG_APPLICATION(message);
      }
   }

   String
   RateLimiter::SerializeUsage_(time_t now, bool &truncated) const
   {
      truncated = false;

      String result = kStateFileVersion;
      result += _T("\r\n");

      const time_t maxPeriodSeconds = (time_t) kMaxPeriodHours * 3600;

      for (const auto &account : account_usage_)
      {
         if (result.GetLength() >= kMaxStateFileChars)
         {
            // Stop rather than write a file the loader would reject wholesale. The
            // accounts that fit keep their counters across a restart; the tail does
            // not, which is the lesser of the two losses.
            truncated = true;
            break;
         }

         // A tab or a line break in the key would corrupt the file. Addresses
         // never contain either, but do not take that on trust.
         if (account.first.Find(_T("\t")) != -1 ||
             account.first.Find(_T("\r")) != -1 ||
             account.first.Find(_T("\n")) != -1)
            continue;

         for (const UsageBucket &bucket : account.second)
         {
            if (bucket.messages <= 0 && bucket.recipients <= 0)
               continue;

            if (bucket.start + maxPeriodSeconds <= now)
               continue;

            String line;
            line.Format(_T("%s\t%s\t%d\t%d\r\n"),
               account.first.c_str(),
               StringParser::IntToString((__int64) bucket.start).c_str(),
               bucket.messages,
               bucket.recipients);

            result += line;
         }
      }

      return result;
   }

   void
   RateLimiter::DropDeadAccounts_(time_t now)
   {
      // Retire accounts whose counters have all aged out. The longest supported
      // period is used deliberately: this sweep runs on behalf of whichever
      // account happened to trigger the save, and using that caller's (possibly
      // short) period here would throw away buckets that are still inside some
      // other account's window - a counter reset for an unrelated account.
      const time_t maxPeriodSeconds = (time_t) kMaxPeriodHours * 3600;

      for (auto it = account_usage_.begin(); it != account_usage_.end(); )
      {
         PruneUsage_(it->second, now, maxPeriodSeconds);

         if (it->second.empty())
            it = account_usage_.erase(it);
         else
            ++it;
      }
   }

   void
   RateLimiter::SaveUsage_(bool force)
   {
      String serialized;
      bool truncated = false;

      {
         boost::lock_guard<boost::mutex> guard(account_mutex_);

         // Nothing has changed since the last write, so there is nothing to write.
         // This is the check that keeps a refusal cheap: a refused MAIL FROM or
         // RCPT TO does not touch a counter, and serializing the whole account map
         // under this lock on an SMTP connection thread - once per rejected
         // command, which one over-quota credential can drive as fast as it likes -
         // is exactly the sort of accept-path work that has exhausted the thread
         // pool here before.
         if (!usage_dirty_)
            return;

         time_t now = time(0);

         int interval = kDefaultSaveIntervalSeconds;
         {
            boost::lock_guard<boost::mutex> settingsGuard(settings_mutex_);
            interval = state_save_interval_seconds_;
         }

         // A clock moved backwards must not postpone the next write indefinitely.
         if (last_save_time_ > now)
            last_save_time_ = now;

         // force is FlushAccountUsage only - an administrative or shutdown call.
         // Every connection-thread caller passes false and is throttled.
         if (!force && now - last_save_time_ < (time_t) interval)
            return;

         last_save_time_ = now;
         usage_dirty_ = false;

         // At most once per interval, and already under the lock we need: keep the
         // map (and so the file) from accumulating accounts that stopped sending.
         DropDeadAccounts_(now);

         serialized = SerializeUsage_(now, truncated);
      }

      if (truncated)
      {
         String message;
         message.Format(_T("RateLimiter - the per-account sending-limit state file reached its size limit of %d bytes and was written short. ")
                        _T("Counters for the accounts that did not fit will not survive a restart."),
            (int) kMaxStateFileBytes);

         LOG_APPLICATION(message);

         bool report = false;
         {
            boost::lock_guard<boost::mutex> guard(account_mutex_);
            report = !state_file_truncated_reported_;
            state_file_truncated_reported_ = true;
         }

         if (report)
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5815, "RateLimiter::SaveUsage_", message);
      }

      // A write that keeps failing (an unwritable data directory) is reported once
      // and then only logged: a Medium error every save interval would bury every
      // other entry in the ERROR log.
      String failure;
      bool wrote = false;
      bool reportFailure = false;

      {
         // The write happens with account_mutex_ released, and only one thread
         // writes at a time: a thread that finds the writer busy skips this round
         // rather than queueing behind another connection thread's disk I/O. The
         // winner does perform the write itself, so this is not a claim that no
         // connection thread ever touches the disk - only that at most one does,
         // at most once per interval.
         //
         // save_mutex_ is never held while account_mutex_ is acquired, and
         // account_mutex_ is never held while save_mutex_ is acquired. Keep it that
         // way; the scope below exists so the dirty flag can be restored afterwards
         // rather than under both locks.
         boost::unique_lock<boost::mutex> saveGuard(save_mutex_, boost::try_to_lock);

         if (saveGuard.owns_lock())
         {
            String fileName = GetStateFileName_();
            String tempFileName = fileName + _T(".tmp");

            // Written as UTF-16 with a BOM so that ReadCompleteTextFile round-trips
            // an internationalized address unchanged.
            if (!FileUtilities::WriteToFile(tempFileName, serialized, true))
            {
               failure = "Could not write the per-account sending-limit state file. The counters will not survive a restart: " + tempFileName;
            }
            else if (!MoveFileEx(tempFileName.c_str(), fileName.c_str(), MOVEFILE_REPLACE_EXISTING))
            {
               // MoveFileEx rather than FileUtilities::Move: Move retries five times
               // with a 250ms sleep between attempts, and this runs on a connection
               // thread.
               failure = "Could not replace the per-account sending-limit state file. The counters will not survive a restart: " + fileName;
               FileUtilities::DeleteFile(tempFileName);
            }

            if (failure.IsEmpty())
            {
               wrote = true;
               save_failure_reported_ = false;
            }
            else if (!save_failure_reported_)
            {
               save_failure_reported_ = true;
               reportFailure = true;
            }
         }
      }

      if (!wrote)
      {
         // Either another thread holds the writer or the write failed, so the
         // counters are still unwritten. Put the flag back, and leave last_save_time_
         // alone so the retry waits out the interval rather than spinning on a
         // failing disk.
         boost::lock_guard<boost::mutex> guard(account_mutex_);
         usage_dirty_ = true;
      }

      if (failure.IsEmpty())
         return;

      if (reportFailure)
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5810, "RateLimiter::SaveUsage_", failure);
      else
         LOG_APPLICATION("RateLimiter - " + failure);
   }

   AccountQuotaResult
   RateLimiter::CheckAndConsume_(const String &key, int addMessages, int addRecipients, const AccountSendingLimits &limits, time_t now)
   {
      const time_t periodSeconds = GetPeriodSeconds_(limits);

      auto it = account_usage_.find(key);
      if (it == account_usage_.end())
      {
         if (account_usage_.size() >= kMaxAccountEntries)
         {
            // Discard accounts whose counters have all aged out. Note that this
            // uses the longest supported period, not this caller's: pruning other
            // accounts by our own (possibly much shorter) period would delete
            // buckets that are still inside their window, which is a counter reset
            // for an account that did nothing but be in the same table as us.
            DropDeadAccounts_(now);
         }

         if (account_usage_.size() >= kMaxAccountEntries)
         {
            // Fail open. Refusing mail because an internal table is full would be
            // a worse failure than not counting this message, but it must not be
            // silent.
            if (!account_map_full_reported_)
            {
               account_map_full_reported_ = true;
               QueueReport_(5813, "RateLimiter::CheckAndConsume_",
                  "The per-account sending-limit table is full; the limit is not being enforced for further accounts.");
            }

            return AccountQuotaResult::QuotaAllowed;
         }

         it = account_usage_.insert(std::make_pair(key, UsageBuckets())).first;
      }

      UsageBuckets &buckets = it->second;
      PruneUsage_(buckets, now, periodSeconds);

      int messages = 0;
      int recipients = 0;
      SumUsage_(buckets, messages, recipients);

      // Check before consuming, so a refused attempt does not eat budget. Written
      // as a subtraction rather than "messages + addMessages > max" because the
      // running totals saturate at INT_MAX and the addition would overflow.
      if (limits.max_messages > 0 && messages > limits.max_messages - addMessages)
         return AccountQuotaResult::QuotaMessageLimitReached;

      if (limits.max_recipients > 0 && recipients > limits.max_recipients - addRecipients)
         return AccountQuotaResult::QuotaRecipientLimitReached;

      const time_t bucketSeconds = GetBucketSeconds_(periodSeconds);
      time_t bucketStart = (now / bucketSeconds) * bucketSeconds;

      // Never insert a bucket older than the newest one (a clock that stepped
      // backwards, or a period that was shortened under us); fold the event into
      // the newest bucket instead. PruneUsage_ has already guaranteed that the
      // newest start is no later than now, so this cannot push a bucket into the
      // future and make it unexpirable.
      if (!buckets.empty() && bucketStart < buckets.back().start)
         bucketStart = buckets.back().start;

      if (buckets.empty() || buckets.back().start != bucketStart)
      {
         if (buckets.size() >= kMaxBucketsPerAccount)
            buckets.pop_front();

         UsageBucket fresh;
         fresh.start = bucketStart;
         buckets.push_back(fresh);
      }

      UsageBucket &current = buckets.back();
      current.messages = SaturatingAdd_(current.messages, addMessages);
      current.recipients = SaturatingAdd_(current.recipients, addRecipients);

      // A counter moved, so the file is now behind. This is the only place that
      // sets it: no other path has anything durable to write.
      usage_dirty_ = true;

      return AccountQuotaResult::QuotaAllowed;
   }

   void
   RateLimiter::ReportSwallowedException_(const char *source)
   {
      // Called from a catch block on a connection thread. Anything that throws in
      // here would replace the exception we just contained, so everything is inside
      // its own barrier.
      try
      {
         bool report = false;
         {
            boost::lock_guard<boost::mutex> guard(report_mutex_);
            report = !internal_error_reported_;
            internal_error_reported_ = true;
         }

         String message = "An unexpected error occurred while applying the per-account sending limit. "
                          "The limit was not applied to this command. Source: ";
         message += source;

         if (report)
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5816, "RateLimiter", message);
         else
            LOG_APPLICATION("RateLimiter - " + message);
      }
      catch (...)
      {
      }
   }

   AccountQuotaResult
   RateLimiter::TryConsumeAccountMessage(const String &accountAddress, const AccountSendingLimits &limits)
   {
      if (!limits.IsEnabled())
         return AccountQuotaResult::QuotaAllowed;

      // Everything below runs inside a MAIL FROM. An exception escaping to
      // TCPConnection::AsyncReadCompleted is reported as HM5136, drops the session
      // and is rethrown so the worker writes a minidump; failing to count one
      // message is a much smaller problem than that, so nothing propagates and the
      // fallback is to allow. See ReportSwallowedException_.
      try
      {
         String key = NormalizeAccountKey_(accountAddress);
         if (key.IsEmpty())
            return AccountQuotaResult::QuotaAllowed;

         AccountQuotaResult result = AccountQuotaResult::QuotaAllowed;

         {
            boost::lock_guard<boost::mutex> guard(account_mutex_);
            EnsureUsageLoaded_();
            result = CheckAndConsume_(key, 1, 0, limits, time(0));
         }

         // Never forced, whatever the outcome. A refusal changes no counter, so it
         // has nothing to persist; the write is throttled either way, and the
         // serialization it would otherwise trigger runs under account_mutex_ on
         // this connection thread.
         SaveUsage_(false);
         EmitQueuedReport_();

         return result;
      }
      catch (...)
      {
         ReportSwallowedException_("TryConsumeAccountMessage");
         return AccountQuotaResult::QuotaAllowed;
      }
   }

   AccountQuotaResult
   RateLimiter::TryConsumeAccountRecipients(const String &accountAddress, const AccountSendingLimits &limits, int recipientCount)
   {
      if (!limits.IsEnabled() || recipientCount <= 0)
         return AccountQuotaResult::QuotaAllowed;

      try
      {
         String key = NormalizeAccountKey_(accountAddress);
         if (key.IsEmpty())
            return AccountQuotaResult::QuotaAllowed;

         AccountQuotaResult result = AccountQuotaResult::QuotaAllowed;

         {
            boost::lock_guard<boost::mutex> guard(account_mutex_);
            EnsureUsageLoaded_();
            result = CheckAndConsume_(key, 0, recipientCount, limits, time(0));
         }

         SaveUsage_(false);
         EmitQueuedReport_();

         return result;
      }
      catch (...)
      {
         ReportSwallowedException_("TryConsumeAccountRecipients");
         return AccountQuotaResult::QuotaAllowed;
      }
   }

   bool
   RateLimiter::GetAccountUsage(const String &accountAddress, int &messages, int &recipients)
   {
      messages = 0;
      recipients = 0;

      // Only used for logging, and called from the refusal path: it must not be
      // able to turn a 452 into a dropped session.
      try
      {
         String key = NormalizeAccountKey_(accountAddress);
         if (key.IsEmpty())
            return false;

         boost::lock_guard<boost::mutex> guard(account_mutex_);

         auto it = account_usage_.find(key);
         if (it == account_usage_.end())
            return false;

         SumUsage_(it->second, messages, recipients);
         return true;
      }
      catch (...)
      {
         ReportSwallowedException_("GetAccountUsage");
         return false;
      }
   }

   void
   RateLimiter::ClearAccountUsage()
   {
      try
      {
         {
            boost::lock_guard<boost::mutex> guard(account_mutex_);
            account_usage_.clear();
            usage_loaded_ = true;
            last_save_time_ = time(0);

            // Nothing left to write, and the file is about to go.
            usage_dirty_ = false;
         }

         boost::unique_lock<boost::mutex> saveGuard(save_mutex_);
         FileUtilities::DeleteFile(GetStateFileName_());
      }
      catch (...)
      {
         ReportSwallowedException_("ClearAccountUsage");
      }
   }

   void
   RateLimiter::FlushAccountUsage()
   {
      try
      {
         SaveUsage_(true);
         EmitQueuedReport_();
      }
      catch (...)
      {
         ReportSwallowedException_("FlushAccountUsage");
      }
   }

   // -------------------------------------------------------------------------
   // Configuration
   // -------------------------------------------------------------------------

   bool
   RateLimiter::ParseOverrideValue_(const String &value, AccountSendingLimits &limits)
   {
      std::vector<String> parts = StringParser::SplitString(value, _T(":"));
      if (parts.size() != 2 && parts.size() != 3)
         return false;

      for (String &part : parts)
         part.Trim();

      if (parts[0].IsEmpty() || parts[1].IsEmpty())
         return false;

      int messages = _ttoi(parts[0]);
      int recipients = _ttoi(parts[1]);

      if (messages < 0 || recipients < 0)
         return false;

      // Without an explicit third field the override inherits the global period,
      // which the caller has already put in limits.period_hours.
      int hours = limits.period_hours;
      if (hours < kMinPeriodHours)
         hours = kMinPeriodHours;
      if (hours > kMaxPeriodHours)
         hours = kMaxPeriodHours;

      if (parts.size() == 3)
      {
         if (parts[2].IsEmpty())
            return false;

         hours = _ttoi(parts[2]);
         if (hours < kMinPeriodHours || hours > kMaxPeriodHours)
            return false;
      }

      limits.max_messages = messages;
      limits.max_recipients = recipients;
      limits.period_hours = hours;

      return true;
   }

   void
   RateLimiter::LoadSettings_()
   {
      String iniFile = IniFileSettings::GetInitializationFile();

      ULONGLONG writeTime = 0;
      WIN32_FILE_ATTRIBUTE_DATA attributes = {};
      if (GetFileAttributesEx(iniFile.c_str(), GetFileExInfoStandard, &attributes))
      {
         writeTime = ((ULONGLONG) attributes.ftLastWriteTime.dwHighDateTime << 32) |
                     (ULONGLONG) attributes.ftLastWriteTime.dwLowDateTime;
      }

      {
         boost::lock_guard<boost::mutex> guard(settings_mutex_);

         // Another thread may have done the work while we waited for the refresh
         // lock, and an unchanged file needs no re-read.
         if (settings_loaded_ && writeTime == settings_file_write_time_)
            return;
      }

      const DWORD bufferSize = 4096;
      TCHAR value[bufferSize];

      GetPrivateProfileString(kSettingsSection, _T("MaxMessagesPerAccountPerPeriod"), _T("0"), value, bufferSize, iniFile.c_str());
      int maxMessages = _ttoi(value);

      GetPrivateProfileString(kSettingsSection, _T("MaxRecipientsPerAccountPerPeriod"), _T("0"), value, bufferSize, iniFile.c_str());
      int maxRecipients = _ttoi(value);

      GetPrivateProfileString(kSettingsSection, _T("PeriodHours"), _T("24"), value, bufferSize, iniFile.c_str());
      int periodHours = _ttoi(value);

      GetPrivateProfileString(kSettingsSection, _T("StateSaveIntervalSeconds"), _T("10"), value, bufferSize, iniFile.c_str());
      int saveInterval = _ttoi(value);

      AccountSendingLimits globalLimits;
      globalLimits.max_messages = maxMessages > 0 ? maxMessages : 0;
      globalLimits.max_recipients = maxRecipients > 0 ? maxRecipients : 0;
      globalLimits.period_hours = periodHours;
      if (globalLimits.period_hours < kMinPeriodHours)
         globalLimits.period_hours = kMinPeriodHours;
      if (globalLimits.period_hours > kMaxPeriodHours)
         globalLimits.period_hours = kMaxPeriodHours;

      if (saveInterval < kMinSaveIntervalSeconds)
         saveInterval = kDefaultSaveIntervalSeconds;
      if (saveInterval > kMaxSaveIntervalSeconds)
         saveInterval = kMaxSaveIntervalSeconds;

      // Read the whole overrides section in one call; grow the buffer while the
      // result looks truncated.
      std::vector<TCHAR> sectionBuffer;
      DWORD sectionSize = 8192;
      DWORD copied = 0;
      for (int attempt = 0; attempt < 6; attempt++)
      {
         sectionBuffer.assign(sectionSize, _T('\0'));
         copied = GetPrivateProfileSection(kOverridesSection, &sectionBuffer[0], sectionSize, iniFile.c_str());

         if (copied < sectionSize - 2)
            break;

         sectionSize *= 4;
      }

      std::map<String, AccountSendingLimits> overrides;
      int malformedOverrides = 0;

      if (copied > 0)
      {
         const TCHAR *entry = &sectionBuffer[0];
         while (*entry != 0)
         {
            String line = entry;
            entry += line.GetLength() + 1;

            line.Trim();
            if (line.IsEmpty() || line.StartsWith(_T(";")) || line.StartsWith(_T("#")))
               continue;

            int separator = line.Find(_T("="));
            if (separator <= 0)
            {
               malformedOverrides++;
               continue;
            }

            String address = NormalizeAccountKey_(line.Mid(0, separator));
            String limitText = line.Mid(separator + 1);
            limitText.Trim();

            AccountSendingLimits accountLimits;
            accountLimits.period_hours = globalLimits.period_hours;

            if (address.IsEmpty() || !ParseOverrideValue_(limitText, accountLimits))
            {
               malformedOverrides++;
               continue;
            }

            overrides[address] = accountLimits;
         }
      }

      {
         boost::lock_guard<boost::mutex> guard(settings_mutex_);

         global_limits_ = globalLimits;
         overrides_.swap(overrides);
         state_save_interval_seconds_ = saveInterval;
         settings_file_write_time_ = writeTime;
         settings_loaded_ = true;
      }

      if (malformedOverrides > 0)
      {
         String message;
         message.Format(_T("RateLimiter - %d malformed entry/entries in the hMailServer.ini [%s] section were ignored. ")
                        _T("The expected form is address=messages:recipients[:hours]. The global limit still applies to those accounts."),
            malformedOverrides, kOverridesSection);

         LOG_APPLICATION(message);

         // Reported once per process: a misconfigured override is a real problem,
         // but the ini is re-read whenever it changes and an ERROR log entry every
         // few seconds would be worse than the misconfiguration.
         bool report = false;
         {
            boost::lock_guard<boost::mutex> guard(settings_mutex_);
            report = !override_parse_error_reported_;
            override_parse_error_reported_ = true;
         }

         if (report)
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5812, "RateLimiter::LoadSettings_", message);
      }
   }

   void
   RateLimiter::MaybeRefreshSettings_()
   {
      bool firstLoad = false;

      {
         boost::lock_guard<boost::mutex> guard(settings_mutex_);

         time_t now = time(0);

         // A clock that stepped backwards must not defer the next check forever.
         if (settings_checked_time_ > now)
            settings_checked_time_ = now;

         if (settings_loaded_ && now - settings_checked_time_ < kSettingsRecheckSeconds)
            return;

         settings_checked_time_ = now;
         firstLoad = !settings_loaded_;
      }

      boost::unique_lock<boost::mutex> refreshGuard(settings_refresh_mutex_, boost::defer_lock);

      if (firstLoad)
      {
         // Nothing is cached yet, so this caller has to wait for the read.
         refreshGuard.lock();
      }
      else
      {
         // A refresh already in flight is good enough; carry on with the cached
         // values rather than queueing behind file I/O on the accept path.
         refreshGuard.try_lock();
         if (!refreshGuard.owns_lock())
            return;
      }

      LoadSettings_();
   }

   AccountSendingLimits
   RateLimiter::GetAccountLimits(const String &accountAddress)
   {
      // Reads the ini file, so it can throw for reasons that have nothing to do
      // with the caller. Same barrier as the consume calls: a limit we cannot
      // determine is treated as no limit, never as a dropped session.
      try
      {
         MaybeRefreshSettings_();

         String key = NormalizeAccountKey_(accountAddress);

         boost::lock_guard<boost::mutex> guard(settings_mutex_);

         auto it = overrides_.find(key);
         if (it != overrides_.end())
            return it->second;

         return global_limits_;
      }
      catch (...)
      {
         ReportSwallowedException_("GetAccountLimits");
         return AccountSendingLimits();
      }
   }
}
