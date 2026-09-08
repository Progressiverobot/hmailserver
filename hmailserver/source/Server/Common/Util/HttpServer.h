// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The HTTP/1.1 server the optional listeners run on. See HttpServer.cpp for
// the shape of the concurrency model and why each ceiling is absolute.

#pragma once

#include <atomic>
#include <functional>
#include <memory>
#include <mutex>
#include <set>
#include <thread>
#include <utility>
#include <vector>

#include "../TCPIP/IPAddress.h"

namespace HM
{
   class HttpConnection;

   // One request, as the server hands it to a handler. The string-based
   // handlers this server was built for parse `raw` themselves; `method`,
   // `target` and `headers` are the same bytes already split, for handlers
   // written after them.
   struct HttpRequest
   {
      AnsiString method;
      AnsiString target;      // the request-target as received, query string included
      AnsiString version;     // "HTTP/1.0" or "HTTP/1.1"
      AnsiString head;        // request line and headers, up to and including the blank line
      AnsiString body;        // exactly Content-Length bytes
      AnsiString raw;         // head + body

      // Header names lower-cased, values trimmed; in arrival order.
      std::vector<std::pair<AnsiString, AnsiString>> headers;

      IPAddress peer;         // an IPv4 client of a dual-stack listener is unmapped to its IPv4 address
      bool over_tls = false;
      int listen_port = 0;

      // The value of the named header (lower-case name), or "" when absent.
      AnsiString Header(const AnsiString &lowerCaseName) const;
   };

   // What a handler returns. The server writes the status line, the two
   // framing headers and the connection header; extra_headers are complete
   // "Name: value\r\n" lines whose values the builder has already checked for
   // CR and LF.
   struct HttpResponse
   {
      int status = 200;
      AnsiString content_type = "text/plain";
      AnsiString body;
      AnsiString extra_headers;

      // Close after this response whatever the client asked for. A refusal on
      // size or a malformed request sets this: the connection's framing can no
      // longer be trusted.
      bool close = false;
   };

   // Every ceiling is absolute - a deadline from a fixed moment - rather than
   // an idle timeout that a client can keep alive by sending one byte at a
   // time. See HttpServer.cpp.
   struct HttpLimits
   {
      // Head and body together. A request whose declared Content-Length would
      // take it over this is refused before the body is read.
      size_t max_request_bytes = 64 * 1024;

      // A second cap, granted per request by the large-request filter once
      // the head has been read and has passed max_request_bytes on its own.
      // Zero means there is no such cap. A request under it gets
      // request_seconds_large instead of request_seconds, so a body that
      // takes time to arrive is not cut off by a timer meant for a form.
      size_t max_request_bytes_large = 0;
      unsigned request_seconds_large = 0;

      // From the moment the server starts waiting for a request (which, on a
      // kept-alive connection, is the moment the previous response was written)
      // to the last byte of the response. Handler time counts.
      unsigned request_seconds = 30;

      // The whole life of one connection, from accept, whatever it is doing.
      unsigned connection_seconds = 300;

      // After this many responses the connection is closed, so a client that
      // keeps one open forever still pays an accept now and then.
      unsigned max_requests_per_connection = 1000;

      // Connections beyond this are closed at accept, before any TLS
      // handshake, so a flood costs the server an accept and a close each.
      unsigned max_connections = 64;

      // Threads running this server's own io_context - bounded, and separate
      // from the mail protocols' IOService so a request storm here cannot
      // starve SMTP accept.
      unsigned worker_threads = 4;
   };

   class HttpServer : public std::enable_shared_from_this<HttpServer>
   {
   public:
      // Answers one request. Runs on one of the worker threads; may block (it
      // usually does - it reads the database). An exception escaping it is
      // answered with a 500 from error_responder and the connection is closed.
      typedef std::function<HttpResponse(const HttpRequest &)> Handler;

      // Decides, from the peer address alone and before any byte is read or any
      // TLS handshake started, whether a connection is taken at all. False
      // closes it silently - the shape of a security range refusing an SMTP
      // connection before its banner.
      typedef std::function<bool(const IPAddress &)> AcceptFilter;

      // Builds the body of a response the server itself has to give - 400 for a
      // malformed request, 413 for an oversized one, 411 for a chunked body, 500
      // for a handler that threw - in the listener's own dialect (JSON for the
      // API, text for the web services), so the two never differ in shape from
      // the responses the handlers build.
      typedef std::function<HttpResponse(int status, const AnsiString &message)> ErrorResponder;

      // Whether a request, by method and target, may be as large as
      // max_request_bytes_large. Asked after the head is parsed, before the
      // body is read; never for the head itself.
      typedef std::function<bool(const AnsiString &method, const AnsiString &target)> LargeRequestFilter;

      HttpServer(const AnsiString &name, const HttpLimits &limits, Handler handler,
                 AcceptFilter accept_filter, ErrorResponder error_responder);
      ~HttpServer();

      // Binds one listener; may be called for several ports before Start(). An
      // empty tls means plain HTTP. Failures are logged under the server's name
      // and returned as false.
      bool Listen(const String &bind_address, int port, std::shared_ptr<boost::asio::ssl::context> tls);

      void Start();

      // Set before Listen; connections copy it as they are made.
      void SetLargeRequestFilter(LargeRequestFilter filter);

      // Closes the listeners and every connection, then joins the workers. A
      // handler that is running at the time is waited for.
      void Stop();

      size_t GetConnectionCount() const;

      // The reason phrase for a status the listeners use; any other status is
      // reported as 500, deliberately - a wrong number in a response line is
      // worse than an honest server error.
      static AnsiString StatusText(int &status);

      // The complete wire form of a response.
      static AnsiString Serialize(const HttpResponse &response, bool keep_alive);

      // "host:port" for log lines, with an IPv6 literal in brackets -
      // "[::1]:8080" - because "::1:8080" reads as a different IPv6 address.
      static String FormatEndpoint(const String &bind_address, int port);

   private:
      friend class HttpConnection;

      struct Listener;

      void Accept_(std::shared_ptr<Listener> listener);
      bool Track_(std::shared_ptr<HttpConnection> connection);
      void Untrack_(HttpConnection *connection);
      void RunWorker_();

      AnsiString name_;
      HttpLimits limits_;
      Handler handler_;
      AcceptFilter accept_filter_;
      ErrorResponder error_responder_;
      LargeRequestFilter large_request_filter_;

      boost::asio::io_context io_;
      std::unique_ptr<boost::asio::executor_work_guard<boost::asio::io_context::executor_type>> work_;
      std::vector<std::shared_ptr<Listener>> listeners_;
      std::vector<std::thread> threads_;

      mutable std::mutex mutex_;
      std::set<std::shared_ptr<HttpConnection>> connections_;
      std::atomic<bool> running_;
      std::atomic<unsigned> refused_over_cap_;
      std::atomic<unsigned> pending_accepts_;
   };
}
