// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include ".\AppPassword.h"

#include "../Application/IniFileSettings.h"
#include "../Util/Crypt.h"
#include "../Util/Parsing/StringParser.h"

#include <openssl/rand.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   AppPassword::AppPassword() :
      account_id_(0),
      encryption_(0),
      active_(true)
   {
      SetID(0);
   }

   AppPassword::AppPassword(__int64 id, __int64 accountID, const String &name, const String &hash,
                            int encryption, const String &created, const String &lastUsed, bool active) :
      account_id_(accountID),
      name_(name),
      hash_(hash),
      encryption_(encryption),
      created_(created),
      last_used_(lastUsed),
      active_(active)
   {
      SetID(id);
   }

   AppPassword::~AppPassword()
   {
   }

   String
   AppPassword::GenerateSecret()
   {
      // Crockford-style base32 without I, L, O, U, 0 or 1. See the header.
      static const wchar_t alphabet[] = L"23456789ABCDEFGHJKMNPQRSTVWXYZ";
      const int alphabet_size = 30;

      const int character_count = 20;

      unsigned char random[character_count];

      if (RAND_bytes(random, character_count) != 1)
      {
         // No secret at all rather than a predictable one. The caller reports the
         // failure; a credential generated from a broken CSPRNG is worse than none,
         // because it looks exactly like a good one.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6196, "AppPassword::GenerateSecret",
            "The random number generator failed, so no app password was generated. Nothing has been stored.");

         return "";
      }

      String result;

      for (int i = 0; i < character_count; i++)
      {
         // 256 is not a multiple of 30, so a plain modulo would make the first 16
         // symbols very slightly more likely than the rest. At 20 characters that bias
         // costs a fraction of a bit and would never be observed - but rejection
         // sampling costs nothing here either, and "we took the modulo" is how
         // generators acquire biases nobody remembers to measure.
         unsigned char value = random[i];
         while (value >= 240)   // 240 = 8 * 30, the largest usable multiple
         {
            unsigned char replacement;
            if (RAND_bytes(&replacement, 1) != 1)
               return "";
            value = replacement;
         }

         if (i > 0 && i % 5 == 0)
            result += _T("-");

         result += alphabet[value % alphabet_size];
      }

      return result;
   }

   void
   AppPassword::SetPassword(const String &clearText)
   {
      // The same algorithm the account password would get, for the same reason: an
      // app password is a mailbox credential, and storing it more weakly than the
      // password it stands in for would make issuing one a downgrade.
      Crypt::EncryptionType type =
         (Crypt::EncryptionType) IniFileSettings::Instance()->GetPreferredHashAlgorithm();

      // ETNone would store the clear text. Nothing generates that here - the
      // preferred algorithm is a configured integer, so it is checked rather than
      // trusted, because "the setting was 0" must not silently become "the
      // credential is in the clear".
      if (type != Crypt::ETPBKDF2 && type != Crypt::ETArgon2id &&
          type != Crypt::ETSHA256 && type != Crypt::ETMD5)
      {
         type = Crypt::ETArgon2id;
      }

      // An administrator who requires a minimum hash scheme means it for every
      // credential that opens a mailbox, and an app password is one. Without this,
      // MinimumAcceptedHashAlgorithm=Argon2id with PreferredHashAlgorithm left at
      // MD5 would refuse the account's own password for being too weakly stored -
      // correctly - while quietly issuing app passwords under exactly that scheme.
      int minimum = IniFileSettings::Instance()->GetMinimumAcceptedHashAlgorithm();

      if (minimum > (int) type && minimum <= (int) Crypt::ETArgon2id)
         type = (Crypt::EncryptionType) minimum;

      hash_ = Crypt::Instance()->EnCrypt(clearText, type);
      encryption_ = (int) type;
   }

   bool
   AppPassword::XMLStore(XNode *pParentNode, int iOptions)
   {
      XNode *pNode = pParentNode->AppendChild(_T("AppPassword"));

      pNode->AppendAttr(_T("Name"), name_);
      pNode->AppendAttr(_T("Hash"), hash_);
      pNode->AppendAttr(_T("Encryption"), String(StringParser::IntToString(encryption_)));
      pNode->AppendAttr(_T("Created"), created_);
      pNode->AppendAttr(_T("LastUsed"), last_used_);
      pNode->AppendAttr(_T("Active"), active_ ? _T("1") : _T("0"));

      return true;
   }

   bool
   AppPassword::XMLLoad(XNode *pNode, int iRestoreOptions)
   {
      name_ = pNode->GetAttrValue(_T("Name"));
      hash_ = pNode->GetAttrValue(_T("Hash"));
      encryption_ = _ttoi(pNode->GetAttrValue(_T("Encryption")));
      created_ = pNode->GetAttrValue(_T("Created"));
      last_used_ = pNode->GetAttrValue(_T("LastUsed"));
      active_ = pNode->GetAttrValue(_T("Active")) == _T("1");

      return true;
   }
}
