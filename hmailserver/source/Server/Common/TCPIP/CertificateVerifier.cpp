// Copyright (c) 2014 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "CertificateVerifier.h"
#include "SocketConstants.h"

#include <openssl/x509.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   CertificateVerifier::CertificateVerifier(int session_id, ConnectionSecurity connection_security, const String &host_name) :
      session_id_(session_id),
      connection_security_(connection_security),
      host_name_(host_name)
   {

   }

   bool 
   CertificateVerifier::VerifyCertificate_( PCCERT_CONTEXT certificate, LPWSTR server_name,int &windows_error_code) const
   {
      windows_error_code = 0;

      LPSTR usage_identifier[] = { szOID_PKIX_KP_SERVER_AUTH, szOID_SERVER_GATED_CRYPTO, szOID_SGC_NETSCAPE };

      CERT_CHAIN_PARA params = { sizeof( params ) };
      params.RequestedUsage.dwType = USAGE_MATCH_TYPE_OR;
      params.RequestedUsage.Usage.cUsageIdentifier = _countof( usage_identifier );
      params.RequestedUsage.Usage.rgpszUsageIdentifier = usage_identifier;

      PCCERT_CHAIN_CONTEXT chain_context = 0;

      if (!CertGetCertificateChain(NULL, 
                                   certificate,  
                                   NULL,
                                   NULL,
                                   &params,
                                   CERT_CHAIN_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT,
                                   NULL,
                                   &chain_context))
      {
         windows_error_code = GetLastError();
         return false;
      }

      SSL_EXTRA_CERT_CHAIN_POLICY_PARA sslPolicy = { sizeof( sslPolicy ) };
      sslPolicy.dwAuthType = AUTHTYPE_SERVER;
      sslPolicy.pwszServerName = server_name;

      CERT_CHAIN_POLICY_PARA policy = { sizeof( policy ) };
      policy.pvExtraPolicyPara = &sslPolicy;

      CERT_CHAIN_POLICY_STATUS status = { sizeof( status ) };

      BOOL policy_checked = CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL,
                                                             chain_context,
                                                             &policy,
                                                             &status );

      CertFreeCertificateChain( chain_context );

      windows_error_code = status.dwError;
      bool certificate_ok = policy_checked && status.dwError == 0;
      return certificate_ok;
   }

   bool CertificateVerifier::operator() (bool preverified, boost::asio::ssl::verify_context& ctx) const
   {
      // We're only interested in checking the certificate at the end of the chain.
      int depth = X509_STORE_CTX_get_error_depth(ctx.native_handle());
      if (depth > 0)
         return OverrideResult_(true);

      // Read the cert and convert it to a raw DER-format which we can hand off to Windows.
      X509* cert = X509_STORE_CTX_get_current_cert(ctx.native_handle());
      BIO* bio = BIO_new(BIO_s_mem());

      // Convert the certificate from the internal structure to a DER structure in memory ('bio').
      if (i2d_X509_bio(bio,cert) != 1) 
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5512, "CertificateVerifier::operator()", "Failed to convert OpenSSL internal X509 to DER-format.");
         BIO_free(bio);
         return OverrideResult_(false);
      }

      // Read the cert from the BIO structure in memory to a char array.
      int raw_size = BIO_pending(bio);
      unsigned char *raw_certificate = new unsigned char[raw_size];

      int actual_read = BIO_read(bio, raw_certificate, raw_size);

      if (raw_size != actual_read) 
      {
         String errorMessage = Formatter::Format(_T("BIO_read returned an unexpected number of characters. Expected: {0}, Returned: {1}"), raw_size, actual_read);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5513, "CertificateVerifier::operator()", errorMessage);
         BIO_free(bio);
         delete[] raw_certificate;
         return OverrideResult_(false);
      }
      
      // Create a Windows certificate context, using the raw DER data.
      PCCERT_CONTEXT context = CertCreateCertificateContext(X509_ASN_ENCODING, (BYTE*) raw_certificate, raw_size);

      if (context == NULL) 
      {
         String errorMessage = Formatter::Format(_T("Call to CertCreateCertificateContext failed. Error: {0}"), (int) GetLastError());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5513, "CertificateVerifier::operator()", errorMessage);
         BIO_free(bio);
         delete[] raw_certificate;
         return OverrideResult_(false);
      }

      String expected_host_name = host_name_;

      int windows_error_code = 0;
      if (VerifyCertificate_(context, expected_host_name.GetBuffer(-1), windows_error_code))
      {
         LOG_DEBUG(Formatter::Format("Certificate verification succeeded for session {0}.", session_id_));
         
         BIO_free(bio);
         delete[] raw_certificate;
         CertFreeCertificateContext(context);

         return OverrideResult_(true);
      }
      else
      {
         String windows_error_text = ErrorManager::Instance()->GetWindowsErrorText(windows_error_code);
         String formattedDebugMessage = Formatter::Format("Certificate verification failed for session {0}. Expected host: {1}, Windows error code: {2}, Windows error message: {3}", 
            session_id_, host_name_, windows_error_code, windows_error_text);

         LOG_DEBUG(formattedDebugMessage);

         BIO_free(bio);
         delete[] raw_certificate;
         CertFreeCertificateContext(context);

         return OverrideResult_(false);
      }

   }

   bool
   CertificateVerifier::OverrideResult_(bool result) const
   {
      if (result == false)
      {
         if (connection_security_ == CSSTARTTLSOptional)
            return true;
         else
            return false;
      }

      return true;
   }

   ClientCertificateVerifier::ClientCertificateVerifier(int session_id, bool fail_handshake_on_error) :
      session_id_(session_id),
      fail_handshake_on_error_(fail_handshake_on_error)
   {

   }

   bool
   ClientCertificateVerifier::operator() (bool preverified, boost::asio::ssl::verify_context& ctx) const
   {
      // OpenSSL has already checked this chain element against the CA bundle the
      // port was configured with (TCPServer loads it into the SSL context), so a
      // true preverification is the whole answer. There is deliberately no
      // Windows-store fallback here: the point of a per-port CA bundle is that
      // ONLY the named CA vouches for clients on this port, and consulting the
      // machine's general trust store would let a certificate from any public CA
      // through a port meant for one private one.
      if (preverified)
         return true;

      // Name the failing certificate and the reason. Without this line the
      // administrator of a "require" port sees only a generic handshake failure
      // and cannot tell a partner with an expired certificate from a stranger.
      int depth = X509_STORE_CTX_get_error_depth(ctx.native_handle());
      int verify_error = X509_STORE_CTX_get_error(ctx.native_handle());

      char subject[256] = "(no certificate)";
      X509* cert = X509_STORE_CTX_get_current_cert(ctx.native_handle());
      if (cert != nullptr)
         X509_NAME_oneline(X509_get_subject_name(cert), subject, sizeof(subject));

      String message;
      message.Format(_T("Client certificate verification failed for session %d. Subject: %s, Depth: %d, Error: %s"),
         session_id_, String(subject).c_str(), depth, String(X509_verify_cert_error_string(verify_error)).c_str());
      LOG_TCPIP(message);

      // CCPRequire fails the handshake; CCPRequest records the failure above and
      // lets the session continue - TCPConnection::AsyncHandshakeCompleted reads
      // the surviving error out of SSL_get_verify_result and logs the summary, so
      // an overridden failure is never mistaken for a verified identity.
      return !fail_handshake_on_error_;
   }

}