// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
//
// The OTLP logs signal: log entries forwarded in batches to an OTLP/HTTP
// (protobuf-over-JSON) collector. Enabled with OtelLogsEndpoint in
// hMailServer.ini; when disabled the entry point is a cheap no-op.
//
// Fed from the Logger's device chokepoint, so this exports exactly what the
// configured log mask already produces - the same categories, the same
// entries, no second rendering pipeline. An entry that the mask suppresses is
// suppressed here too; an ERROR entry reaches this exporter even when general
// logging is off, mirroring the error log's own always-written file. Records
// carry the active span's trace and span ids when one exists on the logging
// thread, which is what lets a backend show the log lines of one delivery
// under that delivery's trace.
//
// Follows the OtelTracer pattern: a Singleton with Start/Stop driven from
// Application::StartServers, a bounded drop-oldest queue, a worker std::thread
// batching to the shared OtelExportChannel transport.
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "OtelExportChannel.h"

#include <thread>
#include <deque>
#include <mutex>
#include <condition_variable>
#include <vector>

namespace HM
{
   class OtelLogExporter : public Singleton<OtelLogExporter>
   {
   public:
      OtelLogExporter();
      ~OtelLogExporter();

      // Reads OtelLogsEndpoint/OtelServiceName and, when an endpoint is
      // configured, starts the background exporter. Safe to call repeatedly
      // (Reinitialize).
      void Start();
      void Stop();

      bool IsEnabled() const { return enabled_; }

      // Queues one log entry for export. Called from the Logger with the
      // pre-rendering fields, so the exporter formats OTLP without caring which
      // file format the log files use. A no-op when disabled, and a no-op on
      // this exporter's own worker thread - the failure log line a failed POST
      // produces must not queue itself for the next POST.
      void OnLogEntry(const String &category, long thread, int session,
                      const String &remote_host, const String &message);

   private:
      struct LogRecord
      {
         unsigned __int64 time_unix_nano = 0;
         int severity_number = 9;      // OTLP INFO
         AnsiString severity_text;
         AnsiString category;
         long thread = 0;
         int session = -1;
         AnsiString remote_host;
         AnsiString body;
         AnsiString trace_id;          // empty when no span was active
         AnsiString span_id;
      };

      void Run_();
      void ExportBatch_(const std::vector<LogRecord> &records);

      bool enabled_;
      OtelExportChannel channel_;
      AnsiString service_name_;
      bool last_post_failed_;

      std::thread worker_;
      std::thread::id worker_thread_id_;
      bool running_;
      std::mutex queue_mutex_;
      std::condition_variable queue_cv_;
      std::deque<LogRecord> queue_;
   };
}
