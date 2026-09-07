// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // The scheduled half of the update check: once at startup and then every
   // UpdateCheckHours, and a no-op until UpdateCheckEnabled=1, because a server
   // must not call out to anyone until its administrator has said it may. The check
   // itself, and the on-demand form the Control Panel and the REST API use, is
   // UpdateChecker.
   class UpdateCheckTask : public ScheduledTask
   {
   public:
      UpdateCheckTask();
      ~UpdateCheckTask();

      virtual void DoWork();
   };
}
