// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "MessageStoreConsistencyTask.h"

#include "IniFileSettings.h"
#include "Logger.h"
#include "../Util/ServerStatus.h"
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
   // (no-op) unless MessageStoreConsistencyCheck is set to 1.
   //---------------------------------------------------------------------------()
   {
      if (!IniFileSettings::Instance()->GetMessageStoreConsistencyCheck())
         return;

      int missingFiles = PersistentMessage::GetMissingFileCount();

      ServerStatus::Instance()->SetMessageStoreMissingFiles(missingFiles);

      if (missingFiles > 0)
      {
         String message;
         message.Format(_T("Message store consistency check: %d message(s) reference a file that is missing on disk."),
            missingFiles);
         LOG_APPLICATION(message);
      }
   }
}
