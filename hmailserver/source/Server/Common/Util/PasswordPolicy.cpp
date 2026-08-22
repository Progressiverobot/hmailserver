// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "PasswordPolicy.h"

#include "../Application/IniFileSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Deliberately short, and deliberately not a "top 10000" list.
      //
      // A real deny-list is megabytes and wants a data file, an update path and a
      // decision about what to do when it grows - none of which exist here, and
      // shipping a token list under a name that promises one would be worse than
      // shipping nothing. What this catches is the passwords that get typed when
      // somebody is setting up a test mailbox and never comes back to it, which is
      // the case that actually shows up in a mail server's logs.
      //
      // Compared case-insensitively, because "Password1" is not meaningfully
      // stronger than "password1".
      const wchar_t *common_passwords[] =
      {
         L"password", L"passw0rd", L"password1", L"password123",
         L"123456", L"1234567", L"12345678", L"123456789", L"1234567890",
         L"qwerty", L"qwerty123", L"abc123", L"letmein", L"welcome",
         L"admin", L"administrator", L"root", L"secret", L"changeme",
         L"test", L"test123", L"mail", L"email", L"hmailserver",
         L"iloveyou", L"monkey", L"dragon", L"sunshine", L"princess",
         L"football", L"baseball", L"master", L"login", L"guest"
      };
   }

   bool
   PasswordPolicy::ContainsUsername_(const String &username, const String &password)
   {
      if (username.IsEmpty() || password.IsEmpty())
         return false;

      String localPart = username;

      int at = localPart.Find(_T("@"));
      if (at > 0)
         localPart = localPart.Mid(0, at);

      // Two characters would match almost anything; this is about the whole name
      // appearing in the password, not an accidental letter pair.
      if (localPart.GetLength() < 3)
         return false;

      String lowerPassword = password;
      lowerPassword.ToLower();
      localPart.ToLower();

      return lowerPassword.Find(localPart) >= 0;
   }

   bool
   PasswordPolicy::Evaluate(const PasswordPolicyConfig &config, const String &username,
                            const String &password, String &out_reason)
   {
      out_reason = "";

      const bool anyRuleActive =
         config.minimum_length > 0 || config.require_mixed_case || config.require_digit ||
         config.require_non_alphanumeric || config.reject_common;

      if (!anyRuleActive)
         return true;

      // An empty password is refused by the validator at logon anyway, so accepting
      // one here would only store a credential that can never be used.
      if (password.IsEmpty())
      {
         out_reason = _T("The password is empty.");
         return false;
      }

      if (config.minimum_length > 0 && password.GetLength() < config.minimum_length)
      {
         out_reason.Format(_T("The password must be at least %d characters long."), config.minimum_length);
         return false;
      }

      if (config.require_mixed_case)
      {
         bool hasUpper = false;
         bool hasLower = false;

         for (int i = 0; i < password.GetLength(); i++)
         {
            if (iswupper(password[i])) hasUpper = true;
            else if (iswlower(password[i])) hasLower = true;
         }

         if (!hasUpper || !hasLower)
         {
            out_reason = _T("The password must contain both upper and lower case letters.");
            return false;
         }
      }

      if (config.require_digit)
      {
         bool hasDigit = false;

         for (int i = 0; i < password.GetLength(); i++)
         {
            if (iswdigit(password[i]))
            {
               hasDigit = true;
               break;
            }
         }

         if (!hasDigit)
         {
            out_reason = _T("The password must contain at least one digit.");
            return false;
         }
      }

      if (config.require_non_alphanumeric)
      {
         bool hasSymbol = false;

         for (int i = 0; i < password.GetLength(); i++)
         {
            // Anything that is not a letter and not a digit, rather than a fixed list
            // of punctuation: a list would refuse characters from the keyboard layout
            // whoever is typing actually has.
            if (!iswalnum(password[i]))
            {
               hasSymbol = true;
               break;
            }
         }

         if (!hasSymbol)
         {
            out_reason = _T("The password must contain at least one character that is not a letter or a digit.");
            return false;
         }
      }

      if (config.reject_common)
      {
         for (size_t i = 0; i < sizeof(common_passwords) / sizeof(common_passwords[0]); i++)
         {
            if (password.CompareNoCase(common_passwords[i]) == 0)
            {
               out_reason = _T("The password is one of the most commonly used passwords and would be guessed immediately.");
               return false;
            }
         }
      }

      // Checked last so that the more specific requirements are reported first: being
      // told "it must contain a digit" is more useful than "it contains your name"
      // when both are true and the digit is the easier fix.
      if (ContainsUsername_(username, password))
      {
         out_reason = _T("The password must not contain the account name.");
         return false;
      }

      return true;
   }

   PasswordPolicyConfig
   PasswordPolicy::GetConfiguredPolicy()
   {
      PasswordPolicyConfig config;

      IniFileSettings *settings = IniFileSettings::Instance();

      config.minimum_length = settings->GetPasswordPolicyMinimumLength();
      config.require_mixed_case = settings->GetPasswordPolicyRequireMixedCase();
      config.require_digit = settings->GetPasswordPolicyRequireDigit();
      config.require_non_alphanumeric = settings->GetPasswordPolicyRequireNonAlphanumeric();
      config.reject_common = settings->GetPasswordPolicyRejectCommon();

      return config;
   }

   bool
   PasswordPolicy::IsAcceptable(const String &username, const String &password, String &out_reason)
   {
      return Evaluate(GetConfiguredPolicy(), username, password, out_reason);
   }
}
