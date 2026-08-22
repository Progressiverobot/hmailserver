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
//
// RFC 7672 section 2.2 requires more than a validated TLSA RRset: the
// MX RRset that produced the host name must be DNSSEC-validated too.
// A TLSA record found under a host name learned from a forged MX
// response proves nothing, because the attacker chose the host - its
// own TLSA record then validates perfectly and DANE reports success.
// GetTlsaRecords therefore takes the MX RRset's validation status as
// an input (MxDnssecStatus); EvaluateMxDnssecStatus derives it, and
// also checks that the host about to be contacted is one of the names
// the validated MX RRset actually published.
//
// This is a mail server, so the section 2.2 check is built to fail
// open. Exactly one MxDnssecStatus value can stop a delivery (Bogus),
// it is the value furthest from zero, and every other value - including
// anything a caller produces by accident - lands on the ordinary
// delivery path. See the invariant on MxDnssecStatus below.
//
// PLUMBED 13 August 2026. DnssecResolver::QueryMx supplies the validated
// MX RRset, ExternalDelivery looks it up once per recipient domain and
// EvaluateMxDnssecStatus derives the per-host status from it.
//
// One deliberate narrowing, because it decides what happens to real mail:
// QueryMx parses the exchange name out of the rdata and REFUSES a
// compression pointer rather than following one, since it does not hold
// the surrounding packet. That is not merely conservative - an MX RRset
// can only have validated if the canonical, uncompressed form of its
// rdata is what was signed (RFC 4034 section 6.2) - but where a resolver
// does hand back compressed rdata, the effect is a name this code will
// not parse, hence no validated MX host, hence Insecure, hence delivery
// exactly as it was before. It cannot fail closed.
//
// Setting DnssecValidationEnabled=0 reverts to opportunistic
// (unvalidated) TLSA usage.
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Common/TCPIP/DaneVerifier.h"
#include "../Common/TCPIP/DnssecResolver.h"

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

      // The DNSSEC status of the MX RRset that named the host now being
      // considered for delivery (RFC 7672 section 2.2).
      //
      // The values are assigned deliberately and must not be reordered.
      // Zero is the permissive value, because zero is what a caller
      // produces by accident: a default- or zero-initialized variable, a
      // value-initialized struct member, a memset() buffer. The one value
      // that can stop a delivery is the highest, so no accident and no
      // off-by-one lands on it. TlsPolicy.cpp static_asserts this.
      //
      // The numbering deliberately does NOT line up with
      // DnssecResolver::ChainStatus (Secure=0, Insecure=1, Bogus=2), so a
      // caller that takes the shortcut of static_cast-ing a ChainStatus
      // into this type cannot produce Bogus: 0 becomes Unknown, 1 becomes
      // Insecure, 2 becomes Secure. All three are fail-open.
      // EvaluateMxDnssecStatus is the only sanctioned conversion.
      enum class MxDnssecStatus
      {
         Unknown = 0,   // the caller did not determine the MX status, so the
                        // section 2.2 check cannot be performed. Preserves the
                        // earlier behaviour in which the TLSA lookup alone
                        // decides. This is what a caller that has not been
                        // taught to do a validated MX lookup gets, and it is
                        // what every caller gets today.
         Insecure = 1,  // the MX RRset is not covered by DNSSEC at all - which
                        // is true of most of the internet. The destination is
                        // not DANE-capable; delivery proceeds exactly as it
                        // would with no TLSA records published. Never a
                        // deferral and never a bounce.
         Secure = 2,    // the MX RRset was DNSSEC-validated and named this host,
                        // so DANE genuinely applies to it
         Bogus = 3      // DNSSEC is published for the MX RRset and failed to
                        // validate: the host names are not trustworthy. Handled
                        // like a bogus TLSA lookup - the host is not used, which
                        // defers the delivery if no host remains. The only
                        // value here that can hold up mail, and reachable only
                        // when a caller passes it explicitly.
      };

      // Derives the value above for one candidate MX host from the result of
      // a DNSSEC-validated MX lookup for the recipient domain.
      // validated_mx_hosts are the host names that lookup returned, and are
      // only consulted when mx_chain_status is Secure: a validated MX RRset
      // makes this particular host DANE-capable only if the RRset is where
      // the host name came from.
      static MxDnssecStatus EvaluateMxDnssecStatus(const String &host_name,
                                                   DnssecResolver::ChainStatus mx_chain_status,
                                                   const std::vector<String> &validated_mx_hosts);

      // Returns DANE-EE (usage 3) TLSA records published for the given
      // MX host and port. An empty result means no usable records exist;
      // status describes the DNSSEC validation outcome. mx_status carries
      // the RFC 7672 section 2.2 status of the MX RRset that named the host;
      // it is trailing and defaulted so that a caller which cannot yet
      // supply it keeps the previous behaviour rather than losing DANE.
      // The default is both the permissive value and the zero value of the
      // enum, so forgetting the argument, zero-initializing it or casting
      // the wrong enum into it all give the same fail-open result.
      static std::vector<TlsaRecord> GetTlsaRecords(const String &host_name, int port, TlsaLookupStatus &status,
                                                    MxDnssecStatus mx_status = MxDnssecStatus::Unknown);

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
      static bool MxRrsetContainsHost_(const String &host_name, const std::vector<String> &mx_host_names);

      static boost::recursive_mutex sts_cache_mutex_;
      static std::map<String, CachedStsPolicy> sts_cache_;
   };
}
