// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "WorkQueueHealthTask.h"

#include "../Util/AlertManager.h"

#include "Application.h"
#include "../Threading/WorkQueue.h"
#include "../Threading/WorkQueueManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   WorkQueueHealthTask::WorkQueueHealthTask(void)
   {

   }

   WorkQueueHealthTask::~WorkQueueHealthTask(void)
   {

   }

   void
   WorkQueueHealthTask::DoWork()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Reports tasks that have been holding a worker thread for longer than
   // AsyncQueueStallThreshold, on EVERY work queue.
   //
   // This asked for the asynchronous queue by name and checked nothing else, so a
   // queue added later was never going to be covered by it. It now asks every
   // queue that has opted in, which is the asynchronous queue and the name-lookup
   // queue - the latter previously invisible, and a queue where a stall is real:
   // its whole purpose is blocking reverse lookups on behalf of live sessions, and
   // an unreachable reverse zone is exactly what wedges it.
   //
   // It is deliberately NOT every queue. See WorkQueue::SetMonitorForStalls: three
   // of this server's queues host tasks that never finish by design, so "all
   // threads busy for two minutes" is their healthy state and reporting it would
   // raise a High error every minute on a stock installation. Opting in per queue
   // puts that decision where the queue is created rather than in a list here that
   // the next person to add a queue will not find.
   //
   // Each queue names itself in its own report, so a stall says which pool is
   // affected rather than merely that one is.
   //---------------------------------------------------------------------------
   {
      // Copied out of the manager before anything is inspected - see GetAllQueues
      // for why holding the manager's lock across this would be a lock-ordering
      // problem rather than a tidiness one.
      std::vector<std::shared_ptr<WorkQueue> > queues = WorkQueueManager::Instance()->GetAllQueues();

      String stalled;

      for (std::shared_ptr<WorkQueue> queue : queues)
      {
         if (!queue || !queue->GetMonitorForStalls())
            continue;

         queue->ReportStalledTasks();

         if (!queue->GetIsStalled())
            continue;

         if (!stalled.IsEmpty())
            stalled += _T(", ");

         stalled += queue->GetName();
      }

      // The alert, once for the whole set rather than once per queue: an
      // administrator wants to be told that work has stopped, not to be told it
      // again by each pool that has stopped doing it. HM5526 in the ERROR log
      // still names the queue and the tasks it is stuck in.
      if (stalled.IsEmpty())
      {
         AlertManager::Instance()->Clear(AlertManager::ConditionQueueStalled,
            _T("Every monitored work queue is turning over again."));
      }
      else
      {
         AlertManager::Instance()->Raise(AlertManager::ConditionQueueStalled, ErrorManager::High,
            Formatter::Format("Work has stopped on: {0}.", stalled),
            _T("Every worker thread on the named queue has been inside the same task for longer than [Settings] AsyncQueueStallThreshold, so nothing posted to it can start. The ERROR log records HM5526 with the tasks each thread is in."));
      }
   }
}
