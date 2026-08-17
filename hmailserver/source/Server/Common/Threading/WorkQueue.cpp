// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
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
      live_worker_count_(0),
      stopping_(false),
      max_simultaneous_(0),
      queue_name_ (sQueueName)
   {
      SetMaxSimultaneous(iMaxSimultaneous);

      LOG_DEBUG(Formatter::Format("Creating work queue {0}", queue_name_));
   }

   void
   WorkQueue::SetMaxSimultaneous(int iMaxSimultaneous)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Sets the size of the thread pool. Clamped at both ends.
   //
   // The upper clamp has always been here. The lower one is new and is the more
   // important of the two: the pool size of the asynchronous queue comes from
   // MaxNumberOfAsynchronousTasks, which is administrator-settable over COM and
   // through the Control Panel with no validation anywhere on the way in, and this
   // function's old body assigned a signed value straight into an unsigned member.
   // Zero produced a queue with no threads at all - Start()'s loop ran zero times -
   // which accepts every task handed to it and runs none of them. On the
   // asynchronous queue that is every received message stopping at end-of-data,
   // for ever, with nothing written anywhere; a negative value wrapped to a huge
   // unsigned number and was then clamped to the maximum 100 threads. A pool of one
   // is slow. A pool of none is a silent outage, and a pool of 100 because someone
   // typed -1 is not what was asked for either.
   //---------------------------------------------------------------------------
   {
      unsigned int requested;

      if (iMaxSimultaneous < 1)
         requested = 1;
      else if (iMaxSimultaneous > 100)
         // Everything over this won't be good for stability.
         requested = 100;
      else
         requested = static_cast<unsigned int>(iMaxSimultaneous);

      if (static_cast<int>(requested) != iMaxSimultaneous)
      {
         LOG_APPLICATION(Formatter::Format("Work queue {0} was configured for {1} thread(s), which is outside the supported range of 1 to 100. Using {2}.",
                                           queue_name_, iMaxSimultaneous, requested));
      }

      unsigned int running = worker_thread_count_.load();

      if (running != 0 && requested != max_simultaneous_)
      {
         // Start() is what turns this number into threads, and it has already run.
         // Said out loud because an administrator has just changed a setting and
         // would otherwise have no way of knowing that the pool did not move. The
         // new value does take effect immediately on the blocking-task cap below.
         LOG_APPLICATION(Formatter::Format("Work queue {0} is now configured for {1} thread(s) but is running {2}. The number of threads itself changes when the server is restarted.",
                                           queue_name_, requested, running));
      }

      max_simultaneous_ = requested;

      // A larger pool may have made room for tasks that are waiting for a slot.
      DispatchBlockingTasks_();
   }

   WorkQueue::~WorkQueue(void)
   {
      try
      {
         if (workerThreads_.empty())
            return;

         // Stop() gave up on these, and they are still inside io_context_.run() -
         // on an io_context that is a member of this object and is about to be
         // destroyed underneath them. Returning here is not a leaked thread, it is
         // a use-after-free on shutdown. One more bounded attempt costs a shutdown
         // that is already stalled a few more seconds and is the difference between
         // a slow stop and a faulting one.
         stopping_ = true;
         io_context_.stop();

         if (JoinWorkers_(5000))
            return;

         LOG_APPLICATION(Formatter::Format("Work queue {0} is being destroyed with {1} worker thread(s) still running.",
                                           queue_name_, workerThreads_.size()));
      }
      catch (...)
      {
         // A destructor is not a place to throw from.
      }
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

      if (stopping_.load())
      {
         // The io_context has been stopped, so a task posted from here on is
         // accepted, held, and then destroyed without ever running. That was the
         // behaviour before this check too - the difference is that it was silent,
         // and what gets discarded at this point is work a live session has already
         // been told would happen.
         LOG_APPLICATION(Formatter::Format("Task {0} was not started because work queue {1} is shutting down.",
                                           task_name, queue_name_));
         return;
      }

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
      catch (const boost::thread_interrupted&)
      {
         // A blocking slot that is never given back would permanently shrink the
         // pool, so the bookkeeping has to be undone on every exit path.
         FinishTask_(task_id, may_block);

         // Passed on, unlike everything below: it means the server is shutting
         // down, and swallowing it would consume the interruption request and
         // leave Stop() waiting on a thread that no longer knows it was asked to
         // stop. The barrier at the top of the worker turns it into a clean exit.
         throw;
      }
      catch (...)
      {
         FinishTask_(task_id, may_block);

         // Not rethrown. Rethrowing was what made one failed task cost a worker
         // thread for the life of the process: the exception left this handler,
         // came out of io_context::run(), and the only catch above it matched one
         // single error code and rethrew the rest - onto a boost::thread, which is
         // std::terminate and a dead mail server. Even contained, the thread was
         // gone while worker_thread_count_ still claimed it was there.
         //
         // ExceptionHandler::Run has already logged and dumped anything the task
         // itself threw, so this path is reached only if that machinery failed.
         LOG_APPLICATION(Formatter::Format("Task {0} in work queue {1} ended in an exception that its own handler did not contain. The worker thread has been kept.",
                                           name, queue_name_));
         return;
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

      // Counted as the threads still inside io_context::run(), not as the threads
      // Start() once created. The two differ exactly when a worker has left, and a
      // queue that has lost workers is the queue this report exists for: measured
      // against the original count, "every thread has been busy for longer than the
      // threshold" could never again be true for such a queue, so the one state
      // most worth reporting was the one state that could not be reported.
      unsigned int thread_count = live_worker_count_.load();

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
         // Re-armed on the way out, for the same reason the "not all stalled" path
         // below does it: a stall that begins after this sample should be reported
         // on the tick it is first seen, not held back because a previous stall
         // left the one-report-per-stall latch set.
         boost::lock_guard<boost::recursive_mutex> guard(runningTasksMutex_);
         stall_reported_ = false;

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

      stopping_ = false;

      io_context_.restart();

      for ( std::size_t i = 0; i < max_simultaneous_; ++i )
      {
         try
         {
            std::shared_ptr<boost::thread> thread = std::shared_ptr<boost::thread>
               (new boost::thread(std::bind( &WorkQueue::IoServiceRunWorker, this )));

            workerThreads_.insert(thread);

            live_worker_count_++;
         }
         catch (...)
         {
            // Out of threads, or out of memory. A pool smaller than asked for is
            // reported below; the thing that must not happen silently is the queue
            // coming up and then accepting work it can never run.
            break;
         }
      }

      worker_thread_count_ = static_cast<unsigned int>(workerThreads_.size());

      if (workerThreads_.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 6077, "WorkQueue::Start",
            Formatter::Format("Work queue {0} started no worker threads at all. Every task given to this queue will be accepted and never run.", queue_name_));
      }
      else if (workerThreads_.size() < static_cast<size_t>(max_simultaneous_))
      {
         LOG_APPLICATION(Formatter::Format("Work queue {0} started {1} of the {2} threads it was configured for.",
                                           queue_name_, workerThreads_.size(), max_simultaneous_));
      }

      // Threads exist now, so the cap is known and anything accepted before this
      // point can be released.
      DispatchBlockingTasks_();

      LOG_DEBUG(Formatter::Format("Started work queue {0}", queue_name_));
   }

   void
   WorkQueue::IoServiceRunWorker()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // The top of one worker thread. There is nothing above this frame to catch
   // anything, so nothing may leave it: an exception that escaped here would be
   // std::terminate and the whole mail server would be gone because one task threw.
   // The reporting paths are all inside RunWorker_; this exists only to catch a
   // failure in them, which is why it is deliberately silent.
   //---------------------------------------------------------------------------
   {
      try
      {
         RunWorker_();
      }
      catch (...)
      {
      }

      // Last, and cannot throw. The stall detector counts the threads that are
      // still in here, and a count that never comes down describes a pool that no
      // longer exists.
      live_worker_count_--;
   }

   void
   WorkQueue::RunWorker_()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Runs this queue's io_context until the queue is stopped, containing anything
   // a handler throws on the way.
   //
   // A handler that throws propagates out of io_context::run(), and asio documents
   // the io_context as still usable afterwards - run() may simply be called again
   // to pick up the next handler. Doing that is the whole difference between a
   // worker that recovers and a pool that shrinks by one thread per incident and
   // never grows back, with worker_thread_count_ still claiming the thread is
   // there. Before this loop, run() was called once and every exception except one
   // single error code was rethrown onto a boost::thread.
   //---------------------------------------------------------------------------
   {
      LOG_DEBUG(Formatter::Format("Running worker in work queue {0}", queue_name_));

      for (;;)
      {
         bool resume = false;

         try
         {
            io_context_.run();

            // Returned normally: the io_context was stopped. There is no "ran out
            // of work" case here, because work_ holds the context open for life.
         }
         catch (const boost::thread_interrupted&)
         {
            // Shutting down.
            boost::this_thread::disable_interruption disabled;
         }
         catch (const boost::system::system_error& error)
         {
            if (error.code().value() != ERROR_ABANDONED_WAIT_0)
            {
               // ERROR_ABANDONED_WAIT_0 is not a failure here: GetQueuedCompletionStatus
               // returns it when the completion port handle is closed while a call is
               // outstanding, which is what a shutdown looks like from inside.
               ReportWorkerException_(String(error.what()));
               resume = true;
            }
         }
         catch (const std::exception& error)
         {
            ReportWorkerException_(String(error.what()));
            resume = true;
         }
         catch (...)
         {
            ReportWorkerException_(_T("Unrecognized exception"));
            resume = true;
         }

         if (!resume)
            break;

         if (stopping_.load() || io_context_.stopped())
         {
            // Nothing left to go back for.
            break;
         }
      }

      LOG_DEBUG(Formatter::Format("Worker exited in work queue {0}", queue_name_));
   }

   void
   WorkQueue::ReportWorkerException_(const String &detail)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Records an exception that reached the top of a worker thread. Called from
   // inside a catch block on a thread with nothing above it, so it may not throw
   // under any circumstances - including one raised by the reporting itself.
   //---------------------------------------------------------------------------
   {
      try
      {
         String message =
            Formatter::Format("An exception reached the top of a worker thread in work queue {0}. It has been contained and the thread returned to the queue. Detail: {1}",
                              queue_name_, detail);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6075, "WorkQueue::RunWorker_", message);
      }
      catch (...)
      {
      }
   }

   bool
   WorkQueue::JoinWorkers_(int max_wait_ms)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Bounded wait for every worker thread to leave. Returns true when none remain.
   //
   // The running-task lock is taken around the diagnostic read and released again
   // before the sleep. Held across it, as it was, this loop blocked the very
   // threads it was waiting for: a worker finishing a task needs that same lock to
   // deregister itself, and a worker starting one needs it to register. The lock
   // was reacquired on every pass and released only after Sleep(250) returned, so
   // for the whole ten seconds the pool was blocked roughly whenever it tried to
   // make progress - which turned a shutdown that should take milliseconds into one
   // that runs to the timeout and then abandons live threads on a dying object.
   //---------------------------------------------------------------------------
   {
      // The retry interval is what an interrupted-but-not-yet-exited worker
      // costs, and it used to be 250ms - paid almost every time, because the
      // timed_join below waits only 1ms and a thread woken from a condition
      // wait rarely reaches its exit within one millisecond of the interrupt.
      // Every queue then missed its first pass and slept the full 250ms, so a
      // server with seven queues took ~1.9 seconds to stop while completely
      // idle - measured, and measurably dependent on nothing but scheduler
      // wakeup latency, which the machine's timer state changes from day to
      // day. 25ms keeps the wait bounded by when the threads actually leave
      // rather than by the penalty for asking too early.
      const int interval_ms = 25;

      // The diagnostic line keeps its old once-a-second cadence: at the new
      // interval it would otherwise repeat forty times a second into the
      // debug log of every slow shutdown it exists to explain.
      const int passes_per_log = 1000 / interval_ms;

      int attempt_count = max_wait_ms / interval_ms;

      if (attempt_count < 1)
         attempt_count = 1;

      std::set<std::shared_ptr<boost::thread>> completedThreads;

      for (int i = 0; i < attempt_count; i++)
      {
         for (std::shared_ptr<boost::thread> thread : workerThreads_)
         {
            thread->interrupt();
         }

         for (std::shared_ptr<boost::thread> thread : workerThreads_)
         {
            if (thread->timed_join(boost::posix_time::milliseconds(1)))
            {
               completedThreads.insert(thread);
            }
         }

         for (std::shared_ptr<boost::thread> thread : completedThreads)
         {
            workerThreads_.erase(thread);
         }

         completedThreads.clear();

         if (workerThreads_.empty())
            return true;

         if (i % passes_per_log == passes_per_log - 1)
         {
            String first_task;

            {
               boost::lock_guard<boost::recursive_mutex> guard(runningTasksMutex_);

               auto iter = runningTasks_.begin();

               if (iter != runningTasks_.end())
                  first_task = (*iter).second.name;
            }

            if (first_task.IsEmpty())
               first_task = _T("<Unknown>");

            LOG_DEBUG(Formatter::Format("Still {0} remaining threads in queue {1}. First task: {2}",
                                        workerThreads_.size(), queue_name_, first_task));
         }

         Sleep(interval_ms);
      }

      return workerThreads_.empty();
   }

   void
   WorkQueue::Stop()
   {
      LOG_DEBUG(Formatter::Format("Stopping working queue {0}.", queue_name_));

      // Set before the io_context is stopped, so that a task handed over from this
      // point on is reported by AddTask rather than accepted and quietly dropped.
      stopping_ = true;

      // Prevent new tasks from being started.
      io_context_.stop();

      worker_thread_count_ = 0;

      int discarded = 0;

      {
         // Tasks still waiting for a blocking slot can never run now. Dropping
         // them releases the objects they keep alive, such as the connection the
         // task was going to answer on.
         boost::lock_guard<boost::mutex> guard(blockingMutex_);

         discarded = static_cast<int>(pending_blocking_tasks_.size());
         pending_blocking_tasks_.clear();
      }

      if (discarded > 0)
      {
         // These were counted as queued when they were accepted, so the depth has
         // to come back down with them or the queue reports a backlog for ever.
         queue_depth_ -= discarded;

         LOG_APPLICATION(Formatter::Format("{0} task(s) waiting for a thread in work queue {1} were discarded because the queue is shutting down.",
                                           discarded, queue_name_));
      }

      LOG_DEBUG(Formatter::Format("Interupt and join threads in working queue {0}", queue_name_));

      if (JoinWorkers_(10000))
      {
         LOG_DEBUG(Formatter::Format("All threads are joined in queue {0}.", queue_name_));
         return;
      }

      // Application level rather than an error record: a worker still inside a
      // wedged external dependency - a scanner, a script, a database that has
      // stopped answering - is a state a correctly configured server can reach, and
      // it is the operator who has to act on it. It was LOG_DEBUG, which on a
      // default log level means the one line explaining a ten second stop, and the
      // threads left running on this object, were both invisible.
      LOG_APPLICATION(Formatter::Format("Gave up waiting for {0} thread(s) in work queue {1} to finish after 10 seconds. They are still running, so the queue cannot be freed until they stop.",
                                        workerThreads_.size(), queue_name_));
   }

   void
   WorkQueue::SetMonitorForStalls(bool monitor)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Opts this queue in to the stall report. See the header for why it is opt-in.
   //---------------------------------------------------------------------------
   {
      monitor_for_stalls_.store(monitor);
   }

   bool
   WorkQueue::GetMonitorForStalls() const
   {
      return monitor_for_stalls_.load();
   }

   const String &
   WorkQueue::GetName() const
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Returns the name of this queue.
   //---------------------------------------------------------------------------
   {
      return queue_name_;
   }
}
