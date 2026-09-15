// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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

      // Which rule demanded the TLS, in words an administrator can read.
      //
      // Empty for a delivery that is merely trying TLS; set when a remote domain
      // policy required it. It travels to SMTPClientConnection so that "the
      // remote does not offer STARTTLS" comes back as a DEFERRAL naming the
      // policy rather than as the permanent 5.7.0 that an unattributed
      // required-STARTTLS failure produces. A policy that silently downgraded
      // would be worse than no policy; one that bounced the mail without saying
      // which rule refused it is not much better.
      void SetTlsPolicyReason(const String &reason) { tls_policy_reason_ = reason; }
      String GetTlsPolicyReason() const { return tls_policy_reason_; }
         
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
      String tls_policy_reason_;

   };
}
