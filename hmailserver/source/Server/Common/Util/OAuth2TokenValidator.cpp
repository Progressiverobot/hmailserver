// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "OAuth2TokenValidator.h"

#include "Unicode.h"
#include "Hashing/HashCreator.h"
#include "../Mime/MimeCode.h"
#include "../Application/IniFileSettings.h"

#include <ctime>
#include <sstream>
#include <vector>

#include <boost/property_tree/ptree.hpp>
#include <boost/property_tree/json_parser.hpp>

#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/bio.h>
#include <openssl/crypto.h>
#include <openssl/hmac.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Allow a small clock skew when checking time-based claims.
      const long long OAUTH2_CLOCK_SKEW_SECONDS = 60;

      AnsiString
      BytesToHexLocal(const unsigned char *data, int length)
      {
         AnsiString result;
         char buffer[3];
         buffer[2] = '\0';
         for (int i = 0; i < length; i++)
         {
            sprintf_s(buffer, 3, "%02x", data[i]);
            result += buffer;
         }
         return result;
      }

      // Split a byte string on a single delimiter, preserving empty fields.
      std::vector<AnsiString>
      SplitBytes(const AnsiString &input, char delimiter)
      {
         std::vector<AnsiString> parts;
         const char *buffer = input;
         int length = input.GetLength();
         int start = 0;
         for (int i = 0; i <= length; i++)
         {
            if (i == length || buffer[i] == delimiter)
            {
               AnsiString part;
               if (i > start)
                  part.append(buffer + start, i - start);
               parts.push_back(part);
               start = i + 1;
            }
         }
         return parts;
      }

      bool
      ParseJson(const AnsiString &json, boost::property_tree::ptree &pt)
      {
         try
         {
            std::stringstream ss;
            ss.write(json.c_str(), json.GetLength());
            boost::property_tree::read_json(ss, pt);
            return true;
         }
         catch (...)
         {
            return false;
         }
      }

      bool
      AudienceMatches(const boost::property_tree::ptree &payload, const AnsiString &expected)
      {
         boost::optional<const boost::property_tree::ptree &> audNode = payload.get_child_optional("aud");
         if (!audNode)
            return false;

         // Scalar "aud".
         std::string scalar = audNode->get_value<std::string>("");
         if (!scalar.empty())
            return AnsiString(scalar.c_str()) == expected;

         // Array "aud".
         for (boost::property_tree::ptree::const_iterator it = audNode->begin(); it != audNode->end(); ++it)
         {
            std::string item = it->second.get_value<std::string>("");
            if (AnsiString(item.c_str()) == expected)
               return true;
         }
         return false;
      }
   }

   bool
   OAuth2TokenValidator::IsEnabled()
   {
      return IniFileSettings::Instance()->GetOAuth2Enabled();
   }

   bool
   OAuth2TokenValidator::RequireTLS()
   {
      return IniFileSettings::Instance()->GetOAuth2RequireTLS();
   }

   OAuth2Config
   OAuth2TokenValidator::LoadConfig()
   {
      IniFileSettings *ini = IniFileSettings::Instance();

      OAuth2Config config;
      config.enabled = ini->GetOAuth2Enabled();
      config.require_tls = ini->GetOAuth2RequireTLS();
      config.allowed_algorithms = ini->GetOAuth2AllowedAlgorithms();

      // The HMAC secret may be UTF-8; convert from the wide INI value preserving bytes.
      AnsiString secretBytes;
      Unicode::WideToMultiByte(ini->GetOAuth2HmacSecret(), secretBytes);
      config.hmac_secret = secretBytes;

      config.rsa_public_key_file = ini->GetOAuth2RsaPublicKeyFile();
      config.issuer = ini->GetOAuth2Issuer();
      config.audience = ini->GetOAuth2Audience();
      config.username_claim = ini->GetOAuth2UsernameClaim();
      if (config.username_claim.IsEmpty())
         config.username_claim = "email";

      return config;
   }

   bool
   OAuth2TokenValidator::ValidateBearerToken(const AnsiString &sToken, String &out_username, AnsiString &out_error)
   {
      OAuth2Config config = LoadConfig();
      if (!config.enabled)
      {
         out_error = "OAuth2 authentication is disabled.";
         return false;
      }
      return ValidateWithConfig(config, sToken, out_username, out_error);
   }

   bool
   OAuth2TokenValidator::ValidateWithConfig(const OAuth2Config &config, const AnsiString &sToken, String &out_username, AnsiString &out_error)
   {
      out_username.Empty();

      // A JWT is exactly three '.'-separated base64url segments.
      std::vector<AnsiString> segments = SplitBytes(sToken, '.');
      if (segments.size() != 3 || segments[0].IsEmpty() || segments[1].IsEmpty() || segments[2].IsEmpty())
      {
         out_error = "Malformed JWT (expected three segments).";
         return false;
      }

      AnsiString headerJson, payloadJson, signature;
      if (!Base64UrlDecode_(segments[0], headerJson) ||
          !Base64UrlDecode_(segments[1], payloadJson) ||
          !Base64UrlDecode_(segments[2], signature))
      {
         out_error = "JWT segment is not valid base64url.";
         return false;
      }

      boost::property_tree::ptree header;
      if (!ParseJson(headerJson, header))
      {
         out_error = "JWT header is not valid JSON.";
         return false;
      }

      AnsiString alg = header.get<std::string>("alg", "").c_str();
      AnsiString algUpper = alg;
      algUpper.MakeUpper();

      // Reject the "alg:none" unsecured-JWS class and any empty algorithm outright,
      // independent of the allow-list, to foreclose algorithm-confusion attacks.
      if (alg.IsEmpty() || algUpper == "NONE")
      {
         out_error = "JWT uses an unsecured or missing algorithm.";
         return false;
      }

      if (!IsAlgorithmAllowed_(config.allowed_algorithms, algUpper))
      {
         out_error = "JWT algorithm is not in the configured allow-list.";
         return false;
      }

      // Signature is computed over the ASCII octets of "header.payload".
      AnsiString signingInput = segments[0] + "." + segments[1];

      bool signatureValid = false;
      if (algUpper == "HS256")
         signatureValid = VerifyHs256_(signingInput, signature, config.hmac_secret);
      else if (algUpper == "RS256")
         signatureValid = VerifyWithPublicKey_(signingInput, signature, config.rsa_public_key_file);
      else if (algUpper == "ES256")
      {
         // JWS carries an ECDSA signature as the raw R||S pair, while OpenSSL
         // expects X9.62 DER, so this never verified - it only ever produced a
         // misleading "signature verification failed". Reject it explicitly
         // until the transcode is implemented and tested, so the cause is clear.
         out_error = "JWT algorithm ES256 is not supported by this server; use RS256 or HS256.";
         return false;
      }
      else
      {
         out_error = "JWT algorithm is not supported.";
         return false;
      }

      if (!signatureValid)
      {
         out_error = "JWT signature verification failed.";
         return false;
      }

      boost::property_tree::ptree payload;
      if (!ParseJson(payloadJson, payload))
      {
         out_error = "JWT payload is not valid JSON.";
         return false;
      }

      long long now = (long long) time(nullptr);

      // "exp" is required and must be in the future (allowing for small clock skew).
      boost::optional<long long> exp = payload.get_optional<long long>("exp");
      if (!exp)
      {
         out_error = "JWT is missing the exp claim.";
         return false;
      }
      if (now > *exp + OAUTH2_CLOCK_SKEW_SECONDS)
      {
         out_error = "JWT has expired.";
         return false;
      }

      // "nbf" (not before), when present, must not be in the future.
      boost::optional<long long> nbf = payload.get_optional<long long>("nbf");
      if (nbf && now + OAUTH2_CLOCK_SKEW_SECONDS < *nbf)
      {
         out_error = "JWT is not yet valid (nbf).";
         return false;
      }

      // Issuer / audience checks are applied only when configured.
      if (!config.issuer.IsEmpty())
      {
         AnsiString iss = payload.get<std::string>("iss", "").c_str();
         if (iss != config.issuer)
         {
            out_error = "JWT issuer does not match the configured value.";
            return false;
         }
      }

      if (!config.audience.IsEmpty())
      {
         if (!AudienceMatches(payload, config.audience))
         {
            out_error = "JWT audience does not match the configured value.";
            return false;
         }
      }

      // Extract the login identity from the configured username claim.
      AnsiString claimValue = payload.get<std::string>(std::string(config.username_claim), "").c_str();
      if (claimValue.IsEmpty())
      {
         out_error = "JWT is missing the configured username claim.";
         return false;
      }

      String username;
      Unicode::MultiByteToWide(claimValue, username);
      out_username = username;
      return true;
   }

   bool
   OAuth2TokenValidator::ParseSaslBearer(const AnsiString &sDecoded, AnsiString &out_identity, AnsiString &out_token)
   {
      out_identity.Empty();
      out_token.Empty();

      // Both XOAUTH2 and OAUTHBEARER use 0x01 (^A) as the key/value separator.
      std::vector<AnsiString> fields = SplitBytes(sDecoded, '\x01');

      for (size_t i = 0; i < fields.size(); i++)
      {
         AnsiString field = fields[i];
         if (field.IsEmpty())
            continue;

         AnsiString lower = field;
         lower.MakeLower();

         if (lower.find("user=") == 0)
         {
            // XOAUTH2: "user=<identity>"
            out_identity = field.Mid(5);
         }
         else if (lower.find("auth=") == 0)
         {
            // "auth=Bearer <token>"
            AnsiString value = field.Mid(5);
            AnsiString valueLower = value;
            valueLower.MakeLower();
            if (valueLower.find("bearer ") == 0)
               out_token = value.Mid(7);
         }
         else if (i == 0 && (field.Left(2) == "n," || field.Left(2) == "y," || field.Left(2) == "p="))
         {
            // OAUTHBEARER gs2 header, e.g. "n,a=<identity>," — pull out the a= identity.
            AnsiString::size_type aPos = field.find("a=");
            if (aPos != AnsiString::npos)
            {
               AnsiString rest = field.Mid((int)aPos + 2);
               AnsiString::size_type comma = rest.find(",");
               if (comma != AnsiString::npos)
                  rest = rest.Left((int)comma);
               if (out_identity.IsEmpty())
                  out_identity = rest;
            }
         }
      }

      return !out_token.IsEmpty();
   }

   bool
   OAuth2TokenValidator::Base64UrlDecode_(const AnsiString &sInput, AnsiString &sOutput)
   {
      AnsiString s = sInput;
      s.Replace("-", "+");
      s.Replace("_", "/");
      while (s.GetLength() % 4 != 0)
         s += "=";

      MimeCodeBase64 decoder;
      decoder.AddLineBreak(false);
      decoder.SetInput(s, s.GetLength(), false);
      decoder.GetOutput(sOutput);
      return true;
   }

   bool
   OAuth2TokenValidator::IsAlgorithmAllowed_(const AnsiString &sAllowList, const AnsiString &sAlgUpper)
   {
      std::vector<AnsiString> allowed = SplitBytes(sAllowList, ',');
      for (size_t i = 0; i < allowed.size(); i++)
      {
         AnsiString entry = allowed[i];
         entry.Trim();
         entry.MakeUpper();
         if (entry == "NONE")
            continue; // never honour "none" even if mis-configured
         if (entry == sAlgUpper)
            return true;
      }
      return false;
   }

   bool
   OAuth2TokenValidator::VerifyHs256_(const AnsiString &sSigningInput, const AnsiString &sSignature, const AnsiString &sSecret)
   {
      if (sSecret.IsEmpty())
         return false;

      AnsiString computedHex = HashCreator::ComputeHMACSHA256Hex(sSecret, sSigningInput);
      if (computedHex.IsEmpty())
         return false;

      AnsiString signatureHex = BytesToHexLocal((const unsigned char *) (const char *) sSignature, sSignature.GetLength());
      return ConstantTimeEquals_(computedHex, signatureHex);
   }

   bool
   OAuth2TokenValidator::VerifyWithPublicKey_(const AnsiString &sSigningInput, const AnsiString &sSignature, const String &sKeyFile)
   {
      if (sKeyFile.IsEmpty())
         return false;

      AnsiString keyPath;
      Unicode::WideToMultiByte(sKeyFile, keyPath);

      BIO *bio = BIO_new_file(keyPath, "r");
      if (bio == nullptr)
         return false;

      EVP_PKEY *pkey = PEM_read_bio_PUBKEY(bio, nullptr, nullptr, nullptr);
      BIO_free(bio);
      if (pkey == nullptr)
         return false;

      bool result = false;
      EVP_MD_CTX *ctx = EVP_MD_CTX_new();
      if (ctx != nullptr)
      {
         if (EVP_DigestVerifyInit(ctx, nullptr, EVP_sha256(), nullptr, pkey) == 1 &&
             EVP_DigestVerifyUpdate(ctx, sSigningInput.c_str(), sSigningInput.GetLength()) == 1)
         {
            int rc = EVP_DigestVerifyFinal(ctx, (const unsigned char *) sSignature.c_str(), sSignature.GetLength());
            result = (rc == 1);
         }
         EVP_MD_CTX_free(ctx);
      }

      EVP_PKEY_free(pkey);
      return result;
   }

   bool
   OAuth2TokenValidator::ConstantTimeEquals_(const AnsiString &a, const AnsiString &b)
   {
      if (a.GetLength() != b.GetLength())
         return false;

      unsigned char diff = 0;
      const char *pa = a;
      const char *pb = b;
      int length = a.GetLength();
      for (int i = 0; i < length; i++)
         diff |= (unsigned char) (pa[i] ^ pb[i]);

      return diff == 0;
   }

   namespace
   {
      AnsiString
      Base64UrlEncodeLocal(const AnsiString &data)
      {
         MimeCodeBase64 coder;
         coder.SetInput(data, data.GetLength(), true);
         coder.AddLineBreak(false);
         AnsiString out;
         coder.GetOutput(out);
         out.Replace("+", "-");
         out.Replace("/", "_");
         out.Replace("=", "");
         return out;
      }

      AnsiString
      HmacSha256Base64UrlLocal(const AnsiString &key, const AnsiString &data)
      {
         unsigned char mac[EVP_MAX_MD_SIZE];
         unsigned int macLen = 0;
         HMAC(EVP_sha256(), key.c_str(), key.GetLength(),
              (const unsigned char *) data.c_str(), data.GetLength(), mac, &macLen);
         AnsiString raw;
         raw.append((char *) mac, macLen);
         return Base64UrlEncodeLocal(raw);
      }
   }

   void
   OAuth2TokenValidatorTester::Test()
   {
      // --- ParseSaslBearer: XOAUTH2 wire format ---
      {
         AnsiString xoauth2 = AnsiString("user=u@example.test");
         xoauth2 += '\x01';
         xoauth2 += "auth=Bearer abc.def.ghi";
         xoauth2 += '\x01';
         xoauth2 += '\x01';
         AnsiString id, token;
         if (!OAuth2TokenValidator::ParseSaslBearer(xoauth2, id, token)) throw 0;
         if (id != "u@example.test") throw 0;
         if (token != "abc.def.ghi") throw 0;
      }

      // --- ParseSaslBearer: OAUTHBEARER (RFC 7628) wire format ---
      {
         AnsiString bearer = AnsiString("n,a=u@example.test,");
         bearer += '\x01';
         bearer += "host=mail";
         bearer += '\x01';
         bearer += "auth=Bearer xyz.tok.sig";
         bearer += '\x01';
         bearer += '\x01';
         AnsiString id, token;
         if (!OAuth2TokenValidator::ParseSaslBearer(bearer, id, token)) throw 0;
         if (id != "u@example.test") throw 0;
         if (token != "xyz.tok.sig") throw 0;
      }

      // --- HS256 JWT end-to-end ---
      AnsiString secret = "test-secret-0123456789-abcdef";
      OAuth2Config config;
      config.enabled = true;
      config.allowed_algorithms = "HS256";
      config.hmac_secret = secret;
      config.username_claim = "email";

      AnsiString encHeader = Base64UrlEncodeLocal("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
      long long future = (long long) time(nullptr) + 3600;
      AnsiString payload;
      payload.Format("{\"email\":\"oauth@example.test\",\"exp\":%I64d}", future);
      AnsiString encPayload = Base64UrlEncodeLocal(payload);
      AnsiString signingInput = encHeader + "." + encPayload;
      AnsiString signature = HmacSha256Base64UrlLocal(secret, signingInput);
      AnsiString validToken = signingInput + "." + signature;

      String user;
      AnsiString error;
      if (!OAuth2TokenValidator::ValidateWithConfig(config, validToken, user, error)) throw 0;
      if (user != L"oauth@example.test") throw 0;

      // A tampered signature must fail.
      {
         AnsiString tampered = signingInput + "." + Base64UrlEncodeLocal("not-a-real-signature-block!!");
         String u; AnsiString e;
         if (OAuth2TokenValidator::ValidateWithConfig(config, tampered, u, e)) throw 0;
      }

      // "alg":"none" must be rejected even with a present signature segment.
      {
         AnsiString noneHeader = Base64UrlEncodeLocal("{\"alg\":\"none\",\"typ\":\"JWT\"}");
         AnsiString noneToken = noneHeader + "." + encPayload + "." + signature;
         String u; AnsiString e;
         if (OAuth2TokenValidator::ValidateWithConfig(config, noneToken, u, e)) throw 0;
      }

      // An algorithm outside the allow-list must be rejected.
      {
         OAuth2Config rsOnly = config;
         rsOnly.allowed_algorithms = "RS256";
         String u; AnsiString e;
         if (OAuth2TokenValidator::ValidateWithConfig(rsOnly, validToken, u, e)) throw 0;
      }

      // An expired token must be rejected.
      {
         long long past = (long long) time(nullptr) - 7200;
         AnsiString expiredPayload;
         expiredPayload.Format("{\"email\":\"oauth@example.test\",\"exp\":%I64d}", past);
         AnsiString ep = Base64UrlEncodeLocal(expiredPayload);
         AnsiString si = encHeader + "." + ep;
         AnsiString es = HmacSha256Base64UrlLocal(secret, si);
         AnsiString expiredToken = si + "." + es;
         String u; AnsiString e;
         if (OAuth2TokenValidator::ValidateWithConfig(config, expiredToken, u, e)) throw 0;
      }

      // The wrong secret must fail signature verification.
      {
         OAuth2Config wrongSecret = config;
         wrongSecret.hmac_secret = "a-different-secret-value";
         String u; AnsiString e;
         if (OAuth2TokenValidator::ValidateWithConfig(wrongSecret, validToken, u, e)) throw 0;
      }
   }
}
