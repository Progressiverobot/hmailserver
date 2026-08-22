// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "DeliveryFailure.h"

namespace HM
{
   class Message;
   class MessageRecipient;
   class RuleResult;
   class ServerInfo;
   class HostNameAndIpAddress;

   class ExternalDelivery
   {
   public:
      ExternalDelivery(const String &sSendersIP, std::shared_ptr<Message> message, const RuleResult &globalRuleResult);
      ~ExternalDelivery(void);

      bool Perform(std::vector<DeliveryFailure> &saErrorMessages);

   private:



      void DeliverToSingleDomain_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, std::shared_ptr<ServerInfo> serverInfo);
      void DeliverToSingleServer_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, std::shared_ptr<ServerInfo> serverInfo);

      bool ResolveRecipientServers_(std::shared_ptr<ServerInfo> &serverInfo, std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, std::vector<HostNameAndIpAddress> &saMailServers);
      bool RecipientWithNonFatalDeliveryErrorExists_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients);
      // enhancedStatusCode has no default, for the reason DeliveryFailure's
      // constructor has no default: every one of these failures is a distinct
      // condition and the caller is the only place that knows which.
      void HandleExternalDeliveryFailure_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, bool bIsFatal, String &sErrorString, const String &enhancedStatusCode);
      void HandleNoRecipientServers_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, bool bDNSQueryOK, bool isSpecificRelayServer);

      // The pending failures are carried as DeliveryFailure rather than as the
      // address-to-prose map this used to be, so that the enhanced status code,
      // the remote server's reply and the host it came from survive the retry
      // window and are still there when the message is finally given up on.
      void CollectDeliveryResult_(const String &serverHostName, std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, std::vector<DeliveryFailure> &saErrorMessages, std::map<String,DeliveryFailure> &mapFailedDueToNonFatalError, std::set<String> &suppressFailureDsnAddresses);
      bool RescheduleDelivery_(std::map<String,DeliveryFailure> &mapFailedDueToNonFatalError, std::vector<DeliveryFailure> &saErrorMessages, std::set<String> &suppressFailureDsnAddresses);
      // Type changed from void to bool for use with ETRN.
      // Function not called anywhere else to matter
      bool GetRetryOptions_(std::map<String,DeliveryFailure> &mapFailedDueToNonFatalError, long &lNoOfRetries, long &lMinutesBetween);

      IPAddress GetLocalAddress_();

      const String &_sendersIP;
      const std::shared_ptr<Message> original_message_;
      const RuleResult &_globalRuleResult;   

      int quick_retries_;      
      int quick_retries_Minutes;      
      int queue_randomness_minutes_;
      int mxtries_factor_; 
   };
}