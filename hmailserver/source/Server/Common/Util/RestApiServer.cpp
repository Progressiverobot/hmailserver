// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// REST administration API over HTTPS. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "RestApiServer.h"
#include "../Application/MetricsHistoryTask.h"
#include "ServerStatus.h"
#include "OtelTracer.h"
#include "OtelTraceContext.h"
#include "Crypt.h"
#include "Totp.h"
#include "AccountLogon.h"
#include "AcmeClient.h"
#include "WebServicesServer.h"
#include "../AntiSpam/QuarantineStore.h"
#include "../BO/Aliases.h"
#include "../BO/Alias.h"
#include "../BO/SecurityRanges.h"
#include "../BO/SecurityRange.h"
#include "../BO/DistributionLists.h"
#include "../BO/DistributionList.h"
#include "../BO/DistributionListRecipients.h"
#include "../BO/DistributionListRecipient.h"
#include "../BO/Rules.h"
#include "../BO/Rule.h"
#include "../BO/RuleCriterias.h"
#include "../BO/RuleCriteria.h"
#include "../BO/RuleActions.h"
#include "../BO/RuleAction.h"
#include "../Persistence/PersistentSecurityRange.h"
#include "../Persistence/PersistentDistributionList.h"
#include "../Persistence/PersistentDistributionListRecipient.h"
#include "../Application/ObjectCache.h"
#include "../Application/BackupManager.h"
#include "../Application/IniFileSettings.h"
#include "../TCPIP/IPAddress.h"
#include "../../SMTP/SMTPConfiguration.h"
#include "../../IMAP/IMAPConfiguration.h"
#include "../../POP3/POP3Configuration.h"
#include "FileInfo.h"
#include <fstream>
#include "FileUtilities.h"
#include "Time.h"
#include "Encoding/Base64.h"
#include "Hashing/HashCreator.h"

#include "../BO/Domains.h"
#include "../BO/Domain.h"
#include "../BO/Accounts.h"
#include "../BO/Account.h"
#include "../BO/SSLCertificates.h"
#include "../BO/SSLCertificate.h"
#include "../BO/TCPIPPort.h"
#include "../BO/TCPIPPorts.h"
#include "../BO/Message.h"
#include "../Persistence/PersistentAccount.h"
#include "../Persistence/PersistentMessage.h"
#include "../Persistence/PersistenceMode.h"
#include "../TCPIP/SocketConstants.h"
#include "../TCPIP/SslContextInitializer.h"
#include "../../SMTP/DeliveryQueue.h"

#include <ws2tcpip.h>

#include <algorithm>
#include <cstring>
#include <mutex>

#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/rand.h>
#include <openssl/crypto.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // size_t rather than int so that every comparison against a std::string
      // size below is between like types. /W3 /WX would otherwise turn one of
      // them into a signed/unsigned build failure the first time it is written
      // without a constant on the signed side.
      const size_t MaxRequestSize = 64 * 1024;
      const DWORD SocketTimeoutMilliseconds = 10000;

      // Total time allowed to read one request, across all reads.
      const ULONGLONG RequestReadTimeoutMilliseconds = 30000;

      // hm_messages.messagetype for a message held for ETRN. There is no
      // Message::State enumerator for it, but GET /api/v1/queue lists it
      // alongside the ordinary Message::Delivering rows, so the retry and
      // delete endpoints have to accept it too.
      const int EtrnHeldMessageType = 3;

      SSL_CTX *tls_context = nullptr;

      // Owns the context that tls_context points into. This listener speaks to
      // OpenSSL directly - blocking sockets and SSL_new - but its configuration now
      // comes from SslContextInitializer, which takes a boost::asio::ssl::context.
      // The boost object owns the underlying SSL_CTX and frees it in its destructor,
      // so it has to outlive every SSL session made from it, and Stop() must not
      // call SSL_CTX_free.
      std::shared_ptr<boost::asio::ssl::context> tls_context_owner;

      // API key store layout. One section per key; the section suffix is the
      // key id, which is what DELETE /api/v1/apikeys/<id> revokes.
      const TCHAR *ApiKeySectionPrefix = _T("Key.");

      // Presented tokens carry this prefix so that a leaked string is
      // recognisable as an hMailServer API key (and so that an administrator
      // pasting a password by mistake fails fast).
      const char *ApiKeyTokenPrefix = "hmapi_";

      // 32 bytes of entropy in the secret; 8 in the id.
      const int ApiKeySecretBytes = 32;
      const int ApiKeyIdBytes = 8;

      // Length of a SHA-256 digest written as lower-case hex.
      const int ApiKeyHashHexLength = 64;

      // Used when a create request does not name an expiry. A key with no
      // expiry at all is exactly the property that makes the administrator
      // password dangerous, so "unlimited" is not an option.
      const int ApiKeyDefaultLifetimeDays = 90;

      // The only Scope value that widens a key beyond reading. Compared
      // case-insensitively; anything else - including nothing at all - leaves
      // the key read-only.
      const TCHAR *ApiKeyScopeFull = _T("full");
      const TCHAR *ApiKeyScopeReadOnly = _T("readonly");

      // The same two words narrow, for the JSON bodies (Format's %hs) and for
      // reading the create request. Spelled once each so the store, the request
      // and the response can never disagree about them.
      const char *ApiKeyScopeFullNarrow = "full";
      const char *ApiKeyScopeReadOnlyNarrow = "readonly";

      // ------------------------------------------------------------------
      // Per-credential request budget.
      //
      // The refused-source set below bounds *failed* authentication. Nothing
      // bounded successful requests at all: one valid credential could drive
      // this listener as fast as its single worker thread could answer, and
      // every request costs a key-store read plus, on most routes, queries on
      // the same database connection pool that SMTP, IMAP and POP3 use. So a
      // leaked key was not only administrative access, it was a lever on mail
      // delivery.
      //
      // Per credential rather than per source address, deliberately, and in
      // both directions:
      //
      //  - a key is the thing whose budget we want to cap, and an address is
      //    not: rotating source addresses must not multiply what one leaked key
      //    can spend;
      //  - two honest keys behind one NAT must not be able to starve each
      //    other, which is what a per-address budget would do.
      //
      // A fixed window, not a sliding one: when a window is older than
      // RateWindowMilliseconds its entry is dropped and the next request starts
      // a fresh one. That makes Retry-After exact - a caller that waits the
      // window out is certainly inside a new window - at the cost of allowing
      // up to twice the budget across a window boundary, which is the right
      // trade for a management API.
      //
      // A short window and a generous budget, on purpose. This is not a throttle
      // meant to shape normal use; it is a ceiling that stops one credential
      // turning into a denial of service against mail. Twenty requests a second
      // sustained is far more than any administrator or dashboard produces
      // (a status poll once a second is 5% of it) and far less than the thread
      // can serve, so what it removes is only the abusive case. The short window
      // also means an honest client that briefly overshoots is forgiven in
      // seconds rather than being locked out for a minute.
      //
      // Nothing here fires on the shipped default configuration: the listener
      // does not run unless RestApiPort is set.
      // ------------------------------------------------------------------
      const int MaxRequestsPerWindowPerCredential = 200;
      const ULONGLONG RateWindowMilliseconds = 10 * 1000;

      // Only the administrator can mint keys, so this cap is not reachable by
      // an attacker. It is here so that a store with thousands of keys cannot
      // turn this into unbounded memory.
      const size_t MaxRateLimitedCredentials = 64;

      struct CredentialRate
      {
         AnsiString identity;
         ULONGLONG window_started_at;
         int count;
      };

      std::mutex credential_rate_mutex;
      std::vector<CredentialRate> credential_rates;

      // ------------------------------------------------------------------
      // The refused-source set.
      //
      // An IP range created by auto-ban is only ever consulted through
      // TCPConnection::GetSecurityRange(). This listener uses raw sockets and
      // consults nothing, so a ban created from a REST authentication failure
      // would never refuse a single REST request.
      //
      // That is not just a missing feature, it is an amplifier. Every
      // MaxInvalidLogonAttempts failures (default 3) AccountLogon inserts
      // another hm_securityranges row, and GetIPRangeName_ pays a run of
      // PersistentSecurityRange::Exists SELECTs looking for a name nothing has
      // used yet. On the mail ports that self-limits, because the very next
      // connection from the banned address is refused before AUTH. Here
      // nothing stopped it: a sustained flood would keep the single REST
      // worker thread doing database round-trips and would grow
      // hm_securityranges at a third of the request rate for AutoBanMinutes.
      // PersistentSecurityRange::ReadMatchingIP is an uncached SELECT on every
      // SMTP, IMAP and POP3 connection, so that growth is paid by every mail
      // connection on the shared database. Slowing mail down is worse than the
      // brute-force attempt being reacted to.
      //
      // So the 'disconnect' flag AccountLogon returns - set exactly when this
      // failure was the one that tripped the ban - is recorded here, and Run_
      // refuses the address before the request is read, before the TLS
      // handshake, and before anything touches the database.
      //
      // Deliberately in-process and deliberately small:
      //
      //  - Bounded at MaxRefusedAddresses. When it is full the entry that
      //    expires soonest is replaced, so an attacker rotating source
      //    addresses churns the set rather than growing it, and the addresses
      //    kept are the ones that offended most recently.
      //  - Every entry carries its own deadline and expired entries are dropped
      //    whenever the set is walked, so it empties itself even if no further
      //    request ever arrives, and nothing here can refuse an administrator
      //    for longer than RefusedAddressMinutes. An entry in force cannot be
      //    renewed either: while an address is refused its connections are
      //    closed before authentication, so no further failure is registered
      //    for it and no later deadline can be written. The window is a
      //    ceiling, not a rolling one.
      //  - A fixed short window rather than AutoBanMinutes. It only has to be
      //    long enough to collapse a flood - one ban per address per window
      //    instead of one per three requests - and an administrator who
      //    mistyped a password should not lose the management interface for the
      //    hour that the shipped AutoBanMinutes grants. It also means
      //    AutoBanMinutes=0 ("disconnect, but do not block") is honoured as a
      //    brief refusal rather than ignored, which is what stops the flood of
      //    logon-failure rows that setting would otherwise still pay for.
      //
      // Nothing here fires on the shipped default configuration: the listener
      // does not run unless RestApiPort is set, and an address only enters the
      // set after it has failed authentication MaxInvalidLogonAttempts times.
      // ------------------------------------------------------------------
      const int RefusedAddressMinutes = 5;
      const ULONGLONG RefusedAddressMilliseconds = static_cast<ULONGLONG>(RefusedAddressMinutes) * 60 * 1000;
      const size_t MaxRefusedAddresses = 256;

      // The address is held as its numeric halves rather than as text, so that
      // the check on the accept path allocates nothing at all.
      struct RefusedAddress
      {
         int family;
         unsigned __int64 address_high;
         unsigned __int64 address_low;
         ULONGLONG expires_at;
      };

      std::mutex refused_addresses_mutex;
      std::vector<RefusedAddress> refused_addresses;

      bool IsSameAddress(const RefusedAddress &entry, int family, unsigned __int64 high, unsigned __int64 low)
      {
         return entry.family == family && entry.address_high == high && entry.address_low == low;
      }

      AnsiString BytesToLowerHex(const unsigned char *data, int length)
      {
         AnsiString result;
         char buffer[3];

         for (int i = 0; i < length; i++)
         {
            sprintf_s(buffer, sizeof(buffer), "%02x", data[i]);
            result += buffer;
         }

         return result;
      }

      // True only for a lower-case hex string of exactly the expected length.
      // Guards the stored hash: a truncated or hand-mangled value must be
      // rejected outright rather than compared against.
      bool IsLowerHex(const AnsiString &value, int expectedLength)
      {
         if (value.GetLength() != expectedLength)
            return false;

         for (int i = 0; i < value.GetLength(); i++)
         {
            char character = value[i];
            bool isDigit = character >= '0' && character <= '9';
            bool isHexLetter = character >= 'a' && character <= 'f';

            if (!isDigit && !isHexLetter)
               return false;
         }

         return true;
      }

      // Constant-time equality. A byte-by-byte comparison that returns on the
      // first difference tells an attacker how much of a guessed token was
      // correct, which turns a 256-bit secret into a per-byte search.
      bool ConstantTimeEquals(const AnsiString &left, const AnsiString &right)
      {
         if (left.GetLength() != right.GetLength() || left.GetLength() == 0)
            return false;

         return CRYPTO_memcmp(left.c_str(), right.c_str(), (size_t) left.GetLength()) == 0;
      }

      // Lower-case hex SHA-256 of the presented token. See LoadKeys_ for why
      // this, rather than one of the password KDFs, is the correct primitive.
      AnsiString HashApiKeyToken(const AnsiString &token)
      {
         HashCreator hasher(HashCreator::SHA256);
         return hasher.GenerateHashNoSalt(token, HashCreator::hex);
      }

      // Parses "<address>/<prefix>" into an inclusive address range. Handles
      // both families; returns false for anything that is not a valid CIDR.
      bool ParseCidr(const AnsiString &text, IPAddress &lower, IPAddress &upper)
      {
         int slashPosition = text.Find("/");
         if (slashPosition <= 0)
            return false;

         AnsiString addressPart = text.Mid(0, slashPosition);
         AnsiString prefixPart = text.Mid(slashPosition + 1);

         if (prefixPart.IsEmpty() || prefixPart.GetLength() > 3)
            return false;

         for (int i = 0; i < prefixPart.GetLength(); i++)
         {
            if (prefixPart[i] < '0' || prefixPart[i] > '9')
               return false;
         }

         int prefix = atoi(prefixPart.c_str());

         IPAddress base;
         if (!base.TryParse(addressPart, false))
            return false;

         if (base.GetType() == IPAddress::IPV4)
         {
            if (prefix > 32)
               return false;

            const unsigned __int64 all = 0xFFFFFFFFULL;

            unsigned __int64 mask = (prefix == 0) ? 0 : ((all << (32 - prefix)) & all);
            unsigned __int64 network = base.GetAddress1() & mask;

            lower = IPAddress(static_cast<__int64>(network));
            upper = IPAddress(static_cast<__int64>(network | (all & ~mask)));
            return true;
         }

         if (base.GetType() == IPAddress::IPV6)
         {
            if (prefix > 128)
               return false;

            const unsigned __int64 all = 0xFFFFFFFFFFFFFFFFULL;

            unsigned __int64 highMask = 0;
            unsigned __int64 lowMask = 0;

            // Shifting a 64-bit value by 64 is undefined, so the boundary
            // prefixes (0, 64 and 128) are spelled out rather than computed.
            if (prefix == 0)
            {
               highMask = 0;
               lowMask = 0;
            }
            else if (prefix < 64)
            {
               highMask = all << (64 - prefix);
               lowMask = 0;
            }
            else if (prefix == 64)
            {
               highMask = all;
               lowMask = 0;
            }
            else if (prefix < 128)
            {
               highMask = all;
               lowMask = all << (128 - prefix);
            }
            else
            {
               highMask = all;
               lowMask = all;
            }

            unsigned __int64 networkHigh = base.GetAddress1() & highMask;
            unsigned __int64 networkLow = base.GetAddress2() & lowMask;

            lower = IPAddress(static_cast<__int64>(networkHigh), static_cast<__int64>(networkLow));
            upper = IPAddress(static_cast<__int64>(networkHigh | ~highMask),
                              static_cast<__int64>(networkLow | ~lowMask));
            return true;
         }

         return false;
      }

      // Parses an API key's allowed-source restriction into an inclusive
      // address range. Accepted forms: a single address, "lower-upper", or
      // CIDR. Anything else is refused - a restriction we cannot understand
      // must not silently become "any source".
      bool ParseSourceRestriction(const AnsiString &text, IPAddress &lower, IPAddress &upper)
      {
         AnsiString restriction = text;
         restriction.Trim();

         if (restriction.IsEmpty())
            return false;

         if (restriction.Find("/") >= 0)
            return ParseCidr(restriction, lower, upper);

         int dashPosition = restriction.Find("-");
         if (dashPosition > 0)
         {
            AnsiString lowerText = restriction.Mid(0, dashPosition);
            AnsiString upperText = restriction.Mid(dashPosition + 1);

            lowerText.Trim();
            upperText.Trim();

            if (!lower.TryParse(lowerText, false) || !upper.TryParse(upperText, false))
               return false;

            return lower.GetType() == upper.GetType();
         }

         if (!lower.TryParse(restriction, false))
            return false;

         upper = lower;
         return true;
      }

      // Parses a strictly numeric message id.
      bool ParseQueueId(const AnsiString &value, __int64 &id)
      {
         if (value.IsEmpty() || value.GetLength() > 18)
            return false;

         for (int i = 0; i < value.GetLength(); i++)
         {
            if (value[i] < '0' || value[i] > '9')
               return false;
         }

         id = _atoi64(value.c_str());
         return id > 0;
      }

      // Why an enum and not a bool: an oversized request and a malformed one
      // are different answers (413 and 400), and a caller that cannot tell them
      // apart is the reason the oversize case used to be answered by silently
      // truncating the body. See the totalExpected check below.
      enum RequestReadResult
      {
         RequestReadOk = 0,
         RequestReadMalformed = 1,
         RequestReadTooLarge = 2
      };

      // Reads an HTTP request (headers + body according to Content-Length)
      // using the supplied read function.
      template <typename ReadFunction>
      RequestReadResult ReadHttpRequest(ReadFunction readSome, AnsiString &request)
      {
         std::string data;
         char buffer[4096];

         size_t headerEnd = std::string::npos;

         // Absolute wall-clock ceiling for reading one request. The per-socket
         // SO_RCVTIMEO only bounds a single read; without a total deadline a
         // client that dribbles a byte at a time just under that timeout can
         // occupy the (single) REST worker thread indefinitely and also stall
         // shutdown, since Stop() waits for the handler to return.
         const ULONGLONG deadline = GetTickCount64() + RequestReadTimeoutMilliseconds;

         for (;;)
         {
            if (GetTickCount64() >= deadline)
               return RequestReadMalformed;

            int bytesRead = readSome(buffer, sizeof(buffer));
            if (bytesRead <= 0)
               break;

            data.append(buffer, bytesRead);

            headerEnd = data.find("\r\n\r\n");

            if (headerEnd == std::string::npos)
            {
               // Header block still not terminated. Bounded here rather than by
               // the loop condition, so that "the headers alone are bigger than
               // the cap" is an oversized request rather than one that is
               // quietly treated as having no body.
               if (data.size() >= MaxRequestSize)
                  return RequestReadTooLarge;

               continue;
            }

            // Determine expected body length.
            size_t contentLength = 0;

            std::string headersLower = data.substr(0, headerEnd);
            for (size_t i = 0; i < headersLower.size(); i++)
               headersLower[i] = (char) tolower((unsigned char) headersLower[i]);

            size_t lengthPosition = headersLower.find("content-length:");
            if (lengthPosition != std::string::npos)
               contentLength = strtoul(headersLower.c_str() + lengthPosition + 15, nullptr, 10);

            // Headers, terminator and body against the one cap.
            //
            // The bug this replaces: the cap was the loop condition and only
            // the declared body length was measured against it, so a request
            // whose headers and body *together* exceeded 64 KB left the loop
            // with the body cut short and was then processed as if it were
            // complete.
            //
            // A truncated JSON body is not a syntax error to GetJsonStringValue_,
            // which looks each field up independently: whichever fields survived
            // the cut are honoured and the rest read as absent. So a
            // POST /api/v1/apikeys whose label came first created a key from a
            // request that was never fully received, and a create-account body
            // could lose its password the same way. One answer now, 413,
            // whichever half is oversized.
            size_t totalExpected = headerEnd + 4 + contentLength;

            if (contentLength > MaxRequestSize || totalExpected > MaxRequestSize)
               return RequestReadTooLarge;

            if (data.size() >= totalExpected)
               break;
         }

         if (headerEnd == std::string::npos)
            return RequestReadMalformed;

         // A NUL anywhere in the request refuses it.
         //
         // There is no legitimate NUL in an HTTP request line, a header block or
         // a JSON body, and `request = data.c_str()` - what this replaces -
         // truncated the request at the first one. So a client could cut its own
         // request short at a byte of its choosing and still have the remains
         // processed, and every length the reader had just checked described a
         // different string from the one the parser saw.
         if (data.find('\0') != std::string::npos)
            return RequestReadMalformed;

         request.assign(data.c_str(), data.size());
         return RequestReadOk;
      }

      // Parses a bind address that is an IPv4 or IPv6 literal into a sockaddr
      // ready for bind(), choosing the family by the presence of a colon - the
      // same test IPAddress::TryParse uses, and one no IPv4 literal can pass.
      // False when the address is neither. A scoped link-local literal
      // ("fe80::1%3") is not accepted, because inet_pton does not parse scope
      // ids; it fails here and is reported as an invalid bind address.
      bool ParseBindAddress(const AnsiString &narrowBindAddress, int port,
                            sockaddr_storage &address, int &addressLength)
      {
         if (narrowBindAddress.Find(":") >= 0)
         {
            sockaddr_in6 address6 = {};
            address6.sin6_family = AF_INET6;
            address6.sin6_port = htons(static_cast<unsigned short>(port));

            if (inet_pton(AF_INET6, narrowBindAddress.c_str(), &address6.sin6_addr) != 1)
               return false;

            memcpy(&address, &address6, sizeof(address6));
            addressLength = static_cast<int>(sizeof(address6));
            return true;
         }

         sockaddr_in address4 = {};
         address4.sin_family = AF_INET;
         address4.sin_port = htons(static_cast<unsigned short>(port));

         if (inet_pton(AF_INET, narrowBindAddress.c_str(), &address4.sin_addr) != 1)
            return false;

         memcpy(&address, &address4, sizeof(address4));
         addressLength = static_cast<int>(sizeof(address4));
         return true;
      }

      // Windows creates AF_INET6 sockets with IPV6_V6ONLY on, so a listener
      // bound to :: would accept IPv6 clients only - and an operator who binds
      // "any" and then finds IPv4 clients refused would have nothing to go on.
      // This listener has exactly one bind-address setting (unlike the mail
      // protocols, which take one port row per address), so :: is the only way
      // to serve both families and is therefore made dual-stack. A specific
      // IPv6 address is left alone: it can only ever accept IPv6, and the
      // option would be dead weight. Returns false only when the option was
      // needed and could not be set, so the caller can say IPv4 will not be
      // served rather than leave it to be discovered.
      bool TryEnableDualStack(SOCKET listenSocket, const sockaddr_storage &address)
      {
         if (address.ss_family != AF_INET6)
            return true;

         const sockaddr_in6 *address6 = reinterpret_cast<const sockaddr_in6*>(&address);

         if (!IN6_IS_ADDR_UNSPECIFIED(&address6->sin6_addr))
            return true;

         DWORD v6Only = 0;

         return setsockopt(listenSocket, IPPROTO_IPV6, IPV6_V6ONLY,
            reinterpret_cast<const char*>(&v6Only), sizeof(v6Only)) != SOCKET_ERROR;
      }

      // Renders an accepted peer's address as text. On the dual-stack listener
      // an IPv4 client arrives as an IPv4-mapped IPv6 address (::ffff:a.b.c.d),
      // which is unmapped to its IPv4 form here - not cosmetics: every consumer
      // of the result matches on address family. The auto-ban exclusion
      // compares against "127.0.0.1", an AllowedFrom restriction written as an
      // IPv4 address, range or CIDR refuses any IPv6 peer outright, and the
      // security ranges the auto-ban creates are IPv4 ranges. A v4 client
      // dressed as v6 would silently match none of them - including the
      // loopback exclusion, so the server could auto-ban its own loopback.
      bool FormatPeerAddress(const sockaddr_storage &peer, char *buffer, size_t bufferSize)
      {
         if (peer.ss_family == AF_INET)
         {
            const sockaddr_in *peer4 = reinterpret_cast<const sockaddr_in*>(&peer);
            return inet_ntop(AF_INET, &peer4->sin_addr, buffer, bufferSize) != nullptr;
         }

         if (peer.ss_family == AF_INET6)
         {
            const sockaddr_in6 *peer6 = reinterpret_cast<const sockaddr_in6*>(&peer);

            if (IN6_IS_ADDR_V4MAPPED(&peer6->sin6_addr))
            {
               in_addr mapped = {};
               memcpy(&mapped, peer6->sin6_addr.s6_addr + 12, sizeof(mapped));
               return inet_ntop(AF_INET, &mapped, buffer, bufferSize) != nullptr;
            }

            return inet_ntop(AF_INET6, &peer6->sin6_addr, buffer, bufferSize) != nullptr;
         }

         return false;
      }

      // "host:port" for log lines, with an IPv6 literal in brackets -
      // "[::1]:8080" - because "::1:8080" reads as a different IPv6 address.
      String FormatEndpoint(const String &bind_address, int port)
      {
         String result;

         if (bind_address.Find(_T(":")) >= 0)
            result.Format(_T("[%s]:%d"), bind_address.c_str(), port);
         else
            result.Format(_T("%s:%d"), bind_address.c_str(), port);

         return result;
      }
   }

   RestApiServer::RestApiServer() :
      listen_socket_(INVALID_SOCKET),
      running_(false),
      use_tls_(false)
   {

   }

   RestApiServer::~RestApiServer()
   {
      Stop();
   }

   bool
   RestApiServer::Start(const String &bind_address, int port, const String &certificate_file, const String &private_key_file)
   {
      if (running_)
         return true;

      if (IniFileSettings::Instance()->GetAdministratorPassword().IsEmpty())
      {
         LOG_APPLICATION("RestApi: Refusing to start - the administrator password is not set.");
         return false;
      }

      use_tls_ = !certificate_file.IsEmpty() && !private_key_file.IsEmpty();
      certificate_file_ = certificate_file;
      private_key_file_ = private_key_file;

      // ::1 carries exactly the guarantee 127.0.0.1 does - only a process on
      // this machine can connect - so it satisfies the TLS exemption on the
      // same grounds. Exact literals only, as before: this is a security gate,
      // and widening it (127/8, mapped forms) is a separate decision.
      bool isLoopback = bind_address == _T("127.0.0.1") || bind_address == _T("localhost") ||
                        bind_address == _T("::1");

      if (!use_tls_ && !isLoopback)
      {
         LOG_APPLICATION("RestApi: Refusing to start - TLS certificate is required unless bound to 127.0.0.1 or ::1. Set RestApiCertificateFile and RestApiPrivateKeyFile.");
         return false;
      }

      if (use_tls_)
      {
         // Why this goes through SslContextInitializer rather than building its own
         // SSL_CTX: that function is the single place the mail protocols get their
         // TLS configuration from - the cipher list, the enabled protocol versions,
         // the server-preference and ChaCha options, the DH parameters and the
         // key-exchange group list, which since August 2026 carries the hybrid
         // post-quantum KEMs from [Settings] TlsKeyExchangeGroups.
         //
         // What this listener did instead is worth stating, because it is the shape
         // the problem takes every time: it set a TLS 1.2 floor and loaded the
         // certificate, and then took OpenSSL's defaults for everything else. So the
         // configured cipher list did not apply to it, the option mask did not apply
         // to it, and the post-quantum groups did not apply to it - an administrator
         // who set TlsKeyExchangeGroups and then served the API over TLS got
         // classical-only key exchange, with nothing anywhere saying so.
         //
         // The bridge is deliberately thin: SslContextInitializer wants an
         // SSLCertificate, so one is built here from the two configured paths rather
         // than looked up in the database - the REST listener's certificate is
         // RestApiCertificateFile/RestApiPrivateKeyFile and is not one of the
         // certificates bound to a mailbox port. Nothing about the TLS configuration
         // is restated locally, so nothing local can drift from the mail protocols
         // again.
         try
         {
            auto certificate = std::shared_ptr<SSLCertificate>(new SSLCertificate());

            certificate->SetName(_T("RestApi"));
            certificate->SetCertificateFile(certificate_file);
            certificate->SetPrivateKeyFile(private_key_file);

            auto context = std::shared_ptr<boost::asio::ssl::context>(
               new boost::asio::ssl::context(boost::asio::ssl::context::sslv23));

            if (!SslContextInitializer::InitServer(*context, certificate, bind_address, port))
            {
               // InitServer has already reported the specific failure - an unreadable
               // certificate file, a private key that does not match - as HM5113.
               // This line records only the consequence for this listener.
               LOG_APPLICATION("RestApi: Refusing to start - the shared TLS configuration could not be applied to the configured certificate.");
               return false;
            }

            tls_context_owner = context;
            tls_context = context->native_handle();

            // The one thing that is deliberately *not* taken from the shared
            // configuration. This listener enforced a TLS 1.2 floor before, and the
            // shared option mask is driven by the [Settings] protocol toggles, which
            // an administrator may well have opened up to TLS 1.0 for an ancient mail
            // client. That argument does not extend to an HTTP API - there is no
            // 2008-era REST client to keep working - so the floor stays, applied after
            // InitServer so it can only tighten what the shared configuration allows.
            SSL_CTX_set_min_proto_version(tls_context, TLS1_2_VERSION);
         }
         catch (...)
         {
            // Constructing the context can throw. This is the startup path, where an
            // escape would take the whole service down before any listener is up.
            LOG_APPLICATION("RestApi: Refusing to start - an exception was raised while preparing TLS.");
            return false;
         }
      }

      AnsiString narrowBindAddress = bind_address == _T("localhost") ? AnsiString("127.0.0.1") : AnsiString(bind_address);

      sockaddr_storage address = {};
      int addressLength = 0;

      if (!ParseBindAddress(narrowBindAddress, port, address, addressLength))
      {
         LOG_APPLICATION("RestApi: Invalid bind address: " + bind_address);
         return false;
      }

      listen_socket_ = socket(address.ss_family, SOCK_STREAM, IPPROTO_TCP);
      if (listen_socket_ == INVALID_SOCKET)
         return false;

      BOOL reuseAddress = TRUE;
      setsockopt(listen_socket_, SOL_SOCKET, SO_REUSEADDR, (const char*) &reuseAddress, sizeof(reuseAddress));

      if (!TryEnableDualStack(listen_socket_, address))
         LOG_APPLICATION("RestApi: IPV6_V6ONLY could not be cleared for the :: bind, so this listener will accept IPv6 connections only. Bind 0.0.0.0 instead if IPv4 is what is needed.");

      if (bind(listen_socket_, reinterpret_cast<const sockaddr*>(&address), addressLength) == SOCKET_ERROR ||
          listen(listen_socket_, 5) == SOCKET_ERROR)
      {
         String message;
         message.Format(_T("RestApi: Failed to bind to %s."), FormatEndpoint(bind_address, port).c_str());
         LOG_APPLICATION(message);

         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
         return false;
      }

      running_ = true;
      worker_ = std::thread(&RestApiServer::Run_, this);

      String message;
      message.Format(_T("RestApi: Listening on %s (%s)."), FormatEndpoint(bind_address, port).c_str(), use_tls_ ? _T("https") : _T("http, loopback only"));
      LOG_APPLICATION(message);

      return true;
   }

   void
   RestApiServer::Stop()
   {
      if (!running_)
         return;

      running_ = false;

      if (listen_socket_ != INVALID_SOCKET)
      {
         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
      }

      if (worker_.joinable())
         worker_.join();

      // After the join, so there is no reader left to race with. A stopped
      // listener holds no refusals and no request counts: whatever was in force
      // is dropped rather than surviving into the next Start().
      ClearRefusedAddresses_();
      ClearRequestRates_();

      // Deliberately no SSL_CTX_free: the boost context owns the SSL_CTX and frees it
      // in its own destructor, so freeing it here as well would be a double free the
      // next time the listener is stopped. Released after the join above, so no
      // session is still using it.
      tls_context = nullptr;
      tls_context_owner.reset();
   }

   void
   RestApiServer::Run_()
   {
      for (;;)
      {
         // The peer address is captured here rather than discarded: an
         // authentication failure has to be attributable to an IP for the
         // auto-ban machinery, and an API key may be restricted to a source
         // address or range. sockaddr_storage, because the listener may be an
         // AF_INET or an AF_INET6 socket and this has to hold either family's
         // address.
         sockaddr_storage peer = {};
         int peerLength = sizeof(peer);

         SOCKET clientSocket = accept(listen_socket_, reinterpret_cast<sockaddr*>(&peer), &peerLength);

         if (clientSocket == INVALID_SOCKET)
         {
            if (!running_)
               return;

            continue;
         }

         // Everything from here on is inside the try. This is the top frame of
         // the worker thread, so anything that escapes it is std::terminate - a
         // dead server - and leaks clientSocket on the way out. Constructing an
         // IPAddress and parsing it can only realistically fail by bad_alloc,
         // but the cost of being certain is one level of indentation.
         try
         {
            IPAddress peerAddress;

            char peerText[INET6_ADDRSTRLEN] = {};
            if (FormatPeerAddress(peer, peerText, sizeof(peerText)))
            {
               // A parse failure leaves peerAddress as the default 0.0.0.0,
               // which is refused by every non-empty source restriction and is
               // never auto-banned. Failing closed is the right direction here.
               peerAddress.TryParse(AnsiString(peerText), false);
            }

            // An address that has already tripped the auto-ban is refused here:
            // before the request is read, before the TLS handshake, and - the
            // point of the exercise - before anything touches the database. A
            // refused connection is closed without a response, exactly as a
            // security range refuses an SMTP connection before its banner.
            if (IsRefusedAddress_(peerAddress))
            {
               LOG_DEBUG("RestApiServer: Refused a connection from " + String(peerAddress.ToString()) +
                  ", which has recently failed authentication repeatedly.");

               closesocket(clientSocket);
               continue;
            }

            HandleClient_(clientSocket, peerAddress);
         }
         catch (...)
         {
            closesocket(clientSocket);
         }
      }
   }

   void
   RestApiServer::HandleClient_(SOCKET client_socket, const IPAddress &peer_address)
   {
      DWORD timeout = SocketTimeoutMilliseconds;
      setsockopt(client_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(client_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      // One place that turns a read outcome into a response, shared by the TLS
      // and plaintext paths below, so the two cannot answer the same condition
      // differently.
      auto answer = [&](RequestReadResult readResult, const AnsiString &httpRequest) -> AnsiString
      {
         if (readResult == RequestReadOk)
            return ProcessRequest_(httpRequest, peer_address);

         // Says nothing about the cap. An administrator hitting this reads the
         // documentation; a stranger measuring it learns nothing useful.
         if (readResult == RequestReadTooLarge)
            return BuildResponse_(413, "{\"error\":\"request too large\"}");

         return BuildResponse_(400, "{\"error\":\"malformed request\"}");
      };

      if (use_tls_)
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

            RequestReadResult readResult = ReadHttpRequest(
               [&](char *buffer, int size) { return SSL_read(tlsSession, buffer, size); },
               request);

            AnsiString response = answer(readResult, request);

            SSL_write(tlsSession, response.c_str(), response.GetLength());
            SSL_shutdown(tlsSession);
         }

         SSL_free(tlsSession);
         closesocket(client_socket);
         return;
      }

      AnsiString request;

      RequestReadResult readResult = ReadHttpRequest(
         [&](char *buffer, int size) { return recv(client_socket, buffer, size, 0); },
         request);

      AnsiString response = answer(readResult, request);

      send(client_socket, response.c_str(), response.GetLength(), 0);

      shutdown(client_socket, SD_SEND);
      closesocket(client_socket);
   }

   AnsiString
   RestApiServer::GetAuthorizationHeader_(const AnsiString &request)
   {
      return GetHeader_(request, "authorization");
   }

   AnsiString
   RestApiServer::GetHeader_(const AnsiString &request, const AnsiString &lowerCaseName)
   {
      AnsiString lowerRequest = request;
      lowerRequest.MakeLower();

      const AnsiString needle = "\r\n" + lowerCaseName + ":";

      int headerPosition = lowerRequest.Find(needle);
      if (headerPosition < 0)
         return "";

      int valueStart = headerPosition + needle.GetLength();
      int lineEnd = request.Find("\r\n", valueStart);
      if (lineEnd < 0)
         return "";

      AnsiString headerValue = request.Mid(valueStart, lineEnd - valueStart);
      headerValue.Trim();

      return headerValue;
   }

   RestApiServer::Caller
   RestApiServer::Authenticate_(const AnsiString &request, const IPAddress &peer_address)
   {
      // Default-constructed: AuthenticationFailed, read-only, no domains. Every
      // early return below is therefore a refusal that grants nothing, and a
      // path that forgets to set the authority cannot accidentally widen one.
      Caller caller;

      AnsiString headerValue = GetAuthorizationHeader_(request);
      if (headerValue.IsEmpty())
         return caller;

      // Bearer is preferred when present. Basic is still accepted, unchanged,
      // because every existing script depends on it.
      // >= and not >, for both schemes below: a header value of exactly
      // "Bearer " or "Basic " is a presented credential that happens to be
      // empty, and it has to reach the failure path so that it is logged and
      // counted like any other. Requiring a byte after the space dropped it
      // silently instead.
      if (headerValue.GetLength() >= 7 && headerValue.Mid(0, 7).CompareNoCase("bearer ") == 0)
      {
         AnsiString token = headerValue.Mid(7);
         token.Trim();

         if (!AuthenticateBearer_(token, peer_address, caller))
         {
            // Deliberately does not say whether the token was unknown, expired
            // or refused by source address - the caller gets one 401 either
            // way, and the log entry is for the administrator, not the client.
            LOG_APPLICATION("REST API: API key authentication failed from " + String(peer_address.ToString()) + ".");
            RegisterAuthenticationFailure_(peer_address);
         }

         return caller;
      }

      if (headerValue.GetLength() >= 6 && headerValue.Mid(0, 6).CompareNoCase("basic ") == 0)
      {
         AnsiString encodedCredentials = headerValue.Mid(6);
         encodedCredentials.Trim();

         const BasicResult basic = AuthenticateBasic_(encodedCredentials, request);

         if (basic == BasicAccepted)
         {
            // The administrator password is the full-authority credential and
            // always has been. It is not read-only and it is not restricted to
            // any domain: the scoping added for API keys narrows keys, and
            // narrowing this one would break every script that exists.
            caller.result = AuthenticatedAsAdministrator;
            caller.read_only = false;
            caller.identity = "administrator";

            return caller;
         }

         if (basic == BasicCodeMissing)
         {
            // The password was right and no code came with it: a client that has
            // not been told a second factor is enrolled, not a guess. Said so in
            // the log (this is exactly when the administrator needs telling) and
            // in the response, and not counted towards the auto-ban.
            LOG_APPLICATION("REST API: the administrator password was accepted but a second factor is enrolled and no X-hMailServer-OTP header carried a code.");
            caller.second_factor_required = true;
            return caller;
         }

         // A rejected credential leaves a trace, so repeated guessing against an
         // exposed management port is at least visible to the administrator.
         // (Presenting no credential at all is normal for the login page and is
         // deliberately not logged here.) A wrong code is a guess at six digits
         // and is counted like any other wrong credential.
         if (basic == BasicCodeWrong)
         {
            LOG_APPLICATION("REST API: the administrator password was accepted but the one-time code in X-hMailServer-OTP was not.");
            caller.second_factor_required = true;
         }
         else
         {
            LOG_APPLICATION("REST API: administrator authentication failed.");
         }

         RegisterAuthenticationFailure_(peer_address);
      }

      return caller;
   }

   RestApiServer::BasicResult
   RestApiServer::AuthenticateBasic_(const AnsiString &encodedCredentials, const AnsiString &request)
   {
      // An empty value is refused before the decoder sees it, rather than
      // relying on what MimeCodeBase64 does with a zero-length input.
      if (encodedCredentials.IsEmpty())
         return BasicRefused;

      AnsiString credentials = Base64::Decode(encodedCredentials, encodedCredentials.GetLength());

      int separatorPosition = credentials.Find(":");
      if (separatorPosition <= 0)
         return BasicRefused;

      String username = credentials.Mid(0, separatorPosition);
      String password = credentials.Mid(separatorPosition + 1);

      if (username.CompareNoCase(_T("administrator")) != 0)
         return BasicRefused;

      String correctPassword = IniFileSettings::Instance()->GetAdministratorPassword();
      if (correctPassword.IsEmpty())
         return BasicRefused;

      Crypt::EncryptionType hashType = Crypt::Instance()->GetHashType(correctPassword);

      if (!Crypt::Instance()->Validate(password, correctPassword, hashType))
         return BasicRefused;

      // The second factor, once the password has been accepted - the same order
      // COMAuthentication uses, for the same reason: what is said about the code
      // is said only to somebody who holds the password.
      const String secret = IniFileSettings::Instance()->GetAdministratorTotpSecret();
      if (secret.IsEmpty())
         return BasicAccepted;

      const AnsiString code = GetHeader_(request, "x-hmailserver-otp");
      if (code.IsEmpty())
         return BasicCodeMissing;

      return Totp::VerifyCode(AnsiString(secret), code) ? BasicAccepted : BasicCodeWrong;
   }

   bool
   RestApiServer::AuthenticateBearer_(const AnsiString &token, const IPAddress &peer_address, Caller &caller)
   {
      // Cheap syntactic rejection first, so that a flood of junk tokens does
      // not cause the store to be read at all.
      AnsiString prefix(ApiKeyTokenPrefix);

      if (token.GetLength() != prefix.GetLength() + ApiKeySecretBytes * 2)
         return false;

      if (token.Mid(0, prefix.GetLength()) != prefix)
         return false;

      if (!IsLowerHex(token.Mid(prefix.GetLength()), ApiKeySecretBytes * 2))
         return false;

      AnsiString presentedHash = HashApiKeyToken(token);
      if (presentedHash.IsEmpty())
         return false;

      std::vector<ApiKeyRecord> keys = LoadKeys_();

      // Every record is compared, with no early exit, so the work done is a
      // function of the number of stored keys only - never of how much of a
      // guessed token happened to be right.
      const ApiKeyRecord *matched = nullptr;

      for (const ApiKeyRecord &key : keys)
      {
         if (ConstantTimeEquals(presentedHash, key.hash) && matched == nullptr)
            matched = &key;
      }

      if (matched == nullptr)
         return false;

      // Expiry and source restriction are checked after the secret matched, and
      // both produce the same indistinguishable failure.
      if (IsExpired_(matched->expires))
      {
         String message;
         message.Format(_T("REST API: API key '%s' was presented after it expired (%s)."),
            matched->label.c_str(), matched->expires.c_str());
         LOG_APPLICATION(message);
         return false;
      }

      if (!IsSourceAllowed_(matched->allowed_from, peer_address))
      {
         String message;
         message.Format(_T("REST API: API key '%s' was presented from %s, which is outside its allowed source '%s'."),
            matched->label.c_str(), String(peer_address.ToString()).c_str(), matched->allowed_from.c_str());
         LOG_APPLICATION(message);
         return false;
      }

      // The key's authority travels with the request from here. Copied out of
      // the record rather than looked up again later, so that a store edited
      // between the authentication and the authorisation of one request cannot
      // change the answer half way through.
      caller.result = AuthenticatedWithApiKey;
      caller.identity = AnsiString("key:") + AnsiString(matched->id);
      caller.read_only = matched->read_only;
      caller.domains = matched->domains;

      return true;
   }

   bool
   RestApiServer::IsWithinRequestRate_(const AnsiString &identity, bool &firstRefusal)
   {
      firstRefusal = false;

      // Not reachable: every authenticated caller has an identity. Belt and
      // braces, because the alternative to returning true here would be an
      // unnamed credential sharing one budget with every other.
      if (identity.IsEmpty())
         return true;

      const ULONGLONG now = GetTickCount64();

      std::lock_guard<std::mutex> guard(credential_rate_mutex);

      for (std::vector<CredentialRate>::iterator it = credential_rates.begin(); it != credential_rates.end(); )
      {
         // Closed windows are dropped as they are met, so the set empties itself
         // even if no further request ever arrives.
         if (now - it->window_started_at >= RateWindowMilliseconds)
         {
            it = credential_rates.erase(it);
            continue;
         }

         if (it->identity == identity)
         {
            it->count++;

            // The refused requests are counted too. A caller that keeps pushing
            // stays refused for the rest of its window rather than being let
            // back in one request at a time.
            firstRefusal = it->count == MaxRequestsPerWindowPerCredential + 1;

            return it->count <= MaxRequestsPerWindowPerCredential;
         }

         ++it;
      }

      CredentialRate entry;
      entry.identity = identity;
      entry.window_started_at = now;
      entry.count = 1;

      if (credential_rates.size() < MaxRateLimitedCredentials)
      {
         credential_rates.push_back(entry);
         return true;
      }

      // Full. Replace the oldest window rather than growing, so this is a hard
      // bound on memory whatever the store contains.
      size_t oldest = 0;

      for (size_t i = 1; i < credential_rates.size(); i++)
      {
         if (credential_rates[i].window_started_at < credential_rates[oldest].window_started_at)
            oldest = i;
      }

      credential_rates[oldest] = entry;

      return true;
   }

   void
   RestApiServer::ClearRequestRates_()
   {
      std::lock_guard<std::mutex> guard(credential_rate_mutex);

      credential_rates.clear();
      credential_rates.shrink_to_fit();
   }

   bool
   RestApiServer::IsRefusedAddress_(const IPAddress &peer_address)
   {
      // Memory only: no allocation, no formatting, no file and no query, so a
      // refused connection costs a lock and a walk of at most
      // MaxRefusedAddresses entries. The lock is held across nothing but that
      // walk - never across the socket work the caller does afterwards.
      if (peer_address.IsAny())
         return false;

      const int family = static_cast<int>(peer_address.GetType());
      const unsigned __int64 high = peer_address.GetAddress1();
      const unsigned __int64 low = peer_address.GetAddress2();
      const ULONGLONG now = GetTickCount64();

      std::lock_guard<std::mutex> guard(refused_addresses_mutex);

      for (std::vector<RefusedAddress>::iterator it = refused_addresses.begin(); it != refused_addresses.end(); )
      {
         // Expired entries are dropped as they are met. Combined with the sweep
         // in RefuseAddress_ this is what makes the set self-emptying: an
         // administrator refused by mistake is let back in by the passage of
         // time alone, with no restart and no configuration change.
         if (now >= it->expires_at)
         {
            it = refused_addresses.erase(it);
            continue;
         }

         if (IsSameAddress(*it, family, high, low))
            return true;

         ++it;
      }

      return false;
   }

   void
   RestApiServer::RefuseAddress_(const IPAddress &peer_address)
   {
      // 0.0.0.0 is what a failed peer-address parse leaves behind. It is never
      // recorded, because it is not one host: refusing it would refuse every
      // connection whose address could not be read.
      if (peer_address.IsAny())
         return;

      const int family = static_cast<int>(peer_address.GetType());
      const unsigned __int64 high = peer_address.GetAddress1();
      const unsigned __int64 low = peer_address.GetAddress2();
      const ULONGLONG now = GetTickCount64();
      const ULONGLONG expiresAt = now + RefusedAddressMilliseconds;

      std::lock_guard<std::mutex> guard(refused_addresses_mutex);

      // Sweep first, so that the size cap below is only reached by addresses
      // that are genuinely still being refused.
      refused_addresses.erase(
         std::remove_if(refused_addresses.begin(), refused_addresses.end(),
            [now](const RefusedAddress &entry) { return now >= entry.expires_at; }),
         refused_addresses.end());

      for (RefusedAddress &entry : refused_addresses)
      {
         if (IsSameAddress(entry, family, high, low))
         {
            entry.expires_at = expiresAt;
            return;
         }
      }

      RefusedAddress entry;
      entry.family = family;
      entry.address_high = high;
      entry.address_low = low;
      entry.expires_at = expiresAt;

      if (refused_addresses.size() < MaxRefusedAddresses)
      {
         refused_addresses.push_back(entry);
         return;
      }

      // Full. Replace the entry closest to expiry rather than growing, so the
      // set is a hard-bounded amount of memory whatever an attacker does with
      // source addresses.
      size_t soonest = 0;

      for (size_t i = 1; i < refused_addresses.size(); i++)
      {
         if (refused_addresses[i].expires_at < refused_addresses[soonest].expires_at)
            soonest = i;
      }

      refused_addresses[soonest] = entry;
   }

   void
   RestApiServer::ClearRefusedAddresses_()
   {
      std::lock_guard<std::mutex> guard(refused_addresses_mutex);

      refused_addresses.clear();
      refused_addresses.shrink_to_fit();
   }

   void
   RestApiServer::RegisterAuthenticationFailure_(const IPAddress &peer_address)
   {
      // The same accounting the SMTP/IMAP/POP3 front ends use, so a brute-force
      // attempt against the management port is counted alongside one against
      // the mail ports and trips the same auto-ban.
      //
      // Loopback is excluded on purpose. Auto-ban creates an IP range at
      // priority 100, and a range covering 127.0.0.1 would deny the server's
      // own local clients, hMailAdmin and every local script - a self-inflicted
      // outage far worse than the brute-force attempt it was reacting to. An
      // attacker who is already on the loopback interface is not being kept out
      // by an IP ban in any case, and the listener refuses to run on a
      // non-loopback address without TLS, so remote attempts are still counted.
      if (peer_address.ToString() == "127.0.0.1" || peer_address.ToString() == "::1")
         return;

      if (peer_address.IsAny())
         return;

      try
      {
         AccountLogon accountLogon;
         bool disconnect = false;

         // The per-name lockout is deliberately not fed here. The credential this
         // listener accepts is the administrator password from the ini, not a
         // mailbox name, so "REST API" is a label for the per-IP accounting and
         // never a name anybody authenticates as - counting it could only lock a
         // string nobody uses, while logging a line that claims a control acted.
         // Brute force against this listener is answered by the per-IP auto-ban
         // and the refused-source set above. See AccountLogon.h.
         accountLogon.RegisterFailedLogin(peer_address, _T("REST API"), disconnect, false);

         // 'disconnect' is set exactly when this failure was the one that
         // tripped the ban. The connection carrying it is closed after its one
         // response either way, so what matters is the *next* connection from
         // this address - and an IP range would not refuse that one, because
         // ranges are only consulted through TCPConnection. Recording the
         // address here is what makes the ban real on this listener, and it is
         // also what stops a flood from creating a ban (and its row, and its
         // run of name-collision SELECTs) every third request. See the
         // refused-source set at the top of this file.
         if (disconnect)
         {
            RefuseAddress_(peer_address);

            // One line per ban, not one per refused request: a line per
            // connection would answer a flood of cheap requests with a flood of
            // log writes, which is the shape of problem being fixed. The
            // refusals themselves are visible under debug logging only.
            String message;
            message.Format(_T("REST API: Refusing requests from %s for %d minutes after repeated authentication failures."),
               String(peer_address.ToString()).c_str(), RefusedAddressMinutes);
            LOG_APPLICATION(message);
         }
      }
      catch (...)
      {
         // Auto-ban accounting touches the database. A failure there must not
         // turn into a 500 on a request that was going to be refused anyway.
         LOG_DEBUG("RestApiServer: Failed to record an authentication failure for auto-ban.");
      }
   }

   AnsiString
   RestApiServer::ProcessRequest_(const AnsiString &request, const IPAddress &peer_address)
   {
      // Parse the request line.
      int lineEnd = request.Find("\r\n");
      if (lineEnd < 0)
         return BuildResponse_(400, "{\"error\":\"malformed request\"}");

      AnsiString requestLine = request.Mid(0, lineEnd);

      std::vector<AnsiString> requestParts = StringParser::SplitString(requestLine, " ");
      if (requestParts.size() < 2)
         return BuildResponse_(400, "{\"error\":\"malformed request\"}");

      AnsiString method = requestParts[0];
      AnsiString path = requestParts[1];

      // Strip the query string from the path, keeping it for the routes that
      // take one.
      AnsiString query;
      int queryPosition = path.Find("?");
      if (queryPosition >= 0)
      {
         query = path.Mid(queryPosition + 1);
         path = path.Mid(0, queryPosition);
      }

      // OpenTelemetry: span this request, continuing the caller's trace when a
      // valid traceparent header arrived and starting a fresh local one when it
      // was absent or rejected - a rejected value never refuses the request.
      // Named after the sanitized method (low-cardinality even for junk input)
      // with the path as an attribute.
      // The RAII scope covers every return below. No-op unless OtelEndpoint is
      // configured.
      OtelSpanScope otelSpan(OtelTraceContext::SanitizeSpanName(method), OtelSpanKindServer,
                             OtelTraceContext::FromHttpRequest(request));
      otelSpan.AddAttribute("http.target", path);

      // The web admin SPA shell is served without authentication (it is a
      // static login page). This is the only unauthenticated route in the
      // listener: everything else goes through Authenticate_ below, once, and
      // no handler is reachable except from the dispatch at the bottom of this
      // function.
      if (method == "GET" && (path == "/" || path == "/index.html"))
         return HandleWebAdminPage_();

      try
      {
         // Inside the try: authenticating a bearer token reads the key store and
         // recording a failure touches the database, neither of which may turn
         // an unauthenticated request into an unhandled exception on the single
         // REST worker thread.
         Caller caller = Authenticate_(request, peer_address);

         if (caller.result == AuthenticationFailed)
            return BuildUnauthorizedResponse_(caller.second_factor_required);

         // After authentication, so the budget belongs to the credential rather
         // than to a source address, and before routing, so that being over it
         // costs nothing but this comparison.
         bool firstRefusal = false;

         if (!IsWithinRequestRate_(caller.identity, firstRefusal))
         {
            if (firstRefusal)
            {
               // One line per credential per window. A line per refused request
               // would answer a flood of cheap requests with a flood of log
               // writes, which is the shape of problem being fixed.
               String message;
               message.Format(_T("REST API: Credential '%s' has exceeded %d requests in %d seconds and is refused for the rest of the window."),
                  String(caller.identity).c_str(), MaxRequestsPerWindowPerCredential,
                  (int) (RateWindowMilliseconds / 1000));
               LOG_APPLICATION(message);
            }

            return BuildTooManyRequestsResponse_();
         }

         Route route;
         ParseRoute_(method, path, route);
         route.query = query;

         // The single authorisation choke point. Every route is decided here,
         // by kind, before any handler runs - so a handler cannot be reached by
         // a credential that was never checked against it, and a new endpoint
         // cannot be added without appearing in Authorize_ as well.
         AnsiString refusalReason;
         AuthorizationResult authorization = Authorize_(caller, route, refusalReason);

         if (authorization == AuthorizationUnauthenticated)
            return BuildUnauthorizedResponse_(false);

         if (authorization == AuthorizationForbidden)
         {
            String message;
            message.Format(_T("REST API: Credential '%s' was refused %s %s - %s."),
               String(caller.identity).c_str(), String(method).c_str(),
               String(path).c_str(), String(refusalReason).c_str());
            LOG_APPLICATION(message);

            return BuildForbiddenResponse_(refusalReason);
         }

         switch (route.kind)
         {
         case RouteApiKeyList:
            return HandleListApiKeys_();

         case RouteApiKeyCreate:
            return HandleCreateApiKey_(GetRequestBody_(request));

         case RouteApiKeyRevoke:
            return HandleRevokeApiKey_(route.identifier);

         case RouteStatus:
            return HandleStatus_();

         case RouteDomainList:
            return HandleListDomains_(caller.domains);

         case RouteAccountList:
            return HandleListAccounts_(String(route.identifier));

         case RouteAccountCreate:
            return HandleCreateAccount_(String(route.identifier), GetRequestBody_(request));

         case RouteAccountDelete:
            return HandleDeleteAccount_(String(route.identifier));

         case RouteQueueList:
            return HandleListQueue_();

         case RouteQueueRetry:
            return HandleQueueRetry_(route.message_id);

         case RouteQueueDelete:
            return HandleQueueDelete_(route.message_id);

         case RouteTlsa:
            return HandleTlsa_();

         case RouteSrv:
            return HandleSrv_(caller.domains);
         case RouteMetricsHistory:
            return HandleMetricsHistory_(route.query);

         case RouteQuarantineList:
            return HandleListQuarantine_();

         case RouteQuarantineRelease:
            return HandleQuarantineRelease_(route.message_id);

         case RouteQuarantineDelete:
            return HandleQuarantineDelete_(route.message_id);

         case RouteAliasList:
            return HandleListAliases_(String(route.identifier));

         case RouteIpRangeList:
            return HandleListIpRanges_();
         case RouteIpRangeCreate:
            return HandleCreateIpRange_(GetRequestBody_(request));
         case RouteIpRangeDelete:
            return HandleDeleteIpRange_(route.range_id);
         case RouteListList:
            return HandleListLists_(String(route.identifier));
         case RouteListCreate:
            return HandleCreateList_(String(route.identifier), GetRequestBody_(request));
         case RouteListDelete:
            return HandleDeleteList_(String(route.identifier));
         case RouteCertificateList:
            return HandleListCertificates_();
         case RouteDkimGet:
            return HandleDkim_(String(route.identifier));
         case RouteRuleList:
            return HandleListRules_();
         case RouteLogList:
            return HandleListLogs_();
         case RouteLogTail:
            return HandleLogTail_(route.identifier, route.query);
         case RouteBackupStart:
            return HandleBackupStart_();
         case RouteBackupStatus:
            return HandleBackupStatus_();
         case RouteSettingsGet:
            return HandleSettings_();
         case RouteOpenApi:
            return HandleOpenApi_();

         default:
            break;
         }

         // RouteUnknown, and RouteApiKeyUnsupported once the administrator check
         // in Authorize_ has let it through. Deliberately after the switch rather
         // than inside its default: a switch every arm of which returns still
         // leaves /W3 asking whether the function does, and answering that with
         // an unreachable return is worse than this.
         return BuildResponse_(404, "{\"error\":\"not found\"}");
      }
      catch (...)
      {
         return BuildResponse_(500, "{\"error\":\"internal error\"}");
      }
   }

   void
   RestApiServer::ParseRoute_(const AnsiString &method, const AnsiString &path, Route &route)
   {
      // Transcribed from the dispatch this replaced, predicate for predicate and
      // in the same order, so that which requests reach which handler is
      // unchanged. Note that StartsWith and EndsWith are case-insensitive in
      // this tree while operator== is not - long-standing behaviour, preserved
      // here rather than tidied, because tightening it is a separate change with
      // its own compatibility question.
      route.kind = RouteUnknown;
      route.identifier = "";
      route.message_id = 0;

      const AnsiString apiKeysPath = "/api/v1/apikeys";

      if (path == apiKeysPath || path.StartsWith(apiKeysPath + "/"))
      {
         route.kind = RouteApiKeyUnsupported;

         if (path == apiKeysPath)
         {
            if (method == "GET")
               route.kind = RouteApiKeyList;
            else if (method == "POST")
               route.kind = RouteApiKeyCreate;
         }
         else if (method == "DELETE")
         {
            AnsiString id = path.Mid(apiKeysPath.GetLength() + 1);

            if (!id.IsEmpty() && id.Find("/") < 0)
            {
               route.kind = RouteApiKeyRevoke;
               route.identifier = id;
            }
         }

         return;
      }

      if (method == "GET" && path == "/api/v1/status")
      {
         route.kind = RouteStatus;
         return;
      }

      if (method == "GET" && path == "/api/v1/domains")
      {
         route.kind = RouteDomainList;
         return;
      }

      // /api/v1/domains/<name>/accounts
      const AnsiString domainsPrefix = "/api/v1/domains/";

      if (path.StartsWith(domainsPrefix) && path.EndsWith("/accounts"))
      {
         AnsiString domainName = path.Mid(domainsPrefix.GetLength(),
            path.GetLength() - domainsPrefix.GetLength() - AnsiString("/accounts").GetLength());

         if (!domainName.IsEmpty() && domainName.Find("/") < 0)
         {
            if (method == "GET")
            {
               route.kind = RouteAccountList;
               route.identifier = domainName;
               return;
            }

            if (method == "POST")
            {
               route.kind = RouteAccountCreate;
               route.identifier = domainName;
               return;
            }
         }
      }

      // /api/v1/accounts/<address>
      const AnsiString accountsPrefix = "/api/v1/accounts/";

      if (method == "DELETE" && path.StartsWith(accountsPrefix))
      {
         AnsiString address = path.Mid(accountsPrefix.GetLength());

         if (!address.IsEmpty() && address.Find("/") < 0)
         {
            route.kind = RouteAccountDelete;
            route.identifier = address;
            return;
         }
      }

      if (method == "GET" && path == "/api/v1/queue")
      {
         route.kind = RouteQueueList;
         return;
      }

      // /api/v1/queue/<id>/retry and /api/v1/queue/<id>
      const AnsiString queuePrefix = "/api/v1/queue/";

      if (path.StartsWith(queuePrefix))
      {
         AnsiString remainder = path.Mid(queuePrefix.GetLength());

         if (method == "POST" && remainder.EndsWith("/retry"))
         {
            AnsiString idPart = remainder.Mid(0, remainder.GetLength() - AnsiString("/retry").GetLength());

            __int64 messageId = 0;
            if (ParseQueueId(idPart, messageId))
            {
               route.kind = RouteQueueRetry;
               route.message_id = messageId;
               return;
            }
         }

         if (method == "DELETE" && remainder.Find("/") < 0)
         {
            __int64 messageId = 0;
            if (ParseQueueId(remainder, messageId))
            {
               route.kind = RouteQueueDelete;
               route.message_id = messageId;
               return;
            }
         }
      }

      if (method == "GET" && path == "/api/v1/tlsa")
      {
         route.kind = RouteTlsa;
         return;
      }

      if (method == "GET" && path == "/api/v1/srv")
      {
         route.kind = RouteSrv;
         return;
      }

      if (method == "GET" && path == "/api/v1/metrics/history")
      {
         route.kind = RouteMetricsHistory;
         return;
      }

      // /api/v1/quarantine, /api/v1/quarantine/<id>/release, /api/v1/quarantine/<id>
      if (method == "GET" && path == "/api/v1/quarantine")
      {
         route.kind = RouteQuarantineList;
         return;
      }

      const AnsiString quarantinePrefix = "/api/v1/quarantine/";

      if (path.StartsWith(quarantinePrefix))
      {
         AnsiString remainder = path.Mid(quarantinePrefix.GetLength());

         if (method == "POST" && remainder.EndsWith("/release"))
         {
            AnsiString idPart = remainder.Mid(0, remainder.GetLength() - AnsiString("/release").GetLength());

            __int64 quarantineId = 0;
            if (ParseQueueId(idPart, quarantineId))
            {
               route.kind = RouteQuarantineRelease;
               route.message_id = quarantineId;
               return;
            }
         }

         if (method == "DELETE" && remainder.Find("/") < 0)
         {
            __int64 quarantineId = 0;
            if (ParseQueueId(remainder, quarantineId))
            {
               route.kind = RouteQuarantineDelete;
               route.message_id = quarantineId;
               return;
            }
         }
      }

      // /api/v1/domains/<name>/aliases - same shape as the accounts listing.
      if (method == "GET" && path.StartsWith(domainsPrefix) && path.EndsWith("/aliases"))
      {
         AnsiString domainName = path.Mid(domainsPrefix.GetLength(),
            path.GetLength() - domainsPrefix.GetLength() - AnsiString("/aliases").GetLength());

         if (!domainName.IsEmpty() && domainName.Find("/") < 0)
         {
            route.kind = RouteAliasList;
            route.identifier = domainName;
            return;
         }
      }

      // Wave 88: the surfaces that were COM-only.
      const AnsiString ipRangesPath = "/api/v1/ipranges";
      if (path == ipRangesPath)
      {
         if (method == "GET")
            route.kind = RouteIpRangeList;
         else if (method == "POST")
            route.kind = RouteIpRangeCreate;
         return;
      }
      if (method == "DELETE" && path.StartsWith(ipRangesPath + "/"))
      {
         AnsiString idPart = path.Mid(ipRangesPath.GetLength() + 1);
         __int64 rangeId = 0;
         if (idPart.Find("/") < 0 && ParseQueueId(idPart, rangeId))
         {
            route.kind = RouteIpRangeDelete;
            route.range_id = rangeId;
            return;
         }
      }
      if (path.StartsWith(domainsPrefix) && path.EndsWith("/lists"))
      {
         AnsiString domainName = path.Mid(domainsPrefix.GetLength(),
            path.GetLength() - domainsPrefix.GetLength() - AnsiString("/lists").GetLength());
         if (!domainName.IsEmpty() && domainName.Find("/") < 0)
         {
            if (method == "GET")
            {
               route.kind = RouteListList;
               route.identifier = domainName;
               return;
            }
            if (method == "POST")
            {
               route.kind = RouteListCreate;
               route.identifier = domainName;
               return;
            }
         }
      }
      const AnsiString listsPrefix = "/api/v1/lists/";
      if (method == "DELETE" && path.StartsWith(listsPrefix))
      {
         AnsiString address = path.Mid(listsPrefix.GetLength());
         if (!address.IsEmpty() && address.Find("/") < 0)
         {
            route.kind = RouteListDelete;
            route.identifier = address;
            return;
         }
      }
      if (method == "GET" && path.StartsWith(domainsPrefix) && path.EndsWith("/dkim"))
      {
         AnsiString domainName = path.Mid(domainsPrefix.GetLength(),
            path.GetLength() - domainsPrefix.GetLength() - AnsiString("/dkim").GetLength());
         if (!domainName.IsEmpty() && domainName.Find("/") < 0)
         {
            route.kind = RouteDkimGet;
            route.identifier = domainName;
            return;
         }
      }
      if (method == "GET" && path == "/api/v1/certificates")
      {
         route.kind = RouteCertificateList;
         return;
      }
      if (method == "GET" && path == "/api/v1/rules")
      {
         route.kind = RouteRuleList;
         return;
      }
      if (method == "GET" && path == "/api/v1/logs")
      {
         route.kind = RouteLogList;
         return;
      }
      const AnsiString logsPrefix = "/api/v1/logs/";
      if (method == "GET" && path.StartsWith(logsPrefix))
      {
         AnsiString name = path.Mid(logsPrefix.GetLength());
         if (!name.IsEmpty() && name.Find("/") < 0)
         {
            route.kind = RouteLogTail;
            route.identifier = name;
            return;
         }
      }
      if (path == "/api/v1/backup")
      {
         if (method == "POST")
            route.kind = RouteBackupStart;
         else if (method == "GET")
            route.kind = RouteBackupStatus;
         return;
      }
      if (method == "GET" && path == "/api/v1/settings")
      {
         route.kind = RouteSettingsGet;
         return;
      }
      if (method == "GET" && path == "/api/v1/openapi.json")
         route.kind = RouteOpenApi;
   }

   bool
   RestApiServer::IsApiKeyRoute_(RouteKind kind)
   {
      switch (kind)
      {
      case RouteApiKeyUnsupported:
      case RouteApiKeyList:
      case RouteApiKeyCreate:
      case RouteApiKeyRevoke:
         return true;

      default:
         break;
      }

      return false;
   }

   bool
   RestApiServer::IsMutatingRoute_(RouteKind kind)
   {
      // By kind and not by HTTP method, deliberately. The method is what a
      // client asserts; the kind is what this server decided the request
      // actually does, so a route that changed something under a GET could not
      // slip past a read-only key by being spelled harmlessly.
      switch (kind)
      {
      case RouteApiKeyCreate:
      case RouteApiKeyRevoke:
      case RouteAccountCreate:
      case RouteAccountDelete:
      case RouteQueueRetry:
      case RouteQueueDelete:
      case RouteQuarantineRelease:
      case RouteQuarantineDelete:
      case RouteIpRangeCreate:
      case RouteIpRangeDelete:
      case RouteListCreate:
      case RouteListDelete:
      case RouteBackupStart:
         return true;

      default:
         break;
      }

      return false;
   }

   bool
   RestApiServer::IsDomainAllowed_(const std::vector<String> &domains, const String &domainName)
   {
      // No list means every domain, which is what an unrestricted key and the
      // administrator password both have.
      if (domains.empty())
         return true;

      if (domainName.IsEmpty())
         return false;

      for (const String &allowed : domains)
      {
         if (allowed.CompareNoCase(domainName.c_str()) == 0)
            return true;
      }

      return false;
   }

   RestApiServer::AuthorizationResult
   RestApiServer::Authorize_(const Caller &caller, const Route &route, AnsiString &refusalReason)
   {
      refusalReason = "";

      // The administrator password carries full authority and always has. This
      // is the one credential nothing below narrows.
      if (caller.result == AuthenticatedAsAdministrator)
         return AuthorizationAllowed;

      // Key management is administrator-password only. An API key that could
      // mint keys would be able to issue itself a replacement with no expiry,
      // no source restriction and full scope, which would give away the whole
      // point of having scoped keys; and one that could revoke keys could lock
      // the administrator out of their own management interface. Answering 401
      // rather than 403 keeps the refusal indistinguishable from any other
      // credential problem - including for a verb that does not exist, which is
      // why RouteApiKeyUnsupported is in this set.
      if (IsApiKeyRoute_(route.kind))
         return AuthorizationUnauthenticated;

      if (caller.read_only && IsMutatingRoute_(route.kind))
      {
         refusalReason = "this api key is read-only";
         return AuthorizationForbidden;
      }

      if (caller.domains.empty())
         return AuthorizationAllowed;

      // A key restricted to named domains. The delivery queue is server-wide -
      // one queued message carries recipients in any number of domains, and
      // GET /api/v1/queue lists the sender and every recipient of all of them -
      // so there is no honest way to narrow it to a domain. Refused outright
      // rather than narrowed wrongly.
      if (route.kind == RouteQueueList || route.kind == RouteQueueRetry || route.kind == RouteQueueDelete)
      {
         refusalReason = "this api key is restricted to named domains, and the delivery queue is server-wide";
         return AuthorizationForbidden;
      }

      // The quarantine has the queue's shape exactly: one entry names a sender
      // and recipients in any number of domains, and releasing one delivers
      // mail. The same reasoning gives the same answer.
      if (route.kind == RouteQuarantineList || route.kind == RouteQuarantineRelease || route.kind == RouteQuarantineDelete)
      {
         refusalReason = "this api key is restricted to named domains, and the quarantine is server-wide";
         return AuthorizationForbidden;
      }
      switch (route.kind)
      {
      case RouteIpRangeList:
      case RouteIpRangeCreate:
      case RouteIpRangeDelete:
      case RouteCertificateList:
      case RouteRuleList:
      case RouteLogList:
      case RouteLogTail:
      case RouteBackupStart:
      case RouteBackupStatus:
      case RouteSettingsGet:
         // IP ranges, certificates, global rules, the logs, the backup and the
         // server settings are all server-wide: none of them belongs to a domain,
         // and the logs in particular carry every domain's traffic.
         refusalReason = "this api key is restricted to named domains, and that resource is server-wide";
         return AuthorizationForbidden;
      default:
         break;
      }

      String targetDomain;

      switch (route.kind)
      {
      case RouteAccountList:
      case RouteAccountCreate:
      case RouteAliasList:
      case RouteListList:
      case RouteListCreate:
      case RouteDkimGet:
         targetDomain = String(route.identifier);
         break;
      case RouteListDelete:
         targetDomain = StringParser::ExtractDomain(String(route.identifier));
         break;

      case RouteAccountDelete:
         // The reason the whole mechanism exists. The account to delete is named
         // by an address in the path and nothing else, so without this a key
         // issued for one domain could delete a mailbox in another by editing
         // one path segment - the classic identifier-in-the-path authorisation
         // bypass, against a route that destroys mail.
         targetDomain = StringParser::ExtractDomain(String(route.identifier));
         break;

      default:
         // Server-wide and read-only: /status, /tlsa, /srv and the domain
         // listing. The latter two filter their per-domain output to the key's
         // domains in their handlers rather than being refused here (a listing
         // that answered 403 would be useless to exactly the credential the
         // restriction exists for, and /srv names every local domain in its
         // records - which a key issued for one customer must not be handed).
         return AuthorizationAllowed;
      }

      if (!IsDomainAllowed_(caller.domains, targetDomain))
      {
         refusalReason = "this api key is not permitted for that domain";
         return AuthorizationForbidden;
      }

      return AuthorizationAllowed;
   }

   AnsiString
   RestApiServer::BuildResponse_(int statusCode, const AnsiString &body, const AnsiString &extraHeaders)
   {
      // Any status not named here becomes a 500, deliberately - a wrong number
      // in a response line is worse than an honest server error. Which is also
      // why 403, 413 and 429 had to be added below the moment anything started
      // using them: BuildResponse_(429, ...) against the previous list answered
      // "500 Internal Server Error" while carrying a rate-limit body.
      AnsiString statusText;
      switch (statusCode)
      {
      case 200: statusText = "OK"; break;
      case 201: statusText = "Created"; break;
      case 202: statusText = "Accepted"; break;
      case 400: statusText = "Bad Request"; break;
      case 403: statusText = "Forbidden"; break;
      case 404: statusText = "Not Found"; break;
      case 409: statusText = "Conflict"; break;
      case 413: statusText = "Payload Too Large"; break;
      case 429: statusText = "Too Many Requests"; break;
      case 503: statusText = "Service Unavailable"; break;
      default:  statusText = "Internal Server Error"; statusCode = 500; break;
      }

      AnsiString response;
      response.Format("HTTP/1.0 %d %hs\r\nContent-Type: application/json\r\nContent-Length: %d\r\n%hsConnection: close\r\n\r\n",
         statusCode, statusText.c_str(), body.GetLength(), extraHeaders.c_str());
      response += body;

      return response;
   }

   AnsiString
   RestApiServer::BuildUnauthorizedResponse_(bool secondFactorRequired)
   {
      // One response for every possible authentication problem: no credential,
      // a wrong administrator password, an unknown API key, an expired key and
      // a key refused by source address are all answered identically. Telling
      // the caller that the token was valid but expired, or valid but presented
      // from the wrong network, would confirm a working secret.
      //
      // The one exception is deliberate: when the administrator PASSWORD was
      // accepted and the one-time code was what was missing or wrong, the
      // response carries "X-hMailServer-OTP: required" - the shape GitHub's API
      // uses - so a client knows to ask its user for the code. That confirms
      // the password to somebody who already holds it, and nothing to anybody
      // else.
      //
      // The challenge advertises Basic only, exactly as before, so browsers
      // reaching the management interface keep prompting as they always have.
      const AnsiString body = secondFactorRequired
         ? "{\"error\":\"authentication failed\",\"second_factor\":\"required\"}"
         : "{\"error\":\"authentication failed\"}";

      AnsiString response;
      response += "HTTP/1.0 401 Unauthorized\r\n";
      response += "WWW-Authenticate: Basic realm=\"hMailServer\"\r\n";
      if (secondFactorRequired)
         response += "X-hMailServer-OTP: required\r\n";
      response += "Content-Type: application/json\r\n";
      response.AppendFormat("Content-Length: %d\r\n", body.GetLength());
      response += "Connection: close\r\n\r\n";
      response += body;

      return response;
   }

   AnsiString
   RestApiServer::BuildForbiddenResponse_(const AnsiString &reason)
   {
      // 403 and not 401, and it says why.
      //
      // The refusals answered 401 above are about the *credential*, where every
      // extra word confirms something to somebody holding a token they should
      // not have. This one is about permission: the caller has already proved
      // its key is genuine (it got this far), so concealing which restriction
      // stopped it protects nothing and costs an administrator an afternoon
      // wondering why a key that authenticates cannot delete an account.
      //
      // The reasons are fixed sentences written here. None of them names a file,
      // a query, a row or another domain.
      AnsiString body;
      body.Format("{\"error\":\"%hs\"}", JsonEscape_(reason).c_str());

      return BuildResponse_(403, body);
   }

   AnsiString
   RestApiServer::BuildTooManyRequestsResponse_()
   {
      // Retry-After is the whole window. The window is fixed rather than
      // sliding, so a caller that waits that long is certainly inside a new one
      // - which is what makes the advice honest rather than a guess.
      AnsiString retryAfter;
      retryAfter.Format("Retry-After: %d\r\n", (int) (RateWindowMilliseconds / 1000));

      return BuildResponse_(429, "{\"error\":\"too many requests\"}", retryAfter);
   }

   String
   RestApiServer::GetApiKeyStoreFile()
   {
      // Storage decision: a dedicated ini file alongside hMailServer.ini.
      //
      //  - The database schema may not change, and the keys are a property of
      //    this listener rather than of any mail object, so no existing table
      //    fits.
      //  - hMailServer.ini itself is owned by IniFileSettings, which caches its
      //    values at InitInstance and would need to change to hold a list; a
      //    separate file needs no change there at all.
      //  - The service account already has write access to that directory (it
      //    writes hMailServer.ini), and the directory is not web-served.
      //  - The file is read on every authentication attempt, so adding or
      //    revoking a key takes effect immediately: no restart, no rebuild.
      //
      // Format - one section per key, section name "Key.<id>":
      //
      //    [Key.3f9a1c4b5d6e7f80]
      //    Label=CI deploy
      //    Hash=<64 lower-case hex characters>
      //    Expires=2027-01-01 00:00:00
      //    AllowedFrom=10.0.0.0/24
      //    Scope=full
      //    Domains=example.com,example.net
      //
      // Scope and Domains both fail closed, which is what makes hand-editing
      // this file safe: a section with no Scope, or a Scope value that is not
      // the literal "full", is read-only, and a Domains list that survives
      // normalisation as nothing at all leaves the key able to reach no domain.
      //
      // An administrator can revoke a key by hand by deleting its section, and
      // can revoke every key by deleting the file; either takes effect on the
      // next request. Adding a key by hand is possible but pointless, since only
      // the hash is stored and the clear text is never recoverable -
      // POST /api/v1/apikeys is the supported way in.
      String iniFile = IniFileSettings::GetInitializationFile();

      return FileUtilities::Combine(FileUtilities::GetFilePath(iniFile), _T("hMailServerApiKeys.ini"));
   }

   std::vector<RestApiServer::ApiKeyRecord>
   RestApiServer::LoadKeys_()
   {
      // Why SHA-256 and not Argon2id or PBKDF2, both of which Crypt offers:
      //
      //  - Those are deliberately slow because they defend low-entropy,
      //    human-chosen passwords against offline guessing. An API key here is
      //    32 bytes straight from RAND_bytes; there is no dictionary to run and
      //    no amount of hash speed that makes 256 bits guessable, so the
      //    slowness buys nothing.
      //  - It would cost something real. Argon2id as configured in HashCreator
      //    allocates 19 MiB and burns two passes over it per verification, on
      //    the single REST worker thread, for any unauthenticated stranger who
      //    sends "Authorization: Bearer junk". That is a free remote CPU and
      //    memory denial of service against the management port - a worse
      //    security bug than the one being fixed.
      //  - A per-record salt would also make lookup impossible without running
      //    the KDF once per stored key, multiplying that cost by the number of
      //    keys. An unsalted digest lets us hash the presented token once and
      //    compare, in constant time, against each stored value.
      //
      // So: HashCreator(SHA256).GenerateHashNoSalt(token, hex) - an existing
      // primitive, used the way the rest of the server already uses it, with no
      // new hashing code introduced.
      //
      // The store is parsed from the file's bytes rather than through
      // GetPrivateProfileString. Writes still go through
      // WritePrivateProfileString, which merges correctly and leaves an
      // administrator's comments alone, but the Windows profile functions keep
      // a cache, and a read served from that cache would mean a key deleted by
      // hand in this file carried on working. There is no cache in this path,
      // so a revocation - through the API or in an editor - takes effect on the
      // very next request.
      std::vector<ApiKeyRecord> keys;

      String storeFile = GetApiKeyStoreFile();

      // No file means no keys. This is the shipped default and is emphatically
      // not an error, so nothing is reported.
      if (!FileUtilities::Exists(storeFile))
         return keys;

      String content = FileUtilities::ReadCompleteTextFile(storeFile);
      if (content.IsEmpty())
         return keys;

      String sectionPrefix(ApiKeySectionPrefix);

      ApiKeyRecord current;
      bool haveSection = false;

      // A record is only usable once its Hash has been seen, so records are
      // committed when the next section starts (and once more at the end).
      auto commit = [&keys, &current, &haveSection, &storeFile]()
      {
         if (!haveSection)
            return;

         haveSection = false;

         // A record whose hash is not a full SHA-256 digest is unusable. It is
         // skipped rather than compared against, because a short or mangled
         // value would otherwise be compared over its own length only - which
         // is exactly how a one-character "hash" would match everything.
         //
         // Deliberately LOG_APPLICATION and not ErrorManager. This condition is
         // reached by hand-editing the store, it is re-evaluated on every single
         // request, and a reported error per request would bury the ERROR log
         // (and fail the fixtures that assert it is clean) over something whose
         // only symptom is one key not working - which the administrator sees
         // immediately anyway.
         if (IsLowerHex(current.hash, ApiKeyHashHexLength))
            keys.push_back(current);
         else
         {
            LOG_APPLICATION("RestApi: Ignoring a record in " + storeFile +
               " with no usable Hash value. Key: " + current.id + ".");
         }

         current = ApiKeyRecord();
      };

      std::vector<String> lines = StringParser::SplitString(content, _T("\n"));

      for (const String &rawLine : lines)
      {
         String line = rawLine;
         line.Trim();

         if (line.IsEmpty() || line.StartsWith(_T(";")) || line.StartsWith(_T("#")))
            continue;

         if (line.StartsWith(_T("[")))
         {
            commit();

            if (!line.EndsWith(_T("]")))
               continue;

            String section = line.Mid(1, line.GetLength() - 2);
            section.Trim();

            if (!section.StartsWith(sectionPrefix))
               continue;

            // StartsWith is case-insensitive, so a hand-written [KEY.AB12...]
            // section is accepted here. The id is normalised to lower case
            // because HandleRevokeApiKey_ only accepts ids that are lower-case
            // hex; without this, such a key would be listed by
            // GET /api/v1/apikeys under an id that DELETE /api/v1/apikeys/<id>
            // answers 404 for, leaving it unrevocable through the API.
            current.id = section.Mid(sectionPrefix.GetLength());
            current.id.MakeLower();

            haveSection = true;
            continue;
         }

         if (!haveSection)
            continue;

         int equalsPosition = line.Find(_T("="));
         if (equalsPosition <= 0)
            continue;

         String name = line.Mid(0, equalsPosition);
         String value = line.Mid(equalsPosition + 1);

         name.Trim();
         value.Trim();

         if (name.CompareNoCase(_T("Hash")) == 0)
            current.hash = AnsiString(value);
         else if (name.CompareNoCase(_T("Label")) == 0)
            current.label = value;
         else if (name.CompareNoCase(_T("Expires")) == 0)
            current.expires = value;
         else if (name.CompareNoCase(_T("AllowedFrom")) == 0)
            current.allowed_from = value;
         else if (name.CompareNoCase(_T("Scope")) == 0)
         {
            // Only the literal "full" widens a key. Every other value - a
            // misspelling, a truncated write, a line an administrator meant to
            // comment out - leaves it read-only, because the opposite default
            // would turn a typo into write access over every domain.
            current.read_only = value.CompareNoCase(ApiKeyScopeFull) != 0;
         }
         else if (name.CompareNoCase(_T("Domains")) == 0)
         {
            // Normalised on the way in - trimmed, lower-cased, empty items
            // dropped - so that the exact comparison in IsDomainAllowed_ is the
            // only thing that has to be right.
            //
            // An entry that is not a real domain name is kept and simply matches
            // nothing, which is the fail-closed direction. A value that is empty
            // or is nothing but separators leaves the list empty, which means
            // "every domain" - the same reading AllowedFrom gives an empty value,
            // and the only one consistent with a section that has no Domains line
            // at all.
            current.domains.clear();

            std::vector<String> parts = StringParser::SplitString(value, _T(","));

            for (const String &part : parts)
            {
               String domainName = part;
               domainName.Trim();
               domainName.MakeLower();

               if (!domainName.IsEmpty())
                  current.domains.push_back(domainName);
            }
         }
      }

      commit();

      return keys;
   }

   bool
   RestApiServer::IsExpired_(const String &expires)
   {
      // Fail closed: a missing or unparseable expiry counts as expired. A key
      // that never expires is the property that makes the administrator
      // password dangerous in the first place, so there is no "no expiry" case
      // to fall through to.
      if (expires.GetLength() < 19)
         return true;

      DateTime expiryTime = Time::GetDateFromSystemDate(expires);
      if (expiryTime.GetStatus() != DateTime::valid)
         return true;

      DateTime now = DateTime::GetCurrentTime();

      return now >= expiryTime ? true : false;
   }

   bool
   RestApiServer::IsSourceAllowed_(const String &allowed_from, const IPAddress &peer_address)
   {
      // No restriction configured: any source, which is the behaviour of the
      // administrator password today and so is not a regression.
      String restriction = allowed_from;
      restriction.Trim();

      if (restriction.IsEmpty())
         return true;

      IPAddress lower;
      IPAddress upper;

      // A restriction we cannot parse refuses the request rather than being
      // ignored. A typo in the store must not quietly widen a key's scope.
      if (!ParseSourceRestriction(AnsiString(restriction), lower, upper))
         return false;

      // WithinRange compares the low 64 bits for IPv4 and both halves for IPv6,
      // so mixing families would compare unrelated numbers. A restriction
      // written in the other family simply does not match.
      if (peer_address.GetType() != lower.GetType() || lower.GetType() != upper.GetType())
         return false;

      return peer_address.WithinRange(lower, upper);
   }

   AnsiString
   RestApiServer::HandleListApiKeys_()
   {
      // Metadata only. The hash is not returned: it is not a usable credential,
      // but publishing it over the API would hand an offline target to anyone
      // who briefly held the administrator password.
      std::vector<ApiKeyRecord> keys = LoadKeys_();

      AnsiString items;
      int count = 0;

      for (const ApiKeyRecord &key : keys)
      {
         // scope and domains are reported because a restriction an administrator
         // cannot see is a restriction they will not trust: the whole point of
         // issuing a narrow key is being able to confirm afterwards that it is
         // narrow.
         AnsiString item;
         item.Format("{\"id\":\"%hs\",\"label\":\"%hs\",\"scope\":\"%hs\",\"domains\":\"%hs\","
                     "\"expires\":\"%hs\",\"allowed_from\":\"%hs\",\"expired\":%hs}",
            JsonEscape_(AnsiString(key.id)).c_str(),
            JsonEscape_(AnsiString(key.label)).c_str(),
            key.read_only ? ApiKeyScopeReadOnlyNarrow : ApiKeyScopeFullNarrow,
            JsonEscape_(AnsiString(StringParser::JoinVector(key.domains, _T(",")))).c_str(),
            JsonEscape_(AnsiString(key.expires)).c_str(),
            JsonEscape_(AnsiString(key.allowed_from)).c_str(),
            IsExpired_(key.expires) ? "true" : "false");

         if (count > 0)
            items += ",";

         items += item;
         count++;
      }

      AnsiString body;
      body.Format("{\"count\":%d,\"keys\":[%hs]}", count, items.c_str());

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleCreateApiKey_(const AnsiString &requestBody)
   {
      AnsiString label = GetJsonStringValue_(requestBody, "label");
      AnsiString expires = GetJsonStringValue_(requestBody, "expires");
      AnsiString allowedFrom = GetJsonStringValue_(requestBody, "allowed_from");
      AnsiString scope = GetJsonStringValue_(requestBody, "scope");
      AnsiString domains = GetJsonStringValue_(requestBody, "domains");

      label.Trim();
      expires.Trim();
      allowedFrom.Trim();
      scope.Trim();
      domains.Trim();

      if (label.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"label is required\"}");

      // The label ends up in an ini value and in log lines, so keep it to
      // something printable and one line long.
      if (label.GetLength() > 64)
         return BuildResponse_(400, "{\"error\":\"label must be 64 characters or fewer\"}");

      for (int i = 0; i < label.GetLength(); i++)
      {
         unsigned char character = static_cast<unsigned char>(label[i]);
         if (character < 0x20 || character == 0x7F)
            return BuildResponse_(400, "{\"error\":\"label must not contain control characters\"}");
      }

      // Least privilege by default. A create request that does not name a scope
      // gets a read-only key, because the alternative is that every caller who
      // has not read the documentation is handed a credential that can delete
      // accounts - and a key is most often minted for something that only reads
      // (a monitoring probe, a CI status check). "full" is one word away for the
      // callers that need it, and the 201 below says which one they got.
      bool readOnly = true;

      if (!scope.IsEmpty())
      {
         if (scope.CompareNoCase(ApiKeyScopeFullNarrow) == 0)
            readOnly = false;
         else if (scope.CompareNoCase(ApiKeyScopeReadOnlyNarrow) != 0)
            return BuildResponse_(400, "{\"error\":\"scope must be 'readonly' or 'full'\"}");
      }

      // The domain restriction is normalised here and stored in that form, so
      // that nothing the caller typed reaches the store verbatim and the value
      // LoadKeys_ reads back is the value this function decided on.
      String normalizedDomains;

      if (!domains.IsEmpty())
      {
         std::vector<AnsiString> parts = StringParser::SplitString(domains, ",");

         for (const AnsiString &part : parts)
         {
            String domainName = String(part);
            domainName.Trim();
            domainName.MakeLower();

            if (domainName.IsEmpty())
               continue;

            // Refuse a restriction we would refuse every request against,
            // rather than issuing a key that can never reach anything. The
            // domain does not have to exist yet - a key may legitimately be
            // issued before the domain it will manage - but it does have to be
            // a domain name.
            if (!StringParser::IsValidDomainName(domainName))
               return BuildResponse_(400, "{\"error\":\"domains must be a comma-separated list of domain names\"}");

            if (!normalizedDomains.IsEmpty())
               normalizedDomains += _T(",");

            normalizedDomains += domainName;
         }

         // The caller asked for a restriction and nothing survived
         // normalisation ("domains":" , "). Storing that would silently mean
         // "every domain", which is the opposite of what was asked for.
         if (normalizedDomains.IsEmpty())
            return BuildResponse_(400, "{\"error\":\"domains must be a comma-separated list of domain names\"}");
      }

      if (expires.IsEmpty())
      {
         // No expiry named: default to a bounded lifetime rather than forever.
         DateTimeSpan span;
         span.SetDateTimeSpan(ApiKeyDefaultLifetimeDays, 0, 0, 0);

         DateTime expiryTime = DateTime::GetCurrentTime() + span;
         expires = AnsiString(Time::GetTimeStampFromDateTime(expiryTime));
      }

      // Validate the caller's expiry rather than storing something that would
      // silently be treated as expired on first use.
      if (IsExpired_(String(expires)))
         return BuildResponse_(400, "{\"error\":\"expires must be a future date in the form YYYY-MM-DD HH:MM:SS\"}");

      // Then store the parsed date rather than the text that produced it.
      //
      // Defence in depth against ini injection: the store is one section per
      // key and is parsed line by line from the file's bytes, so any value that
      // reached WritePrivateProfileString carrying a CRLF would appear to
      // LoadKeys_ as further lines - a second [Key.*] section, or a Scope=full
      // line under an existing one. The label is already refused if it holds a
      // control character and AllowedFrom has to parse as an address, but the
      // expiry had only a length-and-parse check, which says nothing about what
      // follows the nineteenth character. Canonicalising removes the question
      // instead of answering it: what is written is generated here.
      DateTime canonicalExpiry = Time::GetDateFromSystemDate(String(expires));
      if (canonicalExpiry.GetStatus() != DateTime::valid)
         return BuildResponse_(400, "{\"error\":\"expires must be a future date in the form YYYY-MM-DD HH:MM:SS\"}");

      expires = AnsiString(Time::GetTimeStampFromDateTime(canonicalExpiry));

      // Same for the source restriction: reject a form we would refuse every
      // request against, instead of issuing a key that can never be used.
      if (!allowedFrom.IsEmpty())
      {
         IPAddress lower;
         IPAddress upper;

         if (!ParseSourceRestriction(allowedFrom, lower, upper))
            return BuildResponse_(400, "{\"error\":\"allowed_from must be an address, an address range 'lower-upper', or CIDR\"}");
      }

      unsigned char secret[ApiKeySecretBytes];
      unsigned char idBytes[ApiKeyIdBytes];

      if (RAND_bytes(secret, sizeof(secret)) != 1 || RAND_bytes(idBytes, sizeof(idBytes)) != 1)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5791, "RestApiServer::HandleCreateApiKey_",
            "Failed to obtain random bytes for a new REST API key. No key was created.");
         return BuildResponse_(500, "{\"error\":\"internal error\"}");
      }

      AnsiString token = AnsiString(ApiKeyTokenPrefix) + BytesToLowerHex(secret, ApiKeySecretBytes);
      AnsiString hash = HashApiKeyToken(token);

      SecureZeroMemory(secret, sizeof(secret));

      if (!IsLowerHex(hash, ApiKeyHashHexLength))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5792, "RestApiServer::HandleCreateApiKey_",
            "Failed to hash a new REST API key. No key was created.");
         return BuildResponse_(500, "{\"error\":\"internal error\"}");
      }

      String id = String(BytesToLowerHex(idBytes, ApiKeyIdBytes));
      String section = String(ApiKeySectionPrefix) + id;
      String storeFile = GetApiKeyStoreFile();

      if (!FileUtilities::Exists(storeFile))
      {
         // Explain the file inside the file. This is the only place an
         // administrator will go looking, and LoadKeys_ ignores ';' lines. A
         // failure here is not fatal: WritePrivateProfileString below creates
         // the file anyway, just without the preamble.
         AnsiString preamble;
         preamble += "; hMailServer REST administration API keys.\r\n";
         preamble += ";\r\n";
         preamble += "; One section per key. Only the SHA-256 digest of a key is stored, so a\r\n";
         preamble += "; key cannot be recovered from this file: it is shown once, in the reply\r\n";
         preamble += "; to POST /api/v1/apikeys, and never again.\r\n";
         preamble += ";\r\n";
         preamble += "; To revoke a key, either DELETE /api/v1/apikeys/<id> or delete its\r\n";
         preamble += "; section below. Both take effect on the next request - no restart and no\r\n";
         preamble += "; rebuild. Deleting this file revokes every key.\r\n";
         preamble += ";\r\n";
         preamble += "; Expires     YYYY-MM-DD HH:MM:SS, local time. Required. A key whose\r\n";
         preamble += ";             expiry is missing or unreadable counts as expired.\r\n";
         preamble += "; AllowedFrom Optional. An address, a 'lower-upper' range, or CIDR.\r\n";
         preamble += ";             Empty means any source address.\r\n";
         preamble += "; Scope       'full' or 'readonly'. Anything else - including a missing\r\n";
         preamble += ";             line - is readonly, so a typo cannot widen a key. A\r\n";
         preamble += ";             readonly key is refused every request that changes\r\n";
         preamble += ";             something.\r\n";
         preamble += "; Domains     Optional, comma-separated. Empty means every domain. A key\r\n";
         preamble += ";             with a list may only act on those domains, and is refused\r\n";
         preamble += ";             the delivery-queue endpoints outright because the queue is\r\n";
         preamble += ";             server-wide.\r\n";
         preamble += ";\r\n";
         preamble += "; No key of any scope can create or revoke keys: that needs the\r\n";
         preamble += "; administrator password.\r\n";
         preamble += "\r\n";

         FileUtilities::WriteToFile(storeFile, preamble);
      }

      // Write the hash last: until it is there the section is ignored by
      // LoadKeys_, so a failure part way through leaves an unusable record
      // rather than a usable key with no expiry - or, now, one with no
      // restrictions. Scope and Domains are written before it for the same
      // reason: a key that became usable before its restrictions landed would be
      // briefly unrestricted, and briefly is enough.
      bool written =
         WritePrivateProfileString(section, _T("Label"), String(label), storeFile) != FALSE &&
         WritePrivateProfileString(section, _T("Expires"), String(expires), storeFile) != FALSE &&
         WritePrivateProfileString(section, _T("AllowedFrom"), String(allowedFrom), storeFile) != FALSE &&
         WritePrivateProfileString(section, _T("Scope"), readOnly ? ApiKeyScopeReadOnly : ApiKeyScopeFull, storeFile) != FALSE &&
         WritePrivateProfileString(section, _T("Domains"), normalizedDomains, storeFile) != FALSE &&
         WritePrivateProfileString(section, _T("Hash"), String(hash), storeFile) != FALSE;

      // Flush the profile cache so the very next request sees the new key.
      WritePrivateProfileString(nullptr, nullptr, nullptr, storeFile);

      if (!written)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5790, "RestApiServer::HandleCreateApiKey_",
            "Failed to write the REST API key store. No key was created. File: " + storeFile);

         WritePrivateProfileString(section, nullptr, nullptr, storeFile);
         WritePrivateProfileString(nullptr, nullptr, nullptr, storeFile);

         return BuildResponse_(500, "{\"error\":\"failed to store the key\"}");
      }

      // The scope is in the log line as well as the response: which keys are
      // full-authority is the thing an administrator will want to answer months
      // later from the log alone.
      String created;
      created.Format(_T("RestApi: API key '%s' (%s) created. Scope: %s. Domains: %s."),
         String(label).c_str(), id.c_str(),
         readOnly ? ApiKeyScopeReadOnly : ApiKeyScopeFull,
         normalizedDomains.IsEmpty() ? _T("(all)") : normalizedDomains.c_str());
      LOG_APPLICATION(created);

      // The clear-text token is returned exactly once, here. It is not stored
      // and cannot be recovered afterwards.
      AnsiString body;
      body.Format("{\"id\":\"%hs\",\"label\":\"%hs\",\"scope\":\"%hs\",\"domains\":\"%hs\","
                  "\"expires\":\"%hs\",\"allowed_from\":\"%hs\",\"key\":\"%hs\"}",
         JsonEscape_(AnsiString(id)).c_str(),
         JsonEscape_(label).c_str(),
         readOnly ? ApiKeyScopeReadOnlyNarrow : ApiKeyScopeFullNarrow,
         JsonEscape_(AnsiString(normalizedDomains)).c_str(),
         JsonEscape_(expires).c_str(),
         JsonEscape_(allowedFrom).c_str(),
         JsonEscape_(token).c_str());

      return BuildResponse_(201, body);
   }

   AnsiString
   RestApiServer::HandleRevokeApiKey_(const AnsiString &id)
   {
      // Only ids of the shape we hand out, so that nothing here can be talked
      // into naming another section (or another file).
      if (!IsLowerHex(id, ApiKeyIdBytes * 2))
         return BuildResponse_(404, "{\"error\":\"api key not found\"}");

      String storeFile = GetApiKeyStoreFile();
      String section = String(ApiKeySectionPrefix) + String(id);

      std::vector<ApiKeyRecord> keys = LoadKeys_();

      bool exists = false;
      String label;

      for (const ApiKeyRecord &key : keys)
      {
         if (key.id.CompareNoCase(String(id)) == 0)
         {
            exists = true;
            label = key.label;
         }
      }

      if (!exists)
         return BuildResponse_(404, "{\"error\":\"api key not found\"}");

      // Passing a null key name deletes the whole section.
      BOOL deleted = WritePrivateProfileString(section, nullptr, nullptr, storeFile);
      WritePrivateProfileString(nullptr, nullptr, nullptr, storeFile);

      if (deleted == FALSE)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5790, "RestApiServer::HandleRevokeApiKey_",
            "Failed to write the REST API key store. The key was not revoked. File: " + storeFile);
         return BuildResponse_(500, "{\"error\":\"failed to revoke the key\"}");
      }

      LOG_APPLICATION("RestApi: API key '" + label + "' (" + String(id) + ") revoked.");

      return BuildResponse_(200, "{\"revoked\":true}");
   }

   AnsiString
   RestApiServer::HandleWebAdminPage_()
   {
      String pagePath = FileUtilities::Combine(
         IniFileSettings::Instance()->GetProgramDirectory(), _T("WebAdmin\\index.html"));

      AnsiString body;
      if (FileUtilities::Exists(pagePath))
      {
         String content = FileUtilities::ReadCompleteTextFile(pagePath);
         body = content;
      }
      else
      {
         body = "<!doctype html><html><body style=\"font-family:sans-serif\">"
                "<h1>hMailServer</h1><p>Web administration page not installed. "
                "The REST API is available under /api/v1/.</p></body></html>";
      }

      AnsiString response;
      response.Format("HTTP/1.0 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: %d\r\nConnection: close\r\n\r\n",
         body.GetLength());
      response += body;

      return response;
   }

   AnsiString
   RestApiServer::HandleStatus_()
   {
      ServerStatus *status = ServerStatus::Instance();

      AnsiString version = Application::Instance()->GetVersionNumber();

      AnsiString body;
      body.Format("{\"version\":\"%hs\",\"state\":%d,\"processedMessages\":%d,\"spamMessages\":%d,\"virusesRemoved\":%d,"
                  "\"sessions\":{\"smtp\":%d,\"imap\":%d,\"pop3\":%d}}",
         JsonEscape_(version).c_str(),
         status->GetState(),
         status->GetNumberOfProcessedMessages(),
         status->GetNumberOfDetectedSpamMessages(),
         status->GetNumberOfRemovedViruses(),
         status->GetNumberOfSessions(STSMTP),
         status->GetNumberOfSessions(STIMAP),
         status->GetNumberOfSessions(STPOP3));

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleListDomains_(const std::vector<String> &allowedDomains)
   {
      Domains domains;
      domains.Refresh();

      AnsiString body = "[";

      // A separate count rather than the loop index, which is the difference
      // between valid and invalid JSON now that entries can be skipped: the
      // separator has to follow the last entry *emitted*, not the last index
      // visited. With `i > 0` a skipped first domain produced "[,{...}]".
      int count = 0;

      for (int i = 0; i < domains.GetCount(); i++)
      {
         std::shared_ptr<Domain> domain = domains.GetItem(i);
         if (!domain)
            continue;

         // A key restricted to named domains sees only those. Filtered rather
         // than refused, because a listing that answered 403 would be useless to
         // exactly the credential the restriction exists for - and a listing that
         // returned everything would tell a key issued for one customer the names
         // of all the others.
         if (!IsDomainAllowed_(allowedDomains, domain->GetName()))
            continue;

         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"name\":\"%hs\",\"active\":%hs}",
            JsonEscape_(AnsiString(domain->GetName())).c_str(),
            domain->GetIsActive() ? "true" : "false");

         body += entry;
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleListAccounts_(const String &domainName)
   {
      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      Accounts accounts(domain->GetID());
      accounts.Refresh();

      AnsiString body = "[";

      // As in HandleListDomains_: counted, not indexed, so that a skipped entry
      // cannot put a separator where there is nothing to separate.
      int count = 0;

      for (int i = 0; i < accounts.GetCount(); i++)
      {
         std::shared_ptr<Account> account = accounts.GetItem(i);
         if (!account)
            continue;

         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"address\":\"%hs\",\"active\":%hs}",
            JsonEscape_(AnsiString(account->GetAddress())).c_str(),
            account->GetActive() ? "true" : "false");

         body += entry;
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleCreateAccount_(const String &domainName, const AnsiString &requestBody)
   {
      AnsiString address = GetJsonStringValue_(requestBody, "address");
      AnsiString password = GetJsonStringValue_(requestBody, "password");

      if (address.IsEmpty() || password.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"address and password are required\"}");

      String addressDomain = StringParser::ExtractDomain(String(address));
      if (addressDomain.CompareNoCase(domainName) != 0)
         return BuildResponse_(400, "{\"error\":\"address does not belong to the domain\"}");

      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      // Reject duplicates.
      std::shared_ptr<Account> existingAccount = std::shared_ptr<Account>(new Account());
      if (PersistentAccount::ReadObject(existingAccount, String(address)) && existingAccount->GetID() > 0)
         return BuildResponse_(409, "{\"error\":\"account already exists\"}");

      int preferredHashAlgorithm = IniFileSettings::Instance()->GetPreferredHashAlgorithm();
      String hashedPassword = Crypt::Instance()->EnCrypt(String(password), (Crypt::EncryptionType) preferredHashAlgorithm);

      std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());
      account->SetDomainID(domain->GetID());
      account->SetAddress(String(address));
      account->SetPassword(hashedPassword);
      account->SetPasswordEncryption(preferredHashAlgorithm);
      account->SetActive(true);

      // createInbox: true. This argument is the whole reason this call changed,
      // and it was losing mail.
      //
      // PersistentAccount::SaveObject(account) - the one-argument overload used
      // here before - forwards createInbox as *false*, so the hm_accounts row was
      // written and no INBOX row in hm_imapfolders was. Every COM caller passes
      // true (InterfaceAccount::Save), so an account made in hMailAdmin or the
      // Control Panel got one and an account made over the REST API did not.
      //
      // What that costs, following the delivery path for a message addressed to
      // such an account: SMTP accepts it at RCPT TO because the account exists,
      // then LocalDelivery::CreateAccountLevelMessage_ asks
      // InboxIDCache::GetUserInboxFolder for the folder to put it in.
      // PersistentIMAPFolder::GetUserInboxFolder selects folderid where
      // foldername = 'INBOX', finds no row, and returns 0; the delivery gives up
      // and returns an empty message. The caller reports HM5209 and returns
      // without adding anything to the bounce list - so the sender is told the
      // message was accepted, the recipient never sees it, and no non-delivery
      // report is generated. The message is simply gone.
      //
      // Worse, InboxIDCache caches the zero, so creating the inbox afterwards
      // does not fix delivery until the cache is cleared.
      //
      // The error message is also captured now. SaveObject already ran
      // PreSaveLimitationsCheck through this path and the old code discarded
      // everything it said, answering 500 "failed to save account" for what are
      // almost always the caller's own doing.
      String saveError;

      if (!PersistentAccount::SaveObject(account, saveError, true, PersistenceModeNormal))
      {
         // Two different failures, told apart by whether anything explained it.
         //
         // A non-empty message comes from PreSaveLimitationsCheck: the address is
         // not a valid mailbox address, the domain has reached its maximum number
         // of accounts, the domain has a maximum account size and this account has
         // none. Those are all the caller's problem, so 400 and say which - an
         // administrator who was told only "failed to save account", with a 500,
         // could not tell a configured limit from a broken database. Every one of
         // those strings is a fixed sentence written for an administrator; none of
         // them carries a path, a query, a row or another account's name, which is
         // what makes passing it through safe.
         //
         // An empty message means the INSERT itself failed. That one is ours: 500,
         // and deliberately without detail.
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to create account " + String(address) + ": " + saveError);

            AnsiString body;
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(AnsiString(saveError)).c_str());

            return BuildResponse_(400, body);
         }

         return BuildResponse_(500, "{\"error\":\"failed to save account\"}");
      }

      // Confirm the account is really there before reporting that it was made.
      //
      // Not belt and braces: PersistentAccount::SaveObject returns the result of
      // the INSERT, and when the inbox it now creates cannot be created it
      // deletes the row it has just written and *still* returns true. So without
      // this the endpoint could answer 201 for a mailbox that no longer exists -
      // the same "reported a success that did not happen" that the queue
      // endpoints were fixed for. The account object keeps its id after the
      // delete, so the question has to be put to the database.
      std::shared_ptr<Account> savedAccount = std::shared_ptr<Account>(new Account());

      if (!PersistentAccount::ReadObject(savedAccount, String(address)) || savedAccount->GetID() == 0)
      {
         LOG_APPLICATION("RestApi: Account " + String(address) +
            " could not be created - it was not present after being saved, which happens when its inbox could not be created.");

         return BuildResponse_(500, "{\"error\":\"failed to save account\"}");
      }

      LOG_APPLICATION("RestApi: Account " + String(address) + " created.");

      AnsiString body;
      body.Format("{\"address\":\"%hs\",\"created\":true}", JsonEscape_(address).c_str());

      return BuildResponse_(201, body);
   }

   AnsiString
   RestApiServer::HandleDeleteAccount_(const String &address)
   {
      std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());

      if (!PersistentAccount::ReadObject(account, address) || account->GetID() == 0)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      if (!PersistentAccount::DeleteObject(account))
         return BuildResponse_(500, "{\"error\":\"failed to delete account\"}");

      LOG_APPLICATION("RestApi: Account " + address + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   AnsiString
   RestApiServer::HandleListQueue_()
   {
      // Reuses the same query that backs the COM Status.UndeliveredMessages
      // property: tab-separated columns id, created, from, recipients,
      // next try, file name, locked, tries.
      AnsiString queueData = ServerStatus::Instance()->GetUnsortedMessageStatus();

      std::vector<AnsiString> lines = StringParser::SplitString(queueData, "\r\n");

      AnsiString items;
      int count = 0;

      for (const AnsiString &line : lines)
      {
         if (line.IsEmpty())
            continue;

         std::vector<AnsiString> columns = StringParser::SplitString(line, "\t");
         if (columns.size() < 8)
            continue;

         AnsiString item;
         item.Format("{\"id\":%hs,\"created\":\"%hs\",\"from\":\"%hs\",\"recipients\":\"%hs\",\"next_try\":\"%hs\",\"locked\":%hs,\"tries\":%hs}",
            columns[0].c_str(),
            JsonEscape_(columns[1]).c_str(),
            JsonEscape_(columns[2]).c_str(),
            JsonEscape_(columns[3]).c_str(),
            JsonEscape_(columns[4]).c_str(),
            columns[6] == "1" ? "true" : "false",
            columns[7].c_str());

         if (count > 0)
            items += ",";

         items += item;
         count++;
      }

      AnsiString body;
      body.Format("{\"count\":%d,\"messages\":[%hs]}", count, items.c_str());

      return BuildResponse_(200, body);
   }

   bool
   RestApiServer::QueueMessageExists_(__int64 messageId)
   {
      // Both DeliveryQueue::ResetDeliveryTime and DeliveryQueue::Remove
      // return void and do nothing at all for an id that is not there
      // (PersistentMessage::SetNextTryTime reports success for an UPDATE
      // that matched no rows), so without this check the retry and delete
      // endpoints answered 200 {"retried":true} for any well-formed
      // number - including one an administrator mistyped, which then read
      // as "the message was requeued" when nothing had happened.
      //
      // Only delivery-queue rows count. A delivered message
      // (Message::Delivered, messagetype 2) lives in a mailbox and is not
      // what GET /api/v1/queue lists; accepting one here would let
      // DELETE /api/v1/queue/<id> destroy delivered mail.
      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());

      if (!PersistentMessage::ReadObject(message, messageId))
         return false;

      int messageType = static_cast<int>(message->GetState());

      return messageType == static_cast<int>(Message::Delivering) ||
             messageType == EtrnHeldMessageType;
   }

   AnsiString
   RestApiServer::HandleQueueRetry_(__int64 messageId)
   {
      if (!QueueMessageExists_(messageId))
         return BuildResponse_(404, "{\"error\":\"queue message not found\"}");

      DeliveryQueue::ResetDeliveryTime(messageId);
      DeliveryQueue::StartDelivery();

      LOG_APPLICATION("RestApi: Queue message " + StringParser::IntToString(messageId) + " scheduled for immediate delivery.");

      return BuildResponse_(200, "{\"retried\":true}");
   }

   AnsiString
   RestApiServer::HandleQueueDelete_(__int64 messageId)
   {
      if (!QueueMessageExists_(messageId))
         return BuildResponse_(404, "{\"error\":\"queue message not found\"}");

      DeliveryQueue::Remove(messageId);

      LOG_APPLICATION("RestApi: Queue message " + StringParser::IntToString(messageId) + " removed from the delivery queue.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   AnsiString
   RestApiServer::HandleListQuarantine_()
   {
      // The same store call the administration surface uses, bounded the same
      // way, so what the API reports and what a reviewer sees cannot disagree.
      const std::vector<QuarantinedMessage> messages = QuarantineStore::List(1000);

      AnsiString body = "[";
      int count = 0;

      for (const QuarantinedMessage &message : messages)
      {
         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"sender\":\"%hs\",\"recipients\":\"%hs\",\"subject\":\"%hs\",\"reason\":\"%hs\",\"score\":%d,\"size\":%d,\"created\":\"%hs\"}",
            message.id,
            JsonEscape_(AnsiString(message.sender)).c_str(),
            JsonEscape_(AnsiString(message.recipients)).c_str(),
            JsonEscape_(AnsiString(message.subject)).c_str(),
            JsonEscape_(AnsiString(message.reason)).c_str(),
            message.score,
            message.size,
            JsonEscape_(AnsiString(message.created)).c_str());

         body += entry;
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleQuarantineRelease_(__int64 id)
   {
      // Existence checked first, so an unknown id is a 404 rather than a 500
      // with somebody else's wording - the queue routes learned this the
      // expensive way.
      QuarantinedMessage message;
      if (!QuarantineStore::GetById(id, message))
         return BuildResponse_(404, "{\"error\":\"quarantined message not found\"}");

      String error;
      if (!QuarantineStore::Release(id, error))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(AnsiString(error)).c_str());
         return BuildResponse_(500, body);
      }

      return BuildResponse_(200, "{\"released\":true}");
   }

   AnsiString
   RestApiServer::HandleQuarantineDelete_(__int64 id)
   {
      QuarantinedMessage message;
      if (!QuarantineStore::GetById(id, message))
         return BuildResponse_(404, "{\"error\":\"quarantined message not found\"}");

      if (!QuarantineStore::Delete(id))
         return BuildResponse_(500, "{\"error\":\"the quarantined message could not be deleted\"}");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   AnsiString
   RestApiServer::HandleListAliases_(const String &domainName)
   {
      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      std::shared_ptr<Aliases> aliases = domain->GetAliases();
      if (!aliases)
         return BuildResponse_(200, "[]");

      aliases->Refresh();

      AnsiString body = "[";
      int count = 0;

      for (int i = 0; i < aliases->GetCount(); i++)
      {
         std::shared_ptr<Alias> alias = aliases->GetItem(i);
         if (!alias)
            continue;

         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"name\":\"%hs\",\"value\":\"%hs\",\"active\":%hs}",
            JsonEscape_(AnsiString(alias->GetName())).c_str(),
            JsonEscape_(AnsiString(alias->GetValue())).c_str(),
            alias->GetIsActive() ? "true" : "false");

         body += entry;
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   // ------------------------------------------------------------------------
   // Wave 88: the surfaces that were COM-only. Same shape as the routes above:
   // a 404 names what was not found, a 400 names what was wrong with the body,
   // every string goes through JsonEscape_, and nothing here is reachable
   // except through the dispatch in ProcessRequest_ after Authorize_.

   AnsiString
   RestApiServer::HandleListIpRanges_()
   {
      SecurityRanges ranges;
      ranges.Refresh();

      AnsiString body = "[";
      int count = 0;
      for (int i = 0; i < ranges.GetCount(); i++)
      {
         std::shared_ptr<SecurityRange> range = ranges.GetItem(i);
         if (!range)
            continue;

         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"name\":\"%hs\",\"lower\":\"%hs\",\"upper\":\"%hs\",\"priority\":%d",
            range->GetID(),
            JsonEscape_(AnsiString(range->GetName())).c_str(),
            JsonEscape_(AnsiString(range->GetLowerIPString())).c_str(),
            JsonEscape_(AnsiString(range->GetUpperIPString())).c_str(),
            (int) range->GetPriority());
         // The flags one at a time: Format has a fixed arity and this row has
         // more of them than it takes.
         auto flag = [&entry](const char *name, bool value)
         {
            entry += ",\"";
            entry += name;
            entry += value ? "\":true" : "\":false";
         };
         flag("allow_smtp", range->GetAllowSMTP());
         flag("allow_imap", range->GetAllowIMAP());
         flag("allow_pop3", range->GetAllowPOP3());
         flag("deliver_local_to_local", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_LOCAL_TO_LOCAL));
         flag("deliver_local_to_remote", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_LOCAL_TO_REMOTE));
         flag("deliver_remote_to_local", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_REMOTE_TO_LOCAL));
         flag("deliver_remote_to_remote", range->GetAllowOption(SecurityRange::IPRANGE_RELAY_REMOTE_TO_REMOTE));
         flag("require_auth_local_to_local", range->GetRequireSMTPAuthLocalToLocal());
         flag("require_auth_local_to_remote", range->GetRequireSMTPAuthLocalToExternal());
         flag("require_auth_remote_to_local", range->GetRequireSMTPAuthExternalToLocal());
         flag("require_auth_remote_to_remote", range->GetRequireSMTPAuthExternalToExternal());
         flag("require_tls_for_auth", range->GetRequireTLSForAuth());
         flag("spam_protection", range->GetSpamProtection());
         flag("virus_protection", range->GetVirusProtection());
         flag("expires", range->GetExpires());
         entry += "}";
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleCreateIpRange_(const AnsiString &requestBody)
   {
      AnsiString name = GetJsonStringValue_(requestBody, "name");
      AnsiString lowerText = GetJsonStringValue_(requestBody, "lower");
      AnsiString upperText = GetJsonStringValue_(requestBody, "upper");
      if (name.IsEmpty() || lowerText.IsEmpty() || upperText.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name, lower and upper are required\"}");

      IPAddress lower;
      IPAddress upper;
      if (!lower.TryParse(lowerText) || !upper.TryParse(upperText))
         return BuildResponse_(400, "{\"error\":\"lower and upper must be IP addresses\"}");

      long priority = 0;
      {
         // A number in the body is not a string, so read it the way the
         // integer routes do: everything after the key up to the next separator.
         AnsiString needle = "\"priority\"";
         int keyPosition = requestBody.Find(needle);
         if (keyPosition >= 0)
         {
            int colon = requestBody.Find(":", keyPosition + needle.GetLength());
            if (colon >= 0)
            {
               AnsiString digits;
               for (int i = colon + 1; i < requestBody.GetLength(); i++)
               {
                  char c = requestBody[i];
                  if (c == ' ' || c == '\t')
                     continue;
                  if ((c >= '0' && c <= '9') || (c == '-' && digits.IsEmpty()))
                  {
                     digits += c;
                     continue;
                  }
                  break;
               }
               if (!digits.IsEmpty())
                  priority = atol(digits.c_str());
            }
         }
      }

      std::shared_ptr<SecurityRange> range(new SecurityRange);
      range->SetName(String(name));
      range->SetLowerIP(lower);
      range->SetUpperIP(upper);
      range->SetPriority(priority);
      range->SetAllowSMTP(GetJsonBoolValue_(requestBody, "allow_smtp", true));
      range->SetAllowIMAP(GetJsonBoolValue_(requestBody, "allow_imap", true));
      range->SetAllowPOP3(GetJsonBoolValue_(requestBody, "allow_pop3", true));
      range->SetAllowRelayL2L(GetJsonBoolValue_(requestBody, "deliver_local_to_local", true));
      range->SetAllowRelayL2R(GetJsonBoolValue_(requestBody, "deliver_local_to_remote", false));
      range->SetAllowRelayR2L(GetJsonBoolValue_(requestBody, "deliver_remote_to_local", true));
      range->SetAllowRelayR2R(GetJsonBoolValue_(requestBody, "deliver_remote_to_remote", false));
      range->SetRequireSMTPAuthLocalToLocal(GetJsonBoolValue_(requestBody, "require_auth_local_to_local", false));
      range->SetRequireSMTPAuthLocalToExternal(GetJsonBoolValue_(requestBody, "require_auth_local_to_remote", true));
      range->SetRequireSMTPAuthExternalToLocal(GetJsonBoolValue_(requestBody, "require_auth_remote_to_local", false));
      range->SetRequireSMTPAuthExternalToExternal(GetJsonBoolValue_(requestBody, "require_auth_remote_to_remote", true));
      range->SetRequireTLSForAuth(GetJsonBoolValue_(requestBody, "require_tls_for_auth", false));
      range->SetSpamProtection(GetJsonBoolValue_(requestBody, "spam_protection", true));
      range->SetVirusProtection(GetJsonBoolValue_(requestBody, "virus_protection", true));

      String result;
      if (!PersistentSecurityRange::SaveObject(range, result, PersistenceModeNormal))
      {
         AnsiString error;
         error.Format("{\"error\":\"%hs\"}", JsonEscape_(AnsiString(result)).c_str());
         return BuildResponse_(400, error);
      }

      LOG_APPLICATION("RestApi: IP range '" + range->GetName() + "' created.");

      AnsiString body;
      body.Format("{\"id\":%I64d}", range->GetID());
      return BuildResponse_(201, body);
   }

   AnsiString
   RestApiServer::HandleDeleteIpRange_(__int64 rangeId)
   {
      SecurityRanges ranges;
      ranges.Refresh();
      std::shared_ptr<SecurityRange> range = ranges.GetItemByDBID(rangeId);
      if (!range)
         return BuildResponse_(404, "{\"error\":\"ip range not found\"}");

      String name = range->GetName();
      if (!ranges.DeleteItemByDBID(rangeId))
         return BuildResponse_(500, "{\"error\":\"the range could not be deleted\"}");

      LOG_APPLICATION("RestApi: IP range '" + name + "' deleted.");
      return BuildResponse_(200, "{\"deleted\":true}");
   }

   AnsiString
   RestApiServer::HandleListLists_(const String &domainName)
   {
      Domains domains;
      domains.Refresh();
      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      DistributionLists lists(domain->GetID());
      lists.Refresh();

      AnsiString body = "[";
      int count = 0;
      for (int i = 0; i < lists.GetCount(); i++)
      {
         std::shared_ptr<DistributionList> list = lists.GetItem(i);
         if (!list)
            continue;

         AnsiString members = "[";
         std::shared_ptr<DistributionListRecipients> recipients = list->GetMembers();
         if (recipients)
         {
            int memberCount = 0;
            for (int m = 0; m < recipients->GetCount(); m++)
            {
               std::shared_ptr<DistributionListRecipient> recipient = recipients->GetItem(m);
               if (!recipient)
                  continue;
               if (memberCount > 0)
                  members += ",";
               members += "\"" + JsonEscape_(AnsiString(recipient->GetAddress())) + "\"";
               memberCount++;
            }
         }
         members += "]";

         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"address\":\"%hs\",\"active\":%hs,\"require_auth\":%hs,\"members\":%hs}",
            JsonEscape_(AnsiString(list->GetAddress())).c_str(),
            list->GetActive() ? "true" : "false",
            list->GetRequireAuth() ? "true" : "false",
            members.c_str());
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleCreateList_(const String &domainName, const AnsiString &requestBody)
   {
      AnsiString address = GetJsonStringValue_(requestBody, "address");
      if (address.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"address is required\"}");

      String addressDomain = StringParser::ExtractDomain(String(address));
      if (addressDomain.CompareNoCase(domainName) != 0)
         return BuildResponse_(400, "{\"error\":\"address does not belong to the domain\"}");

      Domains domains;
      domains.Refresh();
      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      DistributionLists lists(domain->GetID());
      lists.Refresh();
      if (lists.GetItemByAddress(String(address)))
         return BuildResponse_(409, "{\"error\":\"a list with that address exists\"}");

      std::shared_ptr<DistributionList> list(new DistributionList);
      list->SetDomainID(domain->GetID());
      list->SetAddress(String(address));
      list->SetActive(true);
      list->SetRequireAuth(GetJsonBoolValue_(requestBody, "require_auth", false));
      list->SetListMode(DistributionList::LMPublic);

      String error;
      if (!PersistentDistributionList::SaveObject(list, error, PersistenceModeNormal))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(AnsiString(error)).c_str());
         return BuildResponse_(400, body);
      }

      std::vector<AnsiString> members = GetJsonStringArray_(requestBody, "members");
      int saved = 0;
      for (const AnsiString &member : members)
      {
         if (member.IsEmpty())
            continue;
         std::shared_ptr<DistributionListRecipient> recipient(new DistributionListRecipient);
         recipient->SetListID(list->GetID());
         recipient->SetAddress(String(member));
         if (PersistentDistributionListRecipient::SaveObject(recipient))
            saved++;
      }

      LOG_APPLICATION("RestApi: Distribution list '" + list->GetAddress() + "' created with " + StringParser::IntToString(saved) + " member(s).");

      AnsiString body;
      body.Format("{\"address\":\"%hs\",\"members\":%d}", JsonEscape_(address).c_str(), saved);
      return BuildResponse_(201, body);
   }

   AnsiString
   RestApiServer::HandleDeleteList_(const String &address)
   {
      String domainName = StringParser::ExtractDomain(address);
      Domains domains;
      domains.Refresh();
      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"list not found\"}");

      DistributionLists lists(domain->GetID());
      lists.Refresh();
      std::shared_ptr<DistributionList> list = lists.GetItemByAddress(address);
      if (!list)
         return BuildResponse_(404, "{\"error\":\"list not found\"}");

      if (!lists.DeleteItemByDBID(list->GetID()))
         return BuildResponse_(500, "{\"error\":\"the list could not be deleted\"}");

      LOG_APPLICATION("RestApi: Distribution list '" + address + "' deleted.");
      return BuildResponse_(200, "{\"deleted\":true}");
   }

   AnsiString
   RestApiServer::HandleListCertificates_()
   {
      std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();
      if (!certificates)
         return BuildResponse_(200, "[]");
      certificates->Refresh();

      AnsiString body = "[";
      int count = 0;
      for (int i = 0; i < certificates->GetCount(); i++)
      {
         std::shared_ptr<SSLCertificate> certificate = certificates->GetItem(i);
         if (!certificate)
            continue;
         if (count > 0)
            body += ",";
         // The private key password is deliberately not here: a listing is for
         // finding out what the server has, not for lifting what unlocks it.
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"name\":\"%hs\",\"certificate_file\":\"%hs\",\"private_key_file\":\"%hs\"}",
            certificate->GetID(),
            JsonEscape_(AnsiString(certificate->GetName())).c_str(),
            JsonEscape_(AnsiString(certificate->GetCertificateFile())).c_str(),
            JsonEscape_(AnsiString(certificate->GetPrivateKeyFile())).c_str());
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleDkim_(const String &domainName)
   {
      Domains domains;
      domains.Refresh();
      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      AnsiString body;
      body.Format("{\"domain\":\"%hs\",\"enabled\":%hs,\"selector\":\"%hs\",\"sign_aliases\":%hs,\"private_key_file\":\"%hs\"}",
         JsonEscape_(AnsiString(domain->GetName())).c_str(),
         domain->GetDKIMEnabled() ? "true" : "false",
         JsonEscape_(domain->GetDKIMSelector()).c_str(),
         domain->GetDKIMAliasesEnabled() ? "true" : "false",
         JsonEscape_(AnsiString(domain->GetDKIMPrivateKeyFile())).c_str());
      return BuildResponse_(200, body);
   }

   namespace
   {
      const char *RuleFieldName(RuleCriteria::PredefinedField field)
      {
         switch (field)
         {
         case RuleCriteria::FTFrom: return "from";
         case RuleCriteria::FTTo: return "to";
         case RuleCriteria::FTCC: return "cc";
         case RuleCriteria::FTSubject: return "subject";
         case RuleCriteria::FTBody: return "body";
         case RuleCriteria::FTMessageSize: return "message_size";
         case RuleCriteria::FTRecipientList: return "recipient_list";
         case RuleCriteria::FTDeliveryAttempts: return "delivery_attempts";
         default: return "unknown";
         }
      }

      const char *RuleMatchName(RuleCriteria::MatchType match)
      {
         switch (match)
         {
         case RuleCriteria::Equals: return "equals";
         case RuleCriteria::Contains: return "contains";
         case RuleCriteria::LessThan: return "less_than";
         case RuleCriteria::GreaterThan: return "greater_than";
         case RuleCriteria::MatchesRegEx: return "regex";
         case RuleCriteria::NotContains: return "not_contains";
         case RuleCriteria::NotEquals: return "not_equals";
         case RuleCriteria::Wildcard: return "wildcard";
         default: return "none";
         }
      }

      const char *RuleActionName(RuleAction::Type type)
      {
         switch (type)
         {
         case RuleAction::Delete: return "delete";
         case RuleAction::Forward: return "forward";
         case RuleAction::Reply: return "reply";
         case RuleAction::MoveToIMAPFolder: return "move_to_folder";
         case RuleAction::ScriptFunction: return "script_function";
         case RuleAction::StopRuleProcessing: return "stop";
         case RuleAction::SetHeaderValue: return "set_header";
         case RuleAction::SendUsingRoute: return "send_using_route";
         case RuleAction::CreateCopy: return "copy";
         case RuleAction::BindToAddress: return "bind_to_address";
         default: return "unknown";
         }
      }
   }

   AnsiString
   RestApiServer::HandleListRules_()
   {
      // The global rules, from the cache the delivery path itself reads, so the
      // answer is what is in force and not a second reading of the table.
      std::shared_ptr<Rules> rules = ObjectCache::Instance()->GetGlobalRules();
      if (!rules)
         return BuildResponse_(200, "[]");

      AnsiString body = "[";
      int count = 0;
      for (int i = 0; i < rules->GetCount(); i++)
      {
         std::shared_ptr<Rule> rule = rules->GetItem(i);
         if (!rule)
            continue;

         AnsiString criteria = "[";
         std::shared_ptr<RuleCriterias> criterias = rule->GetCriterias();
         if (criterias)
         {
            int criterionCount = 0;
            for (int c = 0; c < criterias->GetCount(); c++)
            {
               std::shared_ptr<RuleCriteria> criterion = criterias->GetItem(c);
               if (!criterion)
                  continue;
               if (criterionCount > 0)
                  criteria += ",";
               AnsiString entry;
               entry.Format("{\"field\":\"%hs\",\"header\":\"%hs\",\"match\":\"%hs\",\"value\":\"%hs\"}",
                  criterion->GetUsePredefined() ? RuleFieldName(criterion->GetPredefinedField()) : "header",
                  JsonEscape_(AnsiString(criterion->GetHeaderField())).c_str(),
                  RuleMatchName(criterion->GetMatchType()),
                  JsonEscape_(AnsiString(criterion->GetMatchValue())).c_str());
               criteria += entry;
               criterionCount++;
            }
         }
         criteria += "]";

         AnsiString actions = "[";
         std::shared_ptr<RuleActions> ruleActions = rule->GetActions();
         if (ruleActions)
         {
            int actionCount = 0;
            for (int a = 0; a < ruleActions->GetCount(); a++)
            {
               std::shared_ptr<RuleAction> action = ruleActions->GetItem(a);
               if (!action)
                  continue;
               if (actionCount > 0)
                  actions += ",";
               // One "value" per action, the one that matters for its type - the
               // folder for a move, the address for a forward, the function for a
               // script, the route for send-using-route - so the listing reads
               // without a schema per action.
               AnsiString value;
               switch (action->GetType())
               {
               case RuleAction::MoveToIMAPFolder: value = AnsiString(action->GetIMAPFolder()); break;
               case RuleAction::Forward: value = AnsiString(action->GetTo()); break;
               case RuleAction::ScriptFunction: value = AnsiString(action->GetScriptFunction()); break;
               case RuleAction::SetHeaderValue: value = AnsiString(action->GetSubject()); break;
               case RuleAction::CreateCopy: value = AnsiString(action->GetIMAPFolder()); break;
               default: break;
               }
               AnsiString entry;
               entry.Format("{\"type\":\"%hs\",\"value\":\"%hs\"}",
                  RuleActionName(action->GetType()), JsonEscape_(value).c_str());
               actions += entry;
               actionCount++;
            }
         }
         actions += "]";

         if (count > 0)
            body += ",";
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"name\":\"%hs\",\"active\":%hs,\"all_criteria\":%hs,\"criteria\":%hs,\"actions\":%hs}",
            rule->GetID(),
            JsonEscape_(AnsiString(rule->GetName())).c_str(),
            rule->GetActive() ? "true" : "false",
            rule->GetUseAND() ? "true" : "false",
            criteria.c_str(), actions.c_str());
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   bool
   RestApiServer::IsSafeLogName_(const AnsiString &name)
   {
      // A log file name and nothing else: no separators, no dot-dot, no leading
      // dot, and the .log suffix the logger writes. The list route is the only
      // source of names a client should have, and this is what it hands out.
      if (name.IsEmpty() || name.GetLength() > 128)
         return false;
      if (!name.EndsWith(".log"))
         return false;
      if (name.Find("..") >= 0 || name[0] == '.')
         return false;
      for (int i = 0; i < name.GetLength(); i++)
      {
         char c = name[i];
         bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                   c == '_' || c == '-' || c == '.';
         if (!ok)
            return false;
      }
      return true;
   }

   AnsiString
   RestApiServer::HandleListLogs_()
   {
      String directory = IniFileSettings::Instance()->GetLogDirectory();
      std::vector<FileInfo> files = FileUtilities::GetFilesInDirectory(directory, _T(".*\\.log"));

      AnsiString body = "[";
      int count = 0;
      for (FileInfo &file : files)
      {
         AnsiString name(file.GetName());
         if (!IsSafeLogName_(name))
            continue;
         String path = FileUtilities::Combine(directory, file.GetName());
         unsigned __int64 size = 0;
         FileUtilities::FileSize64(path, size);
         if (count > 0)
            body += ",";
         AnsiString entry;
         entry.Format("{\"name\":\"%hs\",\"size\":%I64u,\"created\":\"%hs\"}",
            JsonEscape_(name).c_str(),
            size,
            JsonEscape_(AnsiString(Time::GetTimeStampFromDateTime(file.GetCreateTime()))).c_str());
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleLogTail_(const AnsiString &name, const AnsiString &query)
   {
      if (!IsSafeLogName_(name))
         return BuildResponse_(400, "{\"error\":\"not a log file name\"}");

      int lines = 200;
      AnsiString linesText = QueryParameter_(query, "lines");
      if (!linesText.IsEmpty())
      {
         lines = atoi(linesText.c_str());
         if (lines < 1)
            lines = 1;
         if (lines > 2000)
            lines = 2000;
      }

      String path = FileUtilities::Combine(IniFileSettings::Instance()->GetLogDirectory(), String(name));
      if (!FileUtilities::Exists(path))
         return BuildResponse_(404, "{\"error\":\"no such log file\"}");

      // Read the tail only: a busy day's log is hundreds of megabytes and the
      // request thread is the single REST worker. 512 KB holds 2000 lines of
      // anything this logger writes.
      const std::streamoff MaxBytes = 512 * 1024;
      std::ifstream stream(path.c_str(), std::ios::binary);
      if (!stream)
         return BuildResponse_(404, "{\"error\":\"no such log file\"}");
      // The backup log is written as UTF-16 with a byte-order mark; the others
      // are narrow. Decode by what the file says it is, not by its name.
      unsigned char bom[2] = { 0, 0 };
      stream.read(reinterpret_cast<char *>(bom), 2);
      bool utf16 = stream.gcount() == 2 && bom[0] == 0xFF && bom[1] == 0xFE;
      stream.clear();
      stream.seekg(0, std::ios::end);
      std::streamoff size = stream.tellg();
      std::streamoff start = size > MaxBytes ? size - MaxBytes : 0;
      if (utf16 && (start % 2) != 0)
         start++;   // stay on a code-unit boundary
      stream.seekg(start, std::ios::beg);
      std::string chunk;
      chunk.resize(static_cast<size_t>(size - start));
      if (!chunk.empty())
         stream.read(&chunk[0], chunk.size());

      std::string text;
      if (utf16)
      {
         size_t offset = start == 0 ? 2 : 0;
         if (chunk.size() > offset)
         {
            std::wstring wide(reinterpret_cast<const wchar_t *>(chunk.data() + offset), (chunk.size() - offset) / 2);
            AnsiString narrow(String(wide.c_str()));
            text = narrow.c_str();
         }
      }
      else
      {
         text.swap(chunk);
      }

      std::vector<std::string> all;
      size_t position = 0;
      while (position < text.size())
      {
         size_t end = text.find('\n', position);
         if (end == std::string::npos)
            end = text.size();
         std::string line = text.substr(position, end - position);
         if (!line.empty() && line[line.size() - 1] == '\r')
            line.erase(line.size() - 1);
         all.push_back(line);
         position = end + 1;
      }
      // A partial first line when the tail started mid-line, and a trailing
      // empty one from the final newline, are both noise.
      if (start > 0 && !all.empty())
         all.erase(all.begin());
      if (!all.empty() && all.back().empty())
         all.pop_back();

      size_t first = all.size() > static_cast<size_t>(lines) ? all.size() - lines : 0;
      AnsiString body;
      body.Format("{\"name\":\"%hs\",\"lines\":[", JsonEscape_(name).c_str());
      for (size_t i = first; i < all.size(); i++)
      {
         if (i > first)
            body += ",";
         body += "\"" + JsonEscape_(AnsiString(all[i].c_str())) + "\"";
      }
      body += "]}";
      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleBackupStart_()
   {
      std::shared_ptr<BackupManager> manager = Application::Instance()->GetBackupManager();
      if (!manager)
         return BuildResponse_(503, "{\"error\":\"the backup manager is not running\"}");

      if (!manager->StartBackup())
      {
         AnsiString body;
         body.Format("{\"error\":\"the backup did not start\",\"status\":\"%hs\"}",
            JsonEscape_(AnsiString(manager->GetStatus())).c_str());
         return BuildResponse_(409, body);
      }

      LOG_APPLICATION("RestApi: Backup started.");
      return BuildResponse_(202, "{\"started\":true}");
   }

   AnsiString
   RestApiServer::HandleBackupStatus_()
   {
      std::shared_ptr<BackupManager> manager = Application::Instance()->GetBackupManager();
      if (!manager)
         return BuildResponse_(503, "{\"error\":\"the backup manager is not running\"}");

      // The manager's status text holds the last failure reason; the progress
      // of a backup goes to the backup log, which is what the Control Panel and
      // the regression fixtures read. Both are here: status, and the last
      // twenty lines of hmailserver_backup.log, newest last.
      AnsiString body;
      body.Format("{\"status\":\"%hs\",\"log\":[", JsonEscape_(AnsiString(manager->GetStatus())).c_str());

      String logPath = FileUtilities::Combine(IniFileSettings::Instance()->GetLogDirectory(), _T("hmailserver_backup.log"));
      if (FileUtilities::Exists(logPath))
      {
         AnsiString text(FileUtilities::ReadCompleteTextFile(logPath));
         std::vector<AnsiString> lines;
         int position = 0;
         while (position < text.GetLength())
         {
            int end = text.Find("\n", position);
            if (end < 0)
               end = text.GetLength();
            AnsiString line = text.Mid(position, end - position);
            line.TrimRight("\r");
            if (!line.IsEmpty())
               lines.push_back(line);
            position = end + 1;
         }
         size_t first = lines.size() > 20 ? lines.size() - 20 : 0;
         for (size_t i = first; i < lines.size(); i++)
         {
            if (i > first)
               body += ",";
            body += "\"" + JsonEscape_(lines[i]) + "\"";
         }
      }
      body += "]}";
      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleSettings_()
   {
      // A snapshot of the settings an operator asks about first, and nothing
      // that unlocks anything: no passwords, no keys, no tokens. Writing
      // settings stays with COM and the Control Panel, where each one is
      // validated by the code that owns it.
      Configuration *configuration = Configuration::Instance();
      std::shared_ptr<SMTPConfiguration> smtp = configuration->GetSMTPConfiguration();
      std::shared_ptr<IMAPConfiguration> imap = configuration->GetIMAPConfiguration();
      std::shared_ptr<POP3Configuration> pop3 = configuration->GetPOP3Configuration();

      AnsiString body;
      body.Format("{\"host_name\":\"%hs\",\"default_domain\":\"%hs\",\"max_message_size_kb\":%d,"
                  "\"max_smtp_connections\":%d,\"max_imap_connections\":%d,\"max_pop3_connections\":%d,"
                  "\"smtp_relayer\":\"%hs\",\"smtp_relayer_port\":%d,"
                  "\"log_smtp_conversations\":%hs,\"log_imap_conversations\":%hs}",
         JsonEscape_(AnsiString(configuration->GetHostName())).c_str(),
         JsonEscape_(AnsiString(configuration->GetDefaultDomain())).c_str(),
         smtp ? smtp->GetMaxMessageSize() : 0,
         smtp ? smtp->GetMaxSMTPConnections() : 0,
         imap ? (int) imap->GetMaxIMAPConnections() : 0,
         pop3 ? (int) pop3->GetMaxPOP3Connections() : 0,
         smtp ? JsonEscape_(AnsiString(smtp->GetSMTPRelayer())).c_str() : "",
         smtp ? (int) smtp->GetSMTPRelayerPort() : 0,
         configuration->GetLogSMTPConversations() ? "true" : "false",
         configuration->GetLogIMAPConversations() ? "true" : "false");
      return BuildResponse_(200, body);
   }

   bool
   RestApiServer::GetJsonBoolValue_(const AnsiString &json, const AnsiString &key, bool defaultValue)
   {
      AnsiString needle = "\"" + key + "\"";
      int keyPosition = json.Find(needle);
      if (keyPosition < 0)
         return defaultValue;
      int colon = json.Find(":", keyPosition + needle.GetLength());
      if (colon < 0)
         return defaultValue;
      for (int i = colon + 1; i < json.GetLength(); i++)
      {
         char c = json[i];
         if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            continue;
         if (json.Mid(i, 4) == "true")
            return true;
         if (json.Mid(i, 5) == "false")
            return false;
         break;
      }
      return defaultValue;
   }

   std::vector<AnsiString>
   RestApiServer::GetJsonStringArray_(const AnsiString &json, const AnsiString &key)
   {
      // The strings of a flat array: "members":["a@x","b@x"]. Nothing nested,
      // which is all the bodies this server accepts contain.
      std::vector<AnsiString> values;
      AnsiString needle = "\"" + key + "\"";
      int keyPosition = json.Find(needle);
      if (keyPosition < 0)
         return values;
      int open = json.Find("[", keyPosition + needle.GetLength());
      if (open < 0)
         return values;
      int close = json.Find("]", open);
      if (close < 0)
         return values;

      AnsiString current;
      bool inString = false;
      for (int i = open + 1; i < close; i++)
      {
         char c = json[i];
         if (inString)
         {
            if (c == '\\' && i + 1 < close)
            {
               current += json[i + 1];
               i++;
               continue;
            }
            if (c == '\"')
            {
               values.push_back(current);
               current = "";
               inString = false;
               continue;
            }
            current += c;
            continue;
         }
         if (c == '\"')
            inString = true;
      }
      return values;
   }

   AnsiString
   RestApiServer::HandleOpenApi_()
   {
      // The description lives here, beside the router it describes, so a route
      // change and its documentation change land in the same diff - a separate
      // file would drift the way every hand-maintained count in this project
      // has. Kept to OpenAPI 3.0 syntax and deliberately terse: the reference
      // for behaviour is the server, and this is the map, not the territory.
      static const char *openApiJson =
         "{"
         "\"openapi\":\"3.0.3\","
         "\"info\":{\"title\":\"hMailServer REST API\",\"version\":\"1\","
         "\"description\":\"Administration API. Authenticate with the administrator password (HTTP Basic, user 'Administrator') or an API key (Bearer). API keys can be read-only or restricted to named domains; key management itself requires the administrator password.\"},"
         "\"paths\":{"
         "\"/api/v1/status\":{\"get\":{\"summary\":\"Server status\",\"responses\":{\"200\":{\"description\":\"Status, state and uptime\"}}}},"
         "\"/api/v1/domains\":{\"get\":{\"summary\":\"List domains\",\"description\":\"A domain-restricted key sees only its own domains.\",\"responses\":{\"200\":{\"description\":\"Array of domains\"}}}},"
         "\"/api/v1/domains/{domain}/accounts\":{"
         "\"get\":{\"summary\":\"List accounts in a domain\",\"responses\":{\"200\":{\"description\":\"Array of accounts\"},\"404\":{\"description\":\"Unknown domain\"}}},"
         "\"post\":{\"summary\":\"Create an account\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"address\",\"password\"],\"properties\":{\"address\":{\"type\":\"string\"},\"password\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"},\"maxSizeMB\":{\"type\":\"integer\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created\"},\"400\":{\"description\":\"Malformed request\"},\"404\":{\"description\":\"Unknown domain\"}}}},"
         "\"/api/v1/accounts/{address}\":{\"delete\":{\"summary\":\"Delete an account\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown account\"}}}},"
         "\"/api/v1/queue\":{\"get\":{\"summary\":\"List the delivery queue\",\"description\":\"Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of queued messages\"}}}},"
         "\"/api/v1/queue/{id}/retry\":{\"post\":{\"summary\":\"Retry a queued message now\",\"responses\":{\"200\":{\"description\":\"Rescheduled\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/queue/{id}\":{\"delete\":{\"summary\":\"Remove a message from the queue\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/quarantine\":{\"get\":{\"summary\":\"List quarantined messages\",\"description\":\"Server-wide; refused for domain-restricted keys. Bounded to the newest 1000.\",\"responses\":{\"200\":{\"description\":\"Array of quarantined messages\"}}}},"
         "\"/api/v1/quarantine/{id}/release\":{\"post\":{\"summary\":\"Release a quarantined message to its original recipients\",\"description\":\"Delivery is direct rather than back through the filters: a release is an administrator overruling them.\",\"responses\":{\"200\":{\"description\":\"Released\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/quarantine/{id}\":{\"delete\":{\"summary\":\"Delete a quarantined message\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/domains/{domain}/aliases\":{\"get\":{\"summary\":\"List aliases in a domain\",\"responses\":{\"200\":{\"description\":\"Array of aliases\"},\"404\":{\"description\":\"Unknown domain\"}}}},"
         "\"/api/v1/tlsa\":{\"get\":{\"summary\":\"Recommended DANE TLSA records for the configured certificates\",\"responses\":{\"200\":{\"description\":\"Array of TLSA records\"}}}},"
         "\"/api/v1/srv\":{\"get\":{\"summary\":\"Recommended client-discovery SRV records (RFC 6186/8314 and Outlook autodiscover) for the enabled listeners\",\"description\":\"One record set per active domain; a domain-restricted key sees only its own domains. Only services that are enabled and not loopback-bound are advertised.\",\"responses\":{\"200\":{\"description\":\"Array of SRV records\"}}}},"
         "\"/api/v1/metrics/history\":{\"get\":{\"summary\":\"The history of one metric\",\"description\":\"Query parameters: metric (a name from the exporter without the hmailserver_ prefix, e.g. sessions_smtp or processed_messages_total) and range (24h, 7d or 30d - samples averaged per minute, per ten minutes or per hour). Counters are totals; a rate is the difference between two samples. Empty when MetricsHistoryDays is 0.\",\"responses\":{\"200\":{\"description\":\"The samples\"},\"400\":{\"description\":\"Unknown metric or range\"}}}},"
         "\"/api/v1/apikeys\":{"
         "\"get\":{\"summary\":\"List API keys\",\"description\":\"Administrator password only.\",\"responses\":{\"200\":{\"description\":\"Array of keys, never the clear-text tokens\"}}},"
         "\"post\":{\"summary\":\"Create an API key\",\"description\":\"Administrator password only. The token is returned once, at creation.\",\"responses\":{\"201\":{\"description\":\"Created\"}}}},"
         "\"/api/v1/apikeys/{id}\":{\"delete\":{\"summary\":\"Revoke an API key\",\"description\":\"Administrator password only.\",\"responses\":{\"200\":{\"description\":\"Revoked\"}}}},"
         "\"/api/v1/ipranges\":{"
         "\"get\":{\"summary\":\"List the IP ranges\",\"description\":\"Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of ranges with their permissions\"}}},"
         "\"post\":{\"summary\":\"Create an IP range\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\",\"lower\",\"upper\"],\"properties\":{\"name\":{\"type\":\"string\"},\"lower\":{\"type\":\"string\"},\"upper\":{\"type\":\"string\"},\"priority\":{\"type\":\"integer\"},\"allow_smtp\":{\"type\":\"boolean\"},\"allow_imap\":{\"type\":\"boolean\"},\"allow_pop3\":{\"type\":\"boolean\"},\"deliver_local_to_local\":{\"type\":\"boolean\"},\"deliver_local_to_remote\":{\"type\":\"boolean\"},\"deliver_remote_to_local\":{\"type\":\"boolean\"},\"deliver_remote_to_remote\":{\"type\":\"boolean\"},\"require_auth_local_to_local\":{\"type\":\"boolean\"},\"require_auth_local_to_remote\":{\"type\":\"boolean\"},\"require_auth_remote_to_local\":{\"type\":\"boolean\"},\"require_auth_remote_to_remote\":{\"type\":\"boolean\"},\"require_tls_for_auth\":{\"type\":\"boolean\"},\"spam_protection\":{\"type\":\"boolean\"},\"virus_protection\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created, with its id\"},\"400\":{\"description\":\"Missing name or an address that does not parse\"}}}},"
         "\"/api/v1/ipranges/{id}\":{\"delete\":{\"summary\":\"Delete an IP range\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/domains/{domain}/lists\":{"
         "\"get\":{\"summary\":\"List the distribution lists in a domain, with their members\",\"responses\":{\"200\":{\"description\":\"Array of lists\"},\"404\":{\"description\":\"Unknown domain\"}}},"
         "\"post\":{\"summary\":\"Create a distribution list\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"address\"],\"properties\":{\"address\":{\"type\":\"string\"},\"members\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"require_auth\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created\"},\"400\":{\"description\":\"Missing address, or one outside the domain\"},\"404\":{\"description\":\"Unknown domain\"},\"409\":{\"description\":\"A list with that address exists\"}}}},"
         "\"/api/v1/lists/{address}\":{\"delete\":{\"summary\":\"Delete a distribution list\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown list\"}}}},"
         "\"/api/v1/certificates\":{\"get\":{\"summary\":\"List the SSL certificates\",\"description\":\"Names and file paths, never a private key password. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of certificates\"}}}},"
         "\"/api/v1/domains/{domain}/dkim\":{\"get\":{\"summary\":\"The DKIM signing configuration of a domain\",\"responses\":{\"200\":{\"description\":\"enabled, selector, sign_aliases and the private key file\"},\"404\":{\"description\":\"Unknown domain\"}}}},"
         "\"/api/v1/rules\":{\"get\":{\"summary\":\"List the global rules with their criteria and actions\",\"description\":\"Read-only. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of rules\"}}}},"
         "\"/api/v1/logs\":{\"get\":{\"summary\":\"List the log files\",\"description\":\"Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of files with size and creation time\"}}}},"
         "\"/api/v1/logs/{name}\":{\"get\":{\"summary\":\"The last lines of a log file\",\"description\":\"Query parameter lines (default 200, at most 2000). The name must be one the list returns; anything with a path in it is refused.\",\"responses\":{\"200\":{\"description\":\"The lines, newest last\"},\"400\":{\"description\":\"Not a log file name\"},\"404\":{\"description\":\"No such log file\"}}}},"
         "\"/api/v1/backup\":{"
         "\"get\":{\"summary\":\"The backup manager's status text and the last lines of the backup log\",\"responses\":{\"200\":{\"description\":\"status (the last failure reason, if any) and log (the backup log's tail, newest last)\"}}},"
         "\"post\":{\"summary\":\"Start a backup with the configured settings\",\"description\":\"Runs on the maintenance queue; poll GET for the outcome.\",\"responses\":{\"202\":{\"description\":\"Started\"},\"409\":{\"description\":\"A backup or restore is already running, or the backup is not configured\"}}}},"
         "\"/api/v1/settings\":{\"get\":{\"summary\":\"A read-only snapshot of the server-wide settings\",\"description\":\"Host name, default domain, size and connection limits, the relay host, the conversation-logging switches. No secrets. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"The snapshot\"}}}},"
         "\"/api/v1/openapi.json\":{\"get\":{\"summary\":\"This document\",\"responses\":{\"200\":{\"description\":\"The OpenAPI description\"}}}}"
         "},"
         "\"components\":{\"securitySchemes\":{"
         "\"basic\":{\"type\":\"http\",\"scheme\":\"basic\"},"
         "\"bearer\":{\"type\":\"http\",\"scheme\":\"bearer\"}}},"
         "\"security\":[{\"basic\":[]},{\"bearer\":[]}]"
         "}";

      return BuildResponse_(200, AnsiString(openApiJson));
   }

   AnsiString
   RestApiServer::HandleTlsa_()
   {
      // Recommended DANE TLSA records (3 1 1: DANE-EE, SPKI, SHA-256) for
      // every configured certificate, so administrators can publish or
      // verify DNS without manual hashing.
      AnsiString hostName = AnsiString(Configuration::Instance()->GetHostName());
      if (hostName.IsEmpty())
         hostName = "<your-mx-hostname>";

      AnsiString items;
      int count = 0;

      auto appendCertificate = [&](const String &name, const String &certificateFile)
      {
         if (certificateFile.IsEmpty() || !FileUtilities::Exists(certificateFile))
            return;

         AnsiString spkiHex;
         if (!AcmeClient::GetCertificateTlsa(certificateFile, spkiHex))
            return;

         AnsiString item;
         item.Format("{\"certificate\":\"%hs\",\"spki_sha256\":\"%hs\",\"record\":\"_25._tcp.%hs. IN TLSA 3 1 1 %hs\"}",
            JsonEscape_(AnsiString(name)).c_str(),
            spkiHex.c_str(),
            JsonEscape_(hostName).c_str(),
            spkiHex.c_str());

         if (count > 0)
            items += ",";

         items += item;
         count++;
      };

      SSLCertificates certificates;
      certificates.Refresh();

      for (int i = 0; i < certificates.GetCount(); i++)
      {
         std::shared_ptr<SSLCertificate> certificate = certificates.GetItem(i);
         if (certificate)
            appendCertificate(certificate->GetName(), certificate->GetCertificateFile());
      }

      if (count == 0)
         appendCertificate(_T("ACME (automatic)"), AcmeClient::GetCertificateDirectory() + _T("\\fullchain.pem"));

      AnsiString body;
      body.Format("{\"host\":\"%hs\",\"count\":%d,\"records\":[%hs]}",
         JsonEscape_(hostName).c_str(), count, items.c_str());

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::QueryParameter_(const AnsiString &query, const AnsiString &name)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The value of one query-string parameter, or "". Enough decoding for the
   // values these routes take (a metric name, a range): '+' and %XX. Anything a
   // caller could smuggle past that is refused by the handler's own validation.
   //---------------------------------------------------------------------------()
   {
      std::vector<AnsiString> pairs = StringParser::SplitString(query, "&");

      for (const AnsiString &pair : pairs)
      {
         int equals = pair.Find("=");
         AnsiString key = equals >= 0 ? pair.Mid(0, equals) : pair;

         if (key.CompareNoCase(name) != 0)
            continue;

         AnsiString raw = equals >= 0 ? pair.Mid(equals + 1) : AnsiString();
         AnsiString value;

         for (int i = 0; i < raw.GetLength(); i++)
         {
            char c = raw.GetAt(i);

            if (c == '+')
            {
               value += " ";
            }
            else if (c == '%' && i + 2 < raw.GetLength())
            {
               char hex[3] = { raw.GetAt(i + 1), raw.GetAt(i + 2), 0 };
               value += (char) strtol(hex, nullptr, 16);
               i += 2;
            }
            else
            {
               value += c;
            }
         }

         return value;
      }

      return "";
   }

   AnsiString
   RestApiServer::HandleMetricsHistory_(const AnsiString &query)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // GET /api/v1/metrics/history?metric=<name>&range=24h|7d|30d. The three ranges
   // are the three views: a minute, ten minutes and an hour per point, so that
   // each answer is at most a few hundred points however long the retention.
   //---------------------------------------------------------------------------()
   {
      AnsiString metric = QueryParameter_(query, "metric");
      AnsiString range = QueryParameter_(query, "range");

      if (metric.IsEmpty() || !MetricsHistoryTask::IsMetricName(String(metric)))
      {
         AnsiString names;

         for (const AnsiString &name : MetricsHistoryTask::MetricNames())
            names += (names.IsEmpty() ? "\"" : ",\"") + name + "\"";

         return BuildResponse_(400, "{\"error\":\"unknown metric\",\"metrics\":[" + names + "]}");
      }

      int minutesBack = 24 * 60;
      int bucketMinutes = 1;

      if (range.IsEmpty() || range.CompareNoCase("24h") == 0)
      {
         minutesBack = 24 * 60;
         bucketMinutes = 1;
      }
      else if (range.CompareNoCase("7d") == 0)
      {
         minutesBack = 7 * 24 * 60;
         bucketMinutes = 10;
      }
      else if (range.CompareNoCase("30d") == 0)
      {
         minutesBack = 30 * 24 * 60;
         bucketMinutes = 60;
      }
      else
      {
         return BuildResponse_(400, "{\"error\":\"unknown range: use 24h, 7d or 30d\"}");
      }

      return BuildResponse_(200, MetricsHistoryTask::QueryAsJson(String(metric), minutesBack, bucketMinutes));
   }

   AnsiString
   RestApiServer::HandleSrv_(const std::vector<String> &allowedDomains)
   {
      // Ready-to-publish client-discovery SRV records: RFC 6186 for
      // _imap/_imaps/_pop3/_pop3s/_submission, RFC 8314 section 5.1 for
      // _submissions, and the Outlook _autodiscover._tcp convention. Built
      // from the ports that are actually configured and enabled - the same
      // one-source-of-truth idea as /api/v1/tlsa, which hashes the real
      // certificates rather than restating them, and the same port ranking as
      // the autoconfig XML, so DNS discovery and autoconfig cannot disagree.
      //
      // A record is only emitted for a service that is genuinely served,
      // because a published SRV pointing at a dead port sends every client of
      // that domain to a socket that will never answer - worse than publishing
      // nothing, which at least leaves clients to their fallback probing. So:
      // a protocol whose service is switched off contributes nothing (its port
      // rows survive in the configuration, but no listener starts for them),
      // and a port bound to a loopback address contributes nothing (the only
      // clients that resolve SRV records are on other machines). What this
      // reads is the configuration, exactly as the autoconfig handlers do; a
      // port added since the last restart is advertised even though its
      // listener starts at the next one, which is the established semantics of
      // every client-discovery answer this server gives.
      AnsiString target = AnsiString(IniFileSettings::Instance()->GetAutoconfigClientHost());
      target.Trim();

      if (target.IsEmpty())
         target = AnsiString(Configuration::Instance()->GetHostName());

      target.Trim();
      target.MakeLower();

      // The placeholder convention of /api/v1/tlsa: with no host name
      // configured there is nothing honest to point a record at, so the
      // records carry a placeholder the administrator has to replace.
      if (target.IsEmpty())
         target = "<your-mail-hostname>";

      bool smtpEnabled = Configuration::Instance()->GetUseSMTP();
      bool imapEnabled = Configuration::Instance()->GetUseIMAP();
      bool pop3Enabled = Configuration::Instance()->GetUsePOP3();

      // Per protocol, the best implicit-TLS port and the best
      // STARTTLS-or-plain port. Ranked the way GetClientAccessSettings_ ranks
      // the autoconfig answer: required STARTTLS over optional over plain, and
      // the standard port over an unusual one.
      int imapsPort = 0, imapsRank = -1;
      int imapPort = 0, imapRank = -1;
      int pop3sPort = 0, pop3sRank = -1;
      int pop3Port = 0, pop3Rank = -1;
      int submissionsPort = 0, submissionsRank = -1;
      int submissionPort = 0, submissionRank = -1;

      auto consider = [](int &bestPort, int &bestRank, int candidatePort, int candidateRank)
      {
         if (candidateRank > bestRank)
         {
            bestRank = candidateRank;
            bestPort = candidatePort;
         }
      };

      // A copy of the vector, exactly as GetClientAccessSettings_ takes one,
      // rather than a Refresh() of the shared collection from this thread.
      std::vector<std::shared_ptr<TCPIPPort>> ports = Configuration::Instance()->GetTCPIPPorts()->GetVector();

      for (std::shared_ptr<TCPIPPort> port : ports)
      {
         if (!port)
            continue;

         // Bound to loopback: real, but unreachable from any machine that
         // would be resolving the record.
         if (port->GetAddress().GetAddress().is_loopback())
            continue;

         int portNumber = port->GetPortNumber();
         ConnectionSecurity security = port->GetConnectionSecurity();
         bool implicitTls = security == CSSSL;

         int starttlsRank = security == CSSTARTTLSRequired ? 30 :
                            security == CSSTARTTLSOptional ? 20 : 10;

         switch (port->GetProtocol())
         {
         case STIMAP:
            if (!imapEnabled)
               break;

            if (implicitTls)
               consider(imapsPort, imapsRank, portNumber, portNumber == 993 ? 2 : 1);
            else
               consider(imapPort, imapRank, portNumber, starttlsRank + (portNumber == 143 ? 5 : 0));
            break;

         case STPOP3:
            if (!pop3Enabled)
               break;

            if (implicitTls)
               consider(pop3sPort, pop3sRank, portNumber, portNumber == 995 ? 2 : 1);
            else
               consider(pop3Port, pop3Rank, portNumber, starttlsRank + (portNumber == 110 ? 5 : 0));
            break;

         case STSMTP:
            if (!smtpEnabled)
               break;

            // Port 25 is the server-to-server MX port, whatever its security
            // setting says. Advertising it for client submission would invite
            // every discovered client onto the port the MX record owns, which
            // is exactly the confusion RFC 6186 discovery exists to end.
            if (portNumber == 25)
               break;

            if (implicitTls)
               consider(submissionsPort, submissionsRank, portNumber, portNumber == 465 ? 2 : 1);
            else
               consider(submissionPort, submissionRank, portNumber, starttlsRank + (portNumber == 587 ? 5 : 0));
            break;

         default:
            break;
         }
      }

      struct ServiceRecord
      {
         AnsiString service;
         int priority;
         int weight;
         int port;
      };

      // Priority 0, weight 1 - the published RFC 6186 example form. With one
      // record per service name the two values carry no load anyway.
      std::vector<ServiceRecord> services;

      if (imapsPort > 0)
         services.push_back({ "_imaps._tcp", 0, 1, imapsPort });
      if (imapPort > 0)
         services.push_back({ "_imap._tcp", 0, 1, imapPort });
      if (pop3sPort > 0)
         services.push_back({ "_pop3s._tcp", 0, 1, pop3sPort });
      if (pop3Port > 0)
         services.push_back({ "_pop3._tcp", 0, 1, pop3Port });
      if (submissionsPort > 0)
         services.push_back({ "_submissions._tcp", 0, 1, submissionsPort });
      if (submissionPort > 0)
         services.push_back({ "_submission._tcp", 0, 1, submissionPort });

      // _autodiscover._tcp points Outlook at the web-services HTTPS listener.
      // The condition is the listener that is RUNNING, not the one configured:
      // WebServicesServer silently keeps HTTPS down when no certificate is
      // available yet (ACME may not have issued one), and an SRV record
      // pointing into that gap would break every Outlook profile setup until
      // it closed. Priority 0 weight 0, the form Microsoft's own documentation
      // publishes. Loopback binds are excluded for the same reason as the mail
      // ports above.
      int autodiscoverPort = WebServicesServer::GetHttpsListenPort();

      if (autodiscoverPort > 0 && IniFileSettings::Instance()->GetAutoconfigEnabled())
      {
         String webBindAddress = IniFileSettings::Instance()->GetWebServicesBindAddress();

         IPAddress parsedBind;
         bool loopbackBound = webBindAddress == _T("localhost") ||
            (parsedBind.TryParse(AnsiString(webBindAddress), false) && parsedBind.GetAddress().is_loopback());

         if (!loopbackBound)
            services.push_back({ "_autodiscover._tcp", 0, 0, autodiscoverPort });
      }

      AnsiString serviceJson;
      int serviceCount = 0;

      for (const ServiceRecord &service : services)
      {
         AnsiString entry;
         entry.Format("{\"service\":\"%hs\",\"priority\":%d,\"weight\":%d,\"port\":%d}",
            service.service.c_str(), service.priority, service.weight, service.port);

         if (serviceCount > 0)
            serviceJson += ",";

         serviceJson += entry;
         serviceCount++;
      }

      // One record set per active local domain: SRV owner names live under the
      // mail domain, not under the server's host name, so this is the shape an
      // administrator actually pastes into a zone file.
      Domains domains;
      domains.Refresh();

      AnsiString recordJson;
      int recordCount = 0;

      for (int i = 0; i < domains.GetCount(); i++)
      {
         std::shared_ptr<Domain> domain = domains.GetItem(i);
         if (!domain)
            continue;

         // An inactive domain accepts no mail; nothing should be inviting
         // clients to configure accounts in it.
         if (!domain->GetIsActive())
            continue;

         // The same filtering as the domain listing, for the same reason: a
         // key issued for one customer's domain must not be handed the names
         // of all the others as a side effect of asking for DNS records.
         if (!IsDomainAllowed_(allowedDomains, domain->GetName()))
            continue;

         AnsiString domainName = JsonEscape_(AnsiString(domain->GetName()));

         for (const ServiceRecord &service : services)
         {
            AnsiString entry;
            entry.Format("{\"domain\":\"%hs\",\"service\":\"%hs\",\"record\":\"%hs.%hs. IN SRV %d %d %d %hs.\"}",
               domainName.c_str(), service.service.c_str(),
               service.service.c_str(), domainName.c_str(),
               service.priority, service.weight, service.port,
               JsonEscape_(target).c_str());

            if (recordCount > 0)
               recordJson += ",";

            recordJson += entry;
            recordCount++;
         }
      }

      AnsiString body;
      body.Format("{\"target\":\"%hs\",\"count\":%d,\"services\":[%hs],\"records\":[%hs]}",
         JsonEscape_(target).c_str(), recordCount, serviceJson.c_str(), recordJson.c_str());

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::GetRequestBody_(const AnsiString &request)
   {
      int bodyStart = request.Find("\r\n\r\n");
      if (bodyStart < 0)
         return "";

      return request.Mid(bodyStart + 4);
   }

   AnsiString
   RestApiServer::GetJsonStringValue_(const AnsiString &json, const AnsiString &key)
   {
      AnsiString needle = "\"" + key + "\"";

      int keyPosition = json.Find(needle);
      if (keyPosition < 0)
         return "";

      int colonPosition = json.Find(":", keyPosition + needle.GetLength());
      if (colonPosition < 0)
         return "";

      int valueStart = json.Find("\"", colonPosition);
      if (valueStart < 0)
         return "";

      valueStart++;

      AnsiString result;
      for (int i = valueStart; i < json.GetLength(); i++)
      {
         char character = json[i];

         if (character == '\\' && i + 1 < json.GetLength())
         {
            char next = json[i + 1];
            if (next == '\"' || next == '\\' || next == '/')
            {
               result += next;
               i++;
               continue;
            }

            result += character;
            continue;
         }

         if (character == '\"')
            break;

         result += character;
      }

      return result;
   }

   AnsiString
   RestApiServer::JsonEscape_(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength() + 8);

      for (int i = 0; i < value.GetLength(); i++)
      {
         char character = value[i];

         switch (character)
         {
         case '\"':
            result += "\\\"";
            break;
         case '\\':
            result += "\\\\";
            break;
         default:
            if (static_cast<unsigned char>(character) >= 0x20)
               result += character;
            break;
         }
      }

      return result;
   }
}
