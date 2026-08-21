// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
//
// W3C Trace Context (traceparent / tracestate) ingestion and propagation.
//
// A traceparent is attacker-supplied on every internet-facing path this server
// has - an HTTP request to one of the optional listeners, or a message header
// on inbound SMTP - so validation here is a security boundary, not a
// formality. The parser accepts exactly one shape: version 00, lowercase hex,
// "00-<32 hex>-<16 hex>-<2 hex>", with a non-zero trace id and a non-zero
// span id. Anything else is rejected whole: a rejected value is never
// propagated onward, never reaches the exporter, and never refuses the
// request that carried it - the caller simply starts a fresh local trace,
// because observability must never be able to refuse mail.
//
// The sampled flag is carried as information and NEVER as a decision. This
// tracer records every span when an endpoint is configured and none when it is
// not, so an inbound flag can neither force recording (there is no sampler to
// force past - the volume is bounded by local configuration alone) nor
// suppress it (an attacker cannot hide their messages from tracing by sending
// sampled=0). Outbound values always carry this server's own recording
// decision, which is 01 whenever a value is emitted at all.
//
// SMTP carries the context as a MESSAGE header rather than a transport header.
// On reception this server prepends its own freshly minted traceparent above
// the original header block - the Received-header model, one authoritative
// value per hop, first occurrence wins - and records an "smtp.receive" span
// linking it to the sender's span when the inbound value was valid. The
// original line is deliberately NOT removed: a sender's DKIM signature may
// cover it, and prepending is signature-safe (DKIM selects duplicate fields
// bottom-up) where deletion is not. Delivery then reads the first traceparent
// back out of the stored message file, so every delivery attempt - and every
// downstream hop - joins the same trace.

#pragma once

#include <memory>

namespace HM
{
   class MimeHeader;

   // A validated inbound trace context, or nothing. valid is true only when a
   // traceparent passed the strict parse; the other fields are meaningless
   // without it.
   struct OtelTraceContext
   {
      bool valid = false;
      AnsiString trace_id;        // 32 lowercase hex chars, not all zero
      AnsiString parent_span_id;  // 16 lowercase hex chars, not all zero
      bool sampled = false;       // the inbound flag, informational only
      AnsiString trace_state;     // validated pass-through, possibly empty

      // Strict W3C parse of one traceparent value. Returns an invalid context
      // for anything but the exact version-00 shape with non-zero ids.
      static OtelTraceContext TryParseTraceparent(const AnsiString &value);

      // "00-<trace_id>-<span_id>-<01|00>". The ids are the caller's problem;
      // this only formats.
      static AnsiString FormatTraceparent(const AnsiString &trace_id, const AnsiString &span_id, bool sampled);

      // Whether a tracestate value is safe to carry through: at most 512 bytes
      // of printable ASCII. tracestate is opaque vendor data, but a value we
      // did not bound would be a free amplification channel and one stray CR/LF
      // would be a header injection when it is re-emitted, so anything that
      // fails this check is dropped rather than forwarded.
      static bool IsValidTraceState(const AnsiString &value);

      // The context carried by a raw HTTP request's header block, for the
      // self-contained listeners (REST API, web services) that parse their
      // requests as text. Invalid when absent or malformed.
      static OtelTraceContext FromHttpRequest(const AnsiString &raw_request);

      // The context carried by a parsed message header (first occurrence of
      // traceparent). Invalid when absent or malformed.
      static OtelTraceContext FromMessageHeaders(std::shared_ptr<MimeHeader> headers);

      // The context carried by a stored message file: reads the header block
      // (bounded) and parses the first traceparent line. Cheap no-op returning
      // an invalid context when tracing is disabled, so the delivery path pays
      // nothing by default.
      static OtelTraceContext FromMessageFile(const String &file_name);

      // A client-supplied token reduced to something safe to use as a span
      // name: alphabetic, at most 16 characters, uppercased - the same
      // acceptance rule TCPConnection applies to protocol verbs - and the
      // generic "request" for anything else, so junk input cannot explode
      // trace cardinality or smuggle bytes into a span name.
      static AnsiString SanitizeSpanName(const AnsiString &token);

      // The inbound-SMTP hook body. Reads any traceparent/tracestate out of the
      // received message's headers, mints this hop's own trace context
      // (continuing the inbound trace when it was valid, starting a fresh one
      // when it was absent or rejected), records the "smtp.receive" span that
      // links the two, and returns the header lines to prepend - traceparent
      // always, tracestate only when a valid inbound one is being carried
      // through. Returns an empty string unless OtelEndpoint is configured, so
      // messages are byte-for-byte untouched by default.
      static AnsiString CreateReceptionHeaders(std::shared_ptr<MimeHeader> original_headers, int session_id);

   private:
      static bool IsLowercaseHex_(const AnsiString &value);
      static bool IsAllZero_(const AnsiString &value);

      // First "name: value" occurrence in a CRLF (or bare-LF) header block,
      // matched case-insensitively, value trimmed. Empty when absent.
      static AnsiString GetHeaderBlockValue_(const AnsiString &header_block, const char *field_name);
   };
}
