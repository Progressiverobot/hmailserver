// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Minimal HTTP endpoint exposing server statistics in the Prometheus
// text exposition format. See MetricsServer.h.

#include "StdAfx.h"

#include "MetricsServer.h"
#include "ServerStatus.h"

#include "../TCPIP/SocketConstants.h"

#include <ws2tcpip.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   MetricsServer::MetricsServer() :
      listen_socket_(INVALID_SOCKET),
      running_(false),
      start_tick_count_(0)
   {

   }

   MetricsServer::~MetricsServer()
   {
      Stop();
   }

   bool
   MetricsServer::Start(const String &bind_address, int port)
   {
      if (running_)
         return true;

      AnsiString narrowBindAddress = bind_address;

      sockaddr_in address = {};
      address.sin_family = AF_INET;
      address.sin_port = htons(static_cast<unsigned short>(port));

      if (inet_pton(AF_INET, narrowBindAddress.c_str(), &address.sin_addr) != 1)
      {
         LOG_APPLICATION("MetricsServer: Invalid bind address: " + bind_address);
         return false;
      }

      listen_socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      if (listen_socket_ == INVALID_SOCKET)
         return false;

      BOOL reuseAddress = TRUE;
      setsockopt(listen_socket_, SOL_SOCKET, SO_REUSEADDR, (const char*) &reuseAddress, sizeof(reuseAddress));

      if (bind(listen_socket_, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR ||
          listen(listen_socket_, 5) == SOCKET_ERROR)
      {
         String message;
         message.Format(_T("MetricsServer: Failed to bind to %s:%d."), bind_address.c_str(), port);
         LOG_APPLICATION(message);

         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
         return false;
      }

      start_tick_count_ = GetTickCount64();
      running_ = true;

      worker_ = std::thread(&MetricsServer::Run_, this);

      String message;
      message.Format(_T("MetricsServer: Listening on %s:%d (/metrics)."), bind_address.c_str(), port);
      LOG_APPLICATION(message);

      return true;
   }

   void
   MetricsServer::Stop()
   {
      if (!running_)
         return;

      running_ = false;

      if (listen_socket_ != INVALID_SOCKET)
      {
         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
      }

      if (worker_.joinable())
         worker_.join();
   }

   void
   MetricsServer::Run_()
   {
      for (;;)
      {
         SOCKET clientSocket = accept(listen_socket_, nullptr, nullptr);

         if (clientSocket == INVALID_SOCKET)
         {
            // The listen socket was closed (shutdown) or an error occurred.
            if (!running_)
               return;

            continue;
         }

         HandleClient_(clientSocket);
      }
   }

   void
   MetricsServer::HandleClient_(SOCKET client_socket)
   {
      DWORD timeout = 5000;
      setsockopt(client_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(client_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      char requestBuffer[2048];
      int bytesReceived = recv(client_socket, requestBuffer, sizeof(requestBuffer) - 1, 0);

      if (bytesReceived <= 0)
      {
         closesocket(client_socket);
         return;
      }

      requestBuffer[bytesReceived] = '\0';

      AnsiString request(requestBuffer);
      AnsiString response;

      if (request.StartsWith("GET /metrics"))
      {
         AnsiString body = BuildMetricsBody_();

         AnsiString headers;
         headers.Format("HTTP/1.0 200 OK\r\nContent-Type: text/plain; version=0.0.4; charset=utf-8\r\nContent-Length: %d\r\nConnection: close\r\n\r\n", body.GetLength());

         response = headers + body;
      }
      else
      {
         response = "HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
      }

      send(client_socket, response.c_str(), response.GetLength(), 0);

      shutdown(client_socket, SD_SEND);
      closesocket(client_socket);
   }

   AnsiString
   MetricsServer::BuildMetricsBody_()
   {
      ServerStatus *status = ServerStatus::Instance();

      ULONGLONG uptimeSeconds = (GetTickCount64() - start_tick_count_) / 1000;

      AnsiString body;

      AnsiString line;

      body += "# HELP hmailserver_processed_messages_total Number of messages processed since server start.\n";
      body += "# TYPE hmailserver_processed_messages_total counter\n";
      line.Format("hmailserver_processed_messages_total %d\n", status->GetNumberOfProcessedMessages());
      body += line;

      body += "# HELP hmailserver_spam_messages_total Number of detected spam messages since server start.\n";
      body += "# TYPE hmailserver_spam_messages_total counter\n";
      line.Format("hmailserver_spam_messages_total %d\n", status->GetNumberOfDetectedSpamMessages());
      body += line;

      body += "# HELP hmailserver_viruses_removed_total Number of viruses removed since server start.\n";
      body += "# TYPE hmailserver_viruses_removed_total counter\n";
      line.Format("hmailserver_viruses_removed_total %d\n", status->GetNumberOfRemovedViruses());
      body += line;

      body += "# HELP hmailserver_sessions Current number of active sessions per protocol.\n";
      body += "# TYPE hmailserver_sessions gauge\n";
      line.Format("hmailserver_sessions{protocol=\"smtp\"} %d\n", status->GetNumberOfSessions(STSMTP));
      body += line;
      line.Format("hmailserver_sessions{protocol=\"imap\"} %d\n", status->GetNumberOfSessions(STIMAP));
      body += line;
      line.Format("hmailserver_sessions{protocol=\"pop3\"} %d\n", status->GetNumberOfSessions(STPOP3));
      body += line;

      body += "# HELP hmailserver_uptime_seconds Time since the metrics endpoint was started.\n";
      body += "# TYPE hmailserver_uptime_seconds gauge\n";
      line.Format("hmailserver_uptime_seconds %I64u\n", uptimeSeconds);
      body += line;

      body += "# HELP hmailserver_state Server state (1=stopped, 2=starting, 3=running, 4=stopping).\n";
      body += "# TYPE hmailserver_state gauge\n";
      line.Format("hmailserver_state %d\n", status->GetState());
      body += line;

      return body;
   }
}
