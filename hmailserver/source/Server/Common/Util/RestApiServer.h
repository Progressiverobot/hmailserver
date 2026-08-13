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
//         because every existing script uses it. It is the full-authority
//         credential: nothing below narrows it.
//   - API key management (/api/v1/apikeys) deliberately requires the
//     administrator password: a key must not be able to mint or revoke keys,
//     otherwise a narrowly scoped key trivially escalates to an unscoped one.
//   - TLS is mandatory unless the listener is bound to 127.0.0.1.
//   - An empty administrator password disables the API entirely.
//
// What "scoped" means, and what it did not mean before August 2026: a key
// carried a label, an expiry and an optional source restriction, and that was
// all. Every key was otherwise the administrator password with a different
// spelling - a key minted for a monitoring probe could DELETE any account in
// any domain and drop any message from the delivery queue. Two restrictions
// now carry that weight, and both are enforced in one place (Authorize_, over
// a Route decided by ParseRoute_) rather than at each endpoint:
//
//   - Scope=readonly|full. readonly keys may only read; every route that
//     changes something is refused with 403. This is the default: a create
//     request that does not name a scope gets a read-only key, because the
//     opposite default hands out full authority to anyone who did not read
//     the documentation.
//   - Domains=<comma-separated list>. Empty means every domain. A key with a
//     list may only act on those domains, which is what stops the attack the
//     path shape invites: changing the address in
//     DELETE /api/v1/accounts/<address> to name a mailbox in someone else's
//     domain. Such a key is also refused the delivery-queue routes outright,
//     because a queued message carries recipients in any number of domains
//     and there is no honest way to narrow it.
//
// Both restrictions fail closed when the store is hand-edited: an absent or
// unrecognised Scope leaves the key read-only, and a Domains entry that is not
// a real domain name simply matches nothing. (An entirely empty Domains value
// means every domain, exactly as an empty AllowedFrom means any source and as a
// section with no Domains line at all already did.)
//
// Rate: requests are counted per credential (see IsWithinRequestRate_), not
// per source address, so one leaked key cannot spend the listener's whole
// capacity by rotating source addresses.
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

      // What a request asks for, decided once from the method and the path.
      // Authorisation is then a decision about a RouteKind rather than about a
      // string, which is what lets it be made in exactly one place: an
      // endpoint that forgets to check something cannot exist if no endpoint
      // does the checking.
      enum RouteKind
      {
         RouteUnknown = 0,

         // Under /api/v1/apikeys but not one of the three supported calls.
         // Distinct from RouteUnknown so the administrator-only rule still
         // covers it: an API key probing this prefix must learn nothing from
         // the answer, not even whether a verb exists.
         RouteApiKeyUnsupported,

         RouteApiKeyList,
         RouteApiKeyCreate,
         RouteApiKeyRevoke,
         RouteStatus,
         RouteDomainList,
         RouteAccountList,
         RouteAccountCreate,
         RouteAccountDelete,
         RouteQueueList,
         RouteQueueRetry,
         RouteQueueDelete,
         RouteTlsa
      };

      struct Route
      {
         Route() : kind(RouteUnknown), message_id(0) { }

         RouteKind kind;
         AnsiString identifier;   // domain name, account address or api key id
         __int64 message_id;
      };

      // One record in the API key store. Never holds the clear-text token.
      struct ApiKeyRecord
      {
         // read_only defaults to true so that every path that fails to read a
         // Scope - a hand-written section, a truncated file, a value we do not
         // recognise - leaves the key unable to change anything.
         ApiKeyRecord() : read_only(true) { }

         String id;             // store section suffix; used to revoke
         String label;          // human-readable, so keys can be told apart
         AnsiString hash;       // lower-case hex SHA-256 of the token
         String expires;        // hMailServer system date: YYYY-MM-DD HH:MM:SS
         String allowed_from;   // empty = any source; else address, range or CIDR
         bool read_only;        // Scope: false only for the literal "full"
         std::vector<String> domains;   // empty = every domain
      };

      // The authority a request carries, and the one thing an authorisation
      // decision is allowed to look at.
      struct Caller
      {
         Caller() : result(AuthenticationFailed), read_only(true) { }

         AuthenticationResult result;

         // "administrator", or "key:<id>". Identifies the credential for rate
         // accounting and for the log; never the secret itself.
         AnsiString identity;

         bool read_only;
         std::vector<String> domains;
      };

      enum AuthorizationResult
      {
         AuthorizationAllowed = 0,

         // Refused as a credential problem: answered 401, so that a key
         // probing key management cannot tell "valid but not permitted" from
         // "not a key at all".
         AuthorizationUnauthenticated = 1,

         // Refused as a permission problem: answered 403 and said why. The
         // caller already knows its credential is good, so there is nothing
         // left to conceal and everything to gain from being clear.
         AuthorizationForbidden = 2
      };

      void Run_();
      void HandleClient_(SOCKET client_socket, const IPAddress &peer_address);

      // Request processing. Returns the full HTTP response.
      AnsiString ProcessRequest_(const AnsiString &request, const IPAddress &peer_address);

      static void ParseRoute_(const AnsiString &method, const AnsiString &path, Route &route);

      // The single authorisation choke point. Every route passes through this
      // and nothing else decides access.
      static AuthorizationResult Authorize_(const Caller &caller, const Route &route, AnsiString &refusalReason);

      static bool IsMutatingRoute_(RouteKind kind);
      static bool IsApiKeyRoute_(RouteKind kind);
      static bool IsDomainAllowed_(const std::vector<String> &domains, const String &domainName);

      static Caller Authenticate_(const AnsiString &request, const IPAddress &peer_address);
      static bool AuthenticateBearer_(const AnsiString &token, const IPAddress &peer_address, Caller &caller);
      static bool AuthenticateBasic_(const AnsiString &encodedCredentials);

      static AnsiString GetAuthorizationHeader_(const AnsiString &request);
      static AnsiString BuildUnauthorizedResponse_();
      static AnsiString BuildForbiddenResponse_(const AnsiString &reason);
      static AnsiString BuildTooManyRequestsResponse_();

      // Per-credential request budget. Returns false when this request is over
      // it; firstRefusal is set only for the request that crosses the line, so
      // that a flood costs one log line per window rather than one per request.
      static bool IsWithinRequestRate_(const AnsiString &identity, bool &firstRefusal);
      static void ClearRequestRates_();

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

      // extraHeaders, when it is not empty, must be complete header lines each
      // ending in CRLF. Only the 429 uses it (Retry-After).
      static AnsiString BuildResponse_(int statusCode, const AnsiString &body, const AnsiString &extraHeaders = "");
      static AnsiString HandleWebAdminPage_();
      static AnsiString HandleStatus_();

      // allowedDomains empty means every domain; otherwise the listing is
      // filtered to those, so a key issued for one customer is not handed the
      // names of all the others.
      static AnsiString HandleListDomains_(const std::vector<String> &allowedDomains);
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
