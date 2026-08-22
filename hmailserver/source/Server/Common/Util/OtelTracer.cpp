// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// Dependency-free OpenTelemetry tracing. See OtelTracer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "OtelTracer.h"

#include "OtelTraceContext.h"

#include "../Application/IniFileSettings.h"

#include <random>
#include <chrono>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // Per-thread stack of in-flight spans, so a child span (e.g. a DB query issued
   // while a protocol command is being processed) parents to the active span on the
   // same thread without any explicit context plumbing.
   static std::vector<OtelSpanHandle> &
   ActiveSpans_()
   {
      static thread_local std::vector<OtelSpanHandle> stack;
      return stack;
   }

   static std::mt19937_64 &
   Rng_()
   {
      static thread_local std::mt19937_64 rng(
         static_cast<unsigned __int64>(std::random_device()()) ^
         (static_cast<unsigned __int64>(GetCurrentThreadId()) << 32) ^
         static_cast<unsigned __int64>(GetTickCount64()));
      return rng;
   }

   OtelTracer::OtelTracer() :
      enabled_(false),
      service_name_("hmailserver"),
      last_post_failed_(false),
      running_(false)
   {

   }

   OtelTracer::~OtelTracer()
   {
      Stop();
   }

   void
   OtelTracer::Start()
   {
      // Reinitialize re-runs the server start sequence; reset cleanly each time.
      Stop();

      AnsiString endpoint = IniFileSettings::Instance()->GetOtelEndpoint();
      if (endpoint.IsEmpty())
      {
         enabled_ = false;
         return;
      }

      AnsiString configureError;
      if (!channel_.Configure(endpoint, "/v1/traces", configureError))
      {
         String message;
         message.Format(_T("OtelTracer: OtelEndpoint %s; tracing disabled."), String(configureError).c_str());
         LOG_APPLICATION(message);
         enabled_ = false;
         return;
      }

      service_name_ = IniFileSettings::Instance()->GetOtelServiceName();
      if (service_name_.IsEmpty())
         service_name_ = "hmailserver";

      last_post_failed_ = false;
      running_ = true;
      enabled_ = true;
      worker_ = std::thread(&OtelTracer::Run_, this);

      String message;
      message.Format(_T("OtelTracer: exporting spans to http://%s:%d%s."),
         String(channel_.GetHost()).c_str(), channel_.GetPort(), String(channel_.GetPath()).c_str());
      LOG_APPLICATION(message);
   }

   void
   OtelTracer::Stop()
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

   AnsiString
   OtelTracer::NewTraceId()
   {
      unsigned __int64 a = Rng_()();
      unsigned __int64 b = Rng_()();
      AnsiString s;
      s.Format("%016I64x%016I64x", a, b);
      return s;
   }

   AnsiString
   OtelTracer::NewSpanId()
   {
      unsigned __int64 a = Rng_()();
      AnsiString s;
      s.Format("%016I64x", a);
      return s;
   }

   unsigned __int64
   OtelTracer::NowUnixNano_()
   {
      FILETIME ft;
      GetSystemTimeAsFileTime(&ft);
      unsigned __int64 t = (static_cast<unsigned __int64>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
      t -= 116444736000000000ULL; // 1601-01-01 -> 1970-01-01 in 100ns ticks
      return t * 100ULL;          // 100ns ticks -> nanoseconds
   }

   unsigned __int64
   OtelTracer::UnixNanoNow()
   {
      return NowUnixNano_();
   }

   OtelSpanHandle
   OtelTracer::StartSpan(const AnsiString &name, int kind, const AnsiString &trace_id,
                         const AnsiString &parent_span_id)
   {
      OtelSpanHandle h;
      if (!enabled_)
         return h;

      h.active = true;
      h.name = name;
      h.kind = kind;
      h.span_id = NewSpanId();
      h.start_unix_nano = NowUnixNano_();

      std::vector<OtelSpanHandle> &stack = ActiveSpans_();
      if (!trace_id.IsEmpty())
      {
         // A new root span for the session trace - parented to a remote span
         // when the caller carried a validated inbound trace context.
         h.trace_id = trace_id;
         h.parent_span_id = parent_span_id;
      }
      else if (!stack.empty())
      {
         h.trace_id = stack.back().trace_id;
         h.parent_span_id = stack.back().span_id;
      }
      else
      {
         h.trace_id = NewTraceId();
         h.parent_span_id = "";
      }

      stack.push_back(h);
      return h;
   }

   void
   OtelTracer::EndSpan(const OtelSpanHandle &handle, bool ok, const std::vector<OtelAttribute> &attributes,
                       const std::vector<OtelEvent> &events)
   {
      if (!handle.active)
         return;

      // Pop the thread-local stack back to (and including) this span. Defensive
      // unwinding in case a nested span was left unbalanced.
      std::vector<OtelSpanHandle> &stack = ActiveSpans_();
      while (!stack.empty() && stack.back().span_id != handle.span_id)
         stack.pop_back();
      if (!stack.empty())
         stack.pop_back();

      if (!enabled_)
         return;

      CompletedSpan cs;
      cs.trace_id = handle.trace_id;
      cs.span_id = handle.span_id;
      cs.parent_span_id = handle.parent_span_id;
      cs.name = handle.name;
      cs.kind = handle.kind;
      cs.start_unix_nano = handle.start_unix_nano;
      cs.end_unix_nano = NowUnixNano_();
      cs.attributes = attributes;
      cs.events = events;
      cs.ok = ok;

      Enqueue_(cs);
   }

   void
   OtelTracer::RecordCompletedSpan(const AnsiString &name, int kind, unsigned __int64 duration_micros,
                                   const std::vector<OtelAttribute> &attributes)
   {
      if (!enabled_)
         return;

      CompletedSpan cs;
      cs.span_id = NewSpanId();

      std::vector<OtelSpanHandle> &stack = ActiveSpans_();
      if (!stack.empty())
      {
         cs.trace_id = stack.back().trace_id;
         cs.parent_span_id = stack.back().span_id;
      }
      else
      {
         cs.trace_id = NewTraceId();
         cs.parent_span_id = "";
      }

      cs.name = name;
      cs.kind = kind;
      cs.end_unix_nano = NowUnixNano_();
      cs.start_unix_nano = cs.end_unix_nano - duration_micros * 1000ULL;
      cs.attributes = attributes;
      cs.ok = true;

      Enqueue_(cs);
   }

   void
   OtelTracer::RecordLinkedSpan(const AnsiString &name, int kind, const AnsiString &trace_id,
                                const AnsiString &span_id, const AnsiString &parent_span_id,
                                const std::vector<OtelAttribute> &attributes)
   {
      if (!enabled_)
         return;

      CompletedSpan cs;
      cs.trace_id = trace_id;
      cs.span_id = span_id;
      cs.parent_span_id = parent_span_id;
      cs.name = name;
      cs.kind = kind;
      cs.end_unix_nano = NowUnixNano_();
      cs.start_unix_nano = cs.end_unix_nano;
      cs.attributes = attributes;
      cs.ok = true;

      Enqueue_(cs);
   }

   bool
   OtelTracer::GetCurrentThreadContext(AnsiString &trace_id, AnsiString &span_id) const
   {
      if (!enabled_)
         return false;

      std::vector<OtelSpanHandle> &stack = ActiveSpans_();
      if (stack.empty())
         return false;

      trace_id = stack.back().trace_id;
      span_id = stack.back().span_id;
      return true;
   }

   void
   OtelTracer::Enqueue_(const CompletedSpan &span)
   {
      {
         std::lock_guard<std::mutex> lock(queue_mutex_);
         // Bound memory: drop the oldest span if the exporter is falling behind.
         if (queue_.size() >= 4096)
            queue_.pop_front();
         queue_.push_back(span);
      }
      queue_cv_.notify_one();
   }

   void
   OtelTracer::Run_()
   {
      for (;;)
      {
         std::vector<CompletedSpan> batch;

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
   OtelTracer::ExportBatch_(const std::vector<CompletedSpan> &spans)
   {
      AnsiString json;
      json += "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"";
      json += OtelExportChannel::JsonEscape(service_name_);
      json += "\"}}]},\"scopeSpans\":[{\"scope\":{\"name\":\"hmailserver\"},\"spans\":[";

      for (size_t i = 0; i < spans.size(); i++)
      {
         const CompletedSpan &s = spans[i];
         if (i > 0)
            json += ",";

         json += "{\"traceId\":\"";
         json += s.trace_id;
         json += "\",\"spanId\":\"";
         json += s.span_id;
         json += "\",";

         if (!s.parent_span_id.IsEmpty())
         {
            json += "\"parentSpanId\":\"";
            json += s.parent_span_id;
            json += "\",";
         }

         json += "\"name\":\"";
         json += OtelExportChannel::JsonEscape(s.name);
         json += "\",";

         AnsiString fields;
         fields.Format("\"kind\":%d,\"startTimeUnixNano\":\"%I64u\",\"endTimeUnixNano\":\"%I64u\",",
            s.kind, s.start_unix_nano, s.end_unix_nano);
         json += fields;

         json += "\"attributes\":[";
         for (size_t a = 0; a < s.attributes.size(); a++)
         {
            if (a > 0)
               json += ",";
            json += "{\"key\":\"";
            json += OtelExportChannel::JsonEscape(s.attributes[a].key);
            json += "\",\"value\":{\"stringValue\":\"";
            json += OtelExportChannel::JsonEscape(s.attributes[a].value);
            json += "\"}}";
         }
         json += "],";

         json += "\"events\":[";
         for (size_t e = 0; e < s.events.size(); e++)
         {
            if (e > 0)
               json += ",";
            AnsiString ev;
            ev.Format("{\"timeUnixNano\":\"%I64u\",\"name\":\"", s.events[e].time_unix_nano);
            json += ev;
            json += OtelExportChannel::JsonEscape(s.events[e].name);
            json += "\"}";
         }
         json += "],";

         AnsiString status;
         status.Format("\"status\":{\"code\":%d}", s.ok ? 1 : 2);
         json += status;

         json += "}";
      }

      json += "]}]}]}";

      if (channel_.PostJson(json))
      {
         last_post_failed_ = false;
      }
      else if (!last_post_failed_)
      {
         LOG_APPLICATION("OtelTracer: failed to export spans to the OTLP endpoint (further failures suppressed until the next success).");
         last_post_failed_ = true;
      }
   }

   //
   // OtelSpanScope - RAII span guard.
   //

   OtelSpanScope::OtelSpanScope(const AnsiString &name, int kind, const AnsiString &trace_id) :
      active_(false),
      ok_(true)
   {
      if (OtelTracer::Instance()->IsEnabled())
      {
         handle_ = OtelTracer::Instance()->StartSpan(name, kind, trace_id);
         active_ = handle_.active;
      }
   }

   OtelSpanScope::OtelSpanScope(const AnsiString &name, int kind, const OtelTraceContext &context) :
      active_(false),
      ok_(true)
   {
      if (OtelTracer::Instance()->IsEnabled())
      {
         // A valid context continues the caller's trace under the caller's span;
         // an invalid one - absent, malformed, all-zero ids - starts a fresh
         // local trace, exactly as if no context had arrived at all.
         if (context.valid)
            handle_ = OtelTracer::Instance()->StartSpan(name, kind, context.trace_id, context.parent_span_id);
         else
            handle_ = OtelTracer::Instance()->StartSpan(name, kind, OtelTracer::NewTraceId());

         active_ = handle_.active;
      }
   }

   AnsiString
   OtelSpanScope::GetTraceparentValue() const
   {
      if (!active_)
         return "";

      // The sampled flag is this server's own recording decision - an active
      // span here IS recorded - never an echo of anything inbound.
      return OtelTraceContext::FormatTraceparent(handle_.trace_id, handle_.span_id, true);
   }

   OtelSpanScope::~OtelSpanScope()
   {
      if (active_)
         OtelTracer::Instance()->EndSpan(handle_, ok_, attributes_, events_);
   }

   void
   OtelSpanScope::AddAttribute(const AnsiString &key, const AnsiString &value)
   {
      if (!active_)
         return;

      OtelAttribute attribute;
      attribute.key = key;
      attribute.value = value;
      attributes_.push_back(attribute);
   }

   void
   OtelSpanScope::AddEvent(const AnsiString &name)
   {
      if (!active_)
         return;

      OtelEvent event;
      event.name = name;
      event.time_unix_nano = OtelTracer::UnixNanoNow();
      events_.push_back(event);
   }
}
