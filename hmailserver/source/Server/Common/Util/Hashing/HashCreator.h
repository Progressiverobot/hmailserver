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
         PBKDF2_MAX_ITERATIONS = 10000000
      };

      HashType hash_type_;
   };

   class HashCreatorTester
   {
   public:
      void Test();
   };

}