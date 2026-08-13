// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21

#include "StdAfx.h"

#include "WorkQueueManager.h"
#include "Task.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   WorkQueueManager::WorkQueueManager(void) :
      next_queue_id_(0)
   {
   }

   WorkQueueManager::~WorkQueueManager(void)
   {
   }

   size_t 
   WorkQueueManager::CreateWorkQueue(int iMaxSimultaneous, const String &sQueueName)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Creates a new work queue and adds it to the list of queues. Returns the ID
   // of the new queue.
   //---------------------------------------------------------------------------
   {
      // Create the work queue
      std::shared_ptr<WorkQueue> pWorkQueue = std::shared_ptr<WorkQueue>(new WorkQueue(iMaxSimultaneous, sQueueName));
      pWorkQueue->Start();

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      // Counted up for ever, rather than derived from work_queues_.size(). Queues
      // come and go over the life of the process - the delivery, external fetch and
      // IOCP queues are created and removed with every server start and stop - so a
      // size-derived id is handed out again as soon as anything has been removed.
      // The reissued id then silently replaces the map entry of a live queue: the
      // caller holding the older id posts its tasks into a queue that is not the one
      // it created, and the queue that was displaced is destroyed without Stop()
      // ever being called on it, with its worker threads still running inside it.
      next_queue_id_++;

      size_t iQueueID = next_queue_id_;

      work_queues_[iQueueID] = pWorkQueue;

      return iQueueID;
   }

   void 
   WorkQueueManager::AddTask(size_t iQueueID, std::shared_ptr<Task> pTask)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Adds a task to a worker queue.
   //---------------------------------------------------------------------------
   {
      // Add the task to the work queue
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      auto iterQueue = work_queues_.find(iQueueID);

      if (iterQueue == work_queues_.end())
      {
         // Someone is trying to add a task to a queue that does not exist. The
         // assert is a debug-build aid and compiles to nothing in a release build,
         // where what used to follow it was a dereference of end() - a crash, in
         // the branch whose entire purpose was to say "this cannot happen".
         assert(0);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6076, "WorkQueueManager::AddTask",
            Formatter::Format("Task {0} was discarded because work queue {1} does not exist.",
                              pTask ? pTask->GetName() : String(_T("<null>")), iQueueID));

         return;
      }

      std::shared_ptr<WorkQueue> pWorkQueue = (*iterQueue).second;

      pWorkQueue->AddTask(pTask);
   }

   void 
   WorkQueueManager::RemoveQueue(const String &sQueueName)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Stops and removes a queue
   //---------------------------------------------------------------------------
   {
      LOG_DEBUG(Formatter::Format("WorkQueueManager::RemoveQueue - {0}", sQueueName));

      // Locate the work queue
      std::shared_ptr<WorkQueue> pQueue;
      std::map<size_t, std::shared_ptr<WorkQueue> >::iterator iterQueue;

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         iterQueue = GetQueueIterator_(sQueueName);
         if (iterQueue == work_queues_.end())
         {
            LOG_DEBUG(Formatter::Format("WorkQueueManager::RemoveQueue - Work quue not found {0}", sQueueName));
            return;
         }

         pQueue = (*iterQueue).second;
         if (!pQueue)
         {
            LOG_DEBUG(Formatter::Format("WorkQueueManager::RemoveQueue - Work quue not found {0}", sQueueName));
            return;
         }
      }

      pQueue->Stop();

      LOG_DEBUG(Formatter::Format("WorkQueueManager::RemoveQueue - Stopped {0}", sQueueName));

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         // Found again by the pointer that was just stopped, rather than erased
         // through the iterator taken above. The lock was released for the whole of
         // Stop(), which is bounded at ten seconds - long enough for a second
         // RemoveQueue of the same name during a stop/start race to have erased this
         // entry already, leaving that iterator dangling and this erase undefined.
         // Matching on the pointer rather than the name also means a queue of the
         // same name created in that window is left where it is instead of being
         // torn out from under whoever just created it.
         for (auto iter = work_queues_.begin(); iter != work_queues_.end(); iter++)
         {
            if ((*iter).second == pQueue)
            {
               work_queues_.erase(iter);
               break;
            }
         }
      }

      LOG_DEBUG(Formatter::Format("WorkQueueManager::RemoveQueue - Erased {0}", sQueueName));

   }

   std::shared_ptr<WorkQueue> 
   WorkQueueManager::GetQueue(const String &sQueueName)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Returns the queue with a specific name. 
   //---------------------------------------------------------------------------
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      // GetQueueIterator_ has already compared the name, so the second comparison
      // that used to be here could not fail, and the iterQueue++ that followed it
      // was unreachable in the case it was written for and did nothing in the case
      // it actually ran.
      auto iterQueue = GetQueueIterator_(sQueueName);

      if (iterQueue != work_queues_.end())
         return (*iterQueue).second;

      std::shared_ptr<WorkQueue> pEmpty;
      return pEmpty;

   }

   std::map<size_t, std::shared_ptr<WorkQueue> >::iterator
   WorkQueueManager::GetQueueIterator_(const String &sQueueName)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Returns a iterator to a queue with the specified name.
   //---------------------------------------------------------------------------
   {
      auto iterQueue = work_queues_.begin();
      while (iterQueue != work_queues_.end())
      {
         std::shared_ptr<WorkQueue> pQueue = (*iterQueue).second;
         if (pQueue->GetName().CompareNoCase(sQueueName) == 0)
            return iterQueue;

         iterQueue++;
      }

      return work_queues_.end();
   }
}