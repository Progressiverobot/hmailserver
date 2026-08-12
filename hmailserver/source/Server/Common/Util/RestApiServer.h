// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// REST administration API over HTTPS.
//
// Disabled by default. Enabled with RestApiPort in hMailServer.ini.
// Security model:
//   - Two credentials are accepted:
//       * Authorization: Bearer <api key> - a scoped, expiring API key. This is
//         the preferred form and is tried first when present.
//       * HTTP Basic authentication against the hMailServer administrator
//         password (same credential as the COM API / hMailAdmin). Retained
//         because every existing script uses it.
//   - API key management (/api/v1/apikeys) deliberately requires the
//     administrator password: a key must not be able to mint or revoke keys,
//     otherwise a narrowly scoped key trivially escalates to an unscoped one.
//   - TLS is mandatory unless the listener is bound to 127.0.0.1.
//   - An empty administrator password disables the API entirely.
//
// API key store: <directory of hMailServer.ini>\hMailServerApiKeys.ini, read
// on every authentication attempt so that adding or revoking a key takes
// effect immediately with no restart and no rebuild. Only the SHA-256 of a key
// is ever stored; the clear-text token exists once, in the 201 response that
// created it. See the comment above LoadKeys_ in the .cpp for the file format
// and for why SHA-256 (and not Argon2id) is the right primitive here.

#pragma once

#include <thread>
#include <vector>

namespace HM
{
   class IPAddress;

   class RestApiServer
   {
   public:
      RestApiServer();
      ~RestApiServer();

      bool Start(const String &bind_address, int port, const String &certificate_file, const String &private_key_file);
      void Stop();

      // Full path of the API key store. Public so that tooling and tests can
      // locate the file without duplicating the derivation.
      static String GetApiKeyStoreFile();

   private:

      // Which credential a request presented. Used to keep key management off
      // limits to API keys themselves.
      enum AuthenticationResult
      {
         AuthenticationFailed = 0,
         AuthenticatedAsAdministrator = 1,
         AuthenticatedWithApiKey = 2
      };

      // One record in the API key store. Never holds the clear-text token.
      struct ApiKeyRecord
      {
         String id;             // store section suffix; used to revoke
         String label;          // human-readable, so keys can be told apart
         AnsiString hash;       // lower-case hex SHA-256 of the token
         String expires;        // hMailServer system date: YYYY-MM-DD HH:MM:SS
         String allowed_from;   // empty = any source; else address, range or CIDR
      };

      void Run_();
      void HandleClient_(SOCKET client_socket, const IPAddress &peer_address);

      // Request processing. Returns the full HTTP response.
      AnsiString ProcessRequest_(const AnsiString &request, const IPAddress &peer_address);

      static AuthenticationResult Authenticate_(const AnsiString &request, const IPAddress &peer_address);
      static AuthenticationResult AuthenticateBearer_(const AnsiString &token, const IPAddress &peer_address);
      static bool AuthenticateBasic_(const AnsiString &encodedCredentials);

      static AnsiString GetAuthorizationHeader_(const AnsiString &request);
      static AnsiString BuildUnauthorizedResponse_();

      // Records a rejected credential so that Configuration's auto-ban
      // machinery counts it, exactly as the SMTP/IMAP/POP3 front ends do.
      static void RegisterAuthenticationFailure_(const IPAddress &peer_address);

      // The refused-source set: the in-process, bounded, self-expiring record
      // of addresses whose credentials have already tripped the auto-ban.
      // Security ranges are only ever consulted through
      // TCPConnection::GetSecurityRange(), which this listener does not use, so
      // without this the ban it creates would be inert here - and creating a
      // ban per three requests while never honouring one turns a credential
      // flood on this port into database load that every mail connection pays
      // for. See the block comment above the definitions in the .cpp.
      static bool IsRefusedAddress_(const IPAddress &peer_address);
      static void RefuseAddress_(const IPAddress &peer_address);
      static void ClearRefusedAddresses_();

      // API key store.
      static std::vector<ApiKeyRecord> LoadKeys_();
      static bool IsExpired_(const String &expires);
      static bool IsSourceAllowed_(const String &allowed_from, const IPAddress &peer_address);

      static AnsiString HandleListApiKeys_();
      static AnsiString HandleCreateApiKey_(const AnsiString &requestBody);
      static AnsiString HandleRevokeApiKey_(const AnsiString &id);

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

      // True if the id names a message that is really in the delivery queue.
      static bool QueueMessageExists_(__int64 messageId);

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
