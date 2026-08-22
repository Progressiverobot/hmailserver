// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
// https://www.progressiverobot.com
//
// The OTLP metrics signal: a periodic push of the SAME counters the Prometheus
// /metrics endpoint scrapes, to an OTLP/HTTP (protobuf-over-JSON) collector.
// Enabled with OtelMetricsEndpoint in hMailServer.ini; when disabled every
// entry point is a cheap no-op, and nothing anywhere counts anything for this
// exporter's sake.
//
// That last point is the design rule of this file: every number exported here
// is read from ServerStatus, the one place the counters live, exactly as
// MetricsServer::BuildMetricsBody_ reads them - same source, same metric
// names. Two independent tallies of the same event drift, and then two
// dashboards disagree with no way to tell which is lying. The metrics that do
// NOT appear here (delivery-queue depth, database probe results, certificate
// expiry) are the ones the metrics listener's refresher computes from the
// database and the file system; they belong to that listener's caching
// machinery and duplicating it here would be exactly the second tally this
// rule forbids.
//
// Follows the OtelTracer pattern: a Singleton with Start/Stop driven from
// Application::StartServers, a worker std::thread, and the shared
// OtelExportChannel transport.

#pragma once

#include "OtelExportChannel.h"
#include "ServerStatus.h"

#include <thread>
#include <mutex>
#include <condition_variable>
#include <utility>
#include <vector>

namespace HM
{
   class OtelMetricsExporter : public Singleton<OtelMetricsExporter>
   {
   public:
      OtelMetricsExporter();
      ~OtelMetricsExporter();

      // Reads OtelMetricsEndpoint/OtelMetricsInterval/OtelServiceName and, when
      // an endpoint is configured, starts the background exporter. Safe to call
      // repeatedly (Reinitialize).
      void Start();
      void Stop();

      bool IsEnabled() const { return enabled_; }

   private:
      void Run_();
      void ExportOnce_();
      AnsiString BuildExportJson_();

      // One cumulative monotonic sum data point, no attributes.
      void AppendSum_(AnsiString &json, bool &first, const char *name, unsigned __int64 value);

      // One gauge metric with a series per (attribute value, value) pair.
      void AppendGauge_(AnsiString &json, bool &first, const char *name,
                        const std::vector<std::pair<AnsiString, __int64>> &series,
                        const char *attribute_key);

      // One cumulative histogram from a ServerStatus latency snapshot. The
      // snapshot's buckets are cumulative (Prometheus-shaped); OTLP wants
      // per-bucket counts, so they are differenced here, and the microsecond
      // sum becomes seconds to match the "s" unit.
      void AppendHistogram_(AnsiString &json, bool &first, const char *name,
                            const std::vector<double> &bounds,
                            const ServerStatus::LatencySnapshot &snapshot);

      bool enabled_;
      OtelExportChannel channel_;
      AnsiString service_name_;
      int interval_seconds_;
      bool last_post_failed_;

      // Stamped when the exporter starts; every cumulative data point carries it
      // as startTimeUnixNano so a collector can tell a restart from a reset.
      unsigned __int64 start_unix_nano_;

      std::thread worker_;
      bool running_;
      std::mutex wakeup_mutex_;
      std::condition_variable wakeup_cv_;
   };
}
