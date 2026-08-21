// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// Shared OTLP/HTTP transport. See OtelExportChannel.h.

#include "StdAfx.h"

#include "OtelExportChannel.h"

#include <ws2tcpip.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   OtelExportChannel::OtelExportChannel() :
      configured_(false),
      port_(4318)
   {

   }

   bool
   OtelExportChannel::Configure(const AnsiString &endpoint, const AnsiString &default_path, AnsiString &error_message)
   {
      configured_ = false;

      AnsiString lower = endpoint;
      lower.MakeLower();
      if (lower.Find("http://") != 0)
      {
         error_message = "must be an http:// URL";
         return false;
      }

      AnsiString remainder = endpoint.Mid(7); // strip "http://"
      int slashPos = remainder.Find("/");
      AnsiString hostPort;
      if (slashPos < 0)
      {
         hostPort = remainder;
         path_ = default_path;
      }
      else
      {
         hostPort = remainder.Mid(0, slashPos);
         path_ = remainder.Mid(slashPos);
      }

      if (path_.IsEmpty() || path_ == "/")
         path_ = default_path;

      int colonPos = hostPort.Find(":");
      if (colonPos < 0)
      {
         host_ = hostPort;
         port_ = 4318;
      }
      else
      {
         host_ = hostPort.Mid(0, colonPos);
         port_ = atoi(hostPort.Mid(colonPos + 1).c_str());
         if (port_ <= 0 || port_ > 65535)
            port_ = 4318;
      }

      if (host_.IsEmpty())
      {
         error_message = "has no host";
         return false;
      }

      configured_ = true;
      return true;
   }

   bool
   OtelExportChannel::PostJson(const AnsiString &json) const
   {
      if (!configured_)
         return false;

      SOCKET sock = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      if (sock == INVALID_SOCKET)
         return false;

      bool ok = false;

      do
      {
         sockaddr_in addr = {};
         addr.sin_family = AF_INET;
         addr.sin_port = htons(static_cast<unsigned short>(port_));

         if (inet_pton(AF_INET, host_.c_str(), &addr.sin_addr) != 1)
         {
            // Not a literal IPv4 address - resolve the host name.
            addrinfo hints = {};
            hints.ai_family = AF_INET;
            hints.ai_socktype = SOCK_STREAM;

            addrinfo *result = nullptr;
            if (getaddrinfo(host_.c_str(), nullptr, &hints, &result) != 0 || result == nullptr)
               break;

            addr.sin_addr = reinterpret_cast<sockaddr_in*>(result->ai_addr)->sin_addr;
            freeaddrinfo(result);
         }

         DWORD timeout = 3000;
         setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));
         setsockopt(sock, SOL_SOCKET, SO_RCVTIMEO, reinterpret_cast<const char*>(&timeout), sizeof(timeout));

         if (connect(sock, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) == SOCKET_ERROR)
            break;

         AnsiString request;
         request.Format("POST %s HTTP/1.1\r\nHost: %s:%d\r\nContent-Type: application/json\r\nContent-Length: %d\r\nConnection: close\r\n\r\n",
            path_.c_str(), host_.c_str(), port_, json.GetLength());
         request += json;

         int total = request.GetLength();
         int sent = 0;
         const char *buffer = request.c_str();
         bool sendOk = true;
         while (sent < total)
         {
            int n = send(sock, buffer + sent, total - sent, 0);
            if (n == SOCKET_ERROR || n <= 0)
            {
               sendOk = false;
               break;
            }
            sent += n;
         }

         if (!sendOk)
            break;

         // Confirm a 2xx status line; the body is irrelevant to us.
         char response[256];
         int received = recv(sock, response, sizeof(response) - 1, 0);
         if (received > 0)
         {
            response[received] = 0;
            if (strstr(response, " 200") || strstr(response, " 202") || strstr(response, " 204"))
               ok = true;
         }
      } while (false);

      closesocket(sock);
      return ok;
   }

   AnsiString
   OtelExportChannel::JsonEscape(const AnsiString &value)
   {
      AnsiString out;
      int len = value.GetLength();
      for (int i = 0; i < len; i++)
      {
         char c = value.GetAt(i);
         switch (c)
         {
         case '\"': out += "\\\""; break;
         case '\\': out += "\\\\"; break;
         case '\b': out += "\\b"; break;
         case '\f': out += "\\f"; break;
         case '\n': out += "\\n"; break;
         case '\r': out += "\\r"; break;
         case '\t': out += "\\t"; break;
         default:
            if (static_cast<unsigned char>(c) < 0x20)
            {
               AnsiString esc;
               esc.Format("\\u%04x", static_cast<unsigned int>(static_cast<unsigned char>(c)));
               out += esc;
            }
            else
            {
               out += c;
            }
         }
      }
      return out;
   }
}
