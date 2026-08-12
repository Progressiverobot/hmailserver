// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21

#include "StdAfx.h"

#include "WorkQueue.h"
#include "Task.h"


#include "../Application/ExceptionHandler.h"
#include "../Application/IniFileSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW

#endif

namespace HM
{
   // A task that spent this long waiting for a worker thread means the pool was
   // already saturated when it was posted. Logged at application level since
   // this is what precedes a queue-wide stall, and the default log level is not
   // enough to see LOG_DEBUG on a live server.
   const unsigned __int64 QUEUE_WAIT_WARNING_MS = 5000;
   const unsigned __int64 QUEUE_WAIT_REPORT_INTERVAL_MS = 60000;

   // A stall report that names every thread of a 100 thread queue is unreadable
   // and produces an event log line that some sinks truncate.
   const size_t MAX_LISTED_STALLED_TASKS = 20;

   WorkQueue::WorkQueue(unsigned int iMaxSimultaneous, const String &sQueueName) :
      work_(boost::asio::make_work_guard(io_context_)),
      next_task_id_(0),
      stall_reported_(false),
      last_stall_report_tick_(0),
      running_blocking_tasks_(0),
      queue_depth_(0),
      last_queue_wait_report_tick_(0),
      worker_thread_count_(0),
      max_simultaneous_(0),
      queue_name_ (sQueueName)
   {
      SetMaxSimultaneous(iMaxSimultaneous);

      LOG_DEBUG(Formatter::Format("Creating work queue {0}", queue_name_));
   }

   void
   WorkQueue::SetMaxSimultaneous(int iMaxSimultaneous)
   {
      max_simultaneous_ = iMaxSimultaneous;

      // Hard code limit to 100. Everything over this won't be good for stability.
      if (max_simultaneous_ > 100)
         max_simultaneous_ = 100;

      // A larger pool may have made room for tasks that are waiting for a slot.
      DispatchBlockingTasks_();
   }

   WorkQueue::~WorkQueue(void)
   {

   }

   unsigned __int64
   WorkQueue::ElapsedSince_(unsigned __int64 now, unsigned __int64 earlier)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Tick difference that cannot underflow. The two ticks are read at different
   // times by different threads, so "now" is not guaranteed to be the later one.
   //---------------------------------------------------------------------------
   {
      if (now <= earlier)
         return 0;

      return now - earlier;
   }

   void
   WorkQueue::AddTask(std::shared_ptr<Task> pTask, TaskClass task_class)
   {
      String task_name = pTask->GetName();

      LOG_DEBUG(Formatter::Format("Adding task {0} to work queue {1}", task_name, queue_name_));

      unsigned __int64 enqueue_tick = GetTickCount64();

      // The task is queued from this point on, whether it goes straight into the
      // io_context or waits for a blocking slot.
      queue_depth_++;

      if (task_class == TaskMayBlock)
      {
         PendingTask pending;
         pending.task = pTask;
         pending.name = task_name;
         pending.enqueue_tick = enqueue_tick;

         {
            boost::lock_guard<boost::mutex> guard(blockingMutex_);

            // Always through the wait list, even when a slot is free, so that
            // tasks start in the order they were accepted.
            pending_blocking_tasks_.push_back(pending);
         }

         DispatchBlockingTasks_();

         return;
      }

      PostTask_(pTask, task_name, enqueue_tick, false);
   }

   void
   WorkQueue::PostTask_(std::shared_ptr<Task> pTask, const String &name, unsigned __int64 enqueue_tick, bool may_block)
   {
      // Post a wrapped task into the queue.
      std::function<void ()> func = std::bind(&WorkQueue::ExecuteTask, this, pTask, name, enqueue_tick, may_block);
      boost::asio::post(io_context_, func);
   }

   unsigned int
   WorkQueue::GetBlockingLimit_() const
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // How many tasks marked TaskMayBlock are allowed to run at the same time.
   //---------------------------------------------------------------------------
   {
      unsigned int thread_count = worker_thread_count_.load();

      if (thread_count == 0)
      {
         // Tasks can be accepted before Start() has created the threads, so the
         // cap must not be derived from a count of zero.
         thread_count = max_simultaneous_;
      }

      if (thread_count == 0)
         thread_count = 1;

      int reserved = IniFileSettings::Instance()->GetAsyncQueueReservedThreads();

      if (reserved <= 0)
      {
         // Nothing reserved means no cap. The io_context already limits us to
         // one task per worker thread.
         return thread_count;
      }

      if (static_cast<unsigned int>(reserved) >= thread_count)
      {
         // A reservation at or above the pool size would stop blocking tasks
         // from ever starting, which is worse than not reserving at all.
         return 1;
      }

      return thread_count - static_cast<unsigned int>(reserved);
   }

   void
   WorkQueue::DispatchBlockingTasks_()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Moves as many waiting tasks into the io_context as the cap allows. Tasks
   // over the cap wait here rather than on a worker thread, so a blocked
   // dependency can never hold a thread it has not been given a slot for.
   // Called from every point that can free a slot or add one, which is what
   // keeps the wait list from stalling while capacity is available.
   //---------------------------------------------------------------------------
   {
      std::vector<PendingTask> to_start;

      {
         boost::lock_guard<boost::mutex> guard(blockingMutex_);

         if (pending_blocking_tasks_.empty())
            return;

         unsigned int limit = GetBlockingLimit_();

         while (running_blocking_tasks_ < limit && !pending_blocking_tasks_.empty())
         {
            to_start.push_back(pending_blocking_tasks_.front());
            pending_blocking_tasks_.pop_front();

            running_blocking_tasks_++;
         }
      }

      for (const PendingTask &pending : to_start)
      {
         PostTask_(pending.task, pending.name, pending.enqueue_tick, true);
      }
   }

   void
   WorkQueue::ExecuteTask(std::shared_ptr<Task> pTask, String name, unsigned __int64 enqueue_tick, bool may_block)
   {
      unsigned __int64 start_tick = GetTickCount64();
      unsigned __int64 queue_wait = ElapsedSince_(start_tick, enqueue_tick);

      queue_depth_--;

      unsigned __int64 task_id = 0;

      {
         boost::lock_guard<boost::recursive_mutex> guard(runningTasksMutex_);

         next_task_id_++;
         task_id = next_task_id_;

         RunningTask running;
         running.name = name;
         running.thread_id = static_cast<unsigned int>(GetCurrentThreadId());
         running.enqueue_tick = enqueue_tick;
         running.start_tick = start_tick;
         running.may_block = may_block;

         runningTasks_[task_id] = running;
      }

      if (queue_wait >= QUEUE_WAIT_WARNING_MS)
      {
         // Rate limited: a queue wait above the warning threshold is routine for
         // every message while a backlog drains (a mailing-list send, or catching
         // up after an outage), and one line per message would bury the log
         // exactly when it is being read.
         unsigned __int64 previous = last_queue_wait_report_tick_.load();

         if (start_tick - previous >= QUEUE_WAIT_REPORT_INTERVAL_MS &&
             last_queue_wait_report_tick_.compare_exchange_strong(previous, start_tick))
         {
            LOG_APPLICATION(Formatter::Format("Task {0} waited {1} seconds for a thread in work queue {2}. {3} task(s) are still queued.",
                                              name, queue_wait / 1000, queue_name_, queue_depth_.load()));
         }
      }

      LOG_DEBUG(Formatter::Format("Executing task {0} in work queue {1}", name, queue_name_));

      String descriptive_name = Formatter::Format("Task-{0}", name);
      boost::function<void()> func = boost::bind( &Task::Run, pTask );

      try
      {
         ExceptionHandler::Run(descriptive_name, func);
      }
      catch (...)
      {
         // A blocking slot that is never given back would permanently shrink the
         // pool, so the bookkeeping has to be undone on every exit path.
         FinishTask_(task_id, may_block);

         throw;
      }

      FinishTask_(task_id, may_block);
   }

   void
   WorkQueue::FinishTask_(unsigned __int64 task_id, bool may_block)
   {
      RemoveRunningTask_(task_id);

      if (!may_block)
         return;

      {
         boost::lock_guard<boost::mutex> guard(blockingMutex_);

         if (running_blocking_tasks_ > 0)
            running_blocking_tasks_--;
      }

      // Releasing a slot is the only thing that can let a waiting task start, so
      // the wait list has to be pumped here. The lock above is released first;
      // DispatchBlockingTasks_ takes it again itself.
      DispatchBlockingTasks_();
   }

   void
   WorkQueue::RemoveRunningTask_(unsigned __int64 task_id)
   {
      boost::lock_guard<boost::recursive_mutex> guard(runningTasksMutex_);
      runningTasks_.erase(task_id);
   }

   int
   WorkQueue::GetQueueDepth() const
   {
      return queue_depth_.load();
   }

   int
   WorkQueue::GetWaitingBlockingTaskCount() const
   {
      boost::lock_guard<boost::mutex> guard(blockingMutex_);

      return static_cast<int>(pending_blocking_tasks_.size());
   }

   std::vector<WorkQueueTaskInfo>
   WorkQueue::GetRunningTasks() const
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Snapshot of what every worker thread is doing. Returned by value so the
   // caller can inspect it without holding the queue lock.
   //---------------------------------------------------------------------------
   {
      unsigned __int64 now = GetTickCount64();

      std::vector<WorkQueueTaskInfo> result;

      boost::lock_guard<boost::recursive_mutex> guard(runningTasksMutex_);

      for (auto iter = runningTasks_.begin(); iter != runningTasks_.end(); iter++)
      {
         const RunningTask &running = (*iter).second;

         WorkQueueTaskInfo info;
         info.name = running.name;
         info.thread_id = running.thread_id;
         info.queue_wait_ms = ElapsedSince_(running.start_tick, running.enqueue_tick);
         info.running_ms = ElapsedSince_(now, running.start_tick);
         info.may_block = running.may_block;

         result.push_back(info);
      }

      return result;
   }

   void
   WorkQueue::ReportStalledTasks()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Reports the case where every worker thread has been inside the same task
   // for longer than the threshold, which means nothing posted to this queue can
   // start. Must not be called from a thread owned by this queue.
   //---------------------------------------------------------------------------
   {
      // Doubles as a safety pump. The cap is read from the ini file on every
      // dispatch, so raising the reserved thread count at runtime must not leave
      // waiting tasks parked until the next enqueue or completion on this queue.
      DispatchBlockingTasks_();

      int threshold_seconds = IniFileSettings::Instance()->GetAsyncQueueStallThreshold();

      if (threshold_seconds <= 0)
      {
         // Stall detection disabled.
         return;
      }

      unsigned int thread_count = worker_thread_count_.load();

      if (thread_count == 0)
      {
         // Not started, or already stopped.
         return;
      }

      // A queue whose threads are all long-lived by design is not stalled: the
      // main server queue is created with exactly as many threads as it is given
      // permanently-running tasks (the scheduler, the IO service, the delivery
      // manager, the external fetcher), and the IOCP queue does the same. Both
      // would otherwise satisfy "every thread busy longer than the threshold"
      // forever and report a stall every interval for the life of the process.
      // What distinguishes a real stall is that work is piling up behind it.
      if (queue_depth_.load() == 0)
      {
         return;
      }

      unsigned __int64 threshold_ms = static_cast<unsigned __int64>(threshold_seconds) * 1000;
      unsigned __int64 now = GetTickCount64();

      bool report = false;
      String task_list;
      size_t running_count = 0;

      {
         boost::lock_guard<boost::recursive_mutex> guard(runningTasksMutex_);

         running_count = runningTasks_.size();

         bool all_threads_stalled = running_count >= static_cast<size_t>(thread_count);

         if (all_threads_stalled)
         {
            size_t listed = 0;

            for (auto iter = runningTasks_.begin(); iter != runningTasks_.end(); iter++)
            {
               const RunningTask &running = (*iter).second;

               unsigned __int64 elapsed = ElapsedSince_(now, running.start_tick);

               if (elapsed < threshold_ms)
               {
                  // At least one thread is doing short work, so the queue is
                  // still turning over.
                  all_threads_stalled = false;
                  break;
               }

               if (listed < MAX_LISTED_STALLED_TASKS)
               {
                  if (!task_list.IsEmpty())
                  {
                     task_list += _T(", ");
                  }

                  task_list += Formatter::Format("{0} (thread {1}, {2}s)", running.name, running.thread_id, elapsed / 1000);

                  listed++;
               }
            }

            if (all_threads_stalled && listed < running_count)
            {
               task_list += Formatter::Format(" and {0} more", running_count - listed);
            }
         }

         if (!all_threads_stalled)
         {
            // Arm the report again so the next stall is reported immediately.
            stall_reported_ = false;
            return;
         }

         if (!stall_reported_ || ElapsedSince_(now, last_stall_report_tick_) >= threshold_ms)
         {
            stall_reported_ = true;
            last_stall_report_tick_ = now;
            report = true;
         }
      }

      if (!report)
      {
         // Rate limited: one report per stall, repeated at most once per
         // threshold period for as long as the stall lasts.
         return;
      }

      String message =
         Formatter::Format("All {0} threads in work queue {1} have been busy for at least {2} seconds, so no further task on this queue can start. {3} task(s) are queued. Running: {4}",
                           thread_count, queue_name_, threshold_seconds, queue_depth_.load(), task_list);

      ErrorManager::Instance()->ReportError(ErrorManager::High, 5526, "WorkQueue::ReportStalledTasks", message);
   }

   void
   WorkQueue::Start()
   {
      LOG_DEBUG(Formatter::Format("Starting work queue {0}", queue_name_));

      io_context_.restart();

      for ( std::size_t i = 0; i < max_simultaneous_; ++i )
      {
         std::shared_ptr<boost::thread> thread = std::shared_ptr<boost::thread>
            (new boost::thread(std::bind( &WorkQueue::IoServiceRunWorker, this )));

         workerThreads_.insert(thread);
      }

      worker_thread_count_ = static_cast<unsigned int>(workerThreads_.size());

      // Threads exist now, so the cap is known and anything accepted before this
      // point can be released.
      DispatchBlockingTasks_();

      LOG_DEBUG(Formatter::Format("Started work queue {0}", queue_name_));
   }

   void
   WorkQueue::IoServiceRunWorker()
   {
      LOG_DEBUG(Formatter::Format("Running worker in work queue {0}", queue_name_));

      try
      {
         io_context_.run();
      }
      catch (boost::system::system_error& error)
      {
         if (error.code().value() == ERROR_ABANDONED_WAIT_0)
         {
            // If a call to GetQueuedCompletionStatus fails because the completion port handle associated with it is
            // closed while the call is outstanding, the function returns FALSE, *lpOverlapped will be NULL,
            //and GetLastError will return ERROR_ABANDONED_WAIT_0.

            return;
         }

         throw;
      }

      LOG_DEBUG(Formatter::Format("Worker exited in work queue {0}", queue_name_));
   }

   void
   WorkQueue::Stop()
   {
      LOG_DEBUG(Formatter::Format("Stopping working queue {0}.", queue_name_));

      // Prevent new tasks from being started.
      io_context_.stop();

      worker_thread_count_ = 0;

      {
         // Tasks still waiting for a blocking slot can never run now. Dropping
         // them releases the objects they keep alive, such as the connection the
         // task was going to answer on.
         boost::lock_guard<boost::mutex> guard(blockingMutex_);
         pending_blocking_tasks_.clear();
      }

      LOG_DEBUG(Formatter::Format("Interupt and join threads in working queue {0}", queue_name_));

      std::set<std::shared_ptr<boost::thread>> completedThreads;

      int attemptCount = 10000 / 250; // 10 seconds, 250 ms between each

      for (int i = 0; i < attemptCount; i++)
      {
         for (std::shared_ptr<boost::thread> thread : workerThreads_)
         {
            thread->interrupt();
         }

         for(std::shared_ptr<boost::thread> thread : workerThreads_)
         {
            if (thread->timed_join(boost::posix_time::milliseconds(1)))
            {
               completedThreads.insert(thread);
            }
         }

         for(std::shared_ptr<boost::thread> thread : completedThreads)
         {
            auto iter = workerThreads_.find(thread);

            workerThreads_.erase(iter);
         }

         completedThreads.clear();

         if (workerThreads_.size() == 0)
         {
            LOG_DEBUG(Formatter::Format("All threads are joined in queue {0}.", queue_name_));
            return;
         }


         boost::lock_guard<boost::recursive_mutex> guard(runningTasksMutex_);
         auto iter = runningTasks_.begin();
         if (iter != runningTasks_.end())
         {
            LOG_DEBUG(Formatter::Format("Still {0} remaining threads in queue {1}. First task: {2}", workerThreads_.size(), queue_name_, (*iter).second.name));
         }
         else
         {
            LOG_DEBUG(Formatter::Format("Still {0} remaining threads in queue {1}. First task: <Unknown>", workerThreads_.size(), queue_name_));

         }

         Sleep(250);
      }

      LOG_DEBUG(Formatter::Format("Given up waiting for threads to join in queue {0}.", queue_name_));
   }

   const String&
   WorkQueue::GetName() const
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Returns the name of this queue.
   //---------------------------------------------------------------------------
   {
      return queue_name_;
   }
}
