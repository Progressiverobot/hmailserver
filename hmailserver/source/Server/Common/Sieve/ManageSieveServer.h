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
   //
   // Because it does not run on the shared Boost.Asio listener, this class has to
   // apply the security-range check itself when it accepts a connection - see
   // IsConnectionAllowed_. Without it the auto-ban written on repeated logon
   // failures would never be enforced, and the per-connection attempt cap could be
   // sidestepped simply by reconnecting.
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

      // Upper bound on a SASL response supplied as a literal. A base64-encoded
      // PLAIN response holding a real address and password is an order of
      // magnitude smaller; the cap stops an unauthenticated client from making
      // us buffer an arbitrary amount of data.
      enum { MaxSaslResponseSize = 8192 };

      // The outcome of a single AUTHENTICATE command.
      enum AuthenticationOutcome
      {
         AuthenticationSucceeded,
         AuthenticationFailed,    // keep the connection, the client may retry
         AuthenticationAborted    // the connection must be closed
      };

      void Run_();
      void HandleClient_(SOCKET client_socket, const IPAddress &client_address);

      // Accept-time IP restriction check. This is the equivalent of what
      // TCPServer::HandleAccept does for SMTP/POP3/IMAP and is what makes an
      // auto-ban actually stop a reconnecting client.
      static bool IsConnectionAllowed_(const IPAddress &client_address);

      static IPAddress GetClientAddress_(const sockaddr *address);

      void SendCapabilities_(SOCKET client_socket);

      // Handles one AUTHENTICATE command. All credential-carrying paths run
      // through here so that no failure can escape the auto-ban accounting or
      // the per-connection cap. On success, account_address holds the address of
      // the account that was logged on to.
      AuthenticationOutcome HandleAuthenticate_(SOCKET client_socket,
                                                const IPAddress &client_address,
                                                const String &line,
                                                int pos,
                                                std::string &buffer,
                                                int &authentication_failures,
                                                String &account_address);

      // Counts a failed attempt, sends the response and reports whether the
      // connection has run out of attempts and must be closed.
      static bool ApplyAuthenticationFailure_(SOCKET client_socket,
                                              int &authentication_failures,
                                              bool disconnect,
                                              const AnsiString &failure_response);

      bool ReadLine_(SOCKET client_socket, std::string &buffer, String &line);
      bool ReadBytes_(SOCKET client_socket, std::string &buffer, int count, String &data);
      static void Send_(SOCKET client_socket, const AnsiString &data);

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;
   };
}
