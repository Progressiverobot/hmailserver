// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// ACME v2 (RFC 8555) client with http-01 challenge support, plus the
// scheduled task that drives automatic certificate renewal.
//
// Disabled by default. Enable with AcmeEnabled=1 in hMailServer.ini and
// configure AcmeContactEmail and AcmeDomains. Issued certificates are
// written as fullchain.pem / privkey.pem in AcmeCertificateDirectory.

#pragma once

#include "../BO/ScheduledTask.h"

#include <openssl/ossl_typ.h>

namespace HM
{
   class AcmeClient
   {
   public:
      AcmeClient();
      ~AcmeClient();

      // When a certificate with these dates should be renewed.
      //
      // A FRACTION of its lifetime rather than a fixed number of days, which is the
      // whole point. Thirty days was right while certificates lasted ninety; it
      // stops being right the moment they do not. Let us Encrypt defaults to 64 days
      // from February 2027, the maximum falls to 100 days in March 2027 and to 47 in
      // March 2029 - and a fixed 30-day window against a 47-day certificate starts
      // renewing seventeen days after issuance and never stops, which is how an
      // operator gets rate-limited by their own CA.
      //
      // Two thirds through the lifetime is what the ACME community settled on and
      // what ARI suggested-windows approximate. It has the property that matters:
      // whatever the lifetime, a third of it is left to notice a failure.
      //
      // Public and pure so the self-test can pin it against the lifetimes the
      // industry is moving to, instead of waiting for a real certificate to age.
      static time_t GetRenewalTime(time_t notBefore, time_t notAfter);

      // The ARI certificate identifier for a certificate (RFC 9773 section 4.1):
      // base64url of the Authority Key Identifier, a dot, base64url of the serial
      // number. Public and static so the self-test can pin it against a real
      // certificate rather than against another copy of this code.
      static bool GetCertificateAriId(const String &certificateFile, AnsiString &ariId);

      // The identifier's construction, separated from reading a certificate so that
      // the self-test can pin it against the worked example RFC 9773 publishes
      // rather than against another certificate this project happens to have.
      static AnsiString BuildAriId(const unsigned char *keyIdentifier, int keyIdentifierLength,
                                   const unsigned char *serialDer, int serialDerLength);

      // Where in a CA-suggested renewal window this server should renew.
      //
      // RFC 9773 section 4.2 says to pick a random point in the window rather than
      // renewing at its start, because the whole purpose of the window is to spread
      // load - a fleet that all renews at "start" has simply moved the thundering
      // herd rather than removed it.
      //
      // The point is derived from the certificate's own identifier instead of from a
      // random number generator, and that is deliberate: this decision is re-taken
      // every hour, and a fresh roll each time would drift the effective renewal
      // towards the start of the window. Deriving it from the identity gives the
      // same answer every hour for this certificate and a different one for the next
      // server along, which is what the RFC is actually asking for.
      static time_t GetRenewalTimeInWindow(const AnsiString &ariId, time_t windowStart, time_t windowEnd);

      // Asks the CA when it would like this certificate renewed. False when the CA
      // does not implement ARI, when the request fails, or when the answer cannot be
      // read - all of which mean "decide for yourself", never "do not renew".
      bool FetchSuggestedRenewalTime_(time_t &renewAt);

      // Parses an RFC 3339 timestamp of the shape ARI responses carry
      // ("2026-01-02T15:04:05Z"). Returns 0 when it cannot be read.
      static time_t ParseRfc3339(const AnsiString &value);

      // The directory holding account.key, fullchain.pem and privkey.pem.
      static String GetCertificateDirectory();

      // Computes the DANE TLSA "3 1 1" payload (SHA-256 over the subject
      // public key info) for the first certificate in a PEM file.
      static bool GetCertificateTlsa(const String &certificate_file, AnsiString &spki_sha256_hex);

      // Runs the full issuance flow. Returns true on success.
      bool RequestCertificate();

      // Whether to renew now.
      //
      // Asks the CA through ARI when it offers it, and falls back to the
      // lifetime-proportional calculation when it does not - which is most CAs
      // today, so the fallback is the normal path rather than an error path.
      //
      // Asking every hour is the intended use of ARI rather than an extravagance:
      // the reason the CA gets a say at all is that it may need to move renewals
      // forward during a mass revocation, and a client that asked once at issuance
      // would never hear about it.
      bool ShouldRenewNow();

      // Finds a challenge whose "type" is the given value, tolerating the
      // whitespace boulder's pretty-printed JSON puts around the colon - the
      // compact-form search this replaces never matched a real Let's Encrypt
      // response (issue #34). Returns the position of the type value, inside
      // the challenge object, or -1. Public and static so the self-test can
      // pin the real CA's response shape.
      static int FindChallengeOfType(const AnsiString &json, const AnsiString &challengeType);

   private:

      struct HttpResponse
      {
         int status_code = 0;
         AnsiString body;
         AnsiString nonce;
         AnsiString location;
      };

      // HTTPS transport.
      bool Transact_(const AnsiString &url, const AnsiString &method, const AnsiString &payload, HttpResponse &response);
      bool SignedPost_(const AnsiString &url, const AnsiString &payload, HttpResponse &response);

      bool FetchDirectory_();
      bool FetchNonce_();
      bool LoadOrCreateAccountKey_();
      bool RegisterAccount_();
      bool CreateOrder_(const std::vector<AnsiString> &domains, HttpResponse &orderResponse);
      bool CompleteAuthorization_(const AnsiString &authorizationUrl);
      bool FinalizeOrder_(const AnsiString &finalizeUrl, const AnsiString &orderUrl, const std::vector<AnsiString> &domains);
      void ApplyCertificate_();

      AnsiString BuildJwk_() const;
      AnsiString GetJwkThumbprint_() const;
      AnsiString SignJws_(const AnsiString &url, const AnsiString &payload, bool useJwk);

      static AnsiString Base64Url_(const unsigned char *data, int length);
      static AnsiString Base64Url_(const AnsiString &data);
      static AnsiString JsonStringValue_(const AnsiString &json, const AnsiString &key, int searchFrom = 0);

      // ACME directory endpoints.
      AnsiString url_new_nonce_;
      AnsiString url_new_account_;
      AnsiString url_new_order_;

      // Absent from the directory of a CA that does not implement ARI, which is
      // most of them today - so everything that uses it has to work without it.
      AnsiString url_renewal_info_;

      AnsiString nonce_;
      AnsiString account_url_;

      EVP_PKEY *account_key_;
   };

   // Process-wide store of pending http-01 challenges. Written by
   // AcmeClient and served either by the transient AcmeChallengeServer
   // or by the always-on WebServicesServer when it owns the HTTP port.
   class AcmeChallengeStore
   {
   public:
      static void Set(const AnsiString &token, const AnsiString &key_authorization);
      static bool Get(const AnsiString &token, AnsiString &key_authorization);
      static void Clear();

   private:
      static boost::recursive_mutex mutex_;
      static std::map<AnsiString, AnsiString> challenges_;
   };

   // Challenge listener: a minimal HTTP server that answers
   // /.well-known/acme-challenge/<token> during validation.
   class AcmeChallengeServer
   {
   public:
      AcmeChallengeServer();
      ~AcmeChallengeServer();

      bool Start(int port);
      void Stop();

      void SetChallenge(const AnsiString &token, const AnsiString &keyAuthorization);

   private:
      void Run_();

      SOCKET listen_socket_;
      std::thread worker_;
      bool running_;

      boost::recursive_mutex mutex_;
      std::map<AnsiString, AnsiString> challenges_;
   };

   class AcmeRenewalTask : public ScheduledTask
   {
   public:
      AcmeRenewalTask();
      ~AcmeRenewalTask();

      virtual void DoWork();
   };
}
