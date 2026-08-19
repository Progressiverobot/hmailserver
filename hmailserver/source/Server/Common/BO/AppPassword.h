// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

namespace HM
{
   class DateTime;

   // One application-specific password: a credential that authenticates a single
   // account over IMAP, POP3 or SMTP, alongside the account's own password and
   // revocable without touching it.
   //
   // It exists because per-account two-factor authentication is otherwise
   // impossible. A mail client has nowhere to type a code, so an account that
   // required one could not be opened by any client at all; the way every large
   // provider resolved that is to let the account issue a credential per client,
   // each one revocable on its own when a laptop is lost.
   //
   // The clear text is generated once, shown once, and never stored: what the row
   // holds is a hash in the same scheme as the account password, so a database
   // that leaks does not hand over mailbox access. That also fixes what the
   // scheme can do - see PasswordValidator for why SCRAM cannot authenticate one.
   class AppPassword : public BusinessObject<AppPassword>
   {
   public:
      AppPassword();
      AppPassword(__int64 id, __int64 accountID, const String &name, const String &hash,
                  int encryption, const String &created, const String &lastUsed, bool active);
      ~AppPassword();

      // The label the account holder gave it - "Thunderbird on the laptop". Named
      // GetName because Collection::GetItemByName looks it up through this.
      String GetName() const { return name_; }
      void SetName(const String &value) { name_ = value; }

      __int64 GetAccountID() const { return account_id_; }
      void SetAccountID(__int64 value) { account_id_ = value; }

      String GetHash() const { return hash_; }
      void SetHash(const String &value) { hash_ = value; }

      int GetEncryption() const { return encryption_; }
      void SetEncryption(int value) { encryption_ = value; }

      String GetCreatedTime() const { return created_; }
      void SetCreatedTime(const String &value) { created_ = value; }

      // Empty for a password that has never authenticated anything. That is the
      // single most useful thing this table can tell an administrator, because it
      // names the credentials that can be revoked with no consequences at all.
      String GetLastUsedTime() const { return last_used_; }
      void SetLastUsedTime(const String &value) { last_used_ = value; }

      bool GetActive() const { return active_; }
      void SetActive(bool value) { active_ = value; }

      // Sets the hash from clear text, using the strongest of the server's preferred
      // hash algorithm and its configured minimum. The clear text is not retained.
      void SetPassword(const String &clearText);

      // A fresh secret, and the only place one should come from.
      //
      // NOT PasswordGenerator::Generate, which returns the first twelve hex
      // characters of a GUID: 48 bits, for a credential that is typed into a client
      // once and then lives for years. This is 20 characters from a 30-symbol
      // alphabet - about 98 bits - drawn from OpenSSL's CSPRNG and grouped in fives,
      // because the one thing a person does with an app password is copy it across.
      //
      // The alphabet omits I, L, O, U and the digits 0 and 1: the first four because
      // they are misread as one another and as 1 and 0, and U because excluding it
      // keeps accidental words out of a string people will read aloud over a phone.
      static String GenerateSecret();

      bool XMLStore(XNode *pParentNode, int iBackupOptions);
      bool XMLLoad(XNode *pNode, int iRestoreOptions);
      bool XMLLoadSubItems(XNode *pNode, int iRestoreOptions) { return true; }

   private:

      __int64 account_id_;
      String name_;
      String hash_;
      int encryption_;
      String created_;
      String last_used_;
      bool active_;
   };
}
