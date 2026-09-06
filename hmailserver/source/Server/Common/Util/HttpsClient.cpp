// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "HttpsClient.h"

#include "../TCPIP/CertificateVerifier.h"
#include "../TCPIP/SslContextInitializer.h"

#include <boost/asio.hpp>
#include <boost/asio/ssl.hpp>

#include <string>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      template <typename Stream>
      bool ReadWholeResponse_(Stream &stream, size_t max_response_bytes, std::string &raw)
      {
         char buffer[4096];
         boost::system::error_code errorCode;

         for (;;)
         {
            const size_t bytesRead = stream.read_some(boost::asio::buffer(buffer, sizeof(buffer)), errorCode);
            if (bytesRead > 0)
               raw.append(buffer, bytesRead);

            if (errorCode)
               break;

            if (raw.size() > max_response_bytes)
               return false;
         }

         return true;
      }

      bool ParseResponse_(const std::string &raw, HttpsClient::Response &response, String &error)
      {
         const size_t firstLineEnd = raw.find("\r\n");
         const size_t headerEnd = raw.find("\r\n\r\n");
         if (firstLineEnd == std::string::npos || headerEnd == std::string::npos)
         {
            error = _T("The response carried no HTTP headers.");
            return false;
         }

         const std::string statusLine = raw.substr(0, firstLineEnd);
         const size_t space = statusLine.find(' ');
         if (statusLine.compare(0, 5, "HTTP/") != 0 || space == std::string::npos)
         {
            error = _T("The response did not start with an HTTP status line.");
            return false;
         }

         response.status_code = atoi(statusLine.c_str() + space + 1);
         response.headers = raw.substr(0, headerEnd).c_str();
         response.body = raw.substr(headerEnd + 4).c_str();

         return true;
      }

      void SetSocketTimeouts_(boost::asio::ip::tcp::socket &socket, int timeout_seconds)
      {
         DWORD timeout = (DWORD) timeout_seconds * 1000;
         setsockopt(socket.native_handle(), SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
         setsockopt(socket.native_handle(), SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));
      }
   }

   bool
   HttpsClient::ParseUrl(const AnsiString &url, bool &https, AnsiString &host, AnsiString &port, AnsiString &path)
   {
      std::string s = url.c_str();

      if (s.compare(0, 8, "https://") == 0)
      {
         https = true;
         s = s.substr(8);
      }
      else if (s.compare(0, 7, "http://") == 0)
      {
         https = false;
         s = s.substr(7);
      }
      else
      {
         return false;
      }

      const size_t slash = s.find('/');
      std::string authority = slash == std::string::npos ? s : s.substr(0, slash);
      std::string p = slash == std::string::npos ? "/" : s.substr(slash);

      if (authority.empty() || authority.find('@') != std::string::npos)
         return false;

      std::string h = authority;
      std::string prt = https ? "443" : "80";

      if (!h.empty() && h[0] == '[')
      {
         // [IPv6]:port
         const size_t close = h.find(']');
         if (close == std::string::npos)
            return false;
         if (close + 1 < h.size())
         {
            if (h[close + 1] != ':')
               return false;
            prt = h.substr(close + 2);
         }
         h = h.substr(1, close - 1);
      }
      else
      {
         const size_t colon = h.find(':');
         if (colon != std::string::npos)
         {
            prt = h.substr(colon + 1);
            h = h.substr(0, colon);
         }
      }

      if (h.empty() || prt.empty() || prt.find_first_not_of("0123456789") != std::string::npos)
         return false;

      host = h.c_str();
      port = prt.c_str();
      path = p.c_str();

      return true;
   }

   bool
   HttpsClient::IsLoopbackHost(const AnsiString &host)
   {
      AnsiString h = host;
      h.MakeLower();

      if (h == "localhost" || h == "::1")
         return true;

      // 127.0.0.0/8
      return h.StartsWith("127.");
   }

   AnsiString
   HttpsClient::FormEncode(const AnsiString &value)
   {
      static const char *hex = "0123456789ABCDEF";
      AnsiString result;

      for (int i = 0; i < value.GetLength(); i++)
      {
         const unsigned char c = (unsigned char) value[i];
         if (isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~')
         {
            result += (char) c;
         }
         else
         {
            result += '%';
            result += hex[c >> 4];
            result += hex[c & 0x0F];
         }
      }

      return result;
   }

   bool
   HttpsClient::Request(const AnsiString &method, const AnsiString &url, const std::vector<AnsiString> &extra_headers,
                        const AnsiString &content_type, const AnsiString &body, Response &response, String &error,
                        int timeout_seconds, size_t max_response_bytes)
   {
      bool https = false;
      AnsiString host, port, path;
      if (!ParseUrl(url, https, host, port, path))
      {
         error = _T("The URL is not an http or https URL with a host.");
         return false;
      }

      if (!https && !IsLoopbackHost(host))
      {
         error = _T("Plain http is only accepted to a loopback address; use https.");
         return false;
      }

      AnsiString request;
      request.append(method);
      request.append(" ");
      request.append(path);
      request.append(" HTTP/1.0\r\nHost: ");
      request.append(host);
      request.append("\r\nUser-Agent: hMailServer\r\nAccept: application/json\r\n");

      for (const AnsiString &header : extra_headers)
      {
         request.append(header);
         request.append("\r\n");
      }

      if (method == "POST")
      {
         AnsiString length;
         length.Format("%d", body.GetLength());
         request.append("Content-Type: ");
         request.append(content_type.IsEmpty() ? AnsiString("application/x-www-form-urlencoded") : content_type);
         request.append("\r\nContent-Length: ");
         request.append(length);
         request.append("\r\n");
      }

      request.append("Connection: close\r\n\r\n");
      request.append(body);

      try
      {
         boost::asio::io_context ioContext;
         boost::asio::ip::tcp::resolver resolver(ioContext);
         boost::asio::ip::tcp::resolver::results_type endpoints =
            resolver.resolve(std::string(host.c_str()), std::string(port.c_str()));

         std::string raw;

         if (https)
         {
            boost::asio::ssl::context sslContext(boost::asio::ssl::context::tls_client);
            // An HTTPS client of a web service: TLS 1.2 is the floor whatever the mail
            // protocol toggles allow, since there is no 2008-era CA, token issuer or
            // policy host to accommodate.
            sslContext.set_options(HM_TLS_CONTEXT_FLOOR);
            sslContext.set_default_verify_paths();
            SslContextInitializer::InitClient(sslContext, false);

            boost::asio::ssl::stream<boost::asio::ip::tcp::socket> stream(ioContext, sslContext);
            boost::asio::connect(stream.next_layer(), endpoints);
            SetSocketTimeouts_(stream.next_layer(), timeout_seconds);

            stream.set_verify_mode(boost::asio::ssl::verify_peer);
            stream.set_verify_callback(CertificateVerifier(0, CSSSL, String(host)));

            if (!SSL_set_tlsext_host_name(stream.native_handle(), host.c_str()))
            {
               error = _T("Could not set the TLS server name.");
               return false;
            }

            stream.handshake(boost::asio::ssl::stream_base::client);
            boost::asio::write(stream, boost::asio::buffer(request.c_str(), request.GetLength()));

            if (!ReadWholeResponse_(stream, max_response_bytes, raw))
            {
               error = _T("The response exceeded the size limit.");
               return false;
            }
         }
         else
         {
            boost::asio::ip::tcp::socket socket(ioContext);
            boost::asio::connect(socket, endpoints);
            SetSocketTimeouts_(socket, timeout_seconds);

            boost::asio::write(socket, boost::asio::buffer(request.c_str(), request.GetLength()));

            if (!ReadWholeResponse_(socket, max_response_bytes, raw))
            {
               error = _T("The response exceeded the size limit.");
               return false;
            }
         }

         return ParseResponse_(raw, response, error);
      }
      catch (const std::exception &e)
      {
         error = Formatter::Format(_T("The request to {0} failed: {1}"), String(host), String(e.what()));
         return false;
      }
   }
}
