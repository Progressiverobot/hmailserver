// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Singleton.h"

#include <deque>
#include <map>

namespace HM
{
   // Thread-safe, in-memory sliding-window rate limiter.
   //
   // Each named bucket (e.g. a source IP address or an outbound destination
   // domain) keeps the timestamps of recent events. TryConsume returns false
   // when registering a new event would exceed maxPerMinute events within the
   // trailing 60-second window; otherwise it records the event and returns true.
   //
   // A maxPerMinute of 0 (or negative) means "unlimited" and always succeeds
   // without recording anything, so the limiter is a no-op until configured.
   class RateLimiter : public Singleton<RateLimiter>
   {
   public:
      RateLimiter();
      virtual ~RateLimiter();

      bool TryConsume(const String &key, int maxPerMinute);

      // Removes all buckets (used by self-tests and on configuration reload).
      void Clear();

   private:
      void PruneExpired_(std::deque<time_t> &events, time_t now) const;

      boost::recursive_mutex mutex_;
      std::map<String, std::deque<time_t>> buckets_;
   };
}
