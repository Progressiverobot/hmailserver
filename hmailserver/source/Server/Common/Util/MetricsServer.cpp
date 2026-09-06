// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Minimal HTTP endpoint exposing server statistics in the Prometheus
// text exposition format. See MetricsServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "MetricsServer.h"
#include "ServerStatus.h"
#include "AcmeClient.h"
#include "FileUtilities.h"
#include "SecureCompare.h"
#include "Encoding/Base64.h"

#include "../Application/Application.h"
#include "../Threading/WorkQueueManager.h"
#include "../Threading/WorkQueue.h"
#include "../Application/Configuration.h"
#include "../BO/Message.h"
#include "../BO/SSLCertificate.h"
#include "../BO/SSLCertificates.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "../Persistence/PersistentMessage.h"
#include "../TCPIP/SocketConstants.h"
#include "../TCPIP/SslContextInitializer.h"

#include <ws2tcpip.h>

#include <openssl/bio.h>
#include <openssl/err.h>
#include <openssl/pem.h>
#include <openssl/ssl.h>
#include <openssl/x509.h>

#include <algorithm>
#include <chrono>
#include <cstring>
#include <ctime>
#include <utility>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Upper bound on one request's header block. Nothing that scrapes or probes
      // this endpoint sends anything close to it; it exists so an unauthenticated
      // client cannot make the listener buffer without limit.
      const size_t MaxRequestSize = 8192;

      // Per-call socket timeout, unchanged from before this listener had any
      // access control. It bounds one read or one write, which is not the same
      // thing as bounding a request - see the two deadlines below.
      const DWORD SocketTimeoutMilliseconds = 5000;

      // Absolute ceiling on reading one request's headers, across all reads. The
      // per-socket SO_RCVTIMEO bounds a single read only, so without a total
      // deadline a client dribbling one byte at a time just under that timeout
      // would occupy the single listener thread indefinitely.
      const ULONGLONG RequestReadTimeoutMilliseconds = 5000;

      // Absolute ceiling on writing one response, across all writes. Longer than
      // the read deadline because the response is the one part of an exchange that
      // is genuinely large - the exposition grows with every configured
      // certificate and every histogram bucket - and a slow but honest link should
      // still finish. Bounded all the same: the write is a loop (a blocking send
      // may accept fewer bytes than it was handed), and a peer that reads a
      // handful of bytes every few seconds would otherwise keep that loop alive
      // for as long as it liked, denying every other scrape AND every health probe
      // behind it on this single-threaded listener.
      const ULONGLONG ResponseWriteTimeoutMilliseconds = 15000;

      // How long the accept loop pauses after accept() fails for a reason that is
      // not shutdown. Without it the loop retried immediately, so a process out of
      // socket descriptors (WSAEMFILE) or a stack out of buffers (WSAENOBUFS) span
      // this thread at 100% of a core for as long as the shortage lasted - a
      // monitoring thread making a resource shortage worse. It also bounds nothing
      // else: Stop() closes the listen socket, so at most one of these is ever
      // waited out during a service stop.
      const DWORD AcceptRetryPauseMilliseconds = 100;

      // The refresher's tick. Short, because most ticks do nothing at all - each one
      // just asks whether any of the three refresh windows below has elapsed - and
      // because it is also how quickly Stop() gets its thread back when the refresher
      // is idle rather than mid-query.
      const ULONGLONG RefresherTickMilliseconds = 1000;

      // How often the database is asked to prove it can still answer, and the much
      // longer interval used after a probe that failed. Backing off is not politeness:
      // during an outage the failing statement occupies a pooled connection and a
      // thread for as long as DBConnectionAcquireTimeout, so probing hard would take
      // capacity away from the mail paths trying to recover, and the answer is already
      // known - the flag is false and /readyz is already reporting 503.
      const ULONGLONG DatabaseProbeIntervalMilliseconds = 5000;
      const ULONGLONG DatabaseProbeFailureIntervalMilliseconds = 30000;

      // How stale the newest SUCCESSFUL probe may be before readiness stops trusting
      // it. This is the ceiling that covers a probe which neither succeeds nor fails
      // but simply does not come back - GetConnection_ waits up to sixty seconds by
      // default - so without it the last good answer would keep a node in rotation
      // for a minute after the database stopped answering. Four probe intervals, so
      // a single slow round trip cannot trip it.
      const ULONGLONG DatabaseProbeStalenessMilliseconds = 20000;

      // How often the configured certificates are re-read from disk. A notAfter only
      // changes when a certificate is replaced.
      const ULONGLONG CertificateRefreshIntervalMilliseconds = 300000;

      // How often the delivery-queue aggregates are re-read.
      const ULONGLONG DeliveryQueueRefreshIntervalMilliseconds = 10000;

      // Ceiling on the number of hmailserver_tls_certificate_expiry_seconds series.
      // Every other family on this endpoint is a fixed number of lines, so this is
      // the only part of the response whose size is driven by data rather than by
      // code - and the whole response is assembled in memory on the listener thread
      // and then written under a deadline, so an operator with a very large
      // certificate table should not be able to turn one scrape into a slow one. A
      // ceiling well above any plausible configuration, taking the earliest expiries
      // first so that if it ever does bite, what survives is what an operator would
      // have wanted to alert on.
      const size_t MaxCertificateExpirySeries = 128;

      // Strips leading and trailing HTTP optional whitespace (space and horizontal
      // tab, per RFC 7230's OWS rule). Not a general trim: CR and LF have already
      // been removed by the line splitter, and stripping anything else would mean
      // silently altering a credential.
      void TrimOptionalWhitespace(std::string &value)
      {
         size_t first = value.find_first_not_of(" \t");

         if (first == std::string::npos)
         {
            value.clear();
            return;
         }

         size_t last = value.find_last_not_of(" \t");

         value = value.substr(first, last - first + 1);
      }

      // The StateSet layout for hmailserver_state: one series per state, so PromQL
      // selects a state by name instead of by magic number.
      struct StateSeries
      {
         const char *label;
         ServerStatus::ServerState state;
      };

      const StateSeries StateSeriesTable[] =
      {
         { "unknown",  ServerStatus::StateUnknown },
         { "stopped",  ServerStatus::StateStopped },
         { "starting", ServerStatus::StateStarting },
         { "running",  ServerStatus::StateRunning },
         { "stopping", ServerStatus::StateStopping }
      };

      // Converts an hMailServer system timestamp ("YYYY-MM-DD HH:MM:SS") to a Unix
      // timestamp. The column is written in local time by Time::GetCurrentDateTime,
      // so it is interpreted as local time here. Returns 0 for an empty, short or
      // unparseable value, so a NULL column reads as "unknown" rather than as 1970 -
      // which would otherwise show up as a 56-year-old message in the queue.
      __int64 SystemTimeStampToUnixTime(const String &timestamp)
      {
         if (timestamp.GetLength() < 19)
            return 0;

         tm parsed = {};
         parsed.tm_year = _ttoi(timestamp.Mid(0, 4).c_str()) - 1900;
         parsed.tm_mon = _ttoi(timestamp.Mid(5, 2).c_str()) - 1;
         parsed.tm_mday = _ttoi(timestamp.Mid(8, 2).c_str());
         parsed.tm_hour = _ttoi(timestamp.Mid(11, 2).c_str());
         parsed.tm_min = _ttoi(timestamp.Mid(14, 2).c_str());
         parsed.tm_sec = _ttoi(timestamp.Mid(17, 2).c_str());

         // -1 asks the CRT to work out whether daylight saving was in effect, which
         // is what a local-time column needs.
         parsed.tm_isdst = -1;

         if (parsed.tm_year < 70)
            return 0;

         time_t converted = mktime(&parsed);
         if (converted == static_cast<time_t>(-1))
            return 0;

         return static_cast<__int64>(converted);
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
      // "any" and then finds their IPv4 Prometheus refused would have nothing
      // to go on. This listener has exactly one bind-address setting (unlike
      // the mail protocols, which take one port row per address), so :: is the
      // only way to serve both families and is therefore made dual-stack. A
      // specific IPv6 address is left alone: it can only ever accept IPv6.
      // Returns false only when the option was needed and could not be set, so
      // the caller can say IPv4 will not be served rather than leave it to be
      // discovered. Note that :: is not loopback, so rule 2's credential gate
      // applies to it exactly as to 0.0.0.0.
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

   MetricsServer::MetricsServer() :
      listen_socket_(INVALID_SOCKET),
      running_(false),
      active_client_socket_(INVALID_SOCKET),
      start_tick_count_(0),
      start_unix_time_(0),
      refresher_stop_(true),
      database_reachable_(false),
      database_probe_success_tick_(0),
      database_probe_success_unix_time_(0),
      database_probe_tick_(0),
      queue_refresh_requested_(false),
      queue_cache_tick_(0),
      queue_depth_cache_value_(0),
      queue_oldest_age_cache_value_(0),
      certificate_refresh_requested_(false),
      certificate_cache_tick_(0),
      metrics_availability_(MetricsAvailable)
   {

   }

   MetricsServer::~MetricsServer()
   {
      Stop();
   }

   bool
   MetricsServer::Start(const String &bind_address, int port)
   {
      if (running_)
         return true;

      // The bind address is validated first, ahead of everything else, because the
      // rules below are phrased in terms of whether it is a loopback address and a
      // value that is not an address at all has no honest answer to that question.
      // Validating it here means a typo is reported as a typo rather than as
      // "not loopback, and no credential is configured" - true, but it would send
      // the operator looking for the wrong problem.
      AnsiString narrowBindAddress = bind_address;

      sockaddr_storage address = {};
      int addressLength = 0;

      if (!ParseBindAddress(narrowBindAddress, port, address, addressLength))
      {
         LOG_APPLICATION("MetricsServer: Invalid bind address: " + bind_address);
         return false;
      }

      // Credentials and TLS are read here rather than passed in, so the call in
      // Application::Start does not have to change shape every time this listener
      // grows another knob. Everything is worked out into locals first: nothing is
      // written to the members until the socket is actually listening, so a failed
      // bind cannot leave the object holding a secret or an SSL context that no
      // thread will ever use.
      //
      // The token and the user name are trimmed, because a value typed into an ini
      // file picks up trailing spaces and a token nobody can present is
      // indistinguishable from a broken listener. The password is deliberately NOT
      // trimmed: leading and trailing whitespace is legitimate inside a password,
      // and silently removing it would reject a credential the operator set on
      // purpose.
      AnsiString token = IniFileSettings::Instance()->GetMetricsServerAuthToken();
      token.Trim();

      AnsiString basicUsername = IniFileSettings::Instance()->GetMetricsServerAuthUsername();
      basicUsername.Trim();

      AnsiString basicPassword = IniFileSettings::Instance()->GetMetricsServerAuthPassword();

      AnsiString basicCredentials;

      if (!basicUsername.IsEmpty() && !basicPassword.IsEmpty())
      {
         basicCredentials = basicUsername + ":" + basicPassword;
      }
      else if (!basicUsername.IsEmpty() || !basicPassword.IsEmpty())
      {
         // Half a Basic credential is the trap this message exists to stop.
         // Without it, an operator who set only the password would get a listener
         // that behaves exactly as if they had configured nothing - which, on a
         // loopback bind, is silently no authentication at all. The value is never
         // named, only its absence.
         LOG_APPLICATION("MetricsServer: MetricsServerAuthUsername and MetricsServerAuthPassword must both be set for HTTP Basic authentication. Only one of them is set, so Basic authentication is NOT enabled.");
      }

      bool credentialConfigured = !token.IsEmpty() || !basicCredentials.IsEmpty();

      String certificateFile = IniFileSettings::Instance()->GetMetricsServerCertificateFile();
      String privateKeyFile = IniFileSettings::Instance()->GetMetricsServerPrivateKeyFile();

      bool tlsRequested = !certificateFile.IsEmpty() && !privateKeyFile.IsEmpty();

      if (!tlsRequested && (!certificateFile.IsEmpty() || !privateKeyFile.IsEmpty()))
      {
         // Same class of trap as the half-configured Basic credential, and worse in
         // its consequence: half a TLS configuration would otherwise mean plain
         // HTTP on a listener whose operator believes they enabled encryption.
         LOG_APPLICATION("MetricsServer: MetricsServerCertificateFile and MetricsServerPrivateKeyFile must both be set to serve HTTPS. Only one of them is set, so TLS is NOT enabled.");
      }

      bool loopbackOnly = IsLoopbackAddress_(bind_address);

      std::shared_ptr<boost::asio::ssl::context> tlsContext;
      bool tlsFailed = false;

      if (tlsRequested && !InitializeTls_(bind_address, port, certificateFile, privateKeyFile, tlsContext))
         tlsFailed = true;

      // Every condition is logged, not just the one that ends up being reported to
      // clients, because an operator reading the log after an upgrade needs to see
      // everything that is wrong rather than the first thing.
      if (tlsFailed)
      {
         // The specific cause - an unreadable certificate, a private key that does
         // not match it - has already been reported by SslContextInitializer as
         // HM5113. This line says what the listener did about it.
         LOG_APPLICATION("MetricsServer: TLS is configured but could not be prepared. The health probes will still be served, over plain HTTP, but /metrics will answer 503: serving the exposition in the clear instead would publish it - and any credential presented with it - on a listener that was asked to encrypt.");
      }

      if (!loopbackOnly && !credentialConfigured)
      {
         String message;
         message.Format(_T("MetricsServer: /metrics will answer 503 - the listener is bound to %s, which is not a loopback address, and no credential is configured. /metrics publishes delivery-queue depth, session counts, authentication-failure counts and the build and schema versions, so it must not be readable from the network unauthenticated. Set MetricsServerAuthToken, or set both MetricsServerAuthUsername and MetricsServerAuthPassword, or bind to 127.0.0.1 or ::1. /livez, /readyz and /healthz are unaffected and keep answering without authentication."),
            bind_address.c_str());
         LOG_APPLICATION(message);
      }

      if (!loopbackOnly && credentialConfigured && !tlsRequested)
      {
         // A warning and not a refusal, on purpose: see rule 3 in the header. The
         // operator who reads this and decides their pod network is trustworthy is
         // making a defensible call, and taking their monitoring away instead would
         // only teach them to pin the old version.
         String message;
         message.Format(_T("MetricsServer: the listener is bound to %s with a credential but without TLS, so that credential will cross the network in clear text on every scrape and can be replayed by anyone on the path. Set MetricsServerCertificateFile and MetricsServerPrivateKeyFile to encrypt it, or bind to 127.0.0.1 or ::1."),
            bind_address.c_str());
         LOG_APPLICATION(message);
      }

      listen_socket_ = socket(address.ss_family, SOCK_STREAM, IPPROTO_TCP);
      if (listen_socket_ == INVALID_SOCKET)
      {
         ScrubSecret_(basicPassword);
         ScrubSecret_(basicCredentials);
         ScrubSecret_(token);
         return false;
      }

      BOOL reuseAddress = TRUE;
      setsockopt(listen_socket_, SOL_SOCKET, SO_REUSEADDR, (const char*) &reuseAddress, sizeof(reuseAddress));

      if (!TryEnableDualStack(listen_socket_, address))
         LOG_APPLICATION("MetricsServer: IPV6_V6ONLY could not be cleared for the :: bind, so this listener will accept IPv6 connections only. Bind 0.0.0.0 instead if IPv4 is what is needed.");

      // SOMAXCONN and not 5. The backlog is how many connections the kernel holds
      // while this single-threaded loop is busy with one, and a connection that
      // overflows it is REFUSED - which on a health-check port means a load balancer
      // reading "node down" for a node that is fine. A pod is routinely probed by
      // liveness, readiness and startup checks at once with a scrape alongside them,
      // and a VIP with several members adds one probe per member; five is not a lot
      // of headroom for that, and the backlog costs nothing until it is used.
      if (bind(listen_socket_, reinterpret_cast<const sockaddr*>(&address), addressLength) == SOCKET_ERROR ||
          listen(listen_socket_, SOMAXCONN) == SOCKET_ERROR)
      {
         String message;
         message.Format(_T("MetricsServer: Failed to bind to %s."), FormatEndpoint(bind_address, port).c_str());
         LOG_APPLICATION(message);

         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;

         ScrubSecret_(basicPassword);
         ScrubSecret_(basicCredentials);
         ScrubSecret_(token);
         return false;
      }

      // Everything the worker thread reads is published here, before the thread
      // exists, and torn down in Stop() after it has been joined. The worker
      // therefore never observes any of it change, which is why none of it is
      // locked.
      auth_token_ = token;
      auth_basic_credentials_ = basicCredentials;
      tls_context_ = tlsContext;

      metrics_availability_ =
         tlsFailed ? MetricsNeedsTls :
         (!loopbackOnly && !credentialConfigured) ? MetricsNeedsCredential : MetricsAvailable;

      // The locals are scrubbed now that the members hold the values. This is
      // housekeeping rather than a security boundary - see ScrubSecret_ - but it
      // costs nothing and it keeps the number of live copies of a credential in
      // this process to the smallest number that still works.
      ScrubSecret_(basicPassword);
      ScrubSecret_(basicCredentials);
      ScrubSecret_(token);

      start_tick_count_ = GetTickCount64();
      start_unix_time_ = static_cast<__int64>(time(nullptr));

      // The refresher's starting state, set before its thread exists.
      //
      // database_reachable_ starts TRUE and the success tick starts NOW, which is
      // deliberately optimistic for exactly one staleness window. The alternative -
      // starting unready until the first probe returns - would make /readyz answer
      // 503 for the first moments after every service start, and a load balancer
      // that has just been told the node is unready does not necessarily come back
      // and ask again promptly. The optimism is also not blind: this function runs
      // from Application::StartServers, which is reached only after the database is
      // connected and the schema version verified, so at this instant a round trip
      // has effectively just succeeded. The refresher's first tick issues a real one
      // within a second, and from then on nothing is assumed.
      //
      // Both refresh flags start set so the queue depth, the queue age and the
      // certificate expiries are correct on the FIRST scrape rather than one tick
      // later. That is three statements and a handful of file reads once per service
      // start; the on-demand rule below is about not repeating them forever on an
      // installation nothing scrapes.
      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);

         refresher_stop_ = false;
         database_reachable_ = true;
         database_probe_tick_ = 0;
         database_probe_success_tick_ = start_tick_count_;
         database_probe_success_unix_time_ = start_unix_time_;
         queue_refresh_requested_ = true;
         queue_cache_tick_ = 0;
         certificate_refresh_requested_ = true;
         certificate_cache_tick_ = 0;
      }

      running_ = true;

      worker_ = std::thread(&MetricsServer::Run_, this);
      refresher_ = std::thread(&MetricsServer::Run_Refresher_, this);

      // The startup line states the access-control shape in full, because an
      // operator reading the log after an upgrade needs to be able to see at a
      // glance whether this endpoint is protected - and because a listener that is
      // deliberately exposed should say so every time it starts. No credential
      // value appears in it, only which schemes are accepted.
      const wchar_t *transport = tls_context_ ? _T("HTTPS") : _T("plain HTTP");

      const wchar_t *metricsAccess =
         metrics_availability_ == MetricsNeedsTls ? _T("/metrics is closed (503) because TLS could not be prepared") :
         metrics_availability_ == MetricsNeedsCredential ? _T("/metrics is closed (503) because a non-loopback bind requires a credential") :
         !auth_token_.IsEmpty() && !auth_basic_credentials_.IsEmpty() ? _T("/metrics requires a bearer token or HTTP Basic") :
         !auth_token_.IsEmpty() ? _T("/metrics requires a bearer token") :
         !auth_basic_credentials_.IsEmpty() ? _T("/metrics requires HTTP Basic") :
         loopbackOnly ? _T("/metrics is open, loopback bind only") : _T("/metrics is open");

      String message;
      message.Format(_T("MetricsServer: Listening on %s over %s. %s. /livez, /readyz and /healthz are always served without authentication."),
         FormatEndpoint(bind_address, port).c_str(), transport, metricsAccess);
      LOG_APPLICATION(message);

      return true;
   }

   void
   MetricsServer::Stop()
   {
      if (!running_)
         return;

      running_ = false;

      if (listen_socket_ != INVALID_SOCKET)
      {
         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
      }

      // Closing the listen socket only stops NEW connections; it says nothing
      // about the one the worker may be in the middle of. Shut that one down too,
      // so a blocked handshake, read or write fails immediately and the join below
      // completes in milliseconds instead of waiting on a remote peer. The read and
      // write deadlines bound the wait even without this, but "bounded" there means
      // seconds per phase, and a service stop that a client can stretch is a
      // service stop that Windows can time out and turn into a kill.
      //
      // shutdown() and not closesocket(): the worker owns the handle and is the
      // only thing that closes it, and the ordering rule described in the header
      // (cleared under the lock before it is closed) is what guarantees the handle
      // is still open and still ours while this runs.
      {
         std::lock_guard<std::mutex> guard(active_client_mutex_);

         if (active_client_socket_ != INVALID_SOCKET)
            shutdown(active_client_socket_, SD_BOTH);
      }

      // The refresher is asked to stop before either thread is joined, so the two
      // waits overlap instead of running one after the other.
      //
      // A refresher that is idle - which is what it is on all but one tick in five -
      // returns immediately, because it is parked in a timed wait on this condition
      // variable rather than in a Sleep. One that is mid-query does not, and cannot
      // be made to: the statement is inside DatabaseConnectionManager and there is no
      // way to cancel it, so this join is bounded by [Settings]
      // DBConnectionAcquireTimeout plus whatever the statement itself takes. That is
      // the same bound a scrape in flight already imposed on this Stop() before any
      // of this moved off the listener thread, so nothing here made a service stop
      // slower than it was; what changed is that the bound is now paid by a thread
      // nobody is waiting on for an answer.
      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);
         refresher_stop_ = true;
      }

      refresher_wakeup_.notify_all();

      if (worker_.joinable())
         worker_.join();

      if (refresher_.joinable())
         refresher_.join();

      // After the join, never before: the worker reads the context for every
      // handshake and the credentials for every scrape, so releasing either while
      // that thread was alive would be a use-after-free. Dropped rather than kept
      // so a Start() after a configuration change picks up the current
      // certificate, TLS settings and credentials.
      tls_context_.reset();
      metrics_availability_ = MetricsAvailable;

      ScrubSecret_(auth_token_);
      ScrubSecret_(auth_basic_credentials_);
   }

   void
   MetricsServer::ScrubSecret_(AnsiString &secret)
   {
      // Overwrites the bytes before releasing them, rather than only emptying the
      // string. Assigning "" or calling Empty() sets the length to zero and leaves
      // the characters sitting in the buffer, where a minidump - and this build
      // writes minidumps on a crash by design - would hand them to whoever reads
      // it. SecureZeroMemory rather than memset because the compiler is entitled
      // to delete a memset over storage nothing reads afterwards, which is exactly
      // this case.
      //
      // What this does NOT claim: that the credential is gone from the process.
      // IniFileSettings holds the configured value for the lifetime of the service
      // - that is the whole point of caching ini settings - and hMailServer.ini
      // holds it on disk. Scrubbing here buys two specific things: a STOPPED
      // listener is not still holding a live copy, and the intermediate copies
      // Start() makes while assembling "username:password" do not outlive the call
      // that made them. Fixing the rest means deciding how every secret in the ini
      // file is held, which is a change to IniFileSettings and not to this file.
      if (!secret.empty())
         SecureZeroMemory(&secret[0], secret.size());

      secret.Empty();
   }

   bool
   MetricsServer::IsLoopbackAddress_(const String &bind_address)
   {
      AnsiString narrowBindAddress = bind_address;

      // A colon can only mean an IPv6 literal - the same family test the bind
      // path uses, so this function cannot classify an address the listener
      // parsed as the other family.
      if (narrowBindAddress.Find(":") >= 0)
      {
         in6_addr parsed6 = {};

         if (inet_pton(AF_INET6, narrowBindAddress.c_str(), &parsed6) != 1)
         {
            // Not a literal; fails closed, as below.
            return false;
         }

         // ::1 and nothing wider: IPv6 has no loopback /8, and every other v6
         // address - including an IPv4-mapped ::ffff:127.0.0.1, which this
         // listener's bind path cannot produce a listener for anyway - stays
         // "not loopback" so the credential gate errs toward demanding one.
         return IN6_IS_ADDR_LOOPBACK(&parsed6) != 0;
      }

      in_addr parsed = {};

      if (inet_pton(AF_INET, narrowBindAddress.c_str(), &parsed) != 1)
      {
         // Not an IPv4 literal, which Start() has already refused before calling
         // this - so unreachable today. It answers "not loopback" rather than
         // "loopback" so that if that ordering is ever changed, the credential gate
         // fails closed instead of treating an unparseable address as safe.
         return false;
      }

      // The whole of 127.0.0.0/8, not just 127.0.0.1. An operator who binds
      // 127.0.0.2 has bound an address only this machine can reach, and demanding a
      // credential there would be a false positive. Note that "localhost" is not
      // accepted here because Start() does not accept it either (inet_pton takes
      // literals only), so treating it as loopback would be dead code claiming to
      // handle a case that cannot arrive.
      return (ntohl(parsed.s_addr) >> 24) == 127u;
   }

   bool
   MetricsServer::InitializeTls_(const String &bind_address, int port,
      const String &certificate_file, const String &private_key_file,
      std::shared_ptr<boost::asio::ssl::context> &context)
   {
      // Why this goes through SslContextInitializer rather than calling
      // SSL_CTX_new: that function is the single place SMTP, POP3 and IMAP get
      // their TLS configuration from - the cipher list, the enabled protocol
      // versions, the server-preference and ChaCha options, the DH parameters, and
      // the key-exchange group list, which since August 2026 carries the hybrid
      // post-quantum KEMs from [Settings] TlsKeyExchangeGroups. A listener that
      // builds its own context restates all of that and then diverges from it the
      // next time it changes, silently, towards the weaker end. That is not
      // hypothetical in this tree: RestApiServer and WebServicesServer each call
      // SSL_CTX_new directly, and neither of them has the group list or the
      // version toggles at all.
      //
      // The bridge between the two stacks is as thin as it can be.
      // SslContextInitializer takes a boost::asio::ssl::context, so one is built
      // here and handed to it, and the raw SSL_CTX it wraps (native_handle) is what
      // this listener's blocking sockets pass to SSL_new. Nothing about the
      // configuration is restated locally, so nothing local can drift. sslv23 is
      // the same method TCPServer constructs its context with, deliberately: the
      // usable protocol versions come from the option mask InitServer applies, and
      // choosing a different method here would silently change which versions the
      // mask is applied to.
      //
      // The certificate is this listener's own setting rather than one borrowed
      // from a mail port, for a reason specific to what does the scraping. A mail
      // client reaches the mail host name, so the mail certificate is the one it
      // already trusts. A Prometheus server reaches this endpoint by container
      // name, service name or bare IP, so the mail certificate's subject would not
      // match and the scrape would need verification turned off anyway - at which
      // point borrowing it has bought nothing and has tied the monitoring port's
      // availability to the mail certificate's renewal. An explicit pair, exactly
      // like RestApiCertificateFile, is the honest shape.
      //
      // This is the small correct thing, not the finished thing: the listener is
      // still its own std::thread accept loop with blocking reads, so it does not
      // get the shared listener's session accounting, its OnAccept scripting event
      // or its asynchronous timeouts. Re-hosting it on the Boost.Asio stack, which
      // would delete this function, is Phase 1 work.
      try
      {
         auto certificate = std::shared_ptr<SSLCertificate>(new SSLCertificate());

         certificate->SetName(_T("MetricsServer"));
         certificate->SetCertificateFile(certificate_file);
         certificate->SetPrivateKeyFile(private_key_file);

         auto built = std::shared_ptr<boost::asio::ssl::context>(
            new boost::asio::ssl::context(boost::asio::ssl::context::sslv23));
         built->set_options(HM_TLS_CONTEXT_FLOOR);

         if (!SslContextInitializer::InitServer(*built, certificate, bind_address, port))
            return false;

         // The one thing deliberately not taken from the shared configuration. The
         // shared option mask is driven by the [Settings] TLS version toggles,
         // which an administrator may well have opened up to TLS 1.0 for an ancient
         // mail client. That argument does not extend to a monitoring endpoint -
         // there is no 2008-era Prometheus - so the floor stays at 1.2, applied
         // after InitServer so it can only tighten what the shared configuration
         // allows, never loosen it.
         SSL_CTX_set_min_proto_version(built->native_handle(), TLS1_2_VERSION);

         context = built;

         return true;
      }
      catch (...)
      {
         // Constructing the context, or reading the configuration, can throw. This
         // runs on the startup path, where an escape would take the whole service
         // down before a single listener is up, so it is contained here and the
         // caller turns it into a closed /metrics.
         return false;
      }
   }

   void
   MetricsServer::Run_()
   {
      for (;;)
      {
         SOCKET clientSocket = accept(listen_socket_, nullptr, nullptr);

         if (clientSocket == INVALID_SOCKET)
         {
            // The listen socket was closed (shutdown) or an error occurred.
            if (!running_)
               return;

            // Anything else is a transient accept() failure that leaves the listen
            // socket usable - the process is out of descriptors, the stack is out of
            // buffers, or the peer reset between the SYN and the accept - so the loop
            // retries. It pauses first, because without the pause it retried
            // immediately: for as long as the condition lasted this thread span at
            // 100% of a core, so a resource shortage anywhere in the process became a
            // monitoring thread competing with the mail threads that were trying to
            // recover from it.
            Sleep(AcceptRetryPauseMilliseconds);

            continue;
         }

         Connection connection(clientSocket);

         // Published for Stop() before anything can block on it, and withdrawn
         // below before it is closed. See the header for why that order is the
         // whole safety argument.
         {
            std::lock_guard<std::mutex> guard(active_client_mutex_);
            active_client_socket_ = clientSocket;
         }

         // Set before anything is read, which since TLS arrived means before the
         // handshake as well as before the request. SSL_accept on a blocking socket
         // inherits SO_RCVTIMEO, so a client that opens a connection and then says
         // nothing - a port scanner, a load balancer probing the port, a plain-HTTP
         // request to an HTTPS listener - is bounded instead of holding this
         // single-threaded accept loop open. A client that keeps dribbling
         // handshake bytes just under the timeout can still stretch the handshake
         // beyond it, which is the one phase here with no absolute deadline: OpenSSL
         // gives no way to bound a blocking SSL_accept without driving it
         // non-blocking through select(), and Stop()'s shutdown() above is what
         // makes that acceptable, because it means such a client can delay this
         // listener but not the service stop.
         DWORD timeout = SocketTimeoutMilliseconds;
         setsockopt(connection.socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
         setsockopt(connection.socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

         // This function is the top of a std::thread. There is nothing above it to
         // catch anything, so an exception that escaped here would be
         // std::terminate() - the entire mail server would die because a monitoring
         // scrape went wrong. Everything the handler touches is fenced off behind
         // this barrier: the database reads behind /metrics and /readyz, the
         // certificate files, and the string building. The connection is
         // deliberately closed out here rather than inside the handler, so the
         // barrier firing cannot leak a socket handle or an SSL object either.
         //
         // The catch blocks below only record what happened; they do not report it.
         // Reporting has to wait until after the socket is closed and has to be
         // fenced off in a barrier of its own, because ErrorManager::ReportError
         // reaches ScriptServer::FireEvent and so runs operator-supplied script. An
         // exception out of that script would otherwise escape this function - the
         // std::terminate() this barrier exists to prevent - and would skip the
         // close below, leaking a socket handle per failed scrape.
         bool exceptionEscaped = false;
         bool exceptionWasStandard = false;
         AnsiString exceptionText;

         try
         {
            HandleClient_(connection);
         }
         catch (const std::exception &error)
         {
            exceptionEscaped = true;
            exceptionWasStandard = true;

            // Copying the text allocates, and the exception in hand may well be the
            // bad_alloc that says allocation is failing. A throw here would escape
            // the catch block itself, so it gets its own fence; the message is then
            // simply omitted.
            try
            {
               exceptionText = error.what();
            }
            catch (...)
            {
            }
         }
         catch (...)
         {
            exceptionEscaped = true;
         }

         // Withdrawn from Stop()'s reach BEFORE it is closed, under the lock. If
         // these two were the other way round, Stop() could take the lock in the
         // window between the close and the clear and call shutdown() on a handle
         // Winsock may already have reissued to an entirely different socket
         // somewhere else in the process.
         {
            std::lock_guard<std::mutex> guard(active_client_mutex_);
            active_client_socket_ = INVALID_SOCKET;
         }

         CloseConnection_(connection);

         if (exceptionEscaped)
         {
            try
            {
               if (exceptionWasStandard)
               {
                  String description = _T("An exception escaped while serving a metrics/health request. The request was abandoned; the listener is still running.");

                  if (!exceptionText.IsEmpty())
                     description = Formatter::Format(_T("{0}, Message: {1}"), description, exceptionText);

                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5750, "MetricsServer::Run_", description);
               }
               else
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5750, "MetricsServer::Run_",
                     "A non-standard exception escaped while serving a metrics/health request. The request was abandoned; the listener is still running.");
               }
            }
            catch (...)
            {
               // Nothing left to do: reporting is what failed, and the alternative
               // to swallowing this is std::terminate() on the mail server.
            }
         }
      }
   }

   void
   MetricsServer::HandleClient_(Connection &connection)
   {
      // The handshake comes first and its failure is silent: nothing is written
      // back to a client whose ClientHello we could not complete. In particular
      // there is no fall back to plain HTTP, which would hand the whole exposition
      // - and the credential the client was about to present - to anyone who simply
      // spoke HTTP to a listener configured for HTTPS.
      if (tls_context_ && !StartTls_(connection))
         return;

      std::string headerBlock;

      if (!ReadRequest_(connection, headerBlock))
         return;

      std::vector<std::string> lines;
      SplitHeaderLines_(headerBlock, lines);

      // Whole paths rather than prefixes. The old StartsWith("GET /metrics") also
      // matched "GET /metricsanything", which was harmless while nothing on this
      // listener was protected and is not a habit worth keeping now that one branch
      // of this dispatch sits behind a credential and three deliberately do not.
      //
      // Matched case-insensitively, which is not what RFC 7230 says about paths but
      // is exactly what StartsWith did here before - StartsWith in this string class
      // folds case - and the point of this change is to stop matching too much, not
      // to start rejecting a request shape that used to work. It costs nothing in
      // safety: every spelling of /metrics lands in the same branch and so passes
      // through the same check.
      std::string path = GetRequestPath_(lines);
      AnsiString response;

      // The three probes are answered FIRST, before anything that could refuse.
      // The ordering is deliberate and is the invariant this whole class is built
      // around: a probe has nowhere to keep a credential, so no configuration and
      // no future edit to the branches below may put a refusal in front of them.
      if (_stricmp(path.c_str(), "/livez") == 0)
      {
         // Liveness: if we got here, the process and the listener thread are
         // responsive. No dependency checks (so a database outage does not cause
         // an orchestrator to kill an otherwise-healthy process).
         response = BuildHttpResponse_(200, "text/plain; charset=utf-8", "alive\n");
      }
      else if (_stricmp(path.c_str(), "/readyz") == 0)
      {
         // Readiness: the server is running and the database is connected.
         AnsiString reason;
         bool ready = IsReady_(reason);
         response = BuildHttpResponse_(ready ? 200 : 503, "text/plain; charset=utf-8",
            ready ? AnsiString("ready\n") : ("not ready: " + reason + "\n"));
      }
      else if (_stricmp(path.c_str(), "/healthz") == 0)
      {
         bool healthy = false;
         AnsiString body = BuildHealthBody_(healthy);
         response = BuildHttpResponse_(healthy ? 200 : 503, "application/json; charset=utf-8", body);
      }
      else if (_stricmp(path.c_str(), "/metrics") == 0)
      {
         if (metrics_availability_ != MetricsAvailable)
         {
            // Not counted in hmailserver_metrics_unauthorized_requests_total: this
            // is not a rejected credential, and counting it would be pointless
            // anyway, because the only way to read that counter is the endpoint
            // that is closed. The reason was written to the application log once,
            // at start, where the operator can act on it.
            response = BuildMetricsUnavailableResponse_();
         }
         else if (!IsRequestAuthorized_(lines))
         {
            // Counted rather than logged. A metrics endpoint reachable from a
            // network gets scanned, and one log line per attempt would fill the
            // application log with something nobody can act on individually; a
            // counter turns the same information into a rate an operator can alert
            // on. Nothing about what was presented is recorded anywhere.
            ServerStatus::Instance()->OnMetricsRequestUnauthorized();

            response = BuildUnauthorizedResponse_();
         }
         else
         {
            response = BuildHttpResponse_(200, "text/plain; version=0.0.4; charset=utf-8", BuildMetricsBody_());
         }
      }
      else
      {
         response = "HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
      }

      Send_(connection, response);
   }

   void
   MetricsServer::CloseConnection_(Connection &connection)
   {
      if (connection.tls_session != nullptr)
      {
         // One attempt at close_notify, not a loop: this also runs on the path
         // where the exception barrier has just fired, and a peer that is not
         // reading must not be able to hold the accept loop here. The send timeout
         // bounds it, and Stop() can cut it short.
         SSL_shutdown(connection.tls_session);
         SSL_free(connection.tls_session);
         connection.tls_session = nullptr;
      }

      if (connection.socket != INVALID_SOCKET)
      {
         // SD_SEND, not SD_BOTH: this half-close is what tells the client the
         // response is complete, and shutting the receive direction down as well
         // would risk a reset that discarded a response the client had not finished
         // reading.
         shutdown(connection.socket, SD_SEND);
         closesocket(connection.socket);
         connection.socket = INVALID_SOCKET;
      }
   }

   bool
   MetricsServer::StartTls_(Connection &connection) const
   {
      // native_handle() is the SSL_CTX SslContextInitializer configured. Taking it
      // rather than making one is what makes the session below inherit the shared
      // cipher list, TLS version toggles and key-exchange groups without this file
      // naming any of them.
      SSL *session = SSL_new(tls_context_->native_handle());

      if (session == nullptr)
      {
         ERR_clear_error();
         return false;
      }

      SSL_set_fd(session, static_cast<int>(connection.socket));

      if (SSL_accept(session) != 1)
      {
         // Deliberately not counted anywhere. A failed handshake here is
         // client-driven - a plain HTTP request to an HTTPS port, a TLS version
         // below the floor, a port scanner - and feeding it into
         // hmailserver_tls_handshake_failures_total would make monitoring traffic
         // move the number that describes TLS on the MAIL protocols, so a scanner
         // knocking on the metrics port would page somebody about SMTP. It gets no
         // counter of its own either: a scraper that cannot handshake is already
         // reported by Prometheus's own synthetic up == 0 for that target, which is
         // a better signal than anything this endpoint could say about its own
         // unreachability.
         //
         // Clear this thread's OpenSSL error queue. It is shared with every other
         // OpenSSL call on this thread - including the certificate parsing behind
         // hmailserver_tls_certificate_expiry_seconds - so a failure left behind
         // here would later be reported against something unrelated.
         ERR_clear_error();

         SSL_free(session);
         return false;
      }

      connection.tls_session = session;

      return true;
   }

   int
   MetricsServer::Receive_(Connection &connection, char *buffer, int size)
   {
      if (connection.tls_session != nullptr)
         return SSL_read(connection.tls_session, buffer, size);

      return recv(connection.socket, buffer, size, 0);
   }

   void
   MetricsServer::Send_(Connection &connection, const AnsiString &data)
   {
      // Written in a loop, not in one call. A blocking send() is permitted to
      // accept fewer bytes than it was handed, and this response is several
      // kilobytes and grows with every configured certificate and every histogram
      // bucket - so a short write would truncate the exposition mid-line, which
      // Prometheus reports as a parse failure against the ENTIRE scrape rather than
      // as one missing metric. The single-call send this replaces was safe only for
      // as long as the body stayed small and the link stayed fast.
      //
      // The loop needs its own absolute deadline, and this is the reason: the
      // socket send timeout bounds one call, so a peer that accepts a few bytes and
      // then stalls, over and over, keeps making just enough progress to reset that
      // timeout forever. On a single-threaded accept loop that is a denial of every
      // other scrape and, worse, of every health probe behind it. A write that runs
      // out of budget is abandoned: there is nothing useful left to say to a client
      // that has stopped reading, and the connection is closed either way.
      const int total = data.GetLength();
      int sent = 0;

      const ULONGLONG deadline = GetTickCount64() + ResponseWriteTimeoutMilliseconds;

      while (sent < total)
      {
         if (GetTickCount64() >= deadline)
            return;

         int written = connection.tls_session != nullptr
            ? SSL_write(connection.tls_session, data.c_str() + sent, total - sent)
            : send(connection.socket, data.c_str() + sent, total - sent, 0);

         if (written <= 0)
            return;

         sent += written;
      }
   }

   bool
   MetricsServer::ReadRequest_(Connection &connection, std::string &header_block)
   {
      // The listener used to take whatever one recv() returned and parse that. For
      // a request line alone that was almost always enough; for a request line plus
      // an Authorization header it is not, because nothing stops the header from
      // arriving in a second TCP segment - and a scrape that got a 401 for that
      // reason would be an intermittent failure with no explanation anywhere. So
      // the headers are read until the terminating blank line, bounded in both size
      // and total time.
      //
      // Two properties of this function are load bearing, and both of them were
      // wrong in the first attempt at it.
      //
      // First, it returns the HEADER BLOCK ONLY. Reading until the blank line and
      // then handing the whole buffer to the parser meant an Authorization header
      // placed AFTER the blank line - in what is, by definition, the body - was
      // found and honoured. The body of a request is not part of its metadata and
      // must never be searched for credentials, so the buffer is truncated at the
      // terminator here, once, rather than left to every caller to remember.
      //
      // Second, a request that is NOT terminated by a blank line is still served.
      // The old code answered anything that arrived in one recv, terminated or not,
      // and probes rely on that: "GET /livez\n\n" from a shell one-liner, an LF-only
      // client, a health checker that half-closes after writing. Returning false for
      // those - which is what an implementation that insists on CRLFCRLF does - shows
      // up as a silent close, which a load balancer reads as a failed health check
      // and a dead node. So false here means one thing only: nothing at all arrived.
      std::string data;
      char buffer[4096];

      const ULONGLONG deadline = GetTickCount64() + RequestReadTimeoutMilliseconds;

      size_t blockEnd = std::string::npos;

      while (data.size() < MaxRequestSize)
      {
         if (GetTickCount64() >= deadline)
            break;

         int bytesRead = Receive_(connection, buffer, static_cast<int>(sizeof(buffer)));
         if (bytesRead <= 0)
            break;

         data.append(buffer, static_cast<size_t>(bytesRead));

         // The request body, if any, is deliberately never read. Every endpoint
         // here is a GET, so a body would be ignored in any case, and reading one
         // would only give an unauthenticated client a way to make us buffer.
         blockEnd = FindHeaderBlockEnd_(data);
         if (blockEnd != std::string::npos)
            break;
      }

      if (data.empty())
         return false;

      header_block = blockEnd == std::string::npos ? data : data.substr(0, blockEnd);

      return true;
   }

   size_t
   MetricsServer::FindHeaderBlockEnd_(const std::string &data)
   {
      // Bare LF counts as a line ending, not only CRLF. RFC 7230 says a recipient
      // MAY accept it and this listener always did, by accident, because it never
      // looked for a terminator at all; probes and hand-typed requests depend on it
      // often enough that tightening it here would be a regression dressed up as
      // correctness.
      size_t lineStart = 0;

      for (size_t i = 0; i < data.size(); i++)
      {
         if (data[i] != '\n')
            continue;

         size_t lineEnd = i;

         if (lineEnd > lineStart && data[lineEnd - 1] == '\r')
            lineEnd--;

         // An empty line ends the header block. The lineStart != 0 guard tolerates
         // a leading empty line, which RFC 7230 tells a server to ignore rather
         // than treat as an empty request with no headers.
         if (lineEnd == lineStart && lineStart != 0)
            return i + 1;

         lineStart = i + 1;
      }

      return std::string::npos;
   }

   void
   MetricsServer::SplitHeaderLines_(const std::string &header_block, std::vector<std::string> &lines)
   {
      size_t lineStart = 0;

      for (;;)
      {
         size_t newline = header_block.find('\n', lineStart);
         size_t lineEnd = newline == std::string::npos ? header_block.size() : newline;

         size_t contentEnd = lineEnd;

         if (contentEnd > lineStart && header_block[contentEnd - 1] == '\r')
            contentEnd--;

         lines.push_back(header_block.substr(lineStart, contentEnd - lineStart));

         if (newline == std::string::npos)
            return;

         lineStart = newline + 1;
      }
   }

   std::string
   MetricsServer::GetRequestPath_(const std::vector<std::string> &lines)
   {
      // Only GET is served, and only the origin form of the request target that a
      // scraper or a probe actually sends. An absolute-form target
      // ("GET http://host/metrics") is not recognised, exactly as before this
      // listener understood paths at all - nothing that scrapes it emits one, and
      // accepting a form nobody sends would mean two spellings of the protected
      // path where one is enough.
      //
      // The method is matched case-insensitively even though RFC 7230 makes methods
      // case-sensitive, for the same reason the path is: StartsWith folded case
      // here before, so "get /livez" used to work, and nothing is gained by
      // breaking it.
      if (lines.empty())
         return "";

      const std::string &requestLine = lines[0];

      if (requestLine.size() < 5 || _strnicmp(requestLine.c_str(), "GET ", 4) != 0)
         return "";

      const size_t pathStart = 4;

      size_t pathEnd = requestLine.find(' ', pathStart);
      if (pathEnd == std::string::npos)
         pathEnd = requestLine.size();

      if (pathEnd <= pathStart)
         return "";

      std::string path = requestLine.substr(pathStart, pathEnd - pathStart);

      // A query string is stripped rather than made part of the path, so that
      // "/metrics?collect[]=x" is the metrics endpoint - and therefore is behind the
      // credential - rather than an unknown path that 404s.
      size_t query = path.find('?');
      if (query != std::string::npos)
         path = path.substr(0, query);

      return path;
   }

   std::string
   MetricsServer::GetHeaderValue_(const std::vector<std::string> &lines, const char *header_name)
   {
      // Header names are case-insensitive in HTTP and clients differ
      // ("Authorization" from Prometheus, "authorization" from anything speaking
      // HTTP/2 through a proxy), so the match has to be too.
      //
      // Matched as a whole field name against the bytes before the colon, rather
      // than by searching the request for the name: a search would find the name
      // inside another header's value, and - before the header block was separated
      // from the body - inside the body as well. The field name has to fill those
      // bytes exactly, with no space before the colon, which is what RFC 7230
      // requires and what a request smuggling attempt gets wrong.
      //
      // Obsolete line folding (a continuation line beginning with a space) is not
      // reassembled. Nothing that scrapes this emits it, RFC 7230 deprecates it,
      // and the failure direction is safe: a folded credential parses as a header
      // with no colon, is skipped, and the request is refused.
      //
      // The first matching header wins. A duplicate Authorization header is
      // malformed, and picking the first means what an operator's proxy prepends is
      // what gets checked, rather than whatever a client appended after it.
      size_t nameLength = strlen(header_name);

      for (size_t i = 1; i < lines.size(); i++)
      {
         const std::string &line = lines[i];

         size_t colon = line.find(':');
         if (colon == std::string::npos)
            continue;

         if (colon != nameLength || _strnicmp(line.c_str(), header_name, nameLength) != 0)
            continue;

         std::string value = line.substr(colon + 1);
         TrimOptionalWhitespace(value);

         return value;
      }

      return "";
   }

   bool
   MetricsServer::IsRequestAuthorized_(const std::vector<std::string> &lines) const
   {
      // No credential configured means no authentication, which is what a loopback
      // scrape has always had and has to keep having: this is the branch every
      // existing installation and every regression fixture takes. Start() is what
      // guarantees that a non-loopback bind cannot reach this branch - it closes
      // /metrics outright instead, before authorization is ever consulted.
      if (auth_token_.IsEmpty() && auth_basic_credentials_.IsEmpty())
         return true;

      std::string headerValue = GetHeaderValue_(lines, "authorization");
      if (headerValue.empty())
         return false;

      if (headerValue.size() >= 7 && _strnicmp(headerValue.c_str(), "bearer ", 7) == 0)
      {
         if (auth_token_.IsEmpty())
            return false;

         std::string presented = headerValue.substr(7);
         TrimOptionalWhitespace(presented);

         return SecureEquals(presented, auth_token_);
      }

      if (headerValue.size() >= 6 && _strnicmp(headerValue.c_str(), "basic ", 6) == 0)
      {
         if (auth_basic_credentials_.IsEmpty())
            return false;

         std::string encoded = headerValue.substr(6);
         TrimOptionalWhitespace(encoded);

         // Refused before the decoder sees it, rather than relying on what the
         // base64 decoder does with a zero-length input.
         if (encoded.empty())
            return false;

         // The decoder stops at the first character outside the base64 alphabet and
         // returns what it had, so junk decodes to something short and the
         // comparison below simply fails. No input validation is needed in front of
         // it, and none is added: the same decoder is on the REST API's Basic path
         // already.
         AnsiString presented = Base64::Decode(encoded.c_str(), static_cast<int>(encoded.size()));

         // One comparison over "username:password" as a whole. Comparing the two
         // halves separately would leak, through the time taken, whether the user
         // name was right - which is the more guessable half and the one an attacker
         // would pin down first.
         //
         // Both sides are bytes, and the expected side was narrowed from the ini
         // file's UTF-16 through the system code page, while the presented side is
         // whatever the client base64-encoded (in practice UTF-8). So a credential
         // outside ASCII can compare unequal even when it looks identical. That is
         // worth knowing rather than worth fixing here: it is the same narrowing the
         // administrator password takes on the REST API, fixing it properly means
         // deciding an encoding for every secret in the ini file, and the credential
         // this listener needs is a machine-generated token that has no business
         // containing anything but ASCII.
         //
         // Note what is deliberately NOT scrubbed here: this decoded copy, the
         // base64 still in "encoded", the header value, the line in "lines" and the
         // raw bytes in ReadRequest_'s buffer. All five hold the presented
         // credential and all five are freed within microseconds. Scrubbing one of
         // them would be theatre - it would read as though the presented credential
         // were being contained when four copies of it were not - and containing all
         // five means a custom allocator for the request path, not a scrub call.
         // ScrubSecret_ is applied where it actually changes something: the
         // CONFIGURED credential, which lives for as long as the listener does.
         return SecureEquals(presented, auth_basic_credentials_);
      }

      // An unrecognised scheme, which also catches a bare "Bearer" or "Basic" with
      // nothing after it - the header value is trimmed, so those are too short to
      // match either prefix. Refused, not ignored: a client configured with a
      // scheme this listener does not accept must not be able to read the
      // exposition by presenting one, and a credential that is present but unusable
      // belongs on the counted failure path rather than being waved through as
      // "absent".
      return false;
   }

   AnsiString
   MetricsServer::BuildUnauthorizedResponse_() const
   {
      // RFC 7235 permits more than one challenge, and which ones are advertised
      // follows what is actually configured. Advertising Basic when only a bearer
      // token exists would make a browser prompt for a password that can never
      // work; advertising Bearer when only Basic exists would tell a client to stop
      // sending the credential that would have worked.
      AnsiString headers;

      if (!auth_token_.IsEmpty())
         headers += "WWW-Authenticate: Bearer realm=\"hMailServer metrics\"\r\n";

      if (!auth_basic_credentials_.IsEmpty())
         headers += "WWW-Authenticate: Basic realm=\"hMailServer metrics\"\r\n";

      // The body says nothing about which scheme was tried, whether the credential
      // was absent or merely wrong, or how close it came.
      return BuildHttpResponse_(401, "text/plain; charset=utf-8", "unauthorized\n", headers);
   }

   AnsiString
   MetricsServer::BuildMetricsUnavailableResponse_() const
   {
      // 503 and not 401: there is no credential that would work in either of these
      // configurations, so a challenge would invite a client to guess at one that
      // does not exist. 503 is also what a scraper already understands as "this
      // target is not serving right now", so it lands in the operator's existing
      // "target down" alert instead of needing a new one.
      //
      // The body names the settings. It therefore tells an anonymous caller that
      // the endpoint is misconfigured rather than merely protected, which is a
      // deliberate trade: it gives an attacker nothing - the endpoint is shut
      // either way, and there is no secret to work towards - and it is the
      // difference between an operator fixing this in a minute with curl and
      // reporting that monitoring broke on upgrade. The same reason is in the
      // application log, once, from Start().
      //
      // No part of the exposition, and no metric name, appears in any of these
      // bodies.
      const char *body;

      switch (metrics_availability_)
      {
      case MetricsNeedsCredential:
         body = "metrics unavailable: this listener is bound to a non-loopback address and no credential is configured, "
                "so the exposition would be readable by anyone who can reach this port. Set MetricsServerAuthToken, or "
                "set both MetricsServerAuthUsername and MetricsServerAuthPassword, or bind to 127.0.0.1 or ::1. The health "
                "probes /livez, /readyz and /healthz are unaffected.\n";
         break;

      case MetricsNeedsTls:
         body = "metrics unavailable: MetricsServerCertificateFile and MetricsServerPrivateKeyFile are set but the TLS "
                "context could not be prepared, and serving the exposition in the clear instead would defeat the "
                "configuration. See the application and error logs for the certificate or key failure. The health "
                "probes /livez, /readyz and /healthz are unaffected.\n";
         break;

      default:
         // Unreachable: the caller checks for MetricsAvailable first. Present so
         // that adding an enumerator cannot silently produce an empty body.
         body = "metrics unavailable\n";
         break;
      }

      return BuildHttpResponse_(503, "text/plain; charset=utf-8", body);
   }

   AnsiString
   MetricsServer::BuildMetricsBody_()
   {
      ServerStatus *status = ServerStatus::Instance();

      AnsiString body;

      AnsiString line;

      body += "# HELP hmailserver_processed_messages_total Number of messages processed since server start.\n";
      body += "# TYPE hmailserver_processed_messages_total counter\n";
      line.Format("hmailserver_processed_messages_total %d\n", status->GetNumberOfProcessedMessages());
      body += line;

      body += "# HELP hmailserver_spam_messages_total Number of detected spam messages since server start.\n";
      body += "# TYPE hmailserver_spam_messages_total counter\n";
      line.Format("hmailserver_spam_messages_total %d\n", status->GetNumberOfDetectedSpamMessages());
      body += line;

      body += "# HELP hmailserver_viruses_removed_total Number of viruses removed since server start.\n";
      body += "# TYPE hmailserver_viruses_removed_total counter\n";
      line.Format("hmailserver_viruses_removed_total %d\n", status->GetNumberOfRemovedViruses());
      body += line;

      body += "# HELP hmailserver_tls_handshakes_total Number of completed TLS/SSL handshakes since server start.\n";
      body += "# TYPE hmailserver_tls_handshakes_total counter\n";
      line.Format("hmailserver_tls_handshakes_total %d\n", status->GetNumberOfTlsHandshakesCompleted());
      body += line;

      body += "# HELP hmailserver_tls_handshake_failures_total Number of failed TLS/SSL handshakes since server start.\n";
      body += "# TYPE hmailserver_tls_handshake_failures_total counter\n";
      line.Format("hmailserver_tls_handshake_failures_total %d\n", status->GetNumberOfTlsHandshakeFailures());
      body += line;

      body += "# HELP hmailserver_auth_success_total Number of successful authentications since server start.\n";
      body += "# TYPE hmailserver_auth_success_total counter\n";
      line.Format("hmailserver_auth_success_total %d\n", status->GetNumberOfAuthenticationsSucceeded());
      body += line;

      body += "# HELP hmailserver_auth_failures_total Number of failed authentication attempts since server start.\n";
      body += "# TYPE hmailserver_auth_failures_total counter\n";
      line.Format("hmailserver_auth_failures_total %d\n", status->GetNumberOfAuthenticationFailures());
      body += line;

      // The one thing an operator cannot otherwise see about this listener. Failed
      // credentials are deliberately not logged per request (see HandleClient_), so
      // without this series a password-guessing run against an exposed /metrics
      // leaves no trace at all. It is a counter rather than a log line so that
      // "somebody is probing the monitoring port" is an alertable rate, and it stays
      // at zero on every installation that has no credential configured - which
      // means a value above zero is always worth reading.
      //
      // Note what it is NOT: it is not folded into hmailserver_auth_failures_total.
      // That counter means "an account failed to log on to a mail protocol" and is
      // what brute-force alerts are built on; a refused scrape has nothing to do
      // with an account and would corrupt it.
      body += "# HELP hmailserver_metrics_unauthorized_requests_total Requests to /metrics refused for a missing or wrong credential. Always 0 when no credential is configured. Alert on rate(hmailserver_metrics_unauthorized_requests_total[5m]) > 0.\n";
      body += "# TYPE hmailserver_metrics_unauthorized_requests_total counter\n";
      line.Format("hmailserver_metrics_unauthorized_requests_total %I64u\n", status->GetMetricsUnauthorizedRequestsCount());
      body += line;

      body += "# HELP hmailserver_messages_delivered_total Number of message delivery passes that completed successfully.\n";
      body += "# TYPE hmailserver_messages_delivered_total counter\n";
      line.Format("hmailserver_messages_delivered_total %d\n", status->GetNumberOfMessagesDelivered());
      body += line;

      body += "# HELP hmailserver_messages_deferred_total Number of message delivery passes rescheduled for a later attempt (temporary failure).\n";
      body += "# TYPE hmailserver_messages_deferred_total counter\n";
      line.Format("hmailserver_messages_deferred_total %d\n", status->GetNumberOfMessagesDeferred());
      body += line;

      body += "# HELP hmailserver_messages_bounced_total Number of bounce/NDR messages generated for permanently failed recipients.\n";
      body += "# TYPE hmailserver_messages_bounced_total counter\n";
      line.Format("hmailserver_messages_bounced_total %d\n", status->GetNumberOfMessagesBounced());
      body += line;

      body += "# HELP hmailserver_sessions Current number of active sessions per protocol.\n";
      body += "# TYPE hmailserver_sessions gauge\n";
      line.Format("hmailserver_sessions{protocol=\"smtp\"} %d\n", status->GetNumberOfSessions(STSMTP));
      body += line;
      line.Format("hmailserver_sessions{protocol=\"imap\"} %d\n", status->GetNumberOfSessions(STIMAP));
      body += line;
      line.Format("hmailserver_sessions{protocol=\"pop3\"} %d\n", status->GetNumberOfSessions(STPOP3));
      body += line;

      // Work queue depth, per queue.
      //
      // WorkQueue has had GetQueueDepth and GetWaitingBlockingTaskCount for a while
      // and they were correct and had no caller anywhere in the tree, so the only
      // route any of it reached an operator was the stall report - which fires only
      // once a queue is already wedged. A queue filling up is the part you want to
      // see coming.
      //
      // Both are cheap enough for the request thread, which is the constraint this
      // listener works under: depth is an atomic load, and the waiting count is a
      // short lock around a size(). Neither touches the database or the disk.
      //
      // Cardinality is bounded and static - this server creates five queues, all
      // named at startup - so a queue label cannot grow a time series per message
      // the way a per-task label would. GetRunningTasks is deliberately NOT exposed
      // here for exactly that reason: it is per-task detail with a name and a thread
      // id, which belongs in the stall report that already prints it, not in a
      // metric that would mint a series per task.
      std::vector<std::shared_ptr<WorkQueue> > queues = WorkQueueManager::Instance()->GetAllQueues();

      body += "# HELP hmailserver_workqueue_depth Tasks accepted but not yet started, per work queue.\n";
      body += "# TYPE hmailserver_workqueue_depth gauge\n";

      for (std::shared_ptr<WorkQueue> queue : queues)
      {
         if (!queue)
            continue;

         line.Format("hmailserver_workqueue_depth{queue=\"%s\"} %d\n",
                     AnsiString(queue->GetName()).c_str(), queue->GetQueueDepth());
         body += line;
      }

      body += "# HELP hmailserver_workqueue_blocking_tasks_waiting Tasks marked as possibly blocking that are waiting for a slot, per work queue. These hold no worker thread; a number that stays above zero means the AsyncQueueReservedThreads cap is doing its job and the dependency behind those tasks is slow.\n";
      body += "# TYPE hmailserver_workqueue_blocking_tasks_waiting gauge\n";

      for (std::shared_ptr<WorkQueue> queue : queues)
      {
         if (!queue)
            continue;

         line.Format("hmailserver_workqueue_blocking_tasks_waiting{queue=\"%s\"} %d\n",
                     AnsiString(queue->GetName()).c_str(), queue->GetWaitingBlockingTaskCount());
         body += line;
      }

      // Per-domain message counters. Present only when MetricsPerDomainEnabled is
      // set, because every label value here is a separate time series in whatever
      // scrapes this - an operator hosting several thousand domains should choose to
      // pay for that rather than find out. The label set is bounded by the domains
      // this server hosts, never by what arrives: a sender domain nobody here hosts
      // is not labelled, so no amount of inbound mail can create series.
      if (IniFileSettings::Instance()->GetMetricsPerDomainEnabled())
      {
         body += "# HELP hmailserver_domain_messages_received_total Messages accepted for each locally hosted domain since start. Counted once per message per domain, not once per recipient.\n";
         body += "# TYPE hmailserver_domain_messages_received_total counter\n";

         for (const auto &entry : ServerStatus::Instance()->GetDomainMessagesReceived())
         {
            line.Format("hmailserver_domain_messages_received_total{domain=\"%s\"} %I64d\n",
                        EscapeLabelValue_(entry.first).c_str(), entry.second);
            body += line;
         }

         body += "# HELP hmailserver_domain_messages_sent_total Messages accepted from each locally hosted domain since start.\n";
         body += "# TYPE hmailserver_domain_messages_sent_total counter\n";

         for (const auto &entry : ServerStatus::Instance()->GetDomainMessagesSent())
         {
            line.Format("hmailserver_domain_messages_sent_total{domain=\"%s\"} %I64d\n",
                        EscapeLabelValue_(entry.first).c_str(), entry.second);
            body += line;
         }
      }

      // A start timestamp rather than an uptime counter, matching the
      // process_start_time_seconds convention every Prometheus client library
      // follows. Uptime is then time() - hmailserver_start_time_seconds, and a
      // restart is a step change that changes() detects, rather than a reset to zero
      // that looks the same as a counter rollover.
      body += "# HELP hmailserver_start_time_seconds Unix timestamp at which the metrics listener started. Uptime is time() - hmailserver_start_time_seconds; restarts are visible as changes(hmailserver_start_time_seconds[1h]).\n";
      body += "# TYPE hmailserver_start_time_seconds gauge\n";
      line.Format("hmailserver_start_time_seconds %I64d\n", start_unix_time_);
      body += line;

      // Build identity via the _info convention: value always 1, everything of
      // interest in the labels. Both label values come from memory. The schema
      // version is the compiled-in REQUIRED_DB_VERSION rather than a read of
      // hm_dbversion, because Application::OnDatabaseConnected refuses to start the
      // server when the two disagree - so the constant is the running schema version
      // by construction, and this avoids putting a database read (and therefore an
      // exception source) on the listener thread once per scrape.
      body += "# HELP hmailserver_build_info Build identity of the running server. The value is always 1; the information is in the labels.\n";
      body += "# TYPE hmailserver_build_info gauge\n";
      line.Format("hmailserver_build_info{version=\"%s\",architecture=\"%s\",database_schema_version=\"%d\"} 1\n",
         EscapeLabelValue_(Application::Instance()->GetVersionNumber()).c_str(),
         EscapeLabelValue_(Application::Instance()->GetVersionArchitecture()).c_str(),
         Configuration::Instance()->GetRequiredDBVersion());
      body += line;

      // OpenMetrics models an enumeration as a StateSet: one series per state
      // carrying a boolean, so exactly one series reads 1 and an alert can say
      // hmailserver_state{state="running"} == 0 instead of encoding a magic number.
      // The series layout below is that StateSet layout. The declared type stays
      // "gauge" on purpose: this endpoint serves the Prometheus text format
      // (version=0.0.4), whose parser rejects an unrecognised type name outright, so
      // declaring "stateset" here would make Prometheus discard the whole scrape and
      // every other metric with it.
      int state = status->GetState();

      body += "# HELP hmailserver_state Current server state, one series per state (OpenMetrics StateSet layout): exactly one carries 1.\n";
      body += "# TYPE hmailserver_state gauge\n";

      for (const StateSeries &entry : StateSeriesTable)
      {
         line.Format("hmailserver_state{state=\"%s\"} %d\n", entry.label, state == entry.state ? 1 : 0);
         body += line;
      }

      // Database connectivity and connection-pool saturation. Named _connected
      // rather than _up: Prometheus synthesises its own "up" series per target, and
      // an operator reading "up" next to "hmailserver_database_up" on a dashboard has
      // no way to tell a scrape failure from a database failure.
      //
      // The value now means "the database answered a statement recently", not "the
      // pool holds connection objects". Those are not the same thing and the
      // difference is not academic: the pool keeps its objects after the server on
      // the other end has gone away, so the old value read 1 throughout an outage.
      // An operator alerting on hmailserver_database_connected == 0 had an alert that
      // could not fire. See rule 6 in the header.
      AnsiString databaseReason;
      bool database_answering = IsDatabaseAnswering_(databaseReason);

      std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();

      body += "# HELP hmailserver_database_connected Whether the database answered this server's readiness probe recently (1=answering, 0=not answering). Proved by a round trip, not by the connection pool holding objects.\n";
      body += "# TYPE hmailserver_database_connected gauge\n";
      line.Format("hmailserver_database_connected %d\n", database_answering ? 1 : 0);
      body += line;

      // When the last round trip actually completed, both as a timestamp and as an
      // age. Two series for one fact, on purpose: the timestamp is the conventional
      // Prometheus form and is what an alert expression wants, while the age is
      // computed entirely from this server's own monotonic clock and so is the only
      // one of the two that stays meaningful when the scraper's clock and the mail
      // server's clock disagree - and a signal whose whole resolution is a few
      // seconds is exactly the size of a routine NTP correction.
      ULONGLONG probeAgeSeconds = 0;
      __int64 probeSuccessUnixTime = 0;

      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);

         probeSuccessUnixTime = database_probe_success_unix_time_;
         probeAgeSeconds = (GetTickCount64() - database_probe_success_tick_) / 1000;
      }

      body += "# HELP hmailserver_database_probe_success_timestamp_seconds Unix timestamp at which the database last answered a readiness probe.\n";
      body += "# TYPE hmailserver_database_probe_success_timestamp_seconds gauge\n";
      line.Format("hmailserver_database_probe_success_timestamp_seconds %I64d\n", probeSuccessUnixTime);
      body += line;

      body += "# HELP hmailserver_database_probe_age_seconds Seconds since the database last answered a readiness probe, measured on this server's monotonic clock. /readyz reports 503 once this exceeds its staleness ceiling.\n";
      body += "# TYPE hmailserver_database_probe_age_seconds gauge\n";
      line.Format("hmailserver_database_probe_age_seconds %I64u\n", probeAgeSeconds);
      body += line;

      int db_busy = db_manager ? db_manager->GetBusyConnectionCount() : 0;
      int db_available = db_manager ? db_manager->GetAvailableConnectionCount() : 0;

      body += "# HELP hmailserver_db_connections Number of database connections in the pool by state.\n";
      body += "# TYPE hmailserver_db_connections gauge\n";
      line.Format("hmailserver_db_connections{state=\"busy\"} %d\n", db_busy);
      body += line;
      line.Format("hmailserver_db_connections{state=\"available\"} %d\n", db_available);
      body += line;

      // Read out of the cache the refresher fills, and a request for the next refresh
      // left behind for it. No query is issued from here: this used to run two
      // aggregate queries against hm_messages on the listener thread, either of which
      // could park it - and every health probe behind it - for the whole of
      // DBConnectionAcquireTimeout. See rule 5 in the header.
      int queueDepth = 0;
      __int64 queueOldestAge = 0;

      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);

         queueDepth = queue_depth_cache_value_;
         queueOldestAge = queue_oldest_age_cache_value_;
         queue_refresh_requested_ = true;
      }

      body += "# HELP hmailserver_delivery_queue_messages Number of messages currently in the SMTP delivery queue.\n";
      body += "# TYPE hmailserver_delivery_queue_messages gauge\n";
      line.Format("hmailserver_delivery_queue_messages %d\n", queueDepth);
      body += line;

      // Depth on its own cannot tell a burst from a stuck relay: a deep queue whose
      // oldest message is seconds old is a busy server draining normally, while a
      // shallow queue whose oldest message is hours old is a destination nobody is
      // getting mail to. This is the second half of that pair.
      body += "# HELP hmailserver_delivery_queue_oldest_message_age_seconds Age of the oldest message in the SMTP delivery queue, 0 when the queue is empty or the age is unknown.\n";
      body += "# TYPE hmailserver_delivery_queue_oldest_message_age_seconds gauge\n";
      line.Format("hmailserver_delivery_queue_oldest_message_age_seconds %I64d\n", queueOldestAge);
      body += line;

      body += "# HELP hmailserver_messagestore_missing_files Number of messages whose backing file was missing on disk at the last consistency check (0 when consistent or the check is disabled).\n";
      body += "# TYPE hmailserver_messagestore_missing_files gauge\n";
      line.Format("hmailserver_messagestore_missing_files %d\n", status->GetMessageStoreMissingFiles());
      body += line;

      // Histograms, not summaries. Both families used to be declared "summary" while
      // emitting only _sum and _count, which meant the only thing derivable from them
      // was a mean - and a mean latency hides exactly the tail an operator cares
      // about. The bucket bounds live in ServerStatus and the buckets are filled on
      // the observation path, so the declared type is a histogram from the very first
      // scrape whether or not any traffic has arrived yet. Note that _sum and _count
      // keep their existing names and meaning, so anything already computing a mean
      // from them keeps working.
      body += "# HELP hmailserver_command_processing_seconds Processing time of client protocol command lines (SMTP/IMAP/POP3).\n";
      body += "# TYPE hmailserver_command_processing_seconds histogram\n";
      body += BuildHistogramSeries_("hmailserver_command_processing_seconds",
         ServerStatus::GetCommandLatencyBucketBounds(), status->GetCommandLatencySnapshot());

      body += "# HELP hmailserver_db_query_seconds Execution time of database statements run through the connection manager (all backends).\n";
      body += "# TYPE hmailserver_db_query_seconds histogram\n";
      body += BuildHistogramSeries_("hmailserver_db_query_seconds",
         ServerStatus::GetDatabaseLatencyBucketBounds(), status->GetDatabaseLatencySnapshot());

      body += "# HELP hmailserver_db_slow_queries_total Database statements whose execution time exceeded SlowQueryLogMilliseconds (0 when the threshold is disabled).\n";
      body += "# TYPE hmailserver_db_slow_queries_total counter\n";
      line.Format("hmailserver_db_slow_queries_total %I64u\n", status->GetDatabaseSlowQueriesCount());
      body += line;

      body += GetCachedCertificateExpiryMetrics_();

      return body;
   }

   AnsiString
   MetricsServer::BuildHistogramSeries_(const char *name, const std::vector<double> &bounds,
      const ServerStatus::LatencySnapshot &snapshot)
   {
      AnsiString series;

      // ServerStatus sizes the bucket vector to bounds.size() + 1. The guard is here
      // so that if the two sides ever drift apart, this emits nothing rather than a
      // malformed histogram family - a truncated bucket list would make
      // histogram_quantile() return confident nonsense.
      if (snapshot.cumulative_buckets.size() != bounds.size() + 1)
         return series;

      AnsiString line;

      for (size_t i = 0; i < bounds.size(); i++)
      {
         line.Format("%s_bucket{le=\"%.6g\"} %I64u\n", name, bounds[i], snapshot.cumulative_buckets[i]);
         series += line;
      }

      // The +Inf bucket is the total, and _count is emitted from that same value
      // rather than read separately, so the two can never disagree.
      unsigned __int64 total = snapshot.cumulative_buckets[bounds.size()];

      line.Format("%s_bucket{le=\"+Inf\"} %I64u\n", name, total);
      series += line;

      line.Format("%s_sum %.6f\n", name, static_cast<double>(snapshot.microseconds_total) / 1000000.0);
      series += line;

      line.Format("%s_count %I64u\n", name, total);
      series += line;

      return series;
   }

   void
   MetricsServer::Run_Refresher_()
   {
      // The second of this class's two threads. It exists so that the first one - the
      // accept loop - performs no database or file I/O at all, which is what stops a
      // stalled database from silently taking the health probes down with it and, on
      // an orchestrator, getting the process killed for it. See rule 5 in the header
      // for the failure this replaces.
      //
      // The same exception-barrier argument as Run_ applies, and for the same reason:
      // this is the top of a std::thread, so an exception that escaped here would be
      // std::terminate() and the whole mail server would die because a monitoring
      // refresh went wrong. Each refresh has its own barrier so one failing does not
      // skip the other two, and this outer barrier catches anything they let past -
      // including out of the reporting itself, which reaches operator-supplied script
      // through ScriptServer::FireEvent.
      for (;;)
      {
         {
            std::unique_lock<std::mutex> guard(refresh_mutex_);

            if (refresher_stop_)
               return;
         }

         try
         {
            RefreshDatabaseProbe_();
            RefreshDeliveryQueueCache_();
            RefreshCertificateExpiryCache_();
         }
         catch (...)
         {
            try
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6045, "MetricsServer::Run_Refresher_",
                  "An exception escaped the metrics refresh thread. The cached figures behind /metrics and /readyz may be stale until the next refresh; the listener is still running.");
            }
            catch (...)
            {
               // Nothing left to do: reporting is what failed, and the alternative to
               // swallowing this is std::terminate() on the mail server.
            }
         }

         // A timed wait and not a Sleep, so a Stop() arriving while this thread is
         // idle gets it back at once instead of after the rest of a tick. The
         // predicate form is what makes that correct in the presence of a spurious
         // wakeup, and its return value is the predicate's final answer - so it is
         // also exactly "was this a stop rather than a timeout", which is why it is
         // captured rather than discarded.
         {
            std::unique_lock<std::mutex> guard(refresh_mutex_);

            if (refresher_stop_)
               return;

            const bool stopping = refresher_wakeup_.wait_for(guard,
               std::chrono::milliseconds(static_cast<long long>(RefresherTickMilliseconds)),
               [this] { return refresher_stop_; });

            if (stopping)
               return;
         }
      }
   }

   void
   MetricsServer::RefreshDatabaseProbe_()
   {
      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);

         const ULONGLONG interval = database_reachable_
            ? DatabaseProbeIntervalMilliseconds
            : DatabaseProbeFailureIntervalMilliseconds;

         if (database_probe_tick_ != 0 && (GetTickCount64() - database_probe_tick_) < interval)
            return;

         // Stamped before the statement runs rather than after it returns. A probe
         // against a database that has stopped answering can sit inside
         // GetConnection_ for the whole of DBConnectionAcquireTimeout, and stamping
         // afterwards would mean the next tick reissued it immediately - one more
         // statement queued behind the first, holding one more pooled connection,
         // against a database already in trouble.
         database_probe_tick_ = GetTickCount64();
      }

      bool reachable = false;

      try
      {
         std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();

         if (db_manager)
         {
            // hm_dbversion, because it is one row, it exists on all four supported
            // backends, and it is the table Application already reads at start-up to
            // verify the schema version - so this depends on nothing that could
            // differ between engines. "select 1" was rejected: SQL Server Compact
            // Edition has no FROM-less select, so it would have made the probe pass
            // on three backends and fail permanently on the fourth, which is a worse
            // failure than no probe at all.
            //
            // The recordset has to come back AND have a row. A null recordset is what
            // OpenRecordset returns both when the pool could not hand out a
            // connection and when the statement itself failed, which are the two
            // shapes a database outage takes.
            SQLCommand command("select * from hm_dbversion");

            std::shared_ptr<DALRecordset> recordset = db_manager->OpenRecordset(command);

            reachable = recordset && !recordset->IsEOF();
         }
      }
      catch (...)
      {
         reachable = false;
      }

      bool changed = false;

      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);

         changed = database_reachable_ != reachable;
         database_reachable_ = reachable;

         if (reachable)
         {
            database_probe_success_tick_ = GetTickCount64();
            database_probe_success_unix_time_ = static_cast<__int64>(time(nullptr));
         }
      }

      // One line per TRANSITION and never one per probe. This is the state a load
      // balancer is about to act on, so it belongs in the log where an operator
      // correlating "why did the VIP move at 03:12" can find it - but at one line
      // every few seconds it would be noise nobody reads, and it would be the first
      // thing to fill a log during the outage it is describing.
      //
      // LOG_APPLICATION and not a reported error, deliberately. A database that
      // cannot be reached is an operational condition, it is already reported by
      // whatever failed to read it, and an error report here would fire on every
      // configuration where this is the first thing to notice - including, through
      // ErrorManager's OnError event, operator script running on this thread.
      //
      // Braced, and it matters: LOG_APPLICATION expands to a bare "if (mask) log;",
      // so an unbraced if/else around two of them binds the else to the macro's own
      // if and logs the wrong line whenever application logging happens to be off.
      if (changed)
      {
         if (reachable)
         {
            LOG_APPLICATION("MetricsServer: the database answered the readiness probe again. /readyz will report ready.");
         }
         else
         {
            LOG_APPLICATION("MetricsServer: the database did not answer the readiness probe. /readyz will report 503 until it does, so a load balancer sheds this node rather than routing mail it cannot deliver. /livez is deliberately unaffected - the process is healthy, and killing it would not fix a database.");
         }
      }
   }

   void
   MetricsServer::RefreshDeliveryQueueCache_()
   {
      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);

         // Nothing has scraped /metrics since the last refresh, so nothing needs
         // these two values. This keeps the cost model the old code had - an
         // installation with the port open and no Prometheus behind it pays nothing
         // for two aggregate queries against hm_messages - while moving the work of
         // an actual scrape off the request path.
         if (!queue_refresh_requested_)
            return;

         if (queue_cache_tick_ != 0 && (GetTickCount64() - queue_cache_tick_) < DeliveryQueueRefreshIntervalMilliseconds)
            return;

         queue_cache_tick_ = GetTickCount64();
         queue_refresh_requested_ = false;
      }

      std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();
      if (!db_manager || !db_manager->GetIsConnected())
         return;

      // Built into locals and published in one short critical section at the end. The
      // mutex is not held across either statement, which is the invariant rule 5 rests
      // on: holding it there would block the request thread for as long as the
      // database took, which is precisely the coupling this thread exists to break.
      int depth = 0;
      __int64 oldestAge = 0;

      try
      {
         depth = PersistentMessage::GetDeliveryQueueCount();

         // min() over a "YYYY-MM-DD HH:MM:SS" column: the format sorts
         // lexicographically the same way it sorts chronologically, so this is one
         // portable aggregate rather than a per-backend date function.
         SQLCommand command("select min(messagecreatetime) as oldest from hm_messages where messagetype = @MESSAGETYPE");
         command.AddParameter("@MESSAGETYPE", Message::Delivering);

         std::shared_ptr<DALRecordset> recordset = db_manager->OpenRecordset(command);

         if (recordset && !recordset->IsEOF())
         {
            __int64 oldestUnixTime = SystemTimeStampToUnixTime(recordset->GetStringValue("oldest"));

            if (oldestUnixTime > 0)
            {
               __int64 age = static_cast<__int64>(time(nullptr)) - oldestUnixTime;

               // Clamped at zero: a clock adjustment must not produce a negative age,
               // which would read as a message from the future and break any alert
               // expressed as a threshold.
               oldestAge = age > 0 ? age : 0;
            }
         }
      }
      catch (...)
      {
         // Serve the previous values rather than losing the whole scrape. Every other
         // metric on this endpoint is more useful than these two, and a database that
         // cannot answer is already reported by hmailserver_database_connected - so
         // this is a debug note, not an error: it is not separately actionable and it
         // would repeat on every cache refresh for as long as the outage lasted.
         LOG_DEBUG("MetricsServer: Delivery-queue metrics could not be refreshed. Serving the previously cached values.");
         return;
      }

      std::lock_guard<std::mutex> guard(refresh_mutex_);

      queue_depth_cache_value_ = depth;
      queue_oldest_age_cache_value_ = oldestAge;
   }

   AnsiString
   MetricsServer::GetCachedCertificateExpiryMetrics_()
   {
      std::lock_guard<std::mutex> guard(refresh_mutex_);

      certificate_refresh_requested_ = true;

      return certificate_cache_body_;
   }

   void
   MetricsServer::RefreshCertificateExpiryCache_()
   {
      {
         std::lock_guard<std::mutex> guard(refresh_mutex_);

         // Same on-demand rule as the delivery queue: no scrape since the last
         // refresh means nothing needs these series, so nothing re-reads the PEMs.
         if (!certificate_refresh_requested_)
            return;

         if (certificate_cache_tick_ != 0 && (GetTickCount64() - certificate_cache_tick_) < CertificateRefreshIntervalMilliseconds)
            return;

         certificate_cache_tick_ = GetTickCount64();
         certificate_refresh_requested_ = false;
      }

      // Keyed by the label value, because the label value is what makes a series
      // unique. sslcertificatename has no unique constraint in any of the schemas, so
      // two certificates can legitimately share a name; emitting one series per
      // certificate row would then emit the same series twice, and Prometheus rejects
      // an entire scrape that contains a duplicate series - taking every other metric
      // on this endpoint down with it. Where a name is shared, the earliest expiry
      // wins, because that is the one worth alerting on.
      std::map<AnsiString, __int64> earliestExpiryByName;

      try
      {
         int count = 0;

         auto addCertificate = [&earliestExpiryByName, &count](const String &name, const String &certificateFile)
         {
            __int64 notAfter = ReadCertificateExpiry_(certificateFile);
            if (notAfter == 0)
               return;

            AnsiString label = EscapeLabelValue_(name);

            auto existing = earliestExpiryByName.find(label);
            if (existing == earliestExpiryByName.end() || notAfter < existing->second)
               earliestExpiryByName[label] = notAfter;

            count++;
         };

         std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();

         int certificateCount = certificates ? certificates->GetCount() : 0;

         for (int i = 0; i < certificateCount; i++)
         {
            std::shared_ptr<SSLCertificate> certificate = certificates->GetItem(i);
            if (!certificate)
               continue;

            addCertificate(certificate->GetName(), certificate->GetCertificateFile());
         }

         // No configured certificate row means the ACME install shape, where the
         // certificate lives on disk under the ACME directory and is never entered in
         // the certificate table - the same fallback RestApiServer applies for the
         // TLSA endpoint. Without it the metric is silent on exactly the installs
         // where a silent renewal failure is the thing worth alerting on. Nothing is
         // emitted when that file does not exist either, so the shipped default (no
         // certificate, no ACME) still produces no series and no diagnostic.
         if (count == 0)
            addCertificate(_T("ACME (automatic)"), AcmeClient::GetCertificateDirectory() + _T("\\fullchain.pem"));
      }
      catch (...)
      {
         // Abandoned rather than published. The previous body stays in the cache,
         // which is the difference between "this refresh found nothing new" and
         // "these series vanish for the next five minutes" - and vanishing is what
         // the old code did, because it wrote whatever it had built when the
         // exception fired straight into the cache. A certificate-expiry series that
         // disappears reads to an alert on absent data exactly like a certificate
         // that was deleted on purpose.
         LOG_DEBUG("MetricsServer: TLS certificate expiry metrics could not be refreshed. Serving the previously cached series.");
         return;
      }

      // Ordered by expiry, soonest first, and then by name so that two certificates
      // expiring at the same instant still come out in the same order on every
      // refresh. The order is what makes the ceiling below defensible: if an
      // installation ever has more certificates than the endpoint will emit series
      // for, the ones that survive are the ones about to expire, which are the only
      // ones anybody would have alerted on.
      std::vector<std::pair<__int64, AnsiString> > byExpiry;

      for (std::map<AnsiString, __int64>::const_iterator it = earliestExpiryByName.begin();
           it != earliestExpiryByName.end(); ++it)
      {
         byExpiry.push_back(std::pair<__int64, AnsiString>(it->second, it->first));
      }

      // The comparator is spelled out rather than left to std::pair's own operator<,
      // because the second member is an AnsiString and relying on which of
      // CStdString's comparison overloads a pair comparison resolves to is not worth
      // the risk on a /WX build.
      std::sort(byExpiry.begin(), byExpiry.end(),
         [](const std::pair<__int64, AnsiString> &left, const std::pair<__int64, AnsiString> &right)
         {
            if (left.first != right.first)
               return left.first < right.first;

            return strcmp(left.second.c_str(), right.second.c_str()) < 0;
         });

      size_t emitted = byExpiry.size();

      if (emitted > MaxCertificateExpirySeries)
      {
         emitted = MaxCertificateExpirySeries;

         String message;
         message.Format(_T("MetricsServer: %d configured TLS certificates have a readable expiry, which is more than the %d series hmailserver_tls_certificate_expiry_seconds will emit. The %d expiring soonest are exposed; the rest are omitted so one scrape cannot grow without bound."),
            (int) byExpiry.size(), (int) MaxCertificateExpirySeries, (int) MaxCertificateExpirySeries);
         LOG_APPLICATION(message);
      }

      AnsiString body;

      // Nothing is emitted when no certificate is configured, which is the shipped
      // default - an empty metric family would only invite an alert on absent data.
      if (emitted > 0)
      {
         body += "# HELP hmailserver_tls_certificate_expiry_seconds Unix timestamp at which a configured TLS certificate expires. Alert on hmailserver_tls_certificate_expiry_seconds - time() falling below your renewal window.\n";
         body += "# TYPE hmailserver_tls_certificate_expiry_seconds gauge\n";

         AnsiString line;
         for (size_t i = 0; i < emitted; i++)
         {
            line.Format("hmailserver_tls_certificate_expiry_seconds{certificate=\"%s\"} %I64d\n",
               byExpiry[i].second.c_str(), byExpiry[i].first);
            body += line;
         }
      }

      std::lock_guard<std::mutex> guard(refresh_mutex_);

      certificate_cache_body_ = body;
   }

   __int64
   MetricsServer::ReadCertificateExpiry_(const String &certificate_file)
   {
      if (certificate_file.IsEmpty() || !FileUtilities::Exists(certificate_file))
         return 0;

      AnsiString narrowFileName = certificate_file;

      BIO *bio = BIO_new_file(narrowFileName.c_str(), "r");
      if (bio == nullptr)
         return 0;

      X509 *certificate = PEM_read_bio_X509(bio, nullptr, nullptr, nullptr);
      BIO_free(bio);

      if (certificate == nullptr)
      {
         // Debug level rather than an application-log line or a reported error: a
         // certificate the server cannot parse already fails loudly where it matters,
         // at the handshake, and repeating it here on every cache refresh would fill
         // the log without telling the operator anything new.
         LOG_DEBUG("MetricsServer: Could not parse certificate " + certificate_file + " while building the expiry metric.");
         return 0;
      }

      __int64 expiry = 0;

      tm notAfter = {};
      if (ASN1_TIME_to_tm(X509_get0_notAfter(certificate), &notAfter) == 1)
      {
         // notAfter is UTC, so it converts with _mkgmtime rather than mktime.
         time_t converted = _mkgmtime(&notAfter);
         if (converted != static_cast<time_t>(-1))
            expiry = static_cast<__int64>(converted);
      }

      X509_free(certificate);

      return expiry;
   }

   AnsiString
   MetricsServer::EscapeLabelValue_(const String &value)
   {
      // The exposition format escapes backslash, double quote and line feed in a
      // label value. Certificate names are operator-supplied text, and a single
      // unescaped quote in one would produce a malformed line and cost the whole
      // scrape. Backslash is replaced first, or it would double the escapes the
      // later replacements introduce.
      AnsiString escaped = value;

      escaped.Replace("\\", "\\\\");
      escaped.Replace("\"", "\\\"");
      escaped.Replace("\r", "");
      escaped.Replace("\n", "\\n");

      return escaped;
   }

   AnsiString
   MetricsServer::BuildHttpResponse_(int status_code, const char *content_type, const AnsiString &body,
      const AnsiString &extra_headers)
   {
      const char *reason;
      switch (status_code)
      {
      case 200: reason = "OK"; break;
      case 401: reason = "Unauthorized"; break;
      case 404: reason = "Not Found"; break;
      case 503: reason = "Service Unavailable"; break;
      default:  reason = "OK"; break;
      }

      // extra_headers, where present, already ends each line it carries with CRLF,
      // so it goes in before the blank line that terminates the header block rather
      // than being concatenated onto the format string with separators of its own.
      AnsiString headers;
      headers.Format("HTTP/1.0 %d %s\r\nContent-Type: %s\r\nContent-Length: %d\r\nConnection: close\r\n",
         status_code, reason, content_type, body.GetLength());

      headers += extra_headers;
      headers += "\r\n";

      return headers + body;
   }

   bool
   MetricsServer::IsDatabaseAnswering_(AnsiString &reason)
   {
      // Three conditions, and the second and third are the point. See rule 6 in the
      // header for the failure they replace.
      //
      // First: the pool has to hold connections at all. That is the only thing this
      // used to check, and on its own it is nearly worthless - the pool is built once
      // at start-up and nothing empties it when the database goes away, so
      // GetIsConnected() answers "connected" for a server nothing can reach. It is
      // kept because it is the one condition that is true before the first probe and
      // false after Disconnect(), and it costs one uncontended lock.
      std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();

      if (!db_manager || !db_manager->GetIsConnected())
      {
         reason = "database not connected";
         return false;
      }

      std::lock_guard<std::mutex> guard(refresh_mutex_);

      // Second: the most recent probe has to have SUCCEEDED. This is what makes the
      // answer about the database rather than about this process's data structures.
      if (!database_reachable_)
      {
         reason = "database did not answer the last readiness probe";
         return false;
      }

      // Third: it has to have succeeded RECENTLY, and this one covers the case the
      // second cannot. A probe against a database that has stopped answering does not
      // necessarily fail - it can simply not come back, for as long as
      // DBConnectionAcquireTimeout allows, which is sixty seconds by default. During
      // that minute database_reachable_ still holds the last good answer, so without
      // this ceiling /readyz would keep a node in a load balancer's rotation for a
      // full minute after the database stopped answering, and every message routed to
      // it in that minute is rejected or deferred.
      //
      // Never held for a probe that has not started: Start() seeds the success tick
      // with the moment the listener started, which is a moment when the database had
      // just been connected and its schema version read.
      if ((GetTickCount64() - database_probe_success_tick_) > DatabaseProbeStalenessMilliseconds)
      {
         reason = "database readiness probe has not completed recently";
         return false;
      }

      return true;
   }

   bool
   MetricsServer::IsReady_(AnsiString &reason)
   {
      if (ServerStatus::Instance()->GetState() != ServerStatus::StateRunning)
      {
         reason = "server not in running state";
         return false;
      }

      return IsDatabaseAnswering_(reason);
   }

   AnsiString
   MetricsServer::BuildHealthBody_(bool &healthy)
   {
      ServerStatus *status = ServerStatus::Instance();
      int state = status->GetState();
      bool running = state == ServerStatus::StateRunning;

      AnsiString databaseReason;
      bool database_up = IsDatabaseAnswering_(databaseReason);

      healthy = running && database_up;

      ULONGLONG uptime_seconds = (GetTickCount64() - start_tick_count_) / 1000;

      const char *state_name =
         state == ServerStatus::StateRunning ? "running" :
         state == ServerStatus::StateStarting ? "starting" :
         state == ServerStatus::StateStopping ? "stopping" :
         state == ServerStatus::StateStopped ? "stopped" : "unknown";

      // The session counts are deliberately gone from this body.
      //
      // They were the one thing here that this listener refuses to serve elsewhere.
      // Rule 2 closes /metrics with a 503 on a non-loopback bind precisely so that
      // "delivery-queue depth, session counts, authentication-failure counts and the
      // build and schema versions" are not readable from the network without a
      // credential - and then /healthz, which rule 1 keeps open and unauthenticated
      // in every configuration including that one, published the session counts
      // anyway. In the topology docs/HighAvailabilityRunbook.md section 3 ships -
      // 0.0.0.0, plain HTTP, no credential - anyone who could reach the port could
      // poll /healthz and watch this server's SMTP, IMAP and POP3 concurrency, which
      // is how much mail it is handling and when. An access-control rule that one
      // path enforces and another path hands out is not a rule.
      //
      // Nothing needed them: a health check acts on the status, and the same numbers
      // are in hmailserver_sessions behind whatever protection /metrics has. What
      // stays is what a probe has to be able to say - whether it is healthy, why not,
      // and for how long the process has been up - and that matches the shape this
      // endpoint has always been documented as having ("JSON status/state/database").
      AnsiString body;
      body.Format(
         "{\"status\":\"%s\",\"state\":\"%s\",\"database\":\"%s\","
         "\"uptime_seconds\":%I64u}\n",
         healthy ? "ok" : "unavailable",
         state_name,
         database_up ? "up" : "down",
         uptime_seconds);

      return body;
   }
}
