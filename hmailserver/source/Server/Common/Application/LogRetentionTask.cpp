// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "LogRetentionTask.h"

#include "IniFileSettings.h"
#include "Logger.h"
#include "../Util/FileUtilities.h"

#include <boost/filesystem.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   LogRetentionTask::LogRetentionTask(void)
   {
   }

   LogRetentionTask::~LogRetentionTask(void)
   {
   }

   void
   LogRetentionTask::DoWork()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Delete date-stamped hMailServer log files older than the configured
   // retention window (LogDeleteDays). Disabled when LogDeleteDays <= 0.
   //---------------------------------------------------------------------------()
   {
      int retentionDays = IniFileSettings::Instance()->GetLogDeleteDays();
      if (retentionDays <= 0)
         return;

      String logDirectory = IniFileSettings::Instance()->GetLogDirectory();
      if (logDirectory.IsEmpty())
         return;

      boost::system::error_code error_code;
      boost::filesystem::path directory(logDirectory.begin(), logDirectory.end());
      if (!boost::filesystem::is_directory(directory, error_code))
         return;

      std::time_t cutoff = std::time(0) - (static_cast<std::time_t>(retentionDays) * 24 * 60 * 60);
      int deletedCount = 0;

      for (boost::filesystem::directory_iterator file(directory, error_code);
           file != boost::filesystem::directory_iterator(); file.increment(error_code))
      {
         boost::filesystem::path current = file->path();

         if (!boost::filesystem::is_regular_file(current, error_code))
            continue;

         String fileName = current.filename().wstring().c_str();
         String lowerName = fileName;
         lowerName.ToLower();

         // Only ever touch hMailServer's own date-stamped log files.
         bool isHmailLog =
            (lowerName.StartsWith(_T("hmailserver_")) || lowerName.StartsWith(_T("error_hmailserver_"))) &&
            lowerName.EndsWith(_T(".log"));

         if (!isHmailLog)
            continue;

         std::time_t lastWrite = boost::filesystem::last_write_time(current, error_code);
         if (error_code)
            continue;

         if (lastWrite >= cutoff)
            continue;

         String fullPath = current.wstring().c_str();
         if (FileUtilities::DeleteFile(fullPath))
            deletedCount++;
      }

      if (deletedCount > 0)
      {
         String message;
         message.Format(_T("Log retention: deleted %d log file(s) older than %d day(s)."),
            deletedCount, retentionDays);
         LOG_APPLICATION(message);
      }
   }
}
