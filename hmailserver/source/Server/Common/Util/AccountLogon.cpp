// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "AccountLogon.h"
#include "AccountLockout.h"
#include "PasswordValidator.h"
#include "../Persistence/PersistentLogonFailure.h"
#include "../Persistence/PersistentAccount.h"
#include "../Persistence/PersistentSecurityRange.h"

#include "../BO/SecurityRange.h"
#include "../BO/Account.h"
#include "VariantDateTime.h"
#include "Time.h"
#include "GUIDCreator.h"
#include "PasswordGenerator.h"
#include "ServerStatus.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   boost::recursive_mutex AccountLogon::ip_range_creation_mutex_;

   AccountLogon::AccountLogon(void)
   {
   }

   AccountLogon::~AccountLogon(void)
   {
   }

   std::shared_ptr<const Account>
   AccountLogon::Logon(const IPAddress &ipaddress, const String &username, const String &password, bool &disconnect)
   {
	   return Logon(ipaddress, _T(""), username, password, disconnect);
   }

   std::shared_ptr<const Account>
   AccountLogon::Logon(const IPAddress &ipaddress, const String &masqname, const String &username, const String &password, bool &disconnect)
   {
      disconnect = false;

      // Per-name lockout, checked BEFORE the password: a locked name is refused
      // without spending an Argon2id verification on it, and the refusal is the
      // ordinary invalid-credentials reply (see AccountLockout.h for what timing
      // still reveals).
      //
      // The refusal is deliberately NOT reported to RegisterFailedLogin, so it
      // reaches neither the per-name counters nor the per-IP auto-ban.
      //
      // The per-IP half is the one that matters, and it is a real hazard rather
      // than tidiness. Once a name is locked, the person most likely to keep
      // trying is its owner, with the CORRECT password - and charging those
      // attempts to their address is how a lockout caused by a botnet gets an
      // innocent office NAT, or the loopback address of a co-hosted webmail
      // front end, banned from SMTP, IMAP and POP3 for everybody behind it by a
      // priority-100 deny-everything range. An attack on one mailbox must not
      // become an outage for unrelated users at the victim's address.
      //
      // Nothing is lost in the other direction: an attacker who reaches this
      // branch has already been charged for the failures that caused the lock,
      // and further attempts against a name that can no longer be verified tell
      // them nothing, so declining to count them removes no pressure that was
      // doing any work. The per-connection attempt caps and
      // OnAuthenticationFailed still see every attempt.
      if (AccountLockout::Instance()->IsLockedOut(username))
      {
         ServerStatus::Instance()->OnAuthenticationFailed();

         std::shared_ptr<Account> empty;
         return empty;
      }

      std::shared_ptr<const Account> account = PasswordValidator::ValidatePassword(masqname, username, password);
      if (account)
      {
         PersistentAccount::UpdateLastLogonTime(account);
         AccountLockout::Instance()->RecordSuccess(username);
         ServerStatus::Instance()->OnAuthenticationSucceeded();
         return account;
      }

      ServerStatus::Instance()->OnAuthenticationFailed();

      RegisterFailedLogin(ipaddress, username, disconnect);

      std::shared_ptr<Account> empty;
      return empty;
   }

   void
   AccountLogon::RegisterFailedLogin(const IPAddress &ipaddress, const String &username, bool &disconnect,
                                     bool countTowardsAccountLockout)
   {
      disconnect = false;

      // The per-name half, independent of the per-IP auto-ban below and of its
      // enablement: each closes a hole the other cannot see - one address
      // spraying many names, and many addresses converging on one name. See the
      // header for the failures that deliberately do not count.
      if (countTowardsAccountLockout)
         AccountLockout::Instance()->RecordFailure(username);

      // is auto banning enabled?
      int maxInvalidLogonAttempts = Configuration::Instance()->GetMaxInvalidLogonAttempts();

      if (Configuration::Instance()->GetAutoBanLogonEnabled() == false || maxInvalidLogonAttempts == 0)
         return;

      // Log on has failed.
      PersistentLogonFailure logonFailure;
      int failureCount = logonFailure.GetCurrrentFailureCount(ipaddress) + 1; // +1 because we count the current failure as well.

      if (failureCount >= maxInvalidLogonAttempts)
      {
         // Unchecked. Failing here leaves this address's spent failures in the table
         // after the ban has been created, so when the ban expires the next single
         // failure re-crosses the threshold and bans it again. That errs towards
         // blocking rather than allowing, which is why it is reported rather than
         // treated as fatal - but an address that cannot get back in after its ban
         // expires is a support call, and this is the only place that would explain it.
         if (!logonFailure.ClearFailuresByIP(ipaddress))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6113, "AccountLogon::RegisterFailedLogin",
               "The spent logon failures for a banned address could not be cleared, so it may be re-banned immediately after this ban expires.");
         }

         int minutes = Configuration::Instance()->GetAutoBanMinutes();
         if (minutes == 0)
         {
            // disconnect, but don't block.
            disconnect = true;
            return;
         }

         CreateIPRange(ipaddress, username, minutes);

         disconnect = true;
         return;
      }

      // logon failed, add new failure.
      //
      // This is the counting half of auto-ban, and its result was discarded - the same
      // defect as the range save in CreateIPRange below it, one method name away from
      // the sweep that found that one, because this class does not call its writers
      // SaveObject.
      //
      // It is the more serious half. If the failure cannot be recorded the count never
      // rises, GetCurrrentFailureCount keeps returning the same number, the threshold
      // is never crossed and no ban is ever created - so an attacker guesses passwords
      // indefinitely against a server whose brute-force protection is switched on,
      // configured, and silently doing nothing at all.
      if (!logonFailure.AddFailure(ipaddress))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6114, "AccountLogon::RegisterFailedLogin",
            "A failed logon could not be recorded, so it does not count towards the auto-ban threshold. While this persists, repeated logon failures from any address will never trigger a ban.");
      }
   }

   void
   AccountLogon::CreateIPRange(const IPAddress &ipaddress, const String &username, int minutes)
   {
      // Why do we need a lock here? IP ranges requires unique names.
      // To prevent conflicts, we need to auto-generate names. We could
      // create random names, but that would be ugly. Instead, we create
      // names containing just an index.
      //
      // The problem is if two clients are banned at the same time. There
      // is a risk that we create two IP ranges with the same name, which
      // will result in a DB error. To prevent this, we use locking to
      // ensure that only one IP range is created at a time.

      boost::lock_guard<boost::recursive_mutex> guard(ip_range_creation_mutex_);

      DateTime dt = DateTime::GetCurrentTime();         
      DateTimeSpan span;
      span.SetDateTimeSpan(0,0,minutes,0);
      dt = dt + span;


      std::shared_ptr<SecurityRange> pSecurityRange = std::shared_ptr<SecurityRange>(new SecurityRange);
      pSecurityRange->SetName(GetIPRangeName_(username));
      pSecurityRange->SetPriority(100);
      pSecurityRange->SetLowerIP(ipaddress);
      pSecurityRange->SetUpperIP(ipaddress);
      pSecurityRange->SetExpires(true);
      pSecurityRange->SetExpiresTime(dt);

      // Auto-ban is a security control, and this is the line that applies it. Its
      // result was discarded, so a failure here meant the range was never created,
      // the address was never blocked, and nothing anywhere said so - the control
      // failed open and silently, which is the combination that makes a control
      // worth nothing. There is no caller to return to and nobody to tell but the
      // administrator, so it is reported.
      if (!PersistentSecurityRange::SaveObject(pSecurityRange))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6099, "AccountLogon::CreateIPRange",
            "The auto-ban IP range '" + pSecurityRange->GetName() + "' could not be saved, so the address has NOT been blocked despite reaching the failed-logon limit.");
      }


   }      

   String 
   AccountLogon::GetIPRangeName_(const String &username)
   {
      String suggestion = "Auto-ban: " + username;
      if (!PersistentSecurityRange::Exists(suggestion))
         return suggestion;

      for (int i = 1; i <= 10; i++)
      {
         suggestion = "Auto-ban: " + username + " (" + StringParser::IntToString(i)+ ")";
         if (!PersistentSecurityRange::Exists(suggestion))
            return suggestion;
      }

      for (int i = 1; i <= 10; i++)
      {
         String pass = PasswordGenerator::Generate();
         pass = pass.Mid(0,9);
         suggestion = "Auto-ban: " + username + " (Z" + pass + ")";
         if (!PersistentSecurityRange::Exists(suggestion))
            return suggestion;
      }

      // okay. we failed finding a unique password. not very likely.
      suggestion = "Auto-ban: " + username + " " + GUIDCreator::GetGUID() + "";
      return suggestion;
   }

}