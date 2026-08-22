// Copyright (c) 2006 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Account;
   class Message;
   class MessageData;

   class SMTPForwarding
   {
   public:

      bool PerformForwarding(std::shared_ptr<const Account> pRecipientAccount, std::shared_ptr<Message> pOriginalMessage);

      // Queues a copy of the message for delivery to an arbitrary address (used by
      // the Sieve "redirect" action). Applies the same loop-count guard and
      // SRS/envelope-from handling as account forwarding. Returns true if a copy
      // was queued.
      bool RedirectToAddress(std::shared_ptr<const Account> pRecipientAccount, std::shared_ptr<Message> pOriginalMessage, const String &targetAddress);

   };
}