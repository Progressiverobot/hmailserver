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

#pragma once

#include "DaneVerifier.h"

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

      // Looks up TXT records with DNSSEC validation. Unlike TLSA, the
      // records are returned for both Secure and Insecure results (an
      // unsigned zone is normal for TXT consumers such as SPF/DKIM/DMARC);
      // only a Bogus result withholds the data.
      ChainStatus QueryTxt(const String &name, std::vector<AnsiString> &texts);

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
