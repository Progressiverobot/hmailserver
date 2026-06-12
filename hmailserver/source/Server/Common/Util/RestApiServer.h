// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// REST administration API over HTTPS.
//
// Disabled by default. Enabled with RestApiPort in hMailServer.ini.
// Security model:
//   - HTTP Basic authentication against the hMailServer administrator
//     password (same credential as the COM API / hMailAdmin).
//   - TLS is mandatory unless the listener is bound to 127.0.0.1.
//   - An empty administrator password disables the API entirely.

#pragma once

#include <thread>

namespace HM
{
   class RestApiServer
   {
   public:
      RestApiServer();
      ~RestApiServer();

      bool Start(const String &bind_address, int port, const String &certificate_file, const String &private_key_file);
      void Stop();

   private:

      void Run_();
      void HandleClient_(SOCKET client_socket);

      // Request processing. Returns the full HTTP response.
      AnsiString ProcessRequest_(const AnsiString &request);
      static bool Authenticate_(const AnsiString &request);

      static AnsiString BuildResponse_(int statusCode, const AnsiString &body);
      static AnsiString HandleWebAdminPage_();
      static AnsiString HandleStatus_();
      static AnsiString HandleListDomains_();
      static AnsiString HandleListAccounts_(const String &domainName);
      static AnsiString HandleCreateAccount_(const String &domainName, const AnsiString &requestBody);
      static AnsiString HandleDeleteAccount_(const String &address);
      static AnsiString HandleListQueue_();
      static AnsiString HandleQueueRetry_(__int64 messageId);
      static AnsiString HandleQueueDelete_(__int64 messageId);
      static AnsiString HandleTlsa_();

      static AnsiString GetRequestBody_(const AnsiString &request);
      static AnsiString GetJsonStringValue_(const AnsiString &json, const AnsiString &key);
      static AnsiString JsonEscape_(const AnsiString &value);

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;
      bool use_tls_;
      String certificate_file_;
      String private_key_file_;
   };
}
