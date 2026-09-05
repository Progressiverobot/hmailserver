// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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

      // scrypt password hashing (RFC 7914, via OpenSSL EVP_PBE_scrypt) with the
      // OWASP parameters N=2^17, r=8, p=1 - 128 MiB per derivation. Produces a
      // self-describing hash string: $s2$<log2 N>$<r>$<p>$<salt-hex>$<derived-key-hex>
      static AnsiString GenerateScrypt(const AnsiString &password);
      static bool ValidateScrypt(const AnsiString &password, const AnsiString &storedHash);
      static bool IsScryptHash(const AnsiString &storedHash);

      // HMAC-SHA256 of data under key, returned as a lower-case hex string. Used by the
      // optional server-wide password pepper (Crypt::ApplyPepper_).
      static AnsiString ComputeHMACSHA256Hex(const AnsiString &key, const AnsiString &data);

      // The work factors new hashes are derived with: [Settings] PasswordHashIterations,
      // PasswordHashMemoryKB and PasswordHashTimeCost, or the defaults below where a
      // key is 0 or the ini has not been read. Bounds are enforced where the ini is
      // read, so these only ever return a value Validate* would accept.
      static int ConfiguredPBKDF2Iterations();
      static int ConfiguredArgon2idMemoryKiB();
      static int ConfiguredArgon2idTimeCost();

      // True when a stored PBKDF2 or Argon2id hash was derived with a lower work
      // factor than is configured now, so the next successful logon should re-derive
      // it. Never true for a costlier hash - lowering a setting affects new hashes
      // only - and never for a hash of any other scheme.
      static bool NeedsRehash(const AnsiString &storedHash);

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
         ARGON2_MAX_PARALLELISM = 16,

         // scrypt parameters. N is stored as its log2; 17 is 131072, which with r=8
         // costs 128 MiB per derivation - OWASP's recommendation. The ceilings bound
         // what a stored hash may ask for at verification: log2 N 20 with r 8 is
         // 1 GiB, and anything larger is a hash nobody wrote.
         SCRYPT_SALT_BYTES = 16,
         SCRYPT_KEY_BYTES = 32,
         SCRYPT_DEFAULT_LOG2_N = 17,
         SCRYPT_DEFAULT_R = 8,
         SCRYPT_DEFAULT_P = 1,
         SCRYPT_MIN_LOG2_N = 10,
         SCRYPT_MAX_LOG2_N = 20,
         SCRYPT_MAX_R = 32,
         SCRYPT_MAX_P = 16
      };

      HashType hash_type_;
   };

   class HashCreatorTester
   {
   public:
      void Test();
   };

}