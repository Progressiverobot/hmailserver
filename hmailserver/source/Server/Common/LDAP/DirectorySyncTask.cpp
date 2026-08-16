// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include "DirectorySyncTask.h"

#include "DirectorySync.h"
#include "LdapSettings.h"

#include "../Application/Application.h"
#include "../Threading/WorkQueue.h"
#include "../Util/ServerStatus.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DirectorySyncTask::DirectorySyncTask() :
      Task("DirectorySync")
   {
   }

   DirectorySyncTask::~DirectorySyncTask()
   {
   }

   void
   DirectorySyncTask::DoWork()
   {
      DirectorySyncOptions options;

      options.mode = DirectorySyncMode::ModeApply;

      // Every linked domain, because a schedule that covered one domain would need a
      // way to say which - and an administrator who wants that can run the COM call
      // per domain instead. The scheduled case is "keep this server in step with the
      // directory", which is all of them.
      options.domain_name = _T("");

      // The one decision this class makes, and it is deliberately the cautious one.
      //
      // Disabling is what turns a synchronisation from something that only ever adds
      // into something that can take a working mailbox away, and unattended is exactly
      // the context where nobody is reading the report that would have shown it about
      // to happen. DirectorySync already refuses to disable on a truncated or empty
      // enumeration, and refuses per domain when nothing in that domain was seen - but
      // those guards protect against a directory that answered oddly, not against a
      // filter an administrator edited last week and has not looked at since.
      //
      // So the schedule creates and updates, and leaves departures to a person who has
      // read a preview. That is not a permanent position - a SyncScheduleDisables
      // setting is the obvious next step - but shipping the schedule with the
      // destructive half switched on by nothing more than a minute count would be the
      // wrong way round.
      options.disable_missing = false;

      const DirectorySyncResult result = DirectorySync::Run(options);

      // Both outcomes are logged, and the successful one is not noise. A schedule is
      // invisible by construction: the only evidence it ran at all is what it wrote
      // here, and an administrator asking "did the sync run last night" is asking a
      // question the application log has to be able to answer. DirectorySync::Run logs
      // its own summary on the paths that got as far as planning; this covers the ones
      // that did not.
      if (!result.succeeded)
      {
         // Not reported through ErrorManager here. Run already did that - it reports
         // every refusal in apply mode, which this is - and a second entry for the
         // same failure would double every line in the error log for as long as the
         // directory stays unreachable.
         LOG_APPLICATION(Formatter::Format("DirectorySync: the scheduled synchronisation did not run: {0}",
            result.failure));
      }
   }

   DirectorySyncScheduleTask::DirectorySyncScheduleTask()
   {
   }

   DirectorySyncScheduleTask::~DirectorySyncScheduleTask()
   {
   }

   int
   DirectorySyncScheduleTask::ScheduleMinutes()
   {
      const LdapConfiguration configuration = LdapSettings::Instance()->GetConfiguration();

      // Both halves, not just the interval. A schedule configured while LDAP is
      // switched off has nothing to read, and registering a task that will refuse on
      // every tick - writing an error each time, because an apply reports its refusals
      // - is worse than not registering one. The administrator who sets an interval
      // and forgets to switch LDAP on is told by the Control Panel's guard, not by an
      // hourly error.
      if (!configuration.enabled)
         return 0;

      return configuration.sync_schedule_minutes;
   }

   void
   DirectorySyncScheduleTask::DoWork()
   {
      // The state guard, taken from BackupScheduleTask, which carries a comment naming
      // this exact hazard. It is needed here for the same reason and only here: this is
      // the one other scheduled task that hands its work to the MAINTENANCE queue
      // instead of running inline on the scheduler's thread. The other scheduled tasks
      // are safe without it because stopping the scheduler stops them; this one can
      // have already escaped onto another queue.
      //
      // The window is real rather than theoretical. StopServers interrupts and joins
      // the scheduler thread, but the maintenance queue is not removed until
      // ExitInstance - so a tick landing during shutdown would queue a run that creates
      // accounts, against a database whose connection pool is being torn down under it.
      // The symmetric case at start-up is the same shape: the RunOnce task fires
      // promptly, and "promptly" must still mean "after the server says it is running".
      if (ServerStatus::Instance()->GetState() != ServerStatus::StateRunning)
         return;

      // Re-read, because registration happened once at start-up and the ini has not
      // stopped changing since. An administrator who switches [LDAP] Enabled to 0 -
      // which is the first thing anyone does when a directory is misbehaving - would
      // otherwise leave a task that still fires, still runs an apply, and writes a High
      // error every time, because an apply reports its refusals by design. The result
      // is an error log filling up hourly to say the administrator did the thing they
      // meant to do. Skipping quietly is right precisely because the state is intended.
      //
      // Deliberately NOT re-reading the interval here. The scheduler owns when this
      // fires, changing that would need the task re-registered, and the Control Panel
      // card says a schedule change needs a restart. Honouring "off" without honouring
      // a new interval is the asymmetry worth having: one prevents noise the operator
      // did not ask for, the other would silently disagree with what the page says.
      if (!LdapSettings::Instance()->GetConfiguration().enabled)
         return;

      std::shared_ptr<WorkQueue> workQueue = Application::Instance()->GetMaintenanceWorkQueue();

      if (!workQueue)
      {
         // Not silent, and the comment that used to say so was wrong:
         // GetMaintenanceWorkQueue reports HM5118 at Medium before returning null. That
         // is the right behaviour and this branch does not add to it - reaching here
         // with the server in StateRunning is a genuine fault worth a log line, and the
         // guard above is what keeps an ordinary shutdown from producing one.
         return;
      }

      std::shared_ptr<DirectorySyncTask> task = std::shared_ptr<DirectorySyncTask>(new DirectorySyncTask());

      // TaskMayBlock, for the same reason a scheduled backup is classified that way: a
      // synchronisation holds its thread for a directory read plus a run of account
      // writes, which against a large directory is minutes. Classifying it keeps it
      // inside the queue's blocking-task cap, so an unattended sync cannot be the
      // reason the last free maintenance thread is missing when an administrator
      // starts a restore or clears the delivery queue.
      //
      // Both caller constraints the class carries hold: this task waits on no other
      // task on this queue, and nothing waits on its GetIsStartedEvent().
      workQueue->AddTask(task, WorkQueue::TaskMayBlock);
   }
}
