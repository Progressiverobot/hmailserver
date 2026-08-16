// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// Dependency-free OpenTelemetry tracing. See OtelTracer.h.

#include "StdAfx.h"

#include "OtelTracer.h"

#include "../Application/IniFileSettings.h"

#include <ws2tcpip.h>
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
      endpoint_port_(4318),
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

      // Parse "http://host[:port][/path]". Only plain HTTP is supported (the
      // standard OTLP/HTTP collector port is 4318); TLS export is future work.
      AnsiString lower = endpoint;
      lower.MakeLower();
      if (lower.Find("http://") != 0)
      {
         LOG_APPLICATION("OtelTracer: OtelEndpoint must be an http:// URL; tracing disabled.");
         enabled_ = false;
         return;
      }

      AnsiString remainder = endpoint.Mid(7); // strip "http://"
      int slashPos = remainder.Find("/");
      AnsiString hostPort;
      if (slashPos < 0)
      {
         hostPort = remainder;
         endpoint_path_ = "/v1/traces";
      }
      else
      {
         hostPort = remainder.Mid(0, slashPos);
         endpoint_path_ = remainder.Mid(slashPos);
      }

      if (endpoint_path_.IsEmpty() || endpoint_path_ == "/")
         endpoint_path_ = "/v1/traces";

      int colonPos = hostPort.Find(":");
      if (colonPos < 0)
      {
         endpoint_host_ = hostPort;
         endpoint_port_ = 4318;
      }
      else
      {
         endpoint_host_ = hostPort.Mid(0, colonPos);
         endpoint_port_ = atoi(hostPort.Mid(colonPos + 1).c_str());
         if (endpoint_port_ <= 0 || endpoint_port_ > 65535)
            endpoint_port_ = 4318;
      }

      if (endpoint_host_.IsEmpty())
      {
         LOG_APPLICATION("OtelTracer: OtelEndpoint has no host; tracing disabled.");
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
         String(endpoint_host_).c_str(), endpoint_port_, String(endpoint_path_).c_str());
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
   OtelTracer::StartSpan(const AnsiString &name, int kind, const AnsiString &trace_id)
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
         // A new root span for the session trace.
         h.trace_id = trace_id;
         h.parent_span_id = "";
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

   AnsiString
   OtelTracer::JsonEscape_(const AnsiString &value)
   {
      AnsiString out;
      int len = value.GetLength();
      for (int i = 0; i < len; i++)
      {
         char c = value.GetAt(i);
         switch (c)
         {
         case '\"': out += "\\\""; break;
         case '\\': out += "\\\\"; break;
         case '\b': out += "\\b"; break;
         case '\f': out += "\\f"; break;
         case '\n': out += "\\n"; break;
         case '\r': out += "\\r"; break;
         case '\t': out += "\\t"; break;
         default:
            if (static_cast<unsigned char>(c) < 0x20)
            {
               AnsiString esc;
               esc.Format("\\u%04x", static_cast<unsigned int>(static_cast<unsigned char>(c)));
               out += esc;
            }
            else
            {
               out += c;
            }
         }
      }
      return out;
   }

   void
   OtelTracer::ExportBatch_(const std::vector<CompletedSpan> &spans)
   {
      AnsiString json;
      json += "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"";
      json += JsonEscape_(service_name_);
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
         json += JsonEscape_(s.name);
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
            json += JsonEscape_(s.attributes[a].key);
            json += "\",\"value\":{\"stringValue\":\"";
            json += JsonEscape_(s.attributes[a].value);
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
            json += JsonEscape_(s.events[e].name);
            json += "\"}";
         }
         json += "],";

         AnsiString status;
         status.Format("\"status\":{\"code\":%d}", s.ok ? 1 : 2);
         json += status;

         json += "}";
      }

      json += "]}]}]}";

      if (PostOtlp_(json))
      {
         last_post_failed_ = false;
      }
      else if (!last_post_failed_)
      {
         LOG_APPLICATION("OtelTracer: failed to export spans to the OTLP endpoint (further failures suppressed until the next success).");
         last_post_failed_ = true;
      }
   }

   bool
   OtelTracer::PostOtlp_(const AnsiString &json)
   {
      SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      if (sock == INVALID_SOCKET)
         return false;

      bool ok = false;

      do
      {
         sockaddr_in addr = {};
         addr.sin_family = AF_INET;
         addr.sin_port = htons(static_cast<unsigned short>(endpoint_port_));

         if (inet_pton(AF_INET, endpoint_host_.c_str(), &addr.sin_addr) != 1)
         {
            // Not a literal IPv4 address - resolve the host name.
            addrinfo hints = {};
            hints.ai_family = AF_INET;
            hints.ai_socktype = SOCK_STREAM;

            addrinfo *result = nullptr;
            if (getaddrinfo(endpoint_host_.c_str(), nullptr, &hints, &result) != 0 || result == nullptr)
               break;

            addr.sin_addr = reinterpret_cast<sockaddr_in*>(result->ai_addr)->sin_addr;
            freeaddrinfo(result);
         }

         DWORD timeout = 3000;
         setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
         setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));

         if (connect(sock, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR)
            break;

         AnsiString request;
         request.Format("POST %s HTTP/1.1\r\nHost: %s:%d\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n",
            endpoint_path_.c_str(), endpoint_host_.c_str(), endpoint_port_, json.GetLength());
         request += json;

         int total = request.GetLength();
         int sent = 0;
         const char *buffer = request.c_str();
         bool sendOk = true;
         while (sent < total)
         {
            int n = send(sock, buffer + sent, total - sent, 0);
            if (n == SOCKET_ERROR || n <= 0)
            {
               sendOk = false;
               break;
            }
            sent += n;
         }

         if (!sendOk)
            break;

         // Confirm a 2xx status line; the body is irrelevant to us.
         char response[256];
         int received = recv(sock, response, sizeof(response) - 1, 0);
         if (received > 0)
         {
            response[received] = 0;
            if (strstr(response, " 200") || strstr(response, " 202") || strstr(response, " 204"))
               ok = true;
         }
      } while (false);

      closesocket(sock);
      return ok;
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
