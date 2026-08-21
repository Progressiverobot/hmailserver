// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// W3C Trace Context ingestion and propagation. See OtelTraceContext.h.

#include "StdAfx.h"

#include "OtelTraceContext.h"

#include "OtelTracer.h"
#include "File.h"
#include "ByteBuffer.h"

#include "../Mime/Mime.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // How much of a stored message file is searched for its header block. A
      // traceparent this server prepended is within the first few hundred bytes;
      // the bound exists so a hostile message with megabytes of headers cannot
      // make every delivery attempt read them all.
      const int MessageHeaderSearchLimit = 65536;

      // The ceiling on a tracestate value carried through (the W3C guidance
      // figure). Anything longer is dropped, not truncated: a truncated
      // tracestate is a corrupted one, and the header is optional anyway.
      const int MaxTraceStateLength = 512;
   }

   bool
   OtelTraceContext::IsLowercaseHex_(const AnsiString &value)
   {
      int length = value.GetLength();
      for (int i = 0; i < length; i++)
      {
         char c = value.GetAt(i);
         bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
         if (!isHex)
            return false;
      }
      return length > 0;
   }

   bool
   OtelTraceContext::IsAllZero_(const AnsiString &value)
   {
      for (int i = 0; i < value.GetLength(); i++)
      {
         if (value.GetAt(i) != '0')
            return false;
      }
      return true;
   }

   OtelTraceContext
   OtelTraceContext::TryParseTraceparent(const AnsiString &value)
   {
      OtelTraceContext context;

      AnsiString trimmed = value;
      trimmed.TrimLeft();
      trimmed.TrimRight();

      // Exactly the version-00 shape: "00-" + 32 hex + "-" + 16 hex + "-" + 2
      // hex = 55 characters. Only version 00 is accepted - a future version is
      // allowed to be longer, and guessing at how to read a format this build
      // has never seen is how a validator stops being one. The spec requires
      // lowercase hex, so uppercase is rejected rather than folded: folding
      // would accept a value the sender's own tooling will not.
      if (trimmed.GetLength() != 55)
         return context;

      if (trimmed.GetAt(0) != '0' || trimmed.GetAt(1) != '0' ||
          trimmed.GetAt(2) != '-' || trimmed.GetAt(35) != '-' || trimmed.GetAt(52) != '-')
         return context;

      AnsiString traceId = trimmed.Mid(3, 32);
      AnsiString spanId = trimmed.Mid(36, 16);
      AnsiString flags = trimmed.Mid(53, 2);

      if (!IsLowercaseHex_(traceId) || !IsLowercaseHex_(spanId) || !IsLowercaseHex_(flags))
         return context;

      // An all-zero trace id or span id is the spec's own "invalid" marker; a
      // context built from one would attach spans to a trace that does not
      // exist.
      if (IsAllZero_(traceId) || IsAllZero_(spanId))
         return context;

      char lowFlagNibble = flags.GetAt(1);
      int flagBits = (lowFlagNibble >= 'a') ? (lowFlagNibble - 'a' + 10) : (lowFlagNibble - '0');

      context.valid = true;
      context.trace_id = traceId;
      context.parent_span_id = spanId;
      context.sampled = (flagBits & 0x01) != 0;
      return context;
   }

   AnsiString
   OtelTraceContext::FormatTraceparent(const AnsiString &trace_id, const AnsiString &span_id, bool sampled)
   {
      AnsiString value;
      value.Format("00-%s-%s-%s", trace_id.c_str(), span_id.c_str(), sampled ? "01" : "00");
      return value;
   }

   bool
   OtelTraceContext::IsValidTraceState(const AnsiString &value)
   {
      int length = value.GetLength();
      if (length == 0 || length > MaxTraceStateLength)
         return false;

      // Printable ASCII only. tracestate is opaque vendor data and this is
      // deliberately not a full RFC grammar check - what matters here is that
      // nothing outside 0x20..0x7E can ride through, because this value is
      // re-emitted into a message header where a CR/LF would be an injection
      // and a control byte would be corruption.
      for (int i = 0; i < length; i++)
      {
         unsigned char c = static_cast<unsigned char>(value.GetAt(i));
         if (c < 0x20 || c > 0x7e)
            return false;
      }

      return true;
   }

   AnsiString
   OtelTraceContext::GetHeaderBlockValue_(const AnsiString &header_block, const char *field_name)
   {
      // Case-insensitive scan for the first "name:" at the start of a line.
      AnsiString needle = field_name;
      needle += ":";

      int searchFrom = 0;
      while (searchFrom <= header_block.GetLength() - needle.GetLength())
      {
         int position = header_block.FindNoCase(needle, searchFrom);
         if (position < 0)
            return "";

         bool atLineStart = position == 0 ||
            header_block.GetAt(position - 1) == '\n';

         if (!atLineStart)
         {
            searchFrom = position + 1;
            continue;
         }

         int valueStart = position + needle.GetLength();
         int lineEnd = header_block.Find("\n", valueStart);
         if (lineEnd < 0)
            lineEnd = header_block.GetLength();

         AnsiString value = header_block.Mid(valueStart, lineEnd - valueStart);
         value.TrimLeft();
         value.TrimRight(); // also removes a trailing \r
         return value;
      }

      return "";
   }

   OtelTraceContext
   OtelTraceContext::FromHttpRequest(const AnsiString &raw_request)
   {
      OtelTraceContext context;
      if (!OtelTracer::Instance()->IsEnabled())
         return context;

      // Only the header block: a request body may legitimately contain the
      // header name as text (a JSON payload about tracing, say).
      AnsiString headerBlock = raw_request;
      int headerEnd = headerBlock.Find("\r\n\r\n");
      if (headerEnd >= 0)
         headerBlock = headerBlock.Mid(0, headerEnd);

      AnsiString traceparent = GetHeaderBlockValue_(headerBlock, "traceparent");
      if (traceparent.IsEmpty())
         return context;

      context = TryParseTraceparent(traceparent);
      if (!context.valid)
         return context;

      AnsiString traceState = GetHeaderBlockValue_(headerBlock, "tracestate");
      if (IsValidTraceState(traceState))
         context.trace_state = traceState;

      return context;
   }

   OtelTraceContext
   OtelTraceContext::FromMessageHeaders(std::shared_ptr<MimeHeader> headers)
   {
      OtelTraceContext context;
      if (!headers)
         return context;

      const char *rawTraceparent = headers->GetRawFieldValue("traceparent");
      if (!rawTraceparent)
         return context;

      context = TryParseTraceparent(AnsiString(rawTraceparent));
      if (!context.valid)
         return context;

      const char *rawTraceState = headers->GetRawFieldValue("tracestate");
      if (rawTraceState)
      {
         AnsiString traceState = rawTraceState;
         traceState.TrimLeft();
         traceState.TrimRight();
         if (IsValidTraceState(traceState))
            context.trace_state = traceState;
      }

      return context;
   }

   OtelTraceContext
   OtelTraceContext::FromMessageFile(const String &file_name)
   {
      OtelTraceContext context;
      if (!OtelTracer::Instance()->IsEnabled())
         return context;

      AnsiString headerBlock;

      try
      {
         File messageFile;
         if (!messageFile.Open(file_name, File::OTReadOnly))
            return context;

         std::shared_ptr<ByteBuffer> chunk = messageFile.ReadChunk(MessageHeaderSearchLimit);
         messageFile.Close();

         if (!chunk || chunk->GetSize() == 0)
            return context;

         headerBlock = AnsiString(reinterpret_cast<const char*>(chunk->GetBuffer()), (int) chunk->GetSize());
      }
      catch (...)
      {
         // An unreadable file is the delivery code's problem to report, not
         // this path's: a trace context that cannot be read means a fresh
         // local trace, nothing more.
         return context;
      }

      int headerEnd = headerBlock.Find("\r\n\r\n");
      if (headerEnd >= 0)
         headerBlock = headerBlock.Mid(0, headerEnd);

      AnsiString traceparent = GetHeaderBlockValue_(headerBlock, "traceparent");
      if (traceparent.IsEmpty())
         return context;

      context = TryParseTraceparent(traceparent);
      if (!context.valid)
         return context;

      AnsiString traceState = GetHeaderBlockValue_(headerBlock, "tracestate");
      if (IsValidTraceState(traceState))
         context.trace_state = traceState;

      return context;
   }

   AnsiString
   OtelTraceContext::SanitizeSpanName(const AnsiString &token)
   {
      int length = token.GetLength();
      if (length == 0 || length > 16)
         return "request";

      for (int i = 0; i < length; i++)
      {
         char c = token.GetAt(i);
         if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')))
            return "request";
      }

      AnsiString name = token;
      name.MakeUpper();
      return name;
   }

   AnsiString
   OtelTraceContext::CreateReceptionHeaders(std::shared_ptr<MimeHeader> original_headers, int session_id)
   {
      if (!OtelTracer::Instance()->IsEnabled())
         return "";

      OtelTraceContext inbound = FromMessageHeaders(original_headers);

      // Continue a valid inbound trace; anything else - absent, malformed, an
      // all-zero id - means a fresh one. Either way the value prepended below
      // is minted here and is valid by construction, so a malformed inbound
      // value can never be the one this server passes on.
      AnsiString traceId = inbound.valid ? inbound.trace_id : OtelTracer::NewTraceId();
      AnsiString spanId = OtelTracer::NewSpanId();

      std::vector<OtelAttribute> attributes;

      OtelAttribute sessionAttribute;
      sessionAttribute.key = "hmailserver.session.id";
      sessionAttribute.value.Format("%d", session_id);
      attributes.push_back(sessionAttribute);

      OtelAttribute remoteAttribute;
      remoteAttribute.key = "hmailserver.trace.remote";
      remoteAttribute.value = inbound.valid ? "true" : "false";
      attributes.push_back(remoteAttribute);

      if (inbound.valid)
      {
         // The inbound flag, recorded so a backend can see the sender's intent.
         // It is deliberately not a decision: recording volume is bounded by
         // local configuration alone, so neither value of this bit changes what
         // this server exports.
         OtelAttribute sampledAttribute;
         sampledAttribute.key = "hmailserver.trace.inbound_sampled";
         sampledAttribute.value = inbound.sampled ? "true" : "false";
         attributes.push_back(sampledAttribute);
      }

      // The span the prepended traceparent names, so a downstream hop's spans
      // have an exported parent to attach to. Parented to the sender's span
      // when the inbound value was valid - that link is the whole point of
      // ingestion.
      OtelTracer::Instance()->RecordLinkedSpan("smtp.receive", OtelSpanKindServer,
         traceId, spanId, inbound.valid ? inbound.parent_span_id : AnsiString(), attributes);

      // This hop's authoritative value, above the original header block the way
      // every Received header is. The sampled flag is this server's own
      // recording decision (a value is only emitted when tracing is on, so it
      // is always 01), never an echo of the inbound bit.
      AnsiString lines = "traceparent: " + FormatTraceparent(traceId, spanId, true) + "\r\n";

      if (inbound.valid && !inbound.trace_state.IsEmpty())
         lines += "tracestate: " + inbound.trace_state + "\r\n";

      return lines;
   }
}
