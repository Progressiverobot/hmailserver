// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include ".\passwordvalidator.h"

#include "../Application/ObjectCache.h"
#include "../Application/DefaultDomain.h"
#include "../Application/IniFileSettings.h"
#include "../Application/Logger.h"
#include "../Cache/CacheContainer.h"
#include "../BO/Account.h"
#include "../BO/Domain.h"
#include "../BO/DomainAliases.h"
#include "../Persistence/PersistentAccount.h"
#include "../LDAP/LdapDirectoryAuthenticator.h"
#include "../LDAP/LdapSettings.h"
#include "../Util/SSPIValidation.h"
#include "../Util/Crypt.h"
#include "../Scripting/Result.h"
#include "../Scripting/Events.h"

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
   PasswordValidator::ValidatePassword(std::shared_ptr<const Account> pAccount, const String &sPassword)
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

      if (!storedIsUnencrypted && !storedIsWeaker)
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
      sMessage.Format(_T("Upgraded the stored password hash for account %s from type %d to type %d."),
         pAccount->GetAddress().c_str(), storedEncryption, preferredHashAlgorithm);
      LOG_APPLICATION(sMessage);

      return preferredHashAlgorithm;
   }

}