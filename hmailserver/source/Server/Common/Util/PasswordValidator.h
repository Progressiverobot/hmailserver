// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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

      // Resolves a login name to an account WITHOUT checking any credential - domain
      // aliases and the default domain applied exactly as the validation paths apply
      // them, and the same active-account and active-domain rules. Exists for the one
      // caller that must know something about the account before it can validate: the
      // COM path needs the TOTP secret in order to check the code that decides whether
      // the password may be tried at all.
      static std::shared_ptr<const Account> LookupAccount(const String &sUsername);
      static std::shared_ptr<const Account> ValidatePassword(const String &sMasqname, const String &sUsername, const String &sPassword);

      // Validates the user password. Return the account if validation is OK. 

      // secondFactorSatisfied says the CALLER has already verified a TOTP code for
      // this account. Only the COM authentication path can - a mail client has
      // nowhere to type one - so it defaults to false and every protocol leaves it
      // alone: an account with a second factor enrolled authenticates in a mail
      // client through an app password and not through its own password.
      static bool ValidatePassword(std::shared_ptr<const Account> pAccount, const String &sPassword,
                                   bool secondFactorSatisfied = false);

      // Validates the user password. Return true if the password is correct.

   private:

      // The account's own password: the Active Directory path, the stored-hash
      // comparison, the opportunistic re-hash and the minimum-hash policy. Split out
      // of ValidatePassword so that a failure here can fall through to an app
      // password rather than being the end of the attempt.
      static bool ValidateAccountPassword_(std::shared_ptr<const Account> pAccount, const String &sPassword);

      // Any of the account's app passwords. Only ever called after the account's own
      // password has failed, and it returns false immediately - without a database
      // query - on a server where none has been issued.
      static bool ValidateAppPassword_(std::shared_ptr<const Account> pAccount, const String &sPassword);

      // Throttles the diagnostic for a stored app password whose hash scheme this
      // server could not have written. True the first time each row is seen.
      static bool ReportUnusableAppPasswordOnce_(__int64 appPasswordID);

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