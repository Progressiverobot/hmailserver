// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include ".\passwordvalidator.h"

#include "../Application/ObjectCache.h"
#include "../Application/DefaultDomain.h"
#include "../Application/IniFileSettings.h"
#include "../Application/Logger.h"
#include "../Cache/CacheContainer.h"
#include "../BO/Account.h"
#include "../BO/AppPasswords.h"
#include "../Persistence/PersistentAppPassword.h"
#include "../BO/Domain.h"
#include "../BO/DomainAliases.h"
#include "../Persistence/PersistentAccount.h"
#include "../LDAP/LdapDirectoryAuthenticator.h"
#include "../LDAP/LdapSettings.h"
#include "../Util/SSPIValidation.h"
#include "../Util/Crypt.h"
#include "../Util/Hashing/HashCreator.h"
#include "../Scripting/Result.h"
#include "../Scripting/Events.h"
#include "PasswordHistory.h"

#include <set>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PasswordValidator::PasswordValidator(void)
   {
   }

   PasswordValidator::~PasswordValidator(void)
   {
   }

   std::shared_ptr<const Account>
   PasswordValidator::ValidatePassword(const String &sUsername, const String &sPassword)
   {
	   return PasswordValidator::ValidatePassword(_T(""), sUsername, sPassword);
   }

   std::shared_ptr<const Account>
   PasswordValidator::LookupAccount(const String &sUsername)
   {
      std::shared_ptr<Account> pEmpty;

      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      String sAccountAddress = pDA->ApplyAliasesOnAddress(sUsername);

      sAccountAddress = DefaultDomain::ApplyDefaultDomain(sAccountAddress);

      std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(sAccountAddress);

      if (!pAccount || !pAccount->GetActive())
         return pEmpty;

      String sDomain = StringParser::ExtractDomain(sAccountAddress);
      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(sDomain);

      if (!pDomain || !pDomain->GetIsActive())
         return pEmpty;

      return pAccount;
   }

   std::shared_ptr<const Account>
   PasswordValidator::ValidatePassword(const String &sMasqname, const String &sUsername, const String &sPassword)
   {
      std::shared_ptr<Account> pEmpty;

      // Apply domain name aliases to this domain name.
      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      String sAccountAddress = pDA->ApplyAliasesOnAddress(sUsername);

      // Apply default domain
      sAccountAddress = DefaultDomain::ApplyDefaultDomain(sAccountAddress);

      std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(sAccountAddress);
      
      if (!pAccount)
         return pEmpty;

      if (!pAccount->GetActive())
         return pEmpty;

      // Check that the domain is active as well.
      
      String sDomain = StringParser::ExtractDomain(sAccountAddress);
      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(sDomain);

      if (!pDomain)
         return pEmpty;

      if (!pDomain->GetIsActive())
         return pEmpty;

      if (!ValidatePassword(pAccount, sPassword))
         return pEmpty;

      if (sMasqname.GetLength() == 0)
	      return pAccount;

      // if we get this far, we are authenticating against one username, but will actually login
      // as a second username (rfc-4616)

      // Apply domain name aliases to this domain name.
      pDA = ObjectCache::Instance()->GetDomainAliases();
      sAccountAddress = pDA->ApplyAliasesOnAddress(sMasqname);

      // Apply default domain
      sAccountAddress = DefaultDomain::ApplyDefaultDomain(sAccountAddress);

      pAccount = CacheContainer::Instance()->GetAccount(sAccountAddress);

      if (!pAccount)
	      return pEmpty;

      if (!pAccount->GetActive())
	      return pEmpty;

      // Check that the domain is active as well.

      sDomain = StringParser::ExtractDomain(sAccountAddress);
      pDomain = CacheContainer::Instance()->GetDomain(sDomain);

      if (!pDomain)
	      return pEmpty;

      if (!pDomain->GetIsActive())
	      return pEmpty;

      return pAccount;
   }

   bool 
   PasswordValidator::ValidatePassword(std::shared_ptr<const Account> pAccount, const String &sPassword, bool secondFactorSatisfied)
   {
      // Let a script override the password validation
      auto eventResult = Events::FireOnClientValidatePassword(pAccount, sPassword);

      if (eventResult != nullptr)
      {
         if (eventResult->GetValue() == 0)
         {
            // The script said to let the user through.
            return true;
         }

         if (eventResult->GetValue() == 1)
         {
            // The script said the password wasn't correct.
            return false;
         }
      }

      if (sPassword.GetLength() == 0)
      {
         // Empty passwords are not permitted.
         return false;
      }

      // A second factor turns the account's own password off HERE, in every client
      // that cannot present a code - which is every mail client there is.
      //
      // This is the point of the whole chain. TOTP has existed in this product for
      // years and protected only the admin tool, because IMAP, POP3 and SMTP have
      // nowhere to type a code: an account required to present one could not be
      // opened at all. App passwords are what make refusing the account password
      // survivable, so the rule is simply that once a secret is enrolled, the
      // account password stops being a mailbox credential and an app password
      // becomes the only one.
      //
      // secondFactorSatisfied is passed by the callers that CAN present a code - the
      // COM authentication path, which the Control Panel uses - and by nothing else.
      // The default is false, so a new call site is safe by omission rather than
      // dangerous by it.
      //
      // Skipped rather than compared-and-refused: there is no reason to spend an
      // Argon2id verification on a password that cannot be accepted whatever it says.
      const bool requiresSecondFactor = !pAccount->GetTotpSecret().IsEmpty();

      if (!requiresSecondFactor || secondFactorSatisfied)
      {
         if (ValidateAccountPassword_(pAccount, sPassword))
         {
            // Correct, and then aged out. Checked AFTER the comparison so that a
            // wrong guess against an expired account is indistinguishable from a
            // wrong guess against any other - and so the log line below is only
            // written for somebody who actually knows the password and is being
            // refused by policy, which is the person an administrator needs to
            // hear about.
            //
            // App passwords are deliberately unaffected. They are separate
            // credentials with their own lifecycle, and expiring them with the
            // account password would break every configured mail client at the
            // moment the account holder least expects it.
            if (!PasswordHistory::HasExpired(pAccount))
               return true;

            PasswordHistory::ReportExpired(pAccount);
         }
      }

      // The account's own password did not match. An app password might.
      //
      // Tried second, and never first, because that ordering is the whole cost model:
      // an app password is stored under the same deliberately-expensive hash as the
      // account password, so checking five of them before the real one would add half
      // a second of Argon2id to every ordinary logon. Checked here, the cost falls
      // only on an attempt that has already failed - and on a server where no app
      // password has ever been issued, AnyConfigured() answers from a cached flag and
      // this costs one comparison.
      //
      // It runs for Active Directory accounts too. Those hold no local password hash,
      // but an app password is local by definition, which is exactly what makes
      // second-factor policy possible for a directory account whose clients cannot
      // present a code.
      return ValidateAppPassword_(pAccount, sPassword);
   }

   bool
   PasswordValidator::ValidateAccountPassword_(std::shared_ptr<const Account> pAccount, const String &sPassword)
   {
      // Check if this is an active directory account.
      if (pAccount->GetIsAD())
      {
         String sADDomain = pAccount->GetADDomain();
         String sADUsername = pAccount->GetADUsername();

         // LDAP directory authentication, when an administrator has switched it on.
         //
         // Off by default and inert when off: GetEnabled reads a cached value and, when
         // it is false, this branch behaves exactly as it did before - LogonUser, same
         // arguments, same result. Nothing about a non-AD account is touched at all.
         //
         // When it IS on, an account already linked to Active Directory is validated by
         // an LDAP bind instead of by LogonUser, using the same AD domain and user name
         // that are already stored against it. That is deliberate: this is the same
         // population of accounts, authenticated against the same directory, by a
         // mechanism that does not require the mail server host to be domain-joined.
         // LogonUser does require that, and on a host in a workgroup it cannot even
         // report the difference between an unreachable domain and a wrong password -
         // every attempt returns ERROR_LOGON_FAILURE. See LdapDirectoryAuthenticator.h
         // for the measurement.
         if (LdapSettings::Instance()->GetEnabled())
         {
            LdapAuthenticationResult ldapResult = LdapDirectoryAuthenticator::ValidateUser(
               sADDomain, sADUsername, pAccount->GetAddress(), sPassword);

            if (ldapResult == LdapAuthenticationResult::ResultAccepted)
               return true;

            if (ldapResult == LdapAuthenticationResult::ResultRejected)
            {
               // The directory answered and said no. Falling through to LogonUser here
               // would be wrong twice over: it would double every failed attempt
               // against the directory - which is how a lockout policy is triggered by
               // a single wrong password - and on an unjoined host it would return the
               // same refusal anyway, just later.
               return false;
            }

            if (ldapResult == LdapAuthenticationResult::ResultUnavailable &&
                !LdapSettings::Instance()->GetConfiguration().fallback_to_windows_logon)
            {
               // The directory could not answer, and the reason has been reported
               // (throttled) by the authenticator. Refused rather than retried through
               // LogonUser, because on the deployment this feature exists for -
               // a mail server that is not domain-joined - LogonUser cannot succeed and
               // its failure would overwrite a precise diagnostic with an
               // indistinguishable one. FallbackToWindowsLogon=1 opts into the retry
               // for a domain-joined host that wants LDAP as its first choice only.
               return false;
            }

            // ResultNotConfigured, or an infrastructure failure with fallback enabled:
            // carry on to the existing path.
         }

         bool bUserOK = SSPIValidation::ValidateUser(sADDomain, sADUsername, sPassword);

         if(bUserOK)
            return true;
         else
            return false;
      }

      Crypt::EncryptionType iPasswordEncryption = (Crypt::EncryptionType) pAccount->GetPasswordEncryption();

      String sComparePassword = pAccount->GetPassword();

      // The clear text to re-hash if the stored password is upgraded below. It is the
      // password the *account* holds, which is not always the one the client sent: the
      // unencrypted and Blowfish comparisons below are case insensitive, so re-hashing
      // what the client sent would silently make a differently-cased password the new
      // correct one and lock out the user who types it the way they always have.
      String sPasswordToStore = sPassword;
      bool upgradeEligible = true;

      if (iPasswordEncryption == 0)
      {
         // Do plain text comparision

         // POTENTIAL TO BREAK BACKWARD COMPATIBILITY
         // Unencrypted passwords are not case sensitive so these changes WOULD fix that
         // but could cause problems for people who've been relying on them not being
         // case sensitive. Perhaps this needs to be optional before implementing.
         //
         // if (sPassword.Compare(sComparePassword) != 0)
         //   return false;
         //
         sComparePassword.MakeLower();
         if (sPassword.CompareNoCase(sComparePassword) != 0)
            return false;

         // Hash the stored password rather than the supplied one, which is exactly what
         // PersistentAccount::ReadObject already does when it re-hashes an unencrypted
         // password in memory.
         sPasswordToStore = pAccount->GetPassword();
      }
      else if (iPasswordEncryption == Crypt::ETMD5 ||
               iPasswordEncryption == Crypt::ETSHA256 ||
               iPasswordEncryption == Crypt::ETPBKDF2 ||
               iPasswordEncryption == Crypt::ETArgon2id)
      {
         // Compare hashs
         if (!Crypt::Instance()->Validate(sPassword, sComparePassword, iPasswordEncryption))
            return false;
      }
      else if (iPasswordEncryption == Crypt::ETBlowFish)
      {
         String decrypted = Crypt::Instance()->DeCrypt(sComparePassword, iPasswordEncryption);

         if (sPassword.CompareNoCase(decrypted) != 0)
            return false;

         // Deliberately not upgraded. A Blowfish password is compared case
         // insensitively, and unlike the unencrypted case that is still live
         // behaviour - nothing re-hashes it on read. Moving it to a hash would make the
         // comparison case sensitive and lock out anyone whose client sends the
         // password in a different case than it was stored in. That is a worse outcome
         // than leaving a reversibly-encrypted password in place, so these accounts
         // keep their scheme until the password is reset.
         upgradeEligible = false;
      }
      else
         return false;

      // The password is correct. This is the only point at which the clear text is
      // known to match the account, so it is the only point at which the stored hash
      // can be re-derived - and the only point at which a database write is justified.
      int effectiveEncryption = (int) iPasswordEncryption;
      if (upgradeEligible)
         effectiveEncryption = UpgradeStoredPasswordHash_(pAccount, sPasswordToStore, (int) iPasswordEncryption);

      // Hash-policy enforcement: an administrator can require that stored account
      // passwords use at least a given hash scheme (the Crypt::EncryptionType values
      // are ordered weakest-to-strongest). A login whose stored hash is still weaker
      // than the configured minimum after the upgrade attempt is refused -- even with
      // the correct password -- so such a password must be reset rather than continuing
      // to be accepted (and exposed in any database leak). The default of 0 (ETNone)
      // disables the policy. Active Directory accounts are exempt (handled above);
      // they hold no local hash.
      //
      // This check deliberately runs *after* verification and after the upgrade
      // attempt. Running it first, on the stored type, made the setting a permanent
      // lockout: the correct password was refused before it could be compared, and the
      // upgrade that would satisfy the policy can only ever happen on a successful
      // login.
      int minimumAcceptedHashAlgorithm = IniFileSettings::Instance()->GetMinimumAcceptedHashAlgorithm();
      if (minimumAcceptedHashAlgorithm > 0 && effectiveEncryption < minimumAcceptedHashAlgorithm)
      {
         String sMessage;
         sMessage.Format(_T("Authentication refused for account %s: the stored password hash type (%d) is weaker than the configured minimum (%d). The password must be reset."),
            pAccount->GetAddress().c_str(), effectiveEncryption, minimumAcceptedHashAlgorithm);
         LOG_APPLICATION(sMessage);
         return false;
      }

      return true;
   }

   namespace
   {
      boost::mutex unusable_app_password_mutex;
      std::set<__int64> reported_unusable_app_passwords;
   }

   // True the first time it is asked about a given row since the server started.
   bool
   PasswordValidator::ReportUnusableAppPasswordOnce_(__int64 appPasswordID)
   {
      boost::lock_guard<boost::mutex> guard(unusable_app_password_mutex);

      return reported_unusable_app_passwords.insert(appPasswordID).second;
   }

   bool
   PasswordValidator::ValidateAppPassword_(std::shared_ptr<const Account> pAccount, const String &sPassword)
   {
      // Nothing has ever issued one: answer without touching the database. This is
      // the case on essentially every server, so it is the case that has to be free.
      if (!PersistentAppPassword::AnyConfigured())
         return false;

      AppPasswords passwords;
      passwords.Refresh(pAccount->GetID());

      const int minimumAcceptedHashAlgorithm = IniFileSettings::Instance()->GetMinimumAcceptedHashAlgorithm();

      for (auto password : passwords.GetVector())
      {
         if (!password->GetActive())
            continue;

         Crypt::EncryptionType type = (Crypt::EncryptionType) password->GetEncryption();

         // A row whose scheme is not a password hash is refused rather than compared.
         // Nothing writes one - AppPassword::SetPassword forces a hashing scheme - so
         // reaching this means the row was edited outside the server, and a
         // clear-text or reversible comparison is not something to fall back to
         // quietly on an authentication path.
         if (type != Crypt::ETMD5 && type != Crypt::ETSHA256 &&
             type != Crypt::ETPBKDF2 && type != Crypt::ETArgon2id)
         {
            // Reported once per row per server start, not once per attempt. This runs
            // on a failed-authentication path, so an attempt-per-line message is a log
            // an attacker can fill at will with wrong guesses; the condition is a
            // property of the stored row and does not change between attempts, so
            // saying it once says all of it.
            if (ReportUnusableAppPasswordOnce_(password->GetID()))
            {
               String message;
               message.Format(_T("App password %d for account %s has an unusable hash type (%d) and is being ignored. It cannot have been written by this server; check whether the row was edited directly."),
                  (int) password->GetID(), pAccount->GetAddress().c_str(), (int) type);
               LOG_APPLICATION(message);
            }

            continue;
         }

         if (!Crypt::Instance()->Validate(sPassword, password->GetHash(), type))
            continue;

         // The same hash-policy floor the account password is held to: an app password
         // issued before the policy was raised must not become the weakly-hashed way in
         // that the policy exists to close.
         //
         // Checked AFTER the comparison, and that ordering is the whole point of the
         // log line. Checked before it, every wrong guess against an account holding
         // one such row would write a line - so a password spray would fill the
         // application log with a message about a credential nobody was using. Here it
         // is written only when somebody presented the CORRECT app password and was
         // refused anyway, which is precisely when an administrator needs to be told,
         // and never in response to an attacker's traffic.
         if (minimumAcceptedHashAlgorithm > 0 && (int) type < minimumAcceptedHashAlgorithm)
         {
            String message;
            message.Format(_T("App password %d for account %s is stored with hash type %d, weaker than the configured minimum (%d), and was refused even though it was correct. Delete it and issue a new one."),
               (int) password->GetID(), pAccount->GetAddress().c_str(), (int) type, minimumAcceptedHashAlgorithm);
            LOG_APPLICATION(message);
            continue;
         }

         // Recorded after the match, and throttled - see PersistentAppPassword.
         PersistentAppPassword::RecordUse(password);

         return true;
      }

      return false;
   }

   int
   PasswordValidator::UpgradeStoredPasswordHash_(std::shared_ptr<const Account> pAccount, const String &sPassword, int currentEncryption)
   {
      int preferredHashAlgorithm = IniFileSettings::Instance()->GetPreferredHashAlgorithm();

      // Only ever move to one of the real password-hashing schemes. ETNone and
      // ETBlowFish are recoverable rather than hashed, and ETDPAPI protects
      // machine-bound secrets (route and fetch account passwords), not account
      // passwords, so none of them is a legitimate target here.
      if (preferredHashAlgorithm < (int) Crypt::ETMD5 || preferredHashAlgorithm > (int) Crypt::ETArgon2id)
         return currentEncryption;

      // Never downgrade, checked before anything else and unconditionally.
      //
      // The pending-flag branch below deliberately bypasses the weaker-than test, so
      // this has to be its own guard rather than part of that test: a stale flag would
      // otherwise permit a downgrade. It can go stale because SaveObject can rewrite
      // the row to a stronger scheme while the flag is still set - so a clear-text
      // account whose password an administrator changes while PreferredHashAlgorithm
      // is Argon2id, followed by the preference being lowered back to PBKDF2, would
      // silently have its Argon2id hash rewritten as PBKDF2 on the next login, and log
      // a false "from type 0" line while doing it. The flag is now cleared in
      // SaveObject and DeleteObject too, but the invariant is cheap to state here and
      // must not depend on that.
      if (preferredHashAlgorithm < currentEncryption)
         return currentEncryption;

      // PersistentAccount::ReadObject re-hashes an unencrypted stored password into
      // the in-memory object, so an account whose row is still clear text arrives here
      // already claiming the preferred type. Those accounts are flagged as pending
      // instead of being detected by their type.
      bool storedIsUnencrypted = PersistentAccount::IsPasswordUpgradePending(pAccount->GetID());

      bool storedIsWeaker = preferredHashAlgorithm > currentEncryption;

      // The same scheme, derived with a lower work factor than the ini asks for now:
      // a cost raised after the hash was stored. Upward only, like the scheme itself -
      // a cost LOWERED later leaves stored hashes as they are and applies to new
      // ones, so an administrator who backs out of an expensive setting is not
      // rewarded with a rewrite of every password to something weaker. The stored
      // value is the hash text itself for every hashed scheme; ReadObject re-hashes
      // only the clear-text case, which the pending flag above accounts for.
      bool storedIsCheaper = !storedIsUnencrypted && !storedIsWeaker &&
                             preferredHashAlgorithm == currentEncryption &&
                             HashCreator::NeedsRehash(AnsiString(pAccount->GetPassword()));

      if (!storedIsUnencrypted && !storedIsWeaker && !storedIsCheaper)
         return currentEncryption;

      int storedEncryption = storedIsUnencrypted ? (int) Crypt::ETNone : currentEncryption;

      try
      {
         String upgradedHash = Crypt::Instance()->EnCrypt(sPassword, (Crypt::EncryptionType) preferredHashAlgorithm);

         if (upgradedHash.IsEmpty() ||
             !PersistentAccount::PersistUpgradedPassword(pAccount, upgradedHash, preferredHashAlgorithm))
         {
            // Log it, but let the login through on the hash the account already has:
            // refusing a correct password because we could not improve its storage
            // would lock the user out of their mail over a housekeeping task.
            //
            // Application log rather than ErrorManager, deliberately. This is on the
            // successful-authentication path, so an unwritable database would report
            // it on *every* login by *every* affected account - and DALConnection has
            // already reported the underlying SQL failure, so the ERROR log would
            // carry two entries per login for one root cause. It is also the shape
            // that breaks fixtures asserting a clean ERROR log.
            String sMessage;
            sMessage.Format(_T("Unable to save an upgraded password hash for account %s. The account keeps its existing password hash; authentication is unaffected and the upgrade is retried on the next logon."),
               pAccount->GetAddress().c_str());
            LOG_APPLICATION(sMessage);
            return currentEncryption;
         }
      }
      catch (...)
      {
         String sError;
         sError.Format(_T("An unknown error occurred while upgrading the password hash for account %s. The account keeps its existing password hash; authentication is unaffected."),
            pAccount->GetAddress().c_str());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5741, "PasswordValidator::UpgradeStoredPasswordHash_", sError);
         return currentEncryption;
      }

      String sMessage;
      if (storedIsCheaper)
         sMessage.Format(_T("Re-derived the stored password hash for account %s (type %d) under the configured work factor."),
            pAccount->GetAddress().c_str(), preferredHashAlgorithm);
      else
         sMessage.Format(_T("Upgraded the stored password hash for account %s from type %d to type %d."),
            pAccount->GetAddress().c_str(), storedEncryption, preferredHashAlgorithm);
      LOG_APPLICATION(sMessage);

      return preferredHashAlgorithm;
   }

}