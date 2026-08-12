// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Public web services: MTA-STS policy hosting, client autoconfiguration
// (Thunderbird, Outlook and Apple .mobileconfig), CalDAV/CardDAV discovery
// redirects and ACME challenge serving. See WebServicesServer.h.

#include "StdAfx.h"

#include "WebServicesServer.h"
#include "AcmeClient.h"
#include "FileUtilities.h"

#include "../BO/Domains.h"
#include "../BO/Domain.h"
#include "../BO/TCPIPPort.h"
#include "../BO/TCPIPPorts.h"
#include "../BO/SSLCertificate.h"
#include "../TCPIP/DNSResolver.h"
#include "../TCPIP/SocketConstants.h"
#include "../TCPIP/SslContextInitializer.h"

#include <ws2tcpip.h>

#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/sha.h>

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

      // How long the two RFC 6764 redirect targets are cached for. They are
      // read straight out of hMailServer.ini rather than from IniFileSettings
      // (see ReadSettingsIniString below), and /.well-known/caldav is a public
      // unauthenticated path, so without a cache a client - or anyone else -
      // could make the listener touch the file once per request.
      const int DavRedirectCacheSeconds = 60;

      SSL_CTX *tls_context = nullptr;

      // Owns the context tls_context points into; the boost object frees the SSL_CTX
      // in its destructor, so it has to outlive every session made from it and
      // nothing here may call SSL_CTX_free. Same arrangement as RestApiServer.
      std::shared_ptr<boost::asio::ssl::context> tls_context_owner;

      // The configuration ReportUnreachableFeatures last reported on, empty
      // until the first report. Keyed on the configuration rather than being a
      // plain once-per-process flag, because the function has two independent
      // callers - the unconditional call in Application::StartServers and
      // Start() - and both are correct to make the call: whichever runs first
      // reports, and the other finds nothing new to say. A bare flag would work
      // for that, but would also silence the report for the rest of the process
      // if the servers are stopped and restarted with a port changed, which is
      // exactly the moment an administrator wants to see whether the fix took.
      // Guarded by a mutex rather than being a lone bool, so the answer does not
      // depend on the two callers being on the same thread.
      boost::recursive_mutex unreachable_features_mutex;
      String unreachable_features_reported_for;

      // The plain-HTTP port a running instance listens on (0 = none).
      // Read by AcmeClient through IsListeningOnPort.
      volatile long http_listen_port = 0;

      // Cache of MX lookups used for MTA-STS policy generation.
      boost::recursive_mutex mx_cache_mutex;
      std::map<AnsiString, std::pair<std::vector<AnsiString>, time_t>> mx_cache;

      // Cached CalDAV / CardDAV redirect targets, and a once-per-start flag
      // per setting so a value that cannot be used is reported at most once
      // rather than on every request. Reset by Start(), which is when the rest
      // of the configuration is re-read too.
      boost::recursive_mutex dav_redirect_mutex;
      AnsiString dav_caldav_target;
      AnsiString dav_carddav_target;
      time_t dav_redirect_expires = 0;
      bool dav_caldav_invalid_reported = false;
      bool dav_carddav_invalid_reported = false;

      // Reads a value from the [Settings] section of hMailServer.ini, the same
      // file and section IniFileSettings reads.
      //
      // CalDavRedirectUrl and CardDavRedirectUrl have no IniFileSettings
      // accessor yet. Rather than half-wire a setting through a class this
      // change does not own - which would leave the two files out of step
      // until both landed - the value is read here from the same place
      // IniFileSettings would read it, so there is still exactly one place the
      // setting lives. Folding it into IniFileSettings with a proper accessor
      // is a mechanical follow-up; nothing here changes when that happens
      // except this function being deleted.
      String ReadSettingsIniString(const String &key)
      {
         const DWORD bufferSize = 1024;
         TCHAR value[bufferSize] = {};

         GetPrivateProfileString(_T("Settings"), key, _T(""), value, bufferSize,
            IniFileSettings::GetInitializationFile());

         return value;
      }

      // A Location header value is written into the response verbatim, so a
      // value carrying a CR or LF would let whoever can edit hMailServer.ini
      // split the response and inject headers of their own. Everything about
      // the value is therefore checked before it is used, and a value that
      // fails is treated as not configured rather than being repaired: a
      // guessed-at repair of a discovery URL sends clients somewhere nobody
      // chose.
      bool IsUsableRedirectTarget(const AnsiString &value)
      {
         if (value.IsEmpty() || value.GetLength() > 512)
            return false;

         AnsiString scheme = value;
         scheme.MakeLower();

         if (!scheme.StartsWith("https://") && !scheme.StartsWith("http://"))
            return false;

         // Printable US-ASCII only: excludes CR, LF, NUL, spaces and anything
         // that would have to be percent-encoded to appear in a URL.
         for (int i = 0; i < value.GetLength(); i++)
         {
            unsigned char character = static_cast<unsigned char>(value[i]);

            if (character < 0x21 || character > 0x7e)
               return false;
         }

         return true;
      }

      // A conservative filter for a host name that is about to be written into
      // a configuration profile the user installs at system level. The domain
      // can be derived from the Host header, so it is caller-controlled;
      // XmlEscape_ already makes it safe to embed in the plist, and this keeps
      // it recognisable as a host name as well. Empty on anything unexpected,
      // which the caller treats as "fall back to the configured client host".
      AnsiString SanitizeHostName(const AnsiString &value)
      {
         if (value.IsEmpty() || value.GetLength() > 253)
            return "";

         for (int i = 0; i < value.GetLength(); i++)
         {
            char character = value[i];

            bool allowed = (character >= 'a' && character <= 'z') ||
                           (character >= '0' && character <= '9') ||
                           character == '.' || character == '-';

            if (!allowed)
               return "";
         }

         return value;
      }

      // Whether an address is safe and complete enough to pre-fill into a
      // configuration profile. Anything else and the address keys are left out
      // entirely, which makes the device prompt for them during installation -
      // the correct outcome, rather than installing a profile with a mangled
      // address in it.
      bool IsPlausibleEmailAddress(const AnsiString &value)
      {
         if (value.GetLength() < 3 || value.GetLength() > 320)
            return false;

         int atCount = 0;
         int atPosition = -1;

         for (int i = 0; i < value.GetLength(); i++)
         {
            char character = value[i];

            bool allowed = (character >= 'a' && character <= 'z') ||
                           (character >= '0' && character <= '9') ||
                           character == '.' || character == '-' ||
                           character == '_' || character == '+' ||
                           character == '@';

            if (!allowed)
               return false;

            if (character == '@')
            {
               atCount++;
               atPosition = i;
            }
         }

         return atCount == 1 && atPosition > 0 && atPosition < value.GetLength() - 1;
      }

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
      // The configured ports, for the unconditional startup call. Start()
      // reports on the ports it was actually handed instead, which is the same
      // thing in production and the truth rather than the settings when it is
      // not (a test starts a listener on an explicit port).
      //
      // The settings read is inside the same catch as the rest of the work, so
      // the "cannot throw out" contract in the header holds for this caller too
      // - it is the one that runs on the startup path of every install.
      try
      {
         IniFileSettings *settings = IniFileSettings::Instance();

         ReportUnreachableFeatures_(settings->GetWebServicesHttpPort(),
            settings->GetWebServicesHttpsPort());
      }
      catch (...)
      {
         // Deliberately swallowed: a startup diagnostic must never be the
         // reason the service fails to start.
      }
   }

   void
   WebServicesServer::ReportUnreachableFeatures_(int http_port, int https_port)
   {
      // Everything below reads settings and formats strings. Nothing here
      // touches a socket, a listener, or any member of this class, so the
      // function is correct at process start with no instance in existence and
      // both ports zero - which is the shipped default, and the case it exists
      // for. The whole body is wrapped because a startup diagnostic must never
      // be the reason the service fails to start.
      try
      {
         IniFileSettings *settings = IniFileSettings::Instance();

         bool mtaStsHosting = settings->GetMtaStsHostingEnabled();
         bool autoconfig = settings->GetAutoconfigEnabled();
         bool acmeEnabled = settings->GetAcmeEnabled();
         int acmeHttpPort = settings->GetAcmeHttpPort();

         // The RFC 6764 redirects only exist once a target is configured, so
         // they are only worth naming when one is - and an administrator who
         // has just set CalDavRedirectUrl is exactly the person who needs to be
         // told the listener is not running. GetDavRedirectSetting_ and not
         // GetDavRedirectTarget_ on purpose: listing a feature must not raise a
         // configuration error as a side effect.
         AnsiString davTarget;
         bool davRedirectConfigured = GetDavRedirectSetting_(true, davTarget) ||
                                      GetDavRedirectSetting_(false, davTarget);

         // Every input that can change a word of what is logged below. Compared
         // against the last report so the two callers say it once between them.
         String signature;
         signature.Format(_T("%d|%d|%d|%d|%d|%d|%d"),
            http_port, https_port,
            mtaStsHosting ? 1 : 0, autoconfig ? 1 : 0,
            acmeEnabled ? 1 : 0, acmeHttpPort,
            davRedirectConfigured ? 1 : 0);

         // ACME http-01 is only a soft dependency: AcmeClient starts a
         // transient listener of its own whenever this server does not already
         // own the challenge port, so issuance works either way. Say which
         // listener will be used, so a port conflict during a renewal that
         // happens weeks after setup is not a surprise. Silent unless ACME has
         // been switched on, which it is not by default.
         String acmeMessage;

         if (acmeEnabled && http_port != acmeHttpPort)
         {
            acmeMessage.Format(_T("WebServices: ACME is enabled, so it will bind a temporary http-01 challenge listener on port %d for the duration of each issuance. Set WebServicesHttpPort to %d if the always-on web services listener should serve the challenges instead."),
               acmeHttpPort, acmeHttpPort);
         }

         String featureMessage;

         if (http_port <= 0 && https_port <= 0)
         {
            // No listener at all. MtaStsHostingEnabled and AutoconfigEnabled
            // both default to 1, while WebServicesHttpPort and
            // WebServicesHttpsPort both default to 0. An administrator who
            // publishes an MTA-STS DNS record and expects this server to host
            // the policy therefore gets silence, with nothing in the log to
            // explain it. The port defaults are deliberately left alone -
            // binding port 80 by default on a box that may be running IIS would
            // be a worse failure - but the combination has to be said out loud.
            AnsiString features;

            if (mtaStsHosting)
               features += "MTA-STS policy hosting (/.well-known/mta-sts.txt)";

            if (autoconfig)
            {
               if (!features.IsEmpty())
                  features += ", ";

               features += "client autoconfiguration (/mail/config-v1.1.xml, /autodiscover/autodiscover.xml and /email.mobileconfig)";
            }

            if (!features.IsEmpty())
               features += ", ";

            // security.txt has no enable setting of its own - it is served for
            // any hosted domain that has a real postmaster address - so it is
            // always in the list, and the list is therefore never empty.
            features += "security.txt (/.well-known/security.txt, for hosted domains with a postmaster address)";

            if (davRedirectConfigured)
               features += ", CalDAV/CardDAV service discovery (/.well-known/caldav and /.well-known/carddav)";

            featureMessage = _T("WebServices: these features are enabled but unreachable, because no web services listener is configured: ") + String(features) +
               _T(". Nothing answers those URLs until WebServicesHttpPort and/or WebServicesHttpsPort is set to a non-zero port in hMailServer.ini - both default to 0.");

            // Only when MTA-STS hosting is actually on. Naming a setting that
            // matters to a feature the administrator has switched off sends them
            // to change a port for no reason.
            if (mtaStsHosting)
            {
               featureMessage += _T(" MTA-STS policy hosting needs WebServicesHttpsPort specifically: RFC 8461 section 3.3 has the policy fetched over HTTPS only, so a plain-HTTP listener cannot serve it.");
            }
         }
         else if (mtaStsHosting && https_port <= 0)
         {
            // A listener exists, but not one this feature can use.
            featureMessage = _T("WebServices: MTA-STS policy hosting is enabled (MtaStsHostingEnabled) but WebServicesHttpsPort is 0, so only the plain-HTTP listener is running. RFC 8461 section 3.3 requires the policy to be fetched over HTTPS, so the policy cannot be served and sending servers will treat the domain as having no policy. Set WebServicesHttpsPort in hMailServer.ini. The other web services are unaffected and are being served over HTTP.");
         }

         if (acmeMessage.IsEmpty() && featureMessage.IsEmpty())
            return;

         // Claim the configuration before logging, and log outside the lock.
         {
            boost::lock_guard<boost::recursive_mutex> guard(unreachable_features_mutex);

            if (unreachable_features_reported_for == signature)
               return;

            unreachable_features_reported_for = signature;
         }

         // LOG_APPLICATION, never ErrorManager, for every line above. All of
         // this describes the shipped default configuration - MtaStsHostingEnabled
         // defaults to 1 and both ports default to 0 - so reporting it as an error
         // would put a Medium entry in the ERROR log of every stock install, alarm
         // administrators who never intended to use these features, and fail every
         // fixture that asserts a clean error log. It stays a line at startup,
         // where someone diagnosing "why does nothing answer that URL" will find it.
         if (!acmeMessage.IsEmpty())
            LOG_APPLICATION(acmeMessage);

         if (!featureMessage.IsEmpty())
            LOG_APPLICATION(featureMessage);
      }
      catch (...)
      {
         // Deliberately swallowed: see above.
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

      // Drop the cached RFC 6764 redirect targets, so a start always sees the
      // hMailServer.ini as it is now rather than as it was up to a minute ago.
      // The invalid-value flags go with them: a value that is still wrong is
      // worth one error entry per service start, and suppressing it forever
      // after the first would hide a misconfiguration from an administrator who
      // restarted the service to fix it.
      {
         boost::lock_guard<boost::recursive_mutex> guard(dav_redirect_mutex);

         dav_redirect_expires = 0;
         dav_caldav_invalid_reported = false;
         dav_carddav_invalid_reported = false;
      }

      // Report anything that is switched on but cannot be reached with the
      // configuration as it stands. Application::StartServers makes this call
      // unconditionally as well - it has to, because this function is not
      // reached at all when both ports are 0, which is the default and the case
      // most worth reporting. Repeating it here costs nothing (the second call
      // for one configuration finds nothing new to say) and reports the ports
      // this listener is actually being started with, which need not be the ones
      // in the settings.
      ReportUnreachableFeatures_(http_port, https_port);

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
            // Through SslContextInitializer, for the reasons set out at the same point
            // in RestApiServer::Start: it is the single place the TLS configuration
            // lives - cipher list, protocol versions, option mask, DH parameters and
            // the key-exchange groups that carry the hybrid post-quantum KEMs - and a
            // listener that configures its own context silently misses every one of
            // them. This one set a TLS 1.2 floor and loaded the certificate, and took
            // OpenSSL's defaults for the rest.
            //
            // The certificate here may be the ACME one resolved just above rather than
            // anything bound to a mailbox port, which is why an SSLCertificate is
            // built from the two paths instead of being looked up.
            try
            {
               auto certificate = std::shared_ptr<SSLCertificate>(new SSLCertificate());

               certificate->SetName(_T("WebServices"));
               certificate->SetCertificateFile(certificateFile);
               certificate->SetPrivateKeyFile(privateKeyFile);

               auto context = std::shared_ptr<boost::asio::ssl::context>(
                  new boost::asio::ssl::context(boost::asio::ssl::context::sslv23));

               if (SslContextInitializer::InitServer(*context, certificate, bind_address, https_port))
               {
                  tls_context_owner = context;
                  tls_context = context->native_handle();

                  // Kept from the previous implementation and applied after
                  // InitServer, so it can only tighten: the shared option mask follows
                  // the [Settings] protocol toggles, which may permit TLS 1.0 for a
                  // legacy mail client, and an HTTPS listener should not inherit that.
                  SSL_CTX_set_min_proto_version(tls_context, TLS1_2_VERSION);

                  tls_available_ = true;
               }
               else
               {
                  // InitServer has already reported the specific failure as HM5113.
                  LOG_APPLICATION("WebServices: The shared TLS configuration could not be applied to the certificate. The HTTPS listener is disabled.");
               }
            }
            catch (...)
            {
               LOG_APPLICATION("WebServices: An exception was raised while preparing TLS. The HTTPS listener is disabled.");
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
         // No SSL_CTX_free: the boost context owns it. See tls_context_owner.
         tls_context = nullptr;
         tls_context_owner.reset();
         tls_available_ = false;

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

      // No SSL_CTX_free: the boost context owns it. Released after both joins above,
      // so no session is still using it.
      tls_context = nullptr;
      tls_context_owner.reset();

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

         // CalDAV / CardDAV service discovery (RFC 6764). Deliberately not
         // restricted to GET: section 6 has clients issuing PROPFIND straight
         // at the well-known URI, and a client that gets 404 for its PROPFIND
         // stops looking. Not gated on AutoconfigEnabled either - this points
         // at a different server entirely, and is off unless a target is set.
         if (path == "/.well-known/caldav" || path == "/.well-known/carddav")
            return HandleWellKnownDavRedirect_(path == "/.well-known/caldav");

         if (IniFileSettings::Instance()->GetAutoconfigEnabled())
         {
            // Thunderbird-style autoconfig.
            if (method == "GET" &&
                (path == "/mail/config-v1.1.xml" || path == "/.well-known/autoconfig/mail/config-v1.1.xml"))
               return HandleAutoconfig_(host, query);

            // Apple configuration profile. Apple has no discovery convention
            // for these, so the path is advertised by the administrator; both
            // a friendly top-level name and one alongside the Thunderbird file
            // are served, because both are what people link to in practice.
            if (method == "GET" &&
                (path == "/email.mobileconfig" || path == "/mail/config.mobileconfig"))
               return HandleMobileConfig_(host, query);

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
   WebServicesServer::BuildResponse_(int status_code, const AnsiString &content_type, const AnsiString &body,
                                     const AnsiString &extra_headers)
   {
      AnsiString statusText;
      switch (status_code)
      {
      case 200: statusText = "OK"; break;
      case 301: statusText = "Moved Permanently"; break;
      case 400: statusText = "Bad Request"; break;
      case 404: statusText = "Not Found"; break;
      default:  statusText = "Internal Server Error"; status_code = 500; break;
      }

      AnsiString response;
      response.Format("HTTP/1.0 %d %hs\r\nContent-Type: %hs\r\nContent-Length: %d\r\n",
         status_code, statusText.c_str(), content_type.c_str(), body.GetLength());

      // extra_headers is only ever built here from values that have already
      // been checked for CR and LF - see IsUsableRedirectTarget.
      response += extra_headers;

      response += "Connection: close\r\n\r\n";
      response += body;

      return response;
   }

   AnsiString
   WebServicesServer::BuildRedirectResponse_(const AnsiString &location)
   {
      // 301 rather than 302: RFC 6764 section 6 wants the client to remember
      // the answer, so it stops asking the mail server about calendars.
      AnsiString headers = "Location: " + location + "\r\n";

      return BuildResponse_(301, "text/plain", "moved permanently: " + location, headers);
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
   WebServicesServer::GetDavRedirectSetting_(bool calendar, AnsiString &value)
   {
      value = "";

      AnsiString calDav;
      AnsiString cardDav;

      {
         boost::lock_guard<boost::recursive_mutex> guard(dav_redirect_mutex);

         if (dav_redirect_expires <= time(nullptr))
         {
            dav_caldav_target = AnsiString(ReadSettingsIniString(_T("CalDavRedirectUrl")));
            dav_carddav_target = AnsiString(ReadSettingsIniString(_T("CardDavRedirectUrl")));

            dav_caldav_target.Trim();
            dav_carddav_target.Trim();

            dav_redirect_expires = time(nullptr) + DavRedirectCacheSeconds;
         }

         calDav = dav_caldav_target;
         cardDav = dav_carddav_target;
      }

      value = calendar ? calDav : cardDav;

      return !value.IsEmpty();
   }

   bool
   WebServicesServer::GetDavRedirectTarget_(bool calendar, AnsiString &target)
   {
      if (!GetDavRedirectSetting_(calendar, target))
      {
         // Nothing configured. This is the shipped default, so it is silent -
         // reporting it would put an entry in the ERROR log of every install
         // that has no separate calendar server, which is nearly all of them.
         target = "";
         return false;
      }

      if (!IsUsableRedirectTarget(target))
      {
         // A non-empty value that cannot be used is a real misconfiguration -
         // not a default - so this one does go to ErrorManager. Once per
         // service start, not once per request: the path is public, and an
         // error entry per request would be a log-flooding vector.
         bool report = false;

         {
            boost::lock_guard<boost::recursive_mutex> guard(dav_redirect_mutex);

            bool &reported = calendar ? dav_caldav_invalid_reported : dav_carddav_invalid_reported;

            if (!reported)
            {
               reported = true;
               report = true;
            }
         }

         if (report)
         {
            String settingName = calendar ? _T("CalDavRedirectUrl") : _T("CardDavRedirectUrl");
            String requestPath = calendar ? _T("/.well-known/caldav") : _T("/.well-known/carddav");

            String message;
            message.Format(_T("%s in hMailServer.ini is not a usable absolute URL, so %s answers 404 instead of redirecting. It must begin with https:// or http:// and contain no spaces or control characters. Correct the value or remove the setting."),
               settingName.c_str(), requestPath.c_str());

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5780,
               "WebServicesServer::GetDavRedirectTarget_", message);
         }

         target = "";
         return false;
      }

      return true;
   }

   AnsiString
   WebServicesServer::HandleWellKnownDavRedirect_(bool calendar)
   {
      AnsiString target;

      if (!GetDavRedirectTarget_(calendar, target))
      {
         // 404, deliberately, and not a redirect to this server or to a guessed
         // host name. A client that follows a redirect to something which does
         // not speak CalDAV reports a broken calendar account and retries it
         // forever; a client that gets 404 concludes there is no calendar
         // service and stops. The second is the truth, and it is also the
         // outcome that does not send an administrator debugging the wrong
         // server.
         LOG_DEBUG(_T("WebServices: No CalDAV/CardDAV redirect served - CalDavRedirectUrl / CardDavRedirectUrl is not set in hMailServer.ini. This server does not implement CalDAV or CardDAV; the setting points at the server that does."));
         return BuildResponse_(404, "text/plain", "not found");
      }

      return BuildRedirectResponse_(target);
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
   WebServicesServer::GetQueryEmailAddress_(const AnsiString &query)
   {
      AnsiString lowerQuery = query;
      lowerQuery.MakeLower();

      int parameterPosition = lowerQuery.Find("emailaddress=");
      if (parameterPosition < 0)
         return "";

      AnsiString value = lowerQuery.Mid(parameterPosition + 13);

      int delimiterPosition = value.Find("&");
      if (delimiterPosition >= 0)
         value = value.Mid(0, delimiterPosition);

      // %40 is the only escape worth decoding: it is how every client that
      // fills this parameter in encodes the @, and no other character in an
      // address in practice arrives percent-encoded. Anything that does still
      // carry a % is caught by IsPlausibleEmailAddress and simply not used.
      int encodedAt = value.Find("%40");
      if (encodedAt >= 0)
         value = value.Mid(0, encodedAt) + "@" + value.Mid(encodedAt + 3);

      value.Trim();

      return value;
   }

   AnsiString
   WebServicesServer::GetRequestedMailDomain_(const AnsiString &host, const AnsiString &query,
                                              const AnsiString &client_host)
   {
      // Derive the mail domain: autoconfig.<domain> host, the emailaddress
      // query parameter, or the client host as fallback.
      AnsiString domain;

      AnsiString autoconfigPrefix = "autoconfig.";
      if (host.StartsWith(autoconfigPrefix))
         domain = host.Mid(autoconfigPrefix.GetLength());

      if (domain.IsEmpty())
      {
         AnsiString address = GetQueryEmailAddress_(query);

         int atPosition = address.Find("@");
         if (atPosition >= 0)
            domain = address.Mid(atPosition + 1);
      }

      if (domain.IsEmpty())
         domain = client_host;

      return domain;
   }

   AnsiString
   WebServicesServer::HandleAutoconfig_(const AnsiString &host, const AnsiString &query)
   {
      AnsiString clientHost;
      ProtocolEndpoint imap, pop3, smtp;

      if (!GetClientAccessSettings_(clientHost, imap, pop3, smtp))
         return BuildResponse_(404, "text/plain", "not found");

      AnsiString domain = GetRequestedMailDomain_(host, query, clientHost);

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
   WebServicesServer::MakeStableUuid_(const AnsiString &seed)
   {
      unsigned char digest[SHA256_DIGEST_LENGTH] = {};
      SHA256(reinterpret_cast<const unsigned char*>(seed.c_str()),
         static_cast<size_t>(seed.GetLength()), digest);

      // A name-based UUID: same name, same UUID, for as long as the domain name
      // and this seed prefix stay the same. That matters on the device - iOS
      // and macOS key an installed profile by its PayloadUUID, so a stable one
      // means reinstalling replaces the profile, while a fresh random one every
      // request would leave the user with a stack of duplicate mail accounts.
      //
      // Marked as version 8 (RFC 9562 section 5.8, vendor-specific), which is
      // the honest label for a UUID whose bits come from SHA-256 rather than
      // from the MD5 or SHA-1 that versions 3 and 5 prescribe. The variant bits
      // are the standard 10xx.
      digest[6] = static_cast<unsigned char>((digest[6] & 0x0f) | 0x80);
      digest[8] = static_cast<unsigned char>((digest[8] & 0x3f) | 0x80);

      AnsiString result;
      for (int i = 0; i < 16; i++)
      {
         if (i == 4 || i == 6 || i == 8 || i == 10)
            result += "-";

         AnsiString octet;
         octet.Format("%02X", static_cast<int>(digest[i]));
         result += octet;
      }

      return result;
   }

   AnsiString
   WebServicesServer::HandleMobileConfig_(const AnsiString &host, const AnsiString &query)
   {
      AnsiString clientHost;
      ProtocolEndpoint imap, pop3, smtp;

      // The same settings the Thunderbird and Outlook handlers use. Apple gets
      // a third wire format, not a second source of truth.
      if (!GetClientAccessSettings_(clientHost, imap, pop3, smtp))
         return BuildResponse_(404, "text/plain", "not found");

      // A com.apple.mail.managed payload carries exactly one incoming server
      // and one outgoing server, and both are mandatory. IMAP is preferred;
      // POP3 is used only when no IMAP port is configured at all.
      bool useImap = imap.port > 0;
      const ProtocolEndpoint &incoming = useImap ? imap : pop3;

      if (incoming.port <= 0 || smtp.port <= 0)
      {
         // No half-configured profile. A profile the device accepts and then
         // cannot send with is worse than no profile: the account exists, mail
         // silently sits in Outbox, and nothing points at the server.
         LOG_DEBUG(_T("WebServices: No .mobileconfig served - an Apple configuration profile needs both an incoming (IMAP or POP3) and an SMTP port, and one of them is not configured."));
         return BuildResponse_(404, "text/plain", "not found");
      }

      AnsiString domain = SanitizeHostName(GetRequestedMailDomain_(host, query, clientHost));
      if (domain.IsEmpty())
         domain = clientHost;

      // Pre-fill the address only when the request actually supplied a usable
      // one. Leaving the address keys out makes the device prompt for them
      // during installation, which is the right behaviour for a profile fetched
      // without any hint of who is installing it.
      AnsiString emailAddress = GetQueryEmailAddress_(query);
      if (!IsPlausibleEmailAddress(emailAddress))
         emailAddress = "";

      // Apple's mail payload has one encryption key per direction and no
      // separate STARTTLS setting: Mail negotiates STARTTLS or implicit TLS
      // from the port. So both of our encrypted modes map to true, and only a
      // genuinely plaintext port maps to false.
      bool incomingSsl = incoming.socket_type != "plain";
      bool outgoingSsl = smtp.socket_type != "plain";

      AnsiString escapedDomain = XmlEscape_(domain);
      AnsiString escapedClientHost = XmlEscape_(clientHost);
      AnsiString escapedAddress = XmlEscape_(emailAddress);

      AnsiString profileUuid = MakeStableUuid_("hMailServer.mobileconfig.profile:" + domain);
      AnsiString mailUuid = MakeStableUuid_("hMailServer.mobileconfig.mail:" + domain);

      AnsiString plist;

      auto appendString = [&plist](const AnsiString &key, const AnsiString &value)
      {
         plist += "      <key>" + key + "</key>\r\n";
         plist += "      <string>" + value + "</string>\r\n";
      };

      auto appendInteger = [&plist](const AnsiString &key, int value)
      {
         AnsiString line;
         line.Format("      <key>%hs</key>\r\n      <integer>%d</integer>\r\n", key.c_str(), value);
         plist += line;
      };

      auto appendBool = [&plist](const AnsiString &key, bool value)
      {
         plist += "      <key>" + key + "</key>\r\n";
         plist += AnsiString("      ") + (value ? "<true/>" : "<false/>") + "\r\n";
      };

      plist += "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n";
      plist += "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\r\n";
      plist += "<plist version=\"1.0\">\r\n";
      plist += "<dict>\r\n";
      plist += "  <key>PayloadContent</key>\r\n";
      plist += "  <array>\r\n";
      plist += "    <dict>\r\n";

      appendString("PayloadType", "com.apple.mail.managed");
      appendInteger("PayloadVersion", 1);
      appendString("PayloadIdentifier", "com.hmailserver.mail." + escapedDomain);
      appendString("PayloadUUID", mailUuid);
      appendString("PayloadDisplayName", "Mail (" + escapedDomain + ")");
      appendString("EmailAccountDescription", escapedDomain);
      appendString("EmailAccountType", useImap ? "EmailTypeIMAP" : "EmailTypePOP");

      if (!escapedAddress.IsEmpty())
         appendString("EmailAddress", escapedAddress);

      appendString("IncomingMailServerHostName", escapedClientHost);
      appendInteger("IncomingMailServerPortNumber", incoming.port);
      appendBool("IncomingMailServerUseSSL", incomingSsl);
      appendString("IncomingMailServerAuthentication", "EmailAuthPassword");

      if (!escapedAddress.IsEmpty())
         appendString("IncomingMailServerUsername", escapedAddress);

      appendString("OutgoingMailServerHostName", escapedClientHost);
      appendInteger("OutgoingMailServerPortNumber", smtp.port);
      appendBool("OutgoingMailServerUseSSL", outgoingSsl);
      appendString("OutgoingMailServerAuthentication", "EmailAuthPassword");

      if (!escapedAddress.IsEmpty())
         appendString("OutgoingMailServerUsername", escapedAddress);

      // Submission on this server authenticates with the mailbox credentials,
      // so the device should reuse the one password it prompts for rather than
      // asking a second time for the same thing.
      appendBool("OutgoingPasswordSameAsIncomingPassword", true);

      plist += "    </dict>\r\n";
      plist += "  </array>\r\n";
      plist += "  <key>PayloadDisplayName</key>\r\n";
      plist += "  <string>" + escapedDomain + " Mail</string>\r\n";
      plist += "  <key>PayloadDescription</key>\r\n";
      plist += "  <string>Configures Mail for " + escapedDomain + ".</string>\r\n";
      plist += "  <key>PayloadIdentifier</key>\r\n";
      plist += "  <string>com.hmailserver." + escapedDomain + "</string>\r\n";
      plist += "  <key>PayloadOrganization</key>\r\n";
      plist += "  <string>" + escapedDomain + "</string>\r\n";
      plist += "  <key>PayloadType</key>\r\n";
      plist += "  <string>Configuration</string>\r\n";
      plist += "  <key>PayloadUUID</key>\r\n";
      plist += "  <string>" + profileUuid + "</string>\r\n";
      plist += "  <key>PayloadVersion</key>\r\n";
      plist += "  <integer>1</integer>\r\n";

      // The user must always be able to take it off again. A mail profile that
      // cannot be removed without wiping the device is not something a mail
      // server should hand out.
      plist += "  <key>PayloadRemovalDisallowed</key>\r\n";
      plist += "  <false/>\r\n";
      plist += "</dict>\r\n";
      plist += "</plist>\r\n";

      // Served inline with the Apple content type and no Content-Disposition:
      // that is what makes Safari on iOS hand the file to the profile
      // installer. Marking it as an attachment sends it to Files instead on
      // some versions, which strands the user with a downloaded file and no
      // obvious way to install it.
      return BuildResponse_(200, "application/x-apple-aspen-config", plist);
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
