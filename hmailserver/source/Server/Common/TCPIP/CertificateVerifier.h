// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21

#pragma once

namespace HM
{
   enum ConnectionSecurity;

   class CertificateVerifier
   {
   public:

      /// The type of the function object's result.
      typedef bool result_type;

      /// Constructor.
      CertificateVerifier(int session_id, ConnectionSecurity connection_security,  const String &host_name);

      /// Perform certificate verification.
      bool operator()(bool preverified, boost::asio::ssl::verify_context& ctx) const;

   private:

      bool VerifyCertificate_( PCCERT_CONTEXT certificate, LPWSTR serverName, int &windows_error_code) const;

      bool OverrideResult_(bool result) const;

      // The host name to be checked.
      ConnectionSecurity connection_security_;
      String host_name_;
      int session_id_;
   };

   // Verifies the certificate an INBOUND session presents when a port's
   // ClientCertificatePolicy asks for one.
   //
   // A separate class rather than a mode on CertificateVerifier, because every
   // assumption CertificateVerifier makes is a server-certificate assumption:
   // it builds the chain with Windows' AUTHTYPE_SERVER policy, it matches an
   // expected host name (a client certificate has none), and OverrideResult_
   // deliberately forgives any failure on CSSTARTTLSOptional connections -
   // correct for opportunistic OUTBOUND TLS (RFC 7435), and precisely the
   // forgiveness that must never apply when an administrator has said a port
   // REQUIRES a trusted client certificate. Keeping the inbound path in its own
   // class means no future change to either can quietly weaken the other.
   //
   // The chain itself is validated by OpenSSL's built-in verification against
   // the CA bundle TCPServer loaded into the port's SSL context - that is what
   // 'preverified' reports - so this callback only has to decide what a failure
   // means for the session, and to name the certificate in the log so the
   // administrator can tell WHICH client was refused.
   class ClientCertificateVerifier
   {
   public:

      /// The type of the function object's result.
      typedef bool result_type;

      // fail_handshake_on_error: true for CCPRequire (an unverifiable
      // certificate ends the handshake), false for CCPRequest (the outcome is
      // logged and the session continues; that mode exists for inventorying
      // clients before enforcement is switched on).
      ClientCertificateVerifier(int session_id, bool fail_handshake_on_error);

      /// Perform certificate verification.
      bool operator()(bool preverified, boost::asio::ssl::verify_context& ctx) const;

   private:

      int session_id_;
      bool fail_handshake_on_error_;
   };
}