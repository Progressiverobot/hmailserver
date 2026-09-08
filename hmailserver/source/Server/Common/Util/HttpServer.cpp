// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// An HTTP/1.1 server on Boost.Asio for the optional listeners.
//
// What it replaces: each listener was a std::thread around a blocking accept()
// that read one request, answered it and closed - HTTP/1.0, Connection: close,
// one request at a time for the whole listener. Adequate for a metrics scrape
// every thirty seconds; hopeless for a client issuing dozens of small requests
// per screen, and the reason a slow client could hold the whole API for as
// long as a read timeout.
//
// The shape here:
//
//  - Its own io_context, run by a bounded pool of worker threads. Nothing the
//    mail protocols do shares an executor with this, so a request storm on the
//    API cannot delay an SMTP accept, and the pool size is the ceiling on how
//    many handlers can be in the database at once.
//
//  - Every connection is a strand: its reads, its writes and its two timers
//    never run concurrently, so the connection needs no lock of its own.
//
//  - Keep-alive. An HTTP/1.1 client gets it unless it says Connection: close;
//    an HTTP/1.0 client gets it only if it asks. The listeners' tests speak
//    HTTP/1.0 with Connection: close and are answered exactly as before.
//
//  - Absolute ceilings, not idle timeouts. A request timer starts when the
//    server begins waiting for a request and covers reading it, handling it and
//    writing the response; a connection timer starts at accept and covers the
//    connection's whole life. A client that dribbles a byte at a time just
//    under an idle timeout used to occupy the single worker indefinitely; here
//    it is closed when the deadline passes, whatever it is doing.
//
//  - A connection cap, enforced at accept before the TLS handshake, because the
//    handshake is the expensive part and the cap exists to bound that cost.
//
// What it does not do yet, and says so: bodies are read whole (bounded by
// max_request_bytes) and responses are written whole, so neither streaming
// shape a webmail needs exists here; chunked request bodies are refused with
// 411. Those are the next layer's work, on this foundation.

#include "StdAfx.h"

#include "HttpServer.h"

#include <algorithm>
#include <chrono>
#include <cstring>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // How long Stop() waits for the connections it closed to drain before
      // stopping the io_context outright. A handler blocked in the database is
      // waited for by the join in any case; this bounds only the socket
      // shutdowns.
      const int StopDrainMilliseconds = 5000;

      // The over-cap refusal is logged for the first refusal and then once per
      // this many, so a flood produces a line the operator can find rather than
      // a log the flood writes.
      const unsigned OverCapLogInterval = 1000;

      const char *HeadTerminator = "\r\n\r\n";

      AnsiString ToLower(const AnsiString &value)
      {
         AnsiString result = value;
         result.MakeLower();
         return result;
      }

      AnsiString Trim(const AnsiString &value)
      {
         const char *text = value.c_str();
         size_t start = 0;
         size_t end = value.size();

         while (start < end && (text[start] == ' ' || text[start] == '\t'))
            start++;
         while (end > start && (text[end - 1] == ' ' || text[end - 1] == '\t' || text[end - 1] == '\r'))
            end--;

         return AnsiString(value.substr(start, end - start));
      }

      // True when the comma-separated Connection header carries the token.
      bool HasConnectionToken(const AnsiString &lowerCaseValue, const char *token)
      {
         size_t position = 0;
         while (position <= lowerCaseValue.size())
         {
            size_t comma = lowerCaseValue.find(',', position);
            if (comma == std::string::npos)
               comma = lowerCaseValue.size();

            if (Trim(AnsiString(lowerCaseValue.substr(position, comma - position))) == token)
               return true;

            position = comma + 1;
         }

         return false;
      }

      IPAddress PeerAddress(const boost::asio::ip::tcp::endpoint &endpoint)
      {
         boost::asio::ip::address address = endpoint.address();

         // An IPv4 client of a dual-stack listener arrives as ::ffff:a.b.c.d.
         // Unmapped here - not cosmetics: every consumer of the result matches
         // on address family. The auto-ban exclusion compares against
         // "127.0.0.1", an AllowedFrom restriction written as an IPv4 range
         // refuses any IPv6 peer outright, and the security ranges the auto-ban
         // creates are IPv4 ranges. A v4 client dressed as v6 would silently
         // match none of them - including the loopback exclusion.
         if (address.is_v6() && address.to_v6().is_v4_mapped())
            address = boost::asio::ip::make_address_v4(boost::asio::ip::v4_mapped, address.to_v6());

         return IPAddress(address);
      }
   }

   AnsiString
   HttpRequest::Header(const AnsiString &lowerCaseName) const
   {
      for (size_t i = 0; i < headers.size(); i++)
      {
         if (headers[i].first == lowerCaseName)
            return headers[i].second;
      }

      return "";
   }

   struct HttpServer::Listener
   {
      Listener(boost::asio::io_context &io, int listen_port, std::shared_ptr<boost::asio::ssl::context> tls_context) :
         acceptor(io),
         tls(tls_context),
         port(listen_port)
      {
      }

      boost::asio::ip::tcp::acceptor acceptor;
      std::shared_ptr<boost::asio::ssl::context> tls;
      int port;
   };

   // One accepted socket, from accept to close. Every method runs inside
   // strand_.
   //
   // The connection holds the server weakly and copies what it needs from it
   // at construction. A strong reference would be a cycle: a handler queued on
   // the io_context holds the connection, the connection would hold the
   // server, and the server owns the io_context - so a server stopped with
   // handlers still queued could never be destroyed.
   class HttpConnection : public std::enable_shared_from_this<HttpConnection>
   {
   public:
      HttpConnection(std::shared_ptr<HttpServer> server, boost::asio::ip::tcp::socket socket,
                     std::shared_ptr<boost::asio::ssl::context> tls, int listen_port, const IPAddress &peer) :
         server_(server),
         limits_(server->limits_),
         handler_(server->handler_),
         error_responder_(server->error_responder_),
         large_request_filter_(server->large_request_filter_),
         strand_(boost::asio::make_strand(server->io_)),
         socket_(std::move(socket)),
         request_timer_(server->io_),
         connection_timer_(server->io_),
         buffer_(std::max(server->limits_.max_request_bytes, server->limits_.max_request_bytes_large) + 1),
         peer_(peer),
         listen_port_(listen_port),
         requests_(0),
         content_length_(0),
         keep_alive_(false),
         closed_(false)
      {
         if (tls)
            tls_.reset(new boost::asio::ssl::stream<boost::asio::ip::tcp::socket&>(socket_, *tls));
      }

      void Start()
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         boost::asio::post(strand_, [self]()
         {
            self->ArmConnectionTimer_();

            if (self->tls_)
               self->Handshake_();
            else
               self->ReadHead_();
         });
      }

      // From Stop(): closes whatever the connection is doing.
      void Close()
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         boost::asio::post(strand_, [self]()
         {
            self->CloseNow_();
         });
      }

   private:
      void ArmConnectionTimer_()
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         connection_timer_.expires_after(std::chrono::seconds(limits_.connection_seconds));
         connection_timer_.async_wait(boost::asio::bind_executor(strand_,
            [self](const boost::system::error_code &error)
            {
               if (!error)
                  self->CloseNow_();
            }));
      }

      void ArmRequestTimer_()
      {
         ArmRequestTimer_(limits_.request_seconds);
      }

      void ArmRequestTimer_(unsigned seconds)
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         // expires_after cancels a wait already pending, whose handler then runs
         // with operation_aborted and does nothing.
         request_timer_.expires_after(std::chrono::seconds(seconds));
         request_timer_.async_wait(boost::asio::bind_executor(strand_,
            [self](const boost::system::error_code &error)
            {
               if (!error)
                  self->CloseNow_();
            }));
      }

      void Handshake_()
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         // The handshake counts against the first request's deadline: a client
         // that connects and never completes it is the same client as one that
         // never sends a request line.
         ArmRequestTimer_();

         tls_->async_handshake(boost::asio::ssl::stream_base::server, boost::asio::bind_executor(strand_,
            [self](const boost::system::error_code &error)
            {
               if (self->closed_)
                  return;

               if (error)
               {
                  self->CloseNow_();
                  return;
               }

               self->ReadHead_();
            }));
      }

      void ReadHead_()
      {
         if (closed_)
            return;

         std::shared_ptr<HttpConnection> self = shared_from_this();

         ArmRequestTimer_();

         auto completion = boost::asio::bind_executor(strand_,
            [self](const boost::system::error_code &error, size_t bytes)
            {
               self->OnHead_(error, bytes);
            });

         if (tls_)
            boost::asio::async_read_until(*tls_, buffer_, HeadTerminator, completion);
         else
            boost::asio::async_read_until(socket_, buffer_, HeadTerminator, completion);
      }

      void OnHead_(const boost::system::error_code &error, size_t bytes)
      {
         if (closed_)
            return;

         if (error)
         {
            // The buffer filled without a blank line: the head alone is over the
            // cap. Says nothing about the cap; an administrator hitting this
            // reads the documentation, a stranger measuring it learns nothing.
            if (error == boost::asio::error::not_found)
            {
               Fail_(413, "request too large");
               return;
            }

            // EOF while waiting for a request is the normal end of a kept-alive
            // connection. Anything else - a reset, a cancelled read - ends it
            // the same way.
            CloseNow_();
            return;
         }

         request_ = HttpRequest();
         request_.head.assign(boost::asio::buffers_begin(buffer_.data()), boost::asio::buffers_begin(buffer_.data()) + bytes);
         request_.peer = peer_;
         request_.over_tls = tls_ != nullptr;
         request_.listen_port = listen_port_;

         buffer_.consume(bytes);

         if (!ParseHead_())
         {
            Fail_(400, "malformed request");
            return;
         }

         // The head alone is held to the first cap whatever the route. The
         // body may be held to the second when the filter grants it - and
         // then the request gets the longer timer, since the body is the
         // point and takes time to arrive.
         size_t cap = limits_.max_request_bytes;
         if (request_.head.size() <= limits_.max_request_bytes && limits_.max_request_bytes_large > cap &&
             large_request_filter_ && large_request_filter_(request_.method, request_.target))
         {
            cap = limits_.max_request_bytes_large;
            if (limits_.request_seconds_large > limits_.request_seconds)
               ArmRequestTimer_(limits_.request_seconds_large);
         }

         if (request_.head.size() > limits_.max_request_bytes || request_.head.size() + content_length_ > cap)
         {
            // Refused before the body is read. The client may still be sending
            // it; the close that follows the response resets what is in flight,
            // which is the point - nothing of an oversized request is acted on.
            Fail_(413, "request too large");
            return;
         }

         size_t buffered = buffer_.size();
         size_t remaining = content_length_ > buffered ? content_length_ - buffered : 0;

         if (remaining > 0 && ToLower(request_.Header("expect")) == "100-continue")
         {
            // RFC 7231 section 5.1.1: the client is waiting to be told to send
            // the body it has declared. Answered now that the declaration has
            // passed the cap, so a refused body is never sent at all.
            WriteContinue_(remaining);
            return;
         }

         ReadBody_(remaining);
      }

      // Splits the head into the request line and the headers. False when the
      // request line is not three tokens with an HTTP/1.x version, or a header
      // line has no colon, or the head holds a NUL - there is no legitimate NUL
      // in a request line or a header, and a request cut at one would be
      // processed as a different string from the one the lengths described.
      bool ParseHead_()
      {
         content_length_ = 0;

         if (request_.head.find('\0') != std::string::npos)
            return false;

         size_t lineEnd = request_.head.find("\r\n");
         if (lineEnd == std::string::npos)
            return false;

         AnsiString requestLine(request_.head.substr(0, lineEnd));

         size_t firstSpace = requestLine.find(' ');
         size_t lastSpace = requestLine.rfind(' ');
         if (firstSpace == std::string::npos || lastSpace == firstSpace)
            return false;

         request_.method = AnsiString(requestLine.substr(0, firstSpace));
         request_.target = AnsiString(requestLine.substr(firstSpace + 1, lastSpace - firstSpace - 1));
         request_.version = AnsiString(requestLine.substr(lastSpace + 1));

         if (request_.method.empty() || request_.target.empty() ||
             request_.version.size() != 8 || request_.version.compare(0, 7, "HTTP/1.") != 0)
            return false;

         size_t position = lineEnd + 2;
         while (position < request_.head.size())
         {
            size_t end = request_.head.find("\r\n", position);
            if (end == std::string::npos)
               break;

            if (end == position)
               break;   // the blank line

            AnsiString line(request_.head.substr(position, end - position));
            size_t colon = line.find(':');
            if (colon == std::string::npos)
               return false;

            AnsiString name = ToLower(Trim(AnsiString(line.substr(0, colon))));
            AnsiString value = Trim(AnsiString(line.substr(colon + 1)));

            request_.headers.push_back(std::make_pair(name, value));

            position = end + 2;
         }

         AnsiString contentLength = request_.Header("content-length");
         if (!contentLength.empty())
         {
            if (contentLength.size() > 9 || contentLength.find_first_not_of("0123456789") != std::string::npos)
               return false;

            content_length_ = static_cast<size_t>(strtoul(contentLength.c_str(), nullptr, 10));
         }

         return true;
      }

      void WriteContinue_(size_t remaining)
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         out_ = "HTTP/1.1 100 Continue\r\n\r\n";

         auto completion = boost::asio::bind_executor(strand_,
            [self, remaining](const boost::system::error_code &error, size_t)
            {
               if (self->closed_)
                  return;

               if (error)
               {
                  self->CloseNow_();
                  return;
               }

               self->ReadBody_(remaining);
            });

         if (tls_)
            boost::asio::async_write(*tls_, boost::asio::buffer(out_.data(), out_.size()), completion);
         else
            boost::asio::async_write(socket_, boost::asio::buffer(out_.data(), out_.size()), completion);
      }

      void ReadBody_(size_t remaining)
      {
         if (remaining == 0)
         {
            OnBody_(boost::system::error_code(), 0);
            return;
         }

         std::shared_ptr<HttpConnection> self = shared_from_this();

         auto completion = boost::asio::bind_executor(strand_,
            [self](const boost::system::error_code &error, size_t bytes)
            {
               self->OnBody_(error, bytes);
            });

         if (tls_)
            boost::asio::async_read(*tls_, buffer_, boost::asio::transfer_exactly(remaining), completion);
         else
            boost::asio::async_read(socket_, buffer_, boost::asio::transfer_exactly(remaining), completion);
      }

      void OnBody_(const boost::system::error_code &error, size_t)
      {
         if (closed_)
            return;

         if (error)
         {
            CloseNow_();
            return;
         }

         request_.body.assign(boost::asio::buffers_begin(buffer_.data()), boost::asio::buffers_begin(buffer_.data()) + content_length_);
         buffer_.consume(content_length_);

         if (request_.body.find('\0') != std::string::npos)
         {
            Fail_(400, "malformed request");
            return;
         }

         request_.raw = request_.head + request_.body;

         if (!request_.Header("transfer-encoding").empty())
         {
            // A body this server would have to de-chunk to measure. Refused
            // rather than guessed at: the cap is on bytes, and a chunked body
            // has no declared size to hold against it.
            Fail_(411, "length required");
            return;
         }

         // Keep-alive is the HTTP/1.1 default and the HTTP/1.0 exception.
         AnsiString connection = ToLower(request_.Header("connection"));
         if (request_.version == "HTTP/1.1")
            keep_alive_ = !HasConnectionToken(connection, "close");
         else
            keep_alive_ = HasConnectionToken(connection, "keep-alive");

         requests_++;
         std::shared_ptr<HttpServer> server = server_.lock();
         if (requests_ >= limits_.max_requests_per_connection || !server || !server->running_)
            keep_alive_ = false;
         server.reset();

         HttpResponse response;
         try
         {
            response = handler_(request_);
         }
         catch (...)
         {
            response = error_responder_(500, "internal error");
            response.close = true;
         }

         if (response.close)
            keep_alive_ = false;

         Write_(response);
      }

      // A response the server gives on its own account. The connection is
      // closed afterwards: what is on the wire after a malformed or oversized
      // request cannot be framed.
      void Fail_(int status, const AnsiString &message)
      {
         HttpResponse response = error_responder_(status, message);
         keep_alive_ = false;
         Write_(response);
      }

      void Write_(const HttpResponse &response)
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         out_ = HttpServer::Serialize(response, keep_alive_);

         auto completion = boost::asio::bind_executor(strand_,
            [self](const boost::system::error_code &error, size_t)
            {
               self->OnWritten_(error);
            });

         if (tls_)
            boost::asio::async_write(*tls_, boost::asio::buffer(out_.data(), out_.size()), completion);
         else
            boost::asio::async_write(socket_, boost::asio::buffer(out_.data(), out_.size()), completion);
      }

      void OnWritten_(const boost::system::error_code &error)
      {
         if (closed_)
            return;

         if (error || !keep_alive_)
         {
            Shutdown_();
            return;
         }

         // The next request may already be in the buffer (pipelining);
         // async_read_until returns at once in that case.
         ReadHead_();
      }

      // The orderly end: a TLS close-notify where there is TLS, then the
      // socket. The request timer still runs, so a peer that never
      // acknowledges the shutdown is closed when it fires.
      void Shutdown_()
      {
         std::shared_ptr<HttpConnection> self = shared_from_this();

         if (tls_)
         {
            tls_->async_shutdown(boost::asio::bind_executor(strand_,
               [self](const boost::system::error_code &)
               {
                  self->CloseNow_();
               }));
            return;
         }

         boost::system::error_code ignored;
         socket_.shutdown(boost::asio::ip::tcp::socket::shutdown_send, ignored);
         CloseNow_();
      }

      void CloseNow_()
      {
         if (closed_)
            return;

         closed_ = true;

         boost::system::error_code ignored;
         request_timer_.cancel();
         connection_timer_.cancel();
         socket_.close(ignored);

         std::shared_ptr<HttpServer> server = server_.lock();
         if (server)
            server->Untrack_(this);
      }

      std::weak_ptr<HttpServer> server_;
      HttpLimits limits_;
      HttpServer::Handler handler_;
      HttpServer::ErrorResponder error_responder_;
      HttpServer::LargeRequestFilter large_request_filter_;
      boost::asio::strand<boost::asio::io_context::executor_type> strand_;
      boost::asio::ip::tcp::socket socket_;
      std::unique_ptr<boost::asio::ssl::stream<boost::asio::ip::tcp::socket&>> tls_;
      boost::asio::steady_timer request_timer_;
      boost::asio::steady_timer connection_timer_;
      boost::asio::streambuf buffer_;
      IPAddress peer_;
      int listen_port_;
      unsigned requests_;

      HttpRequest request_;
      size_t content_length_;
      bool keep_alive_;
      bool closed_;
      std::string out_;
   };

   HttpServer::HttpServer(const AnsiString &name, const HttpLimits &limits, Handler handler,
                          AcceptFilter accept_filter, ErrorResponder error_responder) :
      name_(name),
      limits_(limits),
      handler_(handler),
      accept_filter_(accept_filter),
      error_responder_(error_responder),
      running_(false),
      refused_over_cap_(0),
      pending_accepts_(0)
   {
      if (limits_.worker_threads == 0)
         limits_.worker_threads = 1;
      if (limits_.max_connections == 0)
         limits_.max_connections = 1;
      if (limits_.max_requests_per_connection == 0)
         limits_.max_requests_per_connection = 1;

      if (!error_responder_)
      {
         error_responder_ = [](int status, const AnsiString &message)
         {
            HttpResponse response;
            response.status = status;
            response.content_type = "text/plain";
            response.body = message;
            return response;
         };
      }
   }

   HttpServer::~HttpServer()
   {
      Stop();
   }

   bool
   HttpServer::Listen(const String &bind_address, int port, std::shared_ptr<boost::asio::ssl::context> tls)
   {
      // "localhost" is a name, and this listener binds addresses. The IPv4
      // loopback is what it always meant here.
      AnsiString narrowBindAddress = bind_address == _T("localhost") ? AnsiString("127.0.0.1") : AnsiString(bind_address);

      boost::system::error_code error;
      boost::asio::ip::address address = boost::asio::ip::make_address(narrowBindAddress.c_str(), error);

      if (error)
      {
         LOG_APPLICATION(String(name_) + ": Invalid bind address: " + bind_address);
         return false;
      }

      std::shared_ptr<Listener> listener(new Listener(io_, port, tls));
      boost::asio::ip::tcp::endpoint endpoint(address, static_cast<unsigned short>(port));

      listener->acceptor.open(endpoint.protocol(), error);
      if (error)
      {
         LOG_APPLICATION(String(name_) + ": Could not open a socket for " + FormatEndpoint(bind_address, port) + ": " + String(error.message().c_str()));
         return false;
      }

      // The unspecified IPv6 address means both families, as it does for the
      // mail protocols. A specific IPv6 address is left alone: it can only ever
      // accept IPv6, and clearing the option would be a no-op that reads as a
      // promise.
      if (address.is_v6() && address.is_unspecified())
      {
         listener->acceptor.set_option(boost::asio::ip::v6_only(false), error);
         if (error)
            LOG_APPLICATION(String(name_) + ": IPV6_V6ONLY could not be cleared for the :: bind, so this listener will accept IPv6 connections only. Bind 0.0.0.0 instead if IPv4 is what is needed.");
      }

      listener->acceptor.set_option(boost::asio::ip::tcp::acceptor::reuse_address(true), error);

      listener->acceptor.bind(endpoint, error);
      if (!error)
         listener->acceptor.listen(boost::asio::socket_base::max_listen_connections, error);

      if (error)
      {
         LOG_APPLICATION(String(name_) + ": Failed to bind to " + FormatEndpoint(bind_address, port) + ". Is the port in use?");
         return false;
      }

      listeners_.push_back(listener);
      return true;
   }

   void
   HttpServer::SetLargeRequestFilter(LargeRequestFilter filter)
   {
      large_request_filter_ = filter;
   }

   void
   HttpServer::Start()
   {
      if (running_)
         return;

      running_ = true;

      work_.reset(new boost::asio::executor_work_guard<boost::asio::io_context::executor_type>(io_.get_executor()));

      for (size_t i = 0; i < listeners_.size(); i++)
         Accept_(listeners_[i]);

      for (unsigned i = 0; i < limits_.worker_threads; i++)
         threads_.push_back(std::thread(&HttpServer::RunWorker_, this));
   }

   void
   HttpServer::RunWorker_()
   {
      // The top frame of a worker. An exception that escapes a completion
      // handler - a handler's own are caught in OnBody_ - would otherwise be
      // std::terminate, a dead server; here it is logged and the worker goes
      // back to work. The loop ends when the io_context has been stopped.
      for (;;)
      {
         try
         {
            io_.run();
            return;
         }
         catch (...)
         {
            LOG_APPLICATION(String(name_) + ": An exception escaped a worker thread. The worker continues.");
         }

         if (io_.stopped())
            return;
      }
   }

   void
   HttpServer::Accept_(std::shared_ptr<Listener> listener)
   {
      // Weak, for the reason given at HttpConnection: the queued accept must
      // not keep the server - and with it the io_context the accept is queued
      // on - alive.
      std::weak_ptr<HttpServer> weak = shared_from_this();
      std::shared_ptr<boost::asio::ip::tcp::socket> socket(new boost::asio::ip::tcp::socket(io_));

      pending_accepts_++;

      listener->acceptor.async_accept(*socket, [weak, listener, socket](const boost::system::error_code &error)
      {
         std::shared_ptr<HttpServer> self = weak.lock();
         if (!self)
            return;

         self->pending_accepts_--;

         if (!self->running_)
            return;

         if (error)
         {
            // The acceptor was closed by Stop(), or accept failed for a reason
            // that leaves it usable (a peer that reset before it was accepted).
            // The first ends the loop; the second continues it.
            if (error == boost::asio::error::operation_aborted || !listener->acceptor.is_open())
               return;

            self->Accept_(listener);
            return;
         }

         self->Accept_(listener);

         try
         {
            boost::system::error_code endpointError;
            boost::asio::ip::tcp::endpoint endpoint = socket->remote_endpoint(endpointError);

            // A failed lookup leaves the peer as 0.0.0.0, which every non-empty
            // source restriction refuses and no auto-ban matches. Failing closed
            // is the right direction.
            IPAddress peer = endpointError ? IPAddress() : PeerAddress(endpoint);

            if (self->accept_filter_ && !self->accept_filter_(peer))
            {
               boost::system::error_code ignored;
               socket->close(ignored);
               return;
            }

            std::shared_ptr<HttpConnection> connection(new HttpConnection(self, std::move(*socket), listener->tls, listener->port, peer));

            if (!self->Track_(connection))
            {
               unsigned refused = ++self->refused_over_cap_;
               if (refused == 1 || refused % OverCapLogInterval == 0)
               {
                  String message;
                  message.Format(_T("%s: Refused a connection from %s - %u connections are open, which is the limit. %u refused so far."),
                     String(self->name_).c_str(), String(peer.ToString()).c_str(), self->limits_.max_connections, refused);
                  LOG_APPLICATION(message);
               }

               // The connection object closes the socket when it goes.
               return;
            }

            connection->Start();
         }
         catch (...)
         {
            // Constructing the connection can only realistically fail by
            // bad_alloc; the socket is closed by its destructor either way.
         }
      });
   }

   bool
   HttpServer::Track_(std::shared_ptr<HttpConnection> connection)
   {
      std::lock_guard<std::mutex> guard(mutex_);

      if (connections_.size() >= limits_.max_connections)
         return false;

      connections_.insert(connection);
      return true;
   }

   void
   HttpServer::Untrack_(HttpConnection *connection)
   {
      std::lock_guard<std::mutex> guard(mutex_);

      for (std::set<std::shared_ptr<HttpConnection>>::iterator iter = connections_.begin(); iter != connections_.end(); ++iter)
      {
         if (iter->get() == connection)
         {
            connections_.erase(iter);
            return;
         }
      }
   }

   size_t
   HttpServer::GetConnectionCount() const
   {
      std::lock_guard<std::mutex> guard(mutex_);
      return connections_.size();
   }

   void
   HttpServer::Stop()
   {
      if (!running_)
      {
         listeners_.clear();
         return;
      }

      running_ = false;

      // The acceptors first, on the io_context, so no accept completes after
      // this point has passed; then every connection, each on its own strand.
      std::vector<std::shared_ptr<Listener>> listeners = listeners_;
      boost::asio::post(io_, [listeners]()
      {
         for (size_t i = 0; i < listeners.size(); i++)
         {
            boost::system::error_code ignored;
            listeners[i]->acceptor.close(ignored);
         }
      });

      std::vector<std::shared_ptr<HttpConnection>> connections;
      {
         std::lock_guard<std::mutex> guard(mutex_);
         connections.assign(connections_.begin(), connections_.end());
      }

      for (size_t i = 0; i < connections.size(); i++)
         connections[i]->Close();

      connections.clear();

      // Let the closes and the aborted accepts run, so that nothing is left
      // queued holding a connection; then stop the context whether or not they
      // all did. A handler that is in the database keeps its thread until it
      // returns; the join below waits for it, as Stop() always has.
      for (int waited = 0; waited < StopDrainMilliseconds; waited += 50)
      {
         if (GetConnectionCount() == 0 && pending_accepts_ == 0)
            break;

         std::this_thread::sleep_for(std::chrono::milliseconds(50));
      }

      work_.reset();
      io_.stop();

      for (size_t i = 0; i < threads_.size(); i++)
      {
         if (threads_[i].joinable())
            threads_[i].join();
      }

      threads_.clear();

      {
         std::lock_guard<std::mutex> guard(mutex_);
         connections_.clear();
      }

      listeners_.clear();
      io_.restart();
   }

   AnsiString
   HttpServer::StatusText(int &status)
   {
      switch (status)
      {
      case 100: return "Continue";
      case 200: return "OK";
      case 201: return "Created";
      case 202: return "Accepted";
      case 204: return "No Content";
      case 301: return "Moved Permanently";
      case 302: return "Found";
      case 304: return "Not Modified";
      case 400: return "Bad Request";
      case 401: return "Unauthorized";
      case 403: return "Forbidden";
      case 404: return "Not Found";
      case 405: return "Method Not Allowed";
      case 409: return "Conflict";
      case 411: return "Length Required";
      case 413: return "Payload Too Large";
      case 415: return "Unsupported Media Type";
      case 429: return "Too Many Requests";
      case 500: return "Internal Server Error";
      case 501: return "Not Implemented";
      case 503: return "Service Unavailable";
      default:
         status = 500;
         return "Internal Server Error";
      }
   }

   AnsiString
   HttpServer::Serialize(const HttpResponse &response, bool keep_alive)
   {
      int status = response.status;
      AnsiString statusText = StatusText(status);

      AnsiString out;
      out.Format("HTTP/1.1 %d %hs\r\nContent-Type: %hs\r\nContent-Length: %d\r\n",
         status, statusText.c_str(), response.content_type.c_str(), (int) response.body.GetLength());

      out += response.extra_headers;
      out += keep_alive ? "Connection: keep-alive\r\n\r\n" : "Connection: close\r\n\r\n";
      out += response.body;

      return out;
   }

   String
   HttpServer::FormatEndpoint(const String &bind_address, int port)
   {
      String result;

      if (bind_address.Find(_T(":")) >= 0)
         result.Format(_T("[%s]:%d"), bind_address.c_str(), port);
      else
         result.Format(_T("%s:%d"), bind_address.c_str(), port);

      return result;
   }
}
