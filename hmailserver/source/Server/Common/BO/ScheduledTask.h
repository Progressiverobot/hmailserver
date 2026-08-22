// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Util/VariantDateTime.h"
#include "../Threading/Task.h"

namespace HM
{
   class ScheduledTask : public Task
   {
   public:
      ScheduledTask();
      ~ScheduledTask(void);

      enum Reoccurance
      {
         RunOnce = 1,
         RunInfinitely =2
      };

      int GetMinutesBetweenRun() const;
      void SetMinutesBetweenRun(int iNewVal);

      Reoccurance GetReoccurance() const;
      void SetReoccurance(Reoccurance ro);

      DateTime GetNextRunTime() const;
      void SetNextRunTime();

   private:

      Reoccurance reoccurance_;
      DateTime next_run_time_;
      int minutes_between_run_;

   };
}