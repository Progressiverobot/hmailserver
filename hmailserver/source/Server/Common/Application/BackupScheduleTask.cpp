// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "BackupScheduleTask.h"

#include "Backup.h"
#include "BackupManager.h"
#include "BackupRetention.h"
#include "Logger.h"

#include "../Util/FileUtilities.h"
#include "../Util/ServerStatus.h"

#include <boost/filesystem.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // A refused run writes one application-log line per hour, not one per scheduler
   // tick. A destination that is unreachable stays unreachable for hours or days,
   // and a line a minute would push everything else out of the log exactly while
   // somebody is reading it to find out why.
   const unsigned __int64 BACKUP_SKIP_LOG_INTERVAL_MS = 3600000;

   // How often the destination is examined for free space and for sharing a volume
   // with the message store. Not on every due run: both checks touch the
   // destination from the scheduler's thread, and a daily figure is what the
   // warnings are worth - a disk fills up over weeks, not over one backup.
   const unsigned __int64 BACKUP_CAPACITY_CHECK_INTERVAL_MS = 86400000;

   // The escalating retry delay after a refused run, in minutes. Starts short so a
   // share that comes back at 02:03 still gets the 02:00 backup, and ends at an
   // hour so a destination that has been gone for a week is probed 24 times a day
   // rather than 1440 - each probe being a blocking call on the scheduler's thread.
   const unsigned int BACKUP_RETRY_MINUTES_FIRST = 5;
   const unsigned int BACKUP_RETRY_MINUTES_MAX = 60;

   // The delay used when something unrelated is holding the backup slot - a restore.
   // Deliberately not escalating: the wait is bounded by the other operation
   // finishing, and escalating would mean an hour of unnecessary delay after it did.
   const unsigned int BACKUP_RETRY_MINUTES_BUSY = 5;

   // How much headroom over the size of the previous archive counts as "enough
   // room". A backup normally grows, so matching the previous size exactly is not a
   // margin worth reporting on.
   const double BACKUP_FREE_SPACE_MARGIN = 1.2;

   BackupScheduleTask::BackupScheduleTask(void) :
      seeded_(false),
      have_history_(false),
      next_attempt_tick_(0),
      consecutive_failures_(0),
      last_skip_log_tick_(0),
      last_capacity_check_tick_(0),
      free_space_warning_reported_(false),
      same_volume_warning_reported_(false)
   {
      // One line per service start, and only for an installation that has asked for
      // scheduled backups - the task is not constructed otherwise. Said here rather
      // than in DoWork because DoWork runs every minute for the life of the process.
      String timeOfDay = IniFileSettings::Instance()->GetScheduledBackupTime();
      int intervalMinutes = IniFileSettings::Instance()->GetScheduledBackupIntervalMinutes();

      int hour = 0;
      int minute = 0;

      if (!timeOfDay.IsEmpty() && ParseTimeOfDay(timeOfDay, hour, minute))
      {
         String message;
         message.Format(_T("Scheduled backup: enabled, daily at %.02d:%.02d local time (ScheduledBackupTime in hMailServer.ini). What is backed up, and where to, comes from the backup settings."),
            hour, minute);

         LOG_APPLICATION(message);
      }
      else if (!timeOfDay.IsEmpty())
      {
         // Not an ErrorManager report. This can only be reached by an administrator
         // who has just edited the ini file by hand, and the message telling them
         // what they mistyped belongs where they will be looking for it.
         LOG_APPLICATION(_T("Scheduled backup: ScheduledBackupTime in hMailServer.ini could not be read as a 24-hour HH:MM local time, so no daily backup will run. Accepted values look like 02:00 or 23:45."));

         if (intervalMinutes > 0)
         {
            String message;
            message.Format(_T("Scheduled backup: falling back to ScheduledBackupIntervalMinutes, every %d minute(s)."), intervalMinutes);

            LOG_APPLICATION(message);
         }
      }
      else
      {
         String message;
         message.Format(_T("Scheduled backup: enabled, every %d minute(s) (ScheduledBackupIntervalMinutes in hMailServer.ini). What is backed up, and where to, comes from the backup settings."),
            intervalMinutes);

         LOG_APPLICATION(message);
      }

      int keepCount = IniFileSettings::Instance()->GetScheduledBackupKeepCount();
      int maxAgeDays = IniFileSettings::Instance()->GetScheduledBackupMaxAgeDays();

      if (keepCount > 0 || maxAgeDays > 0)
      {
         String message;
         message.Format(_T("Scheduled backup: retention is on (keep %d, maximum age %d day(s); 0 means no limit). An old archive is only ever deleted after a new backup has completed successfully."),
            keepCount, maxAgeDays);

         LOG_APPLICATION(message);

         // Without compression, every backup writes its message files into the same
         // "DataBackup" folder in the destination, so each backup overwrites the
         // previous one's messages and only the small .7z of settings and domains is
         // per-run. Retention can therefore count archives but cannot keep N
         // generations of mail - which is not what an administrator who has set a
         // keep count is expecting. Worth saying out loud, once, rather than letting
         // them discover it during a restore.
         if ((Configuration::Instance()->GetBackupOptions() & Backup::BOMessages) &&
             !(Configuration::Instance()->GetBackupOptions() & Backup::BOCompression))
         {
            LOG_APPLICATION(_T("Scheduled backup: messages are included but compression is off, so every backup writes its message files into the same DataBackup folder and the previous backup's messages are overwritten. Retention can only keep generations of the compressed archives - switch on compression of destination files if you want more than one generation of mail."));
         }
      }
   }

   BackupScheduleTask::~BackupScheduleTask(void)
   {
   }

   bool
   BackupScheduleTask::IsEnabled()
   {
      if (!IniFileSettings::Instance()->GetScheduledBackupTime().IsEmpty())
         return true;

      return IniFileSettings::Instance()->GetScheduledBackupIntervalMinutes() > 0;
   }

   bool
   BackupScheduleTask::ParseTimeOfDay(const String &value, int &hour, int &minute)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads a 24-hour local time of day, "HH:MM". Deliberately strict about both the
   // range and trailing rubbish: silently reading "26:00" as 02:00, or "2" as
   // 02:00, would move somebody's backup window without telling them, and a backup
   // window that has quietly moved into the working day is a performance incident
   // nobody would think to look here for.
   //---------------------------------------------------------------------------()
   {
      String trimmed = value;
      trimmed.Trim();

      int separator = trimmed.Find(_T(":"));
      if (separator < 1)
         return false;

      String hourPart = trimmed.Mid(0, separator);
      String minutePart = trimmed.Mid(separator + 1);

      if (hourPart.GetLength() > 2 || minutePart.GetLength() != 2)
         return false;

      if (!StringParser::IsNumeric(hourPart) || !StringParser::IsNumeric(minutePart))
         return false;

      int parsedHour = _ttoi(hourPart.c_str());
      int parsedMinute = _ttoi(minutePart.c_str());

      if (parsedHour < 0 || parsedHour > 23)
         return false;

      if (parsedMinute < 0 || parsedMinute > 59)
         return false;

      hour = parsedHour;
      minute = parsedMinute;

      return true;
   }

   String
   BackupScheduleTask::FormatDay_(const DateTime &value)
   {
      String result;
      result.Format(_T("%d-%.02d-%.02d"), value.GetYear(), value.GetMonth(), value.GetDay());

      return result;
   }

   void
   BackupScheduleTask::Seed_(const DateTime &now)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Establishes when the last backup happened, from the archives already in the
   // destination rather than from anything this process remembers.
   //
   // The state that matters is "how old is the newest backup", and the destination
   // is where that fact lives. Keeping it only in memory breaks two ordinary cases.
   // A server restarted more often than the backup interval - a machine that reboots
   // for updates every night, or a service an administrator is stopping and starting
   // while working on something - would restart its interval on every start and
   // never reach the end of one. And a daily backup whose slot was missed because
   // the server was down at the time would be skipped entirely rather than taken as
   // soon as the server came back.
   //
   // A manual backup taken over COM counts too, which is deliberate: the schedule
   // means "there should be a backup no older than this", and taking an automatic one
   // twenty minutes after the administrator took their own is pointless load on a
   // live server.
   //
   // This is the one enumeration of the destination that happens whether or not a
   // run is due, so it is also the one place a dead network share can delay the
   // scheduler thread without a backup being owed. It happens once per service
   // start, on the first tick rather than during startup, and the alternative -
   // trusting in-memory state - is the two bugs above.
   //---------------------------------------------------------------------------()
   {
      last_started_ = now;
      last_started_day_ = _T("");
      have_history_ = false;

      String destination = Configuration::Instance()->GetBackupDestination();
      if (destination.IsEmpty())
         return;

      std::vector<BackupArchive> archives = BackupRetention::ListArchives(destination);
      if (archives.empty())
         return;

      const BackupArchive &newest = archives.back();

      last_started_ = newest.created;
      last_started_day_ = FormatDay_(newest.created);
      have_history_ = true;

      String message;
      message.Format(_T("Scheduled backup: the newest backup in %s is %s."),
         destination.c_str(), newest.file_name.c_str());

      LOG_APPLICATION(message);
   }

   void
   BackupScheduleTask::ConsumeSlot_(const DateTime &now)
   {
      last_started_ = now;
      last_started_day_ = FormatDay_(now);
      have_history_ = true;
   }

   bool
   BackupScheduleTask::IsDueNow_(const DateTime &now)
   {
      String timeOfDay = IniFileSettings::Instance()->GetScheduledBackupTime();

      int hour = 0;
      int minute = 0;

      if (!timeOfDay.IsEmpty() && ParseTimeOfDay(timeOfDay, hour, minute))
      {
         // Daily: at most one run per calendar day, taken at the first tick at or
         // after the configured time.
         //
         // Comparing calendar days rather than counting twenty-four hours is what
         // makes the schedule stable across a daylight-saving change and across a
         // run that started late. On the spring-forward day a 02:30 window that the
         // clock jumps straight over is still satisfied at 03:00, because the test is
         // "at or after", not "equal to". On the autumn day 02:30 happens twice and
         // the day guard stops the second one producing a second backup.
         if (last_started_day_.Compare(FormatDay_(now).c_str()) == 0)
            return false;

         if (now.GetHour() < hour)
            return false;

         if (now.GetHour() == hour && now.GetMinute() < minute)
            return false;

         return true;
      }

      int intervalMinutes = IniFileSettings::Instance()->GetScheduledBackupIntervalMinutes();
      if (intervalMinutes <= 0)
         return false;

      // No backup exists anywhere yet: take one now. See have_history_.
      if (!have_history_)
         return true;

      DateTimeSpan elapsed = now - last_started_;

      return elapsed.GetNumberOfSeconds() >= (double) intervalMinutes * 60;
   }

   void
   BackupScheduleTask::RetryIn_(unsigned __int64 tick, unsigned int minutes)
   {
      next_attempt_tick_ = tick + (unsigned __int64) minutes * 60 * 1000;
   }

   void
   BackupScheduleTask::BackOff_(unsigned __int64 tick)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // 5, 10, 20, 40 and then 60 minutes. See the header for why a refused run is not
   // simply retried on the next tick.
   //---------------------------------------------------------------------------()
   {
      if (consecutive_failures_ < 30)
         consecutive_failures_++;

      unsigned int minutes = BACKUP_RETRY_MINUTES_FIRST;

      for (unsigned int doubled = 1; doubled < consecutive_failures_; doubled++)
      {
         if (minutes >= BACKUP_RETRY_MINUTES_MAX)
            break;

         minutes = minutes * 2;
      }

      if (minutes > BACKUP_RETRY_MINUTES_MAX)
         minutes = BACKUP_RETRY_MINUTES_MAX;

      RetryIn_(tick, minutes);
   }

   void
   BackupScheduleTask::ClearFailureState_()
   {
      // A run started, so the reporting is armed again: the next time something
      // stops a backup it is news, and says so immediately rather than an hour
      // later.
      consecutive_failures_ = 0;
      next_attempt_tick_ = 0;
      last_skip_log_tick_ = 0;
      last_reported_reason_ = _T("");
   }

   bool
   BackupScheduleTask::PreflightOk_(const String &destination, int backupOptions)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Refuses to start a run that is certain to fail, or certain to be useless.
   //
   // Every condition here is one BackupExecuter would also detect - but it would
   // detect it after claiming the single backup slot and after reporting a critical
   // error, once per scheduled run, for as long as the condition lasts. A scheduled
   // backup that cannot run should say so once and stay out of the way.
   //
   // The checks are ordered by what they cost, not by what is most likely. The first
   // three read cached settings and one mutex-protected count; only the last one
   // touches the destination, which on an unreachable share means blocking the
   // scheduler's thread until the operating system gives up. That check is therefore
   // reached only when a run is genuinely owed and the configuration is otherwise
   // sound, and its failure feeds the retry backoff rather than a per-minute retry.
   //---------------------------------------------------------------------------()
   {
      if (destination.IsEmpty())
      {
         ReportSkipped_(_T("no backup destination has been configured (see the backup settings)"), 5886);
         return false;
      }

      if ((backupOptions & (Backup::BOSettings | Backup::BODomains | Backup::BOMessages)) == 0)
      {
         ReportSkipped_(_T("the backup settings do not include settings, domains or messages, so the backup would contain nothing"), 5886);
         return false;
      }

      // The one failure mode that produces a plausible-looking but useless backup
      // rather than an obvious failure. GetIsConnected only proves the pool holds
      // connections, so this catches "the database never came up" and not "the
      // database stopped answering a moment ago"; the second case is caught inside
      // BackupExecuter, which aborts before writing anything if any of its reads did
      // not run. Both halves are needed - this one keeps the common case out of the
      // backup slot and out of the critical error log.
      std::shared_ptr<DatabaseConnectionManager> databaseManager = Application::Instance()->GetDBManager();

      if (!databaseManager || !databaseManager->GetIsConnected())
      {
         ReportSkipped_(_T("the database is not available, and a backup taken now would not contain the domains or accounts"), 5889);
         return false;
      }

      if (!FileUtilities::DirectoryExists(destination))
      {
         ReportSkipped_(_T("the backup destination is not accessible: ") + destination, 5886);
         return false;
      }

      return true;
   }

   void
   BackupScheduleTask::CheckDestinationCapacity_(const String &destination, unsigned __int64 tick)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Warns about the two things that turn a working backup schedule into a broken
   // one without any single run failing in an obvious way.
   //
   // Free space below what the previous archive needed: the next backup will fail
   // part-written, and so will the one after it. Said before the run rather than
   // after, because "the backup failed, the disk is full" is a much worse thing to
   // find than "the destination has 900 MB free and the last backup needed 4 GB".
   //
   // A destination on the same volume as the message store: a backup that fills the
   // volume then also stops the server accepting mail, because the spool and the
   // database live there too. Not hypothetical - a destination pointed at somewhere
   // under the install directory is the normal way to end up here.
   //
   // Deliberately never refuses the run. Both conditions are estimates, and a
   // schedule that stopped taking backups on the strength of an estimate would be
   // worse than one that takes a backup which might fail: the write failure is
   // authoritative, is reported, and leaves the previous archives intact, whereas
   // refusing means no backup at all.
   //
   // Called only after a successful pre-flight, so the destination is known to be
   // reachable at this point, and at most once a day, because these are slow calls on
   // the scheduler's thread and the answers change over weeks.
   //---------------------------------------------------------------------------()
   {
      if (last_capacity_check_tick_ != 0 && tick < last_capacity_check_tick_ + BACKUP_CAPACITY_CHECK_INTERVAL_MS)
         return;

      last_capacity_check_tick_ = tick;

      if (!free_space_warning_reported_)
      {
         ULARGE_INTEGER freeBytesAvailable;
         freeBytesAvailable.QuadPart = 0;

         if (GetDiskFreeSpaceEx(destination.c_str(), &freeBytesAvailable, nullptr, nullptr))
         {
            std::vector<BackupArchive> archives = BackupRetention::ListArchives(destination);

            if (!archives.empty())
            {
               // boost::filesystem::file_size, not FileUtilities::FileSize: the
               // latter returns long, and a backup archive is routinely larger than
               // 2 GB - which is exactly the case this check exists for.
               boost::system::error_code errorCode;
               boost::uintmax_t previousSize =
                  boost::filesystem::file_size(std::wstring(FileUtilities::Combine(destination, archives.back().file_name)), errorCode);

               if (!errorCode && previousSize > 0 &&
                   (double) freeBytesAvailable.QuadPart < (double) previousSize * BACKUP_FREE_SPACE_MARGIN)
               {
                  String message;
                  message.Format(_T("Scheduled backup: %s has %I64u MB free and the previous backup archive is %I64u MB, so the next backup is likely to run out of space. The backup will be attempted anyway - a write failure fails the backup and leaves the existing archives alone."),
                     destination.c_str(),
                     (unsigned __int64) (freeBytesAvailable.QuadPart / (1024 * 1024)),
                     (unsigned __int64) (previousSize / (1024 * 1024)));

                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5888, "BackupScheduleTask::CheckDestinationCapacity_", message);

                  // Once per service start for this one. It is a prediction, and
                  // repeating a prediction daily adds nothing an administrator who
                  // has read it once needs.
                  free_space_warning_reported_ = true;
               }
            }
         }
      }

      if (same_volume_warning_reported_)
         return;

      // GetVolumePathName resolves drive letters, mounted volumes and UNC roots, so
      // this is not a comparison of the first character of two strings.
      String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();
      if (dataDirectory.IsEmpty())
         return;

      TCHAR destinationVolume[MAX_PATH + 1];
      TCHAR dataVolume[MAX_PATH + 1];

      if (!GetVolumePathName(destination.c_str(), destinationVolume, MAX_PATH))
         return;

      if (!GetVolumePathName(dataDirectory.c_str(), dataVolume, MAX_PATH))
         return;

      if (String(destinationVolume).CompareNoCase(dataVolume) != 0)
         return;

      // The application log, not the error log. This is a configuration opinion, not
      // a fault, and it is reachable on an installation that is otherwise working
      // perfectly - putting it in the error log would fail every fixture's
      // clean-error-log assertion for a server that is backing up successfully.
      String message;
      message.Format(_T("Scheduled backup: the backup destination %s is on the same volume (%s) as the message store %s. A backup that fills the volume will also stop mail being accepted. Point the destination at a different volume."),
         destination.c_str(), destinationVolume, dataDirectory.c_str());

      LOG_APPLICATION(message);

      same_volume_warning_reported_ = true;
   }

   void
   BackupScheduleTask::ReportSkipped_(const String &reason, int errorCode)
   {
      String message;
      message.Format(_T("Scheduled backup skipped: %s."), reason.c_str());

      unsigned __int64 tick = GetTickCount64();

      // The first refusal after a run that started always logs; after that, one line
      // an hour however many ticks are refused in between.
      bool logNow = last_skip_log_tick_ == 0 ||
                    tick >= last_skip_log_tick_ + BACKUP_SKIP_LOG_INTERVAL_MS;

      if (logNow)
      {
         LOG_APPLICATION(message);

         // Also to the backup log, which is where somebody investigating "why is
         // there no backup from last night" will be looking, and which does not roll
         // over daily.
         Logger::Instance()->LogBackup(message);

         last_skip_log_tick_ = tick;
      }

      // One error-log entry per distinct reason. Keyed on the reason text rather
      // than on the error code deliberately: several reasons share a code, so keying
      // on the code would mean the first reason to occur silently suppressed every
      // later, different one - a destination that has gone away hiding the fact that
      // the database is down as well.
      if (errorCode != 0 && last_reported_reason_.CompareNoCase(reason.c_str()) != 0)
      {
         // ErrorManager::High rather than Critical: nothing is broken in the server,
         // and the schedule will pick the backup up as soon as the condition clears.
         // Unreachable on the shipped default configuration, because the task is
         // only created once a schedule has been configured.
         ErrorManager::Instance()->ReportError(ErrorManager::High, errorCode, "BackupScheduleTask::DoWork", message);

         last_reported_reason_ = reason;
      }
   }

   void
   BackupScheduleTask::DoWork()
   {
      // Belt and braces: the settings are only re-read by InitInstance, which also
      // rebuilds the scheduler and its tasks, so a task that exists always had a
      // schedule when it was created. Checking anyway costs two reads of cached
      // values and means this can never be the thing that starts a backup nobody
      // asked for.
      if (!IsEnabled())
         return;

      // A backup started while the server is starting up or shutting down would race
      // the things it depends on: the connection pool is created and destroyed around
      // this window, and the maintenance work queue the run is handed to is torn down
      // during shutdown.
      if (ServerStatus::Instance()->GetState() != ServerStatus::StateRunning)
         return;

      DateTime now = DateTime::GetCurrentTime();
      unsigned __int64 tick = GetTickCount64();

      if (!seeded_)
      {
         Seed_(now);
         seeded_ = true;
      }

      // Backing off after a refused run. Checked before IsDueNow_ so that the daily
      // mode does not consume its slot while waiting, and before the pre-flight so
      // that the blocking destination check is not made.
      if (tick < next_attempt_tick_)
         return;

      if (!IsDueNow_(now))
         return;

      String destination = Configuration::Instance()->GetBackupDestination();
      int backupOptions = Configuration::Instance()->GetBackupOptions();

      if (!PreflightOk_(destination, backupOptions))
      {
         // The slot is deliberately not consumed. Nothing was backed up and nothing
         // is running, and most of what the pre-flight rejects is temporary - a share
         // that is not answering, a database that is restarting - so a destination
         // that comes back at 02:05 should still get the 02:00 backup.
         BackOff_(tick);
         return;
      }

      CheckDestinationCapacity_(destination, tick);

      std::shared_ptr<BackupManager> backupManager = Application::Instance()->GetBackupManager();

      if (!backupManager)
      {
         ReportSkipped_(_T("the backup manager is not available"), 5887);
         BackOff_(tick);
         return;
      }

      BackupManager::ScheduledBackupResult result = backupManager->StartScheduledBackup();

      switch (result)
      {
      case BackupManager::ScheduledBackupQueued:
         {
            ConsumeSlot_(now);
            ClearFailureState_();

            String message;
            message.Format(_T("Scheduled backup: queued a backup to %s."), destination.c_str());

            LOG_APPLICATION(message);
         }
         break;

      case BackupManager::ScheduledBackupAlreadyRunning:
         // The slot is consumed: the backup the schedule wanted is in progress right
         // now, so this run is satisfied by the one already going. See the header for
         // why this is not retried.
         ConsumeSlot_(now);

         ReportSkipped_(_T("the previous backup is still running, so this run is covered by the one already in progress"), 0);
         break;

      case BackupManager::ScheduledBackupRestoreRunning:
         // Not consumed, and only a short retry delay. A restore is unrelated work
         // that happens to hold the same slot, and it is the moment at which the next
         // backup matters most - the data has just been replaced wholesale. Writing
         // the slot off here would mean no backup for a whole day after a restore.
         ReportSkipped_(_T("a restore is running, so the backup will be taken once it has finished"), 0);
         RetryIn_(tick, BACKUP_RETRY_MINUTES_BUSY);
         break;

      case BackupManager::ScheduledBackupNoWorkQueue:
         ReportSkipped_(_T("the maintenance work queue is not available"), 5887);
         BackOff_(tick);
         break;
      }
   }
}
