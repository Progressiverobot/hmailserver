// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

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
      bool LocalDeliveryPreProcess_(std::shared_ptr<const Account> account, std::shared_ptr<Message> accountLevelMessage, const String &sOriginalAddress, std::vector<String> &saErrorMessages);
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
      void EvaluateSieveScript_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message, const String &sOriginalAddress, String &sieveFolder, bool &sieveDrop);

      std::shared_ptr<Message>  CreateAccountLevelMessage_(std::shared_ptr<Message> pOriginalMessage, std::shared_ptr<const Account> pRecipientAccount, bool reuseMessage, const String &sOriginalAddress);

      const String &_sendersIP;
      const std::shared_ptr<Message> original_message_;
      const RuleResult &_globalRuleResult;

   };
}