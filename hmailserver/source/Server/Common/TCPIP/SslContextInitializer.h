// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class SSLCertificate;

   class SslContextInitializer
   {
   public:

      static bool InitServer(boost::asio::ssl::context& context, std::shared_ptr<SSLCertificate> certificate, String ip_address, int port);
      static bool InitClient(boost::asio::ssl::context& context);


   private:

      static void SetContextOptions_(boost::asio::ssl::context& context);
 
      static std::string  GetPassword_();

      static void SetCipherList_(boost::asio::ssl::context& context);
      static void SetTls13CipherSuites_(boost::asio::ssl::context& context);

      // Sets the TLS key-exchange group list. Named "groups" rather than "curves"
      // because the list now carries hybrid post-quantum KEMs (X25519MLKEM768 and
      // friends) alongside the classical elliptic curves.
      static void SetKeyExchangeGroups_(boost::asio::ssl::context& context);

      // False if the given OpenSSL group list would end up enabling no group at
      // all, which OpenSSL accepts but which makes TLS 1.3 unusable.
      static bool ContainsGroupToAdd_(const AnsiString& groupList);

      // Reports an expired or not-yet-valid certificate on a context that has just
      // loaded one. OpenSSL checks the validity period of certificates it verifies
      // and not of the one it is told to serve, so without this an expired
      // certificate is served silently and fails every handshake at the client.
      // Reports; does not fail the listener - see the call site.
      static void ReportCertificateValidityProblem_(boost::asio::ssl::context& context, const String &certificate_file, const String &ip_address, int port);

      // First error on OpenSSL's per-thread error queue, as text. Empties the
      // queue so our failure is not later reported against an unrelated call.
      static AnsiString GetOpenSslError_();
      
   };
}