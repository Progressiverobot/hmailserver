// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Public web services for mail domains:
//
//   - MTA-STS policy hosting (RFC 8461): serves
//     https://mta-sts.<domain>/.well-known/mta-sts.txt for every local
//     domain, with the mx list derived from the domain's live MX records
//     (overridable with MtaStsPolicyMx).
//   - Client autoconfiguration: Thunderbird-style autoconfig
//     (/mail/config-v1.1.xml) and Outlook autodiscover
//     (/autodiscover/autodiscover.xml), generated from the actual
//     TCP/IP port configuration.
//   - ACME http-01 challenges (/.well-known/acme-challenge/<token>),
//     served from AcmeChallengeStore so certificate issuance works
//     while this server owns port 80.
//   - security.txt (RFC 9116): serves /.well-known/security.txt for a
//     local domain that has a postmaster address configured. Generated,
//     so the mandatory Expires field is always in the future.
//
// Disabled by default. Enable with WebServicesHttpPort / WebServicesHttpsPort
// in hMailServer.ini. The HTTPS listener uses WebServicesCertificateFile /
// WebServicesPrivateKeyFile, falling back to the ACME certificate.
//
// Several of the features above are enabled by default while the listener
// is not, so ReportUnreachableFeatures() exists to say so at startup.

#pragma once

#include <thread>

namespace HM
{
   class WebServicesServer
   {
   public:
      WebServicesServer();
      ~WebServicesServer();

      bool Start(const String &bind_address, int http_port, int https_port,
                 const String &certificate_file, const String &private_key_file);
      void Stop();

      // True if a running instance serves plain HTTP on the given port.
      // Used by the ACME client to decide whether a transient challenge
      // listener is needed.
      static bool IsListeningOnPort(int port);

      // Reports every feature that is switched on but cannot be reached
      // because no web-services listener is configured. Call this once at
      // startup, unconditionally - including when both ports are 0, which
      // is exactly the case that needs reporting. Start() calls it too, so
      // a partial configuration (HTTP only, with MTA-STS enabled) is also
      // reported. Repeat calls are ignored.
      static void ReportUnreachableFeatures();

   private:

      struct ProtocolEndpoint
      {
         int port = 0;
         AnsiString socket_type; // "SSL", "STARTTLS" or "plain"
      };

      bool StartListener_(const String &bind_address, int port, SOCKET &listen_socket);
      void Run_(SOCKET listen_socket, bool use_tls);
      void HandleClient_(SOCKET client_socket, bool use_tls);

      static AnsiString ProcessRequest_(const AnsiString &request);

      static AnsiString BuildResponse_(int status_code, const AnsiString &content_type, const AnsiString &body);
      static AnsiString GetRequestHost_(const AnsiString &request);
      static AnsiString GetRequestBody_(const AnsiString &request);

      static AnsiString HandleAcmeChallenge_(const AnsiString &path);
      static AnsiString HandleMtaStsPolicy_(const AnsiString &host);
      static AnsiString HandleAutoconfig_(const AnsiString &host, const AnsiString &query);
      static AnsiString HandleAutodiscover_(const AnsiString &body);
      static AnsiString HandleSecurityTxt_(const AnsiString &host);

      // Resolves the RFC 9116 Contact address for the requested host from
      // the postmaster address of the matching local domain. False when
      // there is nothing publishable, in which case no file is served.
      static bool GetSecurityContact_(const AnsiString &host, AnsiString &contact);

      static bool GetClientAccessSettings_(AnsiString &client_host,
         ProtocolEndpoint &imap, ProtocolEndpoint &pop3, ProtocolEndpoint &smtp);
      static bool GetPolicyMxHosts_(const String &domain, std::vector<AnsiString> &mx_hosts);
      static AnsiString XmlEscape_(const AnsiString &value);

      SOCKET http_socket_;
      SOCKET https_socket_;
      std::thread http_worker_;
      std::thread https_worker_;
      bool running_;
      bool tls_available_;
   };
}
