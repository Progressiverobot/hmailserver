// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include "Totp.h"

#include <openssl/hmac.h>
#include <openssl/rand.h>

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int PeriodSeconds = 30;
      const int Digits = 6;
      const char *Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
      const int SecretBytes = 20;   // 160 bits, per RFC 4226 section 4
   }

   bool
   Totp::Base32Decode_(const AnsiString &input, std::vector<unsigned char> &output)
   {
      output.clear();

      int buffer = 0;
      int bitsLeft = 0;

      for (int i = 0; i < input.GetLength(); i++)
      {
         char c = input.GetAt(i);

         // Padding and separators are tolerated: authenticator apps and the people
         // who retype secrets both add them, and a secret that differs only by a
         // space is not a different secret.
         if (c == '=' || c == ' ' || c == '-')
            continue;

         if (c >= 'a' && c <= 'z')
            c = (char) (c - 'a' + 'A');

         const char *found = strchr(Base32Alphabet, c);

         if (found == nullptr || c == 0)
            return false;

         buffer = (buffer << 5) | (int) (found - Base32Alphabet);
         bitsLeft += 5;

         if (bitsLeft >= 8)
         {
            bitsLeft -= 8;
            output.push_back((unsigned char) ((buffer >> bitsLeft) & 0xFF));
         }
      }

      return !output.empty();
   }

   AnsiString
   Totp::Base32Encode_(const std::vector<unsigned char> &input)
   {
      AnsiString result;

      int buffer = 0;
      int bitsLeft = 0;

      for (size_t i = 0; i < input.size(); i++)
      {
         buffer = (buffer << 8) | input[i];
         bitsLeft += 8;

         while (bitsLeft >= 5)
         {
            bitsLeft -= 5;
            char c = Base32Alphabet[(buffer >> bitsLeft) & 0x1F];
            result.append(&c, 1);
         }
      }

      if (bitsLeft > 0)
      {
         char c = Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F];
         result.append(&c, 1);
      }

      return result;
   }

   bool
   Totp::IsValidSecret(const AnsiString &base32Secret)
   {
      std::vector<unsigned char> key;
      return Base32Decode_(base32Secret, key);
   }

   AnsiString
   Totp::ComputeCode(const AnsiString &base32Secret, __int64 counter)
   {
      std::vector<unsigned char> key;

      if (!Base32Decode_(base32Secret, key))
         return "";

      // RFC 4226 section 5.1: the counter is eight bytes, big-endian, regardless of
      // the host's byte order.
      unsigned char counterBytes[8];
      for (int i = 7; i >= 0; i--)
      {
         counterBytes[i] = (unsigned char) (counter & 0xFF);
         counter >>= 8;
      }

      unsigned char digest[EVP_MAX_MD_SIZE];
      unsigned int digestLength = 0;

      if (HMAC(EVP_sha1(), &key[0], (int) key.size(), counterBytes, sizeof(counterBytes),
               digest, &digestLength) == nullptr || digestLength < 20)
      {
         return "";
      }

      // RFC 4226 section 5.3, dynamic truncation. The masks are not decoration: the
      // high bit of the four selected bytes is cleared so the result is the same on
      // a machine that treats the value as signed.
      int offset = digest[digestLength - 1] & 0x0F;

      int binary =
         ((digest[offset] & 0x7F) << 24) |
         ((digest[offset + 1] & 0xFF) << 16) |
         ((digest[offset + 2] & 0xFF) << 8) |
         (digest[offset + 3] & 0xFF);

      int modulus = 1;
      for (int i = 0; i < Digits; i++)
         modulus *= 10;

      AnsiString result;
      result.Format("%0*d", Digits, binary % modulus);

      return result;
   }

   bool
   Totp::VerifyCode(const AnsiString &base32Secret, const AnsiString &code, time_t now)
   {
      if (base32Secret.IsEmpty() || code.IsEmpty())
         return false;

      AnsiString trimmed = code;
      trimmed.Trim();

      if (trimmed.GetLength() != Digits)
         return false;

      for (int i = 0; i < trimmed.GetLength(); i++)
      {
         if (!isdigit((unsigned char) trimmed.GetAt(i)))
            return false;
      }

      __int64 counter = (__int64) now / PeriodSeconds;

      // Compared over the whole accepted window rather than returning on the first
      // match, so the time taken does not reveal WHICH step matched. That leaks
      // nothing about the secret, but it does leak the client's clock offset, and a
      // comparison on an authentication path is the wrong place to be casual.
      bool matched = false;

      for (__int64 offset = -1; offset <= 1; offset++)
      {
         AnsiString expected = ComputeCode(base32Secret, counter + offset);

         if (expected.IsEmpty())
            return false;

         // Fixed-time comparison of two equal-length digit strings.
         unsigned char difference = 0;
         for (int i = 0; i < Digits; i++)
            difference |= (unsigned char) (expected.GetAt(i) ^ trimmed.GetAt(i));

         if (difference == 0)
            matched = true;
      }

      return matched;
   }

   bool
   Totp::VerifyCode(const AnsiString &base32Secret, const AnsiString &code)
   {
      return VerifyCode(base32Secret, code, time(nullptr));
   }

   AnsiString
   Totp::GenerateSecret()
   {
      std::vector<unsigned char> buffer(SecretBytes);

      if (RAND_bytes(&buffer[0], SecretBytes) != 1)
      {
         // Empty means "no second factor" everywhere else here, so the caller has to
         // treat this as a failure to enrol rather than storing it.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6199, "Totp::GenerateSecret",
            "The random number generator failed, so no TOTP secret was generated. Nothing has been stored.");

         return "";
      }

      return Base32Encode_(buffer);
   }

   AnsiString
   Totp::BuildOtpAuthUri(const AnsiString &accountAddress, const AnsiString &base32Secret)
   {
      AnsiString uri;

      // The label is percent-encoded because an address may contain characters the
      // URI grammar treats as delimiters, and an authenticator app given a broken
      // URI reports nothing more useful than "invalid QR code".
      AnsiString encodedLabel;

      for (int i = 0; i < accountAddress.GetLength(); i++)
      {
         char c = accountAddress.GetAt(i);

         if (isalnum((unsigned char) c) || c == '-' || c == '.' || c == '_' || c == '~')
         {
            encodedLabel.append(&c, 1);
         }
         else
         {
            AnsiString escaped;
            escaped.Format("%%%02X", (unsigned char) c);
            encodedLabel += escaped;
         }
      }

      uri.Format("otpauth://totp/%s?secret=%s&issuer=hMailServer&digits=%d&period=%d",
                 encodedLabel.c_str(), base32Secret.c_str(), Digits, PeriodSeconds);

      return uri;
   }

   void
   TotpTester::Test()
   {
      // RFC 6238 Appendix B, the SHA-1 rows. The published secret is the ASCII
      // string "12345678901234567890"; base32 that is the value below, which is
      // what an authenticator app would be given.
      //
      // The RFC prints eight-digit codes and this server issues six, which is not a
      // discrepancy: RFC 4226 section 5.3 truncates with "modulo 10^Digits", so the
      // six-digit code is the last six digits of the eight-digit one. Getting that
      // relationship wrong is the classic TOTP bug and it is invisible without
      // vectors.
      const AnsiString secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

      struct Vector { __int64 unixTime; const char *code; };

      const Vector vectors[] =
      {
         {          59LL, "287082" },   // 94287082
         {  1111111109LL, "081804" },   // 07081804
         {  1111111111LL, "050471" },   // 14050471
         {  1234567890LL, "005924" },   // 89005924
         {  2000000000LL, "279037" },   // 69279037
         { 20000000000LL, "353130" },   // 65353130 - past 2038, so this also pins
                                        // that the counter is 64-bit throughout
      };

      for (size_t i = 0; i < sizeof(vectors) / sizeof(vectors[0]); i++)
      {
         AnsiString actual = Totp::ComputeCode(secret, vectors[i].unixTime / 30);

         if (actual.Compare(vectors[i].code) != 0)
            throw 0;

         // ...and the verifier accepts it at that time, which is the thing callers
         // actually use.
         if (!Totp::VerifyCode(secret, vectors[i].code, (time_t) vectors[i].unixTime))
            throw 0;
      }

      // One step either side is accepted, and two is not. Without the tolerance a
      // client whose clock is a second out fails at the boundary of every window;
      // with too much of it, a code stays valid long after it has been shoulder-surfed.
      if (!Totp::VerifyCode(secret, "050471", (time_t) (1111111111LL + 30)))
         throw 0;

      if (!Totp::VerifyCode(secret, "050471", (time_t) (1111111111LL - 30)))
         throw 0;

      if (Totp::VerifyCode(secret, "050471", (time_t) (1111111111LL + 90)))
         throw 0;

      // Malformed input is refused rather than throwing, because this runs on an
      // authentication path.
      if (Totp::VerifyCode(secret, "", (time_t) 1111111111LL)) throw 0;
      if (Totp::VerifyCode("", "050471", (time_t) 1111111111LL)) throw 0;
      if (Totp::VerifyCode(secret, "05047", (time_t) 1111111111LL)) throw 0;      // too short
      if (Totp::VerifyCode(secret, "0504711", (time_t) 1111111111LL)) throw 0;    // too long
      if (Totp::VerifyCode(secret, "05047a", (time_t) 1111111111LL)) throw 0;     // not digits
      if (Totp::VerifyCode("not!base32", "050471", (time_t) 1111111111LL)) throw 0;

      // A generated secret is usable and round-trips through the decoder.
      AnsiString generated = Totp::GenerateSecret();

      if (generated.GetLength() != 32 || !Totp::IsValidSecret(generated))
         throw 0;

      if (Totp::GenerateSecret().Compare(generated) == 0)
         throw 0;   // two secrets in a row must differ

      // The otpauth URI must percent-encode the address, or an app given an
      // address containing '@' reports nothing more useful than "invalid QR code".
      AnsiString uri = Totp::BuildOtpAuthUri("user@example.com", secret);

      if (uri.Find("user%40example.com") < 0 || uri.Find(secret) < 0)
         throw 0;
   }
}
