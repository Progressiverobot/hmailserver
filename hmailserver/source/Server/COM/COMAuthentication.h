// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Common/Util/AuditTrail.h"

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

      // The audit trail's identity chokepoint for COM. GetIsServerAdmin and
      // GetIsDomainAdmin are the authorisation questions every COM write asks
      // before it writes, and they are asked ON THE THREAD that is about to do
      // the writing - which matters, because hMailServer's COM server is an
      // out-of-process MTA and RPC dispatches each call on whichever pool thread
      // is free. An actor installed when the client authenticated would be on a
      // thread that never writes anything.
      //
      // The COM methods that write without asking - the settings property
      // setters, which are reachable only once LoadSettings has asked once -
      // carry an AuditScope of their own built from this; see
      // COMAuthenticator::GetAuditActor and build/check-audit-choke-point.py.
      AuditTrail::Actor GetAuditActor() const;

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