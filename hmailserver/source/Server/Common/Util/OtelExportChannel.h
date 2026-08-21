// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
//
// The OTLP/HTTP transport shared by the three signal exporters (OtelTracer,
// OtelMetricsExporter, OtelLogExporter): endpoint-URL parsing with a per-signal
// default path, and a blocking JSON POST over a raw socket. Extracted from
// OtelTracer rather than written three times, for the same reason the metrics
// listener routes TLS through SslContextInitializer: two of the optional HTTP
// listeners each built their own context and each drifted from the shared
// configuration, and a third copy of THIS code would repeat that mistake with
// the endpoint grammar instead of the cipher list.

#pragma once

namespace HM
{
   class OtelExportChannel
   {
   public:
      OtelExportChannel();

      // Parses "http://host[:port][/path]". Only plain HTTP is supported (the
      // standard OTLP/HTTP collector port is 4318); TLS export is future work.
      // default_path is the signal's OTLP path ("/v1/traces", "/v1/metrics",
      // "/v1/logs"), applied when the URL names no path. On failure the channel
      // stays unconfigured and error_message names what was wrong, in the words
      // the operator needs ("must be an http:// URL", "has no host").
      bool Configure(const AnsiString &endpoint, const AnsiString &default_path, AnsiString &error_message);

      bool IsConfigured() const { return configured_; }

      const AnsiString &GetHost() const { return host_; }
      int GetPort() const { return port_; }
      const AnsiString &GetPath() const { return path_; }

      // POSTs one JSON document to the configured endpoint and confirms a 2xx
      // status line; the response body is irrelevant to us. Blocking, with a
      // short socket timeout - callers run this on their own exporter thread,
      // never on a protocol thread.
      bool PostJson(const AnsiString &json) const;

      // JSON string escaping for everything the exporters serialise. Kept here so
      // the three signals cannot disagree about what needs escaping.
      static AnsiString JsonEscape(const AnsiString &value);

   private:
      bool configured_;
      AnsiString host_;
      int port_;
      AnsiString path_;
   };
}
