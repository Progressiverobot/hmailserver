// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <vector>

namespace HM
{
   // One HTTP request, synchronously, with the response read to the end.
   //
   // The third such client in this tree (AcmeClient::Transact_ and
   // OutboundOAuth2TokenClient::HttpsPost_ are the other two) and the one meant to be
   // shared from here on: the JWKS fetch and token introspection both need a verified
   // HTTPS GET or POST with a bounded response, and nothing about that is specific to
   // either. HTTP/1.0 with Connection: close, so the end of the response is the end of
   // the stream and no chunked or keep-alive handling is needed.
   //
   // https is verified against the machine's root store with the host name checked
   // (CertificateVerifier), exactly as the two older clients do. Plain http is accepted
   // only to a loopback address: a JWKS document or an introspection verdict fetched in
   // the clear from across a network would let whoever is on that network sign tokens
   // or revoke them, while a service on the same machine - or the fixtures - is a
   // different matter.
   class HttpsClient
   {
   public:

      struct Response
      {
         Response() : status_code(0) {}

         int status_code;
         AnsiString headers;   // the raw header block, for callers that need one
         AnsiString body;
      };

      // method is GET or POST. extra_headers are complete lines without the CRLF.
      // content_type and body are used for POST. Returns false, with error set, when
      // no response was obtained at all; an HTTP error status is a true return with the
      // status in the response, for the caller to judge.
      static bool Request(const AnsiString &method, const AnsiString &url, const std::vector<AnsiString> &extra_headers,
                          const AnsiString &content_type, const AnsiString &body, Response &response, String &error,
                          int timeout_seconds = 20, size_t max_response_bytes = 1024 * 1024);

      // http(s)://host[:port]/path -> parts. Port defaults to 443 or 80; path to "/".
      static bool ParseUrl(const AnsiString &url, bool &https, AnsiString &host, AnsiString &port, AnsiString &path);

      static bool IsLoopbackHost(const AnsiString &host);

      // application/x-www-form-urlencoded, one value.
      static AnsiString FormEncode(const AnsiString &value);
   };
}
