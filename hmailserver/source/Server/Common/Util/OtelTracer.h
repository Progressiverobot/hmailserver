// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com
//
// Dependency-free OpenTelemetry tracing. Spans are emitted at the points already
// instrumented for metrics (per-command dispatch in TCPConnection, the database
// query chokepoint in DatabaseConnectionManager) and exported in batches to an
// OTLP/HTTP (protobuf-over-JSON) collector. Enabled with OtelEndpoint in
// hMailServer.ini; when disabled every entry point is a cheap no-op.
//
// This deliberately follows the same self-contained pattern as MetricsServer /
// RestApiServer (raw sockets + a std::thread) rather than vendoring the
// opentelemetry-cpp SDK (which would drag protobuf/gRPC into the v145/ATL build).

#pragma once

#include <thread>
#include <deque>
#include <vector>
#include <mutex>
#include <condition_variable>

namespace HM
{
   // OTLP span kind (subset we emit).
   enum OtelSpanKind
   {
      OtelSpanKindInternal = 1,
      OtelSpanKindServer = 2,
      OtelSpanKindClient = 3,
   };

   // A single string-valued span attribute.
   struct OtelAttribute
   {
      AnsiString key;
      AnsiString value;
   };

   // A timestamped span event (a milestone recorded within a span's lifetime).
   struct OtelEvent
   {
      AnsiString name;
      unsigned __int64 time_unix_nano = 0;
   };

   // An in-flight span: its trace/span ids plus enough to emit it on completion.
   struct OtelSpanHandle
   {
      bool active = false;
      AnsiString trace_id;        // 32 lowercase hex chars
      AnsiString span_id;         // 16 lowercase hex chars
      AnsiString parent_span_id;  // 16 hex chars, or empty for a root span
      AnsiString name;
      int kind = OtelSpanKindInternal;
      unsigned __int64 start_unix_nano = 0;
   };

   class OtelTracer : public Singleton<OtelTracer>
   {
   public:
      OtelTracer();
      ~OtelTracer();

      // Reads OtelEndpoint/OtelServiceName and, when an endpoint is configured,
      // starts the background exporter. Safe to call repeatedly (Reinitialize).
      void Start();
      void Stop();

      bool IsEnabled() const { return enabled_; }

      // A fresh 128-bit trace id (32 lowercase hex) identifying one session/trace.
      static AnsiString NewTraceId();

      // Current wall-clock time as Unix nanoseconds (for stamping span events).
      static unsigned __int64 UnixNanoNow();

      // Starts a span as a child of the current thread-local active span. Pass a
      // trace_id to begin a new root span (e.g. the first command of a session).
      // Pushes the span onto the thread-local stack; pair with EndSpan.
      OtelSpanHandle StartSpan(const AnsiString &name, int kind, const AnsiString &trace_id);

      // Ends a span started with StartSpan: pops the thread-local stack and queues
      // the completed span for export, along with any attributes and span events.
      void EndSpan(const OtelSpanHandle &handle, bool ok, const std::vector<OtelAttribute> &attributes,
                   const std::vector<OtelEvent> &events = std::vector<OtelEvent>());

      // Records an already-timed span, parented to the current thread-local active
      // span if one exists (else a new root trace). Used at the DB chokepoint where
      // the work was already measured.
      void RecordCompletedSpan(const AnsiString &name, int kind, unsigned __int64 duration_micros,
                               const std::vector<OtelAttribute> &attributes);

   private:
      struct CompletedSpan
      {
         AnsiString trace_id;
         AnsiString span_id;
         AnsiString parent_span_id;
         AnsiString name;
         int kind = OtelSpanKindInternal;
         unsigned __int64 start_unix_nano = 0;
         unsigned __int64 end_unix_nano = 0;
         std::vector<OtelAttribute> attributes;
         std::vector<OtelEvent> events;
         bool ok = true;
      };

      void Run_();
      void ExportBatch_(const std::vector<CompletedSpan> &spans);
      bool PostOtlp_(const AnsiString &json);
      void Enqueue_(const CompletedSpan &span);

      static AnsiString NewSpanId();
      static unsigned __int64 NowUnixNano_();
      static AnsiString JsonEscape_(const AnsiString &value);

      bool enabled_;
      AnsiString endpoint_host_;
      int endpoint_port_;
      AnsiString endpoint_path_;
      AnsiString service_name_;
      bool last_post_failed_;

      std::thread worker_;
      bool running_;
      std::mutex queue_mutex_;
      std::condition_variable queue_cv_;
      std::deque<CompletedSpan> queue_;
   };

   // RAII helper: starts a span on construction and ends it on scope exit (so every
   // throw/return path is covered). A no-op when tracing is disabled.
   class OtelSpanScope
   {
   public:
      OtelSpanScope(const AnsiString &name, int kind, const AnsiString &trace_id);
      ~OtelSpanScope();

      void SetOk(bool ok) { ok_ = ok; }
      void AddAttribute(const AnsiString &key, const AnsiString &value);

      // Records a milestone event on the span (no-op when tracing is disabled).
      void AddEvent(const AnsiString &name);

   private:
      OtelSpanScope(const OtelSpanScope &);
      OtelSpanScope &operator=(const OtelSpanScope &);

      OtelSpanHandle handle_;
      bool active_;
      bool ok_;
      std::vector<OtelAttribute> attributes_;
      std::vector<OtelEvent> events_;
   };
}
