// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   class Account;
   class Message;

   // What the webmail put off until later: a draft to be sent at a time
   // (send later) and a message to come back to its folder, unread, at a
   // time (snooze). Rows of hm_scheduled; this task runs every minute and
   // acts on the ones whose time has come.
   class ScheduledMailTask : public ScheduledTask
   {
   public:
      ScheduledMailTask();
      ~ScheduledMailTask();

      virtual void DoWork();

      // Everything due now, acted on and removed; how many rows. Public so an
      // administrator's route can run it on demand, which is what the
      // regression suite does rather than waiting a minute.
      static int RunDue();

      // The two actions, public so a cancelled snooze can be undone at once.
      static bool SendDraft(std::shared_ptr<const Account> account, std::shared_ptr<Message> draft);
      static bool ReturnSnoozed(std::shared_ptr<const Account> account, std::shared_ptr<Message> message, __int64 folderId);

      static const int ActionSend = 1;
      static const int ActionReturn = 2;
   };
}
