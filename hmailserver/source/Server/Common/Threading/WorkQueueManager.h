// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21

#pragma once

#include "WorkQueue.h"

namespace HM
{
   class WorkQueue;
   class Task;

   class WorkQueueManager : public Singleton<WorkQueueManager>
   {
   public:
      WorkQueueManager(void);
      ~WorkQueueManager(void);

      size_t CreateWorkQueue(int iMaxSimultaneous, const String &sQueueName);
      void RemoveQueue(const String &sQueueName);

      void AddTask(size_t iQueueID, std::shared_ptr<Task> pTask);

      std::shared_ptr<WorkQueue> GetQueue(const String &sQueueName);

      // Every queue that currently exists, for callers that measure rather than
      // dispatch. Returned by value, and deliberately: the health check must not
      // hold the manager's lock while it walks each queue's own state, or the two
      // locks are taken in opposite orders by anything that creates a queue while
      // holding a queue lock.
      std::vector<std::shared_ptr<WorkQueue> > GetAllQueues();

   private:

      std::map<size_t, std::shared_ptr<WorkQueue> >::iterator GetQueueIterator_(const String &sQueueName);

      boost::recursive_mutex mutex_;
      std::map<size_t, std::shared_ptr<WorkQueue> > work_queues_;

      // Never reused, never derived from the size of the map above. See
      // CreateWorkQueue for what reissuing an id costs.
      size_t next_queue_id_;

   };
}