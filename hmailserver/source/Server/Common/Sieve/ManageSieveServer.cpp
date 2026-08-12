// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "ManageSieveServer.h"

#include "SieveStorage.h"
#include "SieveScript.h"

#include "../BO/Account.h"
#include "../BO/SecurityRange.h"
#include "../BO/SSLCertificate.h"
#include "../BO/SSLCertificates.h"
#include "../BO/TCPIPPort.h"
#include "../BO/TCPIPPorts.h"
#include "../Persistence/PersistentSecurityRange.h"
#include "../Util/PasswordValidator.h"
#include "../Util/AccountLogon.h"
#include "../Util/ServerStatus.h"
#include "../Util/Parsing/StringParser.h"
#include "../TCPIP/IPAddress.h"
#include "../TCPIP/SocketConstants.h"
#include "../TCPIP/SslContextInitializer.h"

#include <ws2tcpip.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      void SkipSpaces(const String &line, int &pos)
      {
         while (pos < line.GetLength() && (line[pos] == L' ' || line[pos] == L'\t'))
            pos++;
      }

      // Parses the next token (a quoted string or a bare atom) from the line.
      bool ParseToken(const String &line, int &pos, String &token)
      {
         token = _T("");
         SkipSpaces(line, pos);

         if (pos >= line.GetLength())
            return false;

         if (line[pos] == L'"')
         {
            pos++; // opening quote
            while (pos < line.GetLength())
            {
               wchar_t ch = line[pos];
               if (ch == L'\\')
               {
                  pos++;
                  if (pos < line.GetLength())
                  {
                     token += line[pos];
                     pos++;
                  }
                  continue;
               }
               if (ch == L'"')
               {
                  pos++; // closing quote
                  return true;
               }
               token += ch;
               pos++;
            }
            return false; // unterminated quoted string
         }

         while (pos < line.GetLength() && line[pos] != L' ' && line[pos] != L'\t')
         {
            token += line[pos];
            pos++;
         }

         return !token.IsEmpty();
      }

      // Detects a trailing literal marker {NNN} or {NNN+} and returns its size.
      bool ParseLiteralSize(const String &line, int &size)
      {
         size = 0;
         int brace = line.ReverseFind(L'{');
         if (brace < 0)
            return false;

         int close = line.Find(_T("}"), brace);
         if (close < 0)
            return false;

         String inner = line.Mid(brace + 1, close - brace - 1);
         inner.TrimRight(_T("+"));

         if (inner.IsEmpty())
            return false;

         for (int i = 0; i < inner.GetLength(); i++)
         {
            if (inner[i] < L'0' || inner[i] > L'9')
               return false;
         }

         size = _wtoi(inner.c_str());
         return true;
      }

      // The Sieve extensions named in the RFC 5804 "SIEVE" capability line.
      //
      // Every ManageSieve client builds its user interface from this line -
      // Roundcube's managesieve plugin most of all, which is how most people ever
      // see a Sieve filter. So the rule here is: a name belongs in this list only
      // once using it end to end actually does something. Listing an extension we
      // merely parse would put a control in front of the user that quietly has no
      // effect, which is worse than not offering it at all.
      //
      // SieveParser::IsSupportedExtension deliberately accepts more than this: the
      // engine also implements "vacation", "imap4flags" and "envelope", and each
      // needs exactly one more step in the delivery path before it is real:
      //
      //   vacation    LocalDelivery::EvaluateSieveScript_ must act on the
      //               "vacation" summary token (or better, on
      //               SieveResult::vacation) by calling
      //               SMTPVacationMessageCreator::CreateVacationMessage.
      //   imap4flags  ... must act on the "flags:" summary token when it stores
      //               the local copy of the message.
      //   envelope    ... must pass the SMTP envelope into the evaluator, so that
      //               envelope "to" works without the Delivered-To header, which
      //               is off in the shipped configuration.
      //
      // Move a name in here in the same commit that lands its delivery-side step.
      // Not before.
      const char *AdvertisedSieveExtensions = "fileinto copy relational subaddress";

      AnsiString EscapeQuoted(const String &value)
      {
         String escaped;
         for (int i = 0; i < value.GetLength(); i++)
         {
            wchar_t ch = value[i];
            if (ch == L'"' || ch == L'\\')
               escaped += L'\\';
            escaped += ch;
         }

         AnsiString result = escaped;
         return result;
      }
   }

   ManageSieveServer::ManageSieveServer() :
      listen_socket_(INVALID_SOCKET),
      running_(false)
   {
   }

   ManageSieveServer::~ManageSieveServer()
   {
      Stop();
   }

   bool
   ManageSieveServer::Start(const String &bind_address, int port)
   {
      if (running_)
         return true;

      AnsiString narrowBindAddress = bind_address;

      sockaddr_in address = {};
      address.sin_family = AF_INET;
      address.sin_port = htons(static_cast<unsigned short>(port));

      if (inet_pton(AF_INET, narrowBindAddress.c_str(), &address.sin_addr) != 1)
      {
         LOG_APPLICATION("ManageSieveServer: Invalid bind address: " + bind_address);
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
         message.Format(_T("ManageSieveServer: Failed to bind to %s:%d."), bind_address.c_str(), port);
         LOG_APPLICATION(message);

         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
         return false;
      }

      // Before the worker thread exists, so the thread only ever reads a context
      // that is already fully built. A failure here is not a failure to start: the
      // listener runs without STARTTLS, exactly as it always has.
      bool tlsAvailable = InitializeTls_(bind_address, port);

      running_ = true;
      worker_ = std::thread(&ManageSieveServer::Run_, this);

      String message;
      message.Format(_T("ManageSieveServer: Listening on %s:%d (%s)."), bind_address.c_str(), port,
         tlsAvailable ? _T("STARTTLS offered") : _T("plain text only, no TLS certificate available"));
      LOG_APPLICATION(message);

      return true;
   }

   void
   ManageSieveServer::Stop()
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

      // After the join: the worker reads the context for every STARTTLS, so
      // releasing it while that thread was still alive would be a use-after-free.
      // Dropped rather than kept, so that a Start() after a configuration change
      // picks up the current certificate and TLS settings.
      tls_context_.reset();
   }

   bool
   ManageSieveServer::InitializeTls_(const String &bind_address, int port)
   {
      // Why this goes through SslContextInitializer rather than doing its own
      // SSL_CTX_new: that function is the single place SMTP, POP3 and IMAP get
      // their TLS configuration from - the cipher list, the enabled TLS versions,
      // the server-preference and ChaCha options, the DH parameters and the
      // key-exchange group list, which since this month carries the hybrid
      // post-quantum KEMs from [Settings] TlsKeyExchangeGroups. Any listener that
      // builds its own context re-implements that list and then diverges from it
      // the next time it changes, silently, in the direction of weaker security -
      // which is exactly what the optional listeners have done up to now.
      //
      // The bridge between the two stacks is deliberately as thin as it can be:
      // SslContextInitializer takes a boost::asio::ssl::context, so one is built
      // here and handed to it, and the raw SSL_CTX it wraps (native_handle) is
      // what this listener's blocking sockets hand to SSL_new. Nothing about the
      // configuration is restated locally, so nothing local can drift.
      //
      // This is the small correct thing, not the finished thing. The listener is
      // still its own std::thread accept loop with blocking reads, so it does not
      // get the shared listener's session accounting, its OnAccept scripting event
      // or its asynchronous timeouts. Re-hosting it on the Boost.Asio stack as a
      // proper TCPConnection - which would make this function disappear entirely -
      // is Phase 1 of the next-generation programme.
      try
      {
         std::shared_ptr<SSLCertificate> certificate = FindTlsCertificate_();

         if (!certificate)
         {
            // Deliberately the application log and not a reported error. An install
            // with no TLS-enabled mailbox port has no certificate for this listener
            // to present, and that is the shipped configuration - a diagnostic here
            // would fire on a default install, which is what every ERROR-log
            // fixture in the suite (and every operator's alerting) would then have
            // to learn to ignore.
            LOG_APPLICATION("ManageSieveServer: No TLS certificate is configured on an IMAP, POP3 or SMTP port, so STARTTLS will not be offered. Bind the listener to 127.0.0.1, or put it behind a TLS terminator.");
            return false;
         }

         std::shared_ptr<boost::asio::ssl::context> context =
            std::shared_ptr<boost::asio::ssl::context>(new boost::asio::ssl::context(boost::asio::ssl::context::sslv23));

         // Same method (sslv23 plus the option mask below), same settings, same
         // function as TCPServer::Run uses for the mailbox protocols.
         if (!SslContextInitializer::InitServer(*context, certificate, bind_address, port))
         {
            // InitServer has already reported the specific failure - a missing
            // certificate file, an unreadable private key. Nothing is added to the
            // ERROR log here; this line only records the consequence for this
            // listener, and the same certificate failing for the mailbox ports is
            // reported there too, so this cannot be a new class of error.
            LOG_APPLICATION("ManageSieveServer: The shared TLS configuration could not be applied to this listener, so STARTTLS will not be offered.");
            return false;
         }

         tls_context_ = context;

         LOG_APPLICATION("ManageSieveServer: STARTTLS enabled using the certificate '" + certificate->GetName() + "' and the shared TLS configuration.");

         return true;
      }
      catch (...)
      {
         // Constructing the context, or reading the configuration, can throw. This
         // runs on the startup path, where an escape would take the whole service
         // down before a single listener is up - so it is contained here and costs
         // nothing but STARTTLS.
         LOG_APPLICATION("ManageSieveServer: An exception was raised while preparing TLS for this listener. STARTTLS will not be offered.");
         return false;
      }
   }

   std::shared_ptr<SSLCertificate>
   ManageSieveServer::FindTlsCertificate_()
   {
      // There is no ManageSieve-specific certificate setting, and adding one would
      // mean an ini setting plus an administration-UI change for a listener that is
      // off by default. So the certificate is borrowed from the TLS-capable mailbox
      // ports, in the same preference order that IsConnectionAllowed_ reasons about
      // access: IMAP, then POP3, then SMTP.
      //
      // That is the right default rather than a convenient one. The client editing
      // Sieve filters is the same client reading the mailbox, from the same host
      // name, so the certificate it already trusts is the one to present - and it is
      // renewed by the same ACME run or the same manual replacement, so ManageSieve
      // cannot end up serving a certificate that expired months ago.
      std::shared_ptr<TCPIPPorts> ports = Configuration::Instance()->GetTCPIPPorts();
      std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();

      if (!ports || !certificates)
         return std::shared_ptr<SSLCertificate>();

      const SessionType preferenceOrder[] = { STIMAP, STPOP3, STSMTP };

      std::vector<std::shared_ptr<TCPIPPort> > portVector = ports->GetVector();

      for (SessionType sessionType : preferenceOrder)
      {
         for (std::shared_ptr<TCPIPPort> port : portVector)
         {
            if (!port || port->GetProtocol() != sessionType)
               continue;

            ConnectionSecurity connectionSecurity = port->GetConnectionSecurity();

            if (connectionSecurity != CSSSL &&
                connectionSecurity != CSSTARTTLSOptional &&
                connectionSecurity != CSSTARTTLSRequired)
               continue;

            std::shared_ptr<SSLCertificate> certificate = certificates->GetItemByDBID(port->GetSSLCertificateID());

            if (!certificate)
               continue;

            // A row with no files is worse than no row at all: SslContextInitializer
            // would report a High error for a certificate the operator never asked
            // this listener to use. Skip it and keep looking.
            if (certificate->GetCertificateFile().IsEmpty() || certificate->GetPrivateKeyFile().IsEmpty())
               continue;

            return certificate;
         }
      }

      return std::shared_ptr<SSLCertificate>();
   }

   void
   ManageSieveServer::Run_()
   {
      for (;;)
      {
         // Keep the peer address: failed authentications have to be reported to
         // the same per-IP auto-ban accounting the other protocols use, and the
         // ban that accounting creates has to be enforced right here.
         sockaddr_in6 clientAddress = {};
         int addressLength = sizeof(clientAddress);

         SOCKET clientSocket = accept(listen_socket_, (sockaddr*) &clientAddress, &addressLength);

         if (clientSocket == INVALID_SOCKET)
         {
            if (!running_)
               return;

            continue;
         }

         Connection connection(clientSocket);

         // This function is the top of a std::thread. There is nothing above it to
         // catch anything, so an exception that escaped here would be
         // std::terminate() - the entire mail server, SMTP and IMAP and POP3
         // included, killed because someone uploaded a Sieve script. Everything the
         // handler touches is fenced off behind this barrier: the security-range
         // lookup and the logon, which are database reads; the script storage, which
         // is file I/O; the syntax checker; and all the string building. The socket
         // is deliberately closed out here rather than inside the handler, so the
         // barrier firing cannot leak a handle or an SSL object either.
         //
         // The catch blocks below only record what happened; they do not report it.
         // Reporting has to wait until after the connection is closed and has to be
         // fenced off in a barrier of its own, because ErrorManager::ReportError
         // reaches ScriptServer::FireEvent and so runs operator-supplied
         // VBScript/JScript. An exception out of that script would otherwise escape
         // this function - the std::terminate() this barrier exists to prevent - and
         // would skip the close below, leaking a socket per failed connection.
         bool exceptionEscaped = false;
         bool exceptionWasStandard = false;
         AnsiString exceptionText;

         try
         {
            IPAddress clientIPAddress = GetClientAddress_((sockaddr*) &clientAddress);

            if (IsConnectionAllowed_(clientIPAddress))
            {
               HandleClient_(connection, clientIPAddress);
            }
            else
            {
               // Drop the connection before the greeting is sent and before a single
               // command is read, the way the Boost.Asio listener does for the other
               // protocols. Nothing is written back: a blocked client learns no more
               // than that the port stopped answering.
               String message;
               message.Format(_T("ManageSieveServer: Connection from %s was not accepted. Blocked by IP range."),
                  String(clientIPAddress.ToString()).c_str());
               LOG_DEBUG(message);
            }
         }
         catch (const std::exception &error)
         {
            exceptionEscaped = true;
            exceptionWasStandard = true;

            // Copying the text allocates, and the exception in hand may well be the
            // bad_alloc that says allocation is failing. A throw here would escape
            // the catch block itself, so it gets its own fence; the message is then
            // simply omitted.
            try
            {
               exceptionText = error.what();
            }
            catch (...)
            {
            }
         }
         catch (...)
         {
            exceptionEscaped = true;
         }

         CloseConnection_(connection);

         if (exceptionEscaped)
         {
            try
            {
               if (exceptionWasStandard)
               {
                  String description = _T("An exception escaped while serving a ManageSieve connection. The connection was abandoned; the listener is still running.");

                  if (!exceptionText.IsEmpty())
                     description = Formatter::Format(_T("{0}, Message: {1}"), description, exceptionText);

                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5830, "ManageSieveServer::Run_", description);
               }
               else
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5830, "ManageSieveServer::Run_",
                     "A non-standard exception escaped while serving a ManageSieve connection. The connection was abandoned; the listener is still running.");
               }
            }
            catch (...)
            {
               // Nothing left to do: reporting is what failed, and the alternative
               // to swallowing this is std::terminate() on the mail server.
            }
         }
      }
   }

   void
   ManageSieveServer::CloseConnection_(Connection &connection)
   {
      if (connection.tls_session != nullptr)
      {
         // One attempt at close_notify, not a loop: this also runs on the path where
         // the exception barrier has just fired, and a peer that is not reading must
         // not be able to hold the accept loop here. The send timeout bounds it.
         SSL_shutdown(connection.tls_session);
         SSL_free(connection.tls_session);
         connection.tls_session = nullptr;
      }

      if (connection.socket != INVALID_SOCKET)
      {
         shutdown(connection.socket, SD_BOTH);
         closesocket(connection.socket);
         connection.socket = INVALID_SOCKET;
      }
   }

   bool
   ManageSieveServer::IsConnectionAllowed_(const IPAddress &client_address)
   {
      // Parity with TCPServer::HandleAccept: look the peer up in the security
      // ranges and only continue if a range matches and permits the connection.
      // This is what gives AccountLogon::RegisterFailedLogin teeth on this
      // listener - the auto-ban it creates is a security range with no protocols
      // enabled, so until this check existed an attacker simply reconnected and
      // carried on guessing passwords, making the per-connection cap pointless.
      if (client_address.IsAny())
      {
         // The peer address could not be determined, so it cannot be matched
         // against the ranges either. Fail closed.
         return false;
      }

      std::shared_ptr<SecurityRange> securityRange = PersistentSecurityRange::ReadMatchingIP(client_address);

      if (!securityRange)
         return false;

      // There is no ManageSieve-specific IP range option and adding one would mean
      // a schema and administration-UI change. ManageSieve does nothing but
      // manipulate the filters of a mailbox, so it is gated on the range allowing
      // mailbox access at all. Every default and normal range that permits IMAP or
      // POP3 keeps working; an auto-ban range permits neither, so it blocks
      // ManageSieve as intended.
      return securityRange->GetAllowIMAP() || securityRange->GetAllowPOP3();
   }

   IPAddress
   ManageSieveServer::GetClientAddress_(const sockaddr *address)
   {
      IPAddress result;

      if (address == nullptr)
         return result;

      wchar_t buffer[INET6_ADDRSTRLEN] = {};

      if (address->sa_family == AF_INET6)
      {
         const sockaddr_in6 *v6 = reinterpret_cast<const sockaddr_in6*>(address);
         if (InetNtopW(AF_INET6, (PVOID) &v6->sin6_addr, buffer, INET6_ADDRSTRLEN) != nullptr)
            result.TryParse(String(buffer), true);
      }
      else if (address->sa_family == AF_INET)
      {
         const sockaddr_in *v4 = reinterpret_cast<const sockaddr_in*>(address);
         if (InetNtopW(AF_INET, (PVOID) &v4->sin_addr, buffer, INET6_ADDRSTRLEN) != nullptr)
            result.TryParse(String(buffer), true);
      }

      return result;
   }

   bool
   ManageSieveServer::StartTls_(Connection &connection)
   {
      if (!tls_context_)
         return false;

      // native_handle() is the SSL_CTX SslContextInitializer configured. Taking it
      // rather than making one means the session below inherits the shared cipher
      // list, TLS version floor and key-exchange groups without this file naming
      // any of them.
      SSL *session = SSL_new(tls_context_->native_handle());

      if (session == nullptr)
      {
         ServerStatus::Instance()->OnTlsHandshakeFailed();
         return false;
      }

      SSL_set_fd(session, static_cast<int>(connection.socket));

      if (SSL_accept(session) != 1)
      {
         // A failed handshake is client-driven - an unsupported TLS version, a
         // rejected certificate, a port scanner - so it is a debug note rather than
         // a reported error. It is counted in the same handshake-failure metric the
         // other listeners feed, which is where a real problem shows up as a rate.
         ServerStatus::Instance()->OnTlsHandshakeFailed();

         // Clear this thread's OpenSSL error queue. It is shared with every other
         // OpenSSL call on this thread, so a failure left behind here would later be
         // reported against something unrelated.
         ERR_clear_error();

         SSL_free(session);
         return false;
      }

      ServerStatus::Instance()->OnTlsHandshakeCompleted();

      connection.tls_session = session;

      return true;
   }

   int
   ManageSieveServer::Receive_(Connection &connection, char *buffer, int size)
   {
      if (connection.tls_session != nullptr)
         return SSL_read(connection.tls_session, buffer, size);

      return recv(connection.socket, buffer, size, 0);
   }

   void
   ManageSieveServer::Send_(Connection &connection, const AnsiString &data)
   {
      if (connection.tls_session != nullptr)
      {
         SSL_write(connection.tls_session, data.c_str(), static_cast<int>(data.GetLength()));
         return;
      }

      send(connection.socket, data.c_str(), static_cast<int>(data.GetLength()), 0);
   }

   bool
   ManageSieveServer::ReadLine_(Connection &connection, String &line)
   {
      for (;;)
      {
         size_t pos = connection.buffer.find("\r\n");
         if (pos != std::string::npos)
         {
            line = AnsiString(connection.buffer.substr(0, pos).c_str());
            connection.buffer.erase(0, pos + 2);
            return true;
         }

         // Guard against an unterminated, unbounded line.
         if (connection.buffer.size() > 1024 * 1024)
            return false;

         char temp[4096];
         int received = Receive_(connection, temp, static_cast<int>(sizeof(temp)));
         if (received <= 0)
            return false;

         connection.buffer.append(temp, received);
      }
   }

   bool
   ManageSieveServer::ReadBytes_(Connection &connection, int count, String &data)
   {
      while (static_cast<int>(connection.buffer.size()) < count)
      {
         char temp[4096];
         int received = Receive_(connection, temp, static_cast<int>(sizeof(temp)));
         if (received <= 0)
            return false;

         connection.buffer.append(temp, received);

         if (connection.buffer.size() > 10 * 1024 * 1024)
            return false;
      }

      data = AnsiString(connection.buffer.substr(0, count).c_str());
      connection.buffer.erase(0, count);
      return true;
   }

   void
   ManageSieveServer::SendCapabilities_(Connection &connection) const
   {
      Send_(connection, "\"IMPLEMENTATION\" \"hMailServer ManageSieve\"\r\n");
      Send_(connection, AnsiString("\"SIEVE\" \"") + AdvertisedSieveExtensions + "\"\r\n");

      // RFC 5804 2.2: STARTTLS is advertised only while it can actually be used -
      // before the handshake, and only when a certificate is available. A client
      // that sees it and cannot get it would have no way to tell a configuration
      // problem from a downgrade attack.
      if (tls_context_ && connection.tls_session == nullptr)
         Send_(connection, "\"STARTTLS\"\r\n");

      Send_(connection, "\"SASL\" \"PLAIN\"\r\n");
      Send_(connection, "\"VERSION\" \"1.0\"\r\n");
   }

   bool
   ManageSieveServer::ApplyAuthenticationFailure_(Connection &connection, int &authentication_failures, bool disconnect, const AnsiString &failure_response)
   {
      authentication_failures++;

      if (disconnect || authentication_failures >= MaxAuthenticationFailures)
      {
         Send_(connection, "NO \"Too many invalid logon attempts.\"\r\n");
         return true;
      }

      Send_(connection, failure_response);
      return false;
   }

   ManageSieveServer::AuthenticationOutcome
   ManageSieveServer::HandleAuthenticate_(Connection &connection, const IPAddress &client_address, const String &line, int pos, int &authentication_failures, String &account_address)
   {
      String mechanism;
      ParseToken(line, pos, mechanism);
      mechanism.ToUpper();

      if (mechanism != _T("PLAIN"))
      {
         Send_(connection, "NO \"Unsupported SASL mechanism.\"\r\n");
         return AuthenticationFailed;
      }

      // RFC 5804 permits the SASL response either as a quoted string on the command
      // line or as a literal whose bytes follow it. Both forms are handled here so
      // that every path carrying credentials shares the failure accounting below.
      // Previously the literal form was rejected as an invalid SASL response and,
      // worse, its unread bytes were then parsed as commands.
      String saslResponse;

      int literalStart = pos;
      SkipSpaces(line, literalStart);

      int literalSize = 0;
      if (literalStart < line.GetLength() && line[literalStart] == L'{' && ParseLiteralSize(line, literalSize))
      {
         if (literalSize > MaxSaslResponseSize)
         {
            // Refuse without reading it. Since the announced bytes are left unread
            // the command stream can no longer be trusted, so the connection goes
            // rather than being allowed to desynchronize.
            Send_(connection, "NO \"SASL response too large.\"\r\n");
            return AuthenticationAborted;
         }

         if (!ReadBytes_(connection, literalSize, saslResponse))
            return AuthenticationAborted;

         // Consume the CRLF that terminates the command after the literal.
         String trailing;
         ReadLine_(connection, trailing);
      }
      else if (!ParseToken(line, pos, saslResponse))
      {
         Send_(connection, "NO \"PLAIN requires an initial response.\"\r\n");
         return AuthenticationFailed;
      }

      String authzid, authcid, password;
      if (!StringParser::DecodeSaslPlain(saslResponse, authzid, authcid, password))
      {
         // Counted against this connection, but deliberately not reported to the
         // per-IP auto-ban: a response we cannot even decode carries no password
         // guess, and banning on it would let a broken client lock its own address
         // out of the server.
         if (ApplyAuthenticationFailure_(connection, authentication_failures, false, "NO \"Invalid SASL response.\"\r\n"))
            return AuthenticationAborted;

         return AuthenticationFailed;
      }

      String username;
      if (!StringParser::SaslPrep(authcid, username))
         username = authcid;

      // The authzid is intentionally ignored rather than passed on as a masquerade
      // name: honouring it would let anyone holding one valid account's password
      // manage a different account's scripts.
      //
      // Going through AccountLogon rather than PasswordValidator directly is what
      // keeps this listener in step with SMTP/POP3/IMAP: it maintains the logon
      // statistics and, on failure, feeds the per-IP auto-ban accounting that the
      // accept-time check in IsConnectionAllowed_ then enforces.
      AccountLogon accountLogon;
      bool disconnect = false;
      std::shared_ptr<const Account> account = accountLogon.Logon(client_address, username, password, disconnect);

      if (!account)
      {
         if (ApplyAuthenticationFailure_(connection, authentication_failures, disconnect, "NO \"Authentication failed.\"\r\n"))
            return AuthenticationAborted;

         return AuthenticationFailed;
      }

      account_address = account->GetAddress();
      authentication_failures = 0;
      Send_(connection, "OK \"Authentication successful.\"\r\n");

      return AuthenticationSucceeded;
   }

   void
   ManageSieveServer::HandleClient_(Connection &connection, const IPAddress &client_address)
   {
      DWORD timeout = 30000;
      setsockopt(connection.socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(connection.socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      bool authenticated = false;
      String accountAddress;
      int authentication_failures = 0;

      // Greeting: advertise capabilities then an OK.
      SendCapabilities_(connection);
      Send_(connection, "OK \"hMailServer ManageSieve ready.\"\r\n");

      for (;;)
      {
         String line;
         if (!ReadLine_(connection, line))
            break;

         int pos = 0;
         String command;
         if (!ParseToken(line, pos, command))
         {
            Send_(connection, "NO \"Empty command.\"\r\n");
            continue;
         }
         command.ToUpper();

         if (command == _T("LOGOUT"))
         {
            Send_(connection, "OK \"Bye.\"\r\n");
            break;
         }

         if (command == _T("CAPABILITY"))
         {
            SendCapabilities_(connection);
            Send_(connection, "OK\r\n");
            continue;
         }

         if (command == _T("NOOP"))
         {
            Send_(connection, "OK\r\n");
            continue;
         }

         if (command == _T("STARTTLS"))
         {
            if (!tls_context_)
            {
               Send_(connection, "NO \"STARTTLS is not available: no TLS certificate is configured.\"\r\n");
               continue;
            }

            if (connection.tls_session != nullptr)
            {
               Send_(connection, "NO \"TLS is already active on this connection.\"\r\n");
               continue;
            }

            if (authenticated)
            {
               // RFC 5804 2.2 has the client discard everything it knew about the
               // session across the upgrade, which includes its authentication.
               // Rather than silently dropping an authenticated session's identity,
               // refuse: a client that wants TLS must ask for it before it logs on.
               Send_(connection, "NO \"STARTTLS must be issued before authenticating.\"\r\n");
               continue;
            }

            if (!connection.buffer.empty())
            {
               // Anything already buffered arrived in clear text before the handshake
               // and would otherwise be executed afterwards as though it had been
               // protected by it - the plaintext command injection that STARTTLS
               // implementations get wrong. There is nothing to salvage: the stream
               // is no longer trustworthy, so the connection ends.
               Send_(connection, "NO \"Unexpected data was sent after STARTTLS.\"\r\n");
               break;
            }

            Send_(connection, "OK \"Begin TLS negotiation now.\"\r\n");

            if (!StartTls_(connection))
            {
               LOG_DEBUG("ManageSieveServer: The TLS handshake failed. Closing the connection.");
               break;
            }

            // RFC 5804 2.2: the server sends a new capability response once the
            // handshake completes, because what it can offer has changed (STARTTLS
            // is gone from it, and a client that refuses to send credentials in
            // clear text will only now look at "SASL").
            //
            // authentication_failures is deliberately *not* reset here. It is the
            // per-connection attempt cap, and resetting it would hand a client a
            // free extra round of password guesses for the price of one STARTTLS -
            // exactly the sidestep IsConnectionAllowed_ exists to prevent.
            SendCapabilities_(connection);
            Send_(connection, "OK \"TLS negotiation successful.\"\r\n");

            continue;
         }

         if (command == _T("AUTHENTICATE"))
         {
            String authenticatedAddress;
            AuthenticationOutcome outcome = HandleAuthenticate_(connection, client_address, line, pos,
               authentication_failures, authenticatedAddress);

            if (outcome == AuthenticationAborted)
               break;

            if (outcome == AuthenticationSucceeded)
            {
               authenticated = true;
               accountAddress = authenticatedAddress;
            }

            continue;
         }

         // All remaining commands require authentication.
         if (!authenticated)
         {
            Send_(connection, "NO \"Authentication required.\"\r\n");
            continue;
         }

         if (command == _T("LISTSCRIPTS"))
         {
            std::vector<String> names = SieveStorage::ListScriptNames(accountAddress);
            String activeName = SieveStorage::GetActiveScriptName(accountAddress);

            for (const String &name : names)
            {
               AnsiString responseLine = "\"" + EscapeQuoted(name) + "\"";
               if (name.CompareNoCase(activeName) == 0)
                  responseLine += " ACTIVE";
               responseLine += "\r\n";
               Send_(connection, responseLine);
            }

            Send_(connection, "OK\r\n");
            continue;
         }

         if (command == _T("PUTSCRIPT"))
         {
            String name;
            if (!ParseToken(line, pos, name) || !SieveStorage::IsValidScriptName(name))
            {
               Send_(connection, "NO \"Invalid script name.\"\r\n");
               continue;
            }

            int literalSize = 0;
            if (!ParseLiteralSize(line, literalSize))
            {
               Send_(connection, "NO \"Expected a script literal.\"\r\n");
               continue;
            }

            String content;
            if (!ReadBytes_(connection, literalSize, content))
               break;

            // Consume the CRLF that terminates the command after the literal.
            String trailing;
            ReadLine_(connection, trailing);

            String syntaxError = SieveScript::CheckSyntax(content);
            if (!syntaxError.IsEmpty())
            {
               Send_(connection, "NO \"" + EscapeQuoted(syntaxError) + "\"\r\n");
               continue;
            }

            if (SieveStorage::PutScript(accountAddress, name, content))
               Send_(connection, "OK\r\n");
            else
               Send_(connection, "NO \"Could not store the script.\"\r\n");
            continue;
         }

         if (command == _T("GETSCRIPT"))
         {
            String name;
            if (!ParseToken(line, pos, name) || !SieveStorage::ScriptExists(accountAddress, name))
            {
               Send_(connection, "NO \"There is no script by that name.\"\r\n");
               continue;
            }

            AnsiString content = SieveStorage::GetScript(accountAddress, name);

            AnsiString response;
            response.Format("{%d}\r\n", content.GetLength());
            Send_(connection, response);
            Send_(connection, content);
            Send_(connection, "\r\nOK\r\n");
            continue;
         }

         if (command == _T("SETACTIVE"))
         {
            String name;
            ParseToken(line, pos, name);

            if (name.IsEmpty())
            {
               SieveStorage::SetActiveScriptName(accountAddress, _T(""));
               Send_(connection, "OK\r\n");
               continue;
            }

            if (!SieveStorage::ScriptExists(accountAddress, name))
            {
               Send_(connection, "NO \"There is no script by that name.\"\r\n");
               continue;
            }

            SieveStorage::SetActiveScriptName(accountAddress, name);
            Send_(connection, "OK\r\n");
            continue;
         }

         if (command == _T("DELETESCRIPT"))
         {
            String name;
            if (!ParseToken(line, pos, name) || !SieveStorage::ScriptExists(accountAddress, name))
            {
               Send_(connection, "NO \"There is no script by that name.\"\r\n");
               continue;
            }

            if (SieveStorage::DeleteScript(accountAddress, name))
               Send_(connection, "OK\r\n");
            else
               Send_(connection, "NO \"The active script cannot be deleted.\"\r\n");
            continue;
         }

         if (command == _T("CHECKSCRIPT"))
         {
            int literalSize = 0;
            if (!ParseLiteralSize(line, literalSize))
            {
               Send_(connection, "NO \"Expected a script literal.\"\r\n");
               continue;
            }

            String content;
            if (!ReadBytes_(connection, literalSize, content))
               break;

            String trailing;
            ReadLine_(connection, trailing);

            String syntaxError = SieveScript::CheckSyntax(content);
            if (syntaxError.IsEmpty())
               Send_(connection, "OK\r\n");
            else
               Send_(connection, "NO \"" + EscapeQuoted(syntaxError) + "\"\r\n");
            continue;
         }

         if (command == _T("HAVESPACE"))
         {
            // No per-account script quota is enforced.
            Send_(connection, "OK\r\n");
            continue;
         }

         Send_(connection, "NO \"Unknown command.\"\r\n");
      }

      // The socket and any TLS session are closed by the accept loop, in
      // CloseConnection_, so that the exception barrier cannot leak either.
   }
}
