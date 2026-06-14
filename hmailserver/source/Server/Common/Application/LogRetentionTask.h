// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // Scheduled maintenance task that deletes date-stamped log files older than
   // the configured retention window (hMailServer.ini [Settings] LogDeleteDays).
   // A value of 0 (the default) disables retention so logs are kept indefinitely,
   // preserving historical behaviour.
   class LogRetentionTask : public ScheduledTask
   {
   public:
      LogRetentionTask(void);
      ~LogRetentionTask(void);

      virtual void DoWork();
   };
}
