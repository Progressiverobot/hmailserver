// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../TCPIP/SocketConstants.h"
#include "../TCPIP/DaneVerifier.h"

namespace HM
{
   class ServerInfo
   {
   public:
	   ServerInfo(bool fixed, const String &host_name, const String &ip_address, int port, const String&userName, const String &passWord, ConnectionSecurity connection_security);
	   virtual ~ServerInfo();

      bool GetFixed();
      String GetHostName();
      String GetIpAddress();
      int GetPort ();
      String GetUsername();
      String GetPassword();
      ConnectionSecurity GetConnectionSecurity();

      // Returns the connection security taking TLS enforcement (MTA-STS /
      // DANE) into account. If TLS is required by policy, optional or
      // disabled STARTTLS is upgraded to required STARTTLS.
      ConnectionSecurity GetEffectiveConnectionSecurity();
      
      void SetHostName(const String &hostName);
      void SetIpAddress(const String &ip_address);

      void DisableConnectionSecurity();

      // TLS policy enforcement (MTA-STS RFC 8461 / DANE RFC 7672).
      void SetRequireTls(bool value) { require_tls_ = value; }
      bool GetRequireTls() const { return require_tls_; }

      void SetRequirePeerVerification(bool value) { require_peer_verification_ = value; }
      bool GetRequirePeerVerification() const { return require_peer_verification_; }

      void SetDaneRecords(const std::vector<TlsaRecord> &records) { dane_records_ = records; }
      const std::vector<TlsaRecord>& GetDaneRecords() const { return dane_records_; }
         
      bool operator== (const ServerInfo &other) const;

   private:

      bool fixed_;
      String host_name_;
      String ip_address_;
      int port_;
      String userName_;
      String passWord_;
      ConnectionSecurity connection_security_;
      bool require_tls_ = false;
      bool require_peer_verification_ = false;
      std::vector<TlsaRecord> dane_records_;

   };
}
