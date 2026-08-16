// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../Common/Sieve/SieveEvaluator.h"

namespace HM
{
   class Message;
   class RuleResult;

   class LocalDelivery
   {
   public:
      LocalDelivery(const String &sSendersIP, std::shared_ptr<Message> message, const RuleResult &globalRuleResult);
      ~LocalDelivery(void);

      bool Perform(std::vector<String> &saErrorMessages);

   private:
      
      void DeliverToLocalAccount_(std::shared_ptr<const Account> account, size_t iNoOfRecipients, std::vector<String> &saErrorMessages, const String &sOriginalAddress, bool &messageReused, bool suppressFailureDsn);
      // suppressFailureDsn is the recipient's RFC 3461 NOTIFY opt-out, threaded
      // through because a Sieve reject sends its non-delivery report from here.
      bool LocalDeliveryPreProcess_(std::shared_ptr<const Account> account, std::shared_ptr<Message> accountLevelMessage, const String &sOriginalAddress, std::vector<String> &saErrorMessages, bool suppressFailureDsn);
      bool AddTraceHeaders_(std::shared_ptr<const Account> account, std::shared_ptr<Message> pMessage, const String &sOriginalAddress);
      void SendAutoReplyMessage_(std::shared_ptr<const Account> pAccount, std::shared_ptr<Message> pMessage);
      bool RunAccountRules_(std::shared_ptr<const Account> pAccount, std::shared_ptr<Message> pMessage, RuleResult &accountRuleResult);
      bool CheckAccountQuotas_(std::shared_ptr<const Account> pAccount, std::vector<String> &saErrorMessages, bool suppressFailureDsn);

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
      void EvaluateSieveScript_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message,
                                const String &sOriginalAddress, String &sieveFolder, bool &sieveDrop,
                                bool &sieveFlagsGiven, std::vector<String> &sieveFlags,
                                String &sieveRejectReason);

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