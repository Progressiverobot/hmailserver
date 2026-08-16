// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../BO/ScheduledTask.h"
#include "../Threading/Task.h"

namespace HM
{
   // Runs one unattended directory synchronisation, on a work queue thread.
   //
   // Separate from the scheduled task below because DirectorySync::Run is a network
   // read of an entire directory followed by a run of account writes, and the
   // scheduler owns ONE thread for every scheduled task in the server. Running the
   // synchronisation on it would stop ACME renewal, log retention, the consistency
   // check and the backup schedule for as long as a directory takes to answer - which,
   // against a directory that is reachable but slow, is the configured timeout per
   // page. The scheduler's job here is to decide; this class does the work.
   class DirectorySyncTask : public Task
   {
   public:
      DirectorySyncTask();
      virtual ~DirectorySyncTask();

   private:
      virtual void DoWork();
   };

   // Decides when an unattended synchronisation is owed and queues one.
   //
   // Registered only when [LDAP] SyncScheduleMinutes is set, so a default installation
   // - and every installation that upgrades into this build without asking for it -
   // gets no task at all and behaves exactly as it did before. The interval is read
   // once, at registration: changing it takes effect at the next service start, which
   // the Control Panel says on the card that edits it rather than leaving an
   // administrator to discover it.
   //
   // Also registered as a RunOnce task at start-up, and that is a decision worth
   // stating because the backup schedule deliberately does the opposite. A backup is
   // expensive, not idempotent, and wants a wall-clock window, so BackupScheduleTask
   // ticks every minute and works out whether a run is owed from the destination. A
   // synchronisation is none of those things: it is one directory read, it is
   // idempotent, and a run that finds nothing to do costs a search. Running one at
   // start-up therefore buys the property that matters most about a schedule - that a
   // server which is restarted more often than the interval still synchronises. With
   // an interval-only schedule, ScheduledTask computes the next run as "now plus the
   // interval" whenever the scheduler is built, so a daily interval on a server that
   // is restarted every morning would never fire once.
   class DirectorySyncScheduleTask : public ScheduledTask
   {
   public:
      DirectorySyncScheduleTask();
      virtual ~DirectorySyncScheduleTask();

      // Zero when no schedule is configured. Read by Application so that the task is
      // not registered at all in that case, rather than registered and inert - an
      // inert task still occupies a scheduler slot and still appears to be doing
      // something to anyone reading the code.
      static int ScheduleMinutes();

      virtual void DoWork();
   };
}
