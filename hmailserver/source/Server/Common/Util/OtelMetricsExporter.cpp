// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// OTLP metrics export from the shared ServerStatus counters. See
// OtelMetricsExporter.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "OtelMetricsExporter.h"

#include "OtelTracer.h"

#include "../Application/IniFileSettings.h"
#include "../TCPIP/SocketConstants.h"

#include <chrono>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Bounds on the export interval. The floor keeps a typo'd "1" from turning
      // the exporter into a load generator against the collector; the ceiling
      // keeps a typo'd large value from looking like a broken exporter.
      const int MinIntervalSeconds = 5;
      const int MaxIntervalSeconds = 3600;
   }

   OtelMetricsExporter::OtelMetricsExporter() :
      enabled_(false),
      service_name_("hmailserver"),
      interval_seconds_(60),
      last_post_failed_(false),
      start_unix_nano_(0),
      running_(false)
   {

   }

   OtelMetricsExporter::~OtelMetricsExporter()
   {
      Stop();
   }

   void
   OtelMetricsExporter::Start()
   {
      // Reinitialize re-runs the server start sequence; reset cleanly each time.
      Stop();

      AnsiString endpoint = IniFileSettings::Instance()->GetOtelMetricsEndpoint();
      if (endpoint.IsEmpty())
      {
         enabled_ = false;
         return;
      }

      AnsiString configureError;
      if (!channel_.Configure(endpoint, "/v1/metrics", configureError))
      {
         String message;
         message.Format(_T("OtelMetricsExporter: OtelMetricsEndpoint %s; metrics export disabled."),
            String(configureError).c_str());
         LOG_APPLICATION(message);
         enabled_ = false;
         return;
      }

      service_name_ = IniFileSettings::Instance()->GetOtelServiceName();
      if (service_name_.IsEmpty())
         service_name_ = "hmailserver";

      interval_seconds_ = IniFileSettings::Instance()->GetOtelMetricsInterval();
      if (interval_seconds_ < MinIntervalSeconds)
         interval_seconds_ = MinIntervalSeconds;
      if (interval_seconds_ > MaxIntervalSeconds)
         interval_seconds_ = MaxIntervalSeconds;

      start_unix_nano_ = OtelTracer::UnixNanoNow();
      last_post_failed_ = false;
      running_ = true;
      enabled_ = true;
      worker_ = std::thread(&OtelMetricsExporter::Run_, this);

      String message;
      message.Format(_T("OtelMetricsExporter: exporting metrics to http://%s:%d%s every %d seconds."),
         String(channel_.GetHost()).c_str(), channel_.GetPort(), String(channel_.GetPath()).c_str(),
         interval_seconds_);
      LOG_APPLICATION(message);
   }

   void
   OtelMetricsExporter::Stop()
   {
      if (!running_)
      {
         enabled_ = false;
         return;
      }

      enabled_ = false;

      {
         std::lock_guard<std::mutex> lock(wakeup_mutex_);
         running_ = false;
      }
      wakeup_cv_.notify_all();

      if (worker_.joinable())
         worker_.join();
   }

   void
   OtelMetricsExporter::Run_()
   {
      for (;;)
      {
         {
            std::unique_lock<std::mutex> lock(wakeup_mutex_);
            wakeup_cv_.wait_for(lock, std::chrono::seconds(interval_seconds_),
               [this] { return !running_; });

            if (!running_)
               return;
         }

         ExportOnce_();
      }
   }

   void
   OtelMetricsExporter::ExportOnce_()
   {
      AnsiString json = BuildExportJson_();

      if (channel_.PostJson(json))
      {
         last_post_failed_ = false;
      }
      else if (!last_post_failed_)
      {
         LOG_APPLICATION("OtelMetricsExporter: failed to export metrics to the OTLP endpoint (further failures suppressed until the next success).");
         last_post_failed_ = true;
      }
   }

   void
   OtelMetricsExporter::AppendSum_(AnsiString &json, bool &first, const char *name, unsigned __int64 value)
   {
      if (!first)
         json += ",";
      first = false;

      AnsiString metric;
      metric.Format("{\"name\":\"%s\",\"unit\":\"1\",\"sum\":{\"aggregationTemporality\":2,\"isMonotonic\":true,"
         "\"dataPoints\":[{\"startTimeUnixNano\":\"%I64u\",\"timeUnixNano\":\"%I64u\",\"asInt\":\"%I64u\"}]}}",
         name, start_unix_nano_, OtelTracer::UnixNanoNow(), value);
      json += metric;
   }

   void
   OtelMetricsExporter::AppendGauge_(AnsiString &json, bool &first, const char *name,
                                     const std::vector<std::pair<AnsiString, __int64>> &series,
                                     const char *attribute_key)
   {
      if (!first)
         json += ",";
      first = false;

      AnsiString metric;
      metric.Format("{\"name\":\"%s\",\"unit\":\"1\",\"gauge\":{\"dataPoints\":[", name);
      json += metric;

      unsigned __int64 now = OtelTracer::UnixNanoNow();

      for (size_t i = 0; i < series.size(); i++)
      {
         if (i > 0)
            json += ",";

         AnsiString point;
         if (attribute_key && !series[i].first.IsEmpty())
         {
            point.Format("{\"timeUnixNano\":\"%I64u\",\"asInt\":\"%I64d\","
               "\"attributes\":[{\"key\":\"%s\",\"value\":{\"stringValue\":\"%s\"}}]}",
               now, series[i].second, attribute_key,
               OtelExportChannel::JsonEscape(series[i].first).c_str());
         }
         else
         {
            point.Format("{\"timeUnixNano\":\"%I64u\",\"asInt\":\"%I64d\"}", now, series[i].second);
         }
         json += point;
      }

      json += "]}}";
   }

   void
   OtelMetricsExporter::AppendHistogram_(AnsiString &json, bool &first, const char *name,
                                         const std::vector<double> &bounds,
                                         const ServerStatus::LatencySnapshot &snapshot)
   {
      if (!first)
         json += ",";
      first = false;

      AnsiString metric;
      metric.Format("{\"name\":\"%s\",\"unit\":\"s\",\"histogram\":{\"aggregationTemporality\":2,"
         "\"dataPoints\":[{\"startTimeUnixNano\":\"%I64u\",\"timeUnixNano\":\"%I64u\",\"count\":\"%I64u\",\"sum\":%.6f,",
         name, start_unix_nano_, OtelTracer::UnixNanoNow(), snapshot.count,
         static_cast<double>(snapshot.microseconds_total) / 1000000.0);
      json += metric;

      // OTLP bucket counts are per-bucket where the snapshot's are cumulative
      // (the +Inf entry equals the count); difference adjacent entries. The
      // counts are read out of one snapshot taken under one lock, so the
      // differences can never go negative.
      json += "\"bucketCounts\":[";
      unsigned __int64 previous = 0;
      for (size_t i = 0; i < snapshot.cumulative_buckets.size(); i++)
      {
         if (i > 0)
            json += ",";
         AnsiString bucketCount;
         bucketCount.Format("\"%I64u\"", snapshot.cumulative_buckets[i] - previous);
         previous = snapshot.cumulative_buckets[i];
         json += bucketCount;
      }
      json += "],";

      json += "\"explicitBounds\":[";
      for (size_t i = 0; i < bounds.size(); i++)
      {
         if (i > 0)
            json += ",";
         AnsiString bound;
         bound.Format("%g", bounds[i]);
         json += bound;
      }
      json += "]}]}}";
   }

   AnsiString
   OtelMetricsExporter::BuildExportJson_()
   {
      ServerStatus *status = ServerStatus::Instance();

      // The resource block matches the traces signal's exactly, so a backend
      // attributes both signals to the same service.
      AnsiString json;
      json += "{\"resourceMetrics\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"";
      json += OtelExportChannel::JsonEscape(service_name_);
      json += "\"}}]},\"scopeMetrics\":[{\"scope\":{\"name\":\"hmailserver\"},\"metrics\":[";

      bool first = true;

      // The metric names are the Prometheus names, verbatim. An operator who has
      // both a scrape and a push configured sees ONE name per fact, and a
      // dashboard built against either transport works against the other.
      AppendSum_(json, first, "hmailserver_processed_messages_total",
         (unsigned __int64) status->GetNumberOfProcessedMessages());
      AppendSum_(json, first, "hmailserver_spam_messages_total",
         (unsigned __int64) status->GetNumberOfDetectedSpamMessages());
      AppendSum_(json, first, "hmailserver_viruses_removed_total",
         (unsigned __int64) status->GetNumberOfRemovedViruses());
      AppendSum_(json, first, "hmailserver_tls_handshakes_total",
         (unsigned __int64) status->GetNumberOfTlsHandshakesCompleted());
      AppendSum_(json, first, "hmailserver_tls_handshake_failures_total",
         (unsigned __int64) status->GetNumberOfTlsHandshakeFailures());
      AppendSum_(json, first, "hmailserver_auth_success_total",
         (unsigned __int64) status->GetNumberOfAuthenticationsSucceeded());
      AppendSum_(json, first, "hmailserver_auth_failures_total",
         (unsigned __int64) status->GetNumberOfAuthenticationFailures());
      AppendSum_(json, first, "hmailserver_metrics_unauthorized_requests_total",
         status->GetMetricsUnauthorizedRequestsCount());
      AppendSum_(json, first, "hmailserver_messages_delivered_total",
         (unsigned __int64) status->GetNumberOfMessagesDelivered());
      AppendSum_(json, first, "hmailserver_messages_deferred_total",
         (unsigned __int64) status->GetNumberOfMessagesDeferred());
      AppendSum_(json, first, "hmailserver_messages_bounced_total",
         (unsigned __int64) status->GetNumberOfMessagesBounced());
      AppendSum_(json, first, "hmailserver_db_slow_queries_total",
         status->GetDatabaseSlowQueriesCount());

      // Sessions per protocol, one gauge with a protocol attribute - the OTLP
      // shape of hmailserver_sessions{protocol="..."}.
      std::vector<std::pair<AnsiString, __int64>> sessions;
      sessions.push_back(std::make_pair(AnsiString("smtp"), (__int64) status->GetNumberOfSessions(STSMTP)));
      sessions.push_back(std::make_pair(AnsiString("imap"), (__int64) status->GetNumberOfSessions(STIMAP)));
      sessions.push_back(std::make_pair(AnsiString("pop3"), (__int64) status->GetNumberOfSessions(STPOP3)));
      AppendGauge_(json, first, "hmailserver_sessions", sessions, "protocol");

      std::vector<std::pair<AnsiString, __int64>> missingFiles;
      missingFiles.push_back(std::make_pair(AnsiString(), (__int64) status->GetMessageStoreMissingFiles()));
      AppendGauge_(json, first, "hmailserver_messagestore_missing_files", missingFiles, nullptr);

      // Per-domain counters. Cardinality is bounded by construction, exactly as
      // on the scrape side: only locally hosted domains are ever labelled.
      std::map<String, __int64> domainsReceived = status->GetDomainMessagesReceived();
      if (!domainsReceived.empty())
      {
         if (!first)
            json += ",";
         first = false;

         json += "{\"name\":\"hmailserver_domain_messages_received_total\",\"unit\":\"1\","
            "\"sum\":{\"aggregationTemporality\":2,\"isMonotonic\":true,\"dataPoints\":[";

         bool firstPoint = true;
         for (auto iter = domainsReceived.begin(); iter != domainsReceived.end(); ++iter)
         {
            if (!firstPoint)
               json += ",";
            firstPoint = false;

            AnsiString point;
            point.Format("{\"startTimeUnixNano\":\"%I64u\",\"timeUnixNano\":\"%I64u\",\"asInt\":\"%I64d\","
               "\"attributes\":[{\"key\":\"domain\",\"value\":{\"stringValue\":\"%s\"}}]}",
               start_unix_nano_, OtelTracer::UnixNanoNow(), iter->second,
               OtelExportChannel::JsonEscape(AnsiString(iter->first)).c_str());
            json += point;
         }

         json += "]}}";
      }

      std::map<String, __int64> domainsSent = status->GetDomainMessagesSent();
      if (!domainsSent.empty())
      {
         if (!first)
            json += ",";
         first = false;

         json += "{\"name\":\"hmailserver_domain_messages_sent_total\",\"unit\":\"1\","
            "\"sum\":{\"aggregationTemporality\":2,\"isMonotonic\":true,\"dataPoints\":[";

         bool firstPoint = true;
         for (auto iter = domainsSent.begin(); iter != domainsSent.end(); ++iter)
         {
            if (!firstPoint)
               json += ",";
            firstPoint = false;

            AnsiString point;
            point.Format("{\"startTimeUnixNano\":\"%I64u\",\"timeUnixNano\":\"%I64u\",\"asInt\":\"%I64d\","
               "\"attributes\":[{\"key\":\"domain\",\"value\":{\"stringValue\":\"%s\"}}]}",
               start_unix_nano_, OtelTracer::UnixNanoNow(), iter->second,
               OtelExportChannel::JsonEscape(AnsiString(iter->first)).c_str());
            json += point;
         }

         json += "]}}";
      }

      AppendHistogram_(json, first, "hmailserver_command_processing_seconds",
         ServerStatus::GetCommandLatencyBucketBounds(), status->GetCommandLatencySnapshot());
      AppendHistogram_(json, first, "hmailserver_db_query_seconds",
         ServerStatus::GetDatabaseLatencyBucketBounds(), status->GetDatabaseLatencySnapshot());

      json += "]}]}]}";
      return json;
   }
}
