// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Outbound TLS policy support:
//   - MTA-STS (RFC 8461): SMTP MTA Strict Transport Security
//   - DANE TLSA lookups (RFC 6698 / RFC 7672) for outbound SMTP
//
// DANE lookups are DNSSEC-validated in-process (see DnssecResolver):
// the chain of trust is verified from the TLSA RRset up to the IANA
// root trust anchors. Validation results follow RFC 7672:
//   secure   -> TLSA records are enforced
//   insecure -> treated as if no TLSA records exist
//   bogus    -> the MX host must not be used
// Setting DnssecValidationEnabled=0 reverts to opportunistic
// (unvalidated) TLSA usage.

#pragma once

#include "../Common/TCPIP/DaneVerifier.h"

namespace HM
{
   class TlsPolicy
   {
   public:

      enum StsMode
      {
         StsNone = 0,
         StsTesting = 1,
         StsEnforce = 2
      };

      struct StsPolicy
      {
         StsMode mode = StsNone;
         std::vector<String> mx_patterns;
      };

      // Returns the MTA-STS policy for a recipient domain. Policies are
      // cached in-process according to the policy max_age.
      static StsPolicy GetStsPolicy(const String &domain);

      // True if the given MX host name matches one of the mx patterns in
      // the policy (RFC 8461 section 4.1; "*." matches one leftmost label).
      static bool HostMatchesStsPolicy(const String &host_name, const StsPolicy &policy);

      enum class TlsaLookupStatus
      {
         DnssecValidated,  // records returned and DNSSEC-validated
         Unvalidated,      // records returned without validation
                           // (DnssecValidationEnabled=0)
         NoRecords,        // no usable records - deliver without DANE
         Bogus             // DNSSEC validation failed - do not use host
      };

      // Returns DANE-EE (usage 3) TLSA records published for the given
      // MX host and port. An empty result means no usable records exist;
      // status describes the DNSSEC validation outcome.
      static std::vector<TlsaRecord> GetTlsaRecords(const String &host_name, int port, TlsaLookupStatus &status);

   private:

      struct CachedStsPolicy
      {
         StsPolicy policy;
         AnsiString id;
         time_t expires_at = 0;
         time_t revalidate_at = 0;
      };

      static bool LookupStsDnsRecord_(const String &domain, AnsiString &id);
      static bool FetchStsPolicy_(const String &domain, StsPolicy &policy, int &max_age);
      static bool ParseStsPolicyBody_(const AnsiString &body, StsPolicy &policy, int &max_age);
      static bool HttpsGet_(const String &host, const AnsiString &path, AnsiString &response_body);

      static bool GetDnsServers_(std::vector<sockaddr_in> &servers);
      static bool RunDnsQuery_(const sockaddr_in &server, const AnsiString &name, unsigned short query_type, std::vector<unsigned char> &response);
      static bool ParseTlsaResponse_(const std::vector<unsigned char> &response, std::vector<TlsaRecord> &records);
      static bool SkipDnsName_(const std::vector<unsigned char> &data, size_t &offset);
      static std::vector<TlsaRecord> LookupTlsaOpportunistic_(const String &host_name, int port);
      static std::vector<TlsaRecord> FilterUsableTlsaRecords_(const std::vector<TlsaRecord> &records);

      static boost::recursive_mutex sts_cache_mutex_;
      static std::map<String, CachedStsPolicy> sts_cache_;
   };
}
