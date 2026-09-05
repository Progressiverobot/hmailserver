// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // Removes delivered messages older than a mailbox's retention policy.
   //
   // The policy is a number of days, set on the domain (Domain.MessageRetentionDays,
   // 0 = none) and optionally overridden on the account (Account.MessageRetentionDays,
   // 0 = the domain's, -1 = keep forever). Every folder of the mailbox is subject to
   // it, the archive and the quarantine are not - they have their own sweeps - and a
   // message counts as old by the time it was written to the mailbox, not by its Date
   // header, which the sender controls.
   //
   // Off unless somebody sets a number, and every number is a decision: the sweep
   // deletes mail the user has not deleted. What it does is what a user would see if
   // they did it themselves - the message goes through the same deletion as an
   // EXPUNGE, so quotas, the QRESYNC expunge record and an IMAP session that has the
   // folder selected all hear about it.
   //
   // The dates are compared in C++ as "YYYY-MM-DD HH:MM:SS" strings, the way the
   // quarantine sweep does it and for the same reason: four SQL dialects' date
   // arithmetic have no business in code that deletes mail.
   class MailboxRetentionTask : public ScheduledTask
   {
   public:
      MailboxRetentionTask(void);
      ~MailboxRetentionTask(void);

      virtual void DoWork();

      // The sweep itself: every domain, every account with an effective policy.
      // Returns the number of messages removed. Public and static so the COM
      // Utilities object can run it on demand.
      static int Sweep();
   };
}
