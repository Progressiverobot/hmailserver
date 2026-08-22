// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "OutboundOAuth2TokenClient.h"

#include "../Application/IniFileSettings.h"
#include "../TCPIP/CertificateVerifier.h"
#include "Charset.h"

#include <boost/asio.hpp>
#include <boost/asio/ssl.hpp>
#include <boost/property_tree/ptree.hpp>
#include <boost/property_tree/json_parser.hpp>

#include <time.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // A stalled identity provider must cost a bounded wait on the delivery
      // thread, exactly as a stalled MTA-STS policy host does.
      const DWORD HttpsTimeoutMilliseconds = 20 * 1000;

      // A token response is a small JSON object; a megabyte of it is not one.
      const size_t MaxResponseSize = 1024 * 1024;

      __int64 CurrentUnixTime()
      {
         return static_cast<__int64>(::time(nullptr));
      }
   }

   OutboundOAuth2TokenClient::OutboundOAuth2TokenClient()
   {
   }

   void
   OutboundOAuth2TokenClient::Invalidate()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      cached_token_.Empty();
      cache_expires_at_ = 0;
   }

   bool
   OutboundOAuth2TokenClient::GetToken(String &token, String &errorMessage)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      if (!cached_token_.IsEmpty() && CurrentUnixTime() < cache_expires_at_)
      {
         token = cached_token_;
         return true;
      }

      __int64 expiresInSeconds = 0;
      if (!FetchToken_(token, expiresInSeconds, errorMessage))
         return false;

      cached_token_ = token;

      // Refresh at 80% of the reported lifetime, floored so a provider
      // reporting very short lifetimes still gets a cache rather than a
      // fetch per message, and never longer than the token actually lives.
      __int64 usable = (expiresInSeconds * 8) / 10;
      if (usable < 60)
         usable = expiresInSeconds > 60 ? expiresInSeconds - 30 : expiresInSeconds;

      cache_expires_at_ = CurrentUnixTime() + usable;

      return true;
   }

   AnsiString
   OutboundOAuth2TokenClient::UrlEncode_(const String &value)
   {
      AnsiString utf8 = Charset::ToMultiByte(value, "utf-8");

      AnsiString encoded;
      for (int i = 0; i < utf8.GetLength(); i++)
      {
         unsigned char ch = static_cast<unsigned char>(utf8[i]);

         bool unreserved = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') ||
                           (ch >= '0' && ch <= '9') || ch == '-' || ch == '.' ||
                           ch == '_' || ch == '~';

         if (unreserved)
         {
            encoded += static_cast<char>(ch);
         }
         else
         {
            char escaped[4];
            sprintf_s(escaped, sizeof(escaped), "%%%02X", ch);
            encoded += escaped;
         }
      }

      return encoded;
   }

   bool
   OutboundOAuth2TokenClient::FetchToken_(String &token, __int64 &expiresInSeconds, String &errorMessage)
   {
      IniFileSettings *settings = IniFileSettings::Instance();

      String url = settings->GetOutboundOAuth2TokenUrl();
      String clientId = settings->GetOutboundOAuth2ClientId();
      String clientSecret = settings->GetOutboundOAuth2ClientSecret();
      String scope = settings->GetOutboundOAuth2Scope();

      if (url.IsEmpty() || clientId.IsEmpty() || clientSecret.IsEmpty())
      {
         errorMessage = _T("Outbound OAuth2 is not fully configured: OutboundOAuth2TokenUrl, OutboundOAuth2ClientId and OutboundOAuth2ClientSecret are all required.");
         return false;
      }

      // The endpoint must be HTTPS: the request carries the client secret, and
      // an identity provider that offered plain HTTP would not be one.
      if (url.Mid(0, 8).CompareNoCase(_T("https://")) != 0)
      {
         errorMessage = _T("OutboundOAuth2TokenUrl must be an https:// URL.");
         return false;
      }

      String remainder = url.Mid(8);
      int slash = remainder.Find(_T("/"));
      String host = slash >= 0 ? remainder.Mid(0, slash) : remainder;
      AnsiString path = slash >= 0 ? AnsiString(remainder.Mid(slash)) : AnsiString("/");

      AnsiString body;
      body.append("grant_type=client_credentials&client_id=");
      body.append(UrlEncode_(clientId));
      body.append("&client_secret=");
      body.append(UrlEncode_(clientSecret));
      body.append("&scope=");
      body.append(UrlEncode_(scope));

      AnsiString responseBody;
      if (!HttpsPost_(host, path, body, responseBody, errorMessage))
         return false;

      try
      {
         std::stringstream stream(std::string(responseBody.c_str()));
         boost::property_tree::ptree parsed;
         boost::property_tree::read_json(stream, parsed);

         std::string accessToken = parsed.get<std::string>("access_token", "");
         __int64 expiresIn = parsed.get<__int64>("expires_in", 0);

         if (accessToken.empty())
         {
            std::string error = parsed.get<std::string>("error", "");
            std::string description = parsed.get<std::string>("error_description", "");

            errorMessage = _T("The token endpoint returned no access token. ");
            if (!error.empty())
               errorMessage += String(error.c_str()) + _T(": ") + String(description.c_str());

            return false;
         }

         token = accessToken.c_str();
         expiresInSeconds = expiresIn > 0 ? expiresIn : 300;
         return true;
      }
      catch (...)
      {
         errorMessage = _T("The token endpoint's response could not be parsed as JSON.");
         return false;
      }
   }

   bool
   OutboundOAuth2TokenClient::HttpsPost_(const String &host, const AnsiString &path, const AnsiString &body,
                                         AnsiString &responseBody, String &errorMessage)
   {
      // The same shape as TlsPolicy::HttpsGet_ - certificate verification
      // included, because the response is a credential.
      try
      {
         AnsiString narrowHost = host;

         boost::asio::io_context ioContext;

         boost::asio::ip::tcp::resolver resolver(ioContext);
         boost::asio::ip::tcp::resolver::results_type endpoints =
            resolver.resolve(std::string(narrowHost.c_str()), "443");

         boost::asio::ssl::context sslContext(boost::asio::ssl::context::tls_client);
         sslContext.set_default_verify_paths();

         boost::asio::ssl::stream<boost::asio::ip::tcp::socket> stream(ioContext, sslContext);

         boost::asio::connect(stream.next_layer(), endpoints);

         DWORD timeout = HttpsTimeoutMilliseconds;
         setsockopt(stream.next_layer().native_handle(), SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
         setsockopt(stream.next_layer().native_handle(), SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

         stream.set_verify_mode(boost::asio::ssl::verify_peer);
         stream.set_verify_callback(CertificateVerifier(0, CSSSL, host));

         if (!SSL_set_tlsext_host_name(stream.native_handle(), narrowHost.c_str()))
         {
            errorMessage = _T("Could not set the TLS server name for the token endpoint.");
            return false;
         }

         stream.handshake(boost::asio::ssl::stream_base::client);

         AnsiString lengthText;
         lengthText.Format("%d", body.GetLength());

         AnsiString request;
         request.append("POST ");
         request.append(path);
         request.append(" HTTP/1.0\r\nHost: ");
         request.append(narrowHost);
         request.append("\r\nUser-Agent: hMailServer\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: ");
         request.append(lengthText);
         request.append("\r\nConnection: close\r\n\r\n");
         request.append(body);

         boost::asio::write(stream, boost::asio::buffer(request.c_str(), request.GetLength()));

         std::string response;
         char buffer[4096];
         boost::system::error_code errorCode;

         for (;;)
         {
            size_t bytesRead = stream.read_some(boost::asio::buffer(buffer, sizeof(buffer)), errorCode);

            if (bytesRead > 0)
               response.append(buffer, bytesRead);

            if (errorCode)
               break;

            if (response.size() > MaxResponseSize)
               break;
         }

         size_t headerEnd = response.find("\r\n\r\n");
         if (headerEnd == std::string::npos)
         {
            errorMessage = _T("The token endpoint's response carried no headers.");
            return false;
         }

         responseBody = response.substr(headerEnd + 4).c_str();

         // The status is checked AFTER extracting the body: OAuth error
         // responses are 400s whose JSON carries the reason, and that reason is
         // worth more than "not 200".
         size_t firstLineEnd = response.find("\r\n");
         std::string statusLine = response.substr(0, firstLineEnd);
         if (statusLine.find(" 200") == std::string::npos)
         {
            // Let the caller parse the error JSON; signal only when there is
            // nothing to parse.
            if (responseBody.IsEmpty())
            {
               errorMessage = String(_T("The token endpoint answered: ")) + String(statusLine.c_str());
               return false;
            }
         }

         return true;
      }
      catch (std::exception &e)
      {
         errorMessage = String(_T("Could not reach the token endpoint: ")) + String(e.what());
         return false;
      }
      catch (...)
      {
         errorMessage = _T("Could not reach the token endpoint.");
         return false;
      }
   }
}
