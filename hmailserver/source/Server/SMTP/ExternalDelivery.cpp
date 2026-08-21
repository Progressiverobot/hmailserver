// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
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
#include "../common/Util/OutboundOAuth2TokenClient.h"
#include "../common/Util/Parsing/StringParser.h"
#include "../common/Application/IniFileSettings.h"
#include "../Common/Util/TlsRptStore.h"
#include "../Common/Util/RateLimiter.h"

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
   namespace
   {
      // Records a recipient whose delivery failed non-fatally, keyed by address.
      //
      // A plain mapFailed[address] = text is what this replaced, and it cannot
      // work any more: DeliveryFailure has no default constructor, precisely so
      // that no code path can produce a half-filled one. Find-then-insert or
      // assign is the same operation the subscript used to perform.
      void RememberNonFatalFailure_(std::map<String, DeliveryFailure> &pendingFailures,
                                    std::shared_ptr<MessageRecipient> recipient,
                                    const String &humanReadableText,
                                    const String &enhancedStatusCode)
      {
         DeliveryFailure failure(recipient->GetAddress(), enhancedStatusCode, humanReadableText);

         // Empty unless a remote server really answered - see MessageRecipient.
         failure.SetRemoteSmtpReply(recipient->GetRemoteSmtpReply());
         failure.SetRemoteMta(recipient->GetRemoteMta());

         auto existing = pendingFailures.find(recipient->GetAddress());

         if (existing == pendingFailures.end())
            pendingFailures.insert(std::make_pair(recipient->GetAddress(), failure));
         else
            existing->second = failure;
      }
   }

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
   ExternalDelivery::Perform(std::vector<DeliveryFailure> &saErrorMessages)
   {
      std::map<String,DeliveryFailure> mapFailedDueToNonFatalError;

      // DSN (RFC 3461): addresses whose recipients opted out of failure
      // notifications (NOTIFY=NEVER, or a NOTIFY list without FAILURE).
      std::set<String> suppressFailureDsnAddresses;

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
               // Each batch gets its own copy: DeliverToSingleDomain_ overwrites the
               // host name with the MX host it selects and can clear the connection
               // security. Reusing one object meant every batch after the first
               // looked up the MTA-STS policy of an MX host rather than of the
               // recipient domain (so enforcement silently stopped applying), lost
               // MX failover, and could inherit a STARTTLS downgrade from the
               // previous batch.
               std::shared_ptr<ServerInfo> batchServerInfo = std::shared_ptr<ServerInfo>(new ServerInfo(*serverInfo));

               // Deliver the message to the remote server.
               DeliverToSingleDomain_(batch, batchServerInfo);

               // Check what status we got on the external deliveries.
               CollectDeliveryResult_(batchServerInfo->GetHostName(), batch, saErrorMessages, mapFailedDueToNonFatalError, suppressFailureDsnAddresses);

               batch.clear();
            }

            iterRecipient++;
         }

      }

      if (mapFailedDueToNonFatalError.size() > 0)
      {   
         bool messageRescheduled = RescheduleDelivery_(mapFailedDueToNonFatalError, saErrorMessages, suppressFailureDsnAddresses);
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

      // Per-destination outbound rate shaping. A configured [Settings]
      // MaxOutboundPerDestinationPerMinute caps how many messages this server
      // sends to a single destination per minute; when exceeded the delivery is
      // deferred (non-fatal) and retried later. 0 = unlimited (default, no-op).
      int maxOutboundPerDestination = IniFileSettings::Instance()->GetMaxOutboundPerDestinationPerMinute();
      if (maxOutboundPerDestination > 0)
      {
         if (!RateLimiter::Instance()->TryConsume(_T("smtp-out:") + recipientDomain.ToLower(), maxOutboundPerDestination))
         {
            LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": Delivery to " + recipientDomain + " deferred due to the per-destination rate limit.");
            String errorMessage = _T("   Error Type: SMTP\r\n   Error Description: Delivery temporarily deferred by the sending server's per-destination rate limit.\r\n\r\n");
            // RFC 3463 X.4.5 "Mail system congestion". The congestion is this
            // server's own deliberate shaping rather than an overload, but the
            // meaning a sender needs - too much mail for this route right now,
            // try later - is exactly what X.4.5 carries.
            HandleExternalDeliveryFailure_(vecRecipients, false, errorMessage, _T("4.4.5"));
            return;
         }
      }

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
               // RFC 3463 X.7.0 "Other or undefined security status". RFC 8461
               // registers no enhanced code of its own, and the undefined
               // security code says the true thing - refused on security grounds
               // - without borrowing a code that means something else.
               HandleExternalDeliveryFailure_(vecRecipients, false, errorMessage, _T("4.7.0"));
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

      // RFC 7672 section 2.2: DANE applies only to a host learned from a
      // DNSSEC-validated MX RRset. Validating the TLSA record alone proves nothing
      // when the attacker chose the host - a forged MX answer points at a name whose
      // own TLSA record then validates perfectly, and DANE reports success.
      //
      // Looked up once per domain rather than per host, and only when DANE is on.
      // Every outcome except Bogus leaves delivery exactly as it was: an unsigned
      // domain - which is most of them - is Insecure, and Insecure means "deliver
      // without DANE", which is what happened before this existed.
      DnssecResolver::ChainStatus mxChainStatus = DnssecResolver::ChainStatus::Insecure;
      std::vector<String> validatedMxHosts;

      if (daneEnabled)
      {
         std::vector<AnsiString> exchanges;
         DnssecResolver dnssecResolver;
         mxChainStatus = dnssecResolver.QueryMx(recipientDomain, exchanges);

         for (const AnsiString &exchange : exchanges)
            validatedMxHosts.push_back(String(exchange));

         if (mxChainStatus == DnssecResolver::ChainStatus::Secure)
            LOG_DEBUG("DANE: the MX RRset for " + recipientDomain + " is DNSSEC-validated; DANE may be applied to the " + StringParser::IntToString((int) validatedMxHosts.size()) + " host(s) it names.");
      }

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
            // The RFC 7672 section 2.2 status of THIS host: the MX RRset validated,
            // and this host is one of the names it published.
            TlsPolicy::MxDnssecStatus mxStatus =
               TlsPolicy::EvaluateMxDnssecStatus(hostAndIp.GetHostName(), mxChainStatus, validatedMxHosts);

            if (mxStatus == TlsPolicy::MxDnssecStatus::Bogus)
            {
               // A forged or broken chain over the MX RRset itself. The host name is
               // not trustworthy, so neither is anything published under it.
               LOG_APPLICATION("SMTPDeliverer - Message " + StringParser::IntToString(original_message_->GetID()) + ": the MX RRset for " + recipientDomain + " failed DNSSEC validation. Skipping " + hostAndIp.GetHostName() + " (RFC 7672 section 2.2).");
               TlsRptStore::Instance()->RecordFailure(recipientDomain, "tlsa", "mx rrset dnssec validation", "dnssec-invalid", hostAndIp.GetHostName());
               continue;
            }

            TlsPolicy::TlsaLookupStatus tlsaStatus = TlsPolicy::TlsaLookupStatus::NoRecords;
            daneRecords = TlsPolicy::GetTlsaRecords(hostAndIp.GetHostName(), serverInfo->GetPort(), tlsaStatus, mxStatus);

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
         // As above: refused on security grounds, RFC 3463 X.7.0.
         HandleExternalDeliveryFailure_(vecRecipients, false, errorMessage, _T("4.7.0"));
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
                                                      String &sErrorString,
                                                      const String &enhancedStatusCode)
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

            pRecipient->SetDeliveryError(sErrorString, enhancedStatusCode);
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

      // The enhanced status code alongside it. Every one of these is class 4,
      // and deliberately so even where the condition looks permanent: the loop
      // below forces a NON-FATAL delivery result whatever DNS said, so the
      // message is retried, and a 5.x.x code paired with a retry would tell the
      // sender's software that the address is dead while this server is still
      // trying it. The class the report carries has to be the class this server
      // is actually acting on.
      String enhancedStatusCode;

      // Generate a string which will be included in the bounce message.

      if (bDNSQueryOK)
      {
         if (isSpecificRelayServer)
         {
            bounceMessageText = _T("   Error Type: SMTP\r\n   Error Description: The host specified as SMTP relay server could not be found. Please contact your server administrator.\r\n\r\n");
            // RFC 3463 X.3.5 "System incorrectly configured" - the relay host is
            // this installation's own setting, and it does not resolve.
            enhancedStatusCode = _T("4.3.5");
         }
         else
         {
            bounceMessageText = _T("   Error Type: SMTP\r\n   Error Description: No mail servers appear to exists for the recipient's address.\r\n   Additional information: Please check that you have not misspelled the recipient's email address.\r\n\r\n");
            // RFC 3463 X.1.2 "Bad destination system address" - DNS answered, and
            // the answer was that the recipient's domain publishes nowhere to
            // deliver mail.
            enhancedStatusCode = _T("4.1.2");
         }
      }
      else
      {
         bounceMessageText = _T("   Error Type: SMTP\r\n   Error Description: Unable to find the recipient's email server. The DNS query has failed.\r\n\r\n");
         // RFC 3463 X.4.4 "Unable to route" - its text names precisely this: the
         // routing information was unavailable from the directory server.
         enhancedStatusCode = _T("4.4.4");
      }

      // Update the recipients with the bounce message text and delivery result.
      for(std::shared_ptr<MessageRecipient> recipient : vecRecipients)
      {
         // Temp change to force non fatal no matter DNS result
         // Messages bouncing immediately due to no mail servers due to DNS issue
         recipient->SetDeliveryResult(MessageRecipient::ResultNonFatalError);
         // recipient->SetDeliveryResult(bDNSQueryOK ? MessageRecipient::ResultFatalError : MessageRecipient::ResultNonFatalError);
         recipient->SetDeliveryError(bounceMessageText, enhancedStatusCode);
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
      {
         pClientConnection->SetAuthInfo(serverInfo->GetUsername(), serverInfo->GetPassword());

         // Outbound XOAUTH2 (the Microsoft 365 Basic-auth cutover): when this
         // destination is one the administrator configured OAuth for, the AUTH
         // step presents a bearer token instead of the password. The token is
         // fetched (or served from cache) HERE, on the delivery thread, the same
         // place the MTA-STS policy fetch already lives. A fetch failure is
         // reported and the delivery proceeds with LOGIN - which the provider
         // will refuse VISIBLY, putting the real error in front of the
         // administrator instead of a silent stall.
         String oauthHosts = IniFileSettings::Instance()->GetOutboundOAuth2Hosts();
         String destinationHost = serverInfo->GetHostName();

         bool oauthApplies = false;
         std::vector<String> hosts = StringParser::SplitString(oauthHosts, ",");
         for (String host : hosts)
         {
            host.TrimLeft();
            host.TrimRight();
            if (!host.IsEmpty() && host.CompareNoCase(destinationHost) == 0)
            {
               oauthApplies = true;
               break;
            }
         }

         if (oauthApplies)
         {
            String fixedToken = IniFileSettings::Instance()->GetOutboundOAuth2FixedToken();

            if (!fixedToken.IsEmpty())
            {
               pClientConnection->SetOAuthBearer(fixedToken);
            }
            else if (!IniFileSettings::Instance()->GetOutboundOAuth2TokenUrl().IsEmpty())
            {
               String token, tokenError;
               if (OutboundOAuth2TokenClient::Instance()->GetToken(token, tokenError))
               {
                  pClientConnection->SetOAuthBearer(token);
               }
               else
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5904, "ExternalDelivery::InitiateExternalConnection",
                     "Could not obtain an OAuth2 token for " + destinationHost + ": " + tokenError +
                     " The delivery will be attempted with password authentication, which this provider is expected to refuse.");
               }
            }
         }
      }

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
                                             std::vector<DeliveryFailure> &saErrorMessages,
                                             std::map<String,DeliveryFailure> &mapFailedDueToNonFatalError,
                                             std::set<String> &suppressFailureDsnAddresses)
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
         // DSN (RFC 3461): remember recipients that opted out of failure
         // notifications so that any eventual bounce can be suppressed.
         int notify = recipient->GetDSNNotify();
         bool wantsFailureDsn = (notify == MessageRecipient::DSNNotifyDefault) ||
                                ((notify & MessageRecipient::DSNNotifyFailure) != 0);
         if (!wantsFailureDsn)
            suppressFailureDsnAddresses.insert(recipient->GetAddress());

         if (recipient->GetDeliveryResult() == MessageRecipient::ResultOK)
         {
            AWStats::LogDeliverySuccess(_sendersIP, serverHostName, original_message_, recipient->GetAddress());

            // Delete this recipient from the database.
            //
            // Unchecked, and a recipient row that survives is what decides whether the
            // message is rescheduled - so this address is delivered to again on the
            // next pass, and again after that. Duplicate mail arriving repeatedly, to
            // one recipient of a message that went out correctly to the others, is
            // close to undiagnosable from the outside.
            if (!PersistentMessageRecipient::DeleteObject(recipient))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 6105, "ExternalDelivery::Perform",
                  Formatter::Format("Message {0} was delivered to {1} but that recipient could not be removed from the database, so it will be delivered to them again.",
                     original_message_->GetID(), recipient->GetAddress()));
            }
         }
         else if (recipient->GetDeliveryResult() == MessageRecipient::ResultNonFatalError)
         {
            RememberNonFatalFailure_(mapFailedDueToNonFatalError, recipient, recipient->GetErrorMessage(),
               // Every path that sets a non-fatal result also sets a status now.
               // The fallback exists so that a path added later without one still
               // produces a well-formed report - RFC 3463 X.0.0, "other undefined
               // status", which is what "we do not know" looks like in this
               // vocabulary and is not a claim about the recipient.
               recipient->GetEnhancedStatusCode().IsEmpty() ? String(_T("4.0.0")) : recipient->GetEnhancedStatusCode());
         }
         else if (recipient->GetDeliveryResult() == MessageRecipient::ResultFatalError)
         {
            // Yes, this is a permanent error.

            // DSN (RFC 3461): only generate a failure notification when the sender
            // has not opted out via NOTIFY=NEVER (or a NOTIFY list without FAILURE).
            int notify = recipient->GetDSNNotify();
            bool sendFailureDsn = (notify == MessageRecipient::DSNNotifyDefault) ||
                                  ((notify & MessageRecipient::DSNNotifyFailure) != 0);

            if (sendFailureDsn)
            {
               String sSingleErrorMsg;
               String sRecipient = recipient->GetAddress();
               sSingleErrorMsg = sRecipient + "\r\n";
               sSingleErrorMsg = sSingleErrorMsg + recipient->GetErrorMessage();
               sSingleErrorMsg = sSingleErrorMsg + "\r\n";

               // The status the remote server's reply produced. Only
               // SMTPClientConnection can reach a fatal result, and it always
               // records one, but the fallback is the honest undefined permanent
               // code rather than a guess at which of the 5.x.x conditions it was.
               DeliveryFailure failure(sRecipient,
                  recipient->GetEnhancedStatusCode().IsEmpty() ? String(_T("5.0.0")) : recipient->GetEnhancedStatusCode(),
                  sSingleErrorMsg);

               failure.SetRemoteSmtpReply(recipient->GetRemoteSmtpReply());
               failure.SetRemoteMta(recipient->GetRemoteMta());

               saErrorMessages.push_back(failure);
            }

            // Delete this recipient from the database. As above: a row that survives
            // is retried, so a permanent failure would be retried and bounced again.
            if (!PersistentMessageRecipient::DeleteObject(recipient))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 6105, "ExternalDelivery::Perform",
                  Formatter::Format("Delivery of message {0} to {1} failed permanently, but that recipient could not be removed from the database, so the attempt and its bounce will be repeated.",
                     original_message_->GetID(), recipient->GetAddress()));
            }

            AWStats::LogDeliveryFailure(_sendersIP, original_message_->GetFromAddress(), recipient->GetAddress(),  550, original_message_->GetID());
            Events::FireOnDeliveryFailed(original_message_, _sendersIP, recipient->GetAddress(), recipient->GetErrorMessage());
         }
         else
         {
            // RFC 3463 X.4.2 "Bad connection". Reached when the recipient came
            // back undefined - nothing in the session ever gave a verdict for it.
            RememberNonFatalFailure_(mapFailedDueToNonFatalError, recipient, _T("Remote server closed connection."), _T("4.4.2"));
         }

      }

      LOG_DEBUG("Summarized delivery results");
   }

   /// Checks if we should reschedule the message for later delivery. If so, we do.
   /// Returns true if the message is rescheduled.
   bool
   ExternalDelivery::RescheduleDelivery_(std::map<String,DeliveryFailure> &mapFailedDueToNonFatalError, std::vector<DeliveryFailure> &saErrorMessages, std::set<String> &suppressFailureDsnAddresses)
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
         //
         // One report per recipient, where this used to build a single block of
         // prose covering all of them. RFC 3464 needs a per-recipient record -
         // "which address failed, and with what status" is the whole question -
         // and there is no way back to that from a concatenated string.
         //
         // The wording is unchanged, including the trailing "Tried N time(s)".
         // For the single-recipient case, which is the overwhelming majority,
         // the text a person reads is byte-for-byte what it was. For several
         // recipients each now carries its own copy of that line instead of the
         // recipients sharing one at the end, which is what makes each block
         // self-contained enough to stand as its own recipient record.
         String sMsg;
         sMsg.Format(_T("Tried %d time(s)"), iMaxNoOfRetries+ 1);

         auto iterFailed = mapFailedDueToNonFatalError.begin();
         while (iterFailed != mapFailedDueToNonFatalError.end())
         {
            String sEmailAddress = (*iterFailed).first;
            const DeliveryFailure &pendingFailure = (*iterFailed).second;
            String sFailed = pendingFailure.GetText();

            // Delivery has failed for the last time.
            AWStats::LogDeliveryFailure(_sendersIP, original_message_->GetFromAddress(), sEmailAddress,  550, original_message_->GetID());
            Events::FireOnDeliveryFailed(original_message_, _sendersIP, sEmailAddress, sFailed);

            // DSN (RFC 3461): suppress the bounce text for recipients that opted
            // out of failure notifications (NOTIFY=NEVER or NOTIFY without FAILURE).
            if (suppressFailureDsnAddresses.find(sEmailAddress) != suppressFailureDsnAddresses.end())
            {
               iterFailed++;
               continue;
            }

            String sErrorMessage = sEmailAddress + "\r\n" + sFailed;
            sErrorMessage += "\r\n";
            sErrorMessage += sMsg;
            sErrorMessage += "\r\n\r\n";

            // The status carried forward is the one the LAST attempt produced,
            // not a generic "gave up" code, because it is the more specific true
            // statement: 4.4.1 says nobody answered, 4.4.2 says the conversation
            // broke, 4.7.0 says a security policy blocked it. All of them are
            // class 4, which is the class RFC 3463 defines for exactly this - a
            // temporary condition that persisted until the attempts were
            // abandoned - and which keeps a list manager from unsubscribing an
            // address that was never shown to be bad.
            DeliveryFailure expired(sEmailAddress, pendingFailure.GetEnhancedStatusCode(), sErrorMessage);
            expired.SetRemoteSmtpReply(pendingFailure.GetRemoteSmtpReply());
            expired.SetRemoteMta(pendingFailure.GetRemoteMta());

            saErrorMessages.push_back(expired);

            iterFailed++;
         }

         LOG_DEBUG("Message not rescheduled for later delivery.")

        return false;
      }
   }

   /// Returns the retry options for a list of address.
   /// The maximum number of retries and the maximum number of mintues between
   /// every try.
   // Type changed to bool for use in ETRN's
   bool 
   ExternalDelivery::GetRetryOptions_(std::map<String,DeliveryFailure> &mapFailedDueToNonFatalError, long &lNoOfRetries, long &lMinutesBetween)
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