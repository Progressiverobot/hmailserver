// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Backup;
   class Task;

   class BackupManager
   {
   public:
      BackupManager(void);
      ~BackupManager(void);

      // What happened to a run requested by the scheduler. Reported to the caller
      // instead of being turned into an error here, because only the caller knows
      // whether a collision means "the backup the schedule wanted is already
      // happening" or "try again shortly".
      enum ScheduledBackupResult
      {
         ScheduledBackupQueued = 1,
         ScheduledBackupAlreadyRunning = 2,
         ScheduledBackupRestoreRunning = 3,
         ScheduledBackupNoWorkQueue = 4
      };

      bool StartBackup();
      // Backups the server according to the settings
      // specified in the configuration.

      // The same work, requested by BackupScheduleTask rather than by an
      // administrator over COM. It exists as a separate entry point because of
      // what it does *not* do: a scheduled run that collides with a backup
      // already in progress is a normal, expected outcome, not a critical error.
      // StartBackup routes that case through OnBackupFailed, which reports
      // ErrorManager::Critical - on a schedule whose interval is shorter than the
      // time a backup takes, that would fill the error log with critical entries
      // describing a server that is working exactly as configured, and would
      // break every fixture's clean-error-log assertion along the way.
      ScheduledBackupResult StartScheduledBackup();

      std::shared_ptr<Backup> LoadBackup(const String &sZipFile) const;
      // Loads a backup from an XML stream and returns
      // an backup object that contains backup information.

      bool StartRestore(std::shared_ptr<Backup> pBackup);
      // Starts a restore of the backup. Restors the part of the
      // backup specified by iRestoreMode.

      void OnThreadStopped();
      // Called by backup/restore thread when operation has finished.

      void SetStatus(const String &sStatus);
      String GetStatus();

      void OnBackupCompleted();
      void OnBackupFailed(const String &sReason);

   private:

      enum RunKind
      {
         RunBackup = 1,
         RunRestore = 2
      };

      // THE SINGLE-FLIGHT GUARD, AND WHY IT IS PROCESS-WIDE RATHER THAN A MEMBER
      //
      // A backup and a restore both write into the backup destination and both
      // rewrite the same hMailServerBackup.xml and the same DataBackup folder, so
      // exactly one may run at a time. That was previously a plain bool member,
      // read and then written without the mutex, and it was good enough while the
      // only way to start a backup was an administrator clicking twice.
      //
      // Two things break it once a schedule exists:
      //
      //  * The read-then-write race becomes reachable without anybody doing
      //    anything. The COM call arrives on an apartment thread while the
      //    scheduler's request arrives on the scheduler's thread; both could see
      //    "not running" and both queue a backup. Claiming under the mutex fixes
      //    that half.
      //
      //  * BackupManager itself is destroyed and recreated by
      //    Application::ExitInstance/InitInstance on every Reinitialize, while a
      //    backup may still be running. WorkQueue::Stop only waits ten seconds for
      //    its threads (deliberately bounded), and a backup of a real message
      //    store takes far longer than that - so the running BackupTask outlives
      //    the manager that started it, and a fresh manager with a fresh member
      //    flag would happily start a second backup into the same destination
      //    while the first is still copying into it. Per-instance state cannot see
      //    that; process-wide state can.
      //
      // The slot is held as a weak_ptr to the task that owns it rather than as a
      // bool, and that is what makes process-wide state safe. A bool would leak:
      // AddTask can accept a task that never runs (WorkQueue::Stop drops tasks
      // still waiting for a blocking slot), OnThreadStopped would then never be
      // called, and no backup could ever start again for the life of the process.
      // A weak_ptr self-heals - when the task object is destroyed, whether it ran
      // or was dropped, the slot is free again.
      static bool ClaimRunSlot_(std::shared_ptr<Task> owner, RunKind kind);
      static void ReleaseRunSlot_();

      // Whether the operation currently holding the slot is a restore. The
      // scheduler treats the two differently: a backup already in progress is the
      // backup the schedule wanted, while a restore is unrelated work that
      // happens to be in the way - and a restore is precisely when the next
      // backup matters most, so that slot must not be written off.
      static bool RestoreHoldsRunSlot_();

      static boost::mutex run_slot_mutex_;
      static std::weak_ptr<Task> run_slot_owner_;
      static bool run_slot_is_restore_;

      boost::recursive_mutex mutex_;
      String log_;
   };
}
