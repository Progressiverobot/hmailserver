// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// REST administration API over HTTPS. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "RestApiServer.h"
#include "HttpServer.h"
#include "Encoding/ModifiedUTF7.h"
#include <boost/thread/thread.hpp>
#include <boost/chrono.hpp>
#include "../Application/MetricsHistoryTask.h"
#include "ServerStatus.h"
#include "OtelTracer.h"
#include "OtelTraceContext.h"
#include "Crypt.h"
#include "Totp.h"
#include "AccountLogon.h"
#include "PasswordPolicy.h"
#include "PasswordHistory.h"
#include "../Cache/AccountSizeCache.h"
#include "AcmeClient.h"
#include "UpdateChecker.h"
#include "UpdateDownloader.h"
#include "UpdateInstaller.h"
#include "WebServicesServer.h"

// The Win32 profile API - GetPrivateProfileString and the three calls beside
// it - lives in kernel32 and has no POSIX equivalent, so the POSIX build
// declares those four functions here and implements them over the INI file
// itself in Common/Util/IniFile.cpp. The Windows build never reaches this line
// and binds the same calls to <windows.h> as it always has.
#ifdef HM_PLATFORM_POSIX
#include "IniFile.h"
#endif
#include "../AntiSpam/QuarantineStore.h"
#include "../BO/IMAPFolders.h"
#include "../BO/IMAPFolder.h"
#include "../BO/Messages.h"
#include "../BO/MessageData.h"
#include "../BO/Attachments.h"
#include "../BO/Attachment.h"
#include "../Mime/Mime.h"
#include "../Application/ACLManager.h"
#include "../../IMAP/IMAPFolderContainer.h"
#include "../../IMAP/IMAPSpecialUse.h"
#include "../../IMAP/MessagesContainer.h"
#include "../../SMTP/RecipientParser.h"
#include "../Persistence/PersistentMessageIndex.h"
#include "../Cache/CacheContainer.h"
#include "../Sieve/SieveStorage.h"
#include "../Sieve/SieveScript.h"
#include "GUIDCreator.h"
#include <iterator>
#include <list>
#include <set>
#include "../BO/MessageRecipients.h"
#include "../BO/MessageRecipient.h"
#include "Unicode.h"
#include "../Application/FolderManager.h"
#include "../BO/ACLPermission.h"
#include "../Tracking/ChangeNotification.h"
#include "../Tracking/NotificationServer.h"
#include "MessageUtilities.h"
#include "../Application/Application.h"
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
#include "../Persistence/PersistentArchiveIndex.h"
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

// <ws2tcpip.h> is Winsock's TCP/IP header. Everything this file takes from it -
// the address structures and the address-conversion calls - comes from
// <netinet/in.h>, <arpa/inet.h> and <netdb.h> on POSIX, which the platform layer
// has already included.
#ifdef _MSC_VER
#include <ws2tcpip.h>
#endif

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
      // The ceilings HttpServer enforces for this listener. Every one is
      // absolute - see HttpServer.cpp for why an idle timeout is not enough.
      const size_t MaxRequestSize = 64 * 1024;

      // The two routes that carry attachments: sixteen megabytes on the
      // wire, and five minutes for them to arrive.
      const size_t MaxLargeRequestSize = 16 * 1024 * 1024;
      const unsigned LargeRequestSeconds = 300;
      const unsigned RequestSeconds = 30;
      const unsigned ConnectionSeconds = 300;
      const unsigned MaxConnections = 64;
      const unsigned WorkerThreads = 4;

      // hm_messages.messagetype for a message held for ETRN. There is no
      // Message::State enumerator for it, but GET /api/v1/queue lists it
      // alongside the ordinary Message::Delivering rows, so the retry and
      // delete endpoints have to accept it too.
      const int EtrnHeldMessageType = 3;

      // The TLS context the HTTPS listener hands to HttpServer. Its configuration
      // comes from SslContextInitializer; the boost object owns the underlying
      // SSL_CTX and frees it in its destructor, so it has to outlive every session
      // made from it, and Stop() releases it only after the server has stopped.
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

      // Browser sessions for the self-service page and for the Control Deck.
      // One row per sign-in, holding the SHA-256 of the cookie's token and who
      // it stands for - an account, or the administrator; the token itself
      // lives only in the browser. Bounded in number and in time (see
      // AuthenticateSession_), and dropped on Stop() - a restarted listener
      // holds no sessions.
      struct BrowserSession
      {
         BrowserSession() : account_id(0), administrator(false), created_at(0), last_seen_at(0) { }

         AnsiString token_hash;
         __int64 account_id;

         // The administrator's session: account_id is 0 and credential_stamp
         // is the SHA-256 of the stored administrator password hash as it was
         // at sign-in, so that a change of the administrator password ends the
         // session on its next request, as a change of an account's password
         // ends that account's other sessions. A hash of a hash: the table
         // still holds nothing that logs anybody in.
         bool administrator;
         AnsiString credential_stamp;

         ULONGLONG created_at;
         ULONGLONG last_seen_at;
      };

      const char *SessionCookieName = "hmailsession";
      const int SessionTokenBytes = 32;
      const ULONGLONG SessionIdleMilliseconds = 30ULL * 60 * 1000;
      const ULONGLONG SessionAbsoluteMilliseconds = 12ULL * 60 * 60 * 1000;
      const size_t MaxBrowserSessions = 1000;

      std::mutex browser_sessions_mutex;
      std::vector<BrowserSession> browser_sessions;

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

      // What an administrator session is checked against on every request: the
      // stored administrator password hash, hashed once more. Empty when no
      // administrator password is set, which is the state in which this API
      // answers nobody at all.
      AnsiString AdministratorCredentialStamp()
      {
         const AnsiString stored = AnsiString(IniFileSettings::Instance()->GetAdministratorPassword());
         if (stored.IsEmpty())
            return AnsiString();

         return HashApiKeyToken(stored);
      }

      // Every administrator session at once. Called when the credential they
      // were minted from is no longer the one in force. Takes the table's lock
      // itself.
      void RevokeAdministratorSessions()
      {
         std::lock_guard<std::mutex> guard(browser_sessions_mutex);

         browser_sessions.erase(
            std::remove_if(browser_sessions.begin(), browser_sessions.end(),
               [](const BrowserSession &session)
               {
                  return session.administrator;
               }),
            browser_sessions.end());
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

   }

   RestApiServer::RestApiServer() :
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
            context->set_options(HM_TLS_CONTEXT_FLOOR);

            if (!SslContextInitializer::InitServer(*context, certificate, bind_address, port))
            {
               // InitServer has already reported the specific failure - an unreadable
               // certificate file, a private key that does not match - as HM5113.
               // This line records only the consequence for this listener.
               LOG_APPLICATION("RestApi: Refusing to start - the shared TLS configuration could not be applied to the configured certificate.");
               return false;
            }

            tls_context_owner = context;

            // The one thing that is deliberately *not* taken from the shared
            // configuration. This listener enforced a TLS 1.2 floor before, and the
            // shared option mask is driven by the [Settings] protocol toggles, which
            // an administrator may well have opened up to TLS 1.0 for an ancient mail
            // client. That argument does not extend to an HTTP API - there is no
            // 2008-era REST client to keep working - so the floor stays, applied after
            // InitServer so it can only tighten what the shared configuration allows.
            SSL_CTX_set_min_proto_version(context->native_handle(), TLS1_2_VERSION);
         }
         catch (...)
         {
            // Constructing the context can throw. This is the startup path, where an
            // escape would take the whole service down before any listener is up.
            LOG_APPLICATION("RestApi: Refusing to start - an exception was raised while preparing TLS.");
            return false;
         }
      }

      HttpLimits limits;
      limits.max_request_bytes = MaxRequestSize;
      limits.max_request_bytes_large = MaxLargeRequestSize;
      limits.request_seconds_large = LargeRequestSeconds;
      limits.request_seconds = RequestSeconds;
      limits.connection_seconds = ConnectionSeconds;
      limits.max_connections = MaxConnections;
      limits.worker_threads = WorkerThreads;

      std::shared_ptr<HttpServer> server(new HttpServer("RestApi", limits,
         [this](const HttpRequest &request)
         {
            return ProcessRequest_(request.raw, request.peer);
         },
         [](const IPAddress &peer)
         {
            // An address that has already tripped the auto-ban is refused here:
            // before the request is read, before the TLS handshake, and - the
            // point of the exercise - before anything touches the database. A
            // refused connection is closed without a response, exactly as a
            // security range refuses an SMTP connection before its banner.
            if (IsRefusedAddress_(peer))
            {
               LOG_DEBUG("RestApiServer: Refused a connection from " + String(peer.ToString()) +
                  ", which has recently failed authentication repeatedly.");
               return false;
            }

            return true;
         },
         [](int status, const AnsiString &message)
         {
            // The server's own refusals - malformed, oversized, chunked, a
            // handler that threw - in the API's JSON shape. Says nothing about
            // the cap: an administrator hitting it reads the documentation, a
            // stranger measuring it learns nothing useful.
            AnsiString body;
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(message).c_str());
            return BuildResponse_(status, body);
         }));

      server->SetLargeRequestFilter(&RestApiServer::IsLargeRequest_);

      if (!server->Listen(bind_address, port, use_tls_ ? tls_context_owner : std::shared_ptr<boost::asio::ssl::context>()))
      {
         tls_context_owner.reset();
         return false;
      }

      server_ = server;
      running_ = true;
      server_->Start();

      String message;
      message.Format(_T("RestApi: Listening on %s (%s)."), HttpServer::FormatEndpoint(bind_address, port).c_str(), use_tls_ ? _T("https") : _T("http, loopback only"));
      LOG_APPLICATION(message);

      return true;
   }

   void
   RestApiServer::Stop()
   {
      if (!running_)
         return;

      running_ = false;

      if (server_)
      {
         server_->Stop();
         server_.reset();
      }

      // After the stop, so there is no request left to race with. A stopped
      // listener holds no refusals and no request counts: whatever was in force
      // is dropped rather than surviving into the next Start().
      ClearRefusedAddresses_();
      ClearRequestRates_();
      ClearBrowserSessions_();

      // Deliberately no SSL_CTX_free: the boost context owns the SSL_CTX and frees it
      // in its own destructor. Released after the server has stopped, so no session
      // is still using it.
      tls_context_owner.reset();
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
      caller.peer = peer_address;

      AnsiString headerValue = GetAuthorizationHeader_(request);
      if (headerValue.IsEmpty())
      {
         // No password and no key: a browser session cookie is the one other
         // credential, and it stands for an account and nothing more.
         AuthenticateSession_(request, peer_address, caller);
         return caller;
      }

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

         // Any Basic user name but the administrator's is an account. The two
         // are told apart here, once, so that an account can never be tried
         // against the administrator password and the administrator's name can
         // never be tried against the accounts.
         {
            AnsiString decoded = Base64::Decode(encodedCredentials, encodedCredentials.GetLength());
            int separatorPosition = decoded.Find(":");
            if (separatorPosition > 0)
            {
               String basicUser = String(decoded.Mid(0, separatorPosition));
               if (basicUser.CompareNoCase(_T("administrator")) != 0)
               {
                  if (!AuthenticateAccount_(basicUser, String(decoded.Mid(separatorPosition + 1)), peer_address, caller))
                     RegisterAuthenticationFailure_(peer_address);
                  return caller;
               }
            }
         }

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
      if (IniFileSettings::Instance()->IsAdministratorTotpEnrolled() && secret.IsEmpty())
      {
         // Enrolled but unopenable: refused, as COMAuthentication refuses, and
         // for the same reason - a lost key must not read as a second factor
         // switched off.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6417, "RestApiServer::AuthenticateBasic_",
            "Administrator logon refused: a second factor is enrolled but its secret cannot be opened on this machine (see the preceding secret-store error). Restore the key the secret was protected with, or clear AdministratorTotpSecret in hMailServer.ini and enrol again.");
         return BasicRefused;
      }
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

   HttpResponse
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

      // The self-service page, likewise unauthenticated: it is a static sign-in
      // form whose script presents the account's credentials to /api/v1/me.
      if (method == "GET" && path == "/portal")
         return HandlePortalPage_();

      if (method == "GET" && path == "/portal.js")
         return HandlePortalScript_();

      try
      {
         // Inside the try: authenticating a bearer token reads the key store and
         // recording a failure touches the database, neither of which may turn
         // an unauthenticated request into an unhandled exception on the single
         // REST worker thread.
         Caller caller = Authenticate_(request, peer_address);

         // Cross-site request forgery, closed twice over: the cookie is
         // SameSite=Strict, so another site's request does not carry it, and
         // a request that changes something must carry a header a browser
         // never adds on its own - which another origin cannot add without a
         // preflight this server never grants.
         if (caller.via_session && method != "GET" && method != "HEAD" &&
             GetHeader_(request, "x-requested-with") != "hMailServer")
         {
            return BuildResponse_(403, "{\"error\":\"a request that changes something must carry X-Requested-With: hMailServer when it is authenticated by a session cookie\"}");
         }

         if (caller.result == AuthenticationFailed)
            return BuildUnauthorizedResponse_(caller.second_factor_required, IsPageScriptRequest_(request));

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
            return BuildUnauthorizedResponse_(false, IsPageScriptRequest_(request));

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

         case RouteDomainCreate:
            return HandleCreateDomain_(GetRequestBody_(request));

         case RouteDomainUpdate:
            return HandleUpdateDomain_(String(route.identifier), GetRequestBody_(request));

         case RouteDomainDelete:
            return HandleDeleteDomain_(String(route.identifier));

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
         case RouteUpdateGet:
            return HandleUpdateGet_();
         case RouteUpdateCheck:
            return HandleUpdateCheck_();
         case RouteUpdateDownload:
            return HandleUpdateDownload_();
         case RouteUpdateInstall:
            return HandleUpdateInstall_();

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
         case RouteSettingsPut:
            return HandleSettingsPut_(GetRequestBody_(request));
         case RouteSettingsAntiSpamGet:
            return HandleSettingsAntiSpam_();
         case RouteSettingsAntiSpamPut:
            return HandleSettingsAntiSpamPut_(GetRequestBody_(request));
         case RouteSettingsLoggingGet:
            return HandleSettingsLogging_();
         case RouteSettingsLoggingPut:
            return HandleSettingsLoggingPut_(GetRequestBody_(request));
         case RouteRuleCreate:
            return HandleCreateRule_(GetRequestBody_(request));
         case RouteRuleUpdate:
            return HandleUpdateRule_(route.record_id, GetRequestBody_(request));
         case RouteRuleDelete:
            return HandleDeleteRule_(route.record_id);
         case RouteCertificateCreate:
            return HandleCreateCertificate_(GetRequestBody_(request));
         case RouteCertificateDelete:
            return HandleDeleteCertificate_(route.record_id);
         case RoutePortList:
            return HandleListPorts_();
         case RoutePortCreate:
            return HandleCreatePort_(GetRequestBody_(request));
         case RoutePortUpdate:
            return HandleUpdatePort_(route.record_id, GetRequestBody_(request));
         case RoutePortDelete:
            return HandleDeletePort_(route.record_id);
         case RouteRouteList:
            return HandleListRoutes_();
         case RouteRouteCreate:
            return HandleCreateRoute_(GetRequestBody_(request));
         case RouteRouteUpdate:
            return HandleUpdateRoute_(route.record_id, GetRequestBody_(request));
         case RouteRouteDelete:
            return HandleDeleteRoute_(route.record_id);
         case RouteAliasCreate:
            return HandleCreateAlias_(String(route.identifier), GetRequestBody_(request));
         case RouteAliasDelete:
            return HandleDeleteAlias_(String(route.identifier));
         case RouteAccountUpdate:
            return HandleUpdateAccount_(caller, String(route.identifier), GetRequestBody_(request));
         case RouteServerReinitialize:
            return HandleServerReinitialize_();
         case RouteArchiveSearch:
            return HandleArchiveSearch_(caller.domains, route.query);
         case RouteArchiveGet:
            return HandleArchiveGet_(caller.domains, route.archive_id);
         case RouteArchiveHold:
            return HandleArchiveHold_(caller.domains, route.archive_id, true);
         case RouteArchiveRelease:
            return HandleArchiveHold_(caller.domains, route.archive_id, false);
         case RouteMe:
            return HandleMe_(caller);

         case RouteMePassword:
            return HandleMePassword_(caller, request);

         case RouteMeVacation:
            return HandleMeVacation_(caller, GetRequestBody_(request));

         case RouteMeQuarantineList:
            return HandleMeQuarantineList_(caller);

         case RouteMeQuarantineRelease:
            return HandleMeQuarantineRelease_(caller, route.message_id);

         case RouteMeQuarantineDelete:
            return HandleMeQuarantineDelete_(caller, route.message_id);

         case RouteMeFolders:
            return HandleMeFolders_(caller);

         case RouteMeFolderCreate:
            return HandleMeFolderCreate_(caller, GetRequestBody_(request));

         case RouteMeFolderRename:
            return HandleMeFolderRename_(caller, route.folder_id, GetRequestBody_(request));

         case RouteMeFolderDelete:
            return HandleMeFolderDelete_(caller, route.folder_id);

         case RouteMeChanges:
            return HandleMeChanges_(caller, route.query);

         case RouteMeFolderMessages:
            return HandleMeFolderMessages_(caller, route.folder_id, route.query);

         case RouteMeMessage:
            return HandleMeMessage_(caller, route.message_id);

         case RouteMeMessageFlags:
            return HandleMeMessageFlags_(caller, route.message_id, GetRequestBody_(request));

         case RouteMeMessageMove:
            return HandleMeMessageMove_(caller, route.message_id, GetRequestBody_(request));

         case RouteMeMessageDelete:
            return HandleMeMessageDelete_(caller, route.message_id, route.query);

         case RouteMeMessageSend:
            return HandleMeMessageSend_(caller, GetRequestBody_(request));

         case RouteMeMessageAttachment:
            return HandleMeMessageAttachment_(caller, route.message_id, route.attachment_index);

         case RouteMeSearch:
            return HandleMeSearch_(caller, route.query);

         case RouteMeSettings:
            return HandleMeSettings_(caller);

         case RouteMeSettingsPut:
            return HandleMeSettingsPut_(caller, GetRequestBody_(request));

         case RouteMeFilters:
            return HandleMeFilters_(caller);

         case RouteMeFiltersPut:
            return HandleMeFiltersPut_(caller, GetRequestBody_(request));

         case RouteMeDraftSave:
            return HandleMeDraftSave_(caller, GetRequestBody_(request));

         case RouteSessionCreate:
            return HandleSessionCreate_(caller);

         case RouteSessionDelete:
            return HandleSessionDelete_(caller);

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

      if (path == "/api/v1/me" && method == "GET")
      {
         route.kind = RouteMe;
         return;
      }

      const AnsiString meQuarantinePath = "/api/v1/me/quarantine";

      if (path == meQuarantinePath && method == "GET")
      {
         route.kind = RouteMeQuarantineList;
         return;
      }

      if (path.StartsWith(meQuarantinePath + "/"))
      {
         AnsiString rest = path.Mid(meQuarantinePath.GetLength() + 1);

         if (method == "POST" && rest.EndsWith("/release"))
         {
            AnsiString idText = rest.Mid(0, rest.GetLength() - AnsiString("/release").GetLength());
            if (ParseQueueId(idText, route.message_id))
               route.kind = RouteMeQuarantineRelease;
            return;
         }

         if (method == "DELETE" && ParseQueueId(rest, route.message_id))
            route.kind = RouteMeQuarantineDelete;

         return;
      }

      // The account's own mailbox: the folder tree and its writes, one folder's
      // messages, one message.
      if (path == "/api/v1/me/folders")
      {
         if (method == "GET")
            route.kind = RouteMeFolders;
         else if (method == "POST")
            route.kind = RouteMeFolderCreate;

         return;
      }

      const AnsiString meFoldersPath = "/api/v1/me/folders/";

      if (path.StartsWith(meFoldersPath))
      {
         AnsiString rest = path.Mid(meFoldersPath.GetLength());

         if (method == "GET" && rest.EndsWith("/messages"))
         {
            AnsiString idText = rest.Mid(0, rest.GetLength() - AnsiString("/messages").GetLength());
            if (ParseQueueId(idText, route.folder_id))
               route.kind = RouteMeFolderMessages;

            return;
         }

         // The remainder is the folder id itself, with no resource segment after
         // it - which is what ParseQueueId enforces, since an id is digits only.
         if (method == "PUT" && ParseQueueId(rest, route.folder_id))
            route.kind = RouteMeFolderRename;
         else if (method == "DELETE" && ParseQueueId(rest, route.folder_id))
            route.kind = RouteMeFolderDelete;

         return;
      }

      if (path == "/api/v1/me/changes" && method == "GET")
      {
         route.kind = RouteMeChanges;
         return;
      }

      if (path == "/api/v1/me/drafts" && method == "POST")
      {
         route.kind = RouteMeDraftSave;
         return;
      }

      if (path == "/api/v1/me/settings")
      {
         if (method == "GET")
            route.kind = RouteMeSettings;
         else if (method == "PUT")
            route.kind = RouteMeSettingsPut;
         return;
      }

      if (path == "/api/v1/me/filters")
      {
         if (method == "GET")
            route.kind = RouteMeFilters;
         else if (method == "PUT")
            route.kind = RouteMeFiltersPut;
         return;
      }

      if (path == "/api/v1/me/search" && method == "GET")
      {
         route.kind = RouteMeSearch;
         return;
      }

      if (path == "/api/v1/me/messages" && method == "POST")
      {
         route.kind = RouteMeMessageSend;
         return;
      }

      const AnsiString meMessagesPath = "/api/v1/me/messages/";

      if (path.StartsWith(meMessagesPath))
      {
         AnsiString rest = path.Mid(meMessagesPath.GetLength());

         if (method == "PUT" && rest.EndsWith("/flags"))
         {
            AnsiString idText = rest.Mid(0, rest.GetLength() - AnsiString("/flags").GetLength());
            if (ParseQueueId(idText, route.message_id))
               route.kind = RouteMeMessageFlags;
            return;
         }

         int attachmentsAt = rest.Find("/attachments/");
         if (method == "GET" && attachmentsAt > 0)
         {
            AnsiString idText = rest.Mid(0, attachmentsAt);
            AnsiString indexText = rest.Mid(attachmentsAt + AnsiString("/attachments/").GetLength());
            // The index starts at zero, which no queue id does.
            bool digits = !indexText.IsEmpty() && indexText.GetLength() <= 5;
            for (int k = 0; digits && k < indexText.GetLength(); k++)
               digits = indexText[k] >= '0' && indexText[k] <= '9';
            if (ParseQueueId(idText, route.message_id) && digits)
            {
               route.attachment_index = atoi(indexText.c_str());
               route.kind = RouteMeMessageAttachment;
            }
            return;
         }

         if (method == "POST" && rest.EndsWith("/move"))
         {
            AnsiString idText = rest.Mid(0, rest.GetLength() - AnsiString("/move").GetLength());
            if (ParseQueueId(idText, route.message_id))
               route.kind = RouteMeMessageMove;
            return;
         }

         if (method == "GET" && ParseQueueId(rest, route.message_id))
            route.kind = RouteMeMessage;
         else if (method == "DELETE" && ParseQueueId(rest, route.message_id))
            route.kind = RouteMeMessageDelete;

         return;
      }

      if (path == "/api/v1/me/password" && method == "POST")
      {
         route.kind = RouteMePassword;
         return;
      }

      if (path == "/api/v1/me/vacation" && method == "PUT")
      {
         route.kind = RouteMeVacation;
         return;
      }

      if (path == "/api/v1/session" && method == "POST")
      {
         route.kind = RouteSessionCreate;
         return;
      }

      if (path == "/api/v1/session" && method == "DELETE")
      {
         route.kind = RouteSessionDelete;
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

      if (method == "POST" && path == "/api/v1/domains")
      {
         route.kind = RouteDomainCreate;
         return;
      }

      // /api/v1/domains/<name>/accounts
      const AnsiString domainsPrefix = "/api/v1/domains/";

      // /api/v1/domains/<name> - the domain itself. The remainder has no slash
      // in it, which is what keeps this apart from every
      // /api/v1/domains/<name>/<resource> route below: those all end in a
      // resource segment and this one ends in the name.
      if ((method == "PUT" || method == "DELETE") && path.StartsWith(domainsPrefix))
      {
         AnsiString domainName = path.Mid(domainsPrefix.GetLength());

         if (!domainName.IsEmpty() && domainName.Find("/") < 0)
         {
            route.kind = method == "PUT" ? RouteDomainUpdate : RouteDomainDelete;
            route.identifier = domainName;
            return;
         }
      }

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

      if ((method == "DELETE" || method == "PUT") && path.StartsWith(accountsPrefix))
      {
         AnsiString address = path.Mid(accountsPrefix.GetLength());

         if (!address.IsEmpty() && address.Find("/") < 0)
         {
            route.kind = method == "DELETE" ? RouteAccountDelete : RouteAccountUpdate;
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

      if (method == "GET" && path == "/api/v1/update")
      {
         route.kind = RouteUpdateGet;
         return;
      }

      if (method == "POST" && path == "/api/v1/update/check")
      {
         route.kind = RouteUpdateCheck;
         return;
      }

      if (method == "POST" && path == "/api/v1/update/download")
      {
         route.kind = RouteUpdateDownload;
         return;
      }

      if (method == "POST" && path == "/api/v1/update/install")
      {
         route.kind = RouteUpdateInstall;
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
      if ((method == "GET" || method == "POST") && path.StartsWith(domainsPrefix) && path.EndsWith("/aliases"))
      {
         AnsiString domainName = path.Mid(domainsPrefix.GetLength(),
            path.GetLength() - domainsPrefix.GetLength() - AnsiString("/aliases").GetLength());

         if (!domainName.IsEmpty() && domainName.Find("/") < 0)
         {
            route.kind = method == "GET" ? RouteAliasList : RouteAliasCreate;
            route.identifier = domainName;
            return;
         }
      }

      // /api/v1/aliases/<address> - the alias itself, named by its address as
      // an account is; scoped to the address's domain in Authorize_.
      const AnsiString aliasesPrefix = "/api/v1/aliases/";

      if (method == "DELETE" && path.StartsWith(aliasesPrefix))
      {
         AnsiString address = path.Mid(aliasesPrefix.GetLength());

         if (!address.IsEmpty() && address.Find("/") < 0)
         {
            route.kind = RouteAliasDelete;
            route.identifier = address;
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

      if (method == "POST" && path == "/api/v1/certificates")
      {
         route.kind = RouteCertificateCreate;
         return;
      }

      // The numbered resources of the write surface: /api/v1/<collection>/<id>
      // with a decimal id and nothing after it, as an IP range is named.
      const AnsiString certificatesPrefix = "/api/v1/certificates/";

      if (method == "DELETE" && path.StartsWith(certificatesPrefix))
      {
         AnsiString idPart = path.Mid(certificatesPrefix.GetLength());
         __int64 id = 0;
         if (idPart.Find("/") < 0 && ParseQueueId(idPart, id))
         {
            route.kind = RouteCertificateDelete;
            route.record_id = id;
            return;
         }
      }

      if (path == "/api/v1/ports" && (method == "GET" || method == "POST"))
      {
         route.kind = method == "GET" ? RoutePortList : RoutePortCreate;
         return;
      }

      const AnsiString portsPrefix = "/api/v1/ports/";

      if ((method == "PUT" || method == "DELETE") && path.StartsWith(portsPrefix))
      {
         AnsiString idPart = path.Mid(portsPrefix.GetLength());
         __int64 id = 0;
         if (idPart.Find("/") < 0 && ParseQueueId(idPart, id))
         {
            route.kind = method == "PUT" ? RoutePortUpdate : RoutePortDelete;
            route.record_id = id;
            return;
         }
      }

      if (path == "/api/v1/routes" && (method == "GET" || method == "POST"))
      {
         route.kind = method == "GET" ? RouteRouteList : RouteRouteCreate;
         return;
      }

      const AnsiString routesPrefix = "/api/v1/routes/";

      if ((method == "PUT" || method == "DELETE") && path.StartsWith(routesPrefix))
      {
         AnsiString idPart = path.Mid(routesPrefix.GetLength());
         __int64 id = 0;
         if (idPart.Find("/") < 0 && ParseQueueId(idPart, id))
         {
            route.kind = method == "PUT" ? RouteRouteUpdate : RouteRouteDelete;
            route.record_id = id;
            return;
         }
      }
      if (method == "GET" && path == "/api/v1/rules")
      {
         route.kind = RouteRuleList;
         return;
      }

      if (method == "POST" && path == "/api/v1/rules")
      {
         route.kind = RouteRuleCreate;
         return;
      }

      const AnsiString rulesPrefix = "/api/v1/rules/";

      if ((method == "PUT" || method == "DELETE") && path.StartsWith(rulesPrefix))
      {
         AnsiString idPart = path.Mid(rulesPrefix.GetLength());
         __int64 id = 0;
         if (idPart.Find("/") < 0 && ParseQueueId(idPart, id))
         {
            route.kind = method == "PUT" ? RouteRuleUpdate : RouteRuleDelete;
            route.record_id = id;
            return;
         }
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

      if (method == "PUT" && path == "/api/v1/settings")
      {
         route.kind = RouteSettingsPut;
         return;
      }

      if (path == "/api/v1/settings/antispam" && (method == "GET" || method == "PUT"))
      {
         route.kind = method == "GET" ? RouteSettingsAntiSpamGet : RouteSettingsAntiSpamPut;
         return;
      }

      if (path == "/api/v1/settings/logging" && (method == "GET" || method == "PUT"))
      {
         route.kind = method == "GET" ? RouteSettingsLoggingGet : RouteSettingsLoggingPut;
         return;
      }

      if (method == "POST" && path == "/api/v1/server/reinitialize")
      {
         route.kind = RouteServerReinitialize;
         return;
      }
      const AnsiString archivePath = "/api/v1/archive";
      if (method == "GET" && path == archivePath)
      {
         route.kind = RouteArchiveSearch;
         return;
      }
      if (path.StartsWith(archivePath + "/"))
      {
         AnsiString remainder = path.Mid(archivePath.GetLength() + 1);
         if (remainder.EndsWith("/hold") && (method == "POST" || method == "DELETE"))
         {
            AnsiString idPart = remainder.Mid(0, remainder.GetLength() - AnsiString("/hold").GetLength());
            __int64 archiveId = 0;
            if (idPart.Find("/") < 0 && ParseQueueId(idPart, archiveId))
            {
               route.kind = method == "POST" ? RouteArchiveHold : RouteArchiveRelease;
               route.archive_id = archiveId;
               return;
            }
         }
         if (method == "GET" && remainder.Find("/") < 0)
         {
            __int64 archiveId = 0;
            if (ParseQueueId(remainder, archiveId))
            {
               route.kind = RouteArchiveGet;
               route.archive_id = archiveId;
               return;
            }
         }
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
      case RouteDomainCreate:
      case RouteDomainUpdate:
      case RouteDomainDelete:
      case RouteQueueRetry:
      case RouteQueueDelete:
      case RouteQuarantineRelease:
      case RouteQuarantineDelete:
      case RouteIpRangeCreate:
      case RouteIpRangeDelete:
      case RouteListCreate:
      case RouteListDelete:
      case RouteBackupStart:
      case RouteSettingsPut:
      case RouteSettingsAntiSpamPut:
      case RouteSettingsLoggingPut:
      case RouteRuleCreate:
      case RouteRuleUpdate:
      case RouteRuleDelete:
      case RouteCertificateCreate:
      case RouteCertificateDelete:
      case RoutePortCreate:
      case RoutePortUpdate:
      case RoutePortDelete:
      case RouteRouteCreate:
      case RouteRouteUpdate:
      case RouteRouteDelete:
      case RouteAliasCreate:
      case RouteAliasDelete:
      case RouteAccountUpdate:
      case RouteServerReinitialize:
      case RouteArchiveHold:
      case RouteArchiveRelease:
      // An update check changes the recorded verdict and makes the server call
      // out, neither of which a read-only credential should be able to cause;
      // a download writes a file the next part will run; an install runs it.
      case RouteUpdateCheck:
      case RouteUpdateDownload:
      case RouteUpdateInstall:
      case RouteMePassword:
      case RouteMeVacation:
      case RouteMeQuarantineRelease:
      case RouteMeQuarantineDelete:
      case RouteMeMessageFlags:
      case RouteMeMessageMove:
      case RouteMeMessageDelete:
      case RouteMeMessageSend:
      case RouteMeSettingsPut:
      case RouteMeFiltersPut:
      case RouteMeDraftSave:
      case RouteMeFolderCreate:
      case RouteMeFolderRename:
      case RouteMeFolderDelete:
      case RouteSessionCreate:
      case RouteSessionDelete:
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

      // A browser session is started by whoever holds a password - an
      // account's, for the self-service page, or the administrator's, for the
      // Control Deck - and ended by the session it came with. An API key is
      // refused: a key is already the credential a script keeps, and a cookie
      // minted from one would be a second credential with none of the key's
      // restrictions written on it.
      if (route.kind == RouteSessionCreate || route.kind == RouteSessionDelete)
      {
         if (caller.result == AuthenticatedAsAccount || caller.result == AuthenticatedAsAdministrator)
            return AuthorizationAllowed;

         refusalReason = "a browser session is started with an account's password or with the administrator password, not with an api key";
         return AuthorizationForbidden;
      }

      // The account's own endpoints, and the account's own credentials: each
      // reaches the other and nothing else. The administrator password and an
      // API key are refused there because neither is an account - there is no
      // mailbox behind them whose quota or vacation message could be meant -
      // and an account is refused everywhere else because the rest of the
      // API administers the server.
      if (IsSelfServiceRoute_(route.kind))
      {
         if (caller.result == AuthenticatedAsAccount)
            return AuthorizationAllowed;

         refusalReason = "this endpoint answers to an account's own credentials, not to the administrator password or an api key";
         return AuthorizationForbidden;
      }

      if (caller.result == AuthenticatedAsAccount)
      {
         refusalReason = "an account's credentials reach only the account's own endpoints under /api/v1/me";
         return AuthorizationForbidden;
      }

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
      // The set of domains is the server's. A key issued for named domains
      // administers what is inside them; it does not decide which domains
      // exist, so creating one - or deleting one, with every mailbox in it -
      // needs a credential that was never narrowed to domains. Changing a
      // domain's active flag is scoped to that domain further down, as the
      // account routes are.
      case RouteDomainCreate:
      case RouteDomainDelete:
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
      case RouteSettingsPut:
      case RouteSettingsAntiSpamGet:
      case RouteSettingsAntiSpamPut:
      case RouteSettingsLoggingGet:
      case RouteSettingsLoggingPut:
      case RouteRuleCreate:
      case RouteRuleUpdate:
      case RouteRuleDelete:
      case RouteCertificateCreate:
      case RouteCertificateDelete:
      case RoutePortList:
      case RoutePortCreate:
      case RoutePortUpdate:
      case RoutePortDelete:
      case RouteRouteList:
      case RouteRouteCreate:
      case RouteRouteUpdate:
      case RouteRouteDelete:
      case RouteServerReinitialize:
      case RouteUpdateGet:
      case RouteUpdateCheck:
      case RouteUpdateDownload:
      case RouteUpdateInstall:
         // IP ranges, certificates, global rules, the logs, the backup, the
         // server settings and the update check are all server-wide: none of
         // them belongs to a domain, and the logs in particular carry every
         // domain's traffic.
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
      case RouteDomainUpdate:
      case RouteAliasList:
      case RouteAliasCreate:
      case RouteListList:
      case RouteListCreate:
      case RouteDkimGet:
         targetDomain = String(route.identifier);
         break;
      case RouteListDelete:
      case RouteAliasDelete:
      case RouteAccountUpdate:
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

   HttpResponse
   RestApiServer::BuildResponse_(int statusCode, const AnsiString &body, const AnsiString &extraHeaders)
   {
      // The status line, the framing headers and the connection header are the
      // server's; a status it does not know becomes a 500 there, deliberately -
      // a wrong number in a response line is worse than an honest server error.
      HttpResponse response;
      response.status = statusCode;
      response.content_type = "application/json";
      response.body = body;
      response.extra_headers = extraHeaders;

      // Nothing the API answers may be served from a cache: every response is
      // the state of the server at that moment, and most of them were only
      // given because a credential was presented with the request. A browser
      // or a proxy that kept one would hand it to the next caller.
      response.extra_headers += "Cache-Control: no-store\r\n";

      return response;
   }

   bool
   RestApiServer::IsPageScriptRequest_(const AnsiString &request)
   {
      // Sec-Fetch-Mode is sent by every current browser and says how the
      // request was made: "navigate" is the address bar or a link, and anything
      // else - cors, same-origin, no-cors - is a script's fetch.
      // X-Requested-With is what this server's own pages set on a write.
      AnsiString mode = GetHeader_(request, "sec-fetch-mode");
      mode.ToLower();

      if (!mode.IsEmpty() && mode != "navigate")
         return true;

      return !GetHeader_(request, "x-requested-with").IsEmpty();
   }

   HttpResponse
   RestApiServer::BuildUnauthorizedResponse_(bool secondFactorRequired, bool suppressChallenge)
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
      // The challenge advertises Basic only, exactly as before - for every
      // caller it helps. It is left out for one kind of request: the fetch a
      // page's own script makes. A browser answers WWW-Authenticate by opening
      // its own credential box, in front of the page that asked, and the fetch
      // does not settle until somebody answers it - so the Control Deck and the
      // portal, which sign in through a form and show this response's own
      // sentence, were talking to a user standing behind a box they never put
      // there. A script, curl, and a browser that navigated straight to a route
      // still get the challenge, because for them the box is the way in.
      const AnsiString body = secondFactorRequired
         ? "{\"error\":\"authentication failed\",\"second_factor\":\"required\"}"
         : "{\"error\":\"authentication failed\"}";

      AnsiString headers;

      if (!suppressChallenge)
         headers += "WWW-Authenticate: Basic realm=\"hMailServer\"\r\n";
      if (secondFactorRequired)
         headers += "X-hMailServer-OTP: required\r\n";

      return BuildResponse_(401, body, headers);
   }

   HttpResponse
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

   HttpResponse
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

   HttpResponse
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
            JsonEscape_(Utf8_(key.id)).c_str(),
            JsonEscape_(Utf8_(key.label)).c_str(),
            key.read_only ? ApiKeyScopeReadOnlyNarrow : ApiKeyScopeFullNarrow,
            JsonEscape_(Utf8_(StringParser::JoinVector(key.domains, _T(",")))).c_str(),
            JsonEscape_(Utf8_(key.expires)).c_str(),
            JsonEscape_(Utf8_(key.allowed_from)).c_str(),
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

   HttpResponse
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

#ifdef HM_PLATFORM_POSIX
      // explicit_bzero is SecureZeroMemory's promise on this platform: a write
      // the compiler is not allowed to remove because the buffer is dead
      // afterwards. A plain memset here would be optimised away and the raw key
      // would stay on the stack of the REST worker thread.
      ::explicit_bzero(secret, sizeof(secret));
#else
      SecureZeroMemory(secret, sizeof(secret));
#endif

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
         JsonEscape_(Utf8_(id)).c_str(),
         JsonEscape_(label).c_str(),
         readOnly ? ApiKeyScopeReadOnlyNarrow : ApiKeyScopeFullNarrow,
         JsonEscape_(Utf8_(normalizedDomains)).c_str(),
         JsonEscape_(expires).c_str(),
         JsonEscape_(allowedFrom).c_str(),
         JsonEscape_(token).c_str());

      return BuildResponse_(201, body);
   }

   HttpResponse
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

   HttpResponse
   RestApiServer::HandleWebAdminPage_()
   {
      String pagePath = FileUtilities::Combine(
         FileUtilities::Combine(IniFileSettings::Instance()->GetProgramDirectory(), _T("WebAdmin")), _T("index.html"));

      // Read as the bytes on disk. The page is UTF-8 and carries characters
      // outside the system code page in its own markup (the navigation glyphs);
      // a round trip through String and back would have put them through the
      // ANSI code page and served question marks.
      AnsiString body;
      if (FileUtilities::Exists(pagePath))
      {
#ifdef HM_PLATFORM_POSIX
         // MSVC's <fstream> has a constructor taking a wide path; libstdc++ has
         // none, because a file name here is bytes. Narrowed the way the rest of
         // the tree narrows a String for a C API.
         const AnsiString narrowPagePath = pagePath.c_str();
         std::ifstream stream(narrowPagePath.c_str(), std::ios::binary);
#else
         std::ifstream stream(pagePath.c_str(), std::ios::binary);
#endif
         std::string bytes((std::istreambuf_iterator<char>(stream)), std::istreambuf_iterator<char>());
         body = bytes.c_str();
         if (body.GetLength() != bytes.size())
            body = AnsiString(bytes);
      }
      else
      {
         body = "<!doctype html><html><body style=\"font-family:sans-serif\">"
                "<h1>hMailServer</h1><p>Web administration page not installed. "
                "The REST API is available under /api/v1/.</p></body></html>";
      }

      HttpResponse response;
      response.content_type = "text/html; charset=utf-8";
      response.body = body;
      // The same discipline the portal gets. This page has inline script and
      // style of its own, so those are allowed for it; what is refused is
      // anything from anywhere else, any frame, any form posted off this
      // origin, and a base tag that would redirect its relative calls.
      response.extra_headers =
         "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'\r\n"
         "X-Content-Type-Options: nosniff\r\n"
         "Referrer-Policy: no-referrer\r\n"
         "Cache-Control: no-store\r\n";

      return response;
   }

   HttpResponse
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

   HttpResponse
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

         body += DomainEntryJson_(domain);
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::DomainEntryJson_(const std::shared_ptr<Domain> &domain)
   {
      AnsiString entry;
      entry.Format("{\"name\":\"%hs\",\"active\":%hs,\"postmaster\":\"%hs\"}",
         JsonEscape_(Utf8_(domain->GetName())).c_str(),
         domain->GetIsActive() ? "true" : "false",
         JsonEscape_(Utf8_(domain->GetPostmaster())).c_str());

      return entry;
   }

   HttpResponse
   RestApiServer::HandleCreateDomain_(const AnsiString &requestBody)
   {
      // Trimmed as InterfaceDomain::put_Name trims, and nothing else done to
      // it here. The name is judged by PreSaveLimitationsCheck inside
      // PersistentDomain::SaveObject, exactly as a domain saved from the
      // Control Panel or over COM is judged: the same IsValidDomainName, the
      // same refusal of a name a domain alias already answers to.
      String name = JsonUtf8Value_(requestBody, "name");
      name.Trim();

      if (name.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name is required\"}");

      // A duplicate is refused before the save rather than by it, so that it
      // is a 409 and not a 400 carrying the limitation check's sentence: to a
      // caller the two are different answers, and one of them means "already
      // done".
      Domains domains;
      domains.Refresh();

      if (domains.GetItemByName(name))
         return BuildResponse_(409, "{\"error\":\"domain already exists\"}");

      // A fresh Domain, as InterfaceDomains::Add makes one, so that every
      // default the Control Panel would leave in place - the plus-addressing
      // character, the signature settings, the limits switched off - is the
      // same here.
      std::shared_ptr<Domain> domain = std::shared_ptr<Domain>(new Domain());
      domain->SetName(name);
      domain->SetIsActive(GetJsonBoolValue_(requestBody, "active", true));
      domain->SetPostmaster(JsonUtf8Value_(requestBody, "postmaster"));

      String saveError;

      if (!PersistentDomain::SaveObject(domain, saveError, PersistenceModeNormal))
      {
         // As for an account: a message is the limitation check explaining the
         // caller's own mistake - a name that is not a domain name, a name a
         // domain alias already has - in fixed sentences written for an
         // administrator, which is what makes passing them through safe. No
         // message is a failed INSERT, and that one is ours.
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to create domain " + name + ": " + saveError);

            AnsiString body;
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError)).c_str());

            return BuildResponse_(400, body);
         }

         return BuildResponse_(500, "{\"error\":\"failed to save domain\"}");
      }

      // SaveObject has already dropped the name from the domain cache, so the
      // next lookup - a listing, or SMTP deciding whether a recipient is
      // local - reads the row that was just written.
      LOG_APPLICATION("RestApi: Domain " + name + " created.");

      return BuildResponse_(201, DomainEntryJson_(domain));
   }

   HttpResponse
   RestApiServer::HandleUpdateDomain_(const String &domainName, const AnsiString &requestBody)
   {
      // active is what the route is for and has to be named, as enabled has
      // to be for the automatic reply; postmaster changes only when the body
      // names it, so a client that sends {"active":false} to switch a domain
      // off does not also blank its postmaster. The name is not changed here:
      // renaming a domain renames every address in it, and stays with COM.
      if (requestBody.Find("\"active\"") < 0)
         return BuildResponse_(400, "{\"error\":\"active is required\"}");

      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      domain->SetIsActive(GetJsonBoolValue_(requestBody, "active", domain->GetIsActive()));

      if (requestBody.Find("\"postmaster\"") >= 0)
         domain->SetPostmaster(JsonUtf8Value_(requestBody, "postmaster"));

      String saveError;

      if (!PersistentDomain::SaveObject(domain, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to update domain " + domain->GetName() + ": " + saveError);

            AnsiString body;
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError)).c_str());

            return BuildResponse_(400, body);
         }

         return BuildResponse_(500, "{\"error\":\"failed to save domain\"}");
      }

      LOG_APPLICATION("RestApi: Domain " + domain->GetName() + " updated, active: " +
         String(domain->GetIsActive() ? _T("true") : _T("false")) + ".");

      return BuildResponse_(200, DomainEntryJson_(domain));
   }

   HttpResponse
   RestApiServer::HandleDeleteDomain_(const String &domainName)
   {
      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      // Through the collection, as InterfaceDomain::Delete goes through its
      // parent collection. Collection::DeleteItemByDBID calls
      // PersistentDomain::DeleteObject, which deletes the accounts with their
      // messages, the aliases, the distribution lists and the domain aliases,
      // rewrites whatever another domain pointed at any of those names, drops
      // the domain from the cache and removes its data and Sieve directories -
      // and reports a refused delete instead of dropping the object anyway.
      if (!domains.DeleteItemByDBID(domain->GetID()))
         return BuildResponse_(500, "{\"error\":\"failed to delete domain\"}");

      LOG_APPLICATION("RestApi: Domain " + domain->GetName() + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   HttpResponse
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
            JsonEscape_(Utf8_(account->GetAddress())).c_str(),
            account->GetActive() ? "true" : "false");

         body += entry;
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   HttpResponse
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

      // The optional fields, as the InterfaceAccount setters take them after
      // InterfaceAccounts::Add: active (default true), the person's names, and
      // the size limit in megabytes. The limit was advertised in the OpenAPI
      // document (as maxSizeMB) and never read, so a domain with a maximum
      // account size refused every account created here - the limitation
      // check requires a size when the domain has one - and nothing the caller
      // sent could change that. Both spellings are accepted; max_size_mb is the
      // one every other field here follows.
      account->SetActive(GetJsonBoolValue_(requestBody, "active", true));
      account->SetPersonFirstName(JsonUtf8Value_(requestBody, "first_name"));
      account->SetPersonLastName(JsonUtf8Value_(requestBody, "last_name"));

      {
         // A number in the body is not a string, so read it the way
         // HandleCreateIpRange_ reads priority: the digits after the colon.
         long maxSizeMb = 0;
         bool maxSizeGiven = false;

         const char *sizeKeys[] = { "\"max_size_mb\"", "\"maxSizeMB\"" };
         for (const char *needle : sizeKeys)
         {
            int keyPosition = requestBody.Find(needle);
            if (keyPosition < 0)
               continue;

            int colon = requestBody.Find(":", keyPosition + AnsiString(needle).GetLength());
            if (colon < 0)
               continue;

            AnsiString digits;
            for (int i = colon + 1; i < requestBody.GetLength(); i++)
            {
               char c = requestBody[i];
               if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                  continue;
               if ((c >= '0' && c <= '9') || (c == '-' && digits.IsEmpty()))
               {
                  digits += c;
                  continue;
               }
               break;
            }

            if (digits.IsEmpty() || digits.GetLength() > 10)
               return BuildResponse_(400, "{\"error\":\"max_size_mb must be a whole number of megabytes, 0 for no limit\"}");

            maxSizeMb = atol(digits.c_str());
            maxSizeGiven = true;
            break;
         }

         if (maxSizeGiven && maxSizeMb < 0)
            return BuildResponse_(400, "{\"error\":\"max_size_mb must be a whole number of megabytes, 0 for no limit\"}");

         account->SetAccountMaxSize(maxSizeMb);
      }

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
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError)).c_str());

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

   HttpResponse
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

   HttpResponse
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

   HttpResponse
   RestApiServer::HandleQueueRetry_(__int64 messageId)
   {
      if (!QueueMessageExists_(messageId))
         return BuildResponse_(404, "{\"error\":\"queue message not found\"}");

      DeliveryQueue::ResetDeliveryTime(messageId);
      DeliveryQueue::StartDelivery();

      LOG_APPLICATION("RestApi: Queue message " + StringParser::IntToString(messageId) + " scheduled for immediate delivery.");

      return BuildResponse_(200, "{\"retried\":true}");
   }

   HttpResponse
   RestApiServer::HandleQueueDelete_(__int64 messageId)
   {
      if (!QueueMessageExists_(messageId))
         return BuildResponse_(404, "{\"error\":\"queue message not found\"}");

      DeliveryQueue::Remove(messageId);

      LOG_APPLICATION("RestApi: Queue message " + StringParser::IntToString(messageId) + " removed from the delivery queue.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   HttpResponse
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
            JsonEscape_(Utf8_(message.sender)).c_str(),
            JsonEscape_(Utf8_(message.recipients)).c_str(),
            JsonEscape_(Utf8_(message.subject)).c_str(),
            JsonEscape_(Utf8_(message.reason)).c_str(),
            message.score,
            message.size,
            JsonEscape_(Utf8_(message.created)).c_str());

         body += entry;
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   HttpResponse
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
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(error)).c_str());
         return BuildResponse_(500, body);
      }

      return BuildResponse_(200, "{\"released\":true}");
   }

   HttpResponse
   RestApiServer::HandleQuarantineDelete_(__int64 id)
   {
      QuarantinedMessage message;
      if (!QuarantineStore::GetById(id, message))
         return BuildResponse_(404, "{\"error\":\"quarantined message not found\"}");

      if (!QuarantineStore::Delete(id))
         return BuildResponse_(500, "{\"error\":\"the quarantined message could not be deleted\"}");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   HttpResponse
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
            JsonEscape_(Utf8_(alias->GetName())).c_str(),
            JsonEscape_(Utf8_(alias->GetValue())).c_str(),
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

   HttpResponse
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
            JsonEscape_(Utf8_(range->GetName())).c_str(),
            JsonEscape_(Utf8_(range->GetLowerIPString())).c_str(),
            JsonEscape_(Utf8_(range->GetUpperIPString())).c_str(),
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

   HttpResponse
   RestApiServer::HandleCreateIpRange_(const AnsiString &requestBody)
   {
      AnsiString name = GetJsonStringValue_(requestBody, "name");
      AnsiString lowerText = GetJsonStringValue_(requestBody, "lower");
      AnsiString upperText = GetJsonStringValue_(requestBody, "upper");
      if (name.IsEmpty() || lowerText.IsEmpty() || upperText.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name, lower and upper are required\"}");

      IPAddress lower;
      IPAddress upper;
      // The two-argument form: a caller's typo is answered with a 400, not
      // written to the ERROR log by the parser as the one-argument form does.
      if (!lower.TryParse(lowerText, false) || !upper.TryParse(upperText, false))
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
         error.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(result)).c_str());
         return BuildResponse_(400, error);
      }

      LOG_APPLICATION("RestApi: IP range '" + range->GetName() + "' created.");

      AnsiString body;
      body.Format("{\"id\":%I64d}", range->GetID());
      return BuildResponse_(201, body);
   }

   HttpResponse
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

   HttpResponse
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
               members += "\"" + JsonEscape_(Utf8_(recipient->GetAddress())) + "\"";
               memberCount++;
            }
         }
         members += "]";

         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"address\":\"%hs\",\"active\":%hs,\"require_auth\":%hs,\"members\":%hs}",
            JsonEscape_(Utf8_(list->GetAddress())).c_str(),
            list->GetActive() ? "true" : "false",
            list->GetRequireAuth() ? "true" : "false",
            members.c_str());
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   HttpResponse
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
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(error)).c_str());
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

   HttpResponse
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

   HttpResponse
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
            JsonEscape_(Utf8_(certificate->GetName())).c_str(),
            JsonEscape_(Utf8_(certificate->GetCertificateFile())).c_str(),
            JsonEscape_(Utf8_(certificate->GetPrivateKeyFile())).c_str());
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleDkim_(const String &domainName)
   {
      Domains domains;
      domains.Refresh();
      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      AnsiString body;
      body.Format("{\"domain\":\"%hs\",\"enabled\":%hs,\"selector\":\"%hs\",\"sign_aliases\":%hs,\"private_key_file\":\"%hs\"}",
         JsonEscape_(Utf8_(domain->GetName())).c_str(),
         domain->GetDKIMEnabled() ? "true" : "false",
         JsonEscape_(domain->GetDKIMSelector()).c_str(),
         domain->GetDKIMAliasesEnabled() ? "true" : "false",
         JsonEscape_(Utf8_(domain->GetDKIMPrivateKeyFile())).c_str());
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

   HttpResponse
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

         if (count > 0)
            body += ",";
         body += RuleEntryJson_(rule);
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

   HttpResponse
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
            JsonEscape_(Utf8_(Time::GetTimeStampFromDateTime(file.GetCreateTime()))).c_str());
         body += entry;
         count++;
      }
      body += "]";
      return BuildResponse_(200, body);
   }

   HttpResponse
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
#ifdef HM_PLATFORM_POSIX
      // As above: a narrow path, because libstdc++ has no wide-path constructor.
      const AnsiString narrowPath = path.c_str();
      std::ifstream stream(narrowPath.c_str(), std::ios::binary);
#else
      std::ifstream stream(path.c_str(), std::ios::binary);
#endif
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
            // Decoded rather than reinterpreted: the file is UTF-16LE on disk on
            // every platform, and a wchar_t is four bytes on Linux, so a copy of the
            // bytes would read every other character as a surrogate. The codec is
            // the one File::Write and ReadCompleteTextFile use.
            std::wstring wide = Unicode::FromUtf16Le(
               reinterpret_cast<const unsigned char *>(chunk.data()) + offset, chunk.size() - offset);
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
         body += "\"" + JsonEscape_(Utf8_(all[i].c_str())) + "\"";
      }
      body += "]}";
      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleBackupStart_()
   {
      std::shared_ptr<BackupManager> manager = Application::Instance()->GetBackupManager();
      if (!manager)
         return BuildResponse_(503, "{\"error\":\"the backup manager is not running\"}");

      if (!manager->StartBackup())
      {
         AnsiString body;
         body.Format("{\"error\":\"the backup did not start\",\"status\":\"%hs\"}",
            JsonEscape_(Utf8_(manager->GetStatus())).c_str());
         return BuildResponse_(409, body);
      }

      LOG_APPLICATION("RestApi: Backup started.");
      return BuildResponse_(202, "{\"started\":true}");
   }

   HttpResponse
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
      body.Format("{\"status\":\"%hs\",\"log\":[", JsonEscape_(Utf8_(manager->GetStatus())).c_str());

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

   HttpResponse
   RestApiServer::HandleSettings_()
   {
      // The group is the table in RestApiSettings.cpp: the same rows the PUT
      // applies and the OpenAPI document describes, so what a read shows is
      // exactly what a write answers with. The ten keys this snapshot has
      // always carried are still there under their names; the rest of
      // InterfaceSettings' scalars come after them. No passwords: the one the
      // group accepts (smtp_relayer_password) is write-only. The function is
      // declared in RestApiServer.h beside the class: a block-scope extern
      // here was placed in the global namespace by MSVC and in HM by GCC and
      // clang, and linked on one side only.
      return BuildResponse_(200, RestApiSettingsServerGroupJson(&RestApiServer::JsonEscape_));
   }

   HttpResponse
   RestApiServer::HandleServerReinitialize_()
   {
      // Application::Reinitialize stops every service - this listener among
      // them - reloads the configuration and starts them again, which is how
      // a port, a certificate binding or a TLS setting written over the API
      // takes effect. It cannot run on this thread: the answer has to leave
      // before the listener that carries it is stopped. So the answer goes
      // first, and a thread of its own does the work a moment later; a caller
      // polls GET /api/v1/status until it answers again. Reinitialize holds
      // its own mutex, so two callers restart twice, in turn, and never at
      // once.
      LOG_APPLICATION("RestApi: Reinitialize requested; the services restart in a moment.");

      boost::thread(&ReinitializeAfterTheAnswer_).detach();

      return BuildResponse_(202, "{\"reinitializing\":true}");
   }

   void
   RestApiServer::ReinitializeAfterTheAnswer_()
   {
      boost::this_thread::sleep_for(boost::chrono::milliseconds(500));

      String error = Application::Instance()->Reinitialize();

      if (error.IsEmpty())
      {
         LOG_APPLICATION("RestApi: Reinitialize completed.");
      }
      else
      {
         LOG_APPLICATION("RestApi: Reinitialize failed: " + error);
      }
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

   // ------------------------------------------------------------------------
   // The archive index (roadmap row 1024). Domain scope is decided here rather
   // than in Authorize_, because the domain is in the query or in the row:
   // a domain-restricted key searches one of its own domains and touches only
   // rows in them; the Inbound copies belong to no domain and are the
   // administrator's alone.

   HttpResponse
   RestApiServer::HandleArchiveSearch_(const std::vector<String> &domains, const AnsiString &query)
   {
      PersistentArchiveIndex::Criteria criteria;
      criteria.domain = String(QueryParameter_(query, "domain"));
      criteria.mailbox = String(QueryParameter_(query, "mailbox"));
      criteria.sender = String(QueryParameter_(query, "sender"));
      criteria.recipient = String(QueryParameter_(query, "recipient"));
      criteria.subject = String(QueryParameter_(query, "subject"));
      criteria.since = String(QueryParameter_(query, "since"));
      criteria.until = String(QueryParameter_(query, "until"));
      criteria.holdOnly = QueryParameter_(query, "hold") == "1";
      AnsiString limit = QueryParameter_(query, "limit");
      if (!limit.IsEmpty())
         criteria.maxRows = atoi(limit.c_str());

      if (!domains.empty())
      {
         if (criteria.domain.IsEmpty())
            return BuildForbiddenResponse_("this api key is restricted to named domains; name one of them in the domain parameter");
         if (!IsDomainAllowed_(domains, criteria.domain))
            return BuildForbiddenResponse_("this api key is not permitted for that domain");
      }

      std::vector<PersistentArchiveIndex::Entry> entries;
      if (!PersistentArchiveIndex::Search(criteria, entries))
         return BuildResponse_(500, "{\"error\":\"the archive index could not be read\"}");
      return BuildResponse_(200, PersistentArchiveIndex::ToJson(entries));
   }

   HttpResponse
   RestApiServer::HandleArchiveGet_(const std::vector<String> &domains, __int64 archiveId)
   {
      PersistentArchiveIndex::Entry entry;
      if (!PersistentArchiveIndex::Get(archiveId, entry))
         return BuildResponse_(404, "{\"error\":\"archive entry not found\"}");
      if (!domains.empty() && (entry.domain.IsEmpty() || !IsDomainAllowed_(domains, entry.domain)))
         return BuildForbiddenResponse_("this api key is not permitted for that domain");
      return BuildResponse_(200, PersistentArchiveIndex::ToJson(entry));
   }

   HttpResponse
   RestApiServer::HandleArchiveHold_(const std::vector<String> &domains, __int64 archiveId, bool hold)
   {
      PersistentArchiveIndex::Entry entry;
      if (!PersistentArchiveIndex::Get(archiveId, entry))
         return BuildResponse_(404, "{\"error\":\"archive entry not found\"}");
      if (!domains.empty() && (entry.domain.IsEmpty() || !IsDomainAllowed_(domains, entry.domain)))
         return BuildForbiddenResponse_("this api key is not permitted for that domain");
      if (!PersistentArchiveIndex::SetHold(archiveId, hold))
         return BuildResponse_(500, "{\"error\":\"the hold could not be changed\"}");

      LOG_APPLICATION("RestApi: Archive entry " + StringParser::IntToString((int) archiveId) + (hold ? " placed on hold." : " released from hold."));
      return BuildResponse_(200, hold ? "{\"hold\":true}" : "{\"hold\":false}");
   }

   bool
   RestApiServer::AuthenticateAccount_(const String &username, const String &password, const IPAddress &peer_address, Caller &caller)
   {
      // The same path IMAP and POP3 take, and for the same reason those two
      // share it: every password scheme the server stores, directory-linked
      // accounts, app passwords, the per-name lockout, the logon failure count
      // that feeds the auto-ban and the last-logon stamp all live behind
      // AccountLogon::Logon. A second implementation here would be a second
      // place for them to drift apart.
      bool disconnect = false;
      std::shared_ptr<const Account> account = AccountLogon().Logon(peer_address, username, password, disconnect);

      if (!account)
      {
         LOG_APPLICATION("REST API: account authentication failed for " + username + " from " + String(peer_address.ToString()) + ".");
         return false;
      }

      if (!account->GetActive())
      {
         LOG_APPLICATION("REST API: account " + username + " authenticated but is inactive; refused.");
         return false;
      }

      caller.result = AuthenticatedAsAccount;
      caller.read_only = false;
      caller.identity = AnsiString("account:") + AnsiString(account->GetAddress());
      caller.account = account;

      return true;
   }

   bool
   RestApiServer::IsSelfServiceRoute_(RouteKind kind)
   {
      switch (kind)
      {
      case RouteMe:
      case RouteMePassword:
      case RouteMeVacation:
      case RouteMeQuarantineList:
      case RouteMeQuarantineRelease:
      case RouteMeQuarantineDelete:
      case RouteMeFolders:
      case RouteMeFolderCreate:
      case RouteMeFolderRename:
      case RouteMeFolderDelete:
      case RouteMeChanges:
      case RouteMeFolderMessages:
      case RouteMeMessage:
      case RouteMeMessageFlags:
      case RouteMeMessageMove:
      case RouteMeMessageDelete:
      case RouteMeMessageSend:
      case RouteMeMessageAttachment:
      case RouteMeSearch:
      case RouteMeSettings:
      case RouteMeSettingsPut:
      case RouteMeFilters:
      case RouteMeFiltersPut:
      case RouteMeDraftSave:
      case RouteSessionCreate:
      case RouteSessionDelete:
         return true;

      default:
         break;
      }

      return false;
   }

   HttpResponse
   RestApiServer::HandleMe_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // Quota as the IMAP QUOTA extension reports it: the limit is stored in
      // megabytes (0 = none) and the usage is the size cache's byte count.
      __int64 usedBytes = AccountSizeCache::Instance()->GetSize(account->GetID());

      AnsiString json;
      json.Format(
         "{\"address\":\"%hs\",\"domain\":\"%hs\",\"active\":%hs,"
         "\"quota\":{\"limit_mb\":%d,\"used_bytes\":%I64d},"
         "\"vacation\":{\"enabled\":%hs,\"active\":%hs,\"subject\":\"%hs\",\"message\":\"%hs\",\"expires\":%hs,\"expires_date\":\"%hs\"},"
         "\"password_changed\":\"%hs\",\"second_factor\":%hs,\"directory_linked\":%hs}",
         JsonEscape_(Utf8_(account->GetAddress())).c_str(),
         JsonEscape_(Utf8_(StringParser::ExtractDomain(account->GetAddress()))).c_str(),
         account->GetActive() ? "true" : "false",
         (int) account->GetAccountMaxSize(),
         usedBytes,
         account->GetVacationMessageIsOn() ? "true" : "false",
         PersistentAccount::GetIsVacationMessageOn(account) ? "true" : "false",
         JsonEscape_(Utf8_(account->GetVacationSubject())).c_str(),
         JsonEscape_(Utf8_(account->GetVacationMessage())).c_str(),
         account->GetVacationExpires() ? "true" : "false",
         // The store keeps a date-time ("2099-12-31 00:00:00"); the day is the
         // part that was chosen, and the form the PUT accepts back.
         JsonEscape_(Utf8_(account->GetVacationExpiresDate().Mid(0, 10))).c_str(),
         JsonEscape_(Utf8_(account->GetPasswordChanged())).c_str(),
         account->GetTotpSecret().IsEmpty() ? "false" : "true",
         account->GetIsAD() ? "true" : "false");

      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMePassword_(const Caller &caller, const AnsiString &request)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      AnsiString requestBody = GetRequestBody_(request);
      String currentPassword = JsonUtf8Value_(requestBody, "current");
      String newPassword = JsonUtf8Value_(requestBody, "new");

      if (currentPassword.IsEmpty() || newPassword.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"current and new are required\"}");

      // A directory-linked account has no password of its own here: the
      // directory holds it, and this server only ever asks the directory
      // whether one is right.
      if (account->GetIsAD())
         return BuildResponse_(409, "{\"error\":\"this account's password is managed by the directory it is linked to\"}");

      // The password that was presented to log on may have been an app
      // password, which is a mailbox credential and not a licence to replace
      // the account's own. "current" therefore has to be the account password
      // itself, checked here against the stored hash, whatever authenticated
      // the request.
      Crypt::EncryptionType currentType = (Crypt::EncryptionType) account->GetPasswordEncryption();
      if (account->GetPassword().IsEmpty() ||
          !Crypt::Instance()->Validate(currentPassword, account->GetPassword(), currentType))
      {
         LOG_APPLICATION("REST API: a password change for " + account->GetAddress() + " was refused - the current password did not match.");
         RegisterAuthenticationFailure_(caller.peer);
         return BuildResponse_(403, "{\"error\":\"the current password is not correct\"}");
      }

      // An account with a second factor proves it here the way the
      // administrator does on every request: the code travels in
      // X-hMailServer-OTP, and the 401 that asks for it names the header.
      const String secret = account->GetTotpSecret();
      if (!secret.IsEmpty())
      {
         const AnsiString code = GetHeader_(request, "x-hmailserver-otp");
         if (code.IsEmpty() || !Totp::VerifyCode(AnsiString(secret), code))
            return BuildUnauthorizedResponse_(true, IsPageScriptRequest_(request));
      }

      // The same rules the Control Panel and COM apply when an administrator
      // sets a password, from the same two places, so a user cannot choose
      // what an administrator could not.
      String policyFailure;
      if (!PasswordPolicy::IsAcceptable(account->GetAddress(), newPassword, policyFailure))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(policyFailure)).c_str());
         return BuildResponse_(400, body);
      }

      if (PasswordHistory::IsReuse(account, newPassword))
         return BuildResponse_(409, "{\"error\":\"this password has been used recently on this account; choose one that has not\"}");

      std::shared_ptr<Account> mutableAccount = std::shared_ptr<Account>(new Account());
      if (!PersistentAccount::ReadObject(mutableAccount, account->GetID()) || mutableAccount->GetID() == 0)
         return BuildResponse_(500, "{\"error\":\"the account could not be read\"}");

      PasswordHistory::Record(mutableAccount);

      int preferredHashAlgorithm = IniFileSettings::Instance()->GetPreferredHashAlgorithm();
      mutableAccount->SetPassword(Crypt::Instance()->EnCrypt(newPassword, (Crypt::EncryptionType) preferredHashAlgorithm));
      mutableAccount->SetPasswordEncryption(preferredHashAlgorithm);
      mutableAccount->SetPasswordChanged(Time::GetCurrentDateTime());

      String saveError;
      if (!PersistentAccount::SaveObject(mutableAccount, saveError, PersistenceModeNormal))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError.IsEmpty() ? String(_T("the account could not be saved")) : saveError)).c_str());
         return BuildResponse_(500, body);
      }

      // Every other browser signed in to this account is signed out: a
      // password is changed most urgently when somebody else may have it,
      // and a session they already hold would outlive the change.
      RevokeSessionsForAccount_(account->GetID(), caller.via_session ? caller.session_hash : AnsiString());

      LOG_APPLICATION("REST API: " + account->GetAddress() + " changed its own password from " + String(caller.peer.ToString()) + ".");

      return BuildResponse_(200, "{\"changed\":true}");
   }

   HttpResponse
   RestApiServer::HandleMeVacation_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // The whole state at once - enabled, subject, message and the optional
      // expiry - so that what the account has afterwards is exactly what the
      // body said, and nothing is left over from before.
      AnsiString enabledText = GetJsonStringValue_(requestBody, "enabled");
      bool enabled = GetJsonBoolValue_(requestBody, "enabled", false);
      String subject = JsonUtf8Value_(requestBody, "subject");
      String message = JsonUtf8Value_(requestBody, "message");
      bool expires = GetJsonBoolValue_(requestBody, "expires", false);
      String expiresDate = JsonUtf8Value_(requestBody, "expires_date");

      if (requestBody.Find("\"enabled\"") < 0)
         return BuildResponse_(400, "{\"error\":\"enabled is required\"}");

      if (subject.GetLength() > 200 || message.GetLength() > 20000)
         return BuildResponse_(400, "{\"error\":\"the subject may be 200 characters and the message 20000\"}");

      if (expires)
      {
         // YYYY-MM-DD, the form the account stores and the deliverer compares.
         bool wellFormed = expiresDate.GetLength() == 10 && expiresDate[4] == '-' && expiresDate[7] == '-';
         for (int i = 0; wellFormed && i < 10; i++)
         {
            if (i == 4 || i == 7)
               continue;
            if (expiresDate[i] < '0' || expiresDate[i] > '9')
               wellFormed = false;
         }

         if (!wellFormed)
            return BuildResponse_(400, "{\"error\":\"expires_date must be YYYY-MM-DD when expires is true\"}");
      }

      std::shared_ptr<Account> mutableAccount = std::shared_ptr<Account>(new Account());
      if (!PersistentAccount::ReadObject(mutableAccount, account->GetID()) || mutableAccount->GetID() == 0)
         return BuildResponse_(500, "{\"error\":\"the account could not be read\"}");

      mutableAccount->SetVacationSubject(subject);
      mutableAccount->SetVacationMessage(message);
      mutableAccount->SetVacationExpires(expires);
      mutableAccount->SetVacationExpiresDate(expires ? expiresDate : String());
      mutableAccount->SetVacationMessageIsOn(enabled);

      String saveError;
      if (!PersistentAccount::SaveObject(mutableAccount, saveError, PersistenceModeNormal))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError.IsEmpty() ? String(_T("the account could not be saved")) : saveError)).c_str());
         return BuildResponse_(500, body);
      }

      AnsiString json;
      json.Format("{\"enabled\":%hs,\"subject\":\"%hs\",\"message\":\"%hs\",\"expires\":%hs,\"expires_date\":\"%hs\"}",
         enabled ? "true" : "false",
         JsonEscape_(Utf8_(subject)).c_str(),
         JsonEscape_(Utf8_(message)).c_str(),
         expires ? "true" : "false",
         JsonEscape_(Utf8_(expires ? expiresDate : String())).c_str());

      return BuildResponse_(200, json);
   }

   bool
   RestApiServer::AuthenticateSession_(const AnsiString &request, const IPAddress &peer_address, Caller &caller)
   {
      // The cookie is the only credential a browser can hold without the page
      // keeping the password in memory. Its value is a random token; only the
      // token's SHA-256 is stored, so the table leaks nothing if it is read.
      AnsiString cookies = GetHeader_(request, "cookie");
      if (cookies.IsEmpty())
         return false;

      AnsiString token;
      std::vector<AnsiString> parts = StringParser::SplitString(cookies, ";");
      for (size_t i = 0; i < parts.size(); i++)
      {
         AnsiString part = parts[i];
         part.Trim();

         const AnsiString prefix = AnsiString(SessionCookieName) + "=";
         if (part.GetLength() > prefix.GetLength() && part.Mid(0, prefix.GetLength()) == prefix)
         {
            token = part.Mid(prefix.GetLength());
            break;
         }
      }

      if (!IsLowerHex(token, SessionTokenBytes * 2))
         return false;

      AnsiString presentedHash = HashApiKeyToken(token);
      if (presentedHash.IsEmpty())
         return false;

      __int64 accountId = 0;
      bool administrator = false;
      AnsiString credentialStamp;
      {
         std::lock_guard<std::mutex> guard(browser_sessions_mutex);

         const ULONGLONG now = GetTickCount64();

         for (std::vector<BrowserSession>::iterator it = browser_sessions.begin(); it != browser_sessions.end(); ++it)
         {
            if (!ConstantTimeEquals(presentedHash, it->token_hash))
               continue;

            // Two ceilings, both absolute in their own way: the session ends
            // when it has been idle for SessionIdleMilliseconds, and it ends
            // SessionAbsoluteMilliseconds after it was created whatever the
            // user is doing - a captured cookie stays useful for a bounded
            // time, not for as long as the victim keeps clicking.
            if (now - it->last_seen_at > SessionIdleMilliseconds || now - it->created_at > SessionAbsoluteMilliseconds)
            {
               browser_sessions.erase(it);
               return false;
            }

            it->last_seen_at = now;
            accountId = it->account_id;
            administrator = it->administrator;
            credentialStamp = it->credential_stamp;
            break;
         }
      }

      if (administrator)
      {
         // Checked afresh on every request, as an account's row is below: the
         // administrator password that started the session has to be the one
         // in force. An empty password is the API switched off; a different
         // one is a change made since sign-in - quite possibly because
         // somebody else has the old one - and either ends every
         // administrator session.
         const AnsiString current = AdministratorCredentialStamp();
         if (current.IsEmpty() || !ConstantTimeEquals(current, credentialStamp))
         {
            RevokeAdministratorSessions();
            return false;
         }

         // The administrator password's authority, exactly: full, unscoped,
         // and named the same for the rate budget and for the log. via_session
         // is what makes a write on it carry X-Requested-With.
         caller.result = AuthenticatedAsAdministrator;
         caller.read_only = false;
         caller.identity = "administrator";
         caller.via_session = true;
         caller.session_hash = presentedHash;

         return true;
      }

      if (accountId == 0)
         return false;

      // Read fresh on every request rather than remembered from sign-in: an
      // account that was deactivated or deleted after the session began is
      // refused from the next request, and its session is dropped.
      std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());
      if (!PersistentAccount::ReadObject(account, accountId) || account->GetID() == 0 || !account->GetActive())
      {
         RevokeSessionsForAccount_(accountId, "");
         return false;
      }

      caller.result = AuthenticatedAsAccount;
      caller.read_only = false;
      caller.identity = AnsiString("account:") + AnsiString(account->GetAddress());
      caller.account = account;
      caller.via_session = true;
      caller.session_hash = presentedHash;

      return true;
   }

   HttpResponse
   RestApiServer::HandleSessionCreate_(const Caller &caller)
   {
      if (caller.via_session)
         return BuildResponse_(403, "{\"error\":\"a session is started with a password, not with another session\"}");

      // Who the session stands for: the account whose password was presented,
      // or the administrator. Authorize_ lets nothing else this far.
      const bool administrator = caller.result == AuthenticatedAsAdministrator;

      std::shared_ptr<const Account> account = caller.account;
      if (!administrator && !account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // The stamp an administrator session is checked against on every
      // request, taken now from the credential that was just accepted.
      AnsiString credentialStamp;
      if (administrator)
      {
         credentialStamp = AdministratorCredentialStamp();
         if (credentialStamp.IsEmpty())
            return BuildResponse_(500, "{\"error\":\"internal error\"}");
      }

      unsigned char secret[SessionTokenBytes];
      if (RAND_bytes(secret, sizeof(secret)) != 1)
         return BuildResponse_(500, "{\"error\":\"no entropy for a session token\"}");

      AnsiString token = BytesToLowerHex(secret, SessionTokenBytes);
      AnsiString tokenHash = HashApiKeyToken(token);
      if (tokenHash.IsEmpty())
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      {
         std::lock_guard<std::mutex> guard(browser_sessions_mutex);

         const ULONGLONG now = GetTickCount64();

         // Expired sessions go first, so the table holds what is live; then,
         // if it is still full, the least recently used one goes. The cap is
         // what stops a script from filling memory with sign-ins.
         browser_sessions.erase(
            std::remove_if(browser_sessions.begin(), browser_sessions.end(),
               [now](const BrowserSession &session)
               {
                  return now - session.last_seen_at > SessionIdleMilliseconds || now - session.created_at > SessionAbsoluteMilliseconds;
               }),
            browser_sessions.end());

         if (browser_sessions.size() >= MaxBrowserSessions)
         {
            size_t oldest = 0;
            for (size_t i = 1; i < browser_sessions.size(); i++)
            {
               if (browser_sessions[i].last_seen_at < browser_sessions[oldest].last_seen_at)
                  oldest = i;
            }

            browser_sessions.erase(browser_sessions.begin() + oldest);
         }

         BrowserSession session;
         session.token_hash = tokenHash;
         session.account_id = administrator ? 0 : account->GetID();
         session.administrator = administrator;
         session.credential_stamp = credentialStamp;
         session.created_at = now;
         session.last_seen_at = now;

         browser_sessions.push_back(session);
      }

      // Braced: LOG_APPLICATION is a macro that carries its own if, and an
      // unbraced else after it binds to the wrong one.
      if (administrator)
      {
         LOG_APPLICATION("REST API: the administrator started a browser session from " + String(caller.peer.ToString()) + ".");
      }
      else
      {
         LOG_APPLICATION("REST API: " + account->GetAddress() + " started a browser session from " + String(caller.peer.ToString()) + ".");
      }

      // HttpOnly: the script never reads it, so a script that should not be
      // there cannot either. SameSite=Strict: a request from another site does
      // not carry it. Secure whenever the listener speaks TLS - which it does
      // everywhere but loopback. Max-Age is the absolute ceiling; the idle
      // ceiling is shorter and is the server's to enforce.
      AnsiString cookie;
      cookie.Format("Set-Cookie: %hs=%hs; Path=/; HttpOnly; SameSite=Strict; Max-Age=%d%hs\r\n",
         SessionCookieName, token.c_str(), (int) (SessionAbsoluteMilliseconds / 1000), use_tls_ ? "; Secure" : "");

      AnsiString body;
      if (administrator)
      {
         body.Format("{\"administrator\":true,\"idle_seconds\":%d,\"lifetime_seconds\":%d}",
            (int) (SessionIdleMilliseconds / 1000), (int) (SessionAbsoluteMilliseconds / 1000));
      }
      else
      {
         body.Format("{\"address\":\"%hs\",\"idle_seconds\":%d,\"lifetime_seconds\":%d}",
            JsonEscape_(Utf8_(account->GetAddress())).c_str(),
            (int) (SessionIdleMilliseconds / 1000), (int) (SessionAbsoluteMilliseconds / 1000));
      }

      return BuildResponse_(201, body, cookie);
   }

   HttpResponse
   RestApiServer::HandleSessionDelete_(const Caller &caller)
   {
      if (!caller.via_session)
         return BuildResponse_(400, "{\"error\":\"there is no session to end: this request carried a password, not a session cookie\"}");

      {
         std::lock_guard<std::mutex> guard(browser_sessions_mutex);

         browser_sessions.erase(
            std::remove_if(browser_sessions.begin(), browser_sessions.end(),
               [&caller](const BrowserSession &session)
               {
                  return ConstantTimeEquals(session.token_hash, caller.session_hash);
               }),
            browser_sessions.end());
      }

      AnsiString cookie;
      cookie.Format("Set-Cookie: %hs=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0%hs\r\n",
         SessionCookieName, use_tls_ ? "; Secure" : "");

      return BuildResponse_(200, "{\"ended\":true}", cookie);
   }

   void
   RestApiServer::RevokeSessionsForAccount_(__int64 accountId, const AnsiString &keepTokenHash)
   {
      std::lock_guard<std::mutex> guard(browser_sessions_mutex);

      browser_sessions.erase(
         std::remove_if(browser_sessions.begin(), browser_sessions.end(),
            [accountId, &keepTokenHash](const BrowserSession &session)
            {
               // Never an administrator row: its account_id is 0, and nothing
               // that names an account means the administrator.
               return !session.administrator && session.account_id == accountId &&
                      (keepTokenHash.IsEmpty() || !ConstantTimeEquals(session.token_hash, keepTokenHash));
            }),
         browser_sessions.end());
   }

   void
   RestApiServer::ClearBrowserSessions_()
   {
      std::lock_guard<std::mutex> guard(browser_sessions_mutex);
      browser_sessions.clear();
   }

   HttpResponse
   RestApiServer::HandleMeQuarantineList_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // The whole table, filtered here to the rows this address is a recipient
      // of; nothing about anyone else's mail is computed into the answer, not
      // even a count. The recipients field is left out for the same reason:
      // the user learns that a message was held for them, not for whom else.
      const std::vector<QuarantinedMessage> messages = QuarantineStore::List(1000);

      AnsiString body;
      body.Format("{\"enabled\":%hs,\"messages\":[", QuarantineStore::GetEnabled() ? "true" : "false");

      int count = 0;
      for (const QuarantinedMessage &message : messages)
      {
         if (!QuarantineStore::IsRecipient(message, account->GetAddress()))
            continue;

         if (count > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"sender\":\"%hs\",\"subject\":\"%hs\",\"reason\":\"%hs\",\"score\":%d,\"size\":%d,\"created\":\"%hs\"}",
            message.id,
            JsonEscape_(Utf8_(message.sender)).c_str(),
            JsonEscape_(Utf8_(message.subject)).c_str(),
            JsonEscape_(Utf8_(message.reason)).c_str(),
            message.score,
            message.size,
            JsonEscape_(Utf8_(message.created)).c_str());

         body += entry;
         count++;
      }

      body += "]}";

      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleMeQuarantineRelease_(const Caller &caller, __int64 id)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // A message this address was not sent is "not found", not "forbidden":
      // the id space is shared, and a refusal that differed would confirm that
      // somebody else's message exists.
      QuarantinedMessage message;
      if (!QuarantineStore::GetById(id, message) || !QuarantineStore::IsRecipient(message, account->GetAddress()))
         return BuildResponse_(404, "{\"error\":\"quarantined message not found\"}");

      String error;
      if (!QuarantineStore::ReleaseTo(id, account->GetAddress(), error))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(error)).c_str());
         return BuildResponse_(500, body);
      }

      return BuildResponse_(200, "{\"released\":true}");
   }

   HttpResponse
   RestApiServer::HandleMeQuarantineDelete_(const Caller &caller, __int64 id)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      QuarantinedMessage message;
      if (!QuarantineStore::GetById(id, message) || !QuarantineStore::IsRecipient(message, account->GetAddress()))
         return BuildResponse_(404, "{\"error\":\"quarantined message not found\"}");

      if (!QuarantineStore::DiscardFor(id, account->GetAddress()))
         return BuildResponse_(500, "{\"error\":\"the quarantined message could not be discarded\"}");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   namespace
   {
      // A message is read whole into memory for GET /api/v1/me/messages/{id};
      // this is the ceiling. Streaming is the HTTP foundation's declared debt,
      // and until it is paid a larger message is described and not read.
      const int MaxMessageBodyBytes = 1024 * 1024;

      // Up to this size a message is still parsed - for its attachment
      // list, and for one attachment at a time on the download route.
      const int MaxMessageParseBytes = 32 * 1024 * 1024;

      // The size a user expects: the bytes the file has, not the base64
      // that carries them. Decoding is a pass over the part, so only a
      // message small enough to read whole pays it; above that the encoded
      // length stands in.
      int AttachmentSize(std::shared_ptr<Attachment> attachment, bool decode)
      {
         if (decode)
         {
            AnsiString decoded;
            if (attachment->GetContent(decoded))
               return decoded.GetLength();
         }

         return attachment->GetSize();
      }

      // The media type a part declares, without the parameters the field
      // carries beside it (name=, charset=): "image/png" out of
      // "image/png; name=\"logo.png\"". Lower-cased, because a media type is
      // case-insensitive and a reader that compares it should not have to know
      // that. Empty when the part declares nothing usable, or when the value
      // could not be a media type at all - no solidus, or a byte that would
      // break out of the header it is written back into.
      AnsiString MediaType(const AnsiString &declared)
      {
         AnsiString type = declared;

         int semicolon = type.Find(";");
         if (semicolon >= 0)
            type = type.Mid(0, semicolon);

         type.ToLower();
         type.TrimLeft();
         type.TrimRight();

         if (type.IsEmpty() || type.Find("\r") >= 0 || type.Find("\n") >= 0 || type.Find("/") < 0)
            return "";

         return type;
      }

      // The part's Content-ID with the angle brackets stripped. RFC 2045 writes
      // the field as a msg-id, angle brackets and all; RFC 2392 writes the same
      // value in a cid: URL without them, and a cid: URL in the HTML body is
      // what a page has to match against. Empty when the part carries no
      // Content-ID, and empty for a value that could not be one.
      AnsiString ContentId(std::shared_ptr<MimeBody> part)
      {
         if (!part)
            return "";

         const char *raw = part->GetRawFieldValue(CMimeConst::ContentID());
         if (!raw)
            return "";

         AnsiString value = raw;
         value.TrimLeft();
         value.TrimRight();

         if (value.GetLength() >= 2 && value.StartsWith("<") && value.EndsWith(">"))
            value = value.Mid(1, value.GetLength() - 2);

         if (value.Find("\r") >= 0 || value.Find("\n") >= 0)
            return "";

         return value;
      }

      // How many messages one listing returns at most: the newest, and the
      // caller pages further back with before_uid.
      const int MaxMessagesPerPage = 200;

      // The listing decodes Subject, From and Date from the head of each
      // message's file - what IMAP FETCH ENVELOPE does - rather than parsing
      // the MIME tree, so a folder of thousands stays cheap.
      void DescribeHeaderWide(const String &fileName, String &subject, String &from, String &date)
      {
         AnsiString header = PersistentMessage::LoadHeader(fileName, false);
         if (header.IsEmpty())
            return;

         MimeHeader mimeHeader;
         mimeHeader.Load(header.c_str(), header.GetLength(), true);

         subject = mimeHeader.GetUnicodeFieldValue("Subject");
         from = mimeHeader.GetUnicodeFieldValue("From");

         const char *rawDate = mimeHeader.GetRawFieldValue("Date");
         date = rawDate ? String(rawDate) : String();
      }

      // The same, as UTF-8 for the JSON.
      void DescribeHeader(const String &fileName, AnsiString &subject, AnsiString &from, AnsiString &date)
      {
         String wideSubject, wideFrom, wideDate;
         DescribeHeaderWide(fileName, wideSubject, wideFrom, wideDate);

         Unicode::WideToMultiByte(wideSubject, subject);
         Unicode::WideToMultiByte(wideFrom, from);
         Unicode::WideToMultiByte(wideDate, date);
      }

      AnsiString FlagsJson(std::shared_ptr<Message> message)
      {
         AnsiString flags;
         flags.Format("{\"seen\":%hs,\"flagged\":%hs,\"answered\":%hs,\"draft\":%hs,\"deleted\":%hs}",
            message->GetFlagSeen() ? "true" : "false",
            message->GetFlagFlagged() ? "true" : "false",
            message->GetFlagAnswered() ? "true" : "false",
            message->GetFlagDraft() ? "true" : "false",
            message->GetFlagDeleted() ? "true" : "false");
         return flags;
      }

      // The messages one search may look at before answering; the client
      // continues from next_before_uid. A mailbox is never scanned whole in
      // one request.
      const int MaxSearchScan = 2000;

      String ToLowerCopy(const String &value)
      {
         String lowered = value;
         lowered.ToLower();
         return lowered;
      }

      bool ContainsNoCase(const String &haystack, const String &needleLower)
      {
         if (needleLower.IsEmpty())
            return true;

         return ToLowerCopy(haystack).Find(needleLower) >= 0;
      }

      // What the full-text index can prove for one query: which of the
      // account's fully indexed messages cannot contain the text. The filter
      // SEARCH uses (IMAPCommandSearch.cpp), and it fails open the same way:
      // anything the index cannot answer for is read.
      class SearchIndexPrune
      {
      public:
         SearchIndexPrune(__int64 accountId, const String &text) :
            usable_(false)
         {
            if (text.IsEmpty() || !IniFileSettings::Instance()->GetIndexerFullTextEnabled())
               return;

            std::vector<String> needles;
            PersistentMessageIndex::CreateQueryNeedles(text, IniFileSettings::Instance()->GetIndexerFullTextMinTokenLength(), needles);
            if (needles.empty())
               return;

            if (!PersistentMessageIndex::GetMessagesWithTerm(accountId, PersistentMessageIndex::CompleteMarker(), complete_))
               return;

            if (!PersistentMessageIndex::GetMessagesWithTerm(accountId, PersistentMessageIndex::OverflowMarker(), overflow_))
               return;

            bool first = true;
            for (size_t i = 0; i < needles.size(); i++)
            {
               std::set<__int64> matches;
               if (!PersistentMessageIndex::GetMessagesWithTermContaining(accountId, needles[i], matches))
                  return;

               if (first)
               {
                  candidates_.swap(matches);
                  first = false;
               }
               else
               {
                  std::set<__int64> merged;
                  std::set_intersection(candidates_.begin(), candidates_.end(), matches.begin(), matches.end(), std::inserter(merged, merged.begin()));
                  candidates_.swap(merged);
               }

               if (candidates_.empty())
                  break;
            }

            usable_ = true;
         }

         bool CannotContain(__int64 messageId) const
         {
            if (!usable_)
               return false;

            if (complete_.find(messageId) == complete_.end())
               return false;

            if (overflow_.find(messageId) != overflow_.end())
               return false;

            return candidates_.find(messageId) == candidates_.end();
         }

      private:
         bool usable_;
         std::set<__int64> complete_;
         std::set<__int64> overflow_;
         std::set<__int64> candidates_;
      };

      // Whether one message contains the text: in its Subject or From, read
      // from the head of the file; failing that in its To, Cc, text and
      // HTML, for a message small enough to read whole and not ruled out by
      // the index.
      bool MessageMatches(const String &fileName, std::shared_ptr<Message> message, const String &needleLower, const SearchIndexPrune &prune)
      {
         String subject, from, date;
         DescribeHeaderWide(fileName, subject, from, date);

         if (ContainsNoCase(subject, needleLower) || ContainsNoCase(from, needleLower))
            return true;

         if (message->GetSize() > MaxMessageBodyBytes)
            return false;

         if (prune.CannotContain(message->GetID()))
            return false;

         MessageData data;
         if (!data.LoadFromMessage(fileName, message))
            return false;

         return ContainsNoCase(data.GetTo(), needleLower) || ContainsNoCase(data.GetCC(), needleLower) ||
                ContainsNoCase(data.GetBody(), needleLower) || ContainsNoCase(data.GetHTMLBody(), needleLower);
      }
   }

   // The right an account has on a folder, asked the way IMAP asks it: on the
   // account's own tree and the public tree it is CheckPermission (everything
   // allowed when enforcement is off); on another account's folder it is a
   // right that account granted, and with enforcement off nothing is granted.
   bool
   RestApiServer::RightOn_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolder> folder, int permission)
   {
      if (!folder)
         return false;

      if (folder->GetAccountID() != 0 && folder->GetAccountID() != account->GetID())
         return ACLManager::CheckDelegatedRight(account->GetID(), folder, permission);

      return ACLManager::CheckPermission(account->GetID(), folder, permission);
   }

   // The folder the id names, if the account may read it: in its own tree,
   // in the public tree, or in the tree of an owner who has shared with it -
   // the three places IMAP LIST looks, under the same rights.
   std::shared_ptr<IMAPFolder>
   RestApiServer::FindReadableFolder_(std::shared_ptr<const Account> account, __int64 folderId)
   {
      std::vector<std::shared_ptr<IMAPFolders>> trees;

      trees.push_back(IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID()));
      trees.push_back(IMAPFolderContainer::Instance()->GetPublicFolders());

      if (ACLManager::GetOtherUsersNamespaceEnabled())
      {
         ACLManager aclManager;
         std::vector<__int64> owners = aclManager.GetAccountsWithFolderShares();
         for (size_t i = 0; i < owners.size(); i++)
         {
            if (owners[i] != account->GetID())
               trees.push_back(IMAPFolderContainer::Instance()->GetFoldersForAccount(owners[i]));
         }
      }

      for (size_t i = 0; i < trees.size(); i++)
      {
         if (!trees[i])
            continue;

         std::shared_ptr<IMAPFolder> folder = trees[i]->GetItemByDBIDRecursive(folderId);
         if (!folder)
            continue;

         if (!RightOn_(account, folder, ACLPermission::PermissionLookup))
            return std::shared_ptr<IMAPFolder>();

         bool readAccess = false;
         bool writeAccess = false;
         ACLManager::GetReadWriteAccess(account->GetID(), folder, readAccess, writeAccess);

         return readAccess ? folder : std::shared_ptr<IMAPFolder>();
      }

      return std::shared_ptr<IMAPFolder>();
   }

   // Where a message's bytes are: under the account that owns it, or in the
   // public folder store when no account does. Never under the caller.
   String
   RestApiServer::MessageFile_(std::shared_ptr<const Message> message)
   {
      if (message->GetAccountID() > 0)
      {
         std::shared_ptr<const Account> owner = CacheContainer::Instance()->GetAccount(message->GetAccountID());
         if (!owner)
            return String();

         return PersistentMessage::GetFileName(owner, message);
      }

      return PersistentMessage::GetFileName(message);
   }

   void
   RestApiServer::AppendFolderJson_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolders> folders,
                                    const String &parentPath, const std::map<__int64, int> &designations,
                                    const String &delimiter, AnsiString &json, int depth)
   {
      // Depth-first, the way IMAP LIST walks the same tree. A tree deeper
      // than this is not one an IMAP client made, and it is not walked.
      if (!folders || depth > 32)
         return;

      int written = 0;
      for (int i = 0; i < folders->GetCount(); i++)
      {
         std::shared_ptr<IMAPFolder> folder = folders->GetItem(i);
         if (!folder)
            continue;

         // A folder the account may not look up or read is left out with its
         // subtree, exactly as LIST leaves it out.
         if (!RightOn_(account, folder, ACLPermission::PermissionLookup))
            continue;

         bool readAccess = false;
         bool writeAccess = false;
         ACLManager::GetReadWriteAccess(account->GetID(), folder, readAccess, writeAccess);
         if (!readAccess)
            continue;

         String path = parentPath.IsEmpty() ? folder->GetFolderName() : parentPath + delimiter + folder->GetFolderName();

         if (written > 0)
            json += ",";

         AppendOneFolderJson_(account, folder, path, designations, delimiter, writeAccess, json, depth);

         written++;
      }
   }

   // A stored folder name as a person should read it. IMAP and COM store the
   // name in modified UTF-7 - "&AMQ-renden" for "Ärenden" - and that is the form
   // to decode. But modified UTF-7 cannot contain a character above 127 by
   // construction, so a stored name that has one was never encoded: it is real
   // Unicode, put there by something that did not encode (an importer, or this
   // release's own create route before it learned to). Handing THAT to the
   // decoder means an AnsiString conversion first, which is lossy above ASCII -
   // the name would come back as question marks. So it is returned as it is.
   String
   RestApiServer::DecodeFolderName_(const String &stored)
   {
      for (int i = 0; i < stored.GetLength(); i++)
      {
         if (stored[i] > 127)
            return stored;
      }

      return ModifiedUTF7::Decode(AnsiString(stored));
   }

   void
   RestApiServer::AppendOneFolderJson_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolder> folder,
                                       const String &path, const std::map<__int64, int> &designations,
                                       const String &delimiter, bool writeAccess, AnsiString &json, int depth)
   {
      if (!folder)
         return;

      std::shared_ptr<Messages> messages = folder->GetMessages();
      long messageCount = messages ? messages->GetCount() : 0;
      long seen = messages ? messages->GetNoOfSeen() : 0;

      std::map<__int64, int>::const_iterator designation = designations.find(folder->GetID());
      String specialUse = designation != designations.end() ? IMAPSpecialUse::FormatDesignations(designation->second) : String();

      AnsiString entry;
      entry.Format("{\"id\":%I64d,\"account_id\":%I64d,\"name\":\"%hs\",\"path\":\"%hs\",\"parent_id\":%I64d,\"special_use\":\"%hs\",\"subscribed\":%hs,\"writable\":%hs,\"messages\":%ld,\"unseen\":%ld,\"uidvalidity\":%u,\"subfolders\":[",
         folder->GetID(),
         folder->GetAccountID(),
         // Decoded, as InterfaceIMAPFolder::get_Name decodes it for COM. A
         // folder name is stored in modified UTF-7 - "&AMQ-renden" for
         // "Ärenden" - which is the form IMAP puts on the wire and no form
         // any other caller wants: a page that shows this to a reader shows
         // them the encoding. The path is the same names joined by the
         // delimiter, and decoding it whole is safe because an encoded run
         // begins with '&' and ends with '-', neither of which a delimiter is.
         JsonEscape_(Utf8_(DecodeFolderName_(folder->GetFolderName()))).c_str(),
         JsonEscape_(Utf8_(DecodeFolderName_(path))).c_str(),
         folder->GetParentFolderID(),
         JsonEscape_(Utf8_(specialUse)).c_str(),
         folder->GetIsSubscribed() ? "true" : "false",
         writeAccess ? "true" : "false",
         messageCount,
         messageCount - seen,
         folder->GetCreationTime().ToInt());
      json += entry;

      AppendFolderJson_(account, folder->GetSubFolders(), path, designations, delimiter, json, depth + 1);

      json += "]}";
   }

   HttpResponse
   RestApiServer::HandleMeFolders_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      String delimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());

      std::map<__int64, int> designations;
      if (folders)
         IMAPSpecialUse::Resolve(folders, designations);

      AnsiString json;
      json.Format("{\"delimiter\":\"%hs\",\"folders\":[", JsonEscape_(Utf8_(delimiter)).c_str());
      AppendFolderJson_(account, folders, String(), designations, delimiter, json, 0);
      json += "],\"shared\":[";

      // What LIST shows beyond the account's own tree, named as LIST names
      // it: the public folders under their namespace, then the folders of
      // each owner who has granted this account a right, under "#Users" and
      // the owner's address. An owner who granted nothing this account may
      // read produces no entry, and so no trace.
      int shares = 0;

      std::shared_ptr<IMAPFolders> publicFolders = IMAPFolderContainer::Instance()->GetPublicFolders();
      if (publicFolders)
      {
         String publicName = Configuration::Instance()->GetIMAPConfiguration()->GetIMAPPublicFolderName();

         AnsiString tree;
         std::map<__int64, int> none;
         AppendFolderJson_(account, publicFolders, publicName, none, delimiter, tree, 0);

         if (!tree.IsEmpty())
         {
            AnsiString entry;
            entry.Format("{\"owner\":\"%hs\",\"folders\":[", JsonEscape_(Utf8_(publicName)).c_str());
            json += entry + tree + "]}";
            shares++;
         }
      }

      if (ACLManager::GetOtherUsersNamespaceEnabled())
      {
         String otherUsersName = ACLManager::GetOtherUsersFolderName();

         ACLManager aclManager;
         std::vector<__int64> owners = aclManager.GetAccountsWithFolderShares();

         for (size_t i = 0; i < owners.size(); i++)
         {
            if (owners[i] == account->GetID())
               continue;

            std::shared_ptr<const Account> owner = CacheContainer::Instance()->GetAccount(owners[i]);
            if (!owner)
               continue;

            std::shared_ptr<IMAPFolders> ownerFolders = IMAPFolderContainer::Instance()->GetFoldersForAccount(owners[i]);
            if (!ownerFolders)
               continue;

            std::map<__int64, int> ownerDesignations;
            IMAPSpecialUse::Resolve(ownerFolders, ownerDesignations);

            AnsiString tree;
            AppendFolderJson_(account, ownerFolders, otherUsersName + delimiter + owner->GetAddress(), ownerDesignations, delimiter, tree, 0);

            if (tree.IsEmpty())
               continue;

            if (shares > 0)
               json += ",";

            AnsiString entry;
            entry.Format("{\"owner\":\"%hs\",\"folders\":[", JsonEscape_(Utf8_(owner->GetAddress())).c_str());
            json += entry + tree + "]}";
            shares++;
         }
      }

      json += "]}";

      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMeFolderMessages_(const Caller &caller, __int64 folderId, const AnsiString &query)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder = FindReadableFolder_(account, folderId);
      if (!folder)
         return BuildResponse_(404, "{\"error\":\"folder not found\"}");

      int limit = MaxMessagesPerPage;
      AnsiString limitText = QueryParameter_(query, "limit");
      if (!limitText.IsEmpty())
      {
         limit = atoi(limitText.c_str());
         if (limit < 1 || limit > MaxMessagesPerPage)
            limit = MaxMessagesPerPage;
      }

      unsigned int beforeUid = 0;
      AnsiString beforeText = QueryParameter_(query, "before_uid");
      if (!beforeText.IsEmpty())
         beforeUid = (unsigned int) strtoul(beforeText.c_str(), nullptr, 10);

      // q narrows the listing to the messages containing the text.
      String text;
      Unicode::MultiByteToWide(QueryParameter_(query, "q"), text);
      text.TrimLeft();
      text.TrimRight();
      const String needleLower = ToLowerCopy(text);
      const bool searching = !text.IsEmpty();
      SearchIndexPrune prune(account->GetID(), text);

      // A snapshot, walked newest UID first: the collection is shared with
      // every IMAP session on the mailbox, and a copy is what lets this read
      // files without holding its lock.
      std::shared_ptr<Messages> messages = folder->GetMessages();
      std::vector<std::shared_ptr<Message>> snapshot = messages ? messages->GetCopy() : std::vector<std::shared_ptr<Message>>();

      int total = 0;
      for (std::vector<std::shared_ptr<Message>>::const_iterator it = snapshot.begin(); it != snapshot.end(); ++it)
      {
         if (*it && !(*it)->GetFlagDeleted())
            total++;
      }

      AnsiString json;
      json.Format("{\"folder_id\":%I64d,\"total\":%d,\"query\":\"%hs\",\"messages\":[", folder->GetID(), total, JsonEscape_(Utf8_(text)).c_str());

      int written = 0;
      int scanned = 0;
      bool complete = true;
      unsigned int lastUid = 0;

      for (std::vector<std::shared_ptr<Message>>::reverse_iterator it = snapshot.rbegin(); it != snapshot.rend(); ++it)
      {
         std::shared_ptr<Message> message = *it;
         if (!message || message->GetFlagDeleted())
            continue;

         if (beforeUid > 0 && message->GetUID() >= beforeUid)
            continue;

         if (written >= limit)
         {
            // A page is full. For a search that is a place to continue from;
            // a plain listing pages by before_uid as before.
            if (searching)
               complete = false;
            break;
         }

         const String fileName = MessageFile_(message);

         if (searching)
         {
            if (scanned >= MaxSearchScan)
            {
               complete = false;
               break;
            }

            scanned++;
            lastUid = message->GetUID();

            if (!MessageMatches(fileName, message, needleLower, prune))
               continue;
         }

         AnsiString subject, from, date;
         DescribeHeader(fileName, subject, from, date);

         if (written > 0)
            json += ",";

         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"uid\":%u,\"size\":%d,\"received\":\"%hs\",\"subject\":\"%hs\",\"from\":\"%hs\",\"date\":\"%hs\",\"flags\":%hs,%hs}",
            message->GetID(),
            message->GetUID(),
            message->GetSize(),
            JsonEscape_(Utf8_(message->GetCreateTime())).c_str(),
            JsonEscape_(subject).c_str(),
            JsonEscape_(from).c_str(),
            JsonEscape_(date).c_str(),
            FlagsJson(message).c_str(),
            ThreadFieldsJson_(MessageFile_(message)).c_str());
         json += entry;
         written++;
         lastUid = message->GetUID();
      }

      AnsiString tail;
      tail.Format("],\"scanned\":%d,\"complete\":%hs,\"next_before_uid\":%u}", scanned, complete ? "true" : "false", complete ? 0 : lastUid);
      json += tail;

      return BuildResponse_(200, json);
   }

   // Every folder of the account's own tree the account may read, with its
   // path, depth-first as the tree lists them.
   void
   RestApiServer::CollectReadableFolders_(std::shared_ptr<const Account> account, std::shared_ptr<IMAPFolders> folders, const String &parentPath,
                                          const String &delimiter, std::vector<std::pair<std::shared_ptr<IMAPFolder>, String>> &out, int depth)
   {
      if (!folders || depth > 32)
         return;

      for (int i = 0; i < folders->GetCount(); i++)
      {
         std::shared_ptr<IMAPFolder> folder = folders->GetItem(i);
         if (!folder)
            continue;

         bool readAccess = false;
         bool writeAccess = false;
         ACLManager::GetReadWriteAccess(account->GetID(), folder, readAccess, writeAccess);
         if (!readAccess)
            continue;

         String path = parentPath.IsEmpty() ? folder->GetFolderName() : parentPath + delimiter + folder->GetFolderName();
         out.push_back(std::make_pair(folder, path));

         CollectReadableFolders_(account, folder->GetSubFolders(), path, delimiter, out, depth + 1);
      }
   }

   HttpResponse
   RestApiServer::HandleMeSearch_(const Caller &caller, const AnsiString &query)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      String text;
      Unicode::MultiByteToWide(QueryParameter_(query, "q"), text);
      text.TrimLeft();
      text.TrimRight();
      if (text.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"q is required\"}");

      int limit = 50;
      AnsiString limitText = QueryParameter_(query, "limit");
      if (!limitText.IsEmpty())
      {
         limit = atoi(limitText.c_str());
         if (limit < 1 || limit > MaxMessagesPerPage)
            limit = MaxMessagesPerPage;
      }

      const String needleLower = ToLowerCopy(text);
      SearchIndexPrune prune(account->GetID(), text);

      String delimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      std::vector<std::pair<std::shared_ptr<IMAPFolder>, String>> folders;
      CollectReadableFolders_(account, IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID()), String(), delimiter, folders, 0);

      struct Hit
      {
         std::shared_ptr<Message> message;
         std::shared_ptr<IMAPFolder> folder;
         String path;
      };

      std::vector<Hit> hits;
      int scanned = 0;
      bool complete = true;

      // Every folder, newest first within each, under one budget for the
      // whole request.
      for (size_t f = 0; f < folders.size() && complete; f++)
      {
         std::shared_ptr<Messages> messages = folders[f].first->GetMessages();
         if (!messages)
            continue;

         std::vector<std::shared_ptr<Message>> snapshot = messages->GetCopy();
         for (std::vector<std::shared_ptr<Message>>::reverse_iterator it = snapshot.rbegin(); it != snapshot.rend(); ++it)
         {
            std::shared_ptr<Message> message = *it;
            if (!message || message->GetFlagDeleted())
               continue;

            if (scanned >= MaxSearchScan)
            {
               complete = false;
               break;
            }

            scanned++;

            if (!MessageMatches(MessageFile_(message), message, needleLower, prune))
               continue;

            Hit hit;
            hit.message = message;
            hit.folder = folders[f].first;
            hit.path = folders[f].second;
            hits.push_back(hit);
         }
      }

      // Newest first across folders: ids grow with time.
      std::sort(hits.begin(), hits.end(), [](const Hit &a, const Hit &b) { return a.message->GetID() > b.message->GetID(); });

      bool more = (int) hits.size() > limit;
      if (more)
         hits.resize(limit);

      AnsiString json;
      json.Format("{\"query\":\"%hs\",\"scanned\":%d,\"complete\":%hs,\"more\":%hs,\"messages\":[",
         JsonEscape_(Utf8_(text)).c_str(), scanned, complete ? "true" : "false", more ? "true" : "false");

      for (size_t i = 0; i < hits.size(); i++)
      {
         std::shared_ptr<Message> message = hits[i].message;

         AnsiString subject, from, date;
         DescribeHeader(MessageFile_(message), subject, from, date);

         if (i > 0)
            json += ",";

         AnsiString entry;
         entry.Format("{\"folder_id\":%I64d,\"folder\":\"%hs\",\"id\":%I64d,\"uid\":%u,\"size\":%d,\"received\":\"%hs\",\"subject\":\"%hs\",\"from\":\"%hs\",\"date\":\"%hs\",\"flags\":%hs,%hs}",
            hits[i].folder->GetID(),
            // Decoded, as the folder listing decodes it: the path is stored
            // names joined, and a stored name is modified UTF-7.
            JsonEscape_(Utf8_(DecodeFolderName_(hits[i].path))).c_str(),
            message->GetID(),
            message->GetUID(),
            message->GetSize(),
            JsonEscape_(Utf8_(message->GetCreateTime())).c_str(),
            JsonEscape_(subject).c_str(),
            JsonEscape_(from).c_str(),
            JsonEscape_(date).c_str(),
            FlagsJson(message).c_str(),
            ThreadFieldsJson_(MessageFile_(message)).c_str());
         json += entry;
      }

      json += "]}";
      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMeMessage_(const Caller &caller, __int64 messageId)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // The row first, then the folder it is in, which is what decides
      // whether this account may read it. Another account's message, or one
      // in a folder the ACL keeps from this account, is "not found": the id
      // space is shared, and a refusal that differed would say it exists.
      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      if (!PersistentMessage::ReadObject(message, messageId) || message->GetID() == 0)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      std::shared_ptr<IMAPFolder> folder = FindReadableFolder_(account, message->GetFolderID());
      if (!folder || folder->GetAccountID() != message->GetAccountID())
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      String fileName = MessageFile_(message);
      if (!FileUtilities::Exists(fileName))
         return BuildResponse_(404, "{\"error\":\"the message file is missing\"}");

      AnsiString subject, from, date;
      DescribeHeader(fileName, subject, from, date);

      AnsiString json;
      json.Format("{\"id\":%I64d,\"uid\":%u,\"folder_id\":%I64d,\"size\":%d,\"received\":\"%hs\",\"flags\":%hs,\"subject\":\"%hs\",\"from\":\"%hs\",\"date\":\"%hs\",",
         message->GetID(),
         message->GetUID(),
         message->GetFolderID(),
         message->GetSize(),
         JsonEscape_(Utf8_(message->GetCreateTime())).c_str(),
         FlagsJson(message).c_str(),
         JsonEscape_(subject).c_str(),
         JsonEscape_(from).c_str(),
         JsonEscape_(date).c_str());
      json += ThreadFieldsJson_(fileName) + ",";

      if (message->GetSize() > MaxMessageParseBytes)
      {
         json += "\"truncated\":true,\"to\":\"\",\"cc\":\"\",\"text\":\"\",\"html\":\"\",\"attachments\":[]}";
         return BuildResponse_(200, json);
      }

      // Between the two ceilings the message is parsed for its attachment
      // list, which the download route serves one at a time, and its text
      // is left out.
      bool bodyTooLarge = message->GetSize() > MaxMessageBodyBytes;

      MessageData messageData;
      if (!messageData.LoadFromMessage(MessageFile_(message), message))
         return BuildResponse_(500, "{\"error\":\"the message could not be parsed\"}");

      // The MIME parts behind the attachment list, in the very order
      // Attachments::Load walked them, so that index i here and index i there
      // are the same part. Attachment itself exposes neither the Content-ID nor
      // an unparameterised type, and adding accessors to a business object the
      // COM API also publishes is a wider change than reading the header the
      // list was built from.
      std::list<std::shared_ptr<MimeBody>> attachmentBodies;
      std::shared_ptr<MimeBody> mimeMessage = messageData.GetMimeMessage();
      if (mimeMessage)
         mimeMessage->GetAttachmentList(mimeMessage, attachmentBodies);

      std::vector<std::shared_ptr<MimeBody>> attachmentParts(attachmentBodies.begin(), attachmentBodies.end());

      AnsiString attachments = "[";
      std::shared_ptr<Attachments> attachmentList = messageData.GetAttachments();
      if (attachmentList)
      {
         for (size_t i = 0; i < attachmentList->GetCount(); i++)
         {
            std::shared_ptr<Attachment> attachment = attachmentList->GetItem((unsigned int) i);
            if (!attachment)
               continue;

            if (i > 0)
               attachments += ",";

            // The declared media type without its parameters, and the
            // Content-ID with the angle brackets RFC 2392 requires in the
            // header and forbids in a cid: URL taken off - which is the form a
            // page needs to match a cid: reference in the HTML body against
            // this list. Both are the empty string when the part declares
            // neither.
            AnsiString contentType = MediaType(attachment->GetContentType());
            AnsiString contentId = i < attachmentParts.size() ? ContentId(attachmentParts[i]) : AnsiString();

            AnsiString entry;
            entry.Format("{\"index\":%d,\"name\":\"%hs\",\"size\":%d,\"content_type\":\"%hs\",\"content_id\":\"%hs\"}",
               (int) i, JsonEscape_(Utf8_(attachment->GetFileName())).c_str(), AttachmentSize(attachment, message->GetSize() <= MaxMessageBodyBytes),
               JsonEscape_(contentType).c_str(), JsonEscape_(contentId).c_str());
            attachments += entry;
         }
      }
      attachments += "]";

      AnsiString tail;
      AnsiString text = bodyTooLarge ? AnsiString() : JsonEscape_(Utf8_(messageData.GetBody()));
      AnsiString html = bodyTooLarge ? AnsiString() : JsonEscape_(Utf8_(messageData.GetHTMLBody()));

      tail.Format("\"truncated\":%hs,\"to\":\"%hs\",\"cc\":\"%hs\",\"text\":\"%hs\",\"html\":\"%hs\",\"attachments\":%hs}",
         bodyTooLarge ? "true" : "false",
         JsonEscape_(Utf8_(messageData.GetTo())).c_str(),
         JsonEscape_(Utf8_(messageData.GetCC())).c_str(),
         text.c_str(),
         html.c_str(),
         attachments.c_str());
      json += tail;

      return BuildResponse_(200, json);
   }

   namespace
   {
      // A bare number in a JSON body - folder_id - with or without quotes.
      bool JsonNumber(const AnsiString &json, const AnsiString &key, __int64 &value)
      {
         AnsiString needle = "\"" + key + "\"";
         int keyPosition = json.Find(needle);
         if (keyPosition < 0)
            return false;

         int colon = json.Find(":", keyPosition + needle.GetLength());
         if (colon < 0)
            return false;

         int i = colon + 1;
         while (i < json.GetLength() && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n' || json[i] == '\"'))
            i++;

         int start = i;
         while (i < json.GetLength() && json[i] >= '0' && json[i] <= '9')
            i++;

         if (i == start)
            return false;

         value = _atoi64(json.Mid(start, i - start).c_str());
         return true;
      }

      // Every IMAP session on the folder is told, the way STORE, MOVE and
      // EXPUNGE tell them: by message id, which each turns into its own
      // sequence numbers.
      void NotifyFolder(std::shared_ptr<IMAPFolder> folder, ChangeNotification::NotificationType type, const std::vector<__int64> &messageIds)
      {
         std::shared_ptr<ChangeNotification> notification =
            std::shared_ptr<ChangeNotification>(new ChangeNotification(folder->GetAccountID(), folder->GetID(), type, messageIds));

         Application::Instance()->GetNotificationServer()->SendNotification(notification);
      }
   }

   // The message the id names, if it is the signed-in account's and sits in
   // a folder the account may read - and the live object every IMAP session
   // on the mailbox shares, since what is changed here must be what they see.
   std::shared_ptr<Message>
   RestApiServer::FindOwnMessage_(std::shared_ptr<const Account> account, __int64 messageId, std::shared_ptr<IMAPFolder> &folder)
   {
      std::shared_ptr<Message> row = std::shared_ptr<Message>(new Message());
      if (!PersistentMessage::ReadObject(row, messageId) || row->GetID() == 0)
         return std::shared_ptr<Message>();

      // The folder decides: the account's own, or one it may read under a
      // right the owner granted - and the row must belong to that folder's
      // owner, so a folder id cannot be used to reach another store's row.
      folder = FindReadableFolder_(account, row->GetFolderID());
      if (!folder || folder->GetAccountID() != row->GetAccountID())
         return std::shared_ptr<Message>();

      std::shared_ptr<Messages> messages = folder->GetMessages();
      if (!messages)
         return std::shared_ptr<Message>();

      return messages->GetItemByDBID(messageId);
   }

   // The account's folder carrying a special-use designation - \Trash,
   // \Sent - if it has one: a CREATE USE designation, or the name a client
   // gave it.
   std::shared_ptr<IMAPFolder>
   RestApiServer::FindDesignatedFolder_(std::shared_ptr<const Account> account, int designation)
   {
      std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
      if (!folders)
         return std::shared_ptr<IMAPFolder>();

      std::map<__int64, int> designations;
      IMAPSpecialUse::Resolve(folders, designations);

      for (std::map<__int64, int>::const_iterator it = designations.begin(); it != designations.end(); ++it)
      {
         if ((it->second & designation) == 0)
            continue;

         std::shared_ptr<IMAPFolder> folder = folders->GetItemByDBIDRecursive(it->first);
         if (folder)
            return folder;
      }

      return std::shared_ptr<IMAPFolder>();
   }

   // What EXPUNGE does for one message: the row and the file go, the
   // folder's shared collection drops it, and every session is told.
   bool
   RestApiServer::DeleteOwnMessage_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message, std::shared_ptr<IMAPFolder> folder)
   {
      std::shared_ptr<Messages> messages = MessagesContainer::Instance()->GetMessages(folder->GetAccountID(), folder->GetID());
      if (!messages)
         return false;

      std::set<__int64> ids;
      ids.insert(message->GetID());

      std::vector<__int64> deleted = messages->DeleteMessagesById(ids);
      if (deleted.empty())
         return false;

      NotifyFolder(folder, ChangeNotification::NotificationMessageDeleted, deleted);
      return true;
   }

   HttpResponse
   RestApiServer::HandleMeMessageFlags_(const Caller &caller, __int64 messageId, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> message = FindOwnMessage_(account, messageId, folder);
      if (!message)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      // Only the flags the body names change; the rest stay as they are, so
      // two clients touching different flags do not undo each other.
      static const char *names[] = { "seen", "flagged", "answered", "draft", "deleted" };
      bool named[5] = { false, false, false, false, false };
      bool values[5] = { false, false, false, false, false };
      int mentioned = 0;

      for (int i = 0; i < 5; i++)
      {
         AnsiString needle = AnsiString("\"") + names[i] + "\"";
         if (requestBody.Find(needle) < 0)
            continue;

         named[i] = true;
         values[i] = GetJsonBoolValue_(requestBody, names[i], false);
         mentioned++;
      }

      if (mentioned == 0)
         return BuildResponse_(400, "{\"error\":\"no flag named: seen, flagged, answered, draft or deleted\"}");

      // The rights STORE asks for. RFC 4314 makes \Seen, \Deleted and the
      // other flags three separate permissions, and they are asked for
      // separately here too.
      if (named[0] && !RightOn_(account, folder, ACLPermission::PermissionWriteSeen))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to change the seen flag\"}");

      if (named[4] && !RightOn_(account, folder, ACLPermission::PermissionWriteDeleted))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to change the deleted flag\"}");

      if ((named[1] || named[2] || named[3]) && !RightOn_(account, folder, ACLPermission::PermissionWriteOthers))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to change flags\"}");

      if (named[0])
         message->SetFlagSeen(values[0]);
      if (named[1])
         message->SetFlagFlagged(values[1]);
      if (named[2])
         message->SetFlagAnswered(values[2]);
      if (named[3])
         message->SetFlagDraft(values[3]);
      if (named[4])
         message->SetFlagDeleted(values[4]);

      // The path STORE takes: the flags are written with the folder's next
      // mod-sequence, so CONDSTORE and QRESYNC clients see the change.
      if (!Application::Instance()->GetFolderManager()->UpdateMessageFlags((int) folder->GetAccountID(), (int) folder->GetID(), message->GetID(), message->GetFlags()))
         return BuildResponse_(500, "{\"error\":\"the flags could not be stored\"}");

      std::vector<__int64> changed;
      changed.push_back(message->GetID());
      NotifyFolder(folder, ChangeNotification::NotificationMessageFlagsChanged, changed);

      AnsiString json;
      json.Format("{\"id\":%I64d,\"folder_id\":%I64d,\"flags\":%hs}", message->GetID(), folder->GetID(), FlagsJson(message).c_str());
      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMeMessageMove_(const Caller &caller, __int64 messageId, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> source;
      std::shared_ptr<Message> message = FindOwnMessage_(account, messageId, source);
      if (!message)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      __int64 folderId = 0;
      if (!JsonNumber(requestBody, "folder_id", folderId))
         return BuildResponse_(400, "{\"error\":\"folder_id is required\"}");

      // The destination goes through the same test as any folder id: the
      // account's own tree, readable. Another account's folder is 404.
      std::shared_ptr<IMAPFolder> destination = FindReadableFolder_(account, folderId);
      if (!destination)
         return BuildResponse_(404, "{\"error\":\"folder not found\"}");

      if (destination->GetID() == source->GetID())
         return BuildResponse_(400, "{\"error\":\"the message is already in that folder\"}");

      if (destination->GetAccountID() != source->GetAccountID())
         return BuildResponse_(400, "{\"error\":\"a message moves within its own mailbox; copy it by other means\"}");

      // The rights MOVE asks for: insert there, mark deleted and expunge here.
      if (!RightOn_(account, destination, ACLPermission::PermissionInsert))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to add messages\"}");

      if (!RightOn_(account, source, ACLPermission::PermissionWriteDeleted) ||
          !RightOn_(account, source, ACLPermission::PermissionExpunge))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to remove messages\"}");

      // Copy, then expunge: the shape MOVE has, so the moved message is a
      // new row with a new UID in the destination and every session on
      // either folder is told. A copy that could not be followed by the
      // expunge is reported as exactly that, and the copy is left.
      __int64 newMessageId = 0;
      if (!MessageUtilities::CopyToIMAPFolder(message, (int) destination->GetID(), newMessageId))
         return BuildResponse_(500, "{\"error\":\"the message could not be copied to the folder\"}");

      if (!DeleteOwnMessage_(account, message, source))
         return BuildResponse_(500, "{\"error\":\"the message was copied to the folder but the original could not be removed\"}");

      AnsiString json;
      json.Format("{\"id\":%I64d,\"folder_id\":%I64d}", newMessageId, destination->GetID());
      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMeMessageDelete_(const Caller &caller, __int64 messageId, const AnsiString &query)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> message = FindOwnMessage_(account, messageId, folder);
      if (!message)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      // The rights EXPUNGE asks for, since that is what this is.
      if (!RightOn_(account, folder, ACLPermission::PermissionWriteDeleted) ||
          !RightOn_(account, folder, ACLPermission::PermissionExpunge))
         return BuildResponse_(403, "{\"error\":\"the folder does not allow this account to delete messages\"}");

      AnsiString permanentText = QueryParameter_(query, "permanent");
      bool permanent = permanentText == "1" || permanentText == "true";

      // Deleting is moving to the folder designated \Trash when the account
      // has one and the message is not in it already - what a mail client
      // does - and final otherwise, or when the caller says permanent.
      if (!permanent && folder->GetAccountID() == account->GetID())
      {
         std::shared_ptr<IMAPFolder> trash = FindDesignatedFolder_(account, IMAPSpecialUse::DesignationTrash);
         if (trash && trash->GetID() != folder->GetID())
         {
            if (!RightOn_(account, trash, ACLPermission::PermissionInsert))
               return BuildResponse_(403, "{\"error\":\"the trash folder does not allow this account to add messages\"}");

            __int64 newMessageId = 0;
            if (!MessageUtilities::CopyToIMAPFolder(message, (int) trash->GetID(), newMessageId))
               return BuildResponse_(500, "{\"error\":\"the message could not be copied to the trash folder\"}");

            if (!DeleteOwnMessage_(account, message, folder))
               return BuildResponse_(500, "{\"error\":\"the message was copied to the trash folder but the original could not be removed\"}");

            AnsiString json;
            json.Format("{\"deleted\":false,\"moved_to\":%I64d,\"id\":%I64d}", trash->GetID(), newMessageId);
            return BuildResponse_(200, json);
         }
      }

      if (!DeleteOwnMessage_(account, message, folder))
         return BuildResponse_(500, "{\"error\":\"the message could not be deleted\"}");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   // A JSON body is UTF-8; the server's strings are wide.
   String
   RestApiServer::JsonUtf8Value_(const AnsiString &json, const AnsiString &key)
   {
      String value;
      Unicode::MultiByteToWide(GetJsonStringValue_(json, key), value);
      return value;
   }

   namespace
   {
      // "a@x, Name <b@y>; c@z" -> the three addresses, and the entries as
      // written for the header. A list is split on commas and semicolons; an
      // entry keeps its display name for the header and gives its address to
      // the envelope.
      void SplitAddressList(const String &list, std::vector<String> &addresses, std::vector<String> &entries)
      {
         std::vector<String> parts = StringParser::SplitString(list, _T(","));
         std::vector<String> all;
         for (size_t i = 0; i < parts.size(); i++)
         {
            std::vector<String> inner = StringParser::SplitString(parts[i], _T(";"));
            all.insert(all.end(), inner.begin(), inner.end());
         }

         for (size_t i = 0; i < all.size(); i++)
         {
            String entry = all[i];
            entry.TrimLeft();
            entry.TrimRight();
            if (entry.IsEmpty())
               continue;

            // "Name <address>" gives its address to the envelope and keeps the
            // whole entry for the header.
            String address = entry;
            int open = entry.Find(_T("<"));
            int close = entry.ReverseFind(_T(">"));
            if (open >= 0 && close > open)
               address = entry.Mid(open + 1, close - open - 1);
            address.TrimLeft();
            address.TrimRight();
            if (address.IsEmpty())
               address = entry;

            addresses.push_back(address);
            entries.push_back(entry);
         }
      }

      String JoinEntries(const std::vector<String> &entries)
      {
         String joined;
         for (size_t i = 0; i < entries.size(); i++)
         {
            if (i > 0)
               joined += _T(", ");
            joined += entries[i];
         }
         return joined;
      }
   }

   HttpResponse
   RestApiServer::HandleMeMessageSend_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      const String from = account->GetAddress();

      std::vector<String> toAddresses, toEntries, ccAddresses, ccEntries, bccAddresses, bccEntries;
      SplitAddressList(JsonUtf8Value_(requestBody, "to"), toAddresses, toEntries);
      SplitAddressList(JsonUtf8Value_(requestBody, "cc"), ccAddresses, ccEntries);
      SplitAddressList(JsonUtf8Value_(requestBody, "bcc"), bccAddresses, bccEntries);

      std::vector<String> recipients;
      recipients.insert(recipients.end(), toAddresses.begin(), toAddresses.end());
      recipients.insert(recipients.end(), ccAddresses.begin(), ccAddresses.end());
      recipients.insert(recipients.end(), bccAddresses.begin(), bccAddresses.end());

      if (recipients.empty())
         return BuildResponse_(400, "{\"error\":\"at least one recipient is required, in to, cc or bcc\"}");

      std::shared_ptr<SMTPConfiguration> smtpConfiguration = Configuration::Instance()->GetSMTPConfiguration();

      int maxRecipients = smtpConfiguration->GetMaxSMTPRecipientsInBatch();
      if (maxRecipients > 0 && (int) recipients.size() > maxRecipients)
      {
         AnsiString json;
         json.Format("{\"error\":\"too many recipients; the server allows %d\"}", maxRecipients);
         return BuildResponse_(400, json);
      }

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      message->SetFromAddress(from);

      // Every address is put through the two questions RCPT TO asks, as an
      // authenticated sender: may this sender deliver there, and what does
      // the address resolve to - so aliases, distribution lists, routes and
      // the relay rules apply unchanged, and a refused address is named.
      RecipientParser parser;
      for (size_t i = 0; i < recipients.size(); i++)
      {
         const String &address = recipients[i];

         if (!StringParser::IsValidEmailAddress(address))
            return BuildResponse_(400, "{\"error\":\"" + JsonEscape_(Utf8_(address)) + ": not an e-mail address\"}");

         String reason;
         bool treatSecurityAsLocal = false;
         RecipientParser::DeliveryPossibility possibility =
            parser.CheckDeliveryPossibility(true, from, address, reason, treatSecurityAsLocal, 0, true);

         if (possibility != RecipientParser::DP_Possible)
         {
            if (reason.IsEmpty())
            {
               if (possibility == RecipientParser::DP_RecipientUnknown)
                  reason = _T("unknown recipient");
               else if (possibility == RecipientParser::DP_MailboxFull)
                  reason = _T("the mailbox is full");
               else
                  reason = _T("delivery is not permitted");
            }

            return BuildResponse_(400, "{\"error\":\"" + JsonEscape_(Utf8_(address)) + ": " + JsonEscape_(Utf8_(reason)) + "\"}");
         }

         bool recipientOK = false;
         parser.CreateMessageRecipientList(address, message->GetRecipients(), recipientOK);
         if (!recipientOK)
            return BuildResponse_(400, "{\"error\":\"" + JsonEscape_(Utf8_(address)) + ": unknown recipient\"}");
      }

      if (message->GetRecipients()->GetCount() == 0)
         return BuildResponse_(400, "{\"error\":\"no address resolved to a recipient\"}");

      // The message: the account's name and address, the lists as written,
      // a Date and a Message-ID, the text as UTF-8. Bcc goes to the envelope
      // and nowhere else.
      String fromHeader = FromHeader_(account);

      const String fileName = PersistentMessage::GetFileName(message);

      MessageData messageData;
      messageData.LoadFromMessage(fileName, message);
      messageData.SetCharset(_T("utf-8"));
      messageData.SetFrom(fromHeader);
      messageData.SetTo(JoinEntries(toEntries));
      if (!ccEntries.empty())
         messageData.SetCC(JoinEntries(ccEntries));
      messageData.SetSubject(JsonUtf8Value_(requestBody, "subject"));
      messageData.SetBody(JsonUtf8Value_(requestBody, "text"));
      messageData.SetSentTime(Time::GetCurrentMimeDate());
      messageData.GenerateMessageID();

      // A reply names what it answers, so the recipient's client threads it.
      String inReplyTo = JsonUtf8Value_(requestBody, "in_reply_to");
      String references = JsonUtf8Value_(requestBody, "references");
      if (!inReplyTo.IsEmpty())
         messageData.SetFieldValue(_T("In-Reply-To"), inReplyTo);
      if (!references.IsEmpty())
         messageData.SetFieldValue(_T("References"), references);

      AnsiString attachmentError;
      int attachmentStatus = AddAttachmentsFromJson_(messageData, requestBody, attachmentError);
      if (attachmentStatus != 0)
         return BuildResponse_(attachmentStatus, attachmentError);

      if (!messageData.Write(fileName))
         return BuildResponse_(500, "{\"error\":\"the message could not be written\"}");

      message->SetSize((int) FileUtilities::FileSize(fileName));

      int maxKB = smtpConfiguration->GetMaxMessageSize();
      if (maxKB > 0 && (__int64) message->GetSize() > (__int64) maxKB * 1024)
      {
         FileUtilities::DeleteFile(fileName);
         return BuildResponse_(413, "{\"error\":\"the message is larger than the server allows\"}");
      }

      message->SetState(Message::Delivering);

      if (!PersistentMessage::SaveObject(message))
      {
         FileUtilities::DeleteFile(fileName);
         return BuildResponse_(500, "{\"error\":\"the message could not be queued\"}");
      }

      // A copy for the folder designated \Sent, marked read, before the queue
      // takes the file - when the account has such a folder and its quota
      // allows. Without one the message is sent and not kept, as over SMTP.
      __int64 sentId = 0;
      std::shared_ptr<IMAPFolder> sent = FindDesignatedFolder_(account, IMAPSpecialUse::DesignationSent);
      if (sent)
      {
         __int64 maxBytes = (__int64) account->GetAccountMaxSize() * 1024 * 1024;
         __int64 used = AccountSizeCache::Instance()->GetSize(account->GetID());
         bool fits = maxBytes <= 0 || used + message->GetSize() <= maxBytes;

         if (fits && RightOn_(account, sent, ACLPermission::PermissionInsert))
         {
            std::shared_ptr<Message> copy = PersistentMessage::CopyToIMAPFolder(message, sent);
            if (copy)
            {
               copy->SetFlagSeen(true);

               if (PersistentMessage::SaveObject(copy))
               {
                  sentId = copy->GetID();
                  sent->GetMessages()->Refresh(false);

                  std::shared_ptr<ChangeNotification> notification =
                     std::shared_ptr<ChangeNotification>(new ChangeNotification(account->GetID(), sent->GetID(), ChangeNotification::NotificationMessageAdded));
                  Application::Instance()->GetNotificationServer()->SendNotification(notification);
               }
               else
               {
                  FileUtilities::DeleteFile(PersistentMessage::GetFileName(account, copy));
               }
            }
         }
      }

      Application::Instance()->SubmitPendingEmail();

      // The message this answers, when the caller names it, gets \Answered -
      // what a mail client does on send, under the right STORE would ask for.
      __int64 answeredId = 0;
      if (JsonNumber(requestBody, "answered_id", answeredId) && answeredId > 0)
      {
         std::shared_ptr<IMAPFolder> answeredFolder;
         std::shared_ptr<Message> answered = FindOwnMessage_(account, answeredId, answeredFolder);
         if (answered && !answered->GetFlagAnswered() && RightOn_(account, answeredFolder, ACLPermission::PermissionWriteOthers))
         {
            answered->SetFlagAnswered(true);
            if (Application::Instance()->GetFolderManager()->UpdateMessageFlags((int) answeredFolder->GetAccountID(), (int) answeredFolder->GetID(), answered->GetID(), answered->GetFlags()))
            {
               std::vector<__int64> changed;
               changed.push_back(answered->GetID());
               NotifyFolder(answeredFolder, ChangeNotification::NotificationMessageFlagsChanged, changed);
            }
         }
      }

      AnsiString json;
      json.Format("{\"queued\":true,\"recipients\":%d,\"sent_id\":%I64d}", message->GetRecipients()->GetCount(), sentId);
      return BuildResponse_(201, json);
   }

   namespace
   {
      // RFC 8187: the bytes of a UTF-8 name that are not attr-char, percent-encoded.
      AnsiString PercentEncodeUtf8(const String &value)
      {
         AnsiString utf8;
         Unicode::WideToMultiByte(value, utf8);

         static const char *hex = "0123456789ABCDEF";
         AnsiString encoded;
         for (int i = 0; i < utf8.GetLength(); i++)
         {
            unsigned char c = (unsigned char) utf8.c_str()[i];
            bool plain = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                         c == '-' || c == '.' || c == '_' || c == '~';
            if (plain)
            {
               encoded += (char) c;
               continue;
            }

            encoded += '%';
            encoded += hex[c >> 4];
            encoded += hex[c & 15];
         }
         return encoded;
      }

      // The name as a quoted-string an old client can read: ASCII only, and
      // nothing that would end the string or the header.
      AnsiString AsciiFileName(const String &value)
      {
         AnsiString ascii;
         for (int i = 0; i < value.GetLength(); i++)
         {
            wchar_t c = value.c_str()[i];
            bool safe = c >= 0x20 && c < 0x7F && c != '"' && c != '\\';
            ascii += safe ? (char) c : '_';
         }
         if (ascii.IsEmpty())
            ascii = "attachment";
         return ascii;
      }

      // A media type that a browser would run or render is not served under
      // it: with the session cookie attached, an attachment that rendered as
      // a page on this origin could act as the page. Everything else keeps
      // its type; the disposition, nosniff and the sandbox policy hold too.
      AnsiString SafeMediaType(const AnsiString &declared)
      {
         // The same media type the message listing reports in content_type, so
         // that a page which decided from that field what the part is gets the
         // very same type when it fetches the bytes - which is what lets an
         // <img> pointed at this route render, with nosniff set.
         AnsiString type = MediaType(declared);

         if (type.IsEmpty())
            return "application/octet-stream";

         if (type == "text/html" || type == "application/xhtml+xml" || type == "image/svg+xml" ||
             type == "text/xml" || type == "application/xml" || type.EndsWith("+xml") ||
             type == "text/javascript" || type == "application/javascript")
            return "application/octet-stream";

         return type;
      }
   }

   HttpResponse
   RestApiServer::HandleMeMessageAttachment_(const Caller &caller, __int64 messageId, int index)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<IMAPFolder> folder;
      std::shared_ptr<Message> message = FindOwnMessage_(account, messageId, folder);
      if (!message)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      if (message->GetSize() > MaxMessageParseBytes)
         return BuildResponse_(413, "{\"error\":\"the message is too large to read here\"}");

      String fileName = MessageFile_(message);
      if (!FileUtilities::Exists(fileName))
         return BuildResponse_(404, "{\"error\":\"the message file is missing\"}");

      MessageData messageData;
      if (!messageData.LoadFromMessage(MessageFile_(message), message))
         return BuildResponse_(500, "{\"error\":\"the message could not be parsed\"}");

      std::shared_ptr<Attachments> attachments = messageData.GetAttachments();
      if (!attachments || index < 0 || index >= (int) attachments->GetCount())
         return BuildResponse_(404, "{\"error\":\"attachment not found\"}");

      std::shared_ptr<Attachment> attachment = attachments->GetItem((unsigned int) index);
      if (!attachment)
         return BuildResponse_(404, "{\"error\":\"attachment not found\"}");

      AnsiString bytes;
      if (!attachment->GetContent(bytes))
         return BuildResponse_(500, "{\"error\":\"the attachment could not be decoded\"}");

      String name = attachment->GetFileName();
      if (name.IsEmpty())
         name = _T("attachment");

      HttpResponse response;
      response.status = 200;
      response.content_type = SafeMediaType(attachment->GetContentType());
      response.body = bytes;
      response.extra_headers =
         "Content-Disposition: attachment; filename=\"" + AsciiFileName(name) + "\"; filename*=UTF-8''" + PercentEncodeUtf8(name) + "\r\n"
         "X-Content-Type-Options: nosniff\r\n"
         "Content-Security-Policy: sandbox\r\n"
         "Cache-Control: no-store\r\n";
      return response;
   }

   namespace
   {
      // The text of one object in a JSON body - for {"forwarding":{...}} the
      // braces and what is between them - or empty when the key is absent.
      // Keys inside two objects may repeat (enabled), so each object is read
      // on its own.
      AnsiString JsonObject(const AnsiString &json, const AnsiString &key)
      {
         AnsiString needle = "\"" + key + "\"";
         int keyPosition = json.Find(needle);
         if (keyPosition < 0)
            return "";

         int open = json.Find("{", keyPosition + needle.GetLength());
         if (open < 0)
            return "";

         int depth = 0;
         bool inString = false;
         for (int i = open; i < json.GetLength(); i++)
         {
            char c = json[i];

            if (inString)
            {
               if (c == '\\')
               {
                  i++;
                  continue;
               }

               if (c == '\"')
                  inString = false;

               continue;
            }

            if (c == '\"')
            {
               inString = true;
               continue;
            }

            if (c == '{')
               depth++;
            else if (c == '}')
            {
               depth--;
               if (depth == 0)
                  return json.Mid(open, i - open + 1);
            }
         }

         return "";
      }

      const int MaxNameLength = 100;
      const int MaxSignatureLength = 20000;
      const int MaxScriptLength = 256 * 1024;
   }

   AnsiString
   RestApiServer::SettingsJson_(std::shared_ptr<const Account> account)
   {
      AnsiString json;
      json.Format("{\"name\":{\"first\":\"%hs\",\"last\":\"%hs\"},"
                  "\"forwarding\":{\"enabled\":%hs,\"address\":\"%hs\",\"keep_original\":%hs},"
                  "\"signature\":{\"enabled\":%hs,\"text\":\"%hs\",\"html\":\"%hs\"}}",
         JsonEscape_(Utf8_(account->GetPersonFirstName())).c_str(),
         JsonEscape_(Utf8_(account->GetPersonLastName())).c_str(),
         account->GetForwardEnabled() ? "true" : "false",
         JsonEscape_(Utf8_(account->GetForwardAddress())).c_str(),
         account->GetForwardKeepOriginal() ? "true" : "false",
         account->GetEnableSignature() ? "true" : "false",
         JsonEscape_(Utf8_(account->GetSignaturePlainText())).c_str(),
         JsonEscape_(Utf8_(account->GetSignatureHTML())).c_str());
      return json;
   }

   HttpResponse
   RestApiServer::HandleMeSettings_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      return BuildResponse_(200, SettingsJson_(account));
   }

   HttpResponse
   RestApiServer::HandleMeSettingsPut_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // Each of the three objects the body names is applied whole; one it
      // does not name is left as it is.
      AnsiString nameObject = JsonObject(requestBody, "name");
      AnsiString forwardingObject = JsonObject(requestBody, "forwarding");
      AnsiString signatureObject = JsonObject(requestBody, "signature");

      if (nameObject.IsEmpty() && forwardingObject.IsEmpty() && signatureObject.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"nothing to change: name, forwarding or signature\"}");

      std::shared_ptr<Account> mutableAccount = std::shared_ptr<Account>(new Account());
      if (!PersistentAccount::ReadObject(mutableAccount, account->GetID()) || mutableAccount->GetID() == 0)
         return BuildResponse_(500, "{\"error\":\"the account could not be read\"}");

      if (!nameObject.IsEmpty())
      {
         String first = JsonUtf8Value_(nameObject, "first");
         String last = JsonUtf8Value_(nameObject, "last");
         if (first.GetLength() > MaxNameLength || last.GetLength() > MaxNameLength)
            return BuildResponse_(400, "{\"error\":\"a name is at most 100 characters\"}");

         mutableAccount->SetPersonFirstName(first);
         mutableAccount->SetPersonLastName(last);
      }

      if (!forwardingObject.IsEmpty())
      {
         bool enabled = GetJsonBoolValue_(forwardingObject, "enabled", false);
         String address = JsonUtf8Value_(forwardingObject, "address");
         bool keepOriginal = GetJsonBoolValue_(forwardingObject, "keep_original", true);
         address.TrimLeft();
         address.TrimRight();

         if (enabled)
         {
            if (!StringParser::IsValidEmailAddress(address))
               return BuildResponse_(400, "{\"error\":\"forwarding needs an e-mail address\"}");

            if (address.CompareNoCase(account->GetAddress()) == 0)
               return BuildResponse_(400, "{\"error\":\"forwarding to the account itself would loop\"}");
         }

         mutableAccount->SetForwardEnabled(enabled);
         mutableAccount->SetForwardAddress(address);
         mutableAccount->SetForwardKeepOriginal(keepOriginal);
      }

      if (!signatureObject.IsEmpty())
      {
         bool enabled = GetJsonBoolValue_(signatureObject, "enabled", false);
         String text = JsonUtf8Value_(signatureObject, "text");
         String html = JsonUtf8Value_(signatureObject, "html");
         if (text.GetLength() > MaxSignatureLength || html.GetLength() > MaxSignatureLength)
            return BuildResponse_(400, "{\"error\":\"a signature is at most 20000 characters\"}");

         mutableAccount->SetEnableSignature(enabled);
         mutableAccount->SetSignaturePlainText(text);
         mutableAccount->SetSignatureHTML(html);
      }

      String saveError;
      if (!PersistentAccount::SaveObject(mutableAccount, saveError, PersistenceModeNormal))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError.IsEmpty() ? String(_T("the account could not be saved")) : saveError)).c_str());
         return BuildResponse_(500, body);
      }

      return BuildResponse_(200, SettingsJson_(mutableAccount));
   }

   HttpResponse
   RestApiServer::HandleMeFilters_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      AnsiString json;
      json.Format("{\"active\":\"%hs\",\"name\":\"%hs\"}",
         JsonEscape_(Utf8_(SieveStorage::GetActiveScript(account->GetAddress()))).c_str(),
         JsonEscape_(Utf8_(SieveStorage::GetActiveScriptName(account->GetAddress()))).c_str());
      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMeFiltersPut_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      if (requestBody.Find("\"script\"") < 0)
         return BuildResponse_(400, "{\"error\":\"script is required; an empty script removes the filter\"}");

      String script = JsonUtf8Value_(requestBody, "script");
      if (script.GetLength() > MaxScriptLength)
         return BuildResponse_(400, "{\"error\":\"a script is at most 256 KB\"}");

      // The check ManageSieve's PUTSCRIPT and CHECKSCRIPT make, with the
      // same wording; a script that does not parse is not stored.
      if (!script.IsEmpty())
      {
         String syntaxError = SieveScript::CheckSyntax(script);
         if (!syntaxError.IsEmpty())
         {
            AnsiString body;
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(syntaxError)).c_str());
            return BuildResponse_(400, body);
         }
      }

      if (!SieveStorage::SetActiveScript(account->GetAddress(), script))
         return BuildResponse_(500, "{\"error\":\"the script could not be stored\"}");

      AnsiString json;
      json.Format("{\"active\":\"%hs\"}", JsonEscape_(Utf8_(script)).c_str());
      return BuildResponse_(200, json);
   }

   // The three headers a thread is built from, read from the head of the
   // file the way the listing reads Subject and From.
   AnsiString
   RestApiServer::ThreadFieldsJson_(const String &fileName)
   {
      AnsiString messageId, inReplyTo, references;

      AnsiString header = PersistentMessage::LoadHeader(fileName, false);
      if (!header.IsEmpty())
      {
         MimeHeader mimeHeader;
         mimeHeader.Load(header.c_str(), header.GetLength(), true);

         const char *value = mimeHeader.GetRawFieldValue("Message-ID");
         messageId = value ? value : "";
         value = mimeHeader.GetRawFieldValue("In-Reply-To");
         inReplyTo = value ? value : "";
         value = mimeHeader.GetRawFieldValue("References");
         references = value ? value : "";

         messageId.TrimLeft();
         messageId.TrimRight();
         inReplyTo.TrimLeft();
         inReplyTo.TrimRight();
         references.TrimLeft();
         references.TrimRight();
      }

      AnsiString json;
      json.Format("\"message_id\":\"%hs\",\"in_reply_to\":\"%hs\",\"references\":\"%hs\"",
         JsonEscape_(messageId).c_str(), JsonEscape_(inReplyTo).c_str(), JsonEscape_(references).c_str());
      return json;
   }

   // "First Last" <address>, or the bare address when the account has no name.
   String
   RestApiServer::FromHeader_(std::shared_ptr<const Account> account)
   {
      String name = account->GetPersonFirstName();
      String lastName = account->GetPersonLastName();
      if (!lastName.IsEmpty())
      {
         if (!name.IsEmpty())
            name += _T(" ");
         name += lastName;
      }

      if (name.IsEmpty())
         return account->GetAddress();

      String header = _T("\"");
      header += name;
      header += _T("\" <");
      header += account->GetAddress();
      header += _T(">");
      return header;
   }

   HttpResponse
   RestApiServer::HandleMeDraftSave_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      // The folder designated \Drafts, made as "Drafts" when the account has
      // none - what a mail client does the first time it saves one.
      std::shared_ptr<IMAPFolder> drafts = FindDesignatedFolder_(account, IMAPSpecialUse::DesignationDrafts);
      if (!drafts)
      {
         std::shared_ptr<IMAPFolders> folders = IMAPFolderContainer::Instance()->GetFoldersForAccount(account->GetID());
         if (!folders)
            return BuildResponse_(500, "{\"error\":\"the folders could not be read\"}");

         std::vector<String> path;
         path.push_back(_T("Drafts"));
         folders->CreatePath(folders, path, true);

         drafts = FindDesignatedFolder_(account, IMAPSpecialUse::DesignationDrafts);
         if (!drafts)
            return BuildResponse_(500, "{\"error\":\"the Drafts folder could not be created\"}");
      }

      if (!RightOn_(account, drafts, ACLPermission::PermissionInsert))
         return BuildResponse_(403, "{\"error\":\"the Drafts folder does not allow this account to add messages\"}");

      // Written as the send route writes a message, and kept instead of
      // queued: Bcc stays in the draft, since nothing is sent.
      std::shared_ptr<Message> draft = std::shared_ptr<Message>(new Message());
      draft->SetAccountID(drafts->GetAccountID());
      draft->SetFolderID(drafts->GetID());

      const String fileName = PersistentMessage::GetFileName(account, draft);

      MessageData messageData;
      messageData.LoadFromMessage(fileName, draft);
      messageData.SetCharset(_T("utf-8"));
      messageData.SetFrom(FromHeader_(account));
      messageData.SetTo(JsonUtf8Value_(requestBody, "to"));
      String cc = JsonUtf8Value_(requestBody, "cc");
      if (!cc.IsEmpty())
         messageData.SetCC(cc);
      String bcc = JsonUtf8Value_(requestBody, "bcc");
      if (!bcc.IsEmpty())
         messageData.SetBCC(bcc);
      messageData.SetSubject(JsonUtf8Value_(requestBody, "subject"));
      messageData.SetBody(JsonUtf8Value_(requestBody, "text"));
      messageData.SetSentTime(Time::GetCurrentMimeDate());
      messageData.GenerateMessageID();

      AnsiString attachmentError;
      int attachmentStatus = AddAttachmentsFromJson_(messageData, requestBody, attachmentError);
      if (attachmentStatus != 0)
         return BuildResponse_(attachmentStatus, attachmentError);

      if (!messageData.Write(fileName))
         return BuildResponse_(500, "{\"error\":\"the draft could not be written\"}");

      draft->SetSize((int) FileUtilities::FileSize(fileName));

      if (!account->SpaceAvailable(draft->GetSize()))
      {
         FileUtilities::DeleteFile(fileName);
         return BuildResponse_(413, "{\"error\":\"the mailbox is full\"}");
      }

      draft->SetState(Message::Delivered);
      draft->SetFlagDraft(true);
      draft->SetFlagSeen(true);

      if (!PersistentMessage::SaveObject(draft))
      {
         FileUtilities::DeleteFile(fileName);
         return BuildResponse_(500, "{\"error\":\"the draft could not be saved\"}");
      }

      MessagesContainer::Instance()->SetFolderNeedsRefresh(drafts->GetID());

      std::shared_ptr<ChangeNotification> notification =
         std::shared_ptr<ChangeNotification>(new ChangeNotification(drafts->GetAccountID(), drafts->GetID(), ChangeNotification::NotificationMessageAdded));
      Application::Instance()->GetNotificationServer()->SendNotification(notification);

      // The draft this one replaces goes once the new one is safe - a new UID
      // for new content, as IMAP requires, and the old one expunged.
      __int64 replaceId = 0;
      if (JsonNumber(requestBody, "replace_id", replaceId) && replaceId > 0)
      {
         std::shared_ptr<IMAPFolder> oldFolder;
         std::shared_ptr<Message> old = FindOwnMessage_(account, replaceId, oldFolder);
         if (old && old->GetFlagDraft() &&
             RightOn_(account, oldFolder, ACLPermission::PermissionWriteDeleted) &&
             RightOn_(account, oldFolder, ACLPermission::PermissionExpunge))
            DeleteOwnMessage_(account, old, oldFolder);
      }

      AnsiString json;
      json.Format("{\"id\":%I64d,\"folder_id\":%I64d}", draft->GetID(), drafts->GetID());
      return BuildResponse_(201, json);
   }

   namespace
   {
      // What one message may carry from the portal. The request cap for the
      // two routes that take attachments is sixteen megabytes on the wire;
      // base64 adds a third, so twelve megabytes of files is what fits.
      const int MaxAttachmentsPerMessage = 20;
      const __int64 MaxAttachmentBytesPerMessage = 12 * 1024 * 1024;

      // The text of the n-th object in the "attachments" array of a JSON
      // body, by brace matching - or empty when there is none.
      AnsiString JsonArrayObject(const AnsiString &json, const AnsiString &key, int index)
      {
         AnsiString needle = "\"" + key + "\"";
         int keyPosition = json.Find(needle);
         if (keyPosition < 0)
            return "";

         int bracket = json.Find("[", keyPosition + needle.GetLength());
         if (bracket < 0)
            return "";

         int depth = 0;
         bool inString = false;
         int objectStart = -1;
         int found = 0;
         for (int i = bracket + 1; i < json.GetLength(); i++)
         {
            char c = json[i];

            if (inString)
            {
               if (c == '\\')
               {
                  i++;
                  continue;
               }

               if (c == '\"')
                  inString = false;

               continue;
            }

            if (c == '\"')
            {
               inString = true;
               continue;
            }

            if (c == '{')
            {
               if (depth == 0)
                  objectStart = i;
               depth++;
            }
            else if (c == '}')
            {
               depth--;
               if (depth == 0)
               {
                  if (found == index)
                     return json.Mid(objectStart, i - objectStart + 1);
                  found++;
               }
            }
            else if (c == ']' && depth == 0)
            {
               break;
            }
         }

         return "";
      }

      // A file name for the temporary file the MIME library reads: the name
      // the sender gave, minus anything a path or a header could not carry.
      String SafeAttachmentName(const String &name)
      {
         String safe;
         for (int i = 0; i < name.GetLength(); i++)
         {
            wchar_t c = name.c_str()[i];
            bool bad = c < 0x20 || c == '\\' || c == '/' || c == ':' || c == '*' || c == '?' || c == '\"' || c == '<' || c == '>' || c == '|';
            safe += bad ? L'_' : c;
         }
         safe.TrimLeft();
         safe.TrimRight();
         if (safe.IsEmpty() || safe == _T(".") || safe == _T(".."))
            safe = _T("attachment");
         if (safe.GetLength() > 200)
            safe = safe.Mid(0, 200);
         return safe;
      }
   }

   // The attachments a body names - name, type, data (base64) - added to the
   // message the way COM's Attachments.Add adds a file: each is written under
   // its own name in a directory of its own in the temp folder, read from
   // there, and the directory removed. Returns 0, or the status to answer.
   int
   RestApiServer::AddAttachmentsFromJson_(MessageData &messageData, const AnsiString &requestBody, AnsiString &error)
   {
      if (requestBody.Find("\"attachments\"") < 0)
         return 0;

      __int64 totalBytes = 0;

      for (int index = 0; index < MaxAttachmentsPerMessage + 1; index++)
      {
         AnsiString object = JsonArrayObject(requestBody, "attachments", index);
         if (object.IsEmpty())
            break;

         if (index >= MaxAttachmentsPerMessage)
         {
            error.Format("{\"error\":\"at most %d attachments\"}", MaxAttachmentsPerMessage);
            return 400;
         }

         String name = SafeAttachmentName(JsonUtf8Value_(object, "name"));
         String type = JsonUtf8Value_(object, "type");
         AnsiString encoded = GetJsonStringValue_(object, "data");

         AnsiString bytes = Base64::Decode(encoded.c_str(), encoded.GetLength());
         totalBytes += bytes.GetLength();
         if (totalBytes > MaxAttachmentBytesPerMessage)
         {
            error = "{\"error\":\"the attachments are larger than twelve megabytes together\"}";
            return 413;
         }

         type.TrimLeft();
         type.TrimRight();
         if (type.IsEmpty() || type.Find(_T("/")) < 0 || type.Find(_T("\r")) >= 0 || type.Find(_T("\n")) >= 0 || type.GetLength() > 100)
            type = _T("application/octet-stream");

         String directory = IniFileSettings::Instance()->GetTempDirectory() + FileUtilities::PathSeparator + GUIDCreator::GetGUID();
         if (!FileUtilities::CreateDirectory(directory))
         {
            error = "{\"error\":\"the attachment could not be written\"}";
            return 500;
         }

         String path = directory + FileUtilities::PathSeparator + name;
         bool added = FileUtilities::WriteToFile(path, bytes) && messageData.GetAttachments()->Add(path, type);

         FileUtilities::DeleteDirectory(directory, true);

         if (!added)
         {
            error = "{\"error\":\"the attachment could not be added\"}";
            return 500;
         }

         std::shared_ptr<Attachments> attachments = messageData.GetAttachments();
         std::shared_ptr<Attachment> last = attachments->GetItem((unsigned int) attachments->GetCount() - 1);
         if (last)
            last->SetFileName(name);
      }

      return 0;
   }

   bool
   RestApiServer::IsLargeRequest_(const AnsiString &method, const AnsiString &target)
   {
      if (method != "POST")
         return false;

      AnsiString path = target;
      int query = path.Find("?");
      if (query >= 0)
         path = path.Mid(0, query);

      return path == "/api/v1/me/messages" || path == "/api/v1/me/drafts";
   }

   // The page, its script and the headers both are served with are defined in
   // RestApiPortal.cpp - the same literals, moved out of the middle of this
   // file so that the page can be worked on without touching the router, and
   // so that a harness can read the script whole. They are pointers to const
   // char, not const pointers, so they have external linkage and this is a
   // declaration of the same three objects.
   extern const char *PortalHtml;
   extern const char *PortalScript;
   extern const char *PortalHeaders;

   HttpResponse
   RestApiServer::HandlePortalPage_()
   {
      HttpResponse response;
      response.content_type = "text/html; charset=utf-8";
      response.body = PortalHtml;
      response.extra_headers = PortalHeaders;
      return response;
   }

   HttpResponse
   RestApiServer::HandlePortalScript_()
   {
      HttpResponse response;
      response.content_type = "text/javascript; charset=utf-8";
      response.body = PortalScript;
      response.extra_headers = PortalHeaders;
      return response;
   }

   HttpResponse
   RestApiServer::HandleOpenApi_()
   {
      // The description lives here, beside the router it describes, so a route
      // change and its documentation change land in the same diff - a separate
      // file would drift the way every hand-maintained count in this project
      // has. Kept to OpenAPI 3.0 syntax and deliberately terse: the reference
      // for behaviour is the server, and this is the map, not the territory.
      static const char *openApiHead =
         "{"
         "\"openapi\":\"3.0.3\","
         "\"info\":{\"title\":\"hMailServer REST API\",\"version\":\"1\","
         "\"description\":\"Administration API. Authenticate with the administrator password (HTTP Basic, user 'Administrator') or an API key (Bearer). API keys can be read-only or restricted to named domains; key management itself requires the administrator password. The /api/v1/me endpoints are the exception: they answer to an account\'s own credentials (HTTP Basic, user = the mailbox address) and to nothing else, and /portal is a sign-in page for them.\"},"
         "\"paths\":{"
         "\"/api/v1/status\":{\"get\":{\"summary\":\"Server status\",\"responses\":{\"200\":{\"description\":\"Status, state and uptime\"}}}},"
         "\"/api/v1/server/reinitialize\":{\"post\":{\"summary\":\"Restart the services in place\",\"description\":\"What the Control Panel's Reinitialize does: every service is stopped, the configuration reloaded and the services started again in the same process, so that a port, a certificate binding or a setting the document marks as taking effect on restart takes effect now. Answers before it happens, because the REST listener itself restarts: poll GET /api/v1/status until it answers again. Server-wide; refused for domain-restricted and read-only keys.\",\"responses\":{\"202\":{\"description\":\"Reinitialising; the listeners restart in a moment\"}}}},"
         "\"/api/v1/me\":{\"get\":{\"summary\":\"The signed-in account's own state\",\"description\":\"HTTP Basic with the account's address and password - the same credential and the same checks as an IMAP logon, including a per-name lockout and the auto-ban. Refused for the administrator password and for API keys.\",\"responses\":{\"200\":{\"description\":\"address, domain, active, quota (limit_mb, used_bytes), vacation (enabled, active, subject, message, expires, expires_date), password_changed, second_factor, directory_linked\"},\"401\":{\"description\":\"Not an account's credentials\"},\"403\":{\"description\":\"The administrator password or an API key was presented\"}}}},"
         "\"/api/v1/me/password\":{\"post\":{\"summary\":\"Change the signed-in account's password\",\"description\":\"Body: current and new. current has to be the account password itself, not an app password. An account with a second factor sends the code in X-hMailServer-OTP; without it the answer is 401 with X-hMailServer-OTP: required. The password policy and the reuse history apply exactly as when an administrator sets a password.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"current\",\"new\"],\"properties\":{\"current\":{\"type\":\"string\"},\"new\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"Changed\"},\"400\":{\"description\":\"Missing fields, or the policy refused the new password (the reason is in error)\"},\"403\":{\"description\":\"The current password did not match\"},\"409\":{\"description\":\"A directory-linked account, or a recently used password\"}}}},"
         "\"/api/v1/me/vacation\":{\"put\":{\"summary\":\"Set the signed-in account's automatic reply\",\"description\":\"The whole state at once: enabled (required), subject, message, expires and expires_date (YYYY-MM-DD, required when expires is true).\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"enabled\"],\"properties\":{\"enabled\":{\"type\":\"boolean\"},\"subject\":{\"type\":\"string\"},\"message\":{\"type\":\"string\"},\"expires\":{\"type\":\"boolean\"},\"expires_date\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The state as saved\"},\"400\":{\"description\":\"enabled missing, a field over its length, or a malformed expires_date\"}}}},"
         "\"/api/v1/session\":{\"post\":{\"summary\":\"Start a browser session for the signed-in account, or for the administrator\",\"description\":\"HTTP Basic, once: an account's address and password, or the administrator's name and password - and, when a second factor is enrolled on the administrator credential, the one-time code in X-hMailServer-OTP (without it the answer is 401 with X-hMailServer-OTP: required). Answers 201 with a Set-Cookie (hmailsession; HttpOnly, SameSite=Strict, Secure over TLS). The cookie then authenticates as the credential itself would - an account reaches the /api/v1/me endpoints, the administrator reaches everything but them - without a password, for 30 minutes of idleness and 12 hours at most; a request that changes something must also carry X-Requested-With: hMailServer. A password change ends the account's other sessions, and a change of the administrator password ends every administrator session. An API key cannot start one.\",\"responses\":{\"201\":{\"description\":\"address (an account) or administrator true, with idle_seconds and lifetime_seconds; the cookie in Set-Cookie\"},\"401\":{\"description\":\"Not an account's or the administrator's credentials\"},\"403\":{\"description\":\"A session cookie or an API key was presented\"}}},\"delete\":{\"summary\":\"End the browser session the request came with\",\"responses\":{\"200\":{\"description\":\"Ended; the cookie is cleared\"},\"400\":{\"description\":\"The request carried a password, not a session\"}}}},"
         "\"/api/v1/me/quarantine\":{\"get\":{\"summary\":\"The messages held as suspected spam for the signed-in account\",\"description\":\"Only the entries this address is a recipient of, without the other recipients. enabled says whether the server holds spam at all.\",\"responses\":{\"200\":{\"description\":\"enabled, messages (id, sender, subject, reason, score, size, created)\"}}}},"
         "\"/api/v1/me/quarantine/{id}/release\":{\"post\":{\"summary\":\"Deliver a held message to the signed-in account\",\"description\":\"Delivered to this address only; the entry stays for its other recipients and goes when this was the last. A message this address was not sent is 404.\",\"responses\":{\"200\":{\"description\":\"Released\"},\"404\":{\"description\":\"Not held for this account\"}}}},"
         "\"/api/v1/me/quarantine/{id}\":{\"delete\":{\"summary\":\"Give up the signed-in account's copy of a held message\",\"description\":\"This address leaves the entry; the entry and its file go when no recipient is left. Nothing is delivered.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Not held for this account\"}}}},"
         "\"/api/v1/me/folders/{id}/messages\":{\"get\":{\"summary\":\"One folder's messages, newest first\",\"description\":\"Query parameters: limit (1-200, default 200), before_uid (only messages with a lower UID - the way to page back) and q (only messages containing the text, case-insensitively, in Subject, From, To, Cc, the text or the HTML; at most 2000 are looked at per request - scanned says how many, complete whether that was all, and next_before_uid where to continue). Each entry: id, uid, size, received, subject, from, date (decoded from the head of the file, as FETCH ENVELOPE would), flags (seen, flagged, answered, draft, deleted), message_id, in_reply_to, references. total is the folder's count. A folder of another account, or one the ACL keeps from this one, is 404.\",\"responses\":{\"200\":{\"description\":\"folder_id, total, messages\"},\"404\":{\"description\":\"Not this account's folder\"}}}},"
         "\"/api/v1/me/drafts\":{\"post\":{\"summary\":\"Keep a draft in the Drafts folder\",\"description\":\"Body: to, cc, bcc, subject, text, and optionally replace_id - the draft this one supersedes, expunged once the new one is saved (new content is a new message with a new UID, as IMAP requires). The Drafts folder is made as Drafts when the account has none. The draft carries the \\\\Draft and \\\\Seen flags and is read, moved and deleted through the message routes.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"to\":{\"type\":\"string\"},\"cc\":{\"type\":\"string\"},\"bcc\":{\"type\":\"string\"},\"subject\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"},\"replace_id\":{\"type\":\"integer\"}}}}}},\"responses\":{\"201\":{\"description\":\"id, folder_id\"},\"403\":{\"description\":\"The Drafts folder does not allow it\"},\"413\":{\"description\":\"The mailbox is full\"}}}},"
         "\"/api/v1/me/settings\":{\"get\":{\"summary\":\"The signed-in account's own settings\",\"responses\":{\"200\":{\"description\":\"name (first, last), forwarding (enabled, address, keep_original), signature (enabled, text, html)\"}}},\"put\":{\"summary\":\"Change the signed-in account's own settings\",\"description\":\"Each of name, forwarding and signature the body names is applied whole; one it does not name is left as it is. A forwarding that is enabled needs an e-mail address, and not the account's own.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"object\"},\"forwarding\":{\"type\":\"object\"},\"signature\":{\"type\":\"object\"}}}}}},\"responses\":{\"200\":{\"description\":\"The settings as saved\"},\"400\":{\"description\":\"Nothing named, a name or signature too long, or a forwarding address refused\"}}}},"
         "\"/api/v1/me/filters\":{\"get\":{\"summary\":\"The signed-in account's active Sieve script\",\"responses\":{\"200\":{\"description\":\"active (the script, empty when none), name (the active script's name when ManageSieve set one)\"}}},\"put\":{\"summary\":\"Set the signed-in account's active Sieve script\",\"description\":\"Body: script. Checked as ManageSieve's PUTSCRIPT checks it, with the same wording in error; an empty script removes the filter. The script runs on every message that arrives from then on.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"script\"],\"properties\":{\"script\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"active\"},\"400\":{\"description\":\"script missing, over 256 KB, or not parsing (the reason is in error)\"}}}},"
         "\"/api/v1/me/search\":{\"get\":{\"summary\":\"Search every folder of the signed-in account\",\"description\":\"Query parameters: q (required) and limit (1-200, default 50). The same match as q on a folder listing, over every folder the account may read, newest first; at most 2000 messages are looked at per request (scanned, complete), and more says whether hits beyond limit were cut. Each hit names its folder_id and folder path.\",\"responses\":{\"200\":{\"description\":\"query, scanned, complete, more, messages\"},\"400\":{\"description\":\"q missing\"}}}},"
         "\"/api/v1/me/messages\":{\"post\":{\"summary\":\"Send a message as the signed-in account\",\"description\":\"Body: to, cc, bcc (address lists, comma or semicolon separated, display names allowed), subject, text; optionally in_reply_to and references (written as the headers of those names, so the recipient's client threads the reply) and answered_id (the id of the message this answers, which gets \\\\Answered). Every address is put through the checks RCPT TO makes for an authenticated sender, and a refused one is named in error. The message is queued through the same delivery pipeline as SMTP submission, and a copy marked read is kept in the folder designated \\\\Sent when the account has one and its quota allows. attachments is an array of {name, type, data} with data as base64 - at most 20, twelve megabytes together; this route and the drafts route take a request of up to sixteen megabytes.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"to\":{\"type\":\"string\"},\"cc\":{\"type\":\"string\"},\"bcc\":{\"type\":\"string\"},\"subject\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"queued, recipients, sent_id (0 when no copy was kept)\"},\"400\":{\"description\":\"No recipient, or an address refused (named in error)\"},\"413\":{\"description\":\"Larger than the server allows\"}}}},"
         "\"/api/v1/me/messages/{id}\":{\"get\":{\"summary\":\"One message, read\",\"description\":\"The listing's fields plus folder_id, to, cc, text, html and attachments (index, name, size, content_type, content_id). content_type is the media type the part declares, lower-cased and without its parameters, and is the empty string when the part declares none; content_id is the part's Content-ID with the angle brackets stripped - the form a cid: URL in html uses - and is the empty string when the part carries none. An inline image is an attachment here like any other part, so a page renders one by matching a cid: URL in html against content_id and pointing at the attachment route. A message over one megabyte is described with truncated true and no body. Another account's message, or one in a folder the ACL keeps from this account, is 404.\",\"responses\":{\"200\":{\"description\":\"The message\"},\"404\":{\"description\":\"Not this account's message\"}}},\"delete\":{\"summary\":\"Delete one message\",\"description\":\"Moved to the folder designated \\\\Trash when the account has one and the message is not in it already; final otherwise, or with ?permanent=1. The rights EXPUNGE asks for.\",\"responses\":{\"200\":{\"description\":\"deleted true, or deleted false with moved_to and the new id\"},\"403\":{\"description\":\"The folder does not allow it\"},\"404\":{\"description\":\"Not this account's message\"}}}},"
         "\"/api/v1/me/messages/{id}/flags\":{\"put\":{\"summary\":\"Change one message's flags\",\"description\":\"Body: any of seen, flagged, answered, draft, deleted as booleans; only the flags named change. The rights STORE asks for - seen, deleted and the rest are three permissions. Every IMAP session on the folder is told.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"properties\":{\"seen\":{\"type\":\"boolean\"},\"flagged\":{\"type\":\"boolean\"},\"answered\":{\"type\":\"boolean\"},\"draft\":{\"type\":\"boolean\"},\"deleted\":{\"type\":\"boolean\"}}}}}},\"responses\":{\"200\":{\"description\":\"id, folder_id, flags\"},\"400\":{\"description\":\"No flag named\"},\"403\":{\"description\":\"The folder does not allow it\"},\"404\":{\"description\":\"Not this account's message\"}}}},"
         "\"/api/v1/me/messages/{id}/move\":{\"post\":{\"summary\":\"Move one message to another of the account's folders\",\"description\":\"Body: folder_id. As MOVE does: a copy with a new UID in the destination, then the original expunged, every session on either folder told. Another account's folder is 404.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"folder_id\"],\"properties\":{\"folder_id\":{\"type\":\"integer\"}}}}}},\"responses\":{\"200\":{\"description\":\"id (the new one), folder_id\"},\"400\":{\"description\":\"folder_id missing, or the same folder\"},\"403\":{\"description\":\"A folder does not allow it\"},\"404\":{\"description\":\"Not this account's message or folder\"}}}},"
         "\"/api/v1/me/messages/{id}/attachments/{index}\":{\"get\":{\"summary\":\"One attachment, decoded, as a download\",\"description\":\"index is the attachment's position in the message's attachments list. Served under the very media type that listing reports in content_type - so an img element pointed here renders, with nosniff set - except the types a browser would run or render (HTML, SVG, XML, script), which go out as application/octet-stream, and a part declaring no usable type, which does too; with Content-Disposition attachment (the name in both filename and RFC 8187 filename*), nosniff, a sandbox policy and no-store. A message over 32 MB is not parsed.\",\"responses\":{\"200\":{\"description\":\"The attachment's bytes\"},\"404\":{\"description\":\"Not this account's message, or no such attachment\"},\"413\":{\"description\":\"The message is too large to read here\"}}}},"
         "\"/api/v1/domains\":{"
         "\"get\":{\"summary\":\"List domains\",\"description\":\"A domain-restricted key sees only its own domains. Each entry: name, active, postmaster.\",\"responses\":{\"200\":{\"description\":\"Array of domains\"}}},"
         "\"post\":{\"summary\":\"Create a domain\",\"description\":\"Body: name (required), active (default true) and postmaster. The name is judged as the Control Panel judges it - a valid domain name, not one a domain alias already has - and every other setting takes the default a new domain gets there. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\"],\"properties\":{\"name\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"},\"postmaster\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created: name, active, postmaster\"},\"400\":{\"description\":\"name missing, not a domain name, or taken by a domain alias (the reason is in error)\"},\"409\":{\"description\":\"A domain with that name exists\"}}}},"
         "\"/api/v1/domains/{domain}\":{"
         "\"put\":{\"summary\":\"Switch a domain on or off, and set its postmaster\",\"description\":\"Body: active (required) and postmaster (changed only when named). The name cannot be changed here.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"active\"],\"properties\":{\"active\":{\"type\":\"boolean\"},\"postmaster\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"The domain as saved: name, active, postmaster\"},\"400\":{\"description\":\"active missing, or the save refused (the reason is in error)\"},\"404\":{\"description\":\"Unknown domain\"}}},"
         "\"delete\":{\"summary\":\"Delete a domain with everything in it\",\"description\":\"The accounts and their messages, the aliases, the distribution lists, the domain aliases and the domain's directories go with it, exactly as when the Control Panel deletes a domain. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown domain\"}}}},"
         "\"/api/v1/domains/{domain}/accounts\":{"
         "\"get\":{\"summary\":\"List accounts in a domain\",\"responses\":{\"200\":{\"description\":\"Array of accounts\"},\"404\":{\"description\":\"Unknown domain\"}}},"
         "\"post\":{\"summary\":\"Create an account\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"address\",\"password\"],\"properties\":{\"address\":{\"type\":\"string\"},\"password\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"},\"max_size_mb\":{\"type\":\"integer\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created\"},\"400\":{\"description\":\"Malformed request\"},\"404\":{\"description\":\"Unknown domain\"}}}},"
         "\"/api/v1/queue\":{\"get\":{\"summary\":\"List the delivery queue\",\"description\":\"Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of queued messages\"}}}},"
         "\"/api/v1/queue/{id}/retry\":{\"post\":{\"summary\":\"Retry a queued message now\",\"responses\":{\"200\":{\"description\":\"Rescheduled\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/queue/{id}\":{\"delete\":{\"summary\":\"Remove a message from the queue\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/quarantine\":{\"get\":{\"summary\":\"List quarantined messages\",\"description\":\"Server-wide; refused for domain-restricted keys. Bounded to the newest 1000.\",\"responses\":{\"200\":{\"description\":\"Array of quarantined messages\"}}}},"
         "\"/api/v1/quarantine/{id}/release\":{\"post\":{\"summary\":\"Release a quarantined message to its original recipients\",\"description\":\"Delivery is direct rather than back through the filters: a release is an administrator overruling them.\",\"responses\":{\"200\":{\"description\":\"Released\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/quarantine/{id}\":{\"delete\":{\"summary\":\"Delete a quarantined message\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/tlsa\":{\"get\":{\"summary\":\"Recommended DANE TLSA records for the configured certificates\",\"responses\":{\"200\":{\"description\":\"Array of TLSA records\"}}}},"
         "\"/api/v1/srv\":{\"get\":{\"summary\":\"Recommended client-discovery SRV records (RFC 6186/8314 and Outlook autodiscover) for the enabled listeners\",\"description\":\"One record set per active domain; a domain-restricted key sees only its own domains. Only services that are enabled and not loopback-bound are advertised.\",\"responses\":{\"200\":{\"description\":\"Array of SRV records\"}}}},"
         "\"/api/v1/update\":{\"get\":{\"summary\":\"The update check\'s verdict\",\"description\":\"state: 0 not checked since the service started, 1 up to date, 2 a newer release is available, 3 its installer is downloaded and verified, 4 installing, 5 the last check failed (lastError). availableVersion, releaseName, publishedAt, releaseUrl and installer describe the newer release when there is one. Nothing is fetched by this route; the scheduled check (UpdateCheckEnabled) or POST /api/v1/update/check does that.\",\"responses\":{\"200\":{\"description\":\"The verdict\"}}}},"
         "\"/api/v1/update/check\":{\"post\":{\"summary\":\"Read the release feed now\",\"description\":\"Runs the update check on this request, whether or not the scheduled check is on, and returns the verdict as GET /api/v1/update does. Refused for read-only keys.\",\"responses\":{\"200\":{\"description\":\"The verdict after the check; state 5 with lastError when the feed could not be read\"}}}},"
         "\"/api/v1/update/download\":{\"post\":{\"summary\":\"Download and verify the newer release\'s installer\",\"description\":\"Fetches the installer the last check found and its Sigstore bundle into the data directory\'s Updates folder and verifies the installer against the bundle: digest, certificate chain at the time the transparency log recorded the signature, the release workflow\'s identity, the signature, and the log\'s own signature. A file that fails is deleted. Nothing is run. Returns the verdict as GET /api/v1/update does; state 3 when the installer is in place and verified, 5 with lastError when it is not. Refused for read-only keys.\",\"responses\":{\"200\":{\"description\":\"The verdict after the download\"}}}},"
         "\"/api/v1/update/install\":{\"post\":{\"summary\":\"Apply the verified installer\",\"description\":\"Verifies the downloaded installer once more, fetches and verifies the running version\'s installer as the rollback image where the feed offers it, and hands the installer to the update helper, which runs it silently, waits for the service to come back, runs the rollback image if it does not, and writes an outcome the service reports at its next start (apply in GET /api/v1/update). The service stops and starts during the update. Returns state 4 when the helper was started. Refused for read-only keys.\",\"responses\":{\"200\":{\"description\":\"The verdict; state 4 when the helper was started, 5 with lastError when it was not\"}}}},"
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
         "\"/api/v1/domains/{domain}/dkim\":{\"get\":{\"summary\":\"The DKIM signing configuration of a domain\",\"responses\":{\"200\":{\"description\":\"enabled, selector, sign_aliases and the private key file\"},\"404\":{\"description\":\"Unknown domain\"}}}},"
         "\"/api/v1/logs\":{\"get\":{\"summary\":\"List the log files\",\"description\":\"Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of files with size and creation time\"}}}},"
         "\"/api/v1/logs/{name}\":{\"get\":{\"summary\":\"The last lines of a log file\",\"description\":\"Query parameter lines (default 200, at most 2000). The name must be one the list returns; anything with a path in it is refused.\",\"responses\":{\"200\":{\"description\":\"The lines, newest last\"},\"400\":{\"description\":\"Not a log file name\"},\"404\":{\"description\":\"No such log file\"}}}},"
         "\"/api/v1/backup\":{"
         "\"get\":{\"summary\":\"The backup manager's status text and the last lines of the backup log\",\"responses\":{\"200\":{\"description\":\"status (the last failure reason, if any) and log (the backup log's tail, newest last)\"}}},"
         "\"post\":{\"summary\":\"Start a backup with the configured settings\",\"description\":\"Runs on the maintenance queue; poll GET for the outcome.\",\"responses\":{\"202\":{\"description\":\"Started\"},\"409\":{\"description\":\"A backup or restore is already running, or the backup is not configured\"}}}},"
         "\"/api/v1/archive\":{\"get\":{\"summary\":\"Search the archive index\",\"description\":\"Query parameters: domain and mailbox (exact), sender, recipient and subject (contains), since and until (YYYY-MM-DD HH:MM:SS), hold=1 for held copies only, limit (1-1000, default 200). Newest first. A domain-restricted key must name one of its domains.\",\"responses\":{\"200\":{\"description\":\"Array of archive entries: id, time, domain, mailbox, direction, sender, recipients, subject, message_id, path, size, hold\"},\"403\":{\"description\":\"A domain-restricted key without a domain of its own\"}}}},"
         "\"/api/v1/archive/{id}\":{\"get\":{\"summary\":\"One archive entry\",\"responses\":{\"200\":{\"description\":\"The entry\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/archive/{id}/hold\":{"
         "\"post\":{\"summary\":\"Put an archived copy on legal hold\",\"description\":\"A held copy is never removed by the retention sweep or by an address erasure.\",\"responses\":{\"200\":{\"description\":\"Held\"},\"404\":{\"description\":\"Unknown id\"}}},"
         "\"delete\":{\"summary\":\"Lift the hold\",\"responses\":{\"200\":{\"description\":\"Released\"},\"404\":{\"description\":\"Unknown id\"}}}},"
         "\"/api/v1/openapi.json\":{\"get\":{\"summary\":\"This document\",\"responses\":{\"200\":{\"description\":\"The OpenAPI description\"}}}}";

      static const char *openApiTail =
         "},"
         "\"components\":{\"securitySchemes\":{"
         "\"basic\":{\"type\":\"http\",\"scheme\":\"basic\"},"
         "\"bearer\":{\"type\":\"http\",\"scheme\":\"bearer\"}}},"
         "\"security\":[{\"basic\":[]},{\"bearer\":[]}]"
         "}";

      // The write surface's paths come from the translation units that
      // implement them, so a route and its description still land in one
      // diff; each function returns entries that begin with a comma, or
      // nothing.
      AnsiString openApiJson = AnsiString(openApiHead);
      openApiJson += OpenApiSettingsPaths_();
      openApiJson += OpenApiRulesPaths_();
      openApiJson += OpenApiCertificatesPaths_();
      openApiJson += OpenApiRoutesPaths_();
      openApiJson += OpenApiMailboxPaths_();
      openApiJson += openApiTail;

      return BuildResponse_(200, openApiJson);
   }

   HttpResponse
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
            JsonEscape_(Utf8_(name)).c_str(),
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
         appendCertificate(_T("ACME (automatic)"), AcmeClient::GetCertificateDirectory() + FileUtilities::PathSeparator + _T("fullchain.pem"));

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

   HttpResponse
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

   HttpResponse
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

         AnsiString domainName = JsonEscape_(Utf8_(domain->GetName()));

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

      // RFC 8259 section 7: the two-character escapes, and \uXXXX with a
      // surrogate pair for what is outside the BMP. The result stays UTF-8.
      AnsiString result;
      for (int i = valueStart; i < json.GetLength(); i++)
      {
         char character = json[i];

         if (character == '\\' && i + 1 < json.GetLength())
         {
            char next = json[i + 1];
            i++;

            switch (next)
            {
            case '\"': result += '\"'; continue;
            case '\\': result += '\\'; continue;
            case '/':  result += '/';  continue;
            case 'n':  result += '\n'; continue;
            case 'r':  result += '\r'; continue;
            case 't':  result += '\t'; continue;
            case 'b':  result += '\b'; continue;
            case 'f':  result += '\f'; continue;
            case 'u':
               {
                  unsigned int codePoint = 0;
                  if (!ReadJsonHex4_(json, i + 1, codePoint))
                  {
                     result += "\\u";
                     continue;
                  }
                  i += 4;

                  if (codePoint >= 0xD800 && codePoint <= 0xDBFF && i + 6 < json.GetLength() &&
                      json[i + 1] == '\\' && json[i + 2] == 'u')
                  {
                     unsigned int low = 0;
                     if (ReadJsonHex4_(json, i + 3, low) && low >= 0xDC00 && low <= 0xDFFF)
                     {
                        codePoint = 0x10000 + ((codePoint - 0xD800) << 10) + (low - 0xDC00);
                        i += 6;
                     }
                  }

                  AppendUtf8_(result, codePoint);
                  continue;
               }
            default:
               result += character;
               result += next;
               continue;
            }
         }

         if (character == '\"')
            break;

         result += character;
      }

      return result;
   }

   bool
   RestApiServer::ReadJsonHex4_(const AnsiString &json, int at, unsigned int &value)
   {
      if (at + 4 > json.GetLength())
         return false;

      value = 0;
      for (int i = 0; i < 4; i++)
      {
         char c = json[at + i];
         unsigned int digit;
         if (c >= '0' && c <= '9')
            digit = c - '0';
         else if (c >= 'a' && c <= 'f')
            digit = 10 + c - 'a';
         else if (c >= 'A' && c <= 'F')
            digit = 10 + c - 'A';
         else
            return false;

         value = (value << 4) | digit;
      }

      return true;
   }

   void
   RestApiServer::AppendUtf8_(AnsiString &out, unsigned int codePoint)
   {
      if (codePoint < 0x80)
      {
         out += (char) codePoint;
      }
      else if (codePoint < 0x800)
      {
         out += (char) (0xC0 | (codePoint >> 6));
         out += (char) (0x80 | (codePoint & 0x3F));
      }
      else if (codePoint < 0x10000)
      {
         out += (char) (0xE0 | (codePoint >> 12));
         out += (char) (0x80 | ((codePoint >> 6) & 0x3F));
         out += (char) (0x80 | (codePoint & 0x3F));
      }
      else
      {
         out += (char) (0xF0 | (codePoint >> 18));
         out += (char) (0x80 | ((codePoint >> 12) & 0x3F));
         out += (char) (0x80 | ((codePoint >> 6) & 0x3F));
         out += (char) (0x80 | (codePoint & 0x3F));
      }
   }

   HttpResponse
   RestApiServer::HandleUpdateGet_()
   {
      return BuildResponse_(200, UpdateChecker::ToJson(UpdateChecker::Current()));
   }

   HttpResponse
   RestApiServer::HandleUpdateInstall_()
   {
      String error;
      if (!UpdateInstaller::Apply(error))
         UpdateChecker::RecordFailure(error);
      return BuildResponse_(200, UpdateChecker::ToJson(UpdateChecker::Current()));
   }

   HttpResponse
   RestApiServer::HandleUpdateDownload_()
   {
      String error;
      UpdateDownloader::DownloadAndVerify(error);
      return BuildResponse_(200, UpdateChecker::ToJson(UpdateChecker::Current()));
   }

   HttpResponse
   RestApiServer::HandleUpdateCheck_()
   {
      // The check's own outcome is in the verdict it returns (state 5 and
      // lastError when the feed could not be read), so the response is 200
      // either way: the request - run a check - was carried out.
      String error;
      UpdateChecker::CheckNow(error);
      return BuildResponse_(200, UpdateChecker::ToJson(UpdateChecker::Current()));
   }

   AnsiString
   RestApiServer::Utf8_(const String &value)
   {
      AnsiString utf8;
      Unicode::WideToMultiByte(value, utf8);
      return utf8;
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
         case '\n':
            result += "\\n";
            break;
         case '\r':
            result += "\\r";
            break;
         case '\t':
            result += "\\t";
            break;
         default:
            if (static_cast<unsigned char>(character) >= 0x20)
            {
               result += character;
            }
            else
            {
               // RFC 8259 section 7: a control character is written as \u00XX,
               // not dropped - a signature or a script has to come back whole.
               AnsiString escaped;
               escaped.Format("\\u%04x", (unsigned int) (unsigned char) character);
               result += escaped;
            }
            break;
         }
      }

      return result;
   }
}
