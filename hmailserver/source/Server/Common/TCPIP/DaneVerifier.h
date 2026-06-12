// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// DANE-EE (RFC 7672) certificate verification for outbound SMTP connections.

#pragma once

#include <string>
#include <vector>

namespace HM
{
   // One TLSA record (RFC 6698). The data member contains the raw
   // certificate association data from the record.
   struct TlsaRecord
   {
      int usage = 0;          // 0=PKIX-TA, 1=PKIX-EE, 2=DANE-TA, 3=DANE-EE
      int selector = 0;       // 0=full certificate, 1=SubjectPublicKeyInfo
      int matching_type = 0;  // 0=exact, 1=SHA-256, 2=SHA-512
      std::string data;
   };

   // Verify callback used during the TLS handshake when DANE-EE TLSA
   // records exist for the remote host. Per RFC 7672 section 3.1.1,
   // DANE-EE(3) matches are authenticated without PKIX chain validation
   // or name checks - the TLSA record itself is the trust anchor.
   class DaneVerifier
   {
   public:
      DaneVerifier(int session_id, const std::vector<TlsaRecord> &records);

      bool operator() (bool preverified, boost::asio::ssl::verify_context& ctx) const;

   private:

      bool MatchesRecord_(X509 *certificate, const TlsaRecord &record) const;
      static bool GetCertificateDer_(X509 *certificate, std::vector<unsigned char> &output);
      static bool GetSubjectPublicKeyInfoDer_(X509 *certificate, std::vector<unsigned char> &output);
      static bool MatchData_(const std::vector<unsigned char> &candidate, int matching_type, const std::string &expected);

      int session_id_;
      std::vector<TlsaRecord> records_;
   };
}
