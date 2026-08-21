// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../Common/Sieve/SieveEvaluator.h"
#include "DeliveryFailure.h"

namespace HM
{
   class Message;
   class RuleResult;

   class LocalDelivery
   {
   public:
      LocalDelivery(const String &sSendersIP, std::shared_ptr<Message> message, const RuleResult &globalRuleResult);
      ~LocalDelivery(void);

      bool Perform(std::vector<DeliveryFailure> &saErrorMessages);

   private:
      
      void DeliverToLocalAccount_(std::shared_ptr<const Account> account, size_t iNoOfRecipients, std::vector<DeliveryFailure> &saErrorMessages, const String &sOriginalAddress, bool &messageReused, bool suppressFailureDsn);
      // suppressFailureDsn is the recipient's RFC 3461 NOTIFY opt-out, threaded
      // through because a Sieve reject sends its non-delivery report from here.
      bool LocalDeliveryPreProcess_(std::shared_ptr<const Account> account, std::shared_ptr<Message> accountLevelMessage, const String &sOriginalAddress, std::vector<DeliveryFailure> &saErrorMessages, bool suppressFailureDsn);
      bool AddTraceHeaders_(std::shared_ptr<const Account> account, std::shared_ptr<Message> pMessage, const String &sOriginalAddress);

      // Applies the account's own spam settings to its copy of the message, before
      // anything else looks at that copy. Everything here re-judges a verdict the
      // conversation already reached - it never re-runs a test - because the spam
      // flag and the recorded X-hMailServer-Reason-Score header are all that
      // survive from the conversation to delivery. A message the global filter did
      // not classify carries neither, so for it this method does nothing, which is
      // exactly the pre-feature behaviour.
      //
      // Returns false when this account's copy must not be delivered: its
      // per-account delete threshold was provably reached (the recorded score when
      // present, otherwise the global mark threshold as a lower bound - never a
      // guess). The copy is first placed in the quarantine store when that is
      // enabled; a quarantine failure delivers the marked copy instead, because
      // discarding mail that was not actually stored would be silent loss.
      //
      // May instead unmark the copy in place (opt-out, or a mark-threshold
      // override the recorded score falls short of): flag cleared, spam headers
      // and subject tag rewritten out of this account's file.
      bool ApplyAccountSpamOverrides_(std::shared_ptr<const Account> account, std::shared_ptr<Message> accountLevelMessage);

      // The total score the conversation recorded on the message - the
      // X-hMailServer-Reason-Score header, written when "add reason headers" is on
      // and the message was classified. Returns -1 when absent or unparseable;
      // never guesses.
      static int ReadRecordedSpamScore_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message);

      // Removes the classification from this account's copy: the spam flag on the
      // message object, the X-hMailServer-Spam / X-hMailServer-Reason-* headers,
      // and the prepended subject tag, so the copy reads as if it was never
      // classified. Only this copy - the file is already the account's own.
      static void RemoveSpamClassification_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message);

      // The account's own vacation message. Returns true when that feature is ON
      // for this account - whether or not a reply was actually produced (the
      // sender may be suppressed, the message spam-flagged, the sender the account
      // itself). The return value is what keeps the precedence rule honest: an
      // account that has spoken for itself, even by staying silent, is never
      // spoken over by the domain-wide reply, so exactly one auto-reply can ever
      // answer one delivered message.
      //
      // messageIsSpamForThisAccount is the flag on the ACCOUNT'S copy, after the
      // per-account overrides ran - an account that opted out of spam filtering
      // has its auto-reply suppression judged against its own view of the message,
      // not against the shared original's flag.
      bool SendAutoReplyMessage_(std::shared_ptr<const Account> pAccount, std::shared_ptr<Message> pMessage, bool messageIsSpamForThisAccount);

      // The domain-wide out-of-office reply, for an account with no active
      // vacation message of its own. Chooses the domain's internal or external
      // text by what the envelope sender resolves to (see
      // SMTPVacationMessageCreator::IsInternalSender) and sends through the same
      // creator the account-level reply uses, so the RFC 3834 loop guards and the
      // once-per-sender memory exist exactly once. Called after the Sieve script
      // has been evaluated, and only when neither the account's stored vacation
      // message nor a Sieve vacation action was active - the account's own voice,
      // in either form, wins over the domain's.
      void SendDomainAutoReplyMessage_(std::shared_ptr<const Account> pAccount, std::shared_ptr<Message> pMessage, bool messageIsSpamForThisAccount);
      bool RunAccountRules_(std::shared_ptr<const Account> pAccount, std::shared_ptr<Message> pMessage, RuleResult &accountRuleResult);
      bool CheckAccountQuotas_(std::shared_ptr<const Account> pAccount, std::vector<DeliveryFailure> &saErrorMessages, bool suppressFailureDsn);

      // Evaluates the recipient account's active Sieve script (if any) against the
      // message, firing any redirect actions and any RFC 5230 vacation auto-reply.
      // Sets sieveFolder to a fileinto target (empty when none) and sieveDrop true
      // when the local copy should not be delivered (a discard, or a redirect that
      // cancels the implicit keep).
      //
      // sOriginalAddress is the address this delivery was addressed to, which is not
      // always the account's own address (an alias or a plus-address reaches it too).
      // It is the SMTP envelope recipient the evaluator needs for "envelope" tests
      // and for the RFC 5230 4.5 check that refuses to auto-reply to a message the
      // user was not actually a recipient of.
      // sieveFlagsGiven is true when the script set the local copy's IMAP flags -
      // through :flags on the keep/fileinto, or through setflag/addflag/removeflag -
      // and sieveFlags is then the final set it decided on, canonicalised by
      // SieveParser::SplitFlagList. Empty-and-given is meaningful and different from
      // not-given: "removeflag" that cleared everything is an instruction, and a
      // script that never mentioned flags is not.
      // sieveRejectReason is non-empty when the script refused the message with
      // reject/ereject (RFC 5429): the caller then drops the local copy AND sends
      // a non-delivery report carrying the reason, through the same machinery as
      // a quota bounce.
      // sieveVacationRequested is true when the script asked for a vacation reply
      // to this message - whether or not the responder's own suppression window
      // then let it out. It exists for the domain-wide out-of-office reply's
      // precedence decision, which is about who SPEAKS for the account, not about
      // whether this particular reply escaped the rate limit.
      void EvaluateSieveScript_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message,
                                const String &sOriginalAddress, String &sieveFolder, bool &sieveDrop,
                                bool &sieveFlagsGiven, std::vector<String> &sieveFlags,
                                String &sieveRejectReason, bool &sieveVacationRequested);

      // Writes the flags a script decided onto the message that is about to be saved.
      //
      // Separate from the evaluation for the same reason sieveFolder and sieveDrop are
      // out-parameters rather than actions: the evaluator decides and the delivery
      // acts, so there is one place to look for what a script actually did to a
      // message.
      static void ApplySieveFlags_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message,
                                   const std::vector<String> &sieveFlags);

      // Rewrites the stored message file with the addheader/deleteheader edits a
      // script decided (RFC 5293), in script order, and refreshes the message's
      // recorded size. Runs BEFORE redirects are queued, so a redirected copy
      // carries the edits - RFC 5293 2: an edit affects all subsequent actions.
      // A failed write is reported through RuleGuard::ReportActionFailed, the
      // same visibility the rules engine's header rewriting gets.
      static void ApplySieveHeaderEdits_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message,
                                         const std::vector<SieveHeaderEdit> &edits);

      // Exact, case-sensitive membership. The flag names reaching here have already
      // been canonicalised by SieveParser::SplitFlagList, so a case-insensitive test
      // would compare values that cannot differ in case and would quietly go on
      // working if that canonicalisation were ever removed.
      static bool ListContains_(const std::vector<String> &values, const String &value);

      std::shared_ptr<Message>  CreateAccountLevelMessage_(std::shared_ptr<Message> pOriginalMessage, std::shared_ptr<const Account> pRecipientAccount, bool reuseMessage, const String &sOriginalAddress);

      const String &_sendersIP;
      const std::shared_ptr<Message> original_message_;
      const RuleResult &_globalRuleResult;

   };
}