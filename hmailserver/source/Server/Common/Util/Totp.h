// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
// RFC 6238 time-based one-time passwords, in the SERVER.
//
// TOTP already existed in this product and was invisible from here: the Control
// Panel implements it in C# and checks the code itself, after the server has
// already accepted the administrator's password. That is a client-side check of
// a client-side secret, which is fine for what it does - locking the admin tool -
// and is why "TOTP is supported" has never meant a mailbox account could have a
// second factor. The server had no idea the feature existed.
//
// Deliberately the same parameters as the Control Panel's implementation
// (HMAC-SHA1, 30-second step, 6 digits, one step of tolerance either side), so a
// secret enrolled in one is verifiable by the other and an authenticator app
// behaves identically against both. RFC 6238 permits SHA-256 and SHA-512, and
// essentially no authenticator app implements them; using one here would produce
// a QR code that scans and then never matches.
//
// The +/-1 step tolerance is the interoperability floor rather than a
// preference: without it, a client whose clock is a second out fails at the
// boundary of every window, and the failure is intermittent in the way most
// likely to be blamed on the server. It costs an attacker nothing meaningful -
// three codes out of a million rather than one.
//
// Nothing here reads configuration or touches a database: the secret and the
// time both arrive as parameters. That is what lets the whole of it be pinned by
// tests against RFC 6238's published vectors, which is the only way to be sure a
// TOTP implementation is right - "it works with my phone" tests the phone.

#pragma once

namespace HM
{
   class Totp
   {
   public:
      // Verifies a 6-digit code against a base32 secret at the given Unix time,
      // accepting the adjacent steps either side. False for an empty secret, an
      // empty or malformed code, or a secret that is not valid base32 - never an
      // exception, because this sits on an authentication path.
      static bool VerifyCode(const AnsiString &base32Secret, const AnsiString &code, time_t now);

      // As above, at the current time.
      static bool VerifyCode(const AnsiString &base32Secret, const AnsiString &code);

      // The code for one 30-second counter value. Public so the RFC 6238 test
      // vectors can be pinned directly rather than inferred through VerifyCode.
      static AnsiString ComputeCode(const AnsiString &base32Secret, __int64 counter);

      // A fresh 160-bit secret, base32 encoded - the size RFC 4226 recommends and
      // what every authenticator app expects. Empty if the CSPRNG fails, which the
      // caller must treat as a failure to enrol rather than as an empty secret,
      // since an empty secret means "no second factor" everywhere else here.
      static AnsiString GenerateSecret();

      // The otpauth:// URI an authenticator app scans. The label is the account
      // address; the issuer is fixed, so all of this server's accounts group
      // together in the app rather than appearing as unrelated entries.
      static AnsiString BuildOtpAuthUri(const AnsiString &accountAddress, const AnsiString &base32Secret);

      // True when the string is syntactically usable as a secret. Used when a
      // secret arrives from outside rather than from GenerateSecret.
      static bool IsValidSecret(const AnsiString &base32Secret);

   private:
      static bool Base32Decode_(const AnsiString &input, std::vector<unsigned char> &output);
      static AnsiString Base32Encode_(const std::vector<unsigned char> &input);
   };

   // Pins RFC 6238's published test vectors. This is the only way to know a TOTP
   // implementation is correct rather than merely self-consistent: an
   // implementation that agrees with itself will happily agree with itself about
   // the wrong answer, and the symptom - "my authenticator app's codes are never
   // accepted" - looks like a clock problem, a QR problem or a typing problem long
   // before anyone suspects the truncation.
   class TotpTester
   {
   public:
      void Test();
   };
}
