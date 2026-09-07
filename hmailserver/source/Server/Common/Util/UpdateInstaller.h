// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // The third of the four parts of the roadmap's live update: the apply.
   //
   // The service cannot run its own installer, because the installer stops the
   // service. So Apply verifies the downloaded installer once more, fetches and
   // verifies the running version's installer as the rollback image where the feed
   // still offers it, issues the single-use token the database upgrade will present
   // (UpdateApplyToken), copies hMailServer.Updater.exe out of Bin - which the
   // installer is about to replace - into the Updates folder, and starts it there,
   // detached. The helper does the rest: runs the installer, waits for the service
   // to come back, runs the rollback image if it does not, and writes one line
   // saying what happened, which ReportLastOutcome reads and reports the next time
   // the service starts.
   //
   // A rollback reinstalls the previous binaries; it cannot take the database back.
   // When the new version had already moved the schema and then failed to start, the
   // previous version will refuse the newer schema, and the outcome says so: that is
   // the case for a backup before an update, which BackupBeforeUpdate arranges.
   class UpdateInstaller
   {
   public:
      // On the calling thread until the helper has been started; the update itself
      // happens after this returns. True when the helper was started.
      static bool Apply(String &error);

      // At startup: the outcome the helper wrote, if any, into the log and the
      // verdict, then out of the way.
      static void ReportLastOutcome();

      // Bin\hMailServer.Updater.exe, beside the running executable.
      static String HelperSourcePath();
      static String OutcomePath();

   private:
      static bool EnsureRollbackImage_(const String &runningVersion, String &rollbackPath, String &why);
      static bool Launch_(const String &commandLine, const String &workingDirectory, String &error);
   };
}
