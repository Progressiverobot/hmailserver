// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "Crypt.h"
#include "BlowFish.h"
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
      default:
         {
            assert(0);
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
      default:
         {
            assert(0);
         }
      }

      return false;

   }

   Crypt::EncryptionType 
   Crypt::GetHashType(const String &hash)
   {
      AnsiString ansiHash = hash;
      if (HashCreator::IsPBKDF2Hash(ansiHash))
         return ETPBKDF2;

      if (HashCreator::IsArgon2idHash(ansiHash))
         return ETArgon2id;

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
         default:
            assert(0);
      }
      
      return "";
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
