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

      // Returns true if the certificate in the configured directory is
      // missing or has reached its renewal time.
      static bool RenewalNeeded();

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

      // The directory holding account.key, fullchain.pem and privkey.pem.
      static String GetCertificateDirectory();

      // Computes the DANE TLSA "3 1 1" payload (SHA-256 over the subject
      // public key info) for the first certificate in a PEM file.
      static bool GetCertificateTlsa(const String &certificate_file, AnsiString &spki_sha256_hex);

      // Runs the full issuance flow. Returns true on success.
      bool RequestCertificate();

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
