// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Validating DNSSEC stub resolver (RFC 4033-4035) used for DANE TLSA
// lookups (RFC 7672).
//
// The Windows system resolver is a non-validating stub and does not
// expose a trustworthy AD bit, so this class performs full chain-of-
// trust validation itself: it fetches TLSA/DNSKEY/DS RRsets together
// with their RRSIG records (DO bit set) and verifies every signature
// from the queried name up to the built-in IANA root trust anchors.
//
// Supported algorithms: RSA/SHA-256 (8), RSA/SHA-512 (10),
// ECDSA P-256/SHA-256 (13), ECDSA P-384/SHA-384 (14), Ed25519 (15).
// Supported DS digests: SHA-256 (2), SHA-384 (4).
//
// A delegation without a DS RRset is Insecure only when the parent proves
// the absence - an NSEC or NSEC3 (SHA-1, Opt-Out understood) in the
// authority section, signed by the parent; without the proof, or with one
// that does not verify, it is Bogus (RFC 4035 section 5.2, RFC 5155
// section 8). A stripped DS is therefore a failure, not a downgrade.
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "DaneVerifier.h"
#include "DNSRecord.h"

namespace HM
{
   class DnssecResolver
   {
   public:
      enum class ChainStatus
      {
         Secure,     // RRset validated up to the root trust anchor
         Insecure,   // an unsigned delegation was proven or no signatures
                     // are available - proceed as if no records exist
         Bogus       // validation failed - the records MUST NOT be used
                     // and the host should not be contacted (RFC 7672)
      };

      // Looks up and validates TLSA records for _<port>._tcp.<host>.
      // records is only populated when the result is Secure.
      ChainStatus QueryTlsa(const String &host_name, int port, std::vector<TlsaRecord> &records);

      // Looks up and validates the MX RRset for a domain. exchanges is populated
      // only for a Secure result - a host name learned from an unvalidated answer
      // is precisely what must not be trusted. Required by RFC 7672 section 2.2
      // before DANE may be applied to any host in that RRset.
      ChainStatus QueryMx(const String &domain, std::vector<AnsiString> &exchanges);

      // Looks up TXT records with DNSSEC validation. Unlike TLSA, the
      // records are returned for both Secure and Insecure results (an
      // unsigned zone is normal for TXT consumers such as SPF/DKIM/DMARC);
      // only a Bogus result withholds the data.
      ChainStatus QueryTxt(const String &name, std::vector<AnsiString> &texts);

      // A PLAIN, NON-VALIDATING LOOKUP, for the platform that has no Windows DNS
      // client.
      //
      // This class already carries a complete DNS implementation - the wire
      // encoder, a UDP transport with TCP fallback on truncation, /etc/resolv.conf
      // and the record parser - because validating a chain needs all of it. The
      // POSIX build has no DnsQueryEx to fall back on, so DNSResolverWinApi's
      // POSIX arm calls this instead of reimplementing the same thing badly
      // beside it. It answers exactly what the Windows client answers, in the
      // same DNSRecord shape and with the same status numbers, and it does NO
      // validation: everything that wants a validated answer already calls
      // QueryMx, QueryTxt or QueryTlsa above.
      //
      // status carries the resolver's verdict in the numbers DNSResolver tests
      // for: 0 on success, 9003 when the name does not exist, 9501 when the name
      // exists with no record of that type, and 9002 when no server answered.
      // The distinction matters: 9003 is a permanent failure that bounces mail
      // and 9002 is a temporary one that must not.
      static bool QueryRecords(const AnsiString &name, unsigned short query_type,
                               std::vector<DNSRecord> &records, int &status);

   private:

      // Queries a single name/type, following validated CNAME links, and
      // classifies the response. rdatas is populated for Secure and
      // Insecure results; it is left empty for Bogus or empty answers.
      ChainStatus QueryValidatedRrset_(const AnsiString &query_name, unsigned short query_type,
                                       std::vector<std::vector<unsigned char>> &rdatas);
   };

   // Returns true if DNSSEC validation is enabled and the TXT records for
   // the given name have a bogus (forged) chain of trust. Used by the
   // vendored SPF resolver, which cannot consume DnssecResolver directly.
   bool DnssecTxtLookupIsBogus(const char *name);
}
