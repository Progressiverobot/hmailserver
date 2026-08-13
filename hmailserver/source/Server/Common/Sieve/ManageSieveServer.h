// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <thread>
#include <string>

namespace HM
{
   class SSLCertificate;

   // Optional ManageSieve (RFC 5804) listener for uploading and managing
   // per-account Sieve scripts. Implemented as a raw-socket + std::thread service
   // (like MetricsServer / RestApiServer), enabled with
   // [Settings] ManageSieveServerPort in hMailServer.ini and disabled (port 0) by
   // default. Authentication is SASL PLAIN against the regular account database.
   //
   // TLS: STARTTLS (RFC 5804 2.2) is offered when a TLS certificate is available,
   // and the SSL context it uses is built by SslContextInitializer - the same
   // function, reading the same settings, that configures TLS for SMTP, POP3 and
   // IMAP. That is deliberate and load bearing: it is the only way the cipher
   // list, the TLS version floor and the key-exchange groups (including the
   // hybrid post-quantum KEMs in TlsKeyExchangeGroups) cannot silently diverge
   // between this listener and the protocols people actually read the release
   // notes about. The bridge is one cast: SslContextInitializer works on a
   // boost::asio::ssl::context, and this listener's blocking sockets use the
   // SSL_CTX underneath it (context::native_handle). Re-hosting the listener on
   // the shared Boost.Asio stack, so that it stops being a separate accept loop
   // at all, is Phase 1 work - see the comment above InitializeTls_.
   //
   // With no certificate available STARTTLS is not advertised and the listener
   // behaves exactly as it did before: plain text only, so bind to 127.0.0.1
   // unless fronted by a TLS terminator.
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

      // Upper bound on a SASL response, in either of the two forms RFC 5804
      // allows it to arrive in - a literal, or a quoted string on the command
      // line. A base64-encoded PLAIN response holding a real address and password
      // is an order of magnitude smaller; the cap stops an unauthenticated client
      // from making us buffer an arbitrary amount of data. It applies to the
      // inline form too because a cap that only covered the literal form was no
      // cap at all: the same payload sent as a quoted string was bounded only by
      // the one-megabyte line guard in ReadLine_.
      enum { MaxSaslResponseSize = 8192 };

      // Commands accepted from a client that has not authenticated yet, after
      // which the session is closed.
      //
      // This is not the same protection as MaxAuthenticationFailures, which only
      // counts commands that actually carried a credential - CAPABILITY, NOOP and
      // AUTHENTICATE naming an unknown mechanism were all unlimited. That matters
      // more on this listener than on the mailbox protocols because Run_ serves
      // connections one at a time on a single worker thread, so an unauthenticated
      // client looping NOOP does not merely waste its own connection: it holds the
      // whole ManageSieve service. Set far above any real client, which needs at
      // most CAPABILITY, STARTTLS, CAPABILITY and AUTHENTICATE.
      enum { MaxPreAuthenticationCommands = 25 };

      // Largest script PUTSCRIPT and CHECKSCRIPT will accept, and the figure
      // HAVESPACE answers against. One megabyte is the same order as the limit
      // other ManageSieve implementations ship with, and a Sieve script that
      // approaches it is generated rather than written.
      //
      // Deliberately a constant and not a setting. The value only has to be high
      // enough that no genuine script meets it and low enough that a malicious one
      // cannot make the server buffer without bound, and no operator has a reason
      // to pick a number in between - whereas the previous behaviour, where the
      // only bound was the ten-megabyte buffer guard inside ReadBytes_ and
      // exceeding it closed the connection without a response, had no defensible
      // value at all.
      enum { MaxScriptSize = 1024 * 1024 };

      // The outcome of a single AUTHENTICATE command.
      enum AuthenticationOutcome
      {
         AuthenticationSucceeded,
         AuthenticationFailed,    // keep the connection, the client may retry
         AuthenticationAborted    // the connection must be closed
      };

      // One accepted client connection: the socket, the bytes read from it but not
      // yet consumed, and - only after a completed STARTTLS handshake - the
      // OpenSSL session that every subsequent read and write has to go through.
      // Grouping the three means no code path can read past the TLS upgrade and
      // still be talking to the bare socket.
      struct Connection
      {
         explicit Connection(SOCKET client_socket) :
            socket(client_socket),
            tls_session(nullptr)
         {
         }

         // Owns a socket and an SSL object, so copying it would mean closing and
         // freeing both twice. Nothing here needs a copy; say so rather than leave
         // it to a future caller to find out.
         Connection(const Connection &) = delete;
         Connection &operator=(const Connection &) = delete;

         SOCKET socket;
         SSL *tls_session;
         std::string buffer;
      };

      void Run_();
      void HandleClient_(Connection &connection, const IPAddress &client_address);

      // Releases the TLS session, if any, and closes the socket. Called from the
      // accept loop rather than from HandleClient_, so that the exception barrier
      // firing cannot leak a socket or an SSL object.
      static void CloseConnection_(Connection &connection);

      // Accept-time IP restriction check. This is the equivalent of what
      // TCPServer::HandleAccept does for SMTP/POP3/IMAP and is what makes an
      // auto-ban actually stop a reconnecting client.
      static bool IsConnectionAllowed_(const IPAddress &client_address);

      static IPAddress GetClientAddress_(const sockaddr *address);

      // Builds the shared server SSL context for STARTTLS. False - which is not an
      // error - simply means STARTTLS is not advertised.
      bool InitializeTls_(const String &bind_address, int port);

      // The certificate this listener presents. There is no ManageSieve-specific
      // certificate setting, so it is borrowed from the TLS-capable mailbox ports.
      static std::shared_ptr<SSLCertificate> FindTlsCertificate_();

      // Performs the server side of the TLS handshake on an accepted connection.
      bool StartTls_(Connection &connection);

      void SendCapabilities_(Connection &connection) const;

      // Handles one AUTHENTICATE command. All credential-carrying paths run
      // through here so that no failure can escape the auto-ban accounting or
      // the per-connection cap. On success, account_address holds the address of
      // the account that was logged on to.
      //
      // Only ever called for a client that has not authenticated yet: HandleClient_
      // refuses a second AUTHENTICATE outright, because a success here sets
      // authentication_failures back to zero, and a client able to reach that path
      // repeatedly could clear the cap between guesses.
      AuthenticationOutcome HandleAuthenticate_(Connection &connection,
                                                const IPAddress &client_address,
                                                const String &line,
                                                int pos,
                                                int &authentication_failures,
                                                String &account_address);

      // Counts a failed attempt, sends the response and reports whether the
      // connection has run out of attempts and must be closed.
      static bool ApplyAuthenticationFailure_(Connection &connection,
                                              int &authentication_failures,
                                              bool disconnect,
                                              const AnsiString &failure_response);

      static bool ReadLine_(Connection &connection, String &line);
      static bool ReadBytes_(Connection &connection, int count, String &data);
      static void Send_(Connection &connection, const AnsiString &data);

      // Single point where bytes come off the wire, so that the plain-socket and
      // TLS cases cannot get out of step.
      static int Receive_(Connection &connection, char *buffer, int size);

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;

      // Held as a Boost.Asio context only because that is what
      // SslContextInitializer configures; the SSL_CTX inside it is what this
      // listener uses. Null when no certificate is available, which is what
      // "STARTTLS is not offered" means. Written by Start() before the worker
      // thread exists and cleared by Stop() after it has been joined, so the
      // worker never sees it change.
      std::shared_ptr<boost::asio::ssl::context> tls_context_;
   };
}
