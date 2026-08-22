// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "ArchiveRetentionTask.h"

#include "IniFileSettings.h"

#include <boost/filesystem.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // <archive>\<domain>\<user>\ is two levels; Inbound is one. Three is already
      // deeper than anything this server creates, and the cap is here so that a
      // symlink or junction somebody has put in the archive cannot turn a sweep into
      // a walk of the whole disk.
      const int max_archive_depth = 3;

      int
      PruneDirectory_(const boost::filesystem::path &directory, std::time_t cutoff, int depth)
      {
         if (depth > max_archive_depth)
            return 0;

         boost::system::error_code error_code;
         int deleted = 0;

         for (boost::filesystem::directory_iterator entry(directory, error_code);
              entry != boost::filesystem::directory_iterator(); entry.increment(error_code))
         {
            if (error_code)
               break;

            const boost::filesystem::path current = entry->path();

            // Followed only when it is a real directory. is_directory follows symlinks,
            // so a junction planted in the archive would otherwise be descended into -
            // and this code deletes things.
            if (boost::filesystem::is_symlink(current, error_code))
               continue;

            if (boost::filesystem::is_directory(current, error_code))
            {
               deleted += PruneDirectory_(current, cutoff, depth + 1);

               // A per-user folder whose last message has just been removed is left
               // behind otherwise, and after a few years of staff turnover the archive
               // is mostly empty directories. Only ever removed when genuinely empty,
               // and never the archive root itself.
               if (depth >= 0 && boost::filesystem::is_empty(current, error_code) && !error_code)
                  boost::filesystem::remove(current, error_code);

               continue;
            }

            if (!boost::filesystem::is_regular_file(current, error_code))
               continue;

            // Only ever the message files this server writes. The archive is somebody's
            // directory, and it is not this task's business what else is in it.
            String fileName = current.filename().wstring().c_str();
            fileName.ToLower();

            if (!fileName.EndsWith(_T(".eml")))
               continue;

            const std::time_t lastWrite = boost::filesystem::last_write_time(current, error_code);

            if (error_code)
            {
               error_code.clear();
               continue;
            }

            if (lastWrite >= cutoff)
               continue;

            boost::filesystem::remove(current, error_code);

            if (error_code)
               error_code.clear();
            else
               deleted++;
         }

         return deleted;
      }
   }

   ArchiveRetentionTask::ArchiveRetentionTask(void)
   {
   }

   ArchiveRetentionTask::~ArchiveRetentionTask(void)
   {
   }

   void
   ArchiveRetentionTask::DoWork()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Delete archived messages older than ArchiveRetentionDays. Disabled at 0.
   //---------------------------------------------------------------------------()
   {
      const int retentionDays = IniFileSettings::Instance()->GetArchiveRetentionDays();

      if (retentionDays <= 0)
         return;

      const String archiveDirectory = IniFileSettings::Instance()->GetArchiveDir();

      if (archiveDirectory.IsEmpty())
         return;

      boost::system::error_code error_code;
      boost::filesystem::path directory(archiveDirectory.begin(), archiveDirectory.end());

      if (!boost::filesystem::is_directory(directory, error_code))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Low, 6170, "ArchiveRetentionTask::DoWork",
            "ArchiveRetentionDays is set but ArchiveDir does not name a directory, so nothing has been pruned. "
            "ArchiveDir: " + archiveDirectory);

         return;
      }

      const std::time_t cutoff = std::time(0) - (static_cast<std::time_t>(retentionDays) * 24 * 60 * 60);

      const int deleted = PruneDirectory_(directory, cutoff, 0);

      if (deleted > 0)
      {
         LOG_APPLICATION(Formatter::Format("Archive retention: removed {0} archived message(s) older than {1} day(s) from {2}.",
                                           deleted, retentionDays, archiveDirectory));
      }
   }
}
