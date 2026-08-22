// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../BO/ScheduledTask.h"
#include "../Util/VariantDateTime.h"

namespace HM
{
   // Scheduled task that starts a backup on a timetable, so that an installation
   // gets backups without anything outside the server having to call
   // BackupManager.StartBackup() over COM. Before this existed that external
   // caller was the only way to get a backup at all, which in practice meant most
   // installations had none - the worst property a mail server can have.
   //
   // Configured in hMailServer.ini [Settings]. Every setting is absent in the
   // shipped configuration, so a default install behaves exactly as it did before:
   // Application::CreateScheduledTasks_ does not even construct this task unless
   // IsEnabled() is true.
   //
   //    ScheduledBackupTime=02:00          daily, at that local time
   //    ScheduledBackupIntervalMinutes=360 every N minutes
   //    ScheduledBackupKeepCount=7         retention: keep this many archives
   //    ScheduledBackupMaxAgeDays=30       retention: delete archives older than this
   //
   // ScheduledBackupTime wins when both schedule modes are set. What goes into a
   // backup, and where it is written, keeps coming from the existing backup
   // settings (Settings.Backup in the COM API and the administrative UI), so there
   // is one description of what a backup is and this only decides when one happens.
   //
   // WHAT THIS TASK DOES, AND WHAT IT DELIBERATELY DOES NOT DO
   //
   // It decides whether a run is owed and hands it to BackupManager, which queues
   // it on the maintenance work queue. It never performs the backup itself. That is
   // not a style preference: Scheduler::RunTasks_ calls Run() on every due task
   // synchronously, on the one thread that also runs greylist cleaning,
   // expired-record removal, TLS reporting, log retention, the message-store
   // consistency check and the work-queue health check. A backup can run for hours,
   // and running it there would stop all of those for the duration - the greylist
   // would stop expiring, so mail would be deferred that should have been let
   // through. Mail flow itself does not run on the maintenance queue either, so a
   // queued backup competes with administrative and maintenance work, not with
   // delivery.
   //
   // The scheduler's thread is also why the pre-flight checks are bounded the way
   // they are. Every check that touches the destination can block for as long as the
   // operating system takes to give up on an unreachable network share, and a
   // destination that is down stays down for hours. So a run that is refused is not
   // simply retried on the next tick: the task backs off, 5 minutes then 10, 20, 40
   // and finally an hour, which bounds the scheduler-thread exposure to a handful of
   // share timeouts an hour instead of sixty, while still picking the backup up
   // within minutes of the share coming back.
   //
   // THE FAILURE MODES THIS EXISTS TO HANDLE
   //
   // A run starting while the previous one is still going. BackupManager is
   // single-flight and, since this change, holds that slot process-wide rather than
   // per-instance, because the manager is destroyed and recreated on every
   // reinitialization while a backup can still be running - see the comment on
   // ClaimRunSlot_. When a run is refused because a *backup* is already in progress,
   // the slot is treated as satisfied: the backup the schedule wanted is happening
   // right now. Retrying instead would turn an interval shorter than the time a
   // backup takes into a continuous back-to-back backup loop that never lets the
   // server idle - far worse than one skipped slot, and the skip is logged. A
   // *restore* in progress is treated the other way round, as something merely in
   // the way: a restore is exactly the moment when the next backup matters most, so
   // that slot is retried rather than written off.
   //
   // A destination that has filled up, gone away, or was never configured. The
   // pre-flight refuses to start a run that is certain to fail, rather than claiming
   // the single backup slot and reporting a critical error for as long as the
   // condition lasts; an unattended schedule is where a loop of failures is least
   // likely to be noticed and most likely to bury the log. It also compares the free
   // space against the size of the previous archive and warns *before* the run, and
   // says so if the destination is on the same volume as the message store. A
   // destination that fills up mid-run is caught by BackupExecuter: every write
   // failure fails the backup, retention does not run, and the archive that could
   // not be completed is removed so that it cannot masquerade as a good backup
   // afterwards.
   //
   // A backup started while the database is unavailable. This is the dangerous one,
   // and most of the fix is in BackupExecuter. A backup reads the domains, accounts
   // and settings out of the database; when those reads fail the collections come
   // back empty, an empty collection stores no XML, and the archive that got written
   // contained no domains at all - and, before this change, was reported as a
   // successful backup. Restoring it deletes every domain on the server and puts
   // nothing back. So the pre-flight refuses to start a scheduled backup while the
   // connection pool is down, and BackupExecuter aborts a backup whose database
   // reads failed instead of writing a hollow archive. Together with retention
   // deleting nothing until a backup has completed, that is what stops a database
   // outage from replacing good backups with useless ones.
   //
   // Deleting an old archive before the new one exists. Retention runs from
   // BackupExecuter, past the point where the archive is complete, and never deletes
   // the archive that run produced. See BackupRetention.
   class BackupScheduleTask : public ScheduledTask
   {
   public:
      BackupScheduleTask(void);
      ~BackupScheduleTask(void);

      virtual void DoWork();

      // Whether any schedule is configured. Read by
      // Application::CreateScheduledTasks_ so that an installation which has not
      // asked for scheduled backups does not get the task at all, and re-read in
      // DoWork so that the task is inert if it ever outlives the setting.
      static bool IsEnabled();

      // Reads the ScheduledBackupTime setting: "HH:MM", 24-hour, local time.
      // Public because it is what the daily schedule actually means, and because it
      // is the one part of this class that is a pure function of its input.
      static bool ParseTimeOfDay(const String &value, int &hour, int &minute);

   private:

      bool IsDueNow_(const DateTime &now);
      void Seed_(const DateTime &now);
      void ConsumeSlot_(const DateTime &now);

      bool PreflightOk_(const String &destination, int backupOptions);
      void CheckDestinationCapacity_(const String &destination, unsigned __int64 tick);

      // Reports a run that was not started. errorCode 0 means "backup log and
      // application log only". Rate limited both ways: one log line per hour however
      // many ticks are refused in between, and one error-log entry per distinct
      // reason rather than one per tick - so a second, different reason is never
      // swallowed by the first, and a reason that repeats is not reported again.
      void ReportSkipped_(const String &reason, int errorCode);

      // Escalating retry delay after a refused run, and the fixed short delay used
      // when something unrelated merely happens to be in the way.
      void BackOff_(unsigned __int64 tick);
      void RetryIn_(unsigned __int64 tick, unsigned int minutes);
      void ClearFailureState_();

      static String FormatDay_(const DateTime &value);

      // The destination is read once per service start, in the first DoWork rather
      // than in the constructor: a backup destination is very often a network share,
      // and enumerating a share that is not answering must not delay service
      // startup. By the first tick the scheduler thread is only running maintenance.
      bool seeded_;

      // Whether any previous backup is known - either found in the destination while
      // seeding, or started by this task. With no backup anywhere and an interval
      // schedule configured, the first run is due immediately: an installation that
      // has asked for backups and has none is the exact state this feature exists to
      // fix, and anchoring the first interval to the service start instead would
      // mean a server that restarts more often than the interval never backs up at
      // all.
      //
      // The daily mode reaches the same conclusion through last_started_day_ being
      // empty: with no backup in the destination, a configured time that has already
      // passed today is treated as owed. So switching a schedule on during the
      // working day does take one backup straight away rather than waiting for
      // tonight. That is deliberate - the alternative leaves a server with no backup
      // for up to another day - but it is the one part of the timing an
      // administrator might reasonably want the other way round, and it is exactly
      // one line: seed last_started_day_ with today's date when there is no history.
      bool have_history_;

      DateTime last_started_;
      String last_started_day_;

      // Monotonic (GetTickCount64), not wall clock. A retry delay measured against
      // the wall clock would be skipped entirely by a clock that stepped forwards,
      // and would hang for the duration of the step if it went backwards. The
      // schedule itself has to use the wall clock, because "02:00" and "the same
      // calendar day" have no meaning without it; the difference is deliberate.
      unsigned __int64 next_attempt_tick_;
      unsigned int consecutive_failures_;

      unsigned __int64 last_skip_log_tick_;
      String last_reported_reason_;

      unsigned __int64 last_capacity_check_tick_;
      bool free_space_warning_reported_;
      bool same_volume_warning_reported_;
   };
}
