// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "MessageStoreConsistencyTask.h"

#include "IniFileSettings.h"
#include "Logger.h"
#include "../Util/ServerStatus.h"
#include "../Util/FileUtilities.h"
#include "../Util/Time.h"
#include "../Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   MessageStoreConsistencyTask::MessageStoreConsistencyTask(void)
   {
   }

   MessageStoreConsistencyTask::~MessageStoreConsistencyTask(void)
   {
   }

   void
   MessageStoreConsistencyTask::DoWork()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Cross-check the message database against the message store on disk and
   // publish the number of messages whose backing file is missing. Disabled
   // (no-op) unless MessageStoreConsistencyCheck is set to 1. When enabled, a
   // recovery report listing the affected messages is written to the log
   // directory so an administrator can act on it.
   //---------------------------------------------------------------------------()
   {
      if (!IniFileSettings::Instance()->GetMessageStoreConsistencyCheck())
         return;

      std::vector<String> missingFiles = PersistentMessage::GetMissingFileDetails();
      int missingCount = static_cast<int>(missingFiles.size());

      ServerStatus::Instance()->SetMessageStoreMissingFiles(missingCount);

      WriteReport_(missingFiles);

      if (missingCount > 0)
      {
         String message;
         message.Format(_T("Message store consistency check: %d message(s) reference a file that is missing on disk. See the recovery report in the log directory."),
            missingCount);
         LOG_APPLICATION(message);
      }
   }

   void
   MessageStoreConsistencyTask::WriteReport_(const std::vector<String> &missingFiles)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Write a recovery report describing the latest consistency-check result to
   // the log directory. The report is overwritten on each run so it always
   // reflects the most recent scan.
   //---------------------------------------------------------------------------()
   {
      String logDirectory = IniFileSettings::Instance()->GetLogDirectory();
      if (logDirectory.IsEmpty())
         return;

      String reportPath = FileUtilities::Combine(logDirectory, _T("hMailServer_messagestore_consistency.report"));

      String report;
      report.Format(_T("# hMailServer message-store consistency report\r\n")
                    _T("# Generated: %s\r\n")
                    _T("# Missing files: %d\r\n")
                    _T("# Columns: messageid<TAB>account<TAB>expected-path\r\n"),
                    Time::GetCurrentDateTime().c_str(),
                    static_cast<int>(missingFiles.size()));

      for (const String &line : missingFiles)
      {
         report += line;
         report += _T("\r\n");
      }

      FileUtilities::WriteToFile(reportPath, report, true);
   }
}
