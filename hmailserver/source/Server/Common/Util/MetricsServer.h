// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Minimal HTTP endpoint exposing server statistics in the Prometheus
// text exposition format. Enabled with MetricsServerPort in hMailServer.ini.

#pragma once

#include <thread>

namespace HM
{
   class MetricsServer
   {
   public:
      MetricsServer();
      ~MetricsServer();

      // Starts the listener. Returns false if the socket could not be bound.
      bool Start(const String &bind_address, int port);
      void Stop();

   private:

      void Run_();
      void HandleClient_(SOCKET client_socket);
      AnsiString BuildMetricsBody_();

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;
      ULONGLONG start_tick_count_;
   };
}
