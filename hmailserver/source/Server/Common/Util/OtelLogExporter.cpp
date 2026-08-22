// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// OTLP log export from the Logger chokepoint. See OtelLogExporter.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "OtelLogExporter.h"

#include "OtelTracer.h"

#include "../Application/IniFileSettings.h"

#include <chrono>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   OtelLogExporter::OtelLogExporter() :
      enabled_(false),
      service_name_("hmailserver"),
      last_post_failed_(false),
      running_(false)
   {

   }

   OtelLogExporter::~OtelLogExporter()
   {
      Stop();
   }

   void
   OtelLogExporter::Start()
   {
      // Reinitialize re-runs the server start sequence; reset cleanly each time.
      Stop();

      AnsiString endpoint = IniFileSettings::Instance()->GetOtelLogsEndpoint();
      if (endpoint.IsEmpty())
      {
         enabled_ = false;
         return;
      }

      AnsiString configureError;
      if (!channel_.Configure(endpoint, "/v1/logs", configureError))
      {
         String message;
         message.Format(_T("OtelLogExporter: OtelLogsEndpoint %s; log export disabled."),
            String(configureError).c_str());
         LOG_APPLICATION(message);
         enabled_ = false;
         return;
      }

      service_name_ = IniFileSettings::Instance()->GetOtelServiceName();
      if (service_name_.IsEmpty())
         service_name_ = "hmailserver";

      last_post_failed_ = false;
      running_ = true;
      worker_ = std::thread(&OtelLogExporter::Run_, this);
      worker_thread_id_ = worker_.get_id();

      // Enabled only once the worker's id is published, so the self-exclusion
      // check in OnLogEntry can never race against a half-started exporter.
      enabled_ = true;

      String message;
      message.Format(_T("OtelLogExporter: exporting log records to http://%s:%d%s."),
         String(channel_.GetHost()).c_str(), channel_.GetPort(), String(channel_.GetPath()).c_str());
      LOG_APPLICATION(message);
   }

   void
   OtelLogExporter::Stop()
   {
      if (!running_)
      {
         enabled_ = false;
         return;
      }

      enabled_ = false;

      {
         std::lock_guard<std::mutex> lock(queue_mutex_);
         running_ = false;
      }
      queue_cv_.notify_all();

      if (worker_.joinable())
         worker_.join();
   }

   void
   OtelLogExporter::OnLogEntry(const String &category, long thread, int session,
                               const String &remote_host, const String &message)
   {
      if (!enabled_)
         return;

      // The exporter's own thread logs its failure line through the Logger. That
      // line must not be queued: a dead collector would otherwise grow one
      // failure record per failed batch, each batch carrying the failures of the
      // previous one.
      if (std::this_thread::get_id() == worker_thread_id_)
         return;

      LogRecord record;
      record.time_unix_nano = OtelTracer::UnixNanoNow();
      record.category = AnsiString(category);
      record.thread = thread;
      record.session = session;
      record.remote_host = AnsiString(remote_host);
      record.body = AnsiString(message);

      // Severity from the category: the Logger's own signal for what an entry
      // is. ERROR is the error log; DEBUG and TCPIP are diagnostic chatter;
      // everything else (APPLICATION and the protocol conversations) is the
      // ordinary operational record.
      if (record.category == "ERROR")
      {
         record.severity_number = 17;
         record.severity_text = "ERROR";
      }
      else if (record.category == "DEBUG" || record.category == "TCPIP")
      {
         record.severity_number = 5;
         record.severity_text = "DEBUG";
      }
      else
      {
         record.severity_number = 9;
         record.severity_text = "INFO";
      }

      // Correlate with the active span on this thread, when there is one: a
      // protocol log line written while a command span is open belongs to that
      // command's trace.
      OtelTracer::Instance()->GetCurrentThreadContext(record.trace_id, record.span_id);

      {
         std::lock_guard<std::mutex> lock(queue_mutex_);
         // Bound memory: drop the oldest record if the exporter is falling behind.
         if (queue_.size() >= 4096)
            queue_.pop_front();
         queue_.push_back(record);
      }
      queue_cv_.notify_one();
   }

   void
   OtelLogExporter::Run_()
   {
      for (;;)
      {
         std::vector<LogRecord> batch;

         {
            std::unique_lock<std::mutex> lock(queue_mutex_);
            queue_cv_.wait_for(lock, std::chrono::milliseconds(1000),
               [this] { return !running_ || !queue_.empty(); });

            if (!running_ && queue_.empty())
               return;

            while (!queue_.empty() && batch.size() < 512)
            {
               batch.push_back(queue_.front());
               queue_.pop_front();
            }
         }

         if (!batch.empty())
            ExportBatch_(batch);
      }
   }

   void
   OtelLogExporter::ExportBatch_(const std::vector<LogRecord> &records)
   {
      // The resource block matches the traces signal's exactly, so a backend
      // attributes both signals to the same service.
      AnsiString json;
      json += "{\"resourceLogs\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"";
      json += OtelExportChannel::JsonEscape(service_name_);
      json += "\"}}]},\"scopeLogs\":[{\"scope\":{\"name\":\"hmailserver\"},\"logRecords\":[";

      for (size_t i = 0; i < records.size(); i++)
      {
         const LogRecord &record = records[i];
         if (i > 0)
            json += ",";

         AnsiString fields;
         fields.Format("{\"timeUnixNano\":\"%I64u\",\"severityNumber\":%d,\"severityText\":\"%s\","
            "\"body\":{\"stringValue\":\"",
            record.time_unix_nano, record.severity_number, record.severity_text.c_str());
         json += fields;
         json += OtelExportChannel::JsonEscape(record.body);
         json += "\"},";

         json += "\"attributes\":[{\"key\":\"log.category\",\"value\":{\"stringValue\":\"";
         json += OtelExportChannel::JsonEscape(record.category);
         json += "\"}}";

         AnsiString threadAttribute;
         threadAttribute.Format(",{\"key\":\"thread.id\",\"value\":{\"stringValue\":\"%ld\"}}", record.thread);
         json += threadAttribute;

         if (record.session >= 0)
         {
            AnsiString sessionAttribute;
            sessionAttribute.Format(",{\"key\":\"hmailserver.session.id\",\"value\":{\"stringValue\":\"%d\"}}",
               record.session);
            json += sessionAttribute;
         }

         if (!record.remote_host.IsEmpty())
         {
            json += ",{\"key\":\"client.address\",\"value\":{\"stringValue\":\"";
            json += OtelExportChannel::JsonEscape(record.remote_host);
            json += "\"}}";
         }

         json += "]";

         if (!record.trace_id.IsEmpty())
         {
            json += ",\"traceId\":\"";
            json += record.trace_id;
            json += "\",\"spanId\":\"";
            json += record.span_id;
            json += "\"";
         }

         json += "}";
      }

      json += "]}]}]}";

      if (channel_.PostJson(json))
      {
         last_post_failed_ = false;
      }
      else if (!last_post_failed_)
      {
         LOG_APPLICATION("OtelLogExporter: failed to export log records to the OTLP endpoint (further failures suppressed until the next success).");
         last_post_failed_ = true;
      }
   }
}
