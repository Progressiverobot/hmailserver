// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class Account;

   class PasswordValidator
   {
   public:
      PasswordValidator(void);
      ~PasswordValidator(void);

      static std::shared_ptr<const Account> ValidatePassword(const String &sUsername, const String &sPassword);
      static std::shared_ptr<const Account> ValidatePassword(const String &sMasqname, const String &sUsername, const String &sPassword);

      // Validates the user password. Return the account if validation is OK. 

      static bool ValidatePassword(std::shared_ptr<const Account> pAccount, const String &sPassword);

      // Validates the user password. Return true if the password is correct.

   private:

      // Brings the account's stored password up to the configured preferred hash
      // scheme. Must only ever be called with a password that has just been verified
      // against the account: that is the one moment the clear text is available, and
      // the only moment a write is justified.
      //
      // Returns the encryption type the stored password uses after the attempt - the
      // type it already had if nothing needed doing or if the upgrade failed.
      static int UpgradeStoredPasswordHash_(std::shared_ptr<const Account> pAccount, const String &sPassword, int currentEncryption);
   };
}