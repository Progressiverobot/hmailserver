// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
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
//   - TLS is mandatory unless the listener is bound to loopback (127.0.0.1
//     or ::1).
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

#include <memory>
#include <vector>
#include <map>

#include "HttpServer.h"

namespace HM
{
   class IPAddress;
   class Account;
   class Domain;
   class Rule;
   class IMAPFolder;
   class IMAPFolders;
   class Message;
   class MessageData;

   // The server-wide settings group as one JSON object, from the table in
   // RestApiSettings.cpp that the GET, the PUT and the OpenAPI description all
   // read; escape is the JSON string escaper to use for its values.
   AnsiString RestApiSettingsServerGroupJson(AnsiString (*escape)(const AnsiString &));

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

      // One address the signed-in account may write as (RestApiIdentities.cpp).
      struct Identity
      {
         String address;
         String name;
         AnsiString kind;
      };

   private:

      // Which credential a request presented. Used to keep key management off
      // limits to API keys themselves.
      enum AuthenticationResult
      {
         AuthenticationFailed = 0,
         AuthenticatedAsAdministrator = 1,
         AuthenticatedWithApiKey = 2,

         // An account's own credentials, for the account's own endpoints
         // under /api/v1/me. Reaches nothing else.
         AuthenticatedAsAccount = 3
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
         // The domain's own writes. Create and delete are server-wide - the set
         // of domains is the server's, not any one domain's - and are refused
         // for a domain-restricted key in Authorize_; the update is scoped to
         // the domain it names, as the account routes are.
         RouteDomainCreate,
         RouteDomainUpdate,
         RouteDomainDelete,
         RouteAccountList,
         RouteAccountCreate,
         RouteAccountDelete,
         RouteQueueList,
         RouteQueueRetry,
         RouteQueueDelete,
         RouteTlsa,
         RouteSrv,
         RouteMetricsHistory,
         RouteUpdateGet,
         RouteUpdateCheck,
         RouteUpdateDownload,
         RouteUpdateInstall,
         RouteQuarantineList,
         RouteQuarantineRelease,
         RouteQuarantineDelete,
         RouteAliasList,
         // Wave 88: the surfaces that were COM-only. Server-wide ones are refused
         // for domain-restricted keys in Authorize_; the list routes are scoped to
         // their domain the way the alias and account routes are.
         RouteIpRangeList,
         RouteIpRangeCreate,
         RouteIpRangeDelete,
         RouteListList,
         RouteListCreate,
         RouteListDelete,
         RouteCertificateList,
         RouteDkimGet,
         RouteRuleList,
         RouteLogList,
         RouteLogTail,
         RouteBackupStart,
         RouteBackupStatus,
         RouteSettingsGet,
         RouteArchiveSearch,
         RouteArchiveGet,
         RouteArchiveHold,
         RouteArchiveRelease,
         // The account's own endpoints: answered to the account's credentials
         // and to nothing else.
         RouteMe,
         RouteMePassword,
         RouteMeVacation,
         RouteMeQuarantineList,
         RouteMeQuarantineRelease,
         RouteMeQuarantineDelete,
         RouteMeFolders,
         RouteMeFolderMessages,
         // Wave 164: the account's own folder writes, and the change probe the
         // portal polls instead of reloading the whole folder tree.
         RouteMeFolderCreate,
         RouteMeFolderRename,
         RouteMeFolderDelete,
         RouteMeChanges,
         RouteMeMessage,
         RouteMeMessageFlags,
         RouteMeMessageMove,
         RouteMeMessageDelete,
         RouteMeMessageSend,
         RouteMeMessageAttachment,
         RouteMeSearch,
         RouteMeSettings,
         RouteMeSettingsPut,
         RouteMeFilters,
         RouteMeFiltersPut,
         RouteMeDraftSave,
         RouteMeContacts,
         RouteMeContactCreate,
         RouteMeContactUpdate,
         RouteMeContactDelete,
         RouteMeIdentities,
         RouteMePreferences,
         RouteMePreferencesPut,
         RouteSessionCreate,
         RouteSessionDelete,
         // Wave 162: the write surface. Server-wide ones are refused for
         // domain-restricted keys in Authorize_; the alias and account writes
         // are scoped to their domain the way the account routes are.
         RouteSettingsPut,
         RouteSettingsAntiSpamGet,
         RouteSettingsAntiSpamPut,
         RouteSettingsLoggingGet,
         RouteSettingsLoggingPut,
         RouteRuleCreate,
         RouteRuleUpdate,
         RouteRuleDelete,
         RouteCertificateCreate,
         RouteCertificateDelete,
         RoutePortList,
         RoutePortCreate,
         RoutePortUpdate,
         RoutePortDelete,
         RouteRouteList,
         RouteRouteCreate,
         RouteRouteUpdate,
         RouteRouteDelete,
         RouteAliasCreate,
         RouteAliasDelete,
         RouteAccountUpdate,
         RouteServerReinitialize,
         RouteOpenApi
      };

      struct Route
      {
         Route() : kind(RouteUnknown), message_id(0), range_id(0), archive_id(0), folder_id(0), attachment_index(0), record_id(0) { }

         RouteKind kind;
         AnsiString identifier;   // domain name, account address or api key id
         __int64 message_id;
         __int64 range_id;        // an IP range id, for the routes that name one
         __int64 archive_id;      // an archive index row id, for the routes that name one
         __int64 folder_id;       // an IMAP folder id, for the account's own mailbox routes
         int attachment_index;    // which attachment of a message, for the download route
         __int64 record_id;       // a rule, certificate, port or route id, for the write routes that name one
         AnsiString query;        // the part after "?", for the routes that take one
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
         Caller() : result(AuthenticationFailed), read_only(true), second_factor_required(false) { }

         AuthenticationResult result;

         // Set on a refusal when the administrator password was right and what
         // was missing was the one-time code; the 401 then says so in a header,
         // as GitHub's API does, so a client knows to ask for it.
         bool second_factor_required;

         // "administrator", or "key:<id>". Identifies the credential for rate
         // accounting and for the log; never the secret itself.
         AnsiString identity;

         bool read_only;
         std::vector<String> domains;

         // Set when result is AuthenticatedAsAccount.
         std::shared_ptr<const Account> account;

         // Where the request came from, for the handlers that count a
         // failure against it.
         IPAddress peer;

         // True when the credential was a browser session cookie rather than
         // a password: such a request must carry the X-Requested-With header
         // to change anything, and may end the session it came with.
         bool via_session = false;
         AnsiString session_hash;
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

      // Request processing. Returns the full HTTP response.
      HttpResponse ProcessRequest_(const AnsiString &request, const IPAddress &peer_address);

      static void ParseRoute_(const AnsiString &method, const AnsiString &path, Route &route);

      // The single authorisation choke point. Every route passes through this
      // and nothing else decides access.
      static AuthorizationResult Authorize_(const Caller &caller, const Route &route, AnsiString &refusalReason);

      static bool IsMutatingRoute_(RouteKind kind);
      static bool IsApiKeyRoute_(RouteKind kind);
      static bool IsSelfServiceRoute_(RouteKind kind);
      static bool AuthenticateAccount_(const String &username, const String &password, const IPAddress &peer_address, Caller &caller);
      static HttpResponse HandleMe_(const Caller &caller);
      static HttpResponse HandleMePassword_(const Caller &caller, const AnsiString &request);
      static HttpResponse HandleMeVacation_(const Caller &caller, const AnsiString &requestBody);
      static HttpResponse HandlePortalPage_();
      static HttpResponse HandleMeQuarantineList_(const Caller &caller);
      static HttpResponse HandleMeQuarantineRelease_(const Caller &caller, __int64 id);
      static HttpResponse HandleMeQuarantineDelete_(const Caller &caller, __int64 id);
      static HttpResponse HandleMeFolders_(const Caller &caller);
      // Wave 164, all of them in RestApiMailbox.cpp: the account's own folder
      // writes and the change probe. Each judges a name exactly as the IMAP
      // command of the same purpose judges it and answers with that command's
      // own sentence, so a portal and a mail client are never told different
      // things about the same mailbox.
      static HttpResponse HandleMeFolderCreate_(const Caller &caller, const AnsiString &requestBody);
      static HttpResponse HandleMeFolderRename_(const Caller &caller, __int64 folderId, const AnsiString &requestBody);
      static HttpResponse HandleMeFolderDelete_(const Caller &caller, __int64 folderId);
      static HttpResponse HandleMeChanges_(const Caller &caller, const AnsiString &query);
      static AnsiString OpenApiMailboxPaths_();
      // The folder the id names in the SIGNED-IN ACCOUNT'S OWN tree, and
      // nowhere else: the public namespace and a delegating owner's folders are
      // readable through the message routes, but they are not this account's to
      // create, rename or delete.
      static std::shared_ptr<IMAPFolder> FindOwnFolder_(std::shared_ptr<const Account> account, __int64 folderId);
      // The path of one folder inside its own tree, walked up by parent id, as
      // the vector of names IMAP would have split from a mailbox name. False
      // when the walk does not reach the root, which is what a folderparentid
      // cycle looks like.
      static String DecodeFolderName_(const String &stored);
      static bool IsAtOrBelow_(std::shared_ptr<IMAPFolders> tree, __int64 folderId, __int64 ancestorId);
      static std::vector<String> StoredFolderPath_(const String &name, const String &delimiter);
      static bool OwnFolderPath_(std::shared_ptr<IMAPFolders> tree, std::shared_ptr<IMAPFolder> folder, std::vector<String> &path);
      // One folder as the listing renders it, subtree and all, so that what a
      // create or a rename answers is exactly what the next listing shows.
      static AnsiString FolderEntryJson_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolder> folder);
      static HttpResponse HandleMeFolderMessages_(const Caller &caller, __int64 folderId, const AnsiString &query);
      static HttpResponse HandleMeMessage_(const Caller &caller, __int64 messageId);
      static std::shared_ptr<IMAPFolder> FindReadableFolder_(std::shared_ptr<const Account> account, __int64 folderId);
      static bool RightOn_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolder> folder, int permission);
      static String MessageFile_(std::shared_ptr<const Message> message);
      static void AppendFolderJson_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolders> folders,
                                    const String &parentPath, const std::map<__int64, int> &designations,
                                    const String &delimiter, AnsiString &json, int depth);
      // One entry of that listing, with its subtree. Split out of the loop so
      // that the folder writes answer with the very same document rather than a
      // second spelling of it that could drift.
      static void AppendOneFolderJson_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolder> folder,
                                       const String &path, const std::map<__int64, int> &designations,
                                       const String &delimiter, bool writeAccess, AnsiString &json, int depth);
      static HttpResponse HandleMeMessageFlags_(const Caller &caller, __int64 messageId, const AnsiString &requestBody);
      static HttpResponse HandleMeMessageMove_(const Caller &caller, __int64 messageId, const AnsiString &requestBody);
      static HttpResponse HandleMeMessageDelete_(const Caller &caller, __int64 messageId, const AnsiString &query);
      static std::shared_ptr<Message> FindOwnMessage_(std::shared_ptr<const Account> account, __int64 messageId, std::shared_ptr<IMAPFolder> &folder);
      static std::shared_ptr<IMAPFolder> FindDesignatedFolder_(std::shared_ptr<const Account> account, int designation);
      static HttpResponse HandleMeMessageSend_(const Caller &caller, const AnsiString &requestBody);
      static String JsonUtf8Value_(const AnsiString &json, const AnsiString &key);
      static HttpResponse HandleMeMessageAttachment_(const Caller &caller, __int64 messageId, int index);
      static HttpResponse HandleMeSearch_(const Caller &caller, const AnsiString &query);
      static HttpResponse HandleMeSettings_(const Caller &caller);
      static HttpResponse HandleMeSettingsPut_(const Caller &caller, const AnsiString &requestBody);
      static HttpResponse HandleMeFilters_(const Caller &caller);
      static HttpResponse HandleMeFiltersPut_(const Caller &caller, const AnsiString &requestBody);
      static AnsiString SettingsJson_(std::shared_ptr<const Account> account);
      static HttpResponse HandleMeDraftSave_(const Caller &caller, const AnsiString &requestBody);

      // The account's address book (RestApiContacts.cpp).
      static HttpResponse HandleMeContacts_(const Caller &caller, const AnsiString &query);
      static HttpResponse HandleMeContactCreate_(const Caller &caller, const AnsiString &requestBody);
      static HttpResponse HandleMeContactUpdate_(const Caller &caller, __int64 id, const AnsiString &requestBody);
      static HttpResponse HandleMeContactDelete_(const Caller &caller, __int64 id);
      static void CollectContacts_(std::shared_ptr<const Account> account, const std::vector<String> &entries);
      static AnsiString ContactJson_(__int64 id, const String &name, const String &address, int source, const String &created);
      static bool FindContact_(__int64 accountId, const String &address, __int64 &contactId);
      static bool InsertContact_(__int64 accountId, const String &name, const String &address, int source, __int64 &contactId, String &created);

      // Who the account may write as, and the From a send or draft asked for (RestApiIdentities.cpp).
      static HttpResponse HandleMeIdentities_(const Caller &caller);
      static void IdentitiesFor_(std::shared_ptr<const Account> account, std::vector<Identity> &identities);
      static int ResolveSender_(std::shared_ptr<const Account> account, const String &requested, String &address, String &header, AnsiString &problem);

      // The account's preferences, a key/value store (RestApiPreferences.cpp).
      static HttpResponse HandleMePreferences_(const Caller &caller);
      static HttpResponse HandleMePreferencesPut_(const Caller &caller, const AnsiString &requestBody);
      static AnsiString PreferencesJson_(__int64 accountId);
      static AnsiString ThreadFieldsJson_(const String &fileName);
      static String FromHeader_(std::shared_ptr<const Account> account);
      static int AddAttachmentsFromJson_(MessageData &messageData, const AnsiString &requestBody, AnsiString &error);
      static bool IsLargeRequest_(const AnsiString &method, const AnsiString &target);
      static bool ReadJsonHex4_(const AnsiString &json, int at, unsigned int &value);
      static void AppendUtf8_(AnsiString &out, unsigned int codePoint);
      static void CollectReadableFolders_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolders> folders, const String &parentPath,
                                          const String &delimiter, std::vector<std::pair<std::shared_ptr<IMAPFolder>, String>> &out, int depth);
      // UTF-8 for the JSON: the plain String-to-AnsiString conversion is the
      // system code page, which is not what a JSON reader expects.
      static AnsiString Utf8_(const String &value);
      static AnsiString Utf8_(const AnsiString &value) { return value; }
      static AnsiString Utf8_(const char *value) { return AnsiString(value); }
      static bool DeleteOwnMessage_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message, std::shared_ptr<IMAPFolder> folder);
      static bool AuthenticateSession_(const AnsiString &request, const IPAddress &peer_address, Caller &caller);
      HttpResponse HandleSessionCreate_(const Caller &caller);
      HttpResponse HandleSessionDelete_(const Caller &caller);
      static void RevokeSessionsForAccount_(__int64 accountId, const AnsiString &keepTokenHash);
      static void ClearBrowserSessions_();
      static HttpResponse HandlePortalScript_();
      static bool IsDomainAllowed_(const std::vector<String> &domains, const String &domainName);

      static Caller Authenticate_(const AnsiString &request, const IPAddress &peer_address);
      static bool AuthenticateBearer_(const AnsiString &token, const IPAddress &peer_address, Caller &caller);
      // The Basic credential is the administrator password, and - once a second
      // factor is enrolled on it - a code in the X-hMailServer-OTP request header
      // as well. The two code outcomes are told apart because only one of them is
      // a guess: a missing code is a client that has not been told yet, a wrong one
      // counts towards the per-address auto-ban like any other wrong credential.
      enum BasicResult
      {
         BasicRefused,
         BasicAccepted,
         BasicCodeMissing,
         BasicCodeWrong
      };

      static BasicResult AuthenticateBasic_(const AnsiString &encodedCredentials, const AnsiString &request);

      static AnsiString GetAuthorizationHeader_(const AnsiString &request);
      static AnsiString GetHeader_(const AnsiString &request, const AnsiString &lowerCaseName);
      // suppressChallenge leaves out WWW-Authenticate, for a request a page's
      // own script made: see the definition, and IsPageScriptRequest_ below.
      static HttpResponse BuildUnauthorizedResponse_(bool secondFactorRequired, bool suppressChallenge);
      static bool IsPageScriptRequest_(const AnsiString &request);
      static HttpResponse BuildForbiddenResponse_(const AnsiString &reason);
      static HttpResponse BuildTooManyRequestsResponse_();

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

      static HttpResponse HandleListApiKeys_();
      static HttpResponse HandleCreateApiKey_(const AnsiString &requestBody);
      static HttpResponse HandleRevokeApiKey_(const AnsiString &id);

      // extraHeaders, when it is not empty, must be complete header lines each
      // ending in CRLF. Only the 429 uses it (Retry-After).
      static HttpResponse BuildResponse_(int statusCode, const AnsiString &body, const AnsiString &extraHeaders = "");
      static HttpResponse HandleWebAdminPage_();
      static HttpResponse HandleStatus_();

      // allowedDomains empty means every domain; otherwise the listing is
      // filtered to those, so a key issued for one customer is not handed the
      // names of all the others.
      static HttpResponse HandleListDomains_(const std::vector<String> &allowedDomains);

      // One domain as the listing renders it, so that what a create or an
      // update answers is exactly what the next listing will show.
      static AnsiString DomainEntryJson_(const std::shared_ptr<Domain> &domain);
      static HttpResponse HandleCreateDomain_(const AnsiString &requestBody);
      static HttpResponse HandleUpdateDomain_(const String &domainName, const AnsiString &requestBody);
      static HttpResponse HandleDeleteDomain_(const String &domainName);
      static HttpResponse HandleListAccounts_(const String &domainName);
      static HttpResponse HandleCreateAccount_(const String &domainName, const AnsiString &requestBody);
      static HttpResponse HandleDeleteAccount_(const String &address);
      static HttpResponse HandleListQueue_();
      static HttpResponse HandleQueueRetry_(__int64 messageId);
      static HttpResponse HandleQueueDelete_(__int64 messageId);
      static HttpResponse HandleTlsa_();

      // Ready-to-publish client-discovery SRV records (RFC 6186 / RFC 8314,
      // plus the Outlook _autodiscover convention), derived from the ports
      // that are actually configured and enabled. allowedDomains filters the
      // per-domain records exactly as HandleListDomains_ filters the domain
      // listing, and for the same reason.
      static HttpResponse HandleSrv_(const std::vector<String> &allowedDomains);
      static HttpResponse HandleMetricsHistory_(const AnsiString &query);
      // The update check's verdict, and a check run now. Both server-wide.
      static HttpResponse HandleUpdateGet_();
      static HttpResponse HandleUpdateCheck_();
      static HttpResponse HandleUpdateDownload_();
      static HttpResponse HandleUpdateInstall_();
      static AnsiString QueryParameter_(const AnsiString &query, const AnsiString &name);

      HttpResponse HandleListQuarantine_();
      HttpResponse HandleQuarantineRelease_(__int64 id);
      HttpResponse HandleQuarantineDelete_(__int64 id);
      HttpResponse HandleListAliases_(const String &domainName);
      HttpResponse HandleListIpRanges_();
      HttpResponse HandleCreateIpRange_(const AnsiString &requestBody);
      HttpResponse HandleDeleteIpRange_(__int64 rangeId);
      HttpResponse HandleListLists_(const String &domainName);
      HttpResponse HandleCreateList_(const String &domainName, const AnsiString &requestBody);
      HttpResponse HandleDeleteList_(const String &address);
      HttpResponse HandleListCertificates_();
      HttpResponse HandleDkim_(const String &domainName);
      HttpResponse HandleListRules_();
      HttpResponse HandleListLogs_();
      HttpResponse HandleLogTail_(const AnsiString &name, const AnsiString &query);
      HttpResponse HandleBackupStart_();
      HttpResponse HandleBackupStatus_();
      HttpResponse HandleSettings_();
      HttpResponse HandleServerReinitialize_();
      static void ReinitializeAfterTheAnswer_();

      // Wave 162: the write surface. Each group lives in its own translation
      // unit beside this one - RestApiSettings.cpp, RestApiRules.cpp,
      // RestApiCertificates.cpp, RestApiRoutes.cpp - and each contributes its
      // paths to the OpenAPI document through the OpenApi*Paths_ function,
      // which returns either nothing or a run of entries each beginning with
      // a comma, appended after the last path HandleOpenApi_ writes itself.
      HttpResponse HandleSettingsPut_(const AnsiString &requestBody);
      HttpResponse HandleSettingsAntiSpam_();
      HttpResponse HandleSettingsAntiSpamPut_(const AnsiString &requestBody);
      HttpResponse HandleSettingsLogging_();
      HttpResponse HandleSettingsLoggingPut_(const AnsiString &requestBody);
      static AnsiString OpenApiSettingsPaths_();
      HttpResponse HandleCreateRule_(const AnsiString &requestBody);
      HttpResponse HandleUpdateRule_(__int64 ruleId, const AnsiString &requestBody);
      HttpResponse HandleDeleteRule_(__int64 ruleId);
      static AnsiString OpenApiRulesPaths_();
      // One rule as every rule route emits it: the listing, the create and
      // the replace answer with the same document (defined in RestApiRules.cpp).
      static AnsiString RuleEntryJson_(const std::shared_ptr<Rule> &rule);
      HttpResponse HandleCreateCertificate_(const AnsiString &requestBody);
      HttpResponse HandleDeleteCertificate_(__int64 certificateId);
      HttpResponse HandleListPorts_();
      HttpResponse HandleCreatePort_(const AnsiString &requestBody);
      HttpResponse HandleUpdatePort_(__int64 portId, const AnsiString &requestBody);
      HttpResponse HandleDeletePort_(__int64 portId);
      static AnsiString OpenApiCertificatesPaths_();
      HttpResponse HandleListRoutes_();
      HttpResponse HandleCreateRoute_(const AnsiString &requestBody);
      HttpResponse HandleUpdateRoute_(__int64 routeId, const AnsiString &requestBody);
      HttpResponse HandleDeleteRoute_(__int64 routeId);
      HttpResponse HandleCreateAlias_(const String &domainName, const AnsiString &requestBody);
      HttpResponse HandleDeleteAlias_(const String &address);
      HttpResponse HandleUpdateAccount_(const Caller &caller, const String &address, const AnsiString &requestBody);
      static AnsiString OpenApiRoutesPaths_();
      HttpResponse HandleArchiveSearch_(const std::vector<String> &domains, const AnsiString &query);
      HttpResponse HandleArchiveGet_(const std::vector<String> &domains, __int64 archiveId);
      HttpResponse HandleArchiveHold_(const std::vector<String> &domains, __int64 archiveId, bool hold);
      static bool GetJsonBoolValue_(const AnsiString &json, const AnsiString &key, bool defaultValue);
      static std::vector<AnsiString> GetJsonStringArray_(const AnsiString &json, const AnsiString &key);
      static bool IsSafeLogName_(const AnsiString &name);
      HttpResponse HandleOpenApi_();

      // True if the id names a message that is really in the delivery queue.
      static bool QueueMessageExists_(__int64 messageId);

      static AnsiString GetRequestBody_(const AnsiString &request);
      static AnsiString GetJsonStringValue_(const AnsiString &json, const AnsiString &key);
      static AnsiString JsonEscape_(const AnsiString &value);

      std::shared_ptr<HttpServer> server_;
      bool running_;
      bool use_tls_;
      String certificate_file_;
      String private_key_file_;
   };
}
