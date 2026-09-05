// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Account;

   class COMAuthentication
   {
   public:
      COMAuthentication(void);
      ~COMAuthentication(void);

      std::shared_ptr<const Account> Authenticate(const String &sUsername, const String &sPassword);

      // As above, with a TOTP code. This is the ONLY path that can satisfy a second
      // factor: a mail client has nowhere to type one, which is precisely why an
      // account with a factor enrolled authenticates there through an app password
      // instead. An empty code is not a shortcut - an enrolled account still needs a
      // valid one.
      std::shared_ptr<const HM::Account> Authenticate(const String &sUsername, const String &sPassword, const String &sCode);

      void AttempAnonymousAuthentication();

      bool GetIsAuthenticated() const;

      bool GetIsDomainAdmin() const;
      bool GetIsServerAdmin() const;

      __int64 GetAccountID() const;
      __int64 GetDomainID() const;

      int GetAccessDenied() const;

   private:

      // The administrator credential: the ini password, and - when one is enrolled
      // (IniFileSettings::GetAdministratorTotpSecret) - a TOTP code. codePresented
      // says which overload the caller came through: without a code, an enrolled
      // administrator is refused outright, so a stolen password is not a
      // credential on its own. Sets account_ on success.
      void AuthenticateAdministrator_(const String &sPassword, const String &sCode, bool codePresented);

      std::shared_ptr<const Account> account_;
   };
}