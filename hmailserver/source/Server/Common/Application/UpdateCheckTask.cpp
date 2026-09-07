// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateCheckTask.h"
#include "IniFileSettings.h"
#include "../Util/UpdateChecker.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   UpdateCheckTask::UpdateCheckTask()
   {
   }

   UpdateCheckTask::~UpdateCheckTask()
   {
   }

   void
   UpdateCheckTask::DoWork()
   {
      if (!IniFileSettings::Instance()->GetUpdateCheckEnabled())
         return;

      // A feed that cannot be read is logged by the check and remembered in its
      // snapshot for the Control Panel to show; it is not an ERROR, because a
      // release server being unreachable says nothing about this server.
      String error;
      UpdateChecker::CheckNow(error);
   }
}
