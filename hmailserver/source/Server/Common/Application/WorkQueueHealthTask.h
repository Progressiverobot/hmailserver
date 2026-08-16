// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // Watches the async work queue, which is the queue that acknowledges received
   // messages. Every task on it holds a thread until it returns, so a dependency
   // that stops responding - a scanner, a DNS server, an event script - consumes
   // threads until none are left, at which point the server accepts mail and
   // never replies. That failure looked, from outside, like the server had simply
   // gone quiet; see GitHub discussion #18.
   //
   // The individual waits are now bounded, so this exists to name the culprit if
   // it happens again: when every worker has been busy longer than
   // AsyncQueueStallThreshold AND work is queued behind them, it reports what each
   // thread is running and for how long.
   //
   // Runs on the scheduler's thread, which belongs to a different queue - the
   // report must not be produced by a thread of the queue being measured.
   class WorkQueueHealthTask : public ScheduledTask
   {
   public:
      WorkQueueHealthTask(void);
      ~WorkQueueHealthTask(void);

      virtual void DoWork();
   };
}
