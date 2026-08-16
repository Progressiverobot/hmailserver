// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include "SSPIValidation.h"

// NetGetJoinInformation, and the two constants that make its answer meaningful. Not
// pulled in by the precompiled header, and asked for explicitly rather than via
// <windows.h> so that the one function this file needs from netapi32 is visible at the
// top of the file it is needed in. Netapi32.lib is in the project's link line.
#include <lm.h>

#include "../Application/ErrorManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Whether a LogonUser failure means "this user did not prove who they are" or
      // "the directory could not answer". The distinction matters operationally: the
      // first is a normal event on any internet-facing server and must not be logged
      // per attempt, while the second means every Active Directory account on the
      // system is failing for a reason that has nothing to do with the users - a
      // domain controller that is down, a broken machine trust, DNS that stopped
      // resolving the domain. Before this, both produced the same silent false, so an
      // outage was indistinguishable from a wave of wrong passwords.
      bool IsCredentialRejection_(DWORD error)
      {
         switch (error)
         {
            case ERROR_LOGON_FAILURE:          // the actual wrong-password case
            case ERROR_NO_SUCH_USER:
            case ERROR_ACCOUNT_DISABLED:
            case ERROR_ACCOUNT_EXPIRED:
            case ERROR_ACCOUNT_LOCKED_OUT:
            case ERROR_PASSWORD_EXPIRED:
            case ERROR_PASSWORD_MUST_CHANGE:
            case ERROR_ACCOUNT_RESTRICTION:
            case ERROR_INVALID_LOGON_HOURS:
            case ERROR_INVALID_WORKSTATION:
            case ERROR_LOGON_TYPE_NOT_GRANTED:
               return true;
            default:
               return false;
         }
      }

      String DescribeLogonFailure_(DWORD error)
      {
         switch (error)
         {
            case ERROR_LOGON_FAILURE:          return "the user name or password is not correct";
            case ERROR_NO_SUCH_USER:           return "the user does not exist in that domain";
            case ERROR_ACCOUNT_DISABLED:       return "the account is disabled";
            case ERROR_ACCOUNT_EXPIRED:        return "the account has expired";
            case ERROR_ACCOUNT_LOCKED_OUT:     return "the account is locked out";
            case ERROR_PASSWORD_EXPIRED:       return "the password has expired";
            case ERROR_PASSWORD_MUST_CHANGE:   return "the password must be changed before the next logon";
            case ERROR_ACCOUNT_RESTRICTION:    return "an account restriction prevents this logon";
            case ERROR_INVALID_LOGON_HOURS:    return "logon is not permitted at this time of day";
            case ERROR_INVALID_WORKSTATION:    return "logon from this computer is not permitted";
            case ERROR_LOGON_TYPE_NOT_GRANTED: return "the account may not log on over the network";
            case ERROR_NO_LOGON_SERVERS:       return "no domain controller could be contacted";
            case ERROR_NETLOGON_NOT_STARTED:   return "the Net Logon service is not running";
            case ERROR_TRUSTED_RELATIONSHIP_FAILURE:
            case ERROR_TRUSTED_DOMAIN_FAILURE: return "the trust relationship with the domain has failed";
            case ERROR_DOWNGRADE_DETECTED:     return "the domain could not be contacted (downgrade detected)";
            case RPC_S_SERVER_UNAVAILABLE:     return "the domain controller did not answer (RPC unavailable)";
            case ERROR_NOT_ENOUGH_MEMORY:      return "the server is out of memory";
            case ERROR_PRIVILEGE_NOT_HELD:     return "the hMailServer service account lacks the privilege to validate logons";
            default:                           return "an unexpected Windows error";
         }
      }

      // The Windows NetBIOS computer name, which is NOT what Utilities::ComputerName()
      // returns and is the reason this exists.
      //
      // Utilities::ComputerName() returns the configured HostName setting when one is
      // set, falling back to the machine name only when it is empty - so on this
      // development machine it returns "examplify.com" while the computer is called
      // "WORK-01". That is right for its callers, which build Message-IDs and SMTP
      // greetings, and wrong here in both directions: the diagnostic below would tell an
      // administrator to type a mail hostname where Windows expects a computer name, and
      // the comparison that decides whether an account targets local Windows accounts
      // would not recognise the correct value - so a properly configured local account
      // would be reported as a broken domain account.
      //
      // Caught by reading the first diagnostic this code produced, which is an argument
      // for making diagnostics quote their inputs.
      const String &LocalComputerName_()
      {
         static String cached;
         static std::once_flag once;

         std::call_once(once, []()
         {
            wchar_t buffer[MAX_COMPUTERNAME_LENGTH + 1] = {};
            DWORD size = MAX_COMPUTERNAME_LENGTH + 1;

            if (GetComputerName(buffer, &size))
               cached = buffer;
         });

         return cached;
      }

      // Whether this computer is joined to a Windows domain, cached for the life of the
      // process because joining or leaving one requires a reboot.
      //
      // This exists because of a measurement, not a theory. LogonUser was called on this
      // machine - a workgroup member - with four different domain values: a name that
      // cannot exist, a name that could exist but does not, the real name of a domain
      // controller sitting on the same subnet and answering LDAP, and the machine's own
      // name with a user that does not exist. All four returned the identical
      // ERROR_LOGON_FAILURE (1326). Windows will not tell an unjoined computer whether a
      // domain exists, because saying so would be an information leak, and the effect for
      // hMailServer is that a mail server in a workgroup can never authenticate an Active
      // Directory account and can never say why: every attempt looks exactly like a wrong
      // password, forever, with no error anywhere.
      //
      // So the "why" has to be established locally. NetGetJoinInformation answers it
      // without touching the network, and the answer is the one an administrator actually
      // needs to hear.
      bool IsComputerDomainJoined_()
      {
         static std::atomic<int> cached(-1);

         const int previous = cached.load();
         if (previous != -1)
            return previous != 0;

         bool joined = false;

         LPWSTR name = nullptr;
         NETSETUP_JOIN_STATUS status = NetSetupUnknownStatus;

         const NET_API_STATUS queryResult = NetGetJoinInformation(nullptr, &name, &status);

         if (queryResult == NERR_Success)
         {
            joined = status == NetSetupDomainName;

            // Logged once per process, because when the diagnostic below does not appear
            // and an administrator expects it, this is the line that says why. The status
            // values are 0 unknown, 1 unjoined, 2 workgroup, 3 domain.
            LOG_DEBUG(Formatter::Format("NetGetJoinInformation: this computer's join status is {0} ({1}), "
               "name '{2}'", (int) status, joined ? _T("in a domain") : _T("not in a domain"),
               name != nullptr ? String(name) : String(_T(""))));

            if (name != nullptr)
               NetApiBufferFree(name);
         }
         else
         {
            LOG_DEBUG(Formatter::Format("NetGetJoinInformation failed with {0}; assuming this computer is "
               "domain-joined so that no misleading diagnostic is produced.", (int) queryResult));

            // If the question cannot be answered, assume joined. The only thing this
            // gate does is add an explanatory error, and an unnecessary silence is a
            // great deal better than telling a properly joined server that it is not.
            joined = true;
         }

         cached.store(joined ? 1 : 0);

         return joined;
      }

      // An infrastructure or configuration failure repeats on every single authentication
      // attempt, so reporting it unthrottled turns one dead domain controller into a log
      // that fills a disk and buries the entry explaining it. One report per distinct
      // problem per window is enough to diagnose, and the window is short enough that a
      // continuing problem stays visible.
      //
      // "Distinct problem" is the Windows error AND the domain it happened against, not
      // the error alone. A server with two linked domains, one unreachable and one
      // misconfigured, has two problems with two different fixes; keying on the error
      // code alone reports whichever failed first and hides the other for a minute - and
      // if they alternate, hides one of them indefinitely. This was found by a test that
      // provoked one domain and then another inside the same minute and saw nothing the
      // second time.
      //
      // Times are stored rather than flags so that a problem which is fixed and later
      // returns is reported again, instead of being suppressed for the lifetime of the
      // process.
      bool ShouldReport_(DWORD error, const String &domain)
      {
         const DWORD throttleMilliseconds = 60 * 1000;

         // Bounded deliberately. The key includes a domain name that ultimately comes
         // from configuration, and an unbounded map keyed on anything an administrator
         // can add rows of is a slow memory leak. Thirty-two distinct linked domains is
         // far past any real deployment; beyond that the oldest entry is evicted, which
         // costs an extra report rather than correctness.
         const size_t maxTrackedProblems = 32;

         typedef std::pair<DWORD, String> Key;

         static boost::mutex mutex;
         static std::map<Key, DWORD> lastReported;

         const DWORD now = GetTickCount();
         const Key key(error, domain);

         boost::lock_guard<boost::mutex> guard(mutex);

         auto existing = lastReported.find(key);

         if (existing != lastReported.end())
         {
            // GetTickCount wraps every 49.7 days. Unsigned subtraction wraps with it, so
            // this stays correct across the wrap rather than going quiet for 49 days.
            if (now - existing->second < throttleMilliseconds)
               return false;

            existing->second = now;
            return true;
         }

         if (lastReported.size() >= maxTrackedProblems)
         {
            auto oldest = lastReported.begin();

            for (auto candidate = lastReported.begin(); candidate != lastReported.end(); ++candidate)
            {
               if (now - candidate->second > now - oldest->second)
                  oldest = candidate;
            }

            lastReported.erase(oldest);
         }

         lastReported[key] = now;
         return true;
      }
   }

   bool
   SSPIValidation::ValidateUser(const String &sDomain, const String &sUsername, const String &sPassword)
   {
      // Initialised, and that is the whole point of this line. It used to be an
      // uninitialised HANDLE with CloseHandle called on it unconditionally - after a
      // FAILED LogonUser as well as a successful one. LogonUser does not write the out
      // parameter when it fails, so every rejected Active Directory login handed
      // CloseHandle whatever bytes happened to be on the stack.
      //
      // That is not a theoretical defect. It is reachable by anyone who can attempt a
      // login on an AD-linked account - so by anyone on the internet, on any of SMTP,
      // POP3 and IMAP, simply by getting a password wrong. The two outcomes are both
      // bad: a stale stack value that happens to be a live handle number closes an
      // unrelated handle belonging to another thread, which is a socket, a log file or
      // a database connection being yanked out from under whoever owned it - a
      // use-after-close whose crash lands somewhere unrelated and cannot be traced
      // back to here. Or, under a debugger or with handle verification enabled, it
      // raises STATUS_INVALID_HANDLE and takes the process down.
      HANDLE token = nullptr;

      if (LogonUser(sUsername, sDomain, sPassword, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, &token))
      {
         // Documented to be set on success, but checked anyway: the cost of a compare
         // is nothing next to the cost of the bug above, and a provider that succeeds
         // without producing a token would otherwise repeat it.
         if (token != nullptr && token != INVALID_HANDLE_VALUE)
            CloseHandle(token);

         return true;
      }

      const DWORD error = GetLastError();

      if (IsCredentialRejection_(error))
      {
         // Deliberately debug-level: on an internet-facing server this fires for every
         // wrong password, and there is a dedicated failed-logon path elsewhere that
         // already counts and blocks them. What this adds is the reason, which the
         // client is never told and which is the difference between "the user typed it
         // wrong" and "the account is locked and no amount of retyping will help".
         LOG_DEBUG(Formatter::Format("Active Directory logon for {0}\\{1} was refused: {2} (Windows error {3})",
            sDomain, sUsername, DescribeLogonFailure_(error), (int) error));

         // A refusal on a computer that is not in a domain, against something other than
         // this computer's own accounts, cannot be a wrong password: it is a
         // configuration that can never work, and Windows is incapable of saying so. This
         // is reported rather than logged at debug level because the administrator who
         // linked the account is the only person who can fix it, and the user who cannot
         // collect their mail will otherwise be told to check a password that was never
         // the problem.
         //
         // The local-machine case is excluded deliberately and is not a special case
         // being papered over: with the domain set to this computer's name, LogonUser
         // validates a LOCAL Windows account, which works perfectly well in a workgroup
         // and is a legitimate way to run this. Only a domain this computer has no way to
         // reach is worth complaining about.
         const bool targetsThisComputer =
            sDomain.IsEmpty() ||
            sDomain.CompareNoCase(LocalComputerName_()) == 0 ||
            sDomain == _T(".");

         if (!targetsThisComputer && !IsComputerDomainJoined_() && ShouldReport_(error, sDomain))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5911, "SSPIValidation::ValidateUser",
               Formatter::Format("An account is linked to the Active Directory domain {0}, but this computer is not "
                  "joined to any domain. Windows cannot validate a domain account from a workgroup computer, and it "
                  "reports the attempt as an incorrect password rather than as a configuration problem - so every "
                  "logon for every account linked to this domain will fail with no other explanation. Either join "
                  "this computer to {0}, or set the account's Active Directory domain to this computer's name ({1}) "
                  "to validate against a local Windows account instead. Further occurrences are suppressed for one "
                  "minute.", sDomain, LocalComputerName_()));
         }

         return false;
      }

      if (ShouldReport_(error, sDomain))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5910, "SSPIValidation::ValidateUser",
            Formatter::Format("Active Directory authentication is failing for a reason unrelated to the credentials "
               "supplied: {0} (Windows error {1}, domain {2}). Every account linked to this domain will be unable to "
               "log in until this is resolved. Further occurrences of this same error are suppressed for one minute.",
               DescribeLogonFailure_(error), (int) error, sDomain));
      }

      return false;
   }
}
