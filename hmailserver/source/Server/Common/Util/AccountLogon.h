// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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
      void RegisterFailedLogin(const IPAddress &ipaddress, const String &username, bool &disconnect);

   private:

      void CreateIPRange(const IPAddress &ipaddress, const String &username, int minutes);

      String GetIPRangeName_(const String &username);

      static boost::recursive_mutex ip_range_creation_mutex_;
   };
}