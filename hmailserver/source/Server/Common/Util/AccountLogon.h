// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Account;

   class AccountLogon
   {
   public:
      AccountLogon(void);
      ~AccountLogon(void);

      std::shared_ptr<const Account> Logon(const IPAddress &ipaddress, const String &sUsername, const String &sPassword, bool &disconnect);
      std::shared_ptr<const Account> Logon(const IPAddress &ipaddress, const String &sMasqname, const String &sUsername, const String &sPassword, bool &disconnect);

      // Record a failed authentication attempt for auto-ban accounting. Sets
      // 'disconnect' to true when the connection should be dropped (and possibly the
      // IP auto-banned). Used both by Logon and by mechanisms such as SCRAM that
      // verify the proof themselves without ever holding a clear-text password.
      //
      // countTowardsAccountLockout: pass false when the failure is not a password
      // guess. An OAuth2 bearer token that was invalid or expired is the case that
      // matters - a client looping on a stale token would otherwise lock its own
      // user out of every password-based client for the lockout duration, and a
      // token is not guessable by brute force, so counting it protects nothing.
      // The REST API passes false too: its Basic credential is the administrator
      // password from the ini, not a mailbox name, so a per-name lock could only
      // ever lock a string nobody authenticates as. Such failures still feed the
      // per-IP auto-ban, which is the control that fits them.
      void RegisterFailedLogin(const IPAddress &ipaddress, const String &username, bool &disconnect,
                               bool countTowardsAccountLockout = true);

   private:

      void CreateIPRange(const IPAddress &ipaddress, const String &username, int minutes);

      String GetIPRangeName_(const String &username);

      static boost::recursive_mutex ip_range_creation_mutex_;
   };
}