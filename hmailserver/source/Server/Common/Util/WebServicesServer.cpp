// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Public web services: MTA-STS policy hosting, client autoconfiguration
// and ACME challenge serving. See WebServicesServer.h.

#include "StdAfx.h"

#include "WebServicesServer.h"
#include "AcmeClient.h"
#include "FileUtilities.h"

#include "../BO/Domains.h"
#include "../BO/Domain.h"
#include "../BO/TCPIPPort.h"
#include "../BO/TCPIPPorts.h"
#include "../TCPIP/DNSResolver.h"
#include "../TCPIP/SocketConstants.h"

#include <ws2tcpip.h>

#include <openssl/ssl.h>
#include <openssl/err.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int MaxRequestSize = 64 * 1024;
      const DWORD SocketTimeoutMilliseconds = 10000;
      const int MxCacheSeconds = 3600;

      // How far ahead the mandatory RFC 9116 Expires field is set. Derived
      // from the current time on every request rather than written as a
      // literal, so the published file cannot silently go stale. 90 days is
      // comfortably inside the "less than a year" the RFC recommends.
      const int SecurityTxtExpirySeconds = 90 * 24 * 60 * 60;

      // The human-readable policy the machine-readable file points at.
      const char *SecurityTxtPolicyUrl =
         "https://github.com/Progressiverobot/hmailserver/blob/master/.github/SECURITY.md";

      SSL_CTX *tls_context = nullptr;

      // ReportUnreachableFeatures runs once per process.
      bool unreachable_features_reported = false;

      // The plain-HTTP port a running instance listens on (0 = none).
      // Read by AcmeClient through IsListeningOnPort.
      volatile long http_listen_port = 0;

      // Cache of MX lookups used for MTA-STS policy generation.
      boost::recursive_mutex mx_cache_mutex;
      std::map<AnsiString, std::pair<std::vector<AnsiString>, time_t>> mx_cache;

      // Reads an HTTP request (headers + body according to Content-Length)
      // using the supplied read function.
      template <typename ReadFunction>
      bool ReadHttpRequest(ReadFunction readSome, AnsiString &request)
      {
         std::string data;
         char buffer[4096];

         size_t headerEnd = std::string::npos;

         while (data.size() < MaxRequestSize)
         {
            int bytesRead = readSome(buffer, sizeof(buffer));
            if (bytesRead <= 0)
               break;

            data.append(buffer, bytesRead);

            headerEnd = data.find("\r\n\r\n");
            if (headerEnd != std::string::npos)
            {
               // Determine expected body length.
               size_t contentLength = 0;

               std::string headers = data.substr(0, headerEnd);
               std::transform(headers.begin(), headers.end(), headers.begin(), ::tolower);

               size_t lengthPosition = headers.find("content-length:");
               if (lengthPosition != std::string::npos)
                  contentLength = atoi(headers.c_str() + lengthPosition + 15);

               if (contentLength > MaxRequestSize)
                  return false;

               if (data.size() >= headerEnd + 4 + contentLength)
                  break;
            }
         }

         if (headerEnd == std::string::npos)
            return false;

         request = data.c_str();
         return true;
      }
   }

   WebServicesServer::WebServicesServer() :
      http_socket_(INVALID_SOCKET),
      https_socket_(INVALID_SOCKET),
      running_(false),
      tls_available_(false)
   {

   }

   WebServicesServer::~WebServicesServer()
   {
      Stop();
   }

   bool
   WebServicesServer::IsListeningOnPort(int port)
   {
      return port > 0 && http_listen_port == port;
   }

   void
   WebServicesServer::ReportUnreachableFeatures()
   {
      if (unreachable_features_reported)
         return;

      unreachable_features_reported = true;

      IniFileSettings *settings = IniFileSettings::Instance();

      int httpPort = settings->GetWebServicesHttpPort();
      int httpsPort = settings->GetWebServicesHttpsPort();

      bool mtaStsHosting = settings->GetMtaStsHostingEnabled();
      bool autoconfig = settings->GetAutoconfigEnabled();

      // ACME http-01 is only a soft dependency: AcmeClient starts a
      // transient listener of its own whenever this server does not already
      // own the challenge port, so issuance works either way. Say which
      // listener will be used, so a port conflict during a renewal that
      // happens weeks after setup is not a surprise.
      if (settings->GetAcmeEnabled())
      {
         int acmeHttpPort = settings->GetAcmeHttpPort();

         if (httpPort != acmeHttpPort)
         {
            String message;
            message.Format(_T("WebServices: ACME is enabled, so it will bind a temporary http-01 challenge listener on port %d for the duration of each issuance. Set WebServicesHttpPort to %d if the always-on web services listener should serve the challenges instead."),
               acmeHttpPort, acmeHttpPort);
            LOG_APPLICATION(message);
         }
      }

      if (httpPort <= 0 && httpsPort <= 0)
      {
         // MtaStsHostingEnabled and AutoconfigEnabled both default to 1,
         // while WebServicesHttpPort and WebServicesHttpsPort both default
         // to 0. An administrator who publishes an MTA-STS DNS record and
         // expects this server to host the policy therefore gets silence,
         // with nothing in the log to explain it. The port defaults are
         // deliberately left alone - binding port 80 by default on a box
         // that may be running IIS would be a worse failure - but the
         // combination has to be said out loud.
         AnsiString features;

         if (mtaStsHosting)
            features += "MTA-STS policy hosting (/.well-known/mta-sts.txt)";

         if (autoconfig)
         {
            if (!features.IsEmpty())
               features += ", ";

            features += "client autoconfiguration (/mail/config-v1.1.xml and /autodiscover/autodiscover.xml)";
         }

         if (!features.IsEmpty())
            features += ", ";

         features += "security.txt (/.well-known/security.txt)";

         // Application log, not ErrorManager. This is the *default* shipped
         // configuration - MtaStsHostingEnabled defaults to 1 and both ports
         // default to 0 - so reporting it as an error would put a Medium entry
         // in the ERROR log of every stock install, and alarm administrators who
         // never intended to use these features. The ACME branch above already
         // takes this approach for the same class of information. It stays a
         // single line at startup, where someone diagnosing "why does nothing
         // answer that URL" will find it.
         LOG_APPLICATION(_T("WebServices: these features are enabled but unreachable, because no web services listener is configured: ") + String(features) +
            _T(". Nothing answers those URLs until WebServicesHttpPort and/or WebServicesHttpsPort is set to a non-zero port in hMailServer.ini - both default to 0. MTA-STS policy hosting specifically needs WebServicesHttpsPort, because a policy is only ever fetched over HTTPS."));

         return;
      }

      if (mtaStsHosting && httpsPort <= 0)
      {
         LOG_APPLICATION(_T("WebServices: MTA-STS policy hosting is enabled (MtaStsHostingEnabled) but WebServicesHttpsPort is 0. RFC 8461 section 3.3 requires the policy to be fetched over HTTPS, so the plain-HTTP listener cannot serve it and sending servers will treat the domain as having no policy. Set WebServicesHttpsPort in hMailServer.ini."));
      }
   }

   bool
   WebServicesServer::StartListener_(const String &bind_address, int port, SOCKET &listen_socket)
   {
      AnsiString narrowBindAddress = bind_address == _T("localhost") ? AnsiString("127.0.0.1") : AnsiString(bind_address);

      sockaddr_in address = {};
      address.sin_family = AF_INET;
      address.sin_port = htons(static_cast<unsigned short>(port));

      if (inet_pton(AF_INET, narrowBindAddress.c_str(), &address.sin_addr) != 1)
      {
         LOG_APPLICATION("WebServices: Invalid bind address: " + bind_address);
         return false;
      }

      listen_socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      if (listen_socket == INVALID_SOCKET)
         return false;

      BOOL reuseAddress = TRUE;
      setsockopt(listen_socket, SOL_SOCKET, SO_REUSEADDR, (const char*) &reuseAddress, sizeof(reuseAddress));

      if (bind(listen_socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR ||
          listen(listen_socket, 5) == SOCKET_ERROR)
      {
         String message;
         message.Format(_T("WebServices: Failed to bind to %s:%d. Is the port in use?"), bind_address.c_str(), port);
         LOG_APPLICATION(message);

         closesocket(listen_socket);
         listen_socket = INVALID_SOCKET;
         return false;
      }

      return true;
   }

   bool
   WebServicesServer::Start(const String &bind_address, int http_port, int https_port,
                            const String &certificate_file, const String &private_key_file)
   {
      if (running_)
         return true;

      // Report anything that is switched on but cannot be reached with the
      // configuration as it stands. Done here as well as from startup so a
      // partial configuration - HTTP only, with MTA-STS hosting enabled -
      // is reported too. The call is idempotent.
      ReportUnreachableFeatures();

      if (http_port <= 0 && https_port <= 0)
         return false;

      // Set up TLS for the HTTPS listener.
      if (https_port > 0)
      {
         String certificateFile = certificate_file;
         String privateKeyFile = private_key_file;

         // Fall back to the ACME certificate when none is configured.
         if (certificateFile.IsEmpty() || privateKeyFile.IsEmpty())
         {
            String acmeCertificate = AcmeClient::GetCertificateDirectory() + _T("\\fullchain.pem");
            String acmeKey = AcmeClient::GetCertificateDirectory() + _T("\\privkey.pem");

            if (FileUtilities::Exists(acmeCertificate) && FileUtilities::Exists(acmeKey))
            {
               certificateFile = acmeCertificate;
               privateKeyFile = acmeKey;
            }
         }

         if (!certificateFile.IsEmpty() && !privateKeyFile.IsEmpty())
         {
            tls_context = SSL_CTX_new(TLS_server_method());

            if (tls_context != nullptr)
            {
               SSL_CTX_set_min_proto_version(tls_context, TLS1_2_VERSION);

               AnsiString narrowCertificateFile = certificateFile;
               AnsiString narrowPrivateKeyFile = privateKeyFile;

               if (SSL_CTX_use_certificate_chain_file(tls_context, narrowCertificateFile.c_str()) == 1 &&
                   SSL_CTX_use_PrivateKey_file(tls_context, narrowPrivateKeyFile.c_str(), SSL_FILETYPE_PEM) == 1 &&
                   SSL_CTX_check_private_key(tls_context) == 1)
               {
                  tls_available_ = true;
               }
               else
               {
                  LOG_APPLICATION("WebServices: Failed to load the TLS certificate or private key. The HTTPS listener is disabled.");
                  SSL_CTX_free(tls_context);
                  tls_context = nullptr;
               }
            }
         }
         else
         {
            LOG_APPLICATION("WebServices: No TLS certificate available yet. The HTTPS listener is disabled until a certificate exists (enable ACME or set WebServicesCertificateFile).");
         }
      }

      bool anyListener = false;

      if (http_port > 0 && StartListener_(bind_address, http_port, http_socket_))
      {
         anyListener = true;
      }

      if (tls_available_ && StartListener_(bind_address, https_port, https_socket_))
      {
         anyListener = true;
      }

      if (!anyListener)
      {
         if (tls_context != nullptr)
         {
            SSL_CTX_free(tls_context);
            tls_context = nullptr;
            tls_available_ = false;
         }

         return false;
      }

      running_ = true;

      if (http_socket_ != INVALID_SOCKET)
      {
         http_listen_port = http_port;
         http_worker_ = std::thread(&WebServicesServer::Run_, this, http_socket_, false);
      }

      if (https_socket_ != INVALID_SOCKET)
         https_worker_ = std::thread(&WebServicesServer::Run_, this, https_socket_, true);

      String message;
      message.Format(_T("WebServices: Listening on %s (http port %d, https port %d)."),
         bind_address.c_str(),
         http_socket_ != INVALID_SOCKET ? http_port : 0,
         https_socket_ != INVALID_SOCKET ? https_port : 0);
      LOG_APPLICATION(message);

      return true;
   }

   void
   WebServicesServer::Stop()
   {
      if (!running_)
         return;

      running_ = false;
      http_listen_port = 0;

      if (http_socket_ != INVALID_SOCKET)
      {
         closesocket(http_socket_);
         http_socket_ = INVALID_SOCKET;
      }

      if (https_socket_ != INVALID_SOCKET)
      {
         closesocket(https_socket_);
         https_socket_ = INVALID_SOCKET;
      }

      if (http_worker_.joinable())
         http_worker_.join();

      if (https_worker_.joinable())
         https_worker_.join();

      if (tls_context != nullptr)
      {
         SSL_CTX_free(tls_context);
         tls_context = nullptr;
      }

      tls_available_ = false;
   }

   void
   WebServicesServer::Run_(SOCKET listen_socket, bool use_tls)
   {
      for (;;)
      {
         SOCKET clientSocket = accept(listen_socket, nullptr, nullptr);

         if (clientSocket == INVALID_SOCKET)
         {
            if (!running_)
               return;

            continue;
         }

         try
         {
            HandleClient_(clientSocket, use_tls);
         }
         catch (...)
         {
            closesocket(clientSocket);
         }
      }
   }

   void
   WebServicesServer::HandleClient_(SOCKET client_socket, bool use_tls)
   {
      DWORD timeout = SocketTimeoutMilliseconds;
      setsockopt(client_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(client_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      if (use_tls)
      {
         SSL *tlsSession = SSL_new(tls_context);
         if (tlsSession == nullptr)
         {
            closesocket(client_socket);
            return;
         }

         SSL_set_fd(tlsSession, static_cast<int>(client_socket));

         if (SSL_accept(tlsSession) == 1)
         {
            AnsiString request;

            bool requestOk = ReadHttpRequest(
               [&](char *buffer, int size) { return SSL_read(tlsSession, buffer, size); },
               request);

            AnsiString response = requestOk
               ? ProcessRequest_(request)
               : BuildResponse_(400, "text/plain", "malformed request");

            SSL_write(tlsSession, response.c_str(), response.GetLength());
            SSL_shutdown(tlsSession);
         }

         SSL_free(tlsSession);
         closesocket(client_socket);
         return;
      }

      AnsiString request;

      bool requestOk = ReadHttpRequest(
         [&](char *buffer, int size) { return recv(client_socket, buffer, size, 0); },
         request);

      AnsiString response = requestOk
         ? ProcessRequest_(request)
         : BuildResponse_(400, "text/plain", "malformed request");

      send(client_socket, response.c_str(), response.GetLength(), 0);

      shutdown(client_socket, SD_SEND);
      closesocket(client_socket);
   }

   AnsiString
   WebServicesServer::ProcessRequest_(const AnsiString &request)
   {
      int lineEnd = request.Find("\r\n");
      if (lineEnd < 0)
         return BuildResponse_(400, "text/plain", "malformed request");

      AnsiString requestLine = request.Mid(0, lineEnd);

      std::vector<AnsiString> requestParts = StringParser::SplitString(requestLine, " ");
      if (requestParts.size() < 2)
         return BuildResponse_(400, "text/plain", "malformed request");

      AnsiString method = requestParts[0];
      AnsiString path = requestParts[1];

      AnsiString query;
      int queryPosition = path.Find("?");
      if (queryPosition >= 0)
      {
         query = path.Mid(queryPosition + 1);
         path = path.Mid(0, queryPosition);
      }

      AnsiString host = GetRequestHost_(request);

      try
      {
         // ACME http-01 challenges.
         AnsiString challengePrefix = "/.well-known/acme-challenge/";
         if (method == "GET" && path.StartsWith(challengePrefix))
            return HandleAcmeChallenge_(path.Mid(challengePrefix.GetLength()));

         // MTA-STS policy.
         if (method == "GET" && path == "/.well-known/mta-sts.txt" &&
             IniFileSettings::Instance()->GetMtaStsHostingEnabled())
            return HandleMtaStsPolicy_(host);

         // security.txt (RFC 9116). Section 3 puts the file under
         // /.well-known/; /security.txt is the legacy location that the RFC
         // still allows, and some scanners only look there.
         if (method == "GET" && (path == "/.well-known/security.txt" || path == "/security.txt"))
            return HandleSecurityTxt_(host);

         if (IniFileSettings::Instance()->GetAutoconfigEnabled())
         {
            // Thunderbird-style autoconfig.
            if (method == "GET" &&
                (path == "/mail/config-v1.1.xml" || path == "/.well-known/autoconfig/mail/config-v1.1.xml"))
               return HandleAutoconfig_(host, query);

            // Outlook autodiscover (POX). Outlook POSTs; accept GET too.
            AnsiString lowerPath = path;
            lowerPath.MakeLower();
            if (lowerPath == "/autodiscover/autodiscover.xml")
               return HandleAutodiscover_(GetRequestBody_(request));
         }

         return BuildResponse_(404, "text/plain", "not found");
      }
      catch (...)
      {
         return BuildResponse_(500, "text/plain", "internal error");
      }
   }

   AnsiString
   WebServicesServer::BuildResponse_(int status_code, const AnsiString &content_type, const AnsiString &body)
   {
      AnsiString statusText;
      switch (status_code)
      {
      case 200: statusText = "OK"; break;
      case 400: statusText = "Bad Request"; break;
      case 404: statusText = "Not Found"; break;
      default:  statusText = "Internal Server Error"; status_code = 500; break;
      }

      AnsiString response;
      response.Format("HTTP/1.0 %d %hs\r\nContent-Type: %hs\r\nContent-Length: %d\r\nConnection: close\r\n\r\n",
         status_code, statusText.c_str(), content_type.c_str(), body.GetLength());
      response += body;

      return response;
   }

   AnsiString
   WebServicesServer::GetRequestHost_(const AnsiString &request)
   {
      AnsiString lowerRequest = request;
      lowerRequest.MakeLower();

      int headerPosition = lowerRequest.Find("\r\nhost:");
      if (headerPosition < 0)
         return "";

      int valueStart = headerPosition + 7;
      int lineEnd = lowerRequest.Find("\r\n", valueStart);
      if (lineEnd < 0)
         return "";

      AnsiString host = lowerRequest.Mid(valueStart, lineEnd - valueStart);
      host.Trim();

      // Strip any port suffix.
      int portPosition = host.Find(":");
      if (portPosition >= 0)
         host = host.Mid(0, portPosition);

      return host;
   }

   AnsiString
   WebServicesServer::GetRequestBody_(const AnsiString &request)
   {
      int bodyStart = request.Find("\r\n\r\n");
      if (bodyStart < 0)
         return "";

      return request.Mid(bodyStart + 4);
   }

   AnsiString
   WebServicesServer::HandleAcmeChallenge_(const AnsiString &path)
   {
      AnsiString token = path;

      if (token.IsEmpty() || token.Find("/") >= 0 || token.GetLength() > 256)
         return BuildResponse_(404, "text/plain", "not found");

      AnsiString keyAuthorization;
      if (!AcmeChallengeStore::Get(token, keyAuthorization))
         return BuildResponse_(404, "text/plain", "not found");

      return BuildResponse_(200, "text/plain", keyAuthorization);
   }

   bool
   WebServicesServer::GetPolicyMxHosts_(const String &domain, std::vector<AnsiString> &mx_hosts)
   {
      mx_hosts.clear();

      // Explicit override.
      AnsiString configured = IniFileSettings::Instance()->GetMtaStsPolicyMx();
      if (!configured.IsEmpty())
      {
         for (AnsiString host : StringParser::SplitString(configured, ","))
         {
            host.Trim();
            host.MakeLower();

            if (!host.IsEmpty())
               mx_hosts.push_back(host);
         }

         return !mx_hosts.empty();
      }

      AnsiString cacheKey = AnsiString(domain);
      cacheKey.MakeLower();

      {
         boost::lock_guard<boost::recursive_mutex> guard(mx_cache_mutex);

         auto iterator = mx_cache.find(cacheKey);
         if (iterator != mx_cache.end() && iterator->second.second > time(nullptr))
         {
            mx_hosts = iterator->second.first;
            return !mx_hosts.empty();
         }
      }

      // Derive the mx patterns from the domain's live MX records: the
      // policy then matches what receivers will connect to.
      DNSResolver resolver;
      std::vector<String> mxRecords;

      if (resolver.GetMXRecords(domain, mxRecords))
      {
         for (const String &record : mxRecords)
         {
            AnsiString host = AnsiString(record);
            host.Trim();
            host.MakeLower();

            // Strip a trailing root dot.
            if (host.EndsWith("."))
               host = host.Mid(0, host.GetLength() - 1);

            if (!host.IsEmpty())
               mx_hosts.push_back(host);
         }
      }

      // Fall back to this server's host name.
      if (mx_hosts.empty())
      {
         AnsiString localHost = AnsiString(Configuration::Instance()->GetHostName());
         localHost.Trim();
         localHost.MakeLower();

         if (!localHost.IsEmpty())
            mx_hosts.push_back(localHost);
      }

      {
         boost::lock_guard<boost::recursive_mutex> guard(mx_cache_mutex);

         mx_cache[cacheKey] = std::make_pair(mx_hosts, time(nullptr) + MxCacheSeconds);

         if (mx_cache.size() > 512)
            mx_cache.clear();
      }

      return !mx_hosts.empty();
   }

   AnsiString
   WebServicesServer::HandleMtaStsPolicy_(const AnsiString &host)
   {
      // The policy host must be mta-sts.<domain> (RFC 8461 section 3.3).
      AnsiString prefix = "mta-sts.";
      if (!host.StartsWith(prefix))
         return BuildResponse_(404, "text/plain", "not found");

      String domainName = String(host.Mid(prefix.GetLength()));
      if (domainName.IsEmpty())
         return BuildResponse_(404, "text/plain", "not found");

      // Only serve policies for domains hosted here.
      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain || !domain->GetIsActive())
         return BuildResponse_(404, "text/plain", "not found");

      std::vector<AnsiString> mxHosts;
      if (!GetPolicyMxHosts_(domainName, mxHosts))
         return BuildResponse_(404, "text/plain", "not found");

      AnsiString mode = IniFileSettings::Instance()->GetMtaStsPolicyMode();
      mode.MakeLower();
      if (mode != "enforce" && mode != "testing" && mode != "none")
         mode = "enforce";

      int maxAge = IniFileSettings::Instance()->GetMtaStsPolicyMaxAge();
      if (maxAge < 86400)
         maxAge = 86400;
      if (maxAge > 31557600)
         maxAge = 31557600;

      AnsiString body;
      body += "version: STSv1\r\n";
      body += "mode: " + mode + "\r\n";

      for (const AnsiString &mxHost : mxHosts)
         body += "mx: " + mxHost + "\r\n";

      AnsiString maxAgeLine;
      maxAgeLine.Format("max_age: %d\r\n", maxAge);
      body += maxAgeLine;

      return BuildResponse_(200, "text/plain", body);
   }

   bool
   WebServicesServer::GetSecurityContact_(const AnsiString &host, AnsiString &contact)
   {
      contact = "";

      AnsiString candidate = host;
      candidate.Trim();
      candidate.MakeLower();

      if (candidate.IsEmpty())
         return false;

      Domains domains;
      domains.Refresh();

      // The request can arrive either on the mail domain itself or on one of
      // the service host names for it (mta-sts.<domain>, autoconfig.<domain>,
      // mail.<domain>, www.<domain>). Try the host verbatim first, so a
      // sub-domain that is itself hosted here wins over its parent, then
      // drop one label and try again.
      for (int attempt = 0; attempt < 2; attempt++)
      {
         std::shared_ptr<Domain> domain = domains.GetItemByName(String(candidate));

         if (domain)
         {
            if (!domain->GetIsActive())
               return false;

            AnsiString postmaster = AnsiString(domain->GetPostmaster());
            postmaster.Trim();

            // Publish only something that is actually an address. An RFC 9116
            // file carrying a placeholder is worse than no file at all: it
            // tells a finder they have a reporting channel when they do not.
            if (postmaster.Find("@") > 0 && postmaster.Find(" ") < 0)
            {
               contact = postmaster;
               return true;
            }

            return false;
         }

         int labelEnd = candidate.Find(".");
         if (labelEnd < 0)
            break;

         candidate = candidate.Mid(labelEnd + 1);
      }

      return false;
   }

   AnsiString
   WebServicesServer::HandleSecurityTxt_(const AnsiString &host)
   {
      AnsiString contact;

      if (!GetSecurityContact_(host, contact))
      {
         LOG_DEBUG("WebServices: No security.txt served - the requested domain is not hosted here, or has no postmaster address configured.");
         return BuildResponse_(404, "text/plain", "not found");
      }

      // Expires is mandatory (RFC 9116 section 2.5.5) and must be in the
      // future, so it is computed per request instead of being written as a
      // literal that would quietly expire and invalidate the whole file.
      time_t expires = time(nullptr) + SecurityTxtExpirySeconds;

      struct tm expiresUtc = {};
      if (gmtime_s(&expiresUtc, &expires) != 0)
         return BuildResponse_(500, "text/plain", "internal error");

      char expiresText[32] = {};
      if (strftime(expiresText, sizeof(expiresText), "%Y-%m-%dT%H:%M:%SZ", &expiresUtc) == 0)
         return BuildResponse_(500, "text/plain", "internal error");

      // Canonical is deliberately omitted: the same file is reachable over
      // both listeners and on whatever ports the administrator chose, and a
      // Canonical value that does not match the fetch URL makes the file
      // invalid rather than merely incomplete.
      AnsiString body;
      body += "Contact: mailto:" + contact + "\r\n";
      body += AnsiString("Expires: ") + expiresText + "\r\n";
      body += AnsiString("Policy: ") + SecurityTxtPolicyUrl + "\r\n";
      body += "Preferred-Languages: en\r\n";

      return BuildResponse_(200, "text/plain; charset=utf-8", body);
   }

   bool
   WebServicesServer::GetClientAccessSettings_(AnsiString &client_host,
      ProtocolEndpoint &imap, ProtocolEndpoint &pop3, ProtocolEndpoint &smtp)
   {
      client_host = IniFileSettings::Instance()->GetAutoconfigClientHost();
      client_host.Trim();

      if (client_host.IsEmpty())
         client_host = AnsiString(Configuration::Instance()->GetHostName());

      client_host.Trim();
      client_host.MakeLower();

      if (client_host.IsEmpty())
         return false;

      // Pick the best advertised port per protocol from the actual
      // configuration: implicit TLS first, then STARTTLS, then plain.
      auto rank = [](const std::shared_ptr<TCPIPPort> &port) -> int
      {
         switch (port->GetConnectionSecurity())
         {
         case CSSSL:
            return 100;
         case CSSTARTTLSRequired:
            return port->GetPortNumber() == 587 ? 90 : 80;
         case CSSTARTTLSOptional:
            return port->GetPortNumber() == 587 ? 70 : 60;
         default:
            return 10;
         }
      };

      auto socketType = [](const std::shared_ptr<TCPIPPort> &port) -> AnsiString
      {
         switch (port->GetConnectionSecurity())
         {
         case CSSSL:
            return "SSL";
         case CSSTARTTLSRequired:
         case CSSTARTTLSOptional:
            return "STARTTLS";
         default:
            return "plain";
         }
      };

      int imapRank = -1, pop3Rank = -1, smtpRank = -1;

      std::vector<std::shared_ptr<TCPIPPort>> ports = Configuration::Instance()->GetTCPIPPorts()->GetVector();

      for (std::shared_ptr<TCPIPPort> port : ports)
      {
         if (!port)
            continue;

         int portRank = rank(port);

         switch (port->GetProtocol())
         {
         case STIMAP:
            if (portRank > imapRank)
            {
               imapRank = portRank;
               imap.port = port->GetPortNumber();
               imap.socket_type = socketType(port);
            }
            break;

         case STPOP3:
            if (portRank > pop3Rank)
            {
               pop3Rank = portRank;
               pop3.port = port->GetPortNumber();
               pop3.socket_type = socketType(port);
            }
            break;

         case STSMTP:
            if (portRank > smtpRank)
            {
               smtpRank = portRank;
               smtp.port = port->GetPortNumber();
               smtp.socket_type = socketType(port);
            }
            break;

         default:
            break;
         }
      }

      return imap.port > 0 || pop3.port > 0 || smtp.port > 0;
   }

   AnsiString
   WebServicesServer::HandleAutoconfig_(const AnsiString &host, const AnsiString &query)
   {
      AnsiString clientHost;
      ProtocolEndpoint imap, pop3, smtp;

      if (!GetClientAccessSettings_(clientHost, imap, pop3, smtp))
         return BuildResponse_(404, "text/plain", "not found");

      // Derive the mail domain: autoconfig.<domain> host, the
      // emailaddress query parameter, or the client host as fallback.
      AnsiString domain;

      AnsiString autoconfigPrefix = "autoconfig.";
      if (host.StartsWith(autoconfigPrefix))
         domain = host.Mid(autoconfigPrefix.GetLength());

      if (domain.IsEmpty())
      {
         AnsiString lowerQuery = query;
         lowerQuery.MakeLower();

         int parameterPosition = lowerQuery.Find("emailaddress=");
         if (parameterPosition >= 0)
         {
            AnsiString value = lowerQuery.Mid(parameterPosition + 13);

            int delimiterPosition = value.Find("&");
            if (delimiterPosition >= 0)
               value = value.Mid(0, delimiterPosition);

            int atPosition = value.Find("%40");
            if (atPosition >= 0)
               domain = value.Mid(atPosition + 3);
            else
            {
               atPosition = value.Find("@");
               if (atPosition >= 0)
                  domain = value.Mid(atPosition + 1);
            }
         }
      }

      if (domain.IsEmpty())
         domain = clientHost;

      AnsiString xml;
      xml += "<?xml version=\"1.0\"?>\r\n";
      xml += "<clientConfig version=\"1.1\">\r\n";
      xml += "  <emailProvider id=\"" + XmlEscape_(domain) + "\">\r\n";
      xml += "    <domain>" + XmlEscape_(domain) + "</domain>\r\n";
      xml += "    <displayName>" + XmlEscape_(domain) + "</displayName>\r\n";

      auto appendServer = [&](const AnsiString &kind, const AnsiString &type, const ProtocolEndpoint &endpoint)
      {
         if (endpoint.port <= 0)
            return;

         AnsiString portText;
         portText.Format("%d", endpoint.port);

         xml += "    <" + kind + " type=\"" + type + "\">\r\n";
         xml += "      <hostname>" + XmlEscape_(clientHost) + "</hostname>\r\n";
         xml += "      <port>" + portText + "</port>\r\n";
         xml += "      <socketType>" + endpoint.socket_type + "</socketType>\r\n";
         xml += "      <authentication>password-cleartext</authentication>\r\n";
         xml += "      <username>%EMAILADDRESS%</username>\r\n";
         xml += "    </" + kind + ">\r\n";
      };

      appendServer("incomingServer", "imap", imap);
      appendServer("incomingServer", "pop3", pop3);
      appendServer("outgoingServer", "smtp", smtp);

      xml += "  </emailProvider>\r\n";
      xml += "</clientConfig>\r\n";

      return BuildResponse_(200, "text/xml", xml);
   }

   AnsiString
   WebServicesServer::HandleAutodiscover_(const AnsiString &body)
   {
      AnsiString clientHost;
      ProtocolEndpoint imap, pop3, smtp;

      if (!GetClientAccessSettings_(clientHost, imap, pop3, smtp))
         return BuildResponse_(404, "text/plain", "not found");

      // Extract the email address from the POX request body.
      AnsiString email;

      AnsiString lowerBody = body;
      lowerBody.MakeLower();

      int tagStart = lowerBody.Find("<emailaddress>");
      if (tagStart >= 0)
      {
         int valueStart = tagStart + AnsiString("<emailaddress>").GetLength();
         int tagEnd = lowerBody.Find("</emailaddress>", valueStart);

         if (tagEnd > valueStart && tagEnd - valueStart < 320)
         {
            email = body.Mid(valueStart, tagEnd - valueStart);
            email.Trim();
         }
      }

      auto appendProtocol = [&](AnsiString &xml, const AnsiString &type, const ProtocolEndpoint &endpoint)
      {
         if (endpoint.port <= 0)
            return;

         AnsiString portText;
         portText.Format("%d", endpoint.port);

         bool implicitTls = endpoint.socket_type == "SSL";
         bool startTls = endpoint.socket_type == "STARTTLS";

         xml += "      <Protocol>\r\n";
         xml += "        <Type>" + type + "</Type>\r\n";
         xml += "        <Server>" + XmlEscape_(clientHost) + "</Server>\r\n";
         xml += "        <Port>" + portText + "</Port>\r\n";
         xml += "        <DomainRequired>off</DomainRequired>\r\n";

         if (!email.IsEmpty())
            xml += "        <LoginName>" + XmlEscape_(email) + "</LoginName>\r\n";

         xml += "        <SPA>off</SPA>\r\n";
         xml += AnsiString("        <SSL>") + (implicitTls || startTls ? "on" : "off") + "</SSL>\r\n";

         if (startTls)
            xml += "        <Encryption>TLS</Encryption>\r\n";
         else if (implicitTls)
            xml += "        <Encryption>SSL</Encryption>\r\n";

         xml += "        <AuthRequired>on</AuthRequired>\r\n";
         xml += "      </Protocol>\r\n";
      };

      AnsiString xml;
      xml += "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n";
      xml += "<Autodiscover xmlns=\"http://schemas.microsoft.com/exchange/autodiscover/responseschema/2006\">\r\n";
      xml += "  <Response xmlns=\"http://schemas.microsoft.com/exchange/autodiscover/outlook/responseschema/2006a\">\r\n";
      xml += "    <Account>\r\n";
      xml += "      <AccountType>email</AccountType>\r\n";
      xml += "      <Action>settings</Action>\r\n";

      appendProtocol(xml, "IMAP", imap);
      appendProtocol(xml, "POP3", pop3);
      appendProtocol(xml, "SMTP", smtp);

      xml += "    </Account>\r\n";
      xml += "  </Response>\r\n";
      xml += "</Autodiscover>\r\n";

      return BuildResponse_(200, "text/xml", xml);
   }

   AnsiString
   WebServicesServer::XmlEscape_(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength() + 8);

      for (int i = 0; i < value.GetLength(); i++)
      {
         char character = value[i];

         switch (character)
         {
         case '<':  result += "&lt;"; break;
         case '>':  result += "&gt;"; break;
         case '&':  result += "&amp;"; break;
         case '\"': result += "&quot;"; break;
         case '\'': result += "&apos;"; break;
         default:
            if (static_cast<unsigned char>(character) >= 32)
               result += character;
            break;
         }
      }

      return result;
   }
}
