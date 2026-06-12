// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../Common/BO/ScheduledTask.h"

namespace HM
{
   class GreyListCleanerTask : public ScheduledTask
   {
   public:
      GreyListCleanerTask(void);
      ~GreyListCleanerTask(void);

      virtual void DoWork();
   private:
   };
}