// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "BackupManager.h"

#include "Backup.h"
#include "BackupRestorer.h"

#include "../Scripting/ScriptServer.h"
#include "../Scripting/ScriptObjectContainer.h"
#include "../Scripting/Result.h"

#include "..\Util\Compression.h"

#include "../Threading/WorkQueueManager.h"
#include "BackupTask.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // See the header for why the single-flight guard lives here rather than in the
   // object: this manager is destroyed and recreated on every reinitialization,
   // and a backup routinely outlives one.
   boost::mutex BackupManager::run_slot_mutex_;
   std::weak_ptr<Task> BackupManager::run_slot_owner_;
   bool BackupManager::run_slot_is_restore_ = false;

   BackupManager::BackupManager(void)
   {
   }

   BackupManager::~BackupManager(void)
   {
   }


   bool
   BackupManager::ClaimRunSlot_(std::shared_ptr<Task> owner, RunKind kind)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Claims the one backup-or-restore slot on behalf of owner, or returns false
   // because something else still holds it. Never call anything that reports or
   // fires a scripting event while holding this lock: OnBackupFailed runs an
   // administrator's script, which can re-enter this object through the COM API.
   //---------------------------------------------------------------------------()
   {
      boost::lock_guard<boost::mutex> guard(run_slot_mutex_);

      // expired() rather than a flag, so a task that was accepted but never ran -
      // WorkQueue::Stop drops tasks that are still waiting for a blocking slot -
      // releases the slot when it is destroyed instead of holding it until the
      // service is restarted.
      if (!run_slot_owner_.expired())
         return false;

      run_slot_owner_ = owner;
      run_slot_is_restore_ = (kind == RunRestore);

      return true;
   }

   void
   BackupManager::ReleaseRunSlot_()
   {
      boost::lock_guard<boost::mutex> guard(run_slot_mutex_);

      run_slot_owner_.reset();
      run_slot_is_restore_ = false;
   }

   bool
   BackupManager::RestoreHoldsRunSlot_()
   {
      boost::lock_guard<boost::mutex> guard(run_slot_mutex_);

      if (run_slot_owner_.expired())
         return false;

      return run_slot_is_restore_;
   }

   bool
   BackupManager::StartBackup()
   {
      Logger::Instance()->LogBackup("Backup started");
      LOG_DEBUG("BackupManager::StartBackup()");

      // The task is created before the slot is claimed because the slot is held as
      // a weak reference to it. Creating it costs nothing and cannot fail in a way
      // that would matter here.
      std::shared_ptr<BackupTask> pBackupTask = std::shared_ptr<BackupTask>(new BackupTask(true));

      // Start the backup thread, if we aren't
      // already running a backup or restore.
      //
      // Tested and claimed in one step, under the lock. The unsynchronised
      // read-then-write this replaces let two callers on different threads both
      // see "not running", and a schedule turns that from something that needed an
      // administrator to click twice into something that happens on its own.
      if (!ClaimRunSlot_(pBackupTask, RunBackup))
      {
         LOG_DEBUG("BackupManager::~StartBackup() - E1");
         OnBackupFailed("Backup or restore operation is already started");
         return false;
      }

      std::shared_ptr<WorkQueue> pWorkQueue = Application::Instance()->GetMaintenanceWorkQueue();
      if (!pWorkQueue)
      {
         ReleaseRunSlot_();

         LOG_DEBUG("BackupManager::~StartBackup() - E2");
         OnBackupFailed("Backup operation failed because random work queue did not exist.");
         return false;
      }

      pWorkQueue->AddTask(pBackupTask);

      LOG_DEBUG("BackupManager::~StartBackup() - E3");

      return true;
   }

   BackupManager::ScheduledBackupResult
   BackupManager::StartScheduledBackup()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Queues a backup on behalf of BackupScheduleTask, reporting the outcome to
   // the caller rather than deciding for it. See the header.
   //---------------------------------------------------------------------------()
   {
      LOG_DEBUG("BackupManager::StartScheduledBackup()");

      std::shared_ptr<BackupTask> pBackupTask = std::shared_ptr<BackupTask>(new BackupTask(true));

      if (!ClaimRunSlot_(pBackupTask, RunBackup))
      {
         LOG_DEBUG("BackupManager::~StartScheduledBackup() - E1");

         // Which of the two it is decides whether the caller writes the slot off
         // or comes back in a minute, so the distinction is reported rather than
         // flattened into "busy". Read after the failed claim, so at worst it is
         // one moment stale - and a slot that has just been released only means
         // the caller waits one more tick.
         if (RestoreHoldsRunSlot_())
            return ScheduledBackupRestoreRunning;

         return ScheduledBackupAlreadyRunning;
      }

      std::shared_ptr<WorkQueue> pWorkQueue = Application::Instance()->GetMaintenanceWorkQueue();
      if (!pWorkQueue)
      {
         ReleaseRunSlot_();

         LOG_DEBUG("BackupManager::~StartScheduledBackup() - E2");
         return ScheduledBackupNoWorkQueue;
      }

      // Written to the backup log, not only to the application log, so that the
      // reason this archive exists sits next to the archive's own progress lines.
      // The first question about an unexpected archive is whether a person or the
      // schedule asked for it, and this is the only line that answers it.
      Logger::Instance()->LogBackup("Scheduled backup started");

      // TaskMayBlock, unlike the administrator-initiated path above.
      //
      // A backup holds one of the maintenance queue's five threads for as long as
      // it takes to copy and compress the whole message store, which can be hours.
      // Classifying it keeps it inside the queue's blocking-task cap - with the
      // shipped AsyncQueueReservedThreads of 2 that cap is three of the five
      // threads - so an unattended backup cannot be the reason the last free
      // thread is missing when an administrator clears the delivery queue, starts
      // a restore, or a run-once maintenance task is due. It is worth being
      // precise about the size of this claim: mail flow does not run on the
      // maintenance queue at all, so this bounds administrative and maintenance
      // work, not message delivery. What keeps a scheduled backup off mail flow is
      // that it is queued here instead of being run on the scheduler's thread; see
      // BackupScheduleTask.
      //
      // Both caller constraints that come with the class hold: this task waits on
      // no other task on this queue, and nothing waits on its GetIsStartedEvent().
      //
      // The consequence to be aware of is that the run slot is claimed above while
      // the task may still be waiting for a blocking slot, so a manual backup
      // started in that window is refused with "already started". That is honest -
      // a backup is pending - and much better than letting the schedule starve the
      // queue. The administrator-initiated path is deliberately left unclassified:
      // reclassifying it would change when an explicit, supervised action starts on
      // installations that reserve threads, which has nothing to do with putting
      // backups on a schedule.
      pWorkQueue->AddTask(pBackupTask, WorkQueue::TaskMayBlock);

      LOG_DEBUG("BackupManager::~StartScheduledBackup() - E3");

      return ScheduledBackupQueued;
   }


   bool
   BackupManager::StartRestore(std::shared_ptr<Backup> pBackup)
   {
      Logger::Instance()->LogBackup("Restore started");
      LOG_DEBUG("BackupManager::StartRestore()");

      std::shared_ptr<BackupTask> pBackupTask = std::shared_ptr<BackupTask>(new BackupTask(false));
      pBackupTask->SetBackupToRestore(pBackup);

      // Start the backup thread, if we aren't
      // already running a backup or restore.
      if (!ClaimRunSlot_(pBackupTask, RunRestore))
      {
         OnBackupFailed("Backup or restore operation is already started");
         LOG_DEBUG("BackupManager::~StartRestore() - E1");
         return false;
      }

      // Checked, unlike before. The slot has been claimed by this point, so
      // dereferencing a queue that does not exist would both crash and - if it
      // somehow did not - leave the slot held, after which no backup or restore
      // could start again. The backup path always checked; this one did not.
      std::shared_ptr<WorkQueue> pWorkQueue = Application::Instance()->GetMaintenanceWorkQueue();
      if (!pWorkQueue)
      {
         ReleaseRunSlot_();

         LOG_DEBUG("BackupManager::~StartRestore() - E3");
         OnBackupFailed("Restore operation failed because the maintenance work queue did not exist.");
         return false;
      }

      pWorkQueue->AddTask(pBackupTask);

      LOG_DEBUG("BackupManager::~StartRestore() - E2");
      return true;
   }

   std::shared_ptr<Backup>
   BackupManager::LoadBackup(const String &sZipFile) const
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Opens a backup archive and reports what is in it, or nothing at all if it is
   // not an archive this server could restore.
   //
   // WHAT THIS USED TO DO INSTEAD, AND WHY IT MATTERED
   //
   // It ignored the result of the extraction, read the file the extraction was
   // supposed to have produced, and carried on. Three consequences, all of them on
   // the path an administrator takes in a disaster:
   //
   //  * A truncated or half-copied archive - the case the whole feature exists to
   //    survive - extracted nothing, read as empty, parsed to no Backup element and
   //    returned a Backup object with Contains=0 and, because SetBackupFile was
   //    called after that early return, no file name either. The COM caller was
   //    handed that object with S_OK. Nothing was written to any log.
   //
   //  * An archive whose index parsed but had no BackupInformation element, or no
   //    Mode attribute on it, dereferenced the null that GetChildAttr returns.
   //
   //  * The index was extracted to a fixed path in the temp directory, so two
   //    administrators opening two different archives at the same moment - or one
   //    doing it while a restore was extracting its own index to the same path -
   //    could read each other's. See BackupRestorer.
   //
   // Returning nothing instead of an empty object is what makes the first case
   // visible: InterfaceBackupManager::LoadBackup turns it into DISP_E_BADINDEX, so
   // the Control Panel says the restore could not be started rather than starting a
   // restore that will do nothing.
   //---------------------------------------------------------------------------()
   {
      LOG_DEBUG("BackupManager::LoadBackup");

      int archiveContents = 0;
      String archiveVersion;
      String failureReason;

      if (!BackupRestorer::ReadArchiveIndex(sZipFile, archiveContents, archiveVersion, failureReason))
      {
         // LOG_APPLICATION and the backup log, not ReportError. Pointing this at a
         // file that is not a backup is an ordinary mistake made by hand at a
         // prompt, and a server that writes to its ERROR log because somebody
         // mistyped a path is a server whose ERROR log stops meaning anything.
         Logger::Instance()->LogBackup("Could not open the backup " + sZipFile + ". " + failureReason);
         LOG_APPLICATION("Could not open the backup " + sZipFile + ". " + failureReason);

         LOG_DEBUG("BackupManager::~LoadBackup - E1");
         return std::shared_ptr<Backup>();
      }

      // Refused here rather than at the start of the restore, so that the
      // administrator finds out while they are still choosing what to restore.
      if (!BackupRestorer::VersionIsRestorable(archiveVersion, failureReason))
      {
         Logger::Instance()->LogBackup("Could not open the backup " + sZipFile + ". " + failureReason);
         LOG_APPLICATION("Could not open the backup " + sZipFile + ". " + failureReason);

         LOG_DEBUG("BackupManager::~LoadBackup - E3");
         return std::shared_ptr<Backup>();
      }

      std::shared_ptr<Backup> pResult = std::shared_ptr<Backup>(new Backup);

      pResult->SetBackupFile(sZipFile);
      pResult->SetContains(archiveContents);

      LOG_DEBUG("BackupManager::~LoadBackup - E2");
      return pResult;
   }

   void
   BackupManager::OnThreadStopped()
   {
      // Reached from BackupTask::DoWork through whichever manager exists *now*,
      // which after a reinitialization is not the one that started the run. That
      // is exactly why the slot is process-wide: releasing per-instance state here
      // would have released a flag nobody was reading.
      ReleaseRunSlot_();
   }

   void
   BackupManager::SetStatus(const String &sStatus)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      log_ += sStatus + "\r\n";
   }

   String
   BackupManager::GetStatus()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      String sVal = log_;
      return sVal;
   }

   void
   BackupManager::OnBackupCompleted()
   {
      LOG_DEBUG("BackupManager::OnBackupCompleted()");
      Logger::Instance()->LogBackup("Backup completed successfully");

      if (Configuration::Instance()->GetUseScriptServer())
      {
         std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
         std::shared_ptr<Result> pResult = std::shared_ptr<Result>(new Result);
         pContainer->AddObject("Result", pResult, ScriptObject::OTResult);
         String sEventCaller = "OnBackupCompleted()";
         ScriptServer::Instance()->FireEvent(ScriptServer::EventOnBackupCompleted, sEventCaller, pContainer);
      }
      LOG_DEBUG("BackupManager::~OnBackupCompleted()");
   }

   void
   BackupManager::OnBackupFailed(const String &sReason)
   {
      LOG_DEBUG("BackupManager::OnBackupFailed()");
      String sErrorMsg = "BACKUP ERROR: " + sReason;
      Logger::Instance()->LogBackup(sErrorMsg);

      ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5014, "BackupManager::OnBackupFailed", sErrorMsg);

      if (Configuration::Instance()->GetUseScriptServer())
      {
         std::shared_ptr<ScriptObjectContainer> pContainer = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);
         std::shared_ptr<Result> pResult = std::shared_ptr<Result>(new Result);
         pContainer->AddObject("Result", pResult, ScriptObject::OTResult);
         String sEventCaller = "OnBackupFailed(\"" + sReason + "\")";
         ScriptServer::Instance()->FireEvent(ScriptServer::EventOnBackupFailed, sEventCaller, pContainer);
      }
      LOG_DEBUG("BackupManager::~OnBackupFailed()");
   }


}
