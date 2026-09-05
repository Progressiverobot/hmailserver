// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <map>
#include <string>

namespace HM
{
   struct OAuth2Config;

   // OAuth 2.0 Token Introspection (RFC 7662): asks the identity provider whether a
   // bearer token is still active, after the token has verified locally.
   //
   // A signed token proves who issued it and when it expires, and nothing about what
   // has happened since: a user removed, a session revoked, a device wiped. Until this
   // existed a stolen token was good until "exp". Introspection is the provider's
   // answer to that, and it is asked here for every token that passes the local checks,
   // with the verdict cached for OAuth2IntrospectionCacheSeconds (bounded by the token's
   // own expiry) so a client that logs on every minute does not put a round trip in
   // front of every one.
   //
   // A verdict the provider cannot give - endpoint down, malformed answer - is a
   // refusal by default: RFC 7662 says an unanswerable token is to be treated as
   // inactive, and a revocation check that passes when it cannot check is not one.
   // OAuth2IntrospectionFailOpen=1 is the administrator's written-down choice to prefer
   // availability, and the log says so every time it is exercised.
   class TokenIntrospection : public Singleton<TokenIntrospection>
   {
   public:

      TokenIntrospection();

      // True when the provider says the token is active (or the cache remembers that it
      // did). False otherwise, with reason set for the log.
      bool IsActive(const OAuth2Config &config, const AnsiString &token, __int64 token_expires_at, AnsiString &reason);

      // Forgets every cached verdict. For Reinitialize and the tests.
      void Clear();

   private:

      struct Verdict
      {
         Verdict() : active(false), expires_at(0) {}

         bool active;
         __int64 expires_at;
      };

      static bool Ask_(const OAuth2Config &config, const AnsiString &token, bool &active, AnsiString &reason);
      static std::string Digest_(const AnsiString &token);

      boost::recursive_mutex mutex_;
      std::map<std::string, Verdict> verdicts_;
   };
}
