// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "ManageSieveServer.h"

#include "SieveStorage.h"
#include "SieveScript.h"

#include "../BO/Account.h"
#include "../Util/PasswordValidator.h"
#include "../Util/Parsing/StringParser.h"

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
         SOCKET clientSocket = accept(listen_socket_, nullptr, nullptr);

         if (clientSocket == INVALID_SOCKET)
         {
            if (!running_)
               return;

            continue;
         }

         HandleClient_(clientSocket);
      }
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

   void
   ManageSieveServer::HandleClient_(SOCKET client_socket)
   {
      DWORD timeout = 30000;
      setsockopt(client_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(client_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      std::string buffer;
      bool authenticated = false;
      String accountAddress;

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
            String mechanism;
            ParseToken(line, pos, mechanism);
            mechanism.ToUpper();

            if (mechanism != _T("PLAIN"))
            {
               Send_(client_socket, "NO \"Unsupported SASL mechanism.\"\r\n");
               continue;
            }

            String initialResponse;
            if (!ParseToken(line, pos, initialResponse))
            {
               Send_(client_socket, "NO \"PLAIN requires an initial response.\"\r\n");
               continue;
            }

            String authzid, authcid, password;
            if (!StringParser::DecodeSaslPlain(initialResponse, authzid, authcid, password))
            {
               Send_(client_socket, "NO \"Invalid SASL response.\"\r\n");
               continue;
            }

            String username;
            if (!StringParser::SaslPrep(authcid, username))
               username = authcid;

            std::shared_ptr<const Account> account = PasswordValidator::ValidatePassword(username, password);
            if (account)
            {
               authenticated = true;
               accountAddress = account->GetAddress();
               Send_(client_socket, "OK \"Authentication successful.\"\r\n");
            }
            else
            {
               Send_(client_socket, "NO \"Authentication failed.\"\r\n");
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
