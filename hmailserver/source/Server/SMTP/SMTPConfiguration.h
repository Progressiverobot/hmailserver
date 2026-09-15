// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Common/BO/IncomingRelays.h"
#include "../Common/TCPIP/SocketConstants.h"

namespace HM
{
   class PropertySet;
   class Routes;
   class RemoteDomainPolicies;
   class DNSBlackLists;
   class BlockedAttachments;

   class SMTPConfiguration
   {
   public:
      SMTPConfiguration();
      virtual ~SMTPConfiguration();

      bool Load();

      void SetMaxSMTPConnections(int newVal);
      void SetAuthAllowPlainText(bool newVal);
      void SetAllowMailFromNull(bool newVal);
      void SetLogSMTPConversations(bool bNewVal);
      void SetUseORDB(bool NewVal);
      void SetUseSpamhaus(bool NewVal);
      void SetNoOfRetries(long lNewVal);
      void SetMinutesBetweenTry(long lHoursBetween);
      void SetSMTPRelayer(const String &sRelayer);
      
      void SetSMTPRelayerPort(long lPort);
      long GetSMTPRelayerPort();

      void SetSMTPRelayerConnectionSecurity(ConnectionSecurity connection_security);
      ConnectionSecurity GetSMTPRelayerConnectionSecurity();

      void SetSMTPConnectionSecurity(ConnectionSecurity connection_security);
      ConnectionSecurity GetSMTPConnectionSecurity();

      void SetMaxNoOfDeliveryThreads(int lNewValue);

      String GetWelcomeMessage() const;
      void SetWelcomeMessage(const String &sMessage);

      String GetSMTPDeliveryBindToIP() const;
      void SetSMTPDeliveryBindToIP(const String &sIP);

      bool GetBlockBareLFs() const;

      int GetMaxSMTPConnections();
      bool GetAuthAllowPlainText();
      bool GetAllowMailFromNull();

      long GetMinutesBetweenTry();
      long GetNoOfRetries();
      String GetSMTPRelayer() const;

      int GetMaxNoOfDeliveryThreads();
      

      bool GetSMTPRelayerRequiresAuthentication();
      void SetSMTPRelayerRequiresAuthentication(bool bNewVal);
      String GetSMTPRelayerUsername() const;
      void SetSMTPRelayerUsername(const String & Value);
      
      String GetSMTPRelayerPassword() const;
      void SetSMTPRelayerPassword(const String & Value);

      bool GetAllowIncorrectLineEndings();
      void SetAllowIncorrectLineEndings(bool bNewVal);

      int GetMaxMessageSize();
      void SetMaxMessageSize(int iNewVal);

      int GetMaxSMTPRecipientsInBatch();
      void SetMaxSMTPRecipientsInBatch(int iNewVal);

      int GetRuleLoopLimit();
      void SetRuleLoopLimit(int iNewVal);

      int GetMaxNumberOfMXHosts();
      void SetMaxNumberOfMXHosts(int iNewVal);


      bool XMLStore(XNode *pBackupNode, int Options);
      bool XMLLoad(XNode *pBackupNode, int iRestoreOptions);

      bool GetAddDeliveredToHeader();
      void SetAddDeliveredToHeader(bool bNewVal);

      void OnPropertyChanged(std::shared_ptr<Property> pProperty);

      std::shared_ptr<IncomingRelays> GetIncomingRelays() {return incoming_relays_;}
      std::shared_ptr<Routes> GetRoutes() {return routes_;}

      // What this server will do for a named remote domain - the TLS it demands
      // of it, the size and concurrency it will attempt, whether a recipient is
      // verified with the primary before mail for it is accepted. Beside the
      // routes because both are read on the delivery path for every message and
      // both are the SMTP configuration's to own; a route decides WHERE mail
      // goes, a policy decides what this server will and will not do when it
      // gets there. See RemoteDomainPolicy.h.
      std::shared_ptr<RemoteDomainPolicies> GetRemoteDomainPolicies() {return remote_domain_policies_;}

   private:

      std::shared_ptr<PropertySet> GetSettings_() const;
      std::shared_ptr<IncomingRelays> incoming_relays_;
      std::shared_ptr<Routes> routes_;
      std::shared_ptr<RemoteDomainPolicies> remote_domain_policies_;
   };
}
