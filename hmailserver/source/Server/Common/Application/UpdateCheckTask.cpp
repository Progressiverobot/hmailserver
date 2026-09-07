// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateCheckTask.h"
#include "IniFileSettings.h"
#include "Application.h"
#include "BackupManager.h"
#include "Configuration.h"
#include "../Util/UpdateChecker.h"
#include "../Util/UpdateDownloader.h"
#include "../Util/UpdateInstaller.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // A backup of a large message store takes a while; an hour is the limit
      // before the window is given up for this run.
      const int BACKUP_WAIT_SECONDS = 3600;

      // Backs up before an unattended apply, when UpdateBackupBeforeApply says to.
      // False - with the reason logged - means the update is not applied this time:
      // a rollback restores binaries, not the schema, and the backup is what would.
      bool BackupBeforeApply_(const String &version)
      {
         if (Configuration::Instance()->GetBackupDestination().IsEmpty())
         {
            LOG_APPLICATION(Formatter::Format(_T("Update: UpdateBackupBeforeApply is on and no backup destination is configured, so hMailServer {0} is not applied unattended. Apply it by hand, configure a backup destination, or set UpdateBackupBeforeApply=0."), version));
            return false;
         }

         std::shared_ptr<BackupManager> manager = Application::Instance()->GetBackupManager();
         if (!manager)
            return false;

         LOG_APPLICATION(Formatter::Format(_T("Update: backing up before applying hMailServer {0}."), version));
         if (!manager->StartBackup())
         {
            LOG_APPLICATION(Formatter::Format(_T("Update: the backup could not be started (another backup or restore is running), so hMailServer {0} is not applied this time."), version));
            return false;
         }

         for (int waited = 0; waited < BACKUP_WAIT_SECONDS && manager->IsRunning(); waited += 5)
            Sleep(5000);

         if (manager->IsRunning())
         {
            LOG_APPLICATION(Formatter::Format(_T("Update: the backup was still running after an hour, so hMailServer {0} is not applied this time."), version));
            return false;
         }
         if (!manager->LastBackupSucceeded())
         {
            LOG_APPLICATION(Formatter::Format(_T("Update: the backup failed (see the backup log), so hMailServer {0} is not applied."), version));
            return false;
         }

         LOG_APPLICATION(Formatter::Format(_T("Update: the backup completed; applying hMailServer {0}."), version));
         return true;
      }
   }

   UpdateCheckTask::UpdateCheckTask()
   {
   }

   UpdateCheckTask::~UpdateCheckTask()
   {
   }

   void
   UpdateCheckTask::DoWork()
   {
      // Whether or not the check is on: an update the helper applied while the
      // service was down is reported the first time the service is up again.
      UpdateInstaller::ReportLastOutcome();

      IniFileSettings *settings = IniFileSettings::Instance();
      if (!settings->GetUpdateCheckEnabled())
         return;

      // The task runs every quarter hour so that a window is not missed; the feed
      // is read every UpdateCheckHours. A feed that cannot be read is logged by
      // the check and remembered in its snapshot for the Control Panel to show;
      // it is not an ERROR, because a release server being unreachable says
      // nothing about this server.
      if (UpdateChecker::CheckIsDue())
      {
         String error;
         UpdateChecker::CheckNow(error);
      }

      UpdateChecker::Snapshot snapshot = UpdateChecker::Current();

      if (snapshot.state == UpdateChecker::StateAvailable && settings->GetUpdateAutoDownload() && !snapshot.installer_url.IsEmpty())
      {
         String error;
         if (UpdateDownloader::DownloadAndVerify(error))
            snapshot = UpdateChecker::Current();
      }

      if (snapshot.state != UpdateChecker::StateDownloaded)
         return;

      String windowText;
      bool open = false;
      String windowError;
      UpdateChecker::WindowStatus(windowText, open, windowError);
      if (!windowError.IsEmpty())
      {
         // Once per process: a setting that does not parse is worth one line, not
         // one every quarter hour.
         static bool reported = false;
         if (!reported)
         {
            reported = true;
            LOG_APPLICATION(Formatter::Format(_T("Update: hMailServer {0} is verified and waiting, but UpdateWindow is not usable: {1}"), snapshot.available_version, windowError));
         }
         return;
      }
      if (!open)
         return;

      if (settings->GetUpdateBackupBeforeApply() && !BackupBeforeApply_(snapshot.available_version))
         return;

      LOG_APPLICATION(Formatter::Format(_T("Update: the window ({0}) is open and hMailServer {1} is verified; applying it unattended."), windowText, snapshot.available_version));
      String error;
      if (!UpdateInstaller::Apply(error))
         LOG_APPLICATION(Formatter::Format(_T("Update: hMailServer {0} could not be applied unattended: {1}"), snapshot.available_version, error));
   }
}
