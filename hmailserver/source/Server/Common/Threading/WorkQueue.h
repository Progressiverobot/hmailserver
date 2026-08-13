// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21

#pragma once

#include "../Util/Event.h"

#include <boost/asio.hpp>
#include <boost/thread.hpp>

#include <atomic>
#include <deque>
#include <map>
#include <set>
#include <vector>

namespace HM
{
   class Task;
   class Thread;

   // One entry per worker thread that is currently executing something. All
   // durations are milliseconds measured at the moment the snapshot was taken.
   struct WorkQueueTaskInfo
   {
      String name;
      unsigned int thread_id;
      unsigned __int64 queue_wait_ms;
      unsigned __int64 running_ms;
      bool may_block;
   };

   class WorkQueue
   {
   public:

      enum TaskClass
      {
         TaskNormal = 0,
         // The task may block on something outside this process - a scanner, a
         // script, a database or a remote host. Those are capped below the
         // thread count so a wedged dependency cannot consume the whole pool.
         // Two caller constraints follow from the cap: such a task must not wait
         // for another task on the same queue, and nothing may wait on its
         // GetIsStartedEvent(), because it can legitimately sit in the wait list
         // for as long as the dependency is wedged.
         TaskMayBlock = 1
      };

      WorkQueue(unsigned int iMaxSimultaneous, const String &sName);
      ~WorkQueue(void);

      void SetMaxSimultaneous(int iMaxSimultaneous);

      void AddTask(std::shared_ptr<Task> pTask, TaskClass task_class = TaskNormal);
      void Start();

      void Stop();

      const String &GetName() const;

      // Tasks accepted but not yet started, including those held back by the
      // TaskMayBlock cap.
      int GetQueueDepth() const;

      // Tasks accepted as TaskMayBlock that are waiting for a slot. These do not
      // occupy a worker thread.
      int GetWaitingBlockingTaskCount() const;

      // What every worker thread is doing right now, and for how long.
      std::vector<WorkQueueTaskInfo> GetRunningTasks() const;

      // Emits a rate limited error when no worker thread has been able to pick
      // up new work for AsyncQueueStallThreshold seconds. Meant to be called
      // periodically from a thread that is not owned by this queue.
      void ReportStalledTasks();

   private:

      struct RunningTask
      {
         String name;
         unsigned int thread_id;
         unsigned __int64 enqueue_tick;
         unsigned __int64 start_tick;
         bool may_block;
      };

      struct PendingTask
      {
         std::shared_ptr<Task> task;
         String name;
         unsigned __int64 enqueue_tick;
      };

      void RemoveRunningTask_(unsigned __int64 task_id);
      void FinishTask_(unsigned __int64 task_id, bool may_block);

      // The top of a worker thread, and the exception barrier for it. Nothing
      // above these frames can catch anything, so nothing may escape them.
      void IoServiceRunWorker();
      void RunWorker_();
      void ReportWorkerException_(const String &detail);

      // Bounded wait for every worker thread to leave. True when none remain.
      bool JoinWorkers_(int max_wait_ms);

      void ExecuteTask(std::shared_ptr<Task> pTask, String name, unsigned __int64 enqueue_tick, bool may_block);
      void PostTask_(std::shared_ptr<Task> pTask, const String &name, unsigned __int64 enqueue_tick, bool may_block);

      void DispatchBlockingTasks_();
      unsigned int GetBlockingLimit_() const;

      static unsigned __int64 ElapsedSince_(unsigned __int64 now, unsigned __int64 earlier);

      boost::asio::io_context io_context_;
      boost::asio::executor_work_guard<boost::asio::io_context::executor_type> work_;

      std::map<unsigned __int64, RunningTask> runningTasks_;
      unsigned __int64 next_task_id_;
      bool stall_reported_;
      unsigned __int64 last_stall_report_tick_;
      mutable boost::recursive_mutex runningTasksMutex_;

      // Guards the TaskMayBlock throttle. Never held while a task is running and
      // never taken while runningTasksMutex_ is held.
      mutable boost::mutex blockingMutex_;
      std::deque<PendingTask> pending_blocking_tasks_;
      unsigned int running_blocking_tasks_;

      std::atomic<int> queue_depth_;

      // Rate limit for the queue-wait warning; see the report site.
      std::atomic<unsigned __int64> last_queue_wait_report_tick_;

      // The number of threads Start() actually created, which is not the same as
      // max_simultaneous_ once that has been changed at runtime.
      std::atomic<unsigned int> worker_thread_count_;

      // The number of threads that are still inside io_context::run() right now.
      // Not the same as worker_thread_count_ the moment a worker leaves, and it is
      // this one the stall detector compares against: a queue that has lost workers
      // is precisely the queue a stall report exists for, and measured against the
      // count Start() created it could never satisfy "every thread is busy" again.
      std::atomic<unsigned int> live_worker_count_;

      // Set by Stop() before the io_context is stopped. From that point a task
      // handed to this queue can never run, so AddTask says so rather than
      // accepting it and dropping it in silence.
      std::atomic<bool> stopping_;

      unsigned int max_simultaneous_;

      String queue_name_;

      std::set<std::shared_ptr<boost::thread>> workerThreads_;
   };

}
