// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "Crypt.h"
#include "BlowFish.h"
#include "DataProtector.h"
#include "Unicode.h"
#include "Hashing/HashCreator.h"
#include "../Application/IniFileSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   Crypt::Crypt()
   {
      blow_fish_ = new BlowFishEncryptor();
   }  

   Crypt::~Crypt()
   {
      delete blow_fish_;
   }

   String
   Crypt::EnCrypt(const String &sInput,EncryptionType iType) const
   {
      switch (iType)
      {
      case ETNone:
         return sInput;
      case ETBlowFish:
         {
            if (sInput.IsEmpty())
               return "";

            return blow_fish_->EncryptToString(sInput);
         }
      case ETMD5:
         {
            HashCreator crypter(HashCreator::MD5);
            String sResult = crypter.GenerateHashNoSalt(sInput, HashCreator::hex);
            return sResult;
         }
      case ETSHA256:
         {
            HashCreator encrypter(HashCreator::SHA256);
            AnsiString result = encrypter.GenerateHash(sInput, "");
            return result;
         }
      case ETPBKDF2:
         {
            AnsiString input = sInput;
            AnsiString result = HashCreator::GeneratePBKDF2(input);
            return result;
         }
      case ETArgon2id:
         {
            AnsiString input = ApplyPepper_(sInput);
            AnsiString result = HashCreator::GenerateArgon2id(input);
            return result;
         }
      case ETScrypt:
         {
            // Peppered like Argon2id: neither scheme can serve SCRAM, so there is no
            // SaltedPassword to keep unpeppered.
            AnsiString input = ApplyPepper_(sInput);
            AnsiString result = HashCreator::GenerateScrypt(input);
            return result;
         }
      case ETDPAPI:
         {
            if (sInput.IsEmpty())
               return "";

            AnsiString utf8;
            Unicode::WideToMultiByte(sInput, utf8);

            AnsiString protectedBase64;
            if (!DataProtector::Protect(utf8, protectedBase64))
               return "";

            return protectedBase64;
         }
      default:
         {
            HM_ASSERT(0);
         }
      }

      return "";

   }

   bool
   Crypt::Validate(const String &password, const String &originalHash, EncryptionType iType) const
   {
      switch (iType)
      {
      case ETMD5:
         {
            // Salts are not used for the MD5 hashes.
            HashCreator encrypter(HashCreator::MD5);
            bool result = encrypter.ValidateHash(password, originalHash, false);
            return result;
         }
      case ETSHA256:
         {
            // Salts are always used for the SHA256 hashes.
            HashCreator encrypter(HashCreator::SHA256);
            bool result = encrypter.ValidateHash(password, originalHash, true);
            return result;
         }
      case ETPBKDF2:
         {
            AnsiString ansiPassword = password;
            AnsiString ansiHash = originalHash;
            return HashCreator::ValidatePBKDF2(ansiPassword, ansiHash);
         }
      case ETArgon2id:
         {
            AnsiString ansiPassword = ApplyPepper_(password);
            AnsiString ansiHash = originalHash;
            return HashCreator::ValidateArgon2id(ansiPassword, ansiHash);
         }
      case ETScrypt:
         {
            AnsiString ansiPassword = ApplyPepper_(password);
            AnsiString ansiHash = originalHash;
            return HashCreator::ValidateScrypt(ansiPassword, ansiHash);
         }
      default:
         {
            HM_ASSERT(0);
         }
      }

      return false;

   }

   int
   Crypt::StrengthRank(int encryptionType)
   {
      switch (encryptionType)
      {
      case ETBlowFish:
         return 1;
      case ETMD5:
         return 2;
      case ETSHA256:
         return 3;
      case ETPBKDF2:
         return 4;
      case ETArgon2id:
      case ETScrypt:
         return 5;
      default:
         // ETNone, ETDPAPI (stored secrets, not passwords) and anything unknown.
         return 0;
      }
   }

   Crypt::EncryptionType
   Crypt::GetHashType(const String &hash)
   {
      AnsiString ansiHash = hash;
      if (HashCreator::IsPBKDF2Hash(ansiHash))
         return ETPBKDF2;

      if (HashCreator::IsArgon2idHash(ansiHash))
         return ETArgon2id;
      if (HashCreator::IsScryptHash(ansiHash))
         return ETScrypt;

      int length = hash.GetLength();
      if (length == 32)
         return ETMD5;
      else if (length == 70)
         return ETSHA256;
      else
         return ETNone;
   }

   String
   Crypt::DeCrypt(const String &sInput, EncryptionType iType) const
   {
      switch (iType)
      {
         case ETNone:
            return sInput;
         case ETBlowFish:
            {
               if (sInput.IsEmpty())
                  return "";

               return blow_fish_->DecryptFromString(sInput);
            }
            break;
         case ETDPAPI:
            {
               if (sInput.IsEmpty())
                  return "";

               AnsiString protectedBase64 = sInput;
               AnsiString utf8;
               if (!DataProtector::Unprotect(protectedBase64, utf8))
                  return "";

               String result;
               Unicode::MultiByteToWide(utf8, result);
               return result;
            }
            break;
         default:
            HM_ASSERT(0);
      }
      
      return "";
   }

   String
   Crypt::ProtectSecret(const String &sInput) const
   {
      if (sInput.IsEmpty())
         return "";

      // When DPAPI protection is enabled (default) and available, store a
      // self-describing, machine-bound envelope. If DPAPI fails for any reason
      // fall back to Blowfish so a secret is never lost.
      if (IniFileSettings::Instance()->GetProtectStoredSecretsWithDPAPI())
      {
         String protectedValue = EnCrypt(sInput, ETDPAPI);
         if (!protectedValue.IsEmpty())
            return _T("DPAPI:") + protectedValue;
      }

      return EnCrypt(sInput, ETBlowFish);
   }

   String
   Crypt::UnprotectSecret(const String &sStored) const
   {
      if (sStored.IsEmpty())
         return "";

      // A DPAPI envelope is self-describing; everything else is a legacy
      // Blowfish value, so existing stored secrets keep working transparently.
      if (sStored.Left(6) == _T("DPAPI:"))
         return DeCrypt(sStored.Mid(6), ETDPAPI);

      return DeCrypt(sStored, ETBlowFish);
   }

   AnsiString
   Crypt::ApplyPepper_(const AnsiString &password) const
   {
      // The pepper is a server-wide secret that is never stored alongside the hash.
      // When configured, the password is HMAC-SHA256'd under the pepper before it is
      // fed to Argon2id, so a stolen hash database cannot be attacked without also
      // obtaining the pepper. An empty pepper preserves the previous behaviour.
      //
      // This is applied to Argon2id only: PBKDF2 hashes double as the SCRAM
      // SaltedPassword, which the client reconstructs from the raw password, so they
      // must remain un-peppered to keep SCRAM working.
      String pepper = IniFileSettings::Instance()->GetPasswordPepper();
      if (pepper.IsEmpty())
         return password;

      AnsiString ansiPepper = pepper;
      return HashCreator::ComputeHMACSHA256Hex(ansiPepper, password);
   }
}
