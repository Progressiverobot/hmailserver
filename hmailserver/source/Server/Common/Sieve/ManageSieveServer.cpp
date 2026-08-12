// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "ManageSieveServer.h"

#include "SieveStorage.h"
#include "SieveScript.h"

#include "../BO/Account.h"
#include "../BO/SecurityRange.h"
#include "../Persistence/PersistentSecurityRange.h"
#include "../Util/PasswordValidator.h"
#include "../Util/AccountLogon.h"
#include "../Util/Parsing/StringParser.h"
#include "../TCPIP/IPAddress.h"

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

      running_ = true;
      worker_ = std::thread(&ManageSieveServer::Run_, this);

      String message;
      message.Format(_T("ManageSieveServer: Listening on %s:%d."), bind_address.c_str(), port);
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

         IPAddress clientIPAddress = GetClientAddress_((sockaddr*) &clientAddress);

         if (!IsConnectionAllowed_(clientIPAddress))
         {
            // Drop the connection before the greeting is sent and before a single
            // command is read, the way the Boost.Asio listener does for the other
            // protocols. Nothing is written back: a blocked client learns no more
            // than that the port stopped answering.
            String message;
            message.Format(_T("ManageSieveServer: Connection from %s was not accepted. Blocked by IP range."),
               String(clientIPAddress.ToString()).c_str());
            LOG_DEBUG(message);

            closesocket(clientSocket);
            continue;
         }

         HandleClient_(clientSocket, clientIPAddress);
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

   void
   ManageSieveServer::Send_(SOCKET client_socket, const AnsiString &data)
   {
      send(client_socket, data.c_str(), static_cast<int>(data.GetLength()), 0);
   }

   bool
   ManageSieveServer::ReadLine_(SOCKET client_socket, std::string &buffer, String &line)
   {
      for (;;)
      {
         size_t pos = buffer.find("\r\n");
         if (pos != std::string::npos)
         {
            line = AnsiString(buffer.substr(0, pos).c_str());
            buffer.erase(0, pos + 2);
            return true;
         }

         // Guard against an unterminated, unbounded line.
         if (buffer.size() > 1024 * 1024)
            return false;

         char temp[4096];
         int received = recv(client_socket, temp, sizeof(temp), 0);
         if (received <= 0)
            return false;

         buffer.append(temp, received);
      }
   }

   bool
   ManageSieveServer::ReadBytes_(SOCKET client_socket, std::string &buffer, int count, String &data)
   {
      while (static_cast<int>(buffer.size()) < count)
      {
         char temp[4096];
         int received = recv(client_socket, temp, sizeof(temp), 0);
         if (received <= 0)
            return false;

         buffer.append(temp, received);

         if (buffer.size() > 10 * 1024 * 1024)
            return false;
      }

      data = AnsiString(buffer.substr(0, count).c_str());
      buffer.erase(0, count);
      return true;
   }

   void
   ManageSieveServer::SendCapabilities_(SOCKET client_socket)
   {
      Send_(client_socket, "\"IMPLEMENTATION\" \"hMailServer ManageSieve\"\r\n");
      Send_(client_socket, "\"SIEVE\" \"fileinto\"\r\n");
      Send_(client_socket, "\"SASL\" \"PLAIN\"\r\n");
      Send_(client_socket, "\"VERSION\" \"1.0\"\r\n");
   }

   bool
   ManageSieveServer::ApplyAuthenticationFailure_(SOCKET client_socket, int &authentication_failures, bool disconnect, const AnsiString &failure_response)
   {
      authentication_failures++;

      if (disconnect || authentication_failures >= MaxAuthenticationFailures)
      {
         Send_(client_socket, "NO \"Too many invalid logon attempts.\"\r\n");
         return true;
      }

      Send_(client_socket, failure_response);
      return false;
   }

   ManageSieveServer::AuthenticationOutcome
   ManageSieveServer::HandleAuthenticate_(SOCKET client_socket, const IPAddress &client_address, const String &line, int pos, std::string &buffer, int &authentication_failures, String &account_address)
   {
      String mechanism;
      ParseToken(line, pos, mechanism);
      mechanism.ToUpper();

      if (mechanism != _T("PLAIN"))
      {
         Send_(client_socket, "NO \"Unsupported SASL mechanism.\"\r\n");
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
            Send_(client_socket, "NO \"SASL response too large.\"\r\n");
            return AuthenticationAborted;
         }

         if (!ReadBytes_(client_socket, buffer, literalSize, saslResponse))
            return AuthenticationAborted;

         // Consume the CRLF that terminates the command after the literal.
         String trailing;
         ReadLine_(client_socket, buffer, trailing);
      }
      else if (!ParseToken(line, pos, saslResponse))
      {
         Send_(client_socket, "NO \"PLAIN requires an initial response.\"\r\n");
         return AuthenticationFailed;
      }

      String authzid, authcid, password;
      if (!StringParser::DecodeSaslPlain(saslResponse, authzid, authcid, password))
      {
         // Counted against this connection, but deliberately not reported to the
         // per-IP auto-ban: a response we cannot even decode carries no password
         // guess, and banning on it would let a broken client lock its own address
         // out of the server.
         if (ApplyAuthenticationFailure_(client_socket, authentication_failures, false, "NO \"Invalid SASL response.\"\r\n"))
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
         if (ApplyAuthenticationFailure_(client_socket, authentication_failures, disconnect, "NO \"Authentication failed.\"\r\n"))
            return AuthenticationAborted;

         return AuthenticationFailed;
      }

      account_address = account->GetAddress();
      authentication_failures = 0;
      Send_(client_socket, "OK \"Authentication successful.\"\r\n");

      return AuthenticationSucceeded;
   }

   void
   ManageSieveServer::HandleClient_(SOCKET client_socket, const IPAddress &client_address)
   {
      DWORD timeout = 30000;
      setsockopt(client_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(client_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      std::string buffer;
      bool authenticated = false;
      String accountAddress;
      int authentication_failures = 0;

      // Greeting: advertise capabilities then an OK.
      SendCapabilities_(client_socket);
      Send_(client_socket, "OK \"hMailServer ManageSieve ready.\"\r\n");

      for (;;)
      {
         String line;
         if (!ReadLine_(client_socket, buffer, line))
            break;

         int pos = 0;
         String command;
         if (!ParseToken(line, pos, command))
         {
            Send_(client_socket, "NO \"Empty command.\"\r\n");
            continue;
         }
         command.ToUpper();

         if (command == _T("LOGOUT"))
         {
            Send_(client_socket, "OK \"Bye.\"\r\n");
            break;
         }

         if (command == _T("CAPABILITY"))
         {
            SendCapabilities_(client_socket);
            Send_(client_socket, "OK\r\n");
            continue;
         }

         if (command == _T("NOOP"))
         {
            Send_(client_socket, "OK\r\n");
            continue;
         }

         if (command == _T("STARTTLS"))
         {
            Send_(client_socket, "NO \"STARTTLS is not supported on this listener.\"\r\n");
            continue;
         }

         if (command == _T("AUTHENTICATE"))
         {
            String authenticatedAddress;
            AuthenticationOutcome outcome = HandleAuthenticate_(client_socket, client_address, line, pos,
               buffer, authentication_failures, authenticatedAddress);

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
            Send_(client_socket, "NO \"Authentication required.\"\r\n");
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
               Send_(client_socket, responseLine);
            }

            Send_(client_socket, "OK\r\n");
            continue;
         }

         if (command == _T("PUTSCRIPT"))
         {
            String name;
            if (!ParseToken(line, pos, name) || !SieveStorage::IsValidScriptName(name))
            {
               Send_(client_socket, "NO \"Invalid script name.\"\r\n");
               continue;
            }

            int literalSize = 0;
            if (!ParseLiteralSize(line, literalSize))
            {
               Send_(client_socket, "NO \"Expected a script literal.\"\r\n");
               continue;
            }

            String content;
            if (!ReadBytes_(client_socket, buffer, literalSize, content))
               break;

            // Consume the CRLF that terminates the command after the literal.
            String trailing;
            ReadLine_(client_socket, buffer, trailing);

            String syntaxError = SieveScript::CheckSyntax(content);
            if (!syntaxError.IsEmpty())
            {
               Send_(client_socket, "NO \"" + EscapeQuoted(syntaxError) + "\"\r\n");
               continue;
            }

            if (SieveStorage::PutScript(accountAddress, name, content))
               Send_(client_socket, "OK\r\n");
            else
               Send_(client_socket, "NO \"Could not store the script.\"\r\n");
            continue;
         }

         if (command == _T("GETSCRIPT"))
         {
            String name;
            if (!ParseToken(line, pos, name) || !SieveStorage::ScriptExists(accountAddress, name))
            {
               Send_(client_socket, "NO \"There is no script by that name.\"\r\n");
               continue;
            }

            AnsiString content = SieveStorage::GetScript(accountAddress, name);

            AnsiString response;
            response.Format("{%d}\r\n", content.GetLength());
            Send_(client_socket, response);
            Send_(client_socket, content);
            Send_(client_socket, "\r\nOK\r\n");
            continue;
         }

         if (command == _T("SETACTIVE"))
         {
            String name;
            ParseToken(line, pos, name);

            if (name.IsEmpty())
            {
               SieveStorage::SetActiveScriptName(accountAddress, _T(""));
               Send_(client_socket, "OK\r\n");
               continue;
            }

            if (!SieveStorage::ScriptExists(accountAddress, name))
            {
               Send_(client_socket, "NO \"There is no script by that name.\"\r\n");
               continue;
            }

            SieveStorage::SetActiveScriptName(accountAddress, name);
            Send_(client_socket, "OK\r\n");
            continue;
         }

         if (command == _T("DELETESCRIPT"))
         {
            String name;
            if (!ParseToken(line, pos, name) || !SieveStorage::ScriptExists(accountAddress, name))
            {
               Send_(client_socket, "NO \"There is no script by that name.\"\r\n");
               continue;
            }

            if (SieveStorage::DeleteScript(accountAddress, name))
               Send_(client_socket, "OK\r\n");
            else
               Send_(client_socket, "NO \"The active script cannot be deleted.\"\r\n");
            continue;
         }

         if (command == _T("CHECKSCRIPT"))
         {
            int literalSize = 0;
            if (!ParseLiteralSize(line, literalSize))
            {
               Send_(client_socket, "NO \"Expected a script literal.\"\r\n");
               continue;
            }

            String content;
            if (!ReadBytes_(client_socket, buffer, literalSize, content))
               break;

            String trailing;
            ReadLine_(client_socket, buffer, trailing);

            String syntaxError = SieveScript::CheckSyntax(content);
            if (syntaxError.IsEmpty())
               Send_(client_socket, "OK\r\n");
            else
               Send_(client_socket, "NO \"" + EscapeQuoted(syntaxError) + "\"\r\n");
            continue;
         }

         if (command == _T("HAVESPACE"))
         {
            // No per-account script quota is enforced.
            Send_(client_socket, "OK\r\n");
            continue;
         }

         Send_(client_socket, "NO \"Unknown command.\"\r\n");
      }

      shutdown(client_socket, SD_BOTH);
      closesocket(client_socket);
   }
}
