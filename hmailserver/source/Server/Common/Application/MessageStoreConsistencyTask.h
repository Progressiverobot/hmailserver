// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // Scheduled maintenance task that cross-checks the message database against
   // the message store on disk and records the number of messages whose backing
   // file is missing (hMailServer.ini [Settings] MessageStoreConsistencyCheck).
   // A value of 0 (the default) disables the check so the potentially expensive
   // store walk is never performed, preserving historical behaviour. The result
   // is published as the hmailserver_messagestore_missing_files metric and a
   // warning is logged whenever divergence is detected. The check is read-only;
   // it never deletes or repairs anything.
   class MessageStoreConsistencyTask : public ScheduledTask
   {
   public:
      MessageStoreConsistencyTask(void);
      ~MessageStoreConsistencyTask(void);

      virtual void DoWork();

   private:

      void WriteReport_(const std::vector<String> &missingFiles);
   };
}
