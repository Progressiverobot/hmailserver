// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

namespace HM
{
   // Fetches and caches the OAuth2 bearer token the outbound SMTP client
   // presents as XOAUTH2 when relaying through a provider that has switched
   // Basic authentication off - Microsoft 365 being the one with a date on it
   // (SMTP AUTH Basic ends December 2026; Exchange Online implements XOAUTH2
   // only, not RFC 7628 OAUTHBEARER).
   //
   // The grant is client_credentials: this server authenticates AS ITSELF to
   // the identity provider's token endpoint and relays as the configured
   // account. There is no user interaction, which is what makes the flow
   // suitable for an MTA - and what the provider's "SMTP.SendAsApp" style
   // permission exists for.
   //
   // The token is cached until 80% of its lifetime has passed: providers hand
   // out hour-long tokens, and fetching one per delivery would put an HTTPS
   // round-trip in front of every message while briefly-cached tokens risk
   // expiring mid-session. A fetch failure is reported to the caller with the
   // reason; it is never papered over with a stale token past its refresh
   // point being older than its actual lifetime.
   class OutboundOAuth2TokenClient : public Singleton<OutboundOAuth2TokenClient>
   {
   public:
      OutboundOAuth2TokenClient();

      // A currently-valid bearer token, from cache or freshly fetched. False
      // means no token could be obtained and errorMessage says why - the caller
      // decides what an unauthenticated delivery attempt should do.
      bool GetToken(String &token, String &errorMessage);

      // Drops the cached token, so the next GetToken fetches. Called when the
      // relay rejects a token the cache considered valid - revocation and clock
      // skew both look exactly like that from here.
      void Invalidate();

   private:
      bool FetchToken_(String &token, __int64 &expiresInSeconds, String &errorMessage);

      static bool HttpsPost_(const String &host, const AnsiString &path, const AnsiString &body,
                             AnsiString &responseBody, String &errorMessage);

      static AnsiString UrlEncode_(const String &value);

      boost::recursive_mutex mutex_;
      String cached_token_;
      __int64 cache_expires_at_ = 0;
   };
}
