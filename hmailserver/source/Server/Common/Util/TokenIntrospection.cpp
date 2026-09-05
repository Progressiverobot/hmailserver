// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "TokenIntrospection.h"
#include "HttpsClient.h"
#include "OAuth2TokenValidator.h"
#include "../Mime/MimeCode.h"

#include <boost/property_tree/ptree.hpp>
#include <boost/property_tree/json_parser.hpp>

#include <openssl/evp.h>

#include <sstream>
#include <time.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   TokenIntrospection::TokenIntrospection()
   {
   }

   void
   TokenIntrospection::Clear()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      verdicts_.clear();
   }

   bool
   TokenIntrospection::IsActive(const OAuth2Config &config, const AnsiString &token, __int64 token_expires_at, AnsiString &reason)
   {
      const __int64 now = (__int64) time(nullptr);
      const std::string digest = Digest_(token);

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         // Verdicts are kept for the configured window; anything past it goes when
         // it is next seen, and the whole map is swept when it grows, so a stream of
         // distinct tokens cannot make it grow without bound.
         if (verdicts_.size() > 10000)
         {
            for (auto iter = verdicts_.begin(); iter != verdicts_.end();)
            {
               if (iter->second.expires_at <= now)
                  iter = verdicts_.erase(iter);
               else
                  ++iter;
            }
         }

         auto cached = verdicts_.find(digest);
         if (cached != verdicts_.end())
         {
            if (cached->second.expires_at > now)
            {
               if (!cached->second.active)
                  reason = "the provider reported the token inactive";
               return cached->second.active;
            }

            verdicts_.erase(cached);
         }
      }

      bool active = false;
      AnsiString askReason;
      const bool answered = Ask_(config, token, active, askReason);

      if (!answered)
      {
         if (config.introspection_fail_open)
         {
            // Written-down preference for availability over the revocation check; the
            // log records each time it decided a logon.
            LOG_APPLICATION("OAuth2: token introspection could not be completed (" + String(askReason) + "); accepting the token because OAuth2IntrospectionFailOpen=1.");
            return true;
         }

         reason = "introspection could not be completed (" + askReason + ")";
         return false;
      }

      // Cache the verdict, positive or negative, for the configured window but never
      // past the token's own expiry - after that the answer is moot.
      __int64 window = config.introspection_cache_seconds;
      if (window < 0)
         window = 0;

      __int64 expiresAt = now + window;
      if (token_expires_at > 0 && token_expires_at < expiresAt)
         expiresAt = token_expires_at;

      if (window > 0)
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);
         Verdict verdict;
         verdict.active = active;
         verdict.expires_at = expiresAt;
         verdicts_[digest] = verdict;
      }

      if (!active)
         reason = "the provider reported the token inactive";

      return active;
   }

   bool
   TokenIntrospection::Ask_(const OAuth2Config &config, const AnsiString &token, bool &active, AnsiString &reason)
   {
      // RFC 7662 section 2.1: a form-encoded POST, authenticated as the client the
      // provider registered for this server (RFC 6749 section 2.3.1: HTTP Basic with
      // the form-encoded id and secret).
      AnsiString body = "token=" + HttpsClient::FormEncode(token) + "&token_type_hint=access_token";

      std::vector<AnsiString> headers;
      if (!config.introspection_client_id.IsEmpty())
      {
         AnsiString credential = HttpsClient::FormEncode(config.introspection_client_id) + ":" + HttpsClient::FormEncode(config.introspection_client_secret);

         MimeCodeBase64 encoder;
         encoder.SetInput(credential, credential.GetLength(), true);
         encoder.AddLineBreak(false);
         AnsiString encoded;
         encoder.GetOutput(encoded);

         headers.push_back("Authorization: Basic " + encoded);
      }

      HttpsClient::Response response;
      String requestError;
      if (!HttpsClient::Request("POST", AnsiString(config.introspection_url), headers, "application/x-www-form-urlencoded", body, response, requestError))
      {
         reason = AnsiString(requestError);
         return false;
      }

      if (response.status_code != 200)
      {
         reason.Format("the introspection endpoint answered HTTP %d", response.status_code);
         return false;
      }

      boost::property_tree::ptree document;
      try
      {
         std::istringstream stream(std::string(response.body.c_str()));
         boost::property_tree::read_json(stream, document);
      }
      catch (const std::exception &)
      {
         reason = "the introspection response is not valid JSON";
         return false;
      }

      // "active" is the one REQUIRED member (RFC 7662 section 2.2), and it is a
      // boolean; a response without it is not an answer.
      boost::optional<std::string> activeText = document.get_optional<std::string>("active");
      if (!activeText)
      {
         reason = "the introspection response carries no 'active' member";
         return false;
      }

      active = (*activeText == "true");
      return true;
   }

   std::string
   TokenIntrospection::Digest_(const AnsiString &token)
   {
      // The token itself is a credential; the cache is keyed by its digest so a
      // memory dump of the map yields nothing replayable.
      unsigned char digest[EVP_MAX_MD_SIZE];
      unsigned int length = 0;

      if (EVP_Digest(token.c_str(), (size_t) token.GetLength(), digest, &length, EVP_sha256(), nullptr) != 1)
         return std::string(token.c_str());

      static const char *hex = "0123456789abcdef";
      std::string result;
      result.reserve(length * 2);
      for (unsigned int i = 0; i < length; i++)
      {
         result += hex[digest[i] >> 4];
         result += hex[digest[i] & 0x0F];
      }

      return result;
   }
}
