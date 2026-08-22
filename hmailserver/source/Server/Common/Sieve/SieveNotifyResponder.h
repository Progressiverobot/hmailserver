// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <vector>

namespace HM
{
   class Account;
   struct SieveNotifyDecision;

   // Sends the notifications a Sieve "notify" action asked for (RFC 5435), by
   // the mailto method (RFC 5436) - the only method this server implements,
   // because the only transport a mail server inherently has is mail.
   //
   // A deliberate lean sibling of SieveVacationResponder: same composition and
   // submission path (a Delivering message written to disk, recipients parsed,
   // saved, queue kicked), same loop discipline - the notification goes out with
   // a NULL envelope return path and Auto-Submitted: auto-notified (RFC 5436
   // 2.7.1), so a bounce of a notification cannot start an exchange and the next
   // auto-responder in the chain knows not to answer. Unlike vacation there is
   // deliberately NO suppression store: RFC 5435 has no per-sender window, and
   // the loop guards live at the source instead - the evaluator refuses to
   // notify about a message that is itself auto-submitted.
   class SieveNotifyResponder : public Singleton<SieveNotifyResponder>
   {
   public:
      // Composes and submits one notification. false means it could not be sent
      // and the reason was reported; delivery of the message that triggered it is
      // never affected either way.
      bool Send(std::shared_ptr<const Account> account, const SieveNotifyDecision &decision);
   };

   // One notification the evaluator decided on: the mailto target, the subject
   // (":message", or a summary built from the triggering message), the sender to
   // present, and RFC 5435 3.2's importance level (1 high, 2 normal, 3 low).
   struct SieveNotifyDecision
   {
      String to;
      String subject;
      String from;
      int importance = 2;
      int loopCount = 0;

      // Context from the triggering message, for the notification body.
      String originalFrom;
      String originalSubject;
   };
}
