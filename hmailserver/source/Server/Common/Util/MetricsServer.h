// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Minimal HTTP endpoint exposing server statistics in the Prometheus
// text exposition format. Enabled with MetricsServerPort in hMailServer.ini.

#pragma once

#include "ServerStatus.h"

#include <thread>
#include <map>
#include <vector>

namespace HM
{
   class MetricsServer
   {
   public:
      MetricsServer();
      ~MetricsServer();

      // Starts the listener. Returns false if the socket could not be bound.
      bool Start(const String &bind_address, int port);
      void Stop();

   private:

      void Run_();

      // Serves one request. Does NOT own the socket - Run_ closes it on both the
      // normal and the exceptional path, because everything in here (database reads,
      // certificate file parsing) sits under an exception barrier that must not leak
      // handles when it fires.
      void HandleClient_(SOCKET client_socket);

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
      static AnsiString BuildHttpResponse_(int status_code, const char *content_type, const AnsiString &body);

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;

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
   };
}
