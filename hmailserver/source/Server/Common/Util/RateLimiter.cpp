// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "RateLimiter.h"

#include <ctime>

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
}
