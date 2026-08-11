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
      // Failed AUTHENTICATE attempts allowed on one connection before it is
      // dropped, matching the other protocol servers.
      enum { MaxAuthenticationFailures = 3 };

      void Run_();
      void HandleClient_(SOCKET client_socket, const IPAddress &client_address);

      static IPAddress GetClientAddress_(const sockaddr *address);

      void SendCapabilities_(SOCKET client_socket);

      bool ReadLine_(SOCKET client_socket, std::string &buffer, String &line);
      bool ReadBytes_(SOCKET client_socket, std::string &buffer, int count, String &data);
      static void Send_(SOCKET client_socket, const AnsiString &data);

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;
   };
}
