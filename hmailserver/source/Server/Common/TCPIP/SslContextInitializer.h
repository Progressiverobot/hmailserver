// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

// The protocol floor every boost::asio::ssl::context in this server is built with,
// spelled out as one constant expression on purpose: the analyser that reads
// set_options (CodeQL's boost::asio TLS-settings check) accepts only a
// compile-time constant carrying no_sslv3, no_tlsv1 and no_tlsv1_1, and refuses a
// mask computed at run time. So the floor is constant and complete - SSLv2, SSLv3,
// TLS 1.0 and TLS 1.1 off - and the [Settings] toggles that let an administrator
// re-enable TLS 1.0 or 1.1 for an old mail client act afterwards, natively, in
// SslContextInitializer::SetContextOptions_, and only for the mail listeners and
// the mail client. The HTTPS clients keep the floor as it is.
#define HM_TLS_CONTEXT_FLOOR \
   (boost::asio::ssl::context::default_workarounds | \
    boost::asio::ssl::context::single_dh_use | \
    boost::asio::ssl::context::no_sslv2 | \
    boost::asio::ssl::context::no_sslv3 | \
    boost::asio::ssl::context::no_tlsv1 | \
    boost::asio::ssl::context::no_tlsv1_1)

namespace HM
{
   class SSLCertificate;

   class SslContextInitializer
   {
   public:

      static bool InitServer(boost::asio::ssl::context& context, std::shared_ptr<SSLCertificate> certificate, String ip_address, int port);
      // honour_legacy_toggles: the mail client follows the [Settings] TLS version
      // toggles like the listeners do; an HTTPS client of a web service passes
      // false and keeps TLS 1.2 as its floor whatever the toggles say.
      static bool InitClient(boost::asio::ssl::context& context, bool honour_legacy_toggles = true);


   private:

      static void SetContextOptions_(boost::asio::ssl::context& context, bool honour_legacy_toggles);

      // The PEM passphrase callback body. OpenSSL only invokes it when the private
      // key file is actually encrypted, so an unencrypted key never reaches this
      // and behaves exactly as it always has. Returns the passphrase configured on
      // the certificate, or an empty string - which makes the key load fail and be
      // reported by InitServer - when none is configured.
      static std::string GetPassword_(std::shared_ptr<SSLCertificate> certificate);

      // Session resumption policy for a server context: an explicit session-ID
      // context, cache size and lifetime control, the ability to turn session
      // tickets off, and ticket-key rotation. Server-side only - none of these
      // apply to the outbound client context. Every setting defaults to "make no
      // OpenSSL call at all", so a default configuration keeps OpenSSL's stock
      // behaviour; see the function body for which knob defends which failure.
      static void SetSessionResumption_(boost::asio::ssl::context& context, const String &ip_address, int port);

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