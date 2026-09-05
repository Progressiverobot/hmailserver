// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <vector>
#include <string>

typedef struct evp_pkey_st EVP_PKEY;

namespace HM
{
   // The identity provider's published signing keys (RFC 7517 JWK Set), fetched from
   // the [Settings] OAuth2JwksUrl and cached.
   //
   // Before this existed the validator knew one key, from a PEM file an administrator
   // copied by hand, and a provider that rotated its keys - which the large ones do on a
   // schedule - stopped every bearer logon until somebody noticed and copied the new
   // one. The JWK Set is the document providers publish for exactly this: every key
   // currently valid, each named by its "kid", the same "kid" a token names in its
   // header.
   //
   // Refresh policy: the document is re-fetched when it is older than
   // OAuth2JwksCacheSeconds, and immediately when a token names a "kid" the cache does
   // not hold - that is what a rotation looks like from here. The second rule is rate
   // limited (one fetch per RefreshCooldownSeconds) so that a stream of tokens with
   // invented key ids cannot turn the validator into a request generator against the
   // provider. A failed fetch keeps the keys already held: a provider outage should not
   // log everyone out while the keys have not actually changed.
   class JwksKeySet : public Singleton<JwksKeySet>
   {
   public:

      JwksKeySet();
      ~JwksKeySet();

      // The key named by kid (or, for a token that names none, the only key of the
      // expected type) as a new reference the caller must EVP_PKEY_free. expected_type
      // is EVP_PKEY_RSA or EVP_PKEY_EC - the kind the token's algorithm needs, so a key
      // of the wrong kind is never handed back, whatever its name. False, with error
      // set, when there is no such key.
      bool GetKey(const String &url, int cache_seconds, const AnsiString &kid, int expected_type, EVP_PKEY **out_key, AnsiString &error);

      // Forgets everything, so the next lookup fetches. For Reinitialize and the tests.
      void Clear();

   private:

      struct Entry
      {
         Entry() : type(0), key(nullptr) {}

         std::string kid;
         int type;
         EVP_PKEY *key;
      };

      bool Refresh_(const String &url, AnsiString &error);
      void ReleaseKeys_();

      static bool ParseJwk_(const AnsiString &kty, const AnsiString &crv, const AnsiString &n, const AnsiString &e,
                            const AnsiString &x, const AnsiString &y, Entry &entry, AnsiString &error);
      static EVP_PKEY *MakeRsaKey_(const AnsiString &modulus, const AnsiString &exponent);
      static EVP_PKEY *MakeP256Key_(const AnsiString &x, const AnsiString &y);

      // One unknown-kid refresh a minute; and after a fetch that failed, no retry for
      // half a minute, so a provider outage costs one request per interval rather than
      // one per logon attempt.
      static const int RefreshCooldownSeconds = 60;
      static const int FailureHoldSeconds = 30;

      boost::recursive_mutex mutex_;
      String cached_url_;
      std::vector<Entry> keys_;
      __int64 fetched_at_;
      __int64 last_miss_refresh_at_;
      __int64 last_failed_refresh_at_;
   };
}
