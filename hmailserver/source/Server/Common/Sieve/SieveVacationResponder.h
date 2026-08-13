// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>
#include <utility>

#include "SieveEvaluator.h"

namespace HM
{
   class Account;
   class MessageData;

   // Turns a SieveVacationDecision into an actual RFC 5230 auto-reply: applies the
   // per-sender suppression window, composes the response, and queues it.
   //
   // This is separate from SMTPVacationMessageCreator (the account-level "I am on
   // holiday" feature configured in hMailServer Administrator) rather than layered
   // on top of it, and that is worth explaining because the duplication is real.
   // The two differ in every part that matters here:
   //
   //   * that one tracks who it has replied to in a std::multimap that is empty
   //     again after a restart, and that has no expiry at all - the entry is removed
   //     only when the user switches the feature off. A ":days 7" window cannot be
   //     expressed with it, and a nightly service restart turns it into "reply to
   //     everyone, every day".
   //   * it keys on the sender alone, so RFC 5230's ":handle" - which is what lets
   //     one script hold two different vacation messages - has nowhere to go.
   //   * it takes the subject and body from the account's stored properties and
   //     expands hMailServer's own "%SUBJECT%" macro. A Sieve reason string is not
   //     macro-expanded (RFC 5230 has no macros, and a reason containing a literal
   //     percent sign must survive), and ":mime" means the reason may be a complete
   //     MIME entity rather than a body.
   //   * it sends with the account's own address as the envelope sender, where
   //     RFC 3834 4 requires a null return path for an automatic response.
   //   * it sends to whatever the caller passes, with the caller doing the
   //     loop-prevention. Here the loop prevention is the feature, so it lives with
   //     the sender.
   //
   // Changing SMTPVacationMessageCreator to cover both would mean changing the
   // behaviour of the existing account-level auto-reply, which is a separate,
   // documented, long-standing feature with its own COM properties and its own
   // Administrator user interface. That is not a change to make as a side effect of
   // adding Sieve vacation support.
   class SieveVacationResponder : public Singleton<SieveVacationResponder>
   {
   public:
      SieveVacationResponder();

      // Sends the auto-reply the decision describes, unless the suppression window
      // for this (sender, handle) is still running or a loop guard says no. Safe to
      // call with a decision whose "send" is false; it does nothing.
      //
      // Everything this needs about the message being replied to is in the decision,
      // so nothing here reads the message file. That matters: by the time an
      // auto-reply is queued the account-level message may already have been moved
      // into an IMAP folder, and a responder that went looking for it would work
      // only for the INBOX case.
      //
      // Never throws, and never reports a failure to the caller. The message the
      // script was filtering has already been accepted; failing its delivery because
      // an auto-reply could not be built would turn a cosmetic problem into lost
      // mail.
      void Respond(std::shared_ptr<const Account> account, const SieveVacationDecision &decision);

   private:
      // The window in seconds, from ":seconds" (RFC 6131) or ":days" (RFC 5230).
      static __int64 SuppressionWindowSeconds_(const SieveVacationDecision &decision);

      // ":from" if the address actually reaches the account, the account's own
      // address otherwise.
      static String ResolveFromAddress_(std::shared_ptr<const Account> account, const String &requested);

      static String BuildSubject_(const SieveVacationDecision &decision);

      // Decodes a raw header value that may be RFC 2047 encoded, so that the
      // original subject can be quoted in the reply's own subject without being
      // double-encoded.
      static String DecodeHeaderValue_(const String &rawValue);

      // Splits a ":mime" reason into its MIME headers and its body.
      static void SplitMimeReason_(const String &reason,
                                   std::vector<std::pair<String, String>> &headers,
                                   String &body);

      // Applies a ":mime" reason to the reply being built.
      static void ApplyMimeReason_(MessageData &data, const String &reason);

      static bool ComposeAndSubmit_(std::shared_ptr<const Account> account,
                                    const SieveVacationDecision &decision,
                                    const String &fromAddress);
   };
}
