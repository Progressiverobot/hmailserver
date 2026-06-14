// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "stdafx.h"
#include "ScramSha256.h"

#include "../Parsing/StringParser.h"

#include <openssl/rand.h>
#include <openssl/hmac.h>
#include <openssl/sha.h>
#include <openssl/crypto.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ScramSha256::ScramSha256() :
      state_(NeedClientFirst),
      real_account_(false)
   {
   }

   AnsiString
   ScramSha256::Base64Encode_(const unsigned char *data, size_t length)
   {
      static const char tbl[] =
         "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

      AnsiString out;
      size_t i = 0;
      while (i + 3 <= length)
      {
         unsigned int n = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
         char chunk[4];
         chunk[0] = tbl[(n >> 18) & 0x3F];
         chunk[1] = tbl[(n >> 12) & 0x3F];
         chunk[2] = tbl[(n >> 6) & 0x3F];
         chunk[3] = tbl[n & 0x3F];
         out.append(chunk, 4);
         i += 3;
      }

      size_t remaining = length - i;
      if (remaining == 1)
      {
         unsigned int n = data[i] << 16;
         char chunk[4];
         chunk[0] = tbl[(n >> 18) & 0x3F];
         chunk[1] = tbl[(n >> 12) & 0x3F];
         chunk[2] = '=';
         chunk[3] = '=';
         out.append(chunk, 4);
      }
      else if (remaining == 2)
      {
         unsigned int n = (data[i] << 16) | (data[i + 1] << 8);
         char chunk[4];
         chunk[0] = tbl[(n >> 18) & 0x3F];
         chunk[1] = tbl[(n >> 12) & 0x3F];
         chunk[2] = tbl[(n >> 6) & 0x3F];
         chunk[3] = '=';
         out.append(chunk, 4);
      }

      return out;
   }

   bool
   ScramSha256::Base64Decode_(const AnsiString &input, std::vector<unsigned char> &out)
   {
      int reverse[256];
      for (int i = 0; i < 256; i++)
         reverse[i] = -1;
      const char *tbl =
         "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
      for (int i = 0; i < 64; i++)
         reverse[(unsigned char) tbl[i]] = i;

      out.clear();

      int accumulated = 0;
      int bits = 0;
      for (int i = 0; i < input.GetLength(); i++)
      {
         unsigned char c = (unsigned char) input.GetAt(i);
         if (c == '=')
            break;
         int value = reverse[c];
         if (value < 0)
            return false;   // not a valid base64 character

         accumulated = (accumulated << 6) | value;
         bits += 6;
         if (bits >= 8)
         {
            bits -= 8;
            out.push_back((unsigned char) ((accumulated >> bits) & 0xFF));
         }
      }

      return true;
   }

   void
   ScramSha256::HmacSha256_(const unsigned char *key, size_t keyLen,
                            const unsigned char *data, size_t dataLen,
                            unsigned char out[32])
   {
      unsigned int len = 32;
      HMAC(EVP_sha256(), key, (int) keyLen, data, dataLen, out, &len);
   }

   void
   ScramSha256::Sha256_(const unsigned char *data, size_t dataLen, unsigned char out[32])
   {
      SHA256(data, dataLen, out);
   }

   AnsiString
   ScramSha256::GenerateNonce_()
   {
      // 18 random bytes encode to exactly 24 base64 characters with no padding, so
      // the nonce never contains the '=' or ',' that SCRAM forbids in a nonce.
      unsigned char raw[18];
      if (RAND_bytes(raw, sizeof(raw)) != 1)
      {
         // Extremely unlikely; fall back to a fixed-length non-secret value rather
         // than an empty nonce (the exchange will simply fail to authenticate).
         memset(raw, 0, sizeof(raw));
      }
      return Base64Encode_(raw, sizeof(raw));
   }

   AnsiString
   ScramSha256::GetAttribute_(const AnsiString &message, char name)
   {
      AnsiString prefix;
      prefix.Format("%c=", name);

      std::vector<AnsiString> parts = StringParser::SplitString(message, ",");
      for (auto &part : parts)
      {
         if (part.StartsWith(prefix.c_str()))
            return part.Mid(2);
      }
      return "";
   }

   bool
   ScramSha256::ParseStoredPbkdf2_(const AnsiString &stored, int &iterations,
                                   std::vector<unsigned char> &salt,
                                   std::vector<unsigned char> &saltedPassword)
   {
      // Format: $h1$<iterations>$<salt-hex>$<derived-key-hex>
      if (!stored.StartsWith("$h1$"))
         return false;

      std::vector<AnsiString> parts = StringParser::SplitString(stored, "$");
      // yields: "", "h1", iter, salt, key
      if (parts.size() != 5)
         return false;

      iterations = atoi(parts[2].c_str());
      if (iterations <= 0)
         return false;

      AnsiString saltHex = parts[3];
      AnsiString keyHex = parts[4];
      if ((saltHex.GetLength() % 2) != 0 || (keyHex.GetLength() % 2) != 0)
         return false;

      auto hexToBytes = [](const AnsiString &hex, std::vector<unsigned char> &bytes) -> bool
      {
         bytes.clear();
         for (int i = 0; i + 1 < hex.GetLength(); i += 2)
         {
            unsigned int value = 0;
            AnsiString byteString = hex.Mid(i, 2);
            if (sscanf_s(byteString.c_str(), "%02x", &value) != 1)
               return false;
            bytes.push_back((unsigned char) value);
         }
         return true;
      };

      if (!hexToBytes(saltHex, salt))
         return false;
      if (!hexToBytes(keyHex, saltedPassword))
         return false;

      // SCRAM-SHA-256 SaltedPassword is the 32-byte PBKDF2-HMAC-SHA256 output.
      if (saltedPassword.size() != 32)
         return false;

      return true;
   }

   bool
   ScramSha256::ExtractUsername(const AnsiString &clientFirst, AnsiString &usernameOut)
   {
      int firstComma = clientFirst.Find(",");
      if (firstComma < 0)
         return false;

      AnsiString afterFirst = clientFirst.Mid(firstComma + 1);
      int secondRel = afterFirst.Find(",");
      if (secondRel < 0)
         return false;

      AnsiString bare = afterFirst.Mid(secondRel + 1);
      AnsiString user = GetAttribute_(bare, 'n');
      if (user.IsEmpty())
         return false;

      // Un-escape the SCRAM saslname encoding (=2C => ',', =3D => '='). The comma
      // escape must be undone first so a literal "=2C" in the name is preserved.
      user.Replace("=2C", ",");
      user.Replace("=2c", ",");
      user.Replace("=3D", "=");
      user.Replace("=3d", "=");

      usernameOut = user;
      return true;
   }

   bool
   ScramSha256::ProcessClientFirst(const AnsiString &clientFirst, const AnsiString &storedPbkdf2Hash, AnsiString &serverFirstOut)
   {
      // Split off the gs2 header: gs2-cbind-flag "," [ authzid ] ","
      int firstComma = clientFirst.Find(",");
      if (firstComma < 0)
         return false;

      AnsiString afterFirst = clientFirst.Mid(firstComma + 1);
      int secondRel = afterFirst.Find(",");
      if (secondRel < 0)
         return false;

      int secondComma = firstComma + 1 + secondRel;
      gs2_header_ = clientFirst.Mid(0, secondComma + 1);
      client_first_bare_ = clientFirst.Mid(secondComma + 1);

      // Channel binding: this is the non-PLUS mechanism, so the client must not
      // require binding ('p'). 'n' (no binding) and 'y' (client thinks the server
      // does not support binding) are both acceptable.
      char flag = clientFirst.GetLength() > 0 ? (char) clientFirst.GetAt(0) : '\0';
      if (flag != 'n' && flag != 'y')
         return false;

      // Authorization identity (impersonation) is not supported here.
      AnsiString authzidPart = clientFirst.Mid(firstComma + 1, secondComma - (firstComma + 1));
      if (!authzidPart.IsEmpty())
         return false;

      AnsiString clientNonce = GetAttribute_(client_first_bare_, 'r');
      if (clientNonce.IsEmpty())
         return false;

      AnsiString serverNonce = GenerateNonce_();
      combined_nonce_ = clientNonce + serverNonce;

      int iterations = 0;
      std::vector<unsigned char> salt;
      if (ParseStoredPbkdf2_(storedPbkdf2Hash, iterations, salt, salted_password_))
      {
         real_account_ = true;
      }
      else
      {
         // Unknown account or a non-PBKDF2 hash: fabricate plausible parameters so the
         // exchange completes normally and fails only at proof verification. This
         // avoids revealing through the protocol whether the account exists.
         real_account_ = false;
         iterations = 210000;
         salt.resize(16);
         RAND_bytes(salt.data(), (int) salt.size());
         salted_password_.resize(32);
         RAND_bytes(salted_password_.data(), (int) salted_password_.size());
      }

      AnsiString saltB64 = Base64Encode_(salt.data(), salt.size());
      AnsiString iterStr;
      iterStr.Format("%d", iterations);

      server_first_ = "r=";
      server_first_ += combined_nonce_;
      server_first_ += ",s=";
      server_first_ += saltB64;
      server_first_ += ",i=";
      server_first_ += iterStr;

      serverFirstOut = server_first_;
      state_ = NeedClientFinal;
      return true;
   }

   bool
   ScramSha256::ProcessClientFinal(const AnsiString &clientFinal, AnsiString &serverFinalOut)
   {
      AnsiString channelBinding = GetAttribute_(clientFinal, 'c');
      AnsiString clientFinalNonce = GetAttribute_(clientFinal, 'r');
      AnsiString proofB64 = GetAttribute_(clientFinal, 'p');

      if (proofB64.IsEmpty())
      {
         state_ = Failed;
         return false;
      }

      // The channel-binding data must be base64(gs2-header) from the first message.
      AnsiString expectedCbind = Base64Encode_((const unsigned char *) gs2_header_.c_str(), gs2_header_.GetLength());
      if (channelBinding != expectedCbind)
      {
         state_ = Failed;
         return false;
      }

      if (clientFinalNonce != combined_nonce_)
      {
         state_ = Failed;
         return false;
      }

      int proofPos = clientFinal.Find(",p=");
      if (proofPos < 0)
      {
         state_ = Failed;
         return false;
      }
      AnsiString clientFinalWithoutProof = clientFinal.Mid(0, proofPos);

      AnsiString authMessage = client_first_bare_;
      authMessage += ",";
      authMessage += server_first_;
      authMessage += ",";
      authMessage += clientFinalWithoutProof;

      unsigned char clientKey[32];
      HmacSha256_(salted_password_.data(), salted_password_.size(),
                  (const unsigned char *) "Client Key", 10, clientKey);

      unsigned char storedKey[32];
      Sha256_(clientKey, sizeof(clientKey), storedKey);

      unsigned char clientSignature[32];
      HmacSha256_(storedKey, sizeof(storedKey),
                  (const unsigned char *) authMessage.c_str(), authMessage.GetLength(), clientSignature);

      std::vector<unsigned char> proof;
      if (!Base64Decode_(proofB64, proof) || proof.size() != sizeof(clientSignature))
      {
         state_ = Failed;
         return false;
      }

      unsigned char recoveredClientKey[32];
      for (size_t i = 0; i < sizeof(recoveredClientKey); i++)
         recoveredClientKey[i] = (unsigned char) (proof[i] ^ clientSignature[i]);

      unsigned char recoveredStoredKey[32];
      Sha256_(recoveredClientKey, sizeof(recoveredClientKey), recoveredStoredKey);

      bool match = real_account_ &&
                   CRYPTO_memcmp(recoveredStoredKey, storedKey, sizeof(storedKey)) == 0;

      OPENSSL_cleanse(clientKey, sizeof(clientKey));
      OPENSSL_cleanse(recoveredClientKey, sizeof(recoveredClientKey));

      if (!match)
      {
         state_ = Failed;
         return false;
      }

      unsigned char serverKey[32];
      HmacSha256_(salted_password_.data(), salted_password_.size(),
                  (const unsigned char *) "Server Key", 10, serverKey);

      unsigned char serverSignature[32];
      HmacSha256_(serverKey, sizeof(serverKey),
                  (const unsigned char *) authMessage.c_str(), authMessage.GetLength(), serverSignature);

      serverFinalOut = "v=";
      serverFinalOut += Base64Encode_(serverSignature, sizeof(serverSignature));

      OPENSSL_cleanse(serverKey, sizeof(serverKey));

      state_ = NeedAck;
      return true;
   }
}
