// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class BlowFishEncryptor;
   
   class Crypt : public Singleton<Crypt>
   {
   public:
	   Crypt();
	   virtual ~Crypt();

      enum EncryptionType
      {
         ETNone = 0,
         ETBlowFish = 1,
         ETMD5 = 2,
         ETSHA256 = 3,
         ETPBKDF2 = 4,
         ETArgon2id = 5,
         ETDPAPI = 6
      };

      EncryptionType GetHashType(const String &hash);

      String EnCrypt(const String &sInput, EncryptionType iType) const;
      String DeCrypt(const String &sInput, EncryptionType iType) const;

      bool Validate(const String &password, const String &originalHash, EncryptionType iType) const;

      // Protects a reversible secret for storage (route/fetch/relayer passwords).
      // When DPAPI protection is enabled (the default) and available the result is
      // a self-describing, machine-bound "DPAPI:<base64>" envelope; otherwise it
      // falls back to the legacy Blowfish form. Empty input returns empty.
      String ProtectSecret(const String &sInput) const;

      // Reverses ProtectSecret. A "DPAPI:" prefixed value is unprotected with
      // DPAPI; any other (legacy) value is decrypted with Blowfish, so existing
      // stored secrets keep working transparently. Empty input returns empty.
      String UnprotectSecret(const String &sStored) const;

   private:

      // Applies the optional server-wide password pepper (HMAC-SHA256 under the
      // configured secret) ahead of Argon2id hashing/verification. Returns the
      // password unchanged when no pepper is configured.
      AnsiString ApplyPepper_(const AnsiString &password) const;

      BlowFishEncryptor *blow_fish_;

   };
}
