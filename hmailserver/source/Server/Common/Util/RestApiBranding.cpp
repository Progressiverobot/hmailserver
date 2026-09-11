// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// What the webmail says it is: a name, a logo and an announcement, set by an
// administrator server-wide and, when a domain wants its own, per domain
// (PUT /api/v1/portal/branding), read by anyone (GET /api/v1/portal/branding,
// the sign-in page has no account yet) and written into the page itself for
// the first paint. Kept in the [Settings] store as WebmailBrandName,
// WebmailBrandLogo and WebmailAnnouncement, each with an optional
// ".<domain>" twin; the store takes a line of up to 4000 characters, which is
// why a logo is an inline SVG or a small PNG, never a file.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "../Application/IniFileSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int BrandNameMaximum = 100;
      const int AnnouncementMaximum = 1000;
      const int LogoMaximum = 3900;

      String Setting(const String &key)
      {
         return IniFileSettings::Instance()->GetSettingsValue(key);
      }

      // The domain's value when it has one, else the server's.
      String Branded(const String &key, const String &domain)
      {
         if (!domain.IsEmpty())
         {
            String own = Setting(key + _T(".") + domain);
            if (!own.IsEmpty())
               return own;
         }
         return Setting(key);
      }

      bool ValidDomain(const String &domain)
      {
         if (domain.IsEmpty() || domain.GetLength() > 253)
            return false;
         for (int i = 0; i < domain.GetLength(); i++)
         {
            wchar_t c = domain[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.';
            if (!ok)
               return false;
         }
         return true;
      }

      bool OneLine(const String &value)
      {
         return value.Find(_T("\r")) < 0 && value.Find(_T("\n")) < 0;
      }

      // Written when given, removed when given empty, left alone when absent.
      bool Apply(const JsonValue &document, const std::string &member, const String &key, int maximum, bool logo, AnsiString &problem)
      {
         const JsonValue *value = document.Get(member);
         if (!value)
            return true;
         if (!value->IsString())
         {
            problem = "{\"error\":\"" + AnsiString(member.c_str()) + " is a string\"}";
            return false;
         }
         String text;
         Unicode::MultiByteToWide(AnsiString(value->AsString().c_str()), text);
         text.TrimLeft();
         text.TrimRight();
         if (text.GetLength() > maximum)
         {
            problem.Format("{\"error\":\"%hs is at most %d characters\"}", member.c_str(), maximum);
            return false;
         }
         if (!OneLine(text))
         {
            problem = "{\"error\":\"" + AnsiString(member.c_str()) + " is one line\"}";
            return false;
         }
         if (logo && !text.IsEmpty() && text.Find(_T("data:image/")) != 0)
         {
            problem = "{\"error\":\"logo is an inline image: data:image/svg+xml,... or data:image/png;base64,...\"}";
            return false;
         }
         bool ok = text.IsEmpty() ? IniFileSettings::Instance()->RemoveSettingsValue(key) : IniFileSettings::Instance()->WriteSettingsValue(key, text);
         if (!ok)
         {
            problem = "{\"error\":\"" + AnsiString(member.c_str()) + " could not be stored\"}";
            return false;
         }
         return true;
      }
   }

   AnsiString
   RestApiServer::BrandingJson_(const String &domain)
   {
      String lowered = domain;
      lowered.ToLower();
      AnsiString json;
      json.Format("{\"name\":\"%hs\",\"logo\":\"%hs\",\"announcement\":\"%hs\",\"domain\":\"%hs\"}",
         JsonEscape_(Utf8_(Branded(_T("WebmailBrandName"), lowered))).c_str(),
         JsonEscape_(Utf8_(Branded(_T("WebmailBrandLogo"), lowered))).c_str(),
         JsonEscape_(Utf8_(Branded(_T("WebmailAnnouncement"), lowered))).c_str(),
         JsonEscape_(Utf8_(lowered)).c_str());
      return json;
   }

   HttpResponse
   RestApiServer::HandlePortalBranding_(const AnsiString &query)
   {
      String domain;
      Unicode::MultiByteToWide(QueryParameter_(query, "domain"), domain);
      domain.ToLower();
      if (!domain.IsEmpty() && !ValidDomain(domain))
         domain = _T("");
      HttpResponse response;
      response.status = 200;
      response.content_type = "application/json";
      response.body = BrandingJson_(domain);
      response.extra_headers = "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n";
      return response;
   }

   HttpResponse
   RestApiServer::HandlePortalBrandingPut_(const AnsiString &requestBody)
   {
      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.size()), document, parseError) || !document.IsObject())
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object: name, logo, announcement, and domain for a domain's own\"}");

      String domain;
      Unicode::MultiByteToWide(AnsiString(document.GetString("domain").c_str()), domain);
      domain.TrimLeft();
      domain.TrimRight();
      domain.ToLower();
      if (!domain.IsEmpty() && !ValidDomain(domain))
         return BuildResponse_(400, "{\"error\":\"domain is a domain name\"}");
      String suffix = domain.IsEmpty() ? String() : _T(".") + domain;

      AnsiString problem;
      if (!Apply(document, "name", _T("WebmailBrandName") + suffix, BrandNameMaximum, false, problem) ||
          !Apply(document, "logo", _T("WebmailBrandLogo") + suffix, LogoMaximum, true, problem) ||
          !Apply(document, "announcement", _T("WebmailAnnouncement") + suffix, AnnouncementMaximum, false, problem))
         return BuildResponse_(400, problem);

      return BuildResponse_(200, BrandingJson_(domain));
   }
}
