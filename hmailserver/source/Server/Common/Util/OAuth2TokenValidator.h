// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

typedef struct evp_pkey_st EVP_PKEY;

namespace HM
{
   // Configuration for the OAuth2 bearer-token validator. Loaded from the [Settings]
   // OAuth2* INI keys, but passed explicitly so the validation core can be unit-tested.
   struct OAuth2Config
   {
      OAuth2Config() : enabled(false), require_tls(true), jwks_cache_seconds(3600), introspection_cache_seconds(300), introspection_fail_open(false) {}

      bool enabled;
      bool require_tls;
      AnsiString allowed_algorithms; // comma-separated allow-list, e.g. "RS256,HS256"
      AnsiString hmac_secret;        // shared secret (UTF-8 bytes) for HS256
      String rsa_public_key_file;    // PEM public key file for RS256/ES256
      AnsiString issuer;             // expected "iss" (empty = do not check)
      AnsiString audience;           // expected "aud" (empty = do not check)
      AnsiString username_claim;     // claim holding the login name (default "email")

      // RFC 7517 JWK Set URL the provider publishes its signing keys at. When set, an
      // RS256/ES256 token's "kid" selects the key from there (JwksKeySet), with the PEM
      // file as the fallback; empty keeps the PEM file the only source.
      String jwks_url;
      int jwks_cache_seconds;

      // RFC 7662 introspection endpoint, asked after a token verifies locally whether
      // it is still active (TokenIntrospection); empty = no revocation check.
      String introspection_url;
      AnsiString introspection_client_id;
      AnsiString introspection_client_secret;
      int introspection_cache_seconds;
      bool introspection_fail_open;
   };

   // Local verifier for OAuth2 / OpenID Connect bearer tokens presented through the
   // SASL XOAUTH2 and OAUTHBEARER mechanisms. The token is a JWS-signed JWT that is
   // validated entirely offline against an administrator-configured signing key:
   //
   //   * the JWT "alg" must appear in the configured allow-list and is NEVER "none"
   //     (the algorithm-confusion / "alg:none" attack class is rejected outright);
   //   * the signature is verified with OpenSSL (HMAC-SHA-256 for HS256, RSA/EC
   //     SHA-256 for RS256/ES256) over the exact "header.payload" octets;
   //   * the standard claims are checked: "exp" (required) must be in the future,
   //     "nbf"/"iat" (optional) must not be in the future, and "iss"/"aud" must match
   //     the configured values when those are set;
   //   * the configured username claim (default "email") is returned as the login
   //     identity, which the caller compares against the SASL-asserted identity.
   //
   // Two optional round trips to the identity provider sit on top of that, both off
   // unless configured: the signing key may come from the provider's published JWK
   // Set instead of a hand-copied PEM file (OAuth2JwksUrl, see JwksKeySet), and a
   // token that verified may be checked for revocation (OAuth2IntrospectionUrl, see
   // TokenIntrospection). See the administrator documentation for the [Settings]
   // OAuth2* INI keys.
   class OAuth2TokenValidator
   {
   public:
      static bool IsEnabled();
      static bool RequireTLS();

      // Verifies a raw JWT bearer token. On success returns true and sets out_username
      // to the configured username claim. out_error receives a short diagnostic that is
      // safe for the server log but must never be returned verbatim to the client.
      static bool ValidateBearerToken(const AnsiString &sToken, String &out_username, AnsiString &out_error);

      // Validation core driven by an explicit configuration (used by ValidateBearerToken
      // and by the self-test). Performs no INI access.
      static bool ValidateWithConfig(const OAuth2Config &config, const AnsiString &sToken, String &out_username, AnsiString &out_error);

      // Parses an already base64-decoded SASL XOAUTH2 or OAUTHBEARER client response and
      // extracts the bearer token plus the client-asserted identity (which may be empty).
      // Returns true when a bearer token was found.
      static bool ParseSaslBearer(const AnsiString &sDecoded, AnsiString &out_identity, AnsiString &out_token);

      static OAuth2Config LoadConfig();

      // JWS carries an ECDSA signature as the raw fixed-width R||S pair (RFC 7515
      // section 3.4); OpenSSL verifies X9.62 DER. Converting between them is the whole
      // reason ES256 was refused rather than merely broken. Public for the self-test,
      // because a transcode verified only through its own round trip proves that the
      // encoder agrees with the decoder and nothing more.
      static bool RawEcdsaSignatureToDer_(const AnsiString &raw, AnsiString &der);

      // base64url (RFC 4648 section 5, unpadded) to bytes. Public because a JWK Set
      // carries its key parameters in the same encoding.
      static bool Base64UrlDecode(const AnsiString &sInput, AnsiString &sOutput) { return Base64UrlDecode_(sInput, sOutput); }

   private:
      static bool Base64UrlDecode_(const AnsiString &sInput, AnsiString &sOutput);
      // Verifies an RS256/ES256 signature with the key the configuration provides for
      // this token: from the JWK Set by kid when OAuth2JwksUrl is set, else - or if
      // the set has no such key - from the PEM file. out_error is set only when the
      // JWK Set lookup itself failed, so the log says why rather than "verification
      // failed".
      static bool VerifyAsymmetric_(const OAuth2Config &config, const AnsiString &kid, const AnsiString &sSigningInput,
                                    const AnsiString &sSignature, int expectedKeyType, AnsiString &out_error);
      static bool VerifyWithKey_(const AnsiString &sSigningInput, const AnsiString &sSignature, EVP_PKEY *key);
      static bool IsAlgorithmAllowed_(const AnsiString &sAllowList, const AnsiString &sAlg);
      static bool VerifyHs256_(const AnsiString &sSigningInput, const AnsiString &sSignature, const AnsiString &sSecret);
      // expectedKeyType is an EVP_PKEY_* id the loaded key must actually be. It is not
      // decoration: without it an EC public key configured against alg=RS256 - or the
      // reverse - reaches OpenSSL and fails with 'signature verification failed',
      // which describes the symptom of an algorithm-confusion attempt rather than the
      // cause.
      static bool VerifyWithPublicKey_(const AnsiString &sSigningInput, const AnsiString &sSignature,
                                       const String &sKeyFile, int expectedKeyType);

      static bool ConstantTimeEquals_(const AnsiString &a, const AnsiString &b);
   };

   class OAuth2TokenValidatorTester
   {
   public:
      void Test();
   };
}
