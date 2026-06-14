// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include <stdafx.h>

#include "../PasswordGenerator.h"
#include "../../Mime/MimeCode.h"

#include <openssl/evp.h>
#include <openssl/rand.h>
#include <openssl/crypto.h>
#include <openssl/kdf.h>
#include <openssl/core_names.h>
#include <openssl/params.h>

#include "HashCreator.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      AnsiString
      BytesToHex(const unsigned char *data, int length)
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

      bool
      HexToBytes(const AnsiString &hex, unsigned char *out, int expectedLength)
      {
         if (hex.GetLength() != expectedLength * 2)
            return false;

         for (int i = 0; i < expectedLength; i++)
         {
            unsigned int value = 0;
            AnsiString byteString = hex.Mid(i * 2, 2);
            if (sscanf_s(byteString.c_str(), "%02x", &value) != 1)
               return false;
            out[i] = static_cast<unsigned char>(value);
         }
         return true;
      }

      // Derive an Argon2id tag using OpenSSL's EVP_KDF. A single lane/thread is used
      // so no provider-level thread configuration is required. Returns false if the
      // Argon2id KDF is unavailable or the derivation fails.
      bool
      DeriveArgon2id(const AnsiString &password, const unsigned char *salt, size_t saltLen,
                     uint32_t memoryKiB, uint32_t timeCost, uint32_t parallelism,
                     unsigned char *out, size_t outLen)
      {
         EVP_KDF *kdf = EVP_KDF_fetch(nullptr, "ARGON2ID", nullptr);
         if (kdf == nullptr)
            return false;

         EVP_KDF_CTX *kctx = EVP_KDF_CTX_new(kdf);
         EVP_KDF_free(kdf);
         if (kctx == nullptr)
            return false;

         uint32_t threads = 1;

         OSSL_PARAM params[7];
         OSSL_PARAM *p = params;
         *p++ = OSSL_PARAM_construct_octet_string(OSSL_KDF_PARAM_PASSWORD,
                  (void *) password.c_str(), (size_t) password.GetLength());
         *p++ = OSSL_PARAM_construct_octet_string(OSSL_KDF_PARAM_SALT,
                  (void *) salt, saltLen);
         *p++ = OSSL_PARAM_construct_uint32(OSSL_KDF_PARAM_ARGON2_LANES, &parallelism);
         *p++ = OSSL_PARAM_construct_uint32(OSSL_KDF_PARAM_THREADS, &threads);
         *p++ = OSSL_PARAM_construct_uint32(OSSL_KDF_PARAM_ARGON2_MEMCOST, &memoryKiB);
         *p++ = OSSL_PARAM_construct_uint32(OSSL_KDF_PARAM_ITER, &timeCost);
         *p = OSSL_PARAM_construct_end();

         int rc = EVP_KDF_derive(kctx, out, outLen, params);

         EVP_KDF_CTX_free(kctx);

         return rc == 1;
      }
   }

   HashCreator::HashCreator(HashCreator::HashType hashType) :
      hash_type_(hashType)
   {

   }

   AnsiString HashCreator::GenerateHash(const AnsiString &inputString, const AnsiString &salt)
   {
      AnsiString saltString = salt;
      if (saltString.GetLength() == 0 && hash_type_ == SHA256)
      {     
         AnsiString randomString = PasswordGenerator::Generate();
         saltString = GetHash_(randomString, hex);
         saltString = saltString.Mid(0, SALT_LENGTH);
      }

      AnsiString value = saltString + GetHash_(saltString + inputString, hex);
      return value;
   }

   AnsiString 
   HashCreator::GenerateHashNoSalt(const AnsiString &inputString, RequestedEncoding encoding)
   {
      return GetHash_(inputString, encoding);
   }

   AnsiString 
   HashCreator::GenerateHashNoSalt(unsigned char *input, int inputLength, RequestedEncoding encoding)
   {
      return GetHash_Raw(input, inputLength, encoding);
   }


   bool 
   HashCreator::ValidateHash(const AnsiString &password, const AnsiString &originalHash, bool useSalt)
   {
      AnsiString result;
      if (useSalt)
      {
         AnsiString salt = GetSalt_(originalHash);
         result = GenerateHash(password, salt);
      }
      else
      {
         result = GetHash_(password, hex);
      }

      if (result.GetLength() != originalHash.GetLength() || result.GetLength() == 0)
         return false;

      // Constant-time comparison to prevent timing side-channels.
      return CRYPTO_memcmp(result.c_str(), originalHash.c_str(), result.GetLength()) == 0;
   }

   AnsiString
   HashCreator::GeneratePBKDF2(const AnsiString &password)
   {
      unsigned char salt[PBKDF2_SALT_BYTES];
      if (RAND_bytes(salt, sizeof(salt)) != 1)
         return "";

      unsigned char key[PBKDF2_KEY_BYTES];
      if (PKCS5_PBKDF2_HMAC(password.c_str(), password.GetLength(),
                            salt, sizeof(salt),
                            PBKDF2_DEFAULT_ITERATIONS, EVP_sha256(),
                            sizeof(key), key) != 1)
         return "";

      AnsiString result;
      result.Format("$h1$%d$%hs$%hs", (int) PBKDF2_DEFAULT_ITERATIONS,
         BytesToHex(salt, sizeof(salt)).c_str(),
         BytesToHex(key, sizeof(key)).c_str());

      OPENSSL_cleanse(key, sizeof(key));
      return result;
   }

   bool
   HashCreator::IsPBKDF2Hash(const AnsiString &storedHash)
   {
      return storedHash.StartsWith("$h1$");
   }

   bool
   HashCreator::ValidatePBKDF2(const AnsiString &password, const AnsiString &storedHash)
   {
      if (!IsPBKDF2Hash(storedHash))
         return false;

      // Format: $h1$<iterations>$<salt-hex>$<key-hex>
      std::vector<AnsiString> parts = StringParser::SplitString(storedHash, "$");
      // SplitString on "$h1$..." yields: "", "h1", iter, salt, key
      if (parts.size() != 5)
         return false;

      int iterations = atoi(parts[2].c_str());
      if (iterations <= 0 || iterations > PBKDF2_MAX_ITERATIONS)
         return false;

      unsigned char salt[PBKDF2_SALT_BYTES];
      if (!HexToBytes(parts[3], salt, sizeof(salt)))
         return false;

      unsigned char expectedKey[PBKDF2_KEY_BYTES];
      if (!HexToBytes(parts[4], expectedKey, sizeof(expectedKey)))
         return false;

      unsigned char actualKey[PBKDF2_KEY_BYTES];
      if (PKCS5_PBKDF2_HMAC(password.c_str(), password.GetLength(),
                            salt, sizeof(salt),
                            iterations, EVP_sha256(),
                            sizeof(actualKey), actualKey) != 1)
         return false;

      bool match = CRYPTO_memcmp(actualKey, expectedKey, sizeof(actualKey)) == 0;

      OPENSSL_cleanse(actualKey, sizeof(actualKey));
      OPENSSL_cleanse(expectedKey, sizeof(expectedKey));

      return match;
   }

   AnsiString
   HashCreator::GenerateArgon2id(const AnsiString &password)
   {
      unsigned char salt[ARGON2_SALT_BYTES];
      if (RAND_bytes(salt, sizeof(salt)) != 1)
         return "";

      unsigned char key[ARGON2_KEY_BYTES];
      if (!DeriveArgon2id(password, salt, sizeof(salt),
                          (uint32_t) ARGON2_DEFAULT_MEMORY_KIB,
                          (uint32_t) ARGON2_DEFAULT_TIME_COST,
                          (uint32_t) ARGON2_DEFAULT_PARALLELISM,
                          key, sizeof(key)))
         return "";

      AnsiString result;
      result.Format("$a2$%d$%d$%d$%hs$%hs",
         (int) ARGON2_DEFAULT_MEMORY_KIB, (int) ARGON2_DEFAULT_TIME_COST, (int) ARGON2_DEFAULT_PARALLELISM,
         BytesToHex(salt, sizeof(salt)).c_str(),
         BytesToHex(key, sizeof(key)).c_str());

      OPENSSL_cleanse(key, sizeof(key));
      return result;
   }

   bool
   HashCreator::IsArgon2idHash(const AnsiString &storedHash)
   {
      return storedHash.StartsWith("$a2$");
   }

   bool
   HashCreator::ValidateArgon2id(const AnsiString &password, const AnsiString &storedHash)
   {
      if (!IsArgon2idHash(storedHash))
         return false;

      // Format: $a2$<memory-KiB>$<time-cost>$<parallelism>$<salt-hex>$<key-hex>
      std::vector<AnsiString> parts = StringParser::SplitString(storedHash, "$");
      // SplitString on "$a2$..." yields: "", "a2", memory, time, parallelism, salt, key
      if (parts.size() != 7)
         return false;

      int memoryKiB = atoi(parts[2].c_str());
      int timeCost = atoi(parts[3].c_str());
      int parallelism = atoi(parts[4].c_str());

      // Defense-in-depth bounds so a malformed stored hash cannot request an
      // unreasonable amount of memory or time during verification.
      if (memoryKiB <= 0 || memoryKiB > ARGON2_MAX_MEMORY_KIB)
         return false;
      if (timeCost <= 0 || timeCost > ARGON2_MAX_TIME_COST)
         return false;
      if (parallelism <= 0 || parallelism > ARGON2_MAX_PARALLELISM)
         return false;

      unsigned char salt[ARGON2_SALT_BYTES];
      if (!HexToBytes(parts[5], salt, sizeof(salt)))
         return false;

      unsigned char expectedKey[ARGON2_KEY_BYTES];
      if (!HexToBytes(parts[6], expectedKey, sizeof(expectedKey)))
         return false;

      unsigned char actualKey[ARGON2_KEY_BYTES];
      if (!DeriveArgon2id(password, salt, sizeof(salt),
                          (uint32_t) memoryKiB, (uint32_t) timeCost, (uint32_t) parallelism,
                          actualKey, sizeof(actualKey)))
         return false;

      bool match = CRYPTO_memcmp(actualKey, expectedKey, sizeof(actualKey)) == 0;

      OPENSSL_cleanse(actualKey, sizeof(actualKey));
      OPENSSL_cleanse(expectedKey, sizeof(expectedKey));

      return match;
   }

   AnsiString HashCreator::GetSalt_(const AnsiString &inputString)
   {
      AnsiString result = inputString.Mid(0,SALT_LENGTH);
      return result;
   }

   AnsiString HashCreator::GetHash_(const AnsiString &sInputString, HashCreator::RequestedEncoding encoding)
   {
      AnsiString temp = sInputString;
      return GetHash_Raw((unsigned char*) temp.GetBuffer(), temp.GetLength(), encoding);
   }

   AnsiString HashCreator::GetHash_Raw(const unsigned char *input, int inputLength, HashCreator::RequestedEncoding encoding)
   {
      const EVP_MD *digest = nullptr;

      switch (hash_type_)
      {
      case SHA1:
         digest = EVP_sha1();
         break;
      case SHA256:
         digest = EVP_sha256();
         break;
      case MD5:
         digest = EVP_md5();
         break;
      }

      if (digest == nullptr)
         return "";

      unsigned char results[EVP_MAX_MD_SIZE];
      unsigned int digestLength = 0;

      if (EVP_Digest(input, inputLength, results, &digestLength, digest, nullptr) != 1)
         return "";

      HM::AnsiString retVal;
      if (encoding == hex)
      {
         retVal = BytesToHex(results, digestLength);
      }
      else if (encoding == base64)
      {
         MimeCodeBase64 encoder;
         encoder.SetInput((const char*) results, digestLength, true);

         AnsiString sEncodedValue;
         encoder.GetOutput(sEncodedValue);

         retVal = sEncodedValue;
         retVal = retVal.Mid(0, retVal.GetLength()-2);
      }

      return retVal;
   }

   void
   HashCreatorTester::Test()
   {
      // Run basic test.
      HashCreator hasher(HashCreator::SHA256);
      AnsiString result = hasher.GenerateHash("The quick brown fox jumps over the lazy dog", "");

      if (!hasher.ValidateHash("The quick brown fox jumps over the lazy dog", result, true))
         throw 0;

      // Check that same password hashed twice yealds separate hashes.
      AnsiString test1 = hasher.GenerateHash("The quick brown fox jumps over the lazy dog", "");
      AnsiString test2 = hasher.GenerateHash("The quick brown fox jumps over the lazy dog", "");
      if (test1 == test2)
         throw 0;

      // PBKDF2 round-trip.
      AnsiString pbkdf2Hash = HashCreator::GeneratePBKDF2("The quick brown fox jumps over the lazy dog");
      if (!HashCreator::IsPBKDF2Hash(pbkdf2Hash))
         throw 0;
      if (!HashCreator::ValidatePBKDF2("The quick brown fox jumps over the lazy dog", pbkdf2Hash))
         throw 0;
      if (HashCreator::ValidatePBKDF2("the wrong password", pbkdf2Hash))
         throw 0;

      // PBKDF2 hashes must be salted - two hashes of the same input must differ.
      AnsiString pbkdf2Hash2 = HashCreator::GeneratePBKDF2("The quick brown fox jumps over the lazy dog");
      if (pbkdf2Hash == pbkdf2Hash2)
         throw 0;

      // Argon2id round-trip.
      AnsiString argon2Hash = HashCreator::GenerateArgon2id("The quick brown fox jumps over the lazy dog");
      if (!HashCreator::IsArgon2idHash(argon2Hash))
         throw 0;
      if (!HashCreator::ValidateArgon2id("The quick brown fox jumps over the lazy dog", argon2Hash))
         throw 0;
      if (HashCreator::ValidateArgon2id("the wrong password", argon2Hash))
         throw 0;

      // Argon2id hashes must be salted - two hashes of the same input must differ.
      AnsiString argon2Hash2 = HashCreator::GenerateArgon2id("The quick brown fox jumps over the lazy dog");
      if (argon2Hash == argon2Hash2)
         throw 0;

      // The PBKDF2 and Argon2id schemes must not be confused for one another.
      if (HashCreator::IsArgon2idHash(pbkdf2Hash))
         throw 0;
      if (HashCreator::IsPBKDF2Hash(argon2Hash))
         throw 0;
      if (HashCreator::ValidatePBKDF2("The quick brown fox jumps over the lazy dog", argon2Hash))
         throw 0;
      if (HashCreator::ValidateArgon2id("The quick brown fox jumps over the lazy dog", pbkdf2Hash))
         throw 0;

      for (int i = 0; i < 250; i++)
      {
         HashCreator memoryTester(HashCreator::SHA256);

         String temp;
         temp.Format(_T("%d"), i);
         AnsiString hashableString = temp;

         hasher.GenerateHash(hashableString, "test");
      }
   }
}