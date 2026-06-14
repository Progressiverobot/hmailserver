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
      // message. Sets sieveFolder to a fileinto target (empty when none) and
      // sieveDiscard true when the message should be silently dropped.
      void EvaluateSieveScript_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message, String &sieveFolder, bool &sieveDiscard);

      std::shared_ptr<Message>  CreateAccountLevelMessage_(std::shared_ptr<Message> pOriginalMessage, std::shared_ptr<const Account> pRecipientAccount, bool reuseMessage, const String &sOriginalAddress);

      const String &_sendersIP;
      const std::shared_ptr<Message> original_message_;
      const RuleResult &_globalRuleResult;

   };
}