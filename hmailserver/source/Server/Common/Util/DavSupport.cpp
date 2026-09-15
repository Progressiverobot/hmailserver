// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See DavSupport.h. Everything here was written for CardDavServer.cpp and
// moved out of it, unchanged, when CalDAV came to share the tree under /dav/.

#include "StdAfx.h"

#include <openssl/sha.h>

#include "DavSupport.h"
#include "AccountLogon.h"
#include "Unicode.h"
#include "Encoding/Base64.h"
#include "../BO/Account.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace Dav
   {
      // The DAV header of every response under /dav/: WebDAV class 1 and 3,
      // and the two protocols served, so a client that asks the principal
      // what the server speaks (Apple's do) is told both.
      const char *DavHeader = "DAV: 1, 3, addressbook, calendar-access\r\n";

      const int MaxXmlDepth = 32;
      const int MaxXmlElements = 20000;

      //------------------------------------------------------------------------
      // Strings
      //------------------------------------------------------------------------

      AnsiString Utf8(const String &value)
      {
         AnsiString utf8;
         Unicode::WideToMultiByte(value, utf8);
         return utf8;
      }

      String Wide(const AnsiString &value)
      {
         String wide;
         if (!Unicode::MultiByteToWide(value, wide))
            wide = String(value);
         return wide;
      }

      AnsiString Trimmed(const AnsiString &value)
      {
         AnsiString text = value;
         text.TrimLeft();
         text.TrimRight();
         return text;
      }

      AnsiString Lower(const AnsiString &value)
      {
         AnsiString text = value;
         text.ToLower();
         return text;
      }

      AnsiString IntText(int value)
      {
         AnsiString text;
         text.Format("%d", value);
         return text;
      }

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      AnsiString XmlEscape(const AnsiString &value)
      {
         AnsiString result;
         result.reserve(value.GetLength() + 8);

         for (int i = 0; i < value.GetLength(); i++)
         {
            char character = value[i];
            switch (character)
            {
            case '<':  result += "&lt;"; break;
            case '>':  result += "&gt;"; break;
            case '&':  result += "&amp;"; break;
            case '\"': result += "&quot;"; break;
            default:
               // Tabs, CR and LF are legal XML text; other controls are not.
               if (static_cast<unsigned char>(character) >= 32 || character == '\t' || character == '\r' || character == '\n')
                  result += character;
               break;
            }
         }

         return result;
      }

      AnsiString Sha256Hex(const AnsiString &bytes)
      {
         unsigned char digest[SHA256_DIGEST_LENGTH] = {};
         SHA256(reinterpret_cast<const unsigned char *>(bytes.c_str()), static_cast<size_t>(bytes.GetLength()), digest);

         static const char *hex = "0123456789abcdef";
         AnsiString result;
         for (int i = 0; i < SHA256_DIGEST_LENGTH; i++)
         {
            result += hex[digest[i] >> 4];
            result += hex[digest[i] & 0x0f];
         }
         return result;
      }

      int HexDigit(char c)
      {
         if (c >= '0' && c <= '9') return c - '0';
         if (c >= 'a' && c <= 'f') return c - 'a' + 10;
         if (c >= 'A' && c <= 'F') return c - 'A' + 10;
         return -1;
      }

      // %XX -> the byte. A '+' stays a '+': this is a path, not a form.
      AnsiString PercentDecode(const AnsiString &value)
      {
         AnsiString result;
         result.reserve(value.GetLength());

         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value[i];
            if (c == '%' && i + 2 < value.GetLength())
            {
               int high = HexDigit(value[i + 1]);
               int low = HexDigit(value[i + 2]);
               if (high >= 0 && low >= 0)
               {
                  result += static_cast<char>((high << 4) | low);
                  i += 2;
                  continue;
               }
            }
            result += c;
         }

         return result;
      }

      // One path segment for an href: the unreserved characters and '@' (a
      // pchar) as they are, everything else percent-encoded.
      AnsiString PercentEncodeSegment(const AnsiString &value)
      {
         static const char *hex = "0123456789ABCDEF";
         AnsiString result;
         result.reserve(value.GetLength() + 8);

         for (int i = 0; i < value.GetLength(); i++)
         {
            unsigned char c = static_cast<unsigned char>(value[i]);
            bool keep = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                        c == '-' || c == '.' || c == '_' || c == '~' || c == '@';
            if (keep)
            {
               result += static_cast<char>(c);
               continue;
            }

            result += '%';
            result += hex[c >> 4];
            result += hex[c & 0x0f];
         }

         return result;
      }

      // A username as a log line may carry it: no control characters (a CR
      // or LF in a Base64-decoded credential would start a line of the
      // attacker's choosing), and not longer than a name can be.
      String LogSafe(const String &value)
      {
         String text;
         for (int i = 0; i < value.GetLength() && i < 200; i++)
            text += value[i] < 32 ? _T('?') : value[i];
         return text;
      }

      AnsiString EtagOf(const AnsiString &bytes)
      {
         return "\"" + Sha256Hex(bytes).Left(32) + "\"";
      }

      // The path of the request-target, percent-decoded, without the query,
      // as segments. A trailing slash is remembered by an empty last segment.
      std::vector<AnsiString> PathSegments(const AnsiString &target)
      {
         AnsiString path = target;
         int query = path.Find("?");
         if (query >= 0)
            path = path.Mid(0, query);

         std::vector<AnsiString> segments;
         AnsiString current;
         for (int i = 0; i < path.GetLength(); i++)
         {
            char c = path[i];
            if (c == '/')
            {
               if (i > 0)
                  segments.push_back(PercentDecode(current));
               current = "";
               continue;
            }
            current += c;
         }
         segments.push_back(PercentDecode(current));
         return segments;
      }

      HttpResponse Respond(int status, const AnsiString &contentType, const AnsiString &body, const AnsiString &extraHeaders)
      {
         HttpResponse response;
         response.status = status;
         response.content_type = contentType;
         response.body = body;
         response.extra_headers = extraHeaders;
         response.extra_headers += DavHeader;
         response.extra_headers += "Cache-Control: no-store\r\n";
         return response;
      }

      HttpResponse Text(int status, const AnsiString &text, const AnsiString &extraHeaders)
      {
         return Respond(status, "text/plain; charset=utf-8", text + "\r\n", extraHeaders);
      }

      HttpResponse Xml(int status, const AnsiString &xml, const AnsiString &extraHeaders)
      {
         return Respond(status, "application/xml; charset=utf-8", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + xml, extraHeaders);
      }

      HttpResponse Unauthorized()
      {
         return Text(401, "authentication required", "WWW-Authenticate: Basic realm=\"hMailServer\"\r\n");
      }

      HttpResponse NotFound()
      {
         return Text(404, "not found");
      }

      // HTTP Basic as the account, through AccountLogon: the same password
      // schemes, directory-linked accounts, app passwords, per-name lockout,
      // auto-ban accounting and last-logon stamp as IMAP. False with no or
      // wrong credentials; disconnect says the auto-ban wants the connection
      // dropped.
      bool Authenticate(const HttpRequest &request, const char *service, std::shared_ptr<const Account> &account, bool &disconnect)
      {
         disconnect = false;

         AnsiString header = request.Header("authorization");
         if (header.GetLength() < 6 || header.Mid(0, 6).CompareNoCase("basic ") != 0)
            return false;

         AnsiString encoded = Trimmed(header.Mid(6));
         AnsiString decoded = Base64::Decode(encoded, encoded.GetLength());
         int separator = decoded.Find(":");
         if (separator <= 0)
            return false;

         String username = Wide(decoded.Mid(0, separator));
         String password = Wide(decoded.Mid(separator + 1));

         bool viaAppPassword = false;
         account = AccountLogon().Logon(request.peer, username, password, disconnect, &viaAppPassword);
         if (!account)
         {
            LOG_APPLICATION(String(service) + ": account authentication failed for " + LogSafe(username) + " from " + String(request.peer.ToString()) + ".");
            return false;
         }

         if (!account->GetActive())
         {
            LOG_APPLICATION(String(service) + ": account " + username + " authenticated but is inactive; refused.");
            account.reset();
            return false;
         }

         return true;
      }

      // The If-Match / If-None-Match rules of RFC 7232 for a write. current is
      // the ETag of what is there, or empty when nothing is. False means 412.
      bool PreconditionsHold(const HttpRequest &request, const AnsiString &current)
      {
         AnsiString ifMatch = Trimmed(request.Header("if-match"));
         if (!ifMatch.IsEmpty())
         {
            if (current.IsEmpty())
               return false;
            if (ifMatch != "*")
            {
               bool matched = false;
               std::vector<AnsiString> tags = StringParser::SplitString(ifMatch, ",");
               for (size_t i = 0; !matched && i < tags.size(); i++)
                  matched = Trimmed(tags[i]) == current;
               if (!matched)
                  return false;
            }
         }

         AnsiString ifNoneMatch = Trimmed(request.Header("if-none-match"));
         if (!ifNoneMatch.IsEmpty())
         {
            if (ifNoneMatch == "*")
               return current.IsEmpty();

            std::vector<AnsiString> tags = StringParser::SplitString(ifNoneMatch, ",");
            for (size_t i = 0; i < tags.size(); i++)
            {
               AnsiString tag = Trimmed(tags[i]);
               if (tag.StartsWith("W/"))
                  tag = tag.Mid(2);
               if (!current.IsEmpty() && tag == current)
                  return false;
            }
         }

         return true;
      }
   }
}
