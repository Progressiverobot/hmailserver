// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <thread>
#include <string>

namespace HM
{
   // Optional ManageSieve (RFC 5804) listener for uploading and managing
   // per-account Sieve scripts. Implemented as a raw-socket + std::thread service
   // (like MetricsServer / RestApiServer), enabled with
   // [Settings] ManageSieveServerPort in hMailServer.ini and disabled (port 0) by
   // default. Authentication is SASL PLAIN against the regular account database;
   // STARTTLS is not yet offered, so bind to 127.0.0.1 unless fronted by a TLS
   // terminator.
   class ManageSieveServer
   {
   public:
      ManageSieveServer();
      ~ManageSieveServer();

      bool Start(const String &bind_address, int port);
      void Stop();

   private:
      void Run_();
      void HandleClient_(SOCKET client_socket);

      void SendCapabilities_(SOCKET client_socket);

      bool ReadLine_(SOCKET client_socket, std::string &buffer, String &line);
      bool ReadBytes_(SOCKET client_socket, std::string &buffer, int count, String &data);
      static void Send_(SOCKET client_socket, const AnsiString &data);

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;
   };
}
