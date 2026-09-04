// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Account;
   enum PersistenceMode;

   class PersistentAccount 
   {
   public:
	   PersistentAccount();
	   virtual ~PersistentAccount();

      static bool DeleteObject(std::shared_ptr<Account> pAccount);
      static bool SaveObject(std::shared_ptr<Account> pAccount);
      static bool SaveObject(std::shared_ptr<Account> pAccount, String &sErrorMessage, PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<Account> pAccount, String &sErrorMessage, bool createInbox, PersistenceMode mode);
      static bool DeleteMessages(std::shared_ptr<Account> pAccount);

      static __int64 GetMessageBoxSize(__int64 iAccountID);
      // Returns the current message box size, 
      // counted in bytes.

      static bool ReadObject(std::shared_ptr<Account> pAccount,std::shared_ptr<DALRecordset> pRS);
      static bool ReadObject(std::shared_ptr<Account> pAccount,const String & sAddress);
      static bool ReadObject(std::shared_ptr<Account> pAccount,__int64 ObjectID);

      static bool UpdateLastLogonTime(std::shared_ptr<const Account> pAccount);

      static bool GetIsVacationMessageOn(std::shared_ptr<const Account> pAccount);
      static bool CreateInbox(const Account &account);

      static bool DisableVacationMessage(std::shared_ptr<const Account> pAccount);

      // Legacy password-hash upgrade, first half.
      //
      // ReadObject re-hashes an account whose stored password is unencrypted
      // (accountpwencryption = 0) into the in-memory object, so that the rest of the
      // server always sees a real hash -- SCRAM-SHA-256 and the hash-policy checks
      // both key off the in-memory type, and an account whose row is still clear text
      // would otherwise stop being able to authenticate with those mechanisms.
      //
      // That re-hash is deliberately not written back at read time: reads happen on
      // every cache miss, on paths that must not write, and SaveObject itself reads
      // the account it is about to save (so writing here would recurse). Instead the
      // account id is remembered as "an upgrade is owed", and the write happens in
      // PasswordValidator at the one point where it is both safe and cheap: right
      // after the clear-text password has been successfully verified.
      //
      // Until that happens the account keeps authenticating exactly as it does today,
      // off its stored value. The set only ever holds ids of accounts whose stored
      // password is still unencrypted, and entries are dropped once persisted.
      static bool IsPasswordUpgradePending(__int64 accountID);

      // Writes an upgraded password hash for an existing account. Returns false if
      // the write failed; the caller must treat that as "keep the old hash and let
      // the login through", never as an authentication failure.
      static bool PersistUpgradedPassword(std::shared_ptr<const Account> pAccount, const String &passwordHash, int encryptionType);

      // Drops every pending flag. Called when the server stops, alongside the account
      // cache being cleared: the flags name account ids, and after a stop the
      // configured database can be a different one, at which point those ids describe
      // somebody else's accounts.
      static void ClearAllPasswordUpgradePending();

   private:

      static bool ReadObject(std::shared_ptr<Account> pAccount,const SQLCommand &command);

      // Creates Drafts, Sent, Trash and Junk for a new account, each with its RFC 6154
      // designation stored on the row. Only called when the setting is on. A folder
      // that cannot be created is reported and skipped; the account is not rolled
      // back for it, because an account with an inbox is usable and one that was
      // deleted again is not.
      static void CreateDefaultSpecialUseFolders_(const Account &account);

      static void MarkPasswordUpgradePending_(__int64 accountID);
      static void ClearPasswordUpgradePending_(__int64 accountID);

      static boost::recursive_mutex password_upgrade_mutex_;
      static std::set<__int64> password_upgrade_pending_;
   };


}