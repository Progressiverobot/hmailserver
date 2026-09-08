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

#include <atomic>

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

      // When protection is enabled (the default) store a self-describing envelope
      // whose prefix names the platform store that wrote it, so that a value
      // carried to the other platform is recognised rather than fed to the wrong
      // cipher. ETDPAPI is the storage identifier for "the platform's secret
      // store" on both: DPAPI itself on Windows, the key file on Linux.
      if (IniFileSettings::Instance()->GetProtectStoredSecretsWithDPAPI())
      {
         String protectedValue = EnCrypt(sInput, ETDPAPI);
#ifdef HM_PLATFORM_POSIX
         if (!protectedValue.IsEmpty())
            return _T("LINUX1:") + protectedValue;

         // No Blowfish here. Its key is a constant in the source, so a value it
         // writes is protected from nobody who has the binary, and a server that
         // wrote one because the real store was unavailable would have downgraded
         // a secret without anyone choosing that. DataProtector has already said
         // why the store was unavailable and what to do; this says what it cost.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6416, "Crypt::ProtectSecret",
            "A secret was NOT stored: the stored-secret key file was unavailable (the error before this one "
            "says why), and this build does not fall back to the legacy Blowfish scheme. Fix the key file and "
            "enter the secret again.");
         return "";
#else
         // If DPAPI fails for any reason fall back to Blowfish so a secret is
         // never lost.
         if (!protectedValue.IsEmpty())
            return _T("DPAPI:") + protectedValue;
#endif
      }

      return EnCrypt(sInput, ETBlowFish);
   }

   String
   Crypt::UnprotectSecret(const String &sStored) const
   {
      if (sStored.IsEmpty())
         return "";

      // An envelope is self-describing; everything else is a legacy Blowfish
      // value, so existing stored secrets keep working transparently. An envelope
      // from the other platform is the one thing neither store can open, and it
      // is reported by name - once, since a moved database holds several - and
      // answered with an empty secret, which every caller treats as "re-enter".
      if (sStored.Left(6) == _T("DPAPI:"))
      {
#ifdef HM_PLATFORM_POSIX
         ReportForeignEnvelopeOnce_(6414,
            "A stored secret is a Windows DPAPI envelope, which only the Windows machine that wrote it can open: "
            "the database or the INI was carried over from a Windows installation. The secret is intact there and "
            "unreadable here; re-enter it on this machine and it is stored under this machine's key file. "
            "Reported once; there may be more than one such value.");
         return "";
#else
         return DeCrypt(sStored.Mid(6), ETDPAPI);
#endif
      }

      if (sStored.Left(7) == _T("LINUX1:"))
      {
#ifdef HM_PLATFORM_POSIX
         return DeCrypt(sStored.Mid(7), ETDPAPI);
#else
         ReportForeignEnvelopeOnce_(6415,
            "A stored secret is a LINUX1 envelope, written by a Linux installation under its key file, which "
            "DPAPI cannot open: the database was carried over from a Linux installation. The secret is intact "
            "there and unreadable here; re-enter it on this machine and it is stored with DPAPI. "
            "Reported once; there may be more than one such value.");
         return "";
#endif
      }

      return DeCrypt(sStored, ETBlowFish);
   }

   void
   Crypt::ReportForeignEnvelopeOnce_(int errorId, const String &message)
   {
      static std::atomic<bool> reported(false);

      bool expected = false;
      if (reported.compare_exchange_strong(expected, true))
         ErrorManager::Instance()->ReportError(ErrorManager::High, errorId, "Crypt::UnprotectSecret", message);
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
