// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// REST administration API over HTTPS. See RestApiServer.h.

#include "StdAfx.h"

#include "RestApiServer.h"
#include "ServerStatus.h"
#include "Crypt.h"
#include "AcmeClient.h"
#include "Encoding/Base64.h"

#include "../BO/Domains.h"
#include "../BO/Domain.h"
#include "../BO/Accounts.h"
#include "../BO/Account.h"
#include "../BO/SSLCertificates.h"
#include "../BO/SSLCertificate.h"
#include "../Persistence/PersistentAccount.h"
#include "../TCPIP/SocketConstants.h"
#include "../../SMTP/DeliveryQueue.h"

#include <ws2tcpip.h>

#include <openssl/ssl.h>
#include <openssl/err.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int MaxRequestSize = 64 * 1024;
      const DWORD SocketTimeoutMilliseconds = 10000;

      SSL_CTX *tls_context = nullptr;

      // Parses a strictly numeric message id.
      bool ParseQueueId(const AnsiString &value, __int64 &id)
      {
         if (value.IsEmpty() || value.GetLength() > 18)
            return false;

         for (int i = 0; i < value.GetLength(); i++)
         {
            if (value[i] < '0' || value[i] > '9')
               return false;
         }

         id = _atoi64(value.c_str());
         return id > 0;
      }

      // Reads an HTTP request (headers + body according to Content-Length)
      // using the supplied read function.
      template <typename ReadFunction>
      bool ReadHttpRequest(ReadFunction readSome, AnsiString &request)
      {
         std::string data;
         char buffer[4096];

         size_t headerEnd = std::string::npos;

         while (data.size() < MaxRequestSize)
         {
            int bytesRead = readSome(buffer, sizeof(buffer));
            if (bytesRead <= 0)
               break;

            data.append(buffer, bytesRead);

            headerEnd = data.find("\r\n\r\n");
            if (headerEnd != std::string::npos)
            {
               // Determine expected body length.
               size_t contentLength = 0;

               std::string headersLower = data.substr(0, headerEnd);
               for (size_t i = 0; i < headersLower.size(); i++)
                  headersLower[i] = (char) tolower((unsigned char) headersLower[i]);

               size_t lengthPosition = headersLower.find("content-length:");
               if (lengthPosition != std::string::npos)
                  contentLength = strtoul(headersLower.c_str() + lengthPosition + 15, nullptr, 10);

               if (contentLength > MaxRequestSize)
                  return false;

               size_t totalExpected = headerEnd + 4 + contentLength;
               if (data.size() >= totalExpected)
                  break;
            }
         }

         if (headerEnd == std::string::npos)
            return false;

         request = data.c_str();
         return true;
      }
   }

   RestApiServer::RestApiServer() :
      listen_socket_(INVALID_SOCKET),
      running_(false),
      use_tls_(false)
   {

   }

   RestApiServer::~RestApiServer()
   {
      Stop();
   }

   bool
   RestApiServer::Start(const String &bind_address, int port, const String &certificate_file, const String &private_key_file)
   {
      if (running_)
         return true;

      if (IniFileSettings::Instance()->GetAdministratorPassword().IsEmpty())
      {
         LOG_APPLICATION("RestApi: Refusing to start - the administrator password is not set.");
         return false;
      }

      use_tls_ = !certificate_file.IsEmpty() && !private_key_file.IsEmpty();
      certificate_file_ = certificate_file;
      private_key_file_ = private_key_file;

      bool isLoopback = bind_address == _T("127.0.0.1") || bind_address == _T("localhost");

      if (!use_tls_ && !isLoopback)
      {
         LOG_APPLICATION("RestApi: Refusing to start - TLS certificate is required unless bound to 127.0.0.1. Set RestApiCertificateFile and RestApiPrivateKeyFile.");
         return false;
      }

      if (use_tls_)
      {
         tls_context = SSL_CTX_new(TLS_server_method());
         if (tls_context == nullptr)
            return false;

         SSL_CTX_set_min_proto_version(tls_context, TLS1_2_VERSION);

         AnsiString narrowCertificateFile = certificate_file;
         AnsiString narrowPrivateKeyFile = private_key_file;

         if (SSL_CTX_use_certificate_chain_file(tls_context, narrowCertificateFile.c_str()) != 1 ||
             SSL_CTX_use_PrivateKey_file(tls_context, narrowPrivateKeyFile.c_str(), SSL_FILETYPE_PEM) != 1 ||
             SSL_CTX_check_private_key(tls_context) != 1)
         {
            LOG_APPLICATION("RestApi: Failed to load the TLS certificate or private key.");
            SSL_CTX_free(tls_context);
            tls_context = nullptr;
            return false;
         }
      }

      AnsiString narrowBindAddress = bind_address == _T("localhost") ? AnsiString("127.0.0.1") : AnsiString(bind_address);

      sockaddr_in address = {};
      address.sin_family = AF_INET;
      address.sin_port = htons(static_cast<unsigned short>(port));

      if (inet_pton(AF_INET, narrowBindAddress.c_str(), &address.sin_addr) != 1)
      {
         LOG_APPLICATION("RestApi: Invalid bind address: " + bind_address);
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
         message.Format(_T("RestApi: Failed to bind to %s:%d."), bind_address.c_str(), port);
         LOG_APPLICATION(message);

         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
         return false;
      }

      running_ = true;
      worker_ = std::thread(&RestApiServer::Run_, this);

      String message;
      message.Format(_T("RestApi: Listening on %s:%d (%s)."), bind_address.c_str(), port, use_tls_ ? _T("https") : _T("http, loopback only"));
      LOG_APPLICATION(message);

      return true;
   }

   void
   RestApiServer::Stop()
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

      if (tls_context != nullptr)
      {
         SSL_CTX_free(tls_context);
         tls_context = nullptr;
      }
   }

   void
   RestApiServer::Run_()
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

         try
         {
            HandleClient_(clientSocket);
         }
         catch (...)
         {
            closesocket(clientSocket);
         }
      }
   }

   void
   RestApiServer::HandleClient_(SOCKET client_socket)
   {
      DWORD timeout = SocketTimeoutMilliseconds;
      setsockopt(client_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(client_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      if (use_tls_)
      {
         SSL *tlsSession = SSL_new(tls_context);
         if (tlsSession == nullptr)
         {
            closesocket(client_socket);
            return;
         }

         SSL_set_fd(tlsSession, static_cast<int>(client_socket));

         if (SSL_accept(tlsSession) == 1)
         {
            AnsiString request;

            bool requestOk = ReadHttpRequest(
               [&](char *buffer, int size) { return SSL_read(tlsSession, buffer, size); },
               request);

            AnsiString response = requestOk
               ? ProcessRequest_(request)
               : BuildResponse_(400, "{\"error\":\"malformed request\"}");

            SSL_write(tlsSession, response.c_str(), response.GetLength());
            SSL_shutdown(tlsSession);
         }

         SSL_free(tlsSession);
         closesocket(client_socket);
         return;
      }

      AnsiString request;

      bool requestOk = ReadHttpRequest(
         [&](char *buffer, int size) { return recv(client_socket, buffer, size, 0); },
         request);

      AnsiString response = requestOk
         ? ProcessRequest_(request)
         : BuildResponse_(400, "{\"error\":\"malformed request\"}");

      send(client_socket, response.c_str(), response.GetLength(), 0);

      shutdown(client_socket, SD_SEND);
      closesocket(client_socket);
   }

   bool
   RestApiServer::Authenticate_(const AnsiString &request)
   {
      // Extract the Authorization header.
      AnsiString lowerRequest = request;
      lowerRequest.MakeLower();

      int headerPosition = lowerRequest.Find("\r\nauthorization:");
      if (headerPosition < 0)
         return false;

      int valueStart = headerPosition + 16;
      int lineEnd = request.Find("\r\n", valueStart);
      if (lineEnd < 0)
         return false;

      AnsiString headerValue = request.Mid(valueStart, lineEnd - valueStart);
      headerValue.Trim();

      if (headerValue.GetLength() < 7 || headerValue.Mid(0, 6).CompareNoCase("basic ") != 0)
         return false;

      AnsiString encodedCredentials = headerValue.Mid(6);
      encodedCredentials.Trim();

      AnsiString credentials = Base64::Decode(encodedCredentials, encodedCredentials.GetLength());

      int separatorPosition = credentials.Find(":");
      if (separatorPosition <= 0)
         return false;

      String username = credentials.Mid(0, separatorPosition);
      String password = credentials.Mid(separatorPosition + 1);

      if (username.CompareNoCase(_T("administrator")) != 0)
         return false;

      String correctPassword = IniFileSettings::Instance()->GetAdministratorPassword();
      if (correctPassword.IsEmpty())
         return false;

      Crypt::EncryptionType hashType = Crypt::Instance()->GetHashType(correctPassword);

      return Crypt::Instance()->Validate(password, correctPassword, hashType);
   }

   AnsiString
   RestApiServer::ProcessRequest_(const AnsiString &request)
   {
      // Parse the request line.
      int lineEnd = request.Find("\r\n");
      if (lineEnd < 0)
         return BuildResponse_(400, "{\"error\":\"malformed request\"}");

      AnsiString requestLine = request.Mid(0, lineEnd);

      std::vector<AnsiString> requestParts = StringParser::SplitString(requestLine, " ");
      if (requestParts.size() < 2)
         return BuildResponse_(400, "{\"error\":\"malformed request\"}");

      AnsiString method = requestParts[0];
      AnsiString path = requestParts[1];

      // Strip any query string.
      int queryPosition = path.Find("?");
      if (queryPosition >= 0)
         path = path.Mid(0, queryPosition);

      if (!Authenticate_(request))
      {
         AnsiString response;
         response += "HTTP/1.0 401 Unauthorized\r\n";
         response += "WWW-Authenticate: Basic realm=\"hMailServer\"\r\n";
         response += "Content-Type: application/json\r\n";
         response += "Content-Length: 32\r\n";
         response += "Connection: close\r\n\r\n";
         response += "{\"error\":\"authentication failed\"}";
         return response;
      }

      try
      {
         if (method == "GET" && path == "/api/v1/status")
            return HandleStatus_();

         if (method == "GET" && path == "/api/v1/domains")
            return HandleListDomains_();

         // /api/v1/domains/<name>/accounts
         AnsiString domainsPrefix = "/api/v1/domains/";
         if (path.StartsWith(domainsPrefix) && path.EndsWith("/accounts"))
         {
            AnsiString domainName = path.Mid(domainsPrefix.GetLength(),
               path.GetLength() - domainsPrefix.GetLength() - AnsiString("/accounts").GetLength());

            if (!domainName.IsEmpty() && domainName.Find("/") < 0)
            {
               if (method == "GET")
                  return HandleListAccounts_(String(domainName));

               if (method == "POST")
                  return HandleCreateAccount_(String(domainName), GetRequestBody_(request));
            }
         }

         // /api/v1/accounts/<address>
         AnsiString accountsPrefix = "/api/v1/accounts/";
         if (method == "DELETE" && path.StartsWith(accountsPrefix))
         {
            AnsiString address = path.Mid(accountsPrefix.GetLength());
            if (!address.IsEmpty() && address.Find("/") < 0)
               return HandleDeleteAccount_(String(address));
         }

         if (method == "GET" && path == "/api/v1/queue")
            return HandleListQueue_();

         // /api/v1/queue/<id>/retry and /api/v1/queue/<id>
         AnsiString queuePrefix = "/api/v1/queue/";
         if (path.StartsWith(queuePrefix))
         {
            AnsiString remainder = path.Mid(queuePrefix.GetLength());

            if (method == "POST" && remainder.EndsWith("/retry"))
            {
               AnsiString idPart = remainder.Mid(0, remainder.GetLength() - AnsiString("/retry").GetLength());

               __int64 messageId = 0;
               if (ParseQueueId(idPart, messageId))
                  return HandleQueueRetry_(messageId);
            }

            if (method == "DELETE" && remainder.Find("/") < 0)
            {
               __int64 messageId = 0;
               if (ParseQueueId(remainder, messageId))
                  return HandleQueueDelete_(messageId);
            }
         }

         if (method == "GET" && path == "/api/v1/tlsa")
            return HandleTlsa_();

         return BuildResponse_(404, "{\"error\":\"not found\"}");
      }
      catch (...)
      {
         return BuildResponse_(500, "{\"error\":\"internal error\"}");
      }
   }

   AnsiString
   RestApiServer::BuildResponse_(int statusCode, const AnsiString &body)
   {
      AnsiString statusText;
      switch (statusCode)
      {
      case 200: statusText = "OK"; break;
      case 201: statusText = "Created"; break;
      case 400: statusText = "Bad Request"; break;
      case 404: statusText = "Not Found"; break;
      case 409: statusText = "Conflict"; break;
      default:  statusText = "Internal Server Error"; statusCode = 500; break;
      }

      AnsiString response;
      response.Format("HTTP/1.0 %d %hs\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n",
         statusCode, statusText.c_str(), body.GetLength());
      response += body;

      return response;
   }

   AnsiString
   RestApiServer::HandleStatus_()
   {
      ServerStatus *status = ServerStatus::Instance();

      AnsiString version = Application::Instance()->GetVersionNumber();

      AnsiString body;
      body.Format("{\"version\":\"%hs\",\"state\":%d,\"processedMessages\":%d,\"spamMessages\":%d,\"virusesRemoved\":%d,"
                  "\"sessions\":{\"smtp\":%d,\"imap\":%d,\"pop3\":%d}}",
         JsonEscape_(version).c_str(),
         status->GetState(),
         status->GetNumberOfProcessedMessages(),
         status->GetNumberOfDetectedSpamMessages(),
         status->GetNumberOfRemovedViruses(),
         status->GetNumberOfSessions(STSMTP),
         status->GetNumberOfSessions(STIMAP),
         status->GetNumberOfSessions(STPOP3));

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleListDomains_()
   {
      Domains domains;
      domains.Refresh();

      AnsiString body = "[";

      for (int i = 0; i < domains.GetCount(); i++)
      {
         std::shared_ptr<Domain> domain = domains.GetItem(i);
         if (!domain)
            continue;

         if (i > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"name\":\"%hs\",\"active\":%hs}",
            JsonEscape_(AnsiString(domain->GetName())).c_str(),
            domain->GetIsActive() ? "true" : "false");

         body += entry;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleListAccounts_(const String &domainName)
   {
      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      Accounts accounts(domain->GetID());
      accounts.Refresh();

      AnsiString body = "[";

      for (int i = 0; i < accounts.GetCount(); i++)
      {
         std::shared_ptr<Account> account = accounts.GetItem(i);
         if (!account)
            continue;

         if (i > 0)
            body += ",";

         AnsiString entry;
         entry.Format("{\"address\":\"%hs\",\"active\":%hs}",
            JsonEscape_(AnsiString(account->GetAddress())).c_str(),
            account->GetActive() ? "true" : "false");

         body += entry;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleCreateAccount_(const String &domainName, const AnsiString &requestBody)
   {
      AnsiString address = GetJsonStringValue_(requestBody, "address");
      AnsiString password = GetJsonStringValue_(requestBody, "password");

      if (address.IsEmpty() || password.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"address and password are required\"}");

      String addressDomain = StringParser::ExtractDomain(String(address));
      if (addressDomain.CompareNoCase(domainName) != 0)
         return BuildResponse_(400, "{\"error\":\"address does not belong to the domain\"}");

      Domains domains;
      domains.Refresh();

      std::shared_ptr<Domain> domain = domains.GetItemByName(domainName);
      if (!domain)
         return BuildResponse_(404, "{\"error\":\"domain not found\"}");

      // Reject duplicates.
      std::shared_ptr<Account> existingAccount = std::shared_ptr<Account>(new Account());
      if (PersistentAccount::ReadObject(existingAccount, String(address)) && existingAccount->GetID() > 0)
         return BuildResponse_(409, "{\"error\":\"account already exists\"}");

      int preferredHashAlgorithm = IniFileSettings::Instance()->GetPreferredHashAlgorithm();
      String hashedPassword = Crypt::Instance()->EnCrypt(String(password), (Crypt::EncryptionType) preferredHashAlgorithm);

      std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());
      account->SetDomainID(domain->GetID());
      account->SetAddress(String(address));
      account->SetPassword(hashedPassword);
      account->SetPasswordEncryption(preferredHashAlgorithm);
      account->SetActive(true);

      if (!PersistentAccount::SaveObject(account))
         return BuildResponse_(500, "{\"error\":\"failed to save account\"}");

      LOG_APPLICATION("RestApi: Account " + String(address) + " created.");

      AnsiString body;
      body.Format("{\"address\":\"%hs\",\"created\":true}", JsonEscape_(address).c_str());

      return BuildResponse_(201, body);
   }

   AnsiString
   RestApiServer::HandleDeleteAccount_(const String &address)
   {
      std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());

      if (!PersistentAccount::ReadObject(account, address) || account->GetID() == 0)
         return BuildResponse_(404, "{\"error\":\"account not found\"}");

      if (!PersistentAccount::DeleteObject(account))
         return BuildResponse_(500, "{\"error\":\"failed to delete account\"}");

      LOG_APPLICATION("RestApi: Account " + address + " deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   AnsiString
   RestApiServer::HandleListQueue_()
   {
      // Reuses the same query that backs the COM Status.UndeliveredMessages
      // property: tab-separated columns id, created, from, recipients,
      // next try, file name, locked, tries.
      AnsiString queueData = ServerStatus::Instance()->GetUnsortedMessageStatus();

      std::vector<AnsiString> lines = StringParser::SplitString(queueData, "\r\n");

      AnsiString items;
      int count = 0;

      for (const AnsiString &line : lines)
      {
         if (line.IsEmpty())
            continue;

         std::vector<AnsiString> columns = StringParser::SplitString(line, "\t");
         if (columns.size() < 8)
            continue;

         AnsiString item;
         item.Format("{\"id\":%hs,\"created\":\"%hs\",\"from\":\"%hs\",\"recipients\":\"%hs\",\"next_try\":\"%hs\",\"locked\":%hs,\"tries\":%hs}",
            columns[0].c_str(),
            JsonEscape_(columns[1]).c_str(),
            JsonEscape_(columns[2]).c_str(),
            JsonEscape_(columns[3]).c_str(),
            JsonEscape_(columns[4]).c_str(),
            columns[6] == "1" ? "true" : "false",
            columns[7].c_str());

         if (count > 0)
            items += ",";

         items += item;
         count++;
      }

      AnsiString body;
      body.Format("{\"count\":%d,\"messages\":[%hs]}", count, items.c_str());

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::HandleQueueRetry_(__int64 messageId)
   {
      DeliveryQueue::ResetDeliveryTime(messageId);
      DeliveryQueue::StartDelivery();

      LOG_APPLICATION("RestApi: Queue message " + StringParser::IntToString(messageId) + " scheduled for immediate delivery.");

      return BuildResponse_(200, "{\"retried\":true}");
   }

   AnsiString
   RestApiServer::HandleQueueDelete_(__int64 messageId)
   {
      DeliveryQueue::Remove(messageId);

      LOG_APPLICATION("RestApi: Queue message " + StringParser::IntToString(messageId) + " removed from the delivery queue.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   AnsiString
   RestApiServer::HandleTlsa_()
   {
      // Recommended DANE TLSA records (3 1 1: DANE-EE, SPKI, SHA-256) for
      // every configured certificate, so administrators can publish or
      // verify DNS without manual hashing.
      AnsiString hostName = AnsiString(Configuration::Instance()->GetHostName());
      if (hostName.IsEmpty())
         hostName = "<your-mx-hostname>";

      AnsiString items;
      int count = 0;

      auto appendCertificate = [&](const String &name, const String &certificateFile)
      {
         if (certificateFile.IsEmpty() || !FileUtilities::Exists(certificateFile))
            return;

         AnsiString spkiHex;
         if (!AcmeClient::GetCertificateTlsa(certificateFile, spkiHex))
            return;

         AnsiString item;
         item.Format("{\"certificate\":\"%hs\",\"spki_sha256\":\"%hs\",\"record\":\"_25._tcp.%hs. IN TLSA 3 1 1 %hs\"}",
            JsonEscape_(AnsiString(name)).c_str(),
            spkiHex.c_str(),
            JsonEscape_(hostName).c_str(),
            spkiHex.c_str());

         if (count > 0)
            items += ",";

         items += item;
         count++;
      };

      SSLCertificates certificates;
      certificates.Refresh();

      for (int i = 0; i < certificates.GetCount(); i++)
      {
         std::shared_ptr<SSLCertificate> certificate = certificates.GetItem(i);
         if (certificate)
            appendCertificate(certificate->GetName(), certificate->GetCertificateFile());
      }

      if (count == 0)
         appendCertificate(_T("ACME (automatic)"), AcmeClient::GetCertificateDirectory() + _T("\\fullchain.pem"));

      AnsiString body;
      body.Format("{\"host\":\"%hs\",\"count\":%d,\"records\":[%hs]}",
         JsonEscape_(hostName).c_str(), count, items.c_str());

      return BuildResponse_(200, body);
   }

   AnsiString
   RestApiServer::GetRequestBody_(const AnsiString &request)
   {
      int bodyStart = request.Find("\r\n\r\n");
      if (bodyStart < 0)
         return "";

      return request.Mid(bodyStart + 4);
   }

   AnsiString
   RestApiServer::GetJsonStringValue_(const AnsiString &json, const AnsiString &key)
   {
      AnsiString needle = "\"" + key + "\"";

      int keyPosition = json.Find(needle);
      if (keyPosition < 0)
         return "";

      int colonPosition = json.Find(":", keyPosition + needle.GetLength());
      if (colonPosition < 0)
         return "";

      int valueStart = json.Find("\"", colonPosition);
      if (valueStart < 0)
         return "";

      valueStart++;

      AnsiString result;
      for (int i = valueStart; i < json.GetLength(); i++)
      {
         char character = json[i];

         if (character == '\\' && i + 1 < json.GetLength())
         {
            char next = json[i + 1];
            if (next == '\"' || next == '\\' || next == '/')
            {
               result += next;
               i++;
               continue;
            }

            result += character;
            continue;
         }

         if (character == '\"')
            break;

         result += character;
      }

      return result;
   }

   AnsiString
   RestApiServer::JsonEscape_(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength() + 8);

      for (int i = 0; i < value.GetLength(); i++)
      {
         char character = value[i];

         switch (character)
         {
         case '\"':
            result += "\\\"";
            break;
         case '\\':
            result += "\\\\";
            break;
         default:
            if (static_cast<unsigned char>(character) >= 0x20)
               result += character;
            break;
         }
      }

      return result;
   }
}
