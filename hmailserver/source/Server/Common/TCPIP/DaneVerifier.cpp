// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// DANE-EE (RFC 7672) certificate verification for outbound SMTP connections.

#include "StdAfx.h"

#include "DaneVerifier.h"

#include <openssl/x509.h>
#include <openssl/evp.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DaneVerifier::DaneVerifier(int session_id, const std::vector<TlsaRecord> &records) :
      session_id_(session_id),
      records_(records)
   {

   }

   bool
   DaneVerifier::operator() (bool preverified, boost::asio::ssl::verify_context& ctx) const
   {
      int depth = X509_STORE_CTX_get_error_depth(ctx.native_handle());

      if (depth > 0)
      {
         // For DANE-EE, only the end-entity certificate matters. Issuer
         // certificates are not required to validate (RFC 7672, 3.1.1).
         return true;
      }

      X509* certificate = X509_STORE_CTX_get_current_cert(ctx.native_handle());
      if (certificate == nullptr)
      {
         String message;
         message.Format(_T("DANE: Session %d: No end-entity certificate presented by remote server."), session_id_);
         LOG_TCPIP(message);
         return false;
      }

      for (const TlsaRecord &record : records_)
      {
         if (record.usage != 3)
            continue;

         if (MatchesRecord_(certificate, record))
         {
            String message;
            message.Format(_T("DANE: Session %d: Certificate matched DANE-EE TLSA record (selector %d, matching type %d)."), session_id_, record.selector, record.matching_type);
            LOG_TCPIP(message);
            return true;
         }
      }

      String message;
      message.Format(_T("DANE: Session %d: Certificate did not match any DANE-EE TLSA record. Failing TLS handshake."), session_id_);
      LOG_TCPIP(message);
      return false;
   }

   bool
   DaneVerifier::MatchesRecord_(X509 *certificate, const TlsaRecord &record) const
   {
      std::vector<unsigned char> der;

      switch (record.selector)
      {
      case 0:
         if (!GetCertificateDer_(certificate, der))
            return false;
         break;
      case 1:
         if (!GetSubjectPublicKeyInfoDer_(certificate, der))
            return false;
         break;
      default:
         return false;
      }

      return MatchData_(der, record.matching_type, record.data);
   }

   bool
   DaneVerifier::GetCertificateDer_(X509 *certificate, std::vector<unsigned char> &output)
   {
      int length = i2d_X509(certificate, nullptr);
      if (length <= 0)
         return false;

      output.resize(static_cast<size_t>(length));
      unsigned char *buffer = output.data();
      if (i2d_X509(certificate, &buffer) != length)
         return false;

      return true;
   }

   bool
   DaneVerifier::GetSubjectPublicKeyInfoDer_(X509 *certificate, std::vector<unsigned char> &output)
   {
      const X509_PUBKEY *public_key = X509_get_X509_PUBKEY(certificate);
      if (public_key == nullptr)
         return false;

      int length = i2d_X509_PUBKEY(const_cast<X509_PUBKEY*>(public_key), nullptr);
      if (length <= 0)
         return false;

      output.resize(static_cast<size_t>(length));
      unsigned char *buffer = output.data();
      if (i2d_X509_PUBKEY(const_cast<X509_PUBKEY*>(public_key), &buffer) != length)
         return false;

      return true;
   }

   bool
   DaneVerifier::MatchData_(const std::vector<unsigned char> &candidate, int matching_type, const std::string &expected)
   {
      switch (matching_type)
      {
      case 0:
         {
            // Exact match of the full DER data.
            if (expected.size() != candidate.size())
               return false;

            return memcmp(expected.data(), candidate.data(), candidate.size()) == 0;
         }
      case 1:
      case 2:
         {
            const EVP_MD *digest_algorithm = matching_type == 1 ? EVP_sha256() : EVP_sha512();

            unsigned char digest[EVP_MAX_MD_SIZE];
            unsigned int digest_length = 0;

            if (EVP_Digest(candidate.data(), candidate.size(), digest, &digest_length, digest_algorithm, nullptr) != 1)
               return false;

            if (expected.size() != digest_length)
               return false;

            return memcmp(expected.data(), digest, digest_length) == 0;
         }
      default:
         return false;
      }
   }
}
