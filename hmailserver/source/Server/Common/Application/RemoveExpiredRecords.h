// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   class RemoveExpiredRecords : public ScheduledTask
   {
   public:
      RemoveExpiredRecords(void);
      ~RemoveExpiredRecords(void);

      virtual void DoWork();

   private:
   };
}