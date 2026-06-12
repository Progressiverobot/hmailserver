// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "./ExternalDelivery.h"

#include "RuleResult.h"

#include "../Common/BO/Message.h"
#include "../common/BO/MessageRecipient.h"
#include "../common/BO/Routes.h"

#include "../common/Scripting/Events.h"

#include "../common/Persistence/PersistentMessageRecipient.h"
#include "../common/Persistence/PersistentMessage.h"

#include "../common/TCPIP/DNSResolver.h"
#include "../common/TCPIP/IOService.h"
#include "../common/TCPIP/HostNameAndIpAddress.h"

#include "../Common/Util/AWstats.h"
#include "../common/Util/ServerInfo.h"
#include "../Common/Util/TlsRptStore.h"

#include "ServerTargetResolver.h"
#include "SMTPConfiguration.h"
#include "SMTPClientConnection.h"
#include "TlsPolicy.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{ 
   ExternalDelivery::ExternalDelivery(const String &sSendersIP, std::shared_ptr<Message> message, const RuleResult &globalRuleResult) :
      _sendersIP(sSendersIP),
      original_message_(message),
      _globalRuleResult(globalRuleResult),
      quick_retries_(0),
      quick_retries_Minutes(0),
      queue_randomness_minutes_(0),
      mxtries_factor_(0)
   {

   }

   ExternalDelivery::~ExternalDelivery(void)
   {

   }

   /// Performs deliver to any external recipients. 
   /// Returns true if the message has been rescheduled for later delivery.
   bool
   ExternalDelivery::Perform(std::vector<String> &saErrorMessages)
   {
      std::map<String,String> mapFailedDueToNonFatalError;

      ServerTargetResolver serverTargetResolver(original_message_, _globalRuleResult);
      std::map<std::shared_ptr<ServerInfo>, std::vector<std::shared_ptr<MessageRecipient> > > mapRecipients = serverTargetResolver.Resolve();
      auto iterDomain = mapRecipients.begin();
      auto iterEnd = mapRecipients.end();

      unsigned int iMaxRecipientsInBatch = Configuration::Instance()->GetSMTPConfiguration()->GetMaxSMTPRecipientsInBatch();
      if (iMaxRecipientsInBatch == 0)
         iMaxRecipientsInBatch = UINT_MAX;

      for (; iterDomain != iterEnd; iterDomain++)
      {
         std::shared_ptr<ServerInfo> serverInfo = (*iterDomain).first;
         std::vector<std::shared_ptr<MessageRecipient> > vecRecipientsOnDomain = (*iterDomain).second;

         // Split up all the recipients for this server into batches of 100 or so.
         std::vector<std::shared_ptr<MessageRecipient> > batch;
         auto iterRecipient = vecRecipientsOnDomain.begin();
         while (iterRecipient != vecRecipientsOnDomain.end())
         {
            batch.push_back(*iterRecipient);

            if (batch.size() >= iMaxRecipientsInBatch ||
               iterRecipient + 1 == vecRecipientsOnDomain.end())
            {
               // Deliver the message to the remote server.
               DeliverToSingleDomain_(batch, serverInfo);

               // Check what status we got on the external deliveries.
               CollectDeliveryResult_(serverInfo->GetHostName(), batch, saErrorMessages, mapFailedDueToNonFatalError);    

               batch.clear();
            }

            iterRecipient++;
         }

      }

      if (mapFailedDueToNonFatalError.size() > 0)
      {   
         bool messageRescheduled = RescheduleDelivery_(mapFailedDueToNonFatalError, saErrorMessages);
         return messageRescheduled;
      }
      else
         return false;
   }

   void
   ExternalDelivery::DeliverToSingleDomain_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, std::shared_ptr<ServerInfo> serverInfo)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Deliveres the message to external accounts (recipients not on this server).
   //---------------------------------------------------------------------------()
   {
      String sFirstRecipientAddress = vecRecipients[0]->GetAddress();
      if (sFirstRecipientAddress.IsEmpty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 4226, "SMTPDeliverer::_DeliverToExternalAccounts", "Could not deliver message; Recipient address missing.");
         return;
      }

      std::vector<HostNameAndIpAddress> mail_servers;

      // The recipient domain (used for TLS policy lookups). For MX-based
      // deliveries the server info host name holds the domain name at this
      // point; it is replaced by individual MX host names further down.
      String recipientDomain = serverInfo->GetHostName();

      // Run DNS query to find the recipient servers IP addresses.
      if (!ResolveRecipientServers_(serverInfo, vecRecipients, mail_servers))
         return;

      // Apply the recipient domain's MTA-STS policy (RFC 8461).
      bool stsEnforced = false;
      if (!serverInfo->GetFixed() && IniFileSettings::Instance()->GetMtaStsEnabled())
      {
         TlsPolicy::StsPolicy stsPolicy = TlsPolicy::GetStsPolicy(recipientDomain);

         if (stsPolicy.mode == TlsPolicy::StsEnforce)
         {
            std::vector<HostNameAndIpAddress> matchingServers;
            for (const HostNameAndIpAddress &mailServer : mail_servers)
            {
               HostNameAndIpAddress serverCopy = mailServer;
               if (TlsPolicy::HostMatchesStsPolicy(serverCopy.GetHostName(), stsPolicy))
                  matchingServers.push_back(mailServer);
               else
               {
                  LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Skipping MX host " + serverCopy.GetHostName() + " - not permitted by the MTA-STS policy of " + recipientDomain + ".");
                  TlsRptStore::Instance()->RecordFailure(recipientDomain, "sts", "mode: enforce", "validation-failure", serverCopy.GetHostName());
               }
            }

            if (matchingServers.empty())
            {
               LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Delivery to " + recipientDomain + " deferred. No MX host matches the domain's MTA-STS policy.");

               String errorMessage = _T("   Error Type: SMTP\r\n   Error Description: Delivery blocked by the recipient domain's MTA-STS policy. None of the MX hosts match the published policy.\r\n\r\n");
               HandleExternalDeliveryFailure_(vecRecipients, false, errorMessage);
               return;
            }

            mail_servers = matchingServers;
            stsEnforced = true;

            LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": MTA-STS policy mode 'enforce' active for " + recipientDomain + ". TLS with certificate validation is required.");
         }
         else if (stsPolicy.mode == TlsPolicy::StsTesting)
         {
            LOG_DEBUG("MTA-STS: Domain " + recipientDomain + " publishes a policy in 'testing' mode. Not enforcing.");
         }
      }

      bool daneEnabled = !serverInfo->GetFixed() && IniFileSettings::Instance()->GetDaneEnabled();

      mxtries_factor_ = IniFileSettings::Instance()->GetMXTriesFactor();

      // Try to connect to one server at a time. If a fatal error
      // occurs, (an exception with eFatalError), we should stop trying
      // and just return an error message.

      unsigned int attemptedHosts = 0;

      for (unsigned int i = 0; i < mail_servers.size(); i++)
      {
         HostNameAndIpAddress hostAndIp = mail_servers[i];

         // Create a list of the remaining recipients. These are the recipients we have
         // not yet delivered to on a previous server (where i > 0). 
         std::vector<std::shared_ptr<MessageRecipient> > remainingRecipients;
         for(std::shared_ptr<MessageRecipient> recipient : vecRecipients)
         {
            if (recipient->GetDeliveryResult() == MessageRecipient::ResultUndefined ||
                recipient->GetDeliveryResult() == MessageRecipient::ResultNonFatalError)
            {
               remainingRecipients.push_back(recipient);
            }
         }

         serverInfo->SetHostName(hostAndIp.GetHostName());
         serverInfo->SetIpAddress(hostAndIp.GetIpAddress());

         // Apply per-host TLS requirements: MTA-STS enforcement applies to
         // all hosts; DANE pins are looked up per MX host (RFC 7672).
         serverInfo->SetRequirePeerVerification(stsEnforced);

         std::vector<TlsaRecord> daneRecords;
         bool daneValidated = false;

         if (daneEnabled && !hostAndIp.GetHostName().IsEmpty())
         {
            TlsPolicy::TlsaLookupStatus tlsaStatus = TlsPolicy::TlsaLookupStatus::NoRecords;
            daneRecords = TlsPolicy::GetTlsaRecords(hostAndIp.GetHostName(), serverInfo->GetPort(), tlsaStatus);

            if (tlsaStatus == TlsPolicy::TlsaLookupStatus::Bogus)
            {
               // RFC 7672 section 2.1.3: a bogus DNSSEC chain means the
               // host must not be used for delivery.
               LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": TLSA records for " + hostAndIp.GetHostName() + " failed DNSSEC validation. Skipping this host (RFC 7672).");
               TlsRptStore::Instance()->RecordFailure(recipientDomain, "tlsa", "dane-ee tlsa records present", "dnssec-invalid", hostAndIp.GetHostName());
               continue;
            }

            daneValidated = tlsaStatus == TlsPolicy::TlsaLookupStatus::DnssecValidated;

            if (!daneRecords.empty())
            {
               if (daneValidated)
               {
                  LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": DNSSEC-validated DANE-EE TLSA records found for " + hostAndIp.GetHostName() + ". TLS with TLSA certificate matching is required.");
               }
               else
               {
                  LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": DANE-EE TLSA records found for " + hostAndIp.GetHostName() + " (unvalidated). TLS with TLSA certificate matching is required.");
               }
            }
         }

         attemptedHosts++;

         serverInfo->SetDaneRecords(daneRecords);
         serverInfo->SetRequireTls(stsEnforced || !daneRecords.empty());

         // Classification for TLS reporting (RFC 8460).
         AnsiString rptPolicyType = !daneRecords.empty() ? "tlsa" : (stsEnforced ? "sts" : "no-policy-found");
         AnsiString rptPolicyString = !daneRecords.empty() ? "dane-ee tlsa records present" : (stsEnforced ? "mode: enforce" : "");

         DeliverToSingleServer_(remainingRecipients, serverInfo);

         bool retryWithoutStartTls = false;

         for(std::shared_ptr<MessageRecipient> recipient : remainingRecipients)
         {
            if (recipient->GetDeliveryResult() == MessageRecipient::ResultOptionalHandshakeFailed)
            {
               recipient->SetDeliveryResult(MessageRecipient::ResultUndefined);
               retryWithoutStartTls = true;
            }
         }

         if (!serverInfo->GetFixed())
         {
            if (retryWithoutStartTls)
               TlsRptStore::Instance()->RecordFailure(recipientDomain, rptPolicyType, rptPolicyString, "validation-failure", hostAndIp.GetHostName());
            else if (serverInfo->GetEffectiveConnectionSecurity() != CSNone)
               TlsRptStore::Instance()->RecordSuccess(recipientDomain, rptPolicyType, rptPolicyString);
         }

         if (retryWithoutStartTls && !serverInfo->GetRequireTls())
         {
            serverInfo->DisableConnectionSecurity();

            DeliverToSingleServer_(remainingRecipients, serverInfo);
         }

         bool try_next_server = RecipientWithNonFatalDeliveryErrorExists_(vecRecipients);

         if (!try_next_server)
         {
            // All deliveries are complete or fatal. 
            return;
         }

         // Let's limit # of servers tried per retry to mxtries_factor_ * current number of retries to free up queue
         int iMXServerLimit = (original_message_->GetNoOfRetries()+1) * mxtries_factor_;
         if (mxtries_factor_ > 0 && i + 1 >= (unsigned int) iMXServerLimit )
         {
            LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Limiting to MXTriesFactored value of " + StringParser::IntToString(iMXServerLimit) + ".");      
            break;
         }
      }

      if (attemptedHosts == 0 && !mail_servers.empty())
      {
         // Every MX host was skipped because its TLSA records failed
         // DNSSEC validation. Defer delivery rather than deliver insecurely.
         LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Delivery to " + recipientDomain + " deferred. TLSA records of all MX hosts failed DNSSEC validation.");

         String errorMessage = _T("   Error Type: SMTP\r\n   Error Description: Delivery blocked: the DANE TLSA records of all MX hosts failed DNSSEC validation.\r\n\r\n");
         HandleExternalDeliveryFailure_(vecRecipients, false, errorMessage);
      }
   }

   /// Resolves IP addresses for the recipient servers. This will either be a MX 
   /// lookup, or a A lookup, if SMTP relaying is used.
   bool 
   ExternalDelivery::ResolveRecipientServers_(std::shared_ptr<ServerInfo> &serverInfo, std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, std::vector<HostNameAndIpAddress> &saMailServers)
   {
      DNSResolver resolver;

      // Resolve the specified hosts.
      bool dnsQueryOK = false;

      bool is_fixed = serverInfo->GetFixed();

      if (serverInfo->GetFixed())
      {
         String target_host_name = serverInfo->GetHostName();
         String target_ip_address = serverInfo->GetIpAddress();

         std::vector<String> mailServerHosts;

         bool useHostName = !target_host_name.IsEmpty();

         String relay_host_log_name;

         if (useHostName)
         {
            relay_host_log_name = target_host_name;

            if (target_host_name.Find(_T("|")) > 0)
               mailServerHosts = StringParser::SplitString(target_host_name, "|");
            else
               mailServerHosts.push_back(target_host_name);

            for(String host : mailServerHosts)
            {
               std::vector<String> ip_addresses;
               dnsQueryOK = resolver.GetIpAddresses(host, ip_addresses, true);

               for(String ip_address:  ip_addresses)
               {
                  HostNameAndIpAddress hostNameAndIpAddress;
                  hostNameAndIpAddress.SetHostName(host);
                  hostNameAndIpAddress.SetIpAddress(ip_address);

                  saMailServers.push_back(hostNameAndIpAddress);
               }
            }
         }
         else
         {
            String target_ip_address = serverInfo->GetIpAddress();
            relay_host_log_name = target_ip_address;

            HostNameAndIpAddress hostNameAndIpAddress;
            hostNameAndIpAddress.SetHostName("");
            hostNameAndIpAddress.SetIpAddress(target_ip_address);

            saMailServers.push_back(hostNameAndIpAddress);
         }

         LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Relaying to host " + relay_host_log_name + ".");      


      }
      else
      {
         // Resolve the mail server. The TCPConnection::Connect will normally do name
         // resolution, but since this is a matter of MX resolution and comparing
         // MX record preference, we have to do it manually.
         dnsQueryOK = resolver.GetEmailServers(serverInfo->GetHostName(), saMailServers);
      }

      std::shared_ptr<SMTPConfiguration> pSMTPConfig = Configuration::Instance()->GetSMTPConfiguration();
      const unsigned int maxNumberOfMXHosts = pSMTPConfig->GetMaxNumberOfMXHosts();

      if (maxNumberOfMXHosts > 0 && saMailServers.size() > maxNumberOfMXHosts)
      {
         LOG_DEBUG("Maximum number of MX host reached. Truncating MX server list.");
         saMailServers.resize(maxNumberOfMXHosts);
      }

      // Check if any servers exists.
      if (saMailServers.size() == 0)
      {
         HandleNoRecipientServers_(vecRecipients, dnsQueryOK, is_fixed);
         return false;
      }

      return true;
   }

   bool
   ExternalDelivery::RecipientWithNonFatalDeliveryErrorExists_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients)
   {
      // If there exists an recipient with nonfatal error,
      // we should try to deliver to other servers.
      auto iterRecipient = vecRecipients.begin();
      bool bTryNextServer = false;
      while (iterRecipient != vecRecipients.end())
      {
         std::shared_ptr<MessageRecipient> pRecipient (*iterRecipient);

         if (pRecipient->GetDeliveryResult() == MessageRecipient::ResultUndefined ||
            pRecipient->GetDeliveryResult() == MessageRecipient::ResultNonFatalError)
         {
            return true;
            break;
         }

         iterRecipient++;
      }

      return false;
   }

   void 
   ExternalDelivery::HandleExternalDeliveryFailure_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients,    
                                                      bool bIsFatal,    
                                                      String &sErrorString)
   {


      auto iterRecipient = vecRecipients.begin();
      while (iterRecipient != vecRecipients.end())
      {
         std::shared_ptr<MessageRecipient> pRecipient = (*iterRecipient);

         // Unless this recipient has already fatally failed, or succeeded,
         // update the state of it.

         bool bDeliveryComplete = pRecipient->GetDeliveryResult() == MessageRecipient::ResultOK ||
            pRecipient->GetDeliveryResult() == MessageRecipient::ResultFatalError;
         if (!bDeliveryComplete)
         {
            if (bIsFatal)
               pRecipient->SetDeliveryResult(MessageRecipient::ResultFatalError);
            else
               pRecipient->SetDeliveryResult(MessageRecipient::ResultNonFatalError);

            pRecipient->SetErrorMessage(sErrorString);
         }

         iterRecipient++;
      } 
   }

   void
   ExternalDelivery::HandleNoRecipientServers_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, bool bDNSQueryOK, bool isSpecificRelayServer)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Takes care of the situation when no valid recipient server addresses exist.
   //---------------------------------------------------------------------------()
   {
      LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": No mail servers could be found for the address " + (*vecRecipients.begin())->GetAddress() + ".");

      String bounceMessageText;

      // Generate a string which will be included in the bounce message.

      if (bDNSQueryOK)
      {
         if (isSpecificRelayServer)
            bounceMessageText = _T("   Error Type: SMTP\r\n   Error Description: The host specified as SMTP relay server could not be found. Please contact your server administrator.\r\n\r\n");
         else
            bounceMessageText = _T("   Error Type: SMTP\r\n   Error Description: No mail servers appear to exists for the recipient's address.\r\n   Additional information: Please check that you have not misspelled the recipient's email address.\r\n\r\n");
      }
      else
      {
         bounceMessageText = _T("   Error Type: SMTP\r\n   Error Description: Unable to find the recipient's email server. The DNS query has failed.\r\n\r\n");
      }

      // Update the recipients with the bounce message text and delivery result.
      for(std::shared_ptr<MessageRecipient> recipient : vecRecipients)
      {
         // Temp change to force non fatal no matter DNS result
         // Messages bouncing immediately due to no mail servers due to DNS issue
         recipient->SetDeliveryResult(MessageRecipient::ResultNonFatalError);
         // recipient->SetDeliveryResult(bDNSQueryOK ? MessageRecipient::ResultFatalError : MessageRecipient::ResultNonFatalError);
         recipient->SetErrorMessage(bounceMessageText);
      }  
   }

   void
   ExternalDelivery::DeliverToSingleServer_(std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients,
                                            std::shared_ptr<ServerInfo> serverInfo)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Connects to a remote server and delivers the message to it.
   //---------------------------------------------------------------------------()
   {
      LOG_DEBUG(Formatter::Format("Starting external delivery process. Server: {0} ({1}), Port: {2}, Security: {3}, User name: {4}", 
         serverInfo->GetHostName(), 
         serverInfo->GetIpAddress(), 
         serverInfo->GetPort(), 
         serverInfo->GetEffectiveConnectionSecurity(), 
         serverInfo->GetUsername()));

      std::shared_ptr<IOService> pIOService = Application::Instance()->GetIOService();

      std::shared_ptr<Event> disconnectEvent = std::shared_ptr<Event>(new Event()) ;
      std::shared_ptr<SMTPClientConnection> pClientConnection 
         = std::shared_ptr<SMTPClientConnection> (new SMTPClientConnection(serverInfo->GetEffectiveConnectionSecurity(), pIOService->GetIOContext(), pIOService->GetClientContext(), disconnectEvent, serverInfo->GetHostName()));

      // Apply TLS policy requirements (MTA-STS / DANE) for this connection.
      if (serverInfo->GetRequirePeerVerification())
         pClientConnection->SetRequirePeerVerification();

      if (!serverInfo->GetDaneRecords().empty())
         pClientConnection->SetDaneRecords(serverInfo->GetDaneRecords());

      pClientConnection->SetDelivery(original_message_, vecRecipients);

      if (!serverInfo->GetUsername().IsEmpty())
         pClientConnection->SetAuthInfo(serverInfo->GetUsername(), serverInfo->GetPassword());

      // Determine what local IP address to use.
      IPAddress localAddress = GetLocalAddress_();

      if (pClientConnection->Connect(serverInfo->GetIpAddress(), serverInfo->GetPort(), localAddress))
      {
         // Make sure we keep no references to the TCP connection so that it
         // can be terminated whenever. We're no longer own the connection.
         pClientConnection.reset();

         disconnectEvent->Wait();
      }

      LOG_DEBUG("External delivery process completed");

   }

   IPAddress 
   ExternalDelivery::GetLocalAddress_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Determines what local IP address to use when delivering to this host.
   //---------------------------------------------------------------------------()
   {
      IPAddress localAddress;

      std::shared_ptr<SMTPConfiguration> pSMTPConfig = Configuration::Instance()->GetSMTPConfiguration();

      String smtpSettingBindToIP = pSMTPConfig->GetSMTPDeliveryBindToIP();
      String ruleBindToAddress = _globalRuleResult.GetBindToAddress();

      if (!ruleBindToAddress.IsEmpty())
         localAddress.TryParse(ruleBindToAddress);
      else if (!smtpSettingBindToIP.IsEmpty())
         localAddress.TryParse(smtpSettingBindToIP);

      return localAddress;

   }

   void 
   ExternalDelivery::CollectDeliveryResult_(const String &serverHostName, 
                                             std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients, 
                                             std::vector<String> &saErrorMessages,
                                             std::map<String,String> &mapFailedDueToNonFatalError)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // This function is called after delivery has ended. It goes through the recipients
   // and collects information on how the delivery went (good or bad). If delivery went
   // good, the recipient is deleted from the database.
   //---------------------------------------------------------------------------()
   {
      LOG_DEBUG("Summarizing delivery result");

      // Check how the delivery went.
      for(std::shared_ptr<MessageRecipient> recipient : vecRecipients)
      {
         if (recipient->GetDeliveryResult() == MessageRecipient::ResultOK)
         {
            AWStats::LogDeliverySuccess(_sendersIP, serverHostName, original_message_, recipient->GetAddress());

            // Delete this recipient from the database.
            PersistentMessageRecipient::DeleteObject(recipient);
         }
         else if (recipient->GetDeliveryResult() == MessageRecipient::ResultNonFatalError)
         {
            mapFailedDueToNonFatalError[recipient->GetAddress()] = recipient->GetErrorMessage();
         }
         else if (recipient->GetDeliveryResult() == MessageRecipient::ResultFatalError)
         {
            // Yes, this is a permanent error.
            String sSingleErrorMsg;
            String sRecipient = recipient->GetAddress();
            sSingleErrorMsg = sRecipient + "\r\n";
            sSingleErrorMsg = sSingleErrorMsg + recipient->GetErrorMessage();
            sSingleErrorMsg = sSingleErrorMsg + "\r\n";

            saErrorMessages.push_back(sSingleErrorMsg);  

            // Delete this recipient from the database.
            PersistentMessageRecipient::DeleteObject(recipient);

            AWStats::LogDeliveryFailure(_sendersIP, original_message_->GetFromAddress(), recipient->GetAddress(),  550);
            Events::FireOnDeliveryFailed(original_message_, _sendersIP, recipient->GetAddress(), recipient->GetErrorMessage());
         }
         else
         {
            mapFailedDueToNonFatalError[recipient->GetAddress()] = "Remote server closed connection.";
         }

      }  

      LOG_DEBUG("Summarized delivery results");
   }

   /// Checks if we should reschedule the message for later delivery. If so, we do.
   /// Returns true if the message is rescheduled.
   bool
   ExternalDelivery::RescheduleDelivery_(std::map<String,String> &mapFailedDueToNonFatalError, std::vector<String> &saErrorMessages)
   {

      LOG_DEBUG("SD::RescheduleDelivery_");

      // We have failed recipients. Iterate over one of them at a time
      long iMaxNoOfRetries = 0;
      long lMinutesBewteen = 0;
      int iCurNoOfRetries = original_message_->GetNoOfRetries() ;

      quick_retries_ = IniFileSettings::Instance()->GetQuickRetries();
      quick_retries_Minutes = IniFileSettings::Instance()->GetQuickRetriesMinutes();
      queue_randomness_minutes_ = IniFileSettings::Instance()->GetQueueRandomnessMinutes();

      // Variables used to generate randomness value for retry delay
      errno_t rnd_err;
      unsigned int tmp_rnd = 0;
      int iRandomAdjust = 0;

      // See if randomness is enabled to work around Win2k compatability issue
      // plus saves work if not enabled which is default
      if (queue_randomness_minutes_ > 0)
      {

         // Get our random #
         // LOG_DEBUG("Windows 2000 does not support rand_s & pukes here");
         rnd_err = (rand_s(&tmp_rnd));

         // If no error getting random # use it
         if (rnd_err == 0)
            iRandomAdjust = (unsigned int) ((double)tmp_rnd / (double) UINT_MAX * queue_randomness_minutes_) + 1;
      }

      LOG_DEBUG("Retrieving retry options.");
      if (GetRetryOptions_(mapFailedDueToNonFatalError, iMaxNoOfRetries, lMinutesBewteen))
      {
         // so return now since no need for retry at this time

         // For now we unlock message here but might be best to do @ ETRN time..
         PersistentMessage::UnlockObject(original_message_);

         LOG_APPLICATION("SMTPDeliverer - Route Message: HOLD for later delivery..");
         return true; // Do not delete e-mail now
      }

      if (iCurNoOfRetries < iMaxNoOfRetries)
      {
         // We should try at least once more - reschedule the message.
         LOG_DEBUG("Starting rescheduling.");

         // First few retries should be quicker for greylisting IF enabled
         if (iCurNoOfRetries < quick_retries_) 
         {
            LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Message could not be delivered. Greylisting? Scheduling it for quick retry " + StringParser::IntToString(iCurNoOfRetries + 1) + " of " + StringParser::IntToString(quick_retries_) + " in " + StringParser::IntToString(quick_retries_Minutes + iRandomAdjust) + " minutes.");
            PersistentMessage::SetNextTryTime(original_message_->GetID(), true, quick_retries_Minutes + iRandomAdjust);
         
            // Unlock the message now so that a future delivery thread can pick it up.
            PersistentMessage::UnlockObject(original_message_);
         
            LOG_DEBUG("Message rescheduled for later quick delivery. (Greylisting?)");
            return true; // Do not delete e-mail now
         }
         else
         {
            LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Message could not be delivered. Scheduling it for later delivery in " + StringParser::IntToString(lMinutesBewteen + iRandomAdjust) + " minutes.");
            PersistentMessage::SetNextTryTime(original_message_->GetID(), true, lMinutesBewteen + iRandomAdjust);
         
            // Unlock the message now so that a future delivery thread can pick it up.
            PersistentMessage::UnlockObject(original_message_);
         
            LOG_DEBUG("Message rescheduled for later delivery.");
            return true; // Do not delete e-mail now
         }
      }
      else
      {
         LOG_DEBUG("Aborting delivery.");

         // We are finished trying. Let's give up!
         LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Message could not be delivered. Returning error log to sender.");

         // Delivery failed the last time.
         String sErrorMessage;

         auto iterFailed = mapFailedDueToNonFatalError.begin();
         while (iterFailed != mapFailedDueToNonFatalError.end())
         {
            if (!sErrorMessage.IsEmpty())
               sErrorMessage += "\r\n";

            String sEmailAddress = (*iterFailed).first;
            String sFailed = (*iterFailed).second;
            sErrorMessage += sEmailAddress + "\r\n" + sFailed;

            // Delivery has failed for the last time.
            AWStats::LogDeliveryFailure(_sendersIP, original_message_->GetFromAddress(), sEmailAddress,  550);
            Events::FireOnDeliveryFailed(original_message_, _sendersIP, sEmailAddress, sFailed);

            iterFailed++;
         }

         String sMsg;
         sMsg.Format(_T("Tried %d time(s)"), iMaxNoOfRetries+ 1);

         sErrorMessage += "\r\n";
         sErrorMessage += sMsg;
         sErrorMessage += "\r\n\r\n";
         saErrorMessages.push_back(sErrorMessage);

         LOG_DEBUG("Message not rescheduled for later delivery.")
         
        return false;
      }
   }

   /// Returns the retry options for a list of address.
   /// The maximum number of retries and the maximum number of mintues between
   /// every try.
   // Type changed to bool for use in ETRN's
   bool 
   ExternalDelivery::GetRetryOptions_(std::map<String,String> &mapFailedDueToNonFatalError, long &lNoOfRetries, long &lMinutesBetween)
   {
      std::shared_ptr<SMTPConfiguration> pSMTPConfig = Configuration::Instance()->GetSMTPConfiguration();
      std::shared_ptr<Routes> pRoutes = Configuration::Instance()->GetSMTPConfiguration()->GetRoutes();

      bool bFirstMatchingRoute = true;

      // First fetch the default values. Theese are used
      // if we can't find a route for any of the domains.
      lNoOfRetries = pSMTPConfig->GetNoOfRetries();
      lMinutesBetween  = pSMTPConfig->GetMinutesBetweenTry();

      auto iterAddress = mapFailedDueToNonFatalError.begin();
      std::map<String, std::shared_ptr<Route> > matchingRoutes;

      bool recipientsFoundNotMatchingRoute = false;

      while (iterAddress != mapFailedDueToNonFatalError.end())
      {
         String sAddress = (*iterAddress).first;
         String sDomainName = StringParser::ExtractDomain (sAddress).ToLower();
         
         std::shared_ptr<Route> pRoute = pRoutes->GetItemByNameWithWildcardMatch(sDomainName);

         if (pRoute)
         {
            int lTmpNoOfRetries = pRoute->NumberOfTries() - 1;
            int lTmpMinutesBetween = pRoute->MinutesBetweenTry();

            if (matchingRoutes.size() == 0)
            {
               lNoOfRetries = lTmpNoOfRetries;
               lMinutesBetween = lTmpMinutesBetween;
            }
            else
            {
               if (lTmpNoOfRetries > lNoOfRetries)
                  lNoOfRetries = lTmpNoOfRetries;

               if (lTmpMinutesBetween > lMinutesBetween)
                  lMinutesBetween = lTmpMinutesBetween;
            }

            matchingRoutes[sDomainName] = pRoute;
         }
         else
            recipientsFoundNotMatchingRoute = true;

         iterAddress++;
      }

      // If ONLY 1 route was found & not any non routes say we HOLD message otherwise don't.
      // HOLD when non-route recipient would be BAD. :D
      if (matchingRoutes.size() == 1 && !recipientsFoundNotMatchingRoute)
      {
         std::shared_ptr<Route> route = (*matchingRoutes.begin()).second;
         String routeDescription = route->GetDescription();

         if (routeDescription.ToUpper().StartsWith(_T("ETRN")))
         {
            __int64 iRouteID = route->GetID();

            // Here we change ID, type to 3 for HOLD. Retries reset to ensure it doesn't
            // bounce yet. NOT 0 though to stop mirror account copy over & over
            SQLCommand command("update hm_messages set messageaccountid = @ROUTEID, messagetype = 3, messagecurnooftries =  1,  messagenexttrytime = '1901-01-01 00:00:01' where messageid = @MESSAGEID");
            
            command.AddParameter("@ROUTEID", iRouteID);
            command.AddParameter("@MESSAGEID", original_message_->GetID());

            if (Application::Instance()->GetDBManager()->Execute(command))
            {
               // Execute OK - Should do some error checking & logging here..
            }

            return true;  // Say we HELD message
         }
         
         return false;
      }
      else
         return false;  // Continue as normal, no HOLD         
   }
}