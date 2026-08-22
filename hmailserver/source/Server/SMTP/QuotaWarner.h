// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Account;

   // Tells an account holder that their mailbox is filling up, before it fills.
   //
   // Until now nothing did, so the first anyone heard of a full mailbox was mail
   // that stopped arriving - and since the refusal happens during the sending
   // server's SMTP conversation, the person it happened to is the last to find out.
   //
   // THE INTERESTING PART IS "ONCE". A warning sent on every delivery while a
   // mailbox sits above the threshold would be worse than none: dozens of identical
   // notices, each one consuming a little of the space they are complaining about,
   // in a mailbox already short of room. The obvious fix is to remember who has been
   // warned, which needs a column, a lifetime and a rule for when to forget.
   //
   // None of that is necessary, because "crossed the threshold" is an event and not
   // a state. The size before this message is known and so is the message, so the
   // crossing can be recognised exactly when it happens: below before, at or above
   // after. That fires once by construction, needs nothing remembered, and does the
   // right thing when somebody empties their mailbox and fills it again - which a
   // remembered flag would have to be told to forget.
   class QuotaWarner
   {
   public:

      // Called on the delivery path once the message is known to fit. Does nothing
      // unless this message is the one that takes the mailbox across
      // QuotaWarningPercent, and nothing at all when that setting is 0 or the
      // account has no quota.
      static void NotifyIfThresholdCrossed(std::shared_ptr<const Account> account, __int64 messageSize);

   private:

      static String GetSenderAddress_(const String &recipientAddress);
   };
}
