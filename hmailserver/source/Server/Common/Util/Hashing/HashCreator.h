// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class HashCreator
   {
   public:
      enum HashType
      {
         SHA1 = 1,
         SHA256 = 2,
         MD5 = 3
      };

      enum RequestedEncoding
      {
         hex = 1,
         base64 = 2
      };

      HashCreator(HashType hashType);

      AnsiString GenerateHash(const AnsiString &inputString, const AnsiString &salt);
      bool ValidateHash(const AnsiString &password, const AnsiString &originalHash, bool useSalt);
      
      AnsiString GenerateHashNoSalt(const AnsiString &inputString, RequestedEncoding encoding);
      AnsiString GenerateHashNoSalt(unsigned char *input, int inputLength, RequestedEncoding encoding);

      // PBKDF2-HMAC-SHA256 password hashing. Produces a self-describing,
      // versioned hash string: $h1$<iterations>$<salt-hex>$<derived-key-hex>
      static AnsiString GeneratePBKDF2(const AnsiString &password);
      static bool ValidatePBKDF2(const AnsiString &password, const AnsiString &storedHash);
      static bool IsPBKDF2Hash(const AnsiString &storedHash);

      // Argon2id password hashing (OWASP-recommended memory-hard KDF, via OpenSSL
      // EVP_KDF). Produces a self-describing hash string:
      // $a2$<memory-KiB>$<time-cost>$<parallelism>$<salt-hex>$<derived-key-hex>
      static AnsiString GenerateArgon2id(const AnsiString &password);
      static bool ValidateArgon2id(const AnsiString &password, const AnsiString &storedHash);
      static bool IsArgon2idHash(const AnsiString &storedHash);

   private:
   
      AnsiString GetSalt_(const AnsiString &inputString);
      AnsiString GetHash_(const AnsiString &sInputString, RequestedEncoding encoding);
      AnsiString GetHash_Raw(const unsigned char *input, int inputLength, RequestedEncoding encoding);

      enum Sizes
      {
         SALT_LENGTH = 6,
         PBKDF2_SALT_BYTES = 16,
         PBKDF2_KEY_BYTES = 32,
         PBKDF2_DEFAULT_ITERATIONS = 210000,
         PBKDF2_MAX_ITERATIONS = 10000000,

         // Argon2id parameters. Memory cost is expressed in KiB; 19456 KiB (19 MiB)
         // with a time cost of 2 and a single lane is an OWASP-recommended minimum.
         ARGON2_SALT_BYTES = 16,
         ARGON2_KEY_BYTES = 32,
         ARGON2_DEFAULT_MEMORY_KIB = 19456,
         ARGON2_DEFAULT_TIME_COST = 2,
         ARGON2_DEFAULT_PARALLELISM = 1,
         ARGON2_MAX_MEMORY_KIB = 1048576,
         ARGON2_MAX_TIME_COST = 100,
         ARGON2_MAX_PARALLELISM = 16
      };

      HashType hash_type_;
   };

   class HashCreatorTester
   {
   public:
      void Test();
   };

}