// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Minimal HTTP endpoint exposing server statistics in the Prometheus
// text exposition format. Enabled with MetricsServerPort in hMailServer.ini.

#pragma once

#include "ServerStatus.h"

#include <thread>
#include <map>
#include <mutex>
#include <string>
#include <vector>

namespace HM
{
   // Access control on this listener, and why it is shaped the way it is.
   //
   // Until now the only protection was the bind address, which defaults to
   // 127.0.0.1. That is total protection and no protection at all at the same
   // time: complete for as long as the default holds, and gone the moment somebody
   // sets MetricsServerBindAddress so a Prometheus server on another host can
   // scrape it - at which point delivery-queue depth, session counts,
   // authentication-failure counts, the build version and the database schema
   // version are readable by anyone who can reach the port. Nothing warned about
   // that and nothing prevented it.
   //
   // Four rules now hold, and the first one outranks the rest.
   //
   //  1. THE HEALTH PROBES ARE ALWAYS SERVED, AND ARE NEVER AUTHENTICATED.
   //     /livez, /readyz and /healthz answer in every configuration this listener
   //     will start in, whatever rules 2 to 4 decide about /metrics. A probe has
   //     nowhere to keep a secret: a Kubernetes httpGet probe, an ELB or ALB
   //     health check and a Docker HEALTHCHECK cannot present a bearer token, and
   //     an authenticated probe would report a healthy server as failing and get
   //     it killed or fenced out of a cluster.
   //
   //     This is also why nothing here refuses to START. An earlier attempt at
   //     this work enforced rules 2 and 3 by returning false from Start(), which
   //     is the one implementation that cannot be allowed: it deletes the probes
   //     along with the exposition. It would have broken this project's own
   //     documented topology - docs/HighAvailabilityRunbook.md section 3 ships
   //     MetricsServerPort=8080 with MetricsServerBindAddress=0.0.0.0, plain HTTP,
   //     no credential, and points the VIP health check at /readyz - so an upgrade
   //     would have taken the failover health check away from every reader who
   //     followed the runbook, silently, and the fix would have looked like
   //     "hMailServer 6.2.19 broke our load balancer". Access control that
   //     operators have to revert is not access control. So the listener always
   //     starts if the socket can be bound, and an unsafe configuration closes
   //     /metrics ALONE.
   //
   //  2. A CREDENTIAL IS REQUIRED ON /metrics WHEN THE BIND IS NOT LOOPBACK.
   //     Optional on a loopback bind, because a scrape from the same machine is
   //     already restricted to processes on that machine, and because every
   //     existing loopback scrape - a Prometheus node on the mail server, a cron
   //     job, this project's own regression fixtures - has to keep working
   //     untouched across an upgrade. Required beyond loopback, because out there
   //     the bind address is not a control at all.
   //
   //     Configured with MetricsServerAuthToken (Authorization: Bearer) and/or
   //     MetricsServerAuthUsername + MetricsServerAuthPassword (HTTP Basic). Both
   //     schemes exist because scrapers differ in what they can send - Prometheus
   //     does either, plenty of other agents do Basic only - and when both are
   //     configured, either one satisfies the check.
   //
   //     When the requirement is not met, /metrics answers 503 and the probes go
   //     on answering 200. 503 rather than 401: there is no credential that would
   //     work, so a challenge would invite a client to start guessing one that
   //     does not exist, and 503 is the code a scraper already understands as "the
   //     target is not serving this right now". The body names the settings that
   //     would fix it. That does tell an anonymous caller that the endpoint is
   //     misconfigured rather than merely protected, which is accepted
   //     deliberately: it buys nothing for an attacker - the endpoint is shut
   //     either way and there is nothing to guess - and it is the difference
   //     between an operator fixing a 503 in a minute and filing a bug about
   //     monitoring that stopped working after an upgrade.
   //
   //  3. TLS IS OPTIONAL AND OPT-IN, AND NEVER INFERRED.
   //     Off unless MetricsServerCertificateFile and MetricsServerPrivateKeyFile
   //     are both set. It is not forced on a non-loopback bind, because forcing it
   //     would break exactly the plain-HTTP probe the runbook documents, and
   //     because cluster-internal scraping over plain HTTP on a pod network is
   //     normal practice rather than a mistake. What does happen on a non-loopback
   //     bind with a credential and no TLS is a line in the application log saying
   //     that the credential will cross the network in the clear and can be
   //     replayed. An operator who has read that and is content with their network
   //     needs no further ceremony from us, and the earlier attempt's
   //     MetricsServerAllowPlaintext escape hatch is therefore gone: it existed
   //     only to waive a refusal that no longer happens, and a setting whose only
   //     purpose is to switch off another setting is a setting worth deleting.
   //
   //     When TLS IS configured, the whole port is HTTPS, probes included - a
   //     Kubernetes probe does that with scheme: HTTPS. What is deliberately NOT
   //     implemented is sniffing the first byte for the TLS record type and
   //     serving plaintext and HTTPS on one port, which would keep a plain-HTTP
   //     probe working next to an HTTPS scrape. It was rejected because it makes
   //     the encryption optional at the CLIENT's choice: a scraper misconfigured
   //     for http:// would send its bearer token in clear text and be accepted,
   //     which is the failure this rule exists to prevent, and it would be
   //     invisible from the server side.
   //
   //     If TLS is configured but cannot be prepared - unreadable certificate, a
   //     private key that does not match it - the listener still starts and still
   //     serves the probes, over plain HTTP, and /metrics answers 503 per rule 2's
   //     mechanism. It does not fall back to serving the exposition in the clear,
   //     which would silently downgrade a listener the operator asked to encrypt
   //     and would accept a credential over the transport they were trying to
   //     avoid.
   //
   //     The SSL_CTX comes from SslContextInitializer::InitServer, the same
   //     function SMTP, POP3 and IMAP use, so the cipher list, the TLS version
   //     toggles, the option mask and the key-exchange groups (including the
   //     hybrid post-quantum KEMs in TlsKeyExchangeGroups) cannot diverge here.
   //     SSL_CTX_new is deliberately absent from this file: RestApiServer and
   //     WebServicesServer each still build their own context and have each
   //     already drifted from the shared configuration, and a third copy would
   //     make that worse rather than better.
   //
   //  4. MONITORING TRAFFIC IS NOT COUNTED IN THE MAIL TLS METRICS.
   //     Nothing here touches hmailserver_tls_handshakes_total or
   //     hmailserver_tls_handshake_failures_total. Those two describe TLS on the
   //     mail protocols, they are what operators build
   //     rate(hmailserver_tls_handshake_failures_total[5m]) alerts on, and folding
   //     scrapes and port scans of the monitoring port into them would move the
   //     numbers for reasons that have nothing to do with mail - a port scanner
   //     hitting :8080 would page somebody about SMTP. A failed handshake on this
   //     port needs no counter of its own either: Prometheus already synthesises
   //     an "up" series per target, so a scraper that cannot handshake reports
   //     up == 0, which is a better signal than anything this endpoint could
   //     publish about its own unreachability.
   //
   // What is deliberately NOT here: no auto-ban and no delay on a failed
   // credential. Feeding this listener into the per-IP auto-ban would let a
   // misconfigured scraper ban the monitoring host off the mail ports as well, and
   // a delay on a rejected request would stall this single-threaded accept loop,
   // turning failed authentication into a denial of the scrape and of the probes.
   // Refusals are counted instead - hmailserver_metrics_unauthorized_requests_total
   // - so an operator can alert on the rate rather than find it in a log nobody
   // reads.
   class MetricsServer
   {
   public:
      MetricsServer();
      ~MetricsServer();

      // Starts the listener. Returns false only if the bind address is not an IPv4
      // literal or the socket could not be bound or listened on; the reason is
      // written to the application log. Deliberately narrow: see rule 1 above -
      // access control never refuses to start, because the probes have to answer
      // in every configuration.
      bool Start(const String &bind_address, int port);
      void Stop();

   private:

      // Whether /metrics may be served at all in this configuration, decided once
      // in Start() and read by the worker for every request. The probes ignore it.
      enum MetricsAvailability
      {
         // Serve it, subject to the credential when one is configured.
         MetricsAvailable = 0,

         // Bound to a non-loopback address with no credential configured. Rule 2.
         MetricsNeedsCredential = 1,

         // TLS was configured and could not be prepared. Rule 3.
         MetricsNeedsTls = 2
      };

      // One accepted client: the socket and, only after a completed TLS handshake,
      // the OpenSSL session that every subsequent read and write has to go
      // through. Grouping the two is what stops a later change reading past the
      // TLS upgrade and still talking to the bare socket, which would put the
      // exposition on the wire in clear text on a listener configured for HTTPS.
      struct Connection
      {
         explicit Connection(SOCKET client_socket) :
            socket(client_socket),
            tls_session(nullptr)
         {
         }

         // Owns a socket and an SSL object, so a copy would close and free both
         // twice. Nothing here needs one; say so rather than leave a future caller
         // to find out.
         Connection(const Connection &) = delete;
         Connection &operator=(const Connection &) = delete;

         SOCKET socket;
         SSL *tls_session;
      };

      void Run_();

      // Serves one request. Does NOT own the connection - Run_ closes it on both
      // the normal and the exceptional path, because everything in here (database
      // reads, certificate file parsing) sits under an exception barrier that must
      // not leak a socket or an SSL object when it fires.
      void HandleClient_(Connection &connection);

      // Releases the TLS session, if any, and closes the socket.
      static void CloseConnection_(Connection &connection);

      // Server side of the TLS handshake. False means the connection must be
      // abandoned without a reply: falling back to plain HTTP would serve the
      // whole exposition to a client that never negotiated encryption, on a
      // listener whose operator asked for HTTPS.
      bool StartTls_(Connection &connection) const;

      // The only two places bytes cross the wire, so the plaintext and TLS cases
      // cannot get out of step.
      static int Receive_(Connection &connection, char *buffer, int size);
      static void Send_(Connection &connection, const AnsiString &data);

      // Reads the request HEADER BLOCK and nothing else, up to and including the
      // blank line that terminates it. False only when not a single byte arrived,
      // so an unterminated request still gets served rather than silently dropped
      // - see the definition for why that leniency is load bearing.
      static bool ReadRequest_(Connection &connection, std::string &header_block);

      // Offset just past the blank line that ends the header block, or npos.
      // Accepts CRLF and bare LF line endings.
      static size_t FindHeaderBlockEnd_(const std::string &data);

      // Splits a header block into lines on CRLF or bare LF, stripping the line
      // endings. Element 0 is the request line; empty elements are possible and
      // are simply skipped by the header lookup.
      static void SplitHeaderLines_(const std::string &header_block, std::vector<std::string> &lines);

      // Request target from the request line, query string stripped. Empty for
      // anything that is not a GET.
      static std::string GetRequestPath_(const std::vector<std::string> &lines);

      // Value of one header, matched case-insensitively as the HTTP grammar
      // requires. Empty when the header is absent.
      static std::string GetHeaderValue_(const std::vector<std::string> &lines, const char *header_name);

      // Whether this request may read /metrics. Always true when no credential is
      // configured, which is the shipped behaviour of a loopback listener.
      bool IsRequestAuthorized_(const std::vector<std::string> &lines) const;

      // 401 carrying only the challenges that are actually configured.
      AnsiString BuildUnauthorizedResponse_() const;

      // 503 for /metrics in a configuration where it must not be served, naming
      // the settings that would open it. Never reached for a health probe.
      AnsiString BuildMetricsUnavailableResponse_() const;

      // Builds the shared server SSL context. False means TLS could not be
      // prepared; the caller closes /metrics and serves the probes in the clear.
      //
      // The certificate and key are passed in rather than re-read from
      // IniFileSettings, so the pair this builds a context from is provably the
      // same pair Start() based its decisions on. Application::Reinitialize()
      // re-reads every ini setting, so two reads a few lines apart can legitimately
      // return different values, and a listener that gated on one pair and
      // encrypted with another would be a bug nobody could reproduce.
      static bool InitializeTls_(const String &bind_address, int port,
         const String &certificate_file, const String &private_key_file,
         std::shared_ptr<boost::asio::ssl::context> &context);

      // True for an address only this machine can reach: the whole of 127.0.0.0/8,
      // not merely 127.0.0.1, because an operator who binds 127.0.0.2 has still
      // bound a loopback address and demanding a credential there would be a false
      // positive - the sort that teaches people the check is noise. Anything that
      // does not parse as an IPv4 literal reports false, so the gate fails closed;
      // Start() rejects such an address before this runs, but a future change to
      // that order must not silently open a hole.
      static bool IsLoopbackAddress_(const String &bind_address);

      // Overwrites a credential's bytes in place and empties it. See the
      // definition for what this does and does not promise.
      static void ScrubSecret_(AnsiString &secret);

      AnsiString BuildMetricsBody_();

      // Refreshes the cached SMTP delivery-queue figures (depth and the age of the
      // oldest queued message), querying the database at most once every few seconds
      // so scrapes never hammer it. Never throws: a database failure leaves the
      // previous values in place.
      void UpdateDeliveryQueueCache_();

      // Expiry timestamps of the configured TLS certificates, cached for minutes
      // because a notAfter only changes when a certificate is replaced.
      AnsiString BuildCertificateExpiryMetrics_();

      // Reads notAfter out of a PEM certificate as a Unix timestamp. Returns 0 when
      // the file is missing, unreadable or unparseable.
      static __int64 ReadCertificateExpiry_(const String &certificate_file);

      // Emits one complete Prometheus histogram family (cumulative _bucket series,
      // _sum, _count) from a ServerStatus latency snapshot.
      static AnsiString BuildHistogramSeries_(const char *name, const std::vector<double> &bounds,
         const ServerStatus::LatencySnapshot &snapshot);

      // Escapes a label value per the exposition format. Label values here can carry
      // operator-supplied text (certificate names), and one stray quote would make
      // the whole scrape unparseable.
      static AnsiString EscapeLabelValue_(const String &value);

      // Health probes (Kubernetes-style): liveness is always served, readiness and
      // health additionally check that the server is running and the database is
      // connected.
      bool IsReady_(AnsiString &reason);
      AnsiString BuildHealthBody_(bool &healthy);
      static AnsiString BuildHttpResponse_(int status_code, const char *content_type, const AnsiString &body,
         const AnsiString &extra_headers = AnsiString());

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;

      // The connection the worker is serving right now, or INVALID_SOCKET. This
      // exists so that Stop() can shut that socket down and thereby unblock a
      // worker sitting in a handshake, a read or a write, instead of waiting for a
      // peer that may never speak again. Without it, Stop() joins a thread whose
      // unblocking depends entirely on a remote client's behaviour, and a stalled
      // scrape would hold up service shutdown - which on this build means the
      // Windows service stop timing out and the process being killed mid-write.
      //
      // The mutex is what makes it safe, and the ordering rule is the whole point:
      // the worker publishes the socket under the lock after accept() and CLEARS
      // it under the lock BEFORE closing it. So a Stop() holding the lock either
      // sees a socket the worker has not closed yet, or sees INVALID_SOCKET; it
      // can never see a handle that has already been closed and possibly reissued
      // to an unrelated socket by another thread, which would make this a way to
      // shut down somebody else's connection. Only the worker ever calls
      // closesocket on it, so there is no double close either.
      std::mutex active_client_mutex_;
      SOCKET active_client_socket_;

      // Monotonic tick, used for the /healthz uptime field, and the wall-clock Unix
      // timestamp the same moment corresponds to, used for
      // hmailserver_start_time_seconds.
      ULONGLONG start_tick_count_;
      __int64 start_unix_time_;

      ULONGLONG queue_cache_tick_;
      int queue_depth_cache_value_;
      __int64 queue_oldest_age_cache_value_;

      ULONGLONG certificate_cache_tick_;
      AnsiString certificate_cache_body_;

      // The expected credentials, empty when the corresponding scheme is not
      // configured. The Basic pair is stored pre-joined as "username:password"
      // because that is exactly what a decoded Basic header contains, so the check
      // is one comparison over the whole credential rather than two comparisons
      // whose individual outcomes could be timed apart - and the user name is the
      // more guessable half, so it is the half that must not be separable.
      //
      // Held in plaintext rather than hashed. hMailServer.ini already holds the
      // database password in plaintext, so the confidentiality requirement on that
      // file is unchanged by this; and a hash in the ini would mean an operator
      // computing a digest by hand for a listener that has no administration UI.
      // What the process must never do is emit either value - no log line, no error
      // report and no metric carries them.
      AnsiString auth_token_;
      AnsiString auth_basic_credentials_;

      // Written by Start() before the worker thread exists and torn down by Stop()
      // after it has been joined, so the worker never sees any of the three change
      // and none of them needs a lock. Null tls_context_ means plain HTTP.
      std::shared_ptr<boost::asio::ssl::context> tls_context_;
      MetricsAvailability metrics_availability_;
   };
}
