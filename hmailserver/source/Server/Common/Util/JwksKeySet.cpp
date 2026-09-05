// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "JwksKeySet.h"
#include "HttpsClient.h"
#include "OAuth2TokenValidator.h"

#include <boost/property_tree/ptree.hpp>
#include <boost/property_tree/json_parser.hpp>

#include <openssl/evp.h>
#include <openssl/bn.h>
#include <openssl/param_build.h>
#include <openssl/core_names.h>

#include <sstream>
#include <time.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   JwksKeySet::JwksKeySet() :
      fetched_at_(0),
      last_miss_refresh_at_(0),
      last_failed_refresh_at_(0)
   {
   }

   JwksKeySet::~JwksKeySet()
   {
      ReleaseKeys_();
   }

   void
   JwksKeySet::Clear()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      ReleaseKeys_();
      cached_url_.Empty();
      fetched_at_ = 0;
      last_miss_refresh_at_ = 0;
      last_failed_refresh_at_ = 0;
   }

   void
   JwksKeySet::ReleaseKeys_()
   {
      for (Entry &entry : keys_)
      {
         if (entry.key != nullptr)
            EVP_PKEY_free(entry.key);
      }

      keys_.clear();
   }

   bool
   JwksKeySet::GetKey(const String &url, int cache_seconds, const AnsiString &kid, int expected_type, EVP_PKEY **out_key, AnsiString &error)
   {
      *out_key = nullptr;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      const __int64 now = (__int64) time(nullptr);

      if (cache_seconds < 10)
         cache_seconds = 10;

      if (cached_url_.CompareNoCase(url) != 0)
      {
         // A different document: nothing held here is about it, and neither is the
         // hold from a fetch of the old one that failed.
         ReleaseKeys_();
         cached_url_ = url;
         fetched_at_ = 0;
         last_miss_refresh_at_ = 0;
         last_failed_refresh_at_ = 0;
      }

      const bool stale = fetched_at_ == 0 || now - fetched_at_ > cache_seconds;

      if (stale && now - last_failed_refresh_at_ >= FailureHoldSeconds)
      {
         AnsiString refreshError;
         if (!Refresh_(url, refreshError) && keys_.empty())
         {
            error = refreshError;
            return false;
         }
      }
      else if (stale && keys_.empty())
      {
         error = "The JWK Set could not be fetched a moment ago; not retrying yet.";
         return false;
      }

      auto find = [&](EVP_PKEY **found) -> bool
      {
         *found = nullptr;

         if (!kid.IsEmpty())
         {
            for (const Entry &entry : keys_)
            {
               if (entry.type == expected_type && entry.kid == kid.c_str())
               {
                  *found = entry.key;
                  return true;
               }
            }
            return false;
         }

         // A token with no kid can only be matched when there is exactly one key it
         // could mean; two candidates would make the choice a guess.
         EVP_PKEY *only = nullptr;
         int candidates = 0;
         for (const Entry &entry : keys_)
         {
            if (entry.type == expected_type)
            {
               only = entry.key;
               candidates++;
            }
         }

         if (candidates == 1)
         {
            *found = only;
            return true;
         }

         return false;
      };

      EVP_PKEY *found = nullptr;
      if (!find(&found))
      {
         // Unknown to the cache: the shape of a rotation. One fetch, rate limited.
         if (now - last_miss_refresh_at_ >= RefreshCooldownSeconds && now - last_failed_refresh_at_ >= FailureHoldSeconds)
         {
            last_miss_refresh_at_ = now;
            AnsiString refreshError;
            Refresh_(url, refreshError);
            find(&found);
         }
      }

      if (found == nullptr)
      {
         if (kid.IsEmpty())
            error = "The token names no key id and the JWK Set does not hold exactly one key of the required kind.";
         else
            error = "The JWK Set holds no key of the required kind with kid '" + kid + "'.";
         return false;
      }

      if (!EVP_PKEY_up_ref(found))
      {
         error = "The key could not be referenced.";
         return false;
      }

      *out_key = found;
      return true;
   }

   bool
   JwksKeySet::Refresh_(const String &url, AnsiString &error)
   {
      HttpsClient::Response response;
      String requestError;
      std::vector<AnsiString> noHeaders;
      if (!HttpsClient::Request("GET", AnsiString(url), noHeaders, "", "", response, requestError))
      {
         last_failed_refresh_at_ = (__int64) time(nullptr);
         error = "The JWK Set could not be fetched: " + AnsiString(requestError);
         LOG_DEBUG("OAuth2: " + error);
         return false;
      }

      if (response.status_code != 200)
      {
         last_failed_refresh_at_ = (__int64) time(nullptr);
         error.Format("The JWK Set endpoint answered HTTP %d.", response.status_code);
         LOG_DEBUG("OAuth2: " + error);
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
         error = "The JWK Set is not valid JSON.";
         LOG_DEBUG("OAuth2: " + error);
         return false;
      }

      std::vector<Entry> parsed;
      boost::optional<boost::property_tree::ptree &> keys = document.get_child_optional("keys");
      if (keys)
      {
         for (const auto &item : *keys)
         {
            const boost::property_tree::ptree &jwk = item.second;

            // A key published for something other than signing - "use":"enc" - is not
            // a key a token may be verified with, whatever its kind.
            const std::string use = jwk.get<std::string>("use", "sig");
            if (use != "sig")
               continue;

            Entry entry;
            AnsiString parseError;
            if (!ParseJwk_(jwk.get<std::string>("kty", "").c_str(), jwk.get<std::string>("crv", "").c_str(),
                           jwk.get<std::string>("n", "").c_str(), jwk.get<std::string>("e", "").c_str(),
                           jwk.get<std::string>("x", "").c_str(), jwk.get<std::string>("y", "").c_str(),
                           entry, parseError))
            {
               // One unusable key does not spoil the set; the ones that parse are the
               // ones tokens will be verified with.
               LOG_DEBUG("OAuth2: a key in the JWK Set was skipped: " + parseError);
               continue;
            }

            entry.kid = jwk.get<std::string>("kid", "");
            parsed.push_back(entry);
         }
      }

      if (parsed.empty())
      {
         error = "The JWK Set holds no usable signing key (RSA, or EC on P-256).";
         LOG_DEBUG("OAuth2: " + error);
         return false;
      }

      ReleaseKeys_();
      keys_.swap(parsed);
      fetched_at_ = (__int64) time(nullptr);

      AnsiString message;
      message.Format("OAuth2: the JWK Set was fetched; %d signing key(s) held.", (int) keys_.size());
      LOG_DEBUG(message);

      return true;
   }

   bool
   JwksKeySet::ParseJwk_(const AnsiString &kty, const AnsiString &crv, const AnsiString &n, const AnsiString &e,
                         const AnsiString &x, const AnsiString &y, Entry &entry, AnsiString &error)
   {
      if (kty == "RSA")
      {
         if (n.IsEmpty() || e.IsEmpty())
         {
            error = "an RSA key without n and e";
            return false;
         }

         entry.key = MakeRsaKey_(n, e);
         entry.type = EVP_PKEY_RSA;
      }
      else if (kty == "EC")
      {
         if (crv != "P-256")
         {
            error = "an EC key on a curve other than P-256 (" + crv + ")";
            return false;
         }

         if (x.IsEmpty() || y.IsEmpty())
         {
            error = "an EC key without x and y";
            return false;
         }

         entry.key = MakeP256Key_(x, y);
         entry.type = EVP_PKEY_EC;
      }
      else
      {
         error = "a key of kind '" + kty + "'";
         return false;
      }

      if (entry.key == nullptr)
      {
         error = "a " + kty + " key whose parameters OpenSSL rejected";
         return false;
      }

      return true;
   }

   EVP_PKEY *
   JwksKeySet::MakeRsaKey_(const AnsiString &modulus, const AnsiString &exponent)
   {
      AnsiString nBytes, eBytes;
      if (!OAuth2TokenValidator::Base64UrlDecode(modulus, nBytes) || !OAuth2TokenValidator::Base64UrlDecode(exponent, eBytes) ||
          nBytes.IsEmpty() || eBytes.IsEmpty())
         return nullptr;

      BIGNUM *n = BN_bin2bn((const unsigned char *) nBytes.c_str(), nBytes.GetLength(), nullptr);
      BIGNUM *e = BN_bin2bn((const unsigned char *) eBytes.c_str(), eBytes.GetLength(), nullptr);

      EVP_PKEY *key = nullptr;
      OSSL_PARAM_BLD *builder = OSSL_PARAM_BLD_new();
      OSSL_PARAM *params = nullptr;
      EVP_PKEY_CTX *ctx = nullptr;

      if (n != nullptr && e != nullptr && builder != nullptr &&
          OSSL_PARAM_BLD_push_BN(builder, OSSL_PKEY_PARAM_RSA_N, n) == 1 &&
          OSSL_PARAM_BLD_push_BN(builder, OSSL_PKEY_PARAM_RSA_E, e) == 1)
      {
         params = OSSL_PARAM_BLD_to_param(builder);
         ctx = EVP_PKEY_CTX_new_from_name(nullptr, "RSA", nullptr);
         if (params != nullptr && ctx != nullptr &&
             EVP_PKEY_fromdata_init(ctx) == 1 &&
             EVP_PKEY_fromdata(ctx, &key, EVP_PKEY_PUBLIC_KEY, params) != 1)
         {
            key = nullptr;
         }
      }

      if (ctx != nullptr) EVP_PKEY_CTX_free(ctx);
      if (params != nullptr) OSSL_PARAM_free(params);
      if (builder != nullptr) OSSL_PARAM_BLD_free(builder);
      if (n != nullptr) BN_free(n);
      if (e != nullptr) BN_free(e);

      // A 2048-bit modulus is 256 octets; anything under 2048 bits is a key no
      // provider publishes today and one this server should not accept a signature from.
      if (key != nullptr && EVP_PKEY_get_bits(key) < 2048)
      {
         EVP_PKEY_free(key);
         key = nullptr;
      }

      return key;
   }

   EVP_PKEY *
   JwksKeySet::MakeP256Key_(const AnsiString &x, const AnsiString &y)
   {
      AnsiString xBytes, yBytes;
      if (!OAuth2TokenValidator::Base64UrlDecode(x, xBytes) || !OAuth2TokenValidator::Base64UrlDecode(y, yBytes) ||
          xBytes.GetLength() != 32 || yBytes.GetLength() != 32)
         return nullptr;

      // The uncompressed point: 0x04 || X || Y.
      unsigned char point[65];
      point[0] = 0x04;
      memcpy(point + 1, xBytes.c_str(), 32);
      memcpy(point + 33, yBytes.c_str(), 32);

      EVP_PKEY *key = nullptr;
      OSSL_PARAM_BLD *builder = OSSL_PARAM_BLD_new();
      OSSL_PARAM *params = nullptr;
      EVP_PKEY_CTX *ctx = nullptr;

      if (builder != nullptr &&
          OSSL_PARAM_BLD_push_utf8_string(builder, OSSL_PKEY_PARAM_GROUP_NAME, "P-256", 0) == 1 &&
          OSSL_PARAM_BLD_push_octet_string(builder, OSSL_PKEY_PARAM_PUB_KEY, point, sizeof(point)) == 1)
      {
         params = OSSL_PARAM_BLD_to_param(builder);
         ctx = EVP_PKEY_CTX_new_from_name(nullptr, "EC", nullptr);
         if (params != nullptr && ctx != nullptr &&
             EVP_PKEY_fromdata_init(ctx) == 1 &&
             EVP_PKEY_fromdata(ctx, &key, EVP_PKEY_PUBLIC_KEY, params) != 1)
         {
            key = nullptr;
         }
      }

      if (ctx != nullptr) EVP_PKEY_CTX_free(ctx);
      if (params != nullptr) OSSL_PARAM_free(params);
      if (builder != nullptr) OSSL_PARAM_BLD_free(builder);

      return key;
   }
}
