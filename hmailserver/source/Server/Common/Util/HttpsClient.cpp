// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "HttpsClient.h"

#include "../TCPIP/CertificateVerifier.h"
#include "../TCPIP/SslContextInitializer.h"
#include "FileUtilities.h"
#include "../Application/IniFileSettings.h"
#include <fstream>

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

   namespace
   {
      // HttpProxy split into host and port; both empty when there is none. A value
      // without a port is an error the caller reports rather than a silent direct
      // connection: somebody who set a proxy expects the traffic to go through it.
      bool ProxySetting_(std::string &proxy_host, std::string &proxy_port, String &error)
      {
         proxy_host.clear();
         proxy_port.clear();

         AnsiString setting = AnsiString(IniFileSettings::Instance()->GetHttpProxy());
         std::string value = std::string(setting.c_str());

         size_t first = value.find_first_not_of(" \t");
         size_t last = value.find_last_not_of(" \t");
         value = (first == std::string::npos) ? std::string() : value.substr(first, last - first + 1);

         if (value.empty())
            return true;

         const size_t colon = value.rfind(':');
         if (colon == std::string::npos || colon == 0 || colon + 1 >= value.size())
         {
            error = _T("HttpProxy must be host:port (or [ipv6]:port).");
            return false;
         }

         std::string host = value.substr(0, colon);
         if (host.size() > 2 && host.front() == '[' && host.back() == ']')
            host = host.substr(1, host.size() - 2);
         else if (host.find(':') != std::string::npos)
         {
            // An IPv6 literal without brackets: the last colon is inside the address.
            error = _T("HttpProxy must be host:port (or [ipv6]:port).");
            return false;
         }

         const std::string port = value.substr(colon + 1);
         if (port.find_first_not_of("0123456789") != std::string::npos)
         {
            error = _T("HttpProxy must be host:port (or [ipv6]:port).");
            return false;
         }

         proxy_host = host;
         proxy_port = port;
         return true;
      }

      // Connects socket to host:port - directly, or through the proxy when one is
      // configured. For an https target the proxy is asked to CONNECT and the TLS
      // handshake then runs inside the tunnel, so the proxy sees the name it was
      // asked for and nothing of what follows. For plain http the caller has put
      // the absolute URL in its request line and the proxy forwards it.
      bool Connect_(boost::asio::io_context &io, boost::asio::ip::tcp::socket &socket, const AnsiString &host,
                    const AnsiString &port, bool https, int timeout_seconds, String &error)
      {
         std::string proxyHost, proxyPort;
         if (!ProxySetting_(proxyHost, proxyPort, error))
            return false;

         boost::asio::ip::tcp::resolver resolver(io);

         if (proxyHost.empty())
         {
            boost::asio::connect(socket, resolver.resolve(std::string(host.c_str()), std::string(port.c_str())));
            SetSocketTimeouts_(socket, timeout_seconds);
            return true;
         }

         boost::asio::connect(socket, resolver.resolve(proxyHost, proxyPort));
         SetSocketTimeouts_(socket, timeout_seconds);

         if (!https)
            return true;

         const std::string target = std::string(host.c_str()) + ":" + std::string(port.c_str());
         const std::string connectRequest = "CONNECT " + target + " HTTP/1.1\r\nHost: " + target + "\r\nUser-Agent: hMailServer\r\n\r\n";
         boost::asio::write(socket, boost::asio::buffer(connectRequest));

         // The proxy's answer ends at the blank line, and a proxy sends nothing more
         // until the client speaks, so read_until cannot swallow TLS bytes. A proxy
         // that talks for longer than 16 KB before that line is refused by the cap.
         boost::asio::streambuf answer(16384);
         boost::asio::read_until(socket, answer, "\r\n\r\n");

         std::istream lines(&answer);
         std::string statusLine;
         std::getline(lines, statusLine);
         if (!statusLine.empty() && statusLine.back() == '\r')
            statusLine.pop_back();

         const size_t space = statusLine.find(' ');
         const int status = (statusLine.compare(0, 5, "HTTP/") == 0 && space != std::string::npos) ? atoi(statusLine.c_str() + space + 1) : 0;

         if (status < 200 || status > 299)
         {
            error = Formatter::Format(_T("The proxy {0} refused CONNECT to {1}: {2}"),
               String((proxyHost + ":" + proxyPort).c_str()), String(target.c_str()), String(statusLine.c_str()));
            return false;
         }

         return true;
      }

      // The request-target for the request line: the path, or through a proxy for
      // plain http the absolute URL, which is how a forward proxy is told where to go.
      AnsiString RequestTarget_(const AnsiString &host, const AnsiString &port, const AnsiString &path, bool https, bool via_proxy)
      {
         if (https || !via_proxy)
            return path;

         const std::string absolute = "http://" + std::string(host.c_str()) + ":" + std::string(port.c_str()) + std::string(path.c_str());
         return AnsiString(absolute.c_str());
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

      std::string proxyHost, proxyPort;
      if (!ProxySetting_(proxyHost, proxyPort, error))
         return false;

      AnsiString request;
      request.append(method);
      request.append(" ");
      request.append(RequestTarget_(host, port, path, https, !proxyHost.empty()));
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
            if (!Connect_(ioContext, stream.next_layer(), host, port, true, timeout_seconds, error))
               return false;

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
            if (!Connect_(ioContext, socket, host, port, false, timeout_seconds, error))
               return false;

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

   namespace
   {
      // Reads one response from stream: the status line and headers into header_block,
      // then the body to sink, up to max_bytes. too_large is set when the body went
      // past max_bytes; the read stops there.
      template <typename Stream>
      bool ReadResponseToSink_(Stream &stream, std::string &header_block, std::ofstream *sink, size_t max_bytes, size_t &body_bytes, bool &too_large)
      {
         char buffer[16384];
         std::string pending;
         bool headersDone = false;
         boost::system::error_code errorCode;
         body_bytes = 0;
         too_large = false;

         for (;;)
         {
            const size_t bytesRead = stream.read_some(boost::asio::buffer(buffer, sizeof(buffer)), errorCode);
            if (bytesRead > 0)
            {
               if (!headersDone)
               {
                  pending.append(buffer, bytesRead);
                  const size_t headerEnd = pending.find("\r\n\r\n");
                  if (headerEnd != std::string::npos)
                  {
                     header_block = pending.substr(0, headerEnd);
                     headersDone = true;
                     const std::string first = pending.substr(headerEnd + 4);
                     body_bytes += first.size();
                     if (body_bytes > max_bytes)
                     {
                        too_large = true;
                        return true;
                     }
                     if (sink && !first.empty())
                        sink->write(first.data(), (std::streamsize) first.size());
                     pending.clear();
                  }
                  else if (pending.size() > 64 * 1024)
                     return false;   // headers that never end are not a response
               }
               else
               {
                  body_bytes += bytesRead;
                  if (body_bytes > max_bytes)
                  {
                     too_large = true;
                     return true;
                  }
                  if (sink)
                     sink->write(buffer, (std::streamsize) bytesRead);
               }
            }
            if (errorCode)
               break;
         }

         return headersDone;
      }

      // The value of one header in a header block, case-insensitively; empty when absent.
      std::string HeaderValue_(const std::string &header_block, const std::string &name)
      {
         size_t position = 0;
         while (position < header_block.size())
         {
            size_t end = header_block.find("\r\n", position);
            if (end == std::string::npos)
               end = header_block.size();
            std::string line = header_block.substr(position, end - position);
            position = end + 2;

            size_t colon = line.find(':');
            if (colon == std::string::npos || colon != name.size())
               continue;
            if (_strnicmp(line.c_str(), name.c_str(), name.size()) != 0)
               continue;
            std::string value = line.substr(colon + 1);
            size_t start = value.find_first_not_of(" \t");
            size_t stop = value.find_last_not_of(" \t\r");
            return start == std::string::npos ? std::string() : value.substr(start, stop - start + 1);
         }
         return std::string();
      }
   }

   bool
   HttpsClient::Download(const AnsiString &url, const String &path, size_t max_bytes, int &status_code, String &error, int timeout_seconds)
   {
      status_code = 0;
      AnsiString current = url;

      for (int hop = 0; hop < 6; hop++)
      {
         bool https = false;
         AnsiString host, port, requestPath;
         if (!ParseUrl(current, https, host, port, requestPath))
         {
            error = Formatter::Format(_T("{0} is not an http or https URL with a host."), String(current));
            return false;
         }
         if (!https && !IsLoopbackHost(host))
         {
            error = Formatter::Format(_T("{0}: plain http is only accepted to a loopback address; use https."), String(current));
            return false;
         }

         std::string proxyHost, proxyPort;
         if (!ProxySetting_(proxyHost, proxyPort, error))
            return false;

         AnsiString request;
         request.append("GET ");
         request.append(RequestTarget_(host, port, requestPath, https, !proxyHost.empty()));
         request.append(" HTTP/1.0\r\nHost: ");
         request.append(host);
         request.append("\r\nUser-Agent: hMailServer\r\nAccept: */*\r\nConnection: close\r\n\r\n");

         std::string headerBlock;
         size_t bodyBytes = 0;
         bool tooLarge = false;
         bool responded = false;

         // The file is opened once the status is known to be 200, so a redirect or
         // an error leaves nothing behind; a body that turns out too large is
         // removed below.
         std::ofstream sink;
         try
         {
            boost::asio::io_context ioContext;

            // The headers are read first with no sink; the body's first bytes are
            // held by the reader until the sink exists. To keep the reader simple the
            // status is decided from the header block it hands back, and the sink
            // is opened before the body is streamed - which means the reader must
            // be told the sink up front. So: open the file now, and delete it unless
            // the response was a 200 written in full.
#ifdef HM_PLATFORM_POSIX
            // A wide path is an extension MSVC's ofstream carries and libstdc++ does
            // not, so the path is narrowed here the way the rest of the tree narrows
            // a String for an API that takes char - the string class does the
            // conversion on construction.
            const AnsiString narrowPath = path.c_str();
            sink.open(narrowPath.c_str(), std::ios::binary | std::ios::trunc);
#else
            sink.open(path.c_str(), std::ios::binary | std::ios::trunc);
#endif
            if (!sink)
            {
               error = Formatter::Format(_T("{0} could not be created."), path);
               return false;
            }

            if (https)
            {
               boost::asio::ssl::context sslContext(boost::asio::ssl::context::tls_client);
               sslContext.set_options(HM_TLS_CONTEXT_FLOOR);
               sslContext.set_default_verify_paths();
               SslContextInitializer::InitClient(sslContext, false);

               boost::asio::ssl::stream<boost::asio::ip::tcp::socket> stream(ioContext, sslContext);
               if (!Connect_(ioContext, stream.next_layer(), host, port, true, timeout_seconds, error))
               {
                  sink.close();
                  FileUtilities::DeleteFile(path);
                  return false;
               }
               stream.set_verify_mode(boost::asio::ssl::verify_peer);
               stream.set_verify_callback(CertificateVerifier(0, CSSSL, String(host)));
               if (!SSL_set_tlsext_host_name(stream.native_handle(), host.c_str()))
               {
                  error = _T("Could not set the TLS server name.");
                  sink.close();
                  FileUtilities::DeleteFile(path);
                  return false;
               }
               stream.handshake(boost::asio::ssl::stream_base::client);
               boost::asio::write(stream, boost::asio::buffer(request.c_str(), request.GetLength()));
               responded = ReadResponseToSink_(stream, headerBlock, &sink, max_bytes, bodyBytes, tooLarge);
            }
            else
            {
               boost::asio::ip::tcp::socket socket(ioContext);
               if (!Connect_(ioContext, socket, host, port, false, timeout_seconds, error))
               {
                  sink.close();
                  FileUtilities::DeleteFile(path);
                  return false;
               }
               boost::asio::write(socket, boost::asio::buffer(request.c_str(), request.GetLength()));
               responded = ReadResponseToSink_(socket, headerBlock, &sink, max_bytes, bodyBytes, tooLarge);
            }
         }
         catch (const std::exception &e)
         {
            sink.close();
            FileUtilities::DeleteFile(path);
            error = Formatter::Format(_T("The request to {0} failed: {1}"), String(host), String(e.what()));
            return false;
         }

         sink.close();

         if (!responded)
         {
            FileUtilities::DeleteFile(path);
            error = Formatter::Format(_T("{0} sent no HTTP response."), String(host));
            return false;
         }

         const size_t firstLineEnd = headerBlock.find("\r\n");
         const std::string statusLine = headerBlock.substr(0, firstLineEnd);
         const size_t space = statusLine.find(' ');
         if (statusLine.compare(0, 5, "HTTP/") != 0 || space == std::string::npos)
         {
            FileUtilities::DeleteFile(path);
            error = _T("The response did not start with an HTTP status line.");
            return false;
         }
         status_code = atoi(statusLine.c_str() + space + 1);

         if (status_code == 301 || status_code == 302 || status_code == 303 || status_code == 307 || status_code == 308)
         {
            FileUtilities::DeleteFile(path);
            std::string location = HeaderValue_(headerBlock, "Location");
            if (location.empty())
            {
               error = Formatter::Format(_T("{0} redirected without saying where."), String(host));
               return false;
            }
            if (location[0] == '/')
            {
               // Relative to the host: same scheme, host and port.
               std::string origin = https ? "https://" : "http://";
               origin += host.c_str();
               if (port != (https ? "443" : "80"))
               {
                  origin += ":";
                  origin += port.c_str();
               }
               location = origin + location;
            }
            current = location.c_str();
            continue;
         }

         if (status_code != 200)
         {
            FileUtilities::DeleteFile(path);
            error = Formatter::Format(_T("{0} answered HTTP {1}."), String(host), status_code);
            return false;
         }

         if (tooLarge)
         {
            FileUtilities::DeleteFile(path);
            error = Formatter::Format(_T("The response from {0} exceeded the size limit."), String(host));
            return false;
         }

         return true;
      }

      FileUtilities::DeleteFile(path);
      error = Formatter::Format(_T("{0} redirected too many times."), String(url));
      return false;
   }
}
