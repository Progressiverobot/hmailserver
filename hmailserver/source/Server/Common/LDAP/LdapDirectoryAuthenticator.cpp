// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include "LdapDirectoryAuthenticator.h"

#include "LdapClient.h"
#include "LdapSettings.h"

#include "../Application/ErrorManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // An infrastructure or configuration failure repeats on every single
      // authentication attempt, so reporting it unthrottled turns one unreachable
      // domain controller into a log that fills a disk and buries the entry explaining
      // it. One report per distinct problem per window is enough to diagnose, and the
      // window is short enough that a continuing problem stays visible.
      //
      // "Distinct problem" is the hMailServer error code AND the LDAP result code AND
      // the directory endpoint - not any one of them alone. This is the same reasoning
      // SSPIValidation arrived at, and it is worth restating because the shape of the
      // bug it avoids is not obvious: keying on the error alone reports whichever
      // problem happened first and hides every other one for the whole window, and if
      // two alternate it hides one of them indefinitely. A server whose certificate is
      // untrusted AND whose service credential has expired has two problems with two
      // different fixes, and an administrator who is shown one at a time, in an order
      // that depends on which logon arrived first, cannot fix either with confidence.
      //
      // Times are stored rather than flags so that a problem which is fixed and later
      // returns is reported again, instead of being suppressed for the lifetime of the
      // process.
      bool ShouldReport_(int reportCode, unsigned long ldapError, const String &target)
      {
         const DWORD throttleMilliseconds = 60 * 1000;

         // Bounded deliberately. Part of the key comes from configuration, and an
         // unbounded map keyed on anything an administrator can change is a slow
         // memory leak. Beyond the cap the oldest entry is evicted, which costs an
         // extra report rather than correctness.
         const size_t maxTrackedProblems = 32;

         typedef std::pair<std::pair<int, unsigned long>, String> Key;

         static boost::mutex mutex;
         static std::map<Key, DWORD> lastReported;

         const DWORD now = GetTickCount();
         const Key key(std::pair<int, unsigned long>(reportCode, ldapError), target);

         boost::lock_guard<boost::mutex> guard(mutex);

         auto existing = lastReported.find(key);

         if (existing != lastReported.end())
         {
            // GetTickCount wraps every 49.7 days. Unsigned subtraction wraps with it,
            // so this stays correct across the wrap rather than going quiet for 49
            // days.
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

      // "host:port", the endpoint half of the throttle key and the thing an
      // administrator needs to see in the diagnostic.
      String DescribeTarget_(const LdapConfiguration &configuration)
      {
         return Formatter::Format("{0}:{1}", configuration.server, configuration.EffectivePort());
      }

      String DescribeTransport_(const LdapConfiguration &configuration)
      {
         switch (configuration.security)
         {
            case LdapTransportSecurity::TransportLdaps:    return _T("LDAPS");
            case LdapTransportSecurity::TransportStartTls: return _T("StartTLS");
            default:                                       return _T("unprotected LDAP");
         }
      }

      // Reports an infrastructure failure, once per minute per distinct problem, and
      // says which stage failed so that "the service credential is wrong" is not
      // confused with "this user's password is wrong".
      void ReportUnavailable_(const LdapConfiguration &configuration, const LdapClient &client,
         const String &stage)
      {
         const String target = DescribeTarget_(configuration);

         if (!ShouldReport_(5920, client.GetLastLdapError(), target))
            return;

         String message = Formatter::Format("LDAP authentication is failing for a reason unrelated to the "
            "credentials supplied. Stage: {0}. Directory: {1} over {2}. Reason: {3} (LDAP error {4}). Every "
            "account authenticated against this directory will be unable to log in until this is resolved. "
            "Further occurrences of this same error against this directory are suppressed for one minute.",
            stage, target, DescribeTransport_(configuration), client.DescribeLastError(),
            (int) client.GetLastLdapError());

         // The server's own diagnosticMessage, when it sent one. Appended rather than
         // substituted because it is often the only text that names the real cause,
         // and it is often absent.
         const String diagnostic = client.GetLastDiagnostic();

         if (!diagnostic.IsEmpty())
            message += Formatter::Format(" The directory said: {0}", diagnostic);

         ErrorManager::Instance()->ReportError(ErrorManager::High, 5920,
            "LdapDirectoryAuthenticator::ValidateUser", message);
      }

      // The username to look up. The account's Active Directory user name when it has
      // one, because that is the value the existing LogonUser path already uses and an
      // account that is linked to AD today must keep working without being edited.
      // Falling back to the local part of the mail address covers the account that was
      // flagged as directory-authenticated but never had a user name typed in.
      String ResolveDirectoryUsername_(const String &username, const String &accountAddress)
      {
         if (!username.IsEmpty())
            return username;

         return StringParser::ExtractAddress(accountAddress);
      }

      LdapOperationOutcome BindServiceCredential_(LdapClient &client, const LdapConfiguration &configuration)
      {
         // One implementation, on the client, because reading the directory as an
         // account source needs the same bind and two copies of "how do we read this
         // directory" would drift apart.
         //
         // BindService also honours BindMethod, which this copy did not - but that
         // makes NO difference on this path, and an earlier version of this comment
         // wrongly claimed it fixed something here. It cannot: the only caller is
         // inside `if (configuration.UsesSearch())`, and UsesSearch returns false
         // whenever bind_method is BindNegotiate, because a Negotiate bind identifies
         // the user by name and domain and never needs a DN to be found first. So the
         // Negotiate branch is unreachable from authentication by construction. It is
         // reached only by the directory enumeration, which searches regardless of how
         // users authenticate - and that is the case it was written for.
         return client.BindService(configuration);
      }
   }

   LdapAuthenticationResult
   LdapDirectoryAuthenticator::ValidateUser(const String &domain, const String &username,
      const String &accountAddress, const String &password)
   {
      try
      {
         const LdapConfiguration configuration = LdapSettings::Instance()->GetConfiguration();

         if (!configuration.enabled)
            return LdapAuthenticationResult::ResultNotConfigured;

         // Refused before a connection is opened. An empty password reaching a bind is
         // an authentication bypass rather than a failed login - see the comment in
         // LdapClient::BindSimple - so it is stopped here as well, where it costs
         // nothing at all.
         if (password.IsEmpty())
            return LdapAuthenticationResult::ResultRejected;

         String missing;

         if (!configuration.IsComplete(missing))
         {
            if (ShouldReport_(5922, 0, missing))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 5922,
                  "LdapDirectoryAuthenticator::ValidateUser",
                  Formatter::Format("LDAP authentication is enabled (hMailServer.ini [LDAP] Enabled=1) but is "
                     "not configured: {0} is not set. No LDAP authentication is being attempted, and accounts "
                     "linked to a directory continue to be validated the old way - through LogonUser, which "
                     "cannot work at all unless this computer is joined to the domain. Further occurrences are "
                     "suppressed for one minute.", missing));
            }

            // Deliberately NotConfigured rather than Unavailable. A half-finished
            // [LDAP] section must not lock everybody out of an installation that was
            // working before the section was added; the old path is no worse than it
            // was yesterday, and the ERROR entry above says what to finish.
            return LdapAuthenticationResult::ResultNotConfigured;
         }

         if (!configuration.PasswordIsProtected() && !configuration.allow_unprotected_password)
         {
            if (ShouldReport_(5921, 0, DescribeTarget_(configuration)))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::High, 5921,
                  "LdapDirectoryAuthenticator::ValidateUser",
                  Formatter::Format("LDAP authentication is enabled against {0} with no transport protection "
                     "(hMailServer.ini [LDAP] Security=0) and a bind method that transmits the password "
                     "(BindMethod=0). The password has NOT been sent and the logon has been refused. Fix this by "
                     "setting Security=2 (LDAPS, recommended) or Security=1 (StartTLS); or BindMethod=1, which "
                     "authenticates over SSPI without ever transmitting the password and satisfies a domain "
                     "controller that refuses unprotected simple binds. Setting AllowUnprotectedPassword=1 will "
                     "send the password in the clear instead, and should only ever be done on a network you "
                     "control end to end. Further occurrences are suppressed for one minute.",
                     DescribeTarget_(configuration)));
            }

            // Fails closed. The administrator asked for a configuration that cannot be
            // satisfied without putting a cleartext password on the network, and
            // guessing which half of the contradiction was meant is not this code's
            // decision to make.
            return LdapAuthenticationResult::ResultUnavailable;
         }

         const String directoryUsername = ResolveDirectoryUsername_(username, accountAddress);

         if (directoryUsername.IsEmpty())
         {
            // Nothing to look up. Not reported: an account with neither an AD user
            // name nor a usable address is a data problem on one account rather than a
            // directory failure, and reporting per attempt would be per logon.
            LOG_DEBUG(Formatter::Format("LDAP - account {0} is directory-authenticated but has no Active "
               "Directory user name and no local part to fall back to; refused.", accountAddress));

            return LdapAuthenticationResult::ResultRejected;
         }

         String userDn;

         if (configuration.UsesSearch())
         {
            LdapClient search;

            if (search.Connect(configuration) != LdapOperationOutcome::OutcomeSuccess)
            {
               ReportUnavailable_(configuration, search, _T("connecting in order to search for the user"));
               return LdapAuthenticationResult::ResultUnavailable;
            }

            if (BindServiceCredential_(search, configuration) != LdapOperationOutcome::OutcomeSuccess)
            {
               // A REJECTED service credential is an infrastructure failure, not a
               // rejected user: the password that was refused belongs to the
               // configuration, not to the person logging in. Mapping it the other way
               // would tell every user their password was wrong because one setting
               // was.
               ReportUnavailable_(configuration, search,
                  configuration.service_username.IsEmpty()
                     ? _T("binding anonymously to search for the user (set ServiceUsername)")
                     : _T("binding with the configured ServiceUsername"));

               return LdapAuthenticationResult::ResultUnavailable;
            }

            const String filter = LdapClient::ExpandTemplate(configuration.user_search_filter,
               directoryUsername, domain, accountAddress, LdapClient::TemplateEscaping::EscapeForFilter);

            int matchCount = 0;

            if (search.FindUserDn(configuration, filter, userDn, matchCount) != LdapOperationOutcome::OutcomeSuccess)
            {
               ReportUnavailable_(configuration, search, _T("searching for the user"));
               return LdapAuthenticationResult::ResultUnavailable;
            }

            if (matchCount == 0)
            {
               // A username that is not in the directory is a rejection, not an
               // outage, and must stay at debug level: it is a normal event on any
               // internet-facing server and the failed-logon machinery elsewhere
               // already counts and blocks these.
               LOG_DEBUG(Formatter::Format("LDAP - no directory entry matched the search for account {0} "
                  "(filter {1}); refused.", accountAddress, filter));

               return LdapAuthenticationResult::ResultRejected;
            }

            if (matchCount > 1)
            {
               if (ShouldReport_(5923, 0, DescribeTarget_(configuration)))
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::High, 5923,
                     "LdapDirectoryAuthenticator::ValidateUser",
                     Formatter::Format("The LDAP user search matched more than one directory entry for account "
                        "{0} (filter {1}, base {2}). No authentication has been attempted: binding as one of "
                        "several matches would authenticate a user against an entry that may not be theirs. "
                        "Make UserSearchFilter or SearchBase select exactly one entry. Further occurrences are "
                        "suppressed for one minute.", accountAddress, filter, configuration.search_base));
               }

               return LdapAuthenticationResult::ResultUnavailable;
            }

            // The search connection is closed before the user's password is used, so
            // the bind that proves the password never shares a connection with the
            // service credential's identity.
            search.Disconnect();
         }
         else if (configuration.bind_method == LdapBindMethod::BindSimple)
         {
            userDn = LdapClient::ExpandTemplate(configuration.user_dn_template, directoryUsername,
               domain, accountAddress, LdapClient::TemplateEscaping::EscapeForDistinguishedName);
         }

         LdapClient user;

         if (user.Connect(configuration) != LdapOperationOutcome::OutcomeSuccess)
         {
            ReportUnavailable_(configuration, user, _T("connecting in order to authenticate the user"));
            return LdapAuthenticationResult::ResultUnavailable;
         }

         const LdapOperationOutcome outcome = configuration.bind_method == LdapBindMethod::BindNegotiate
            ? user.BindNegotiate(directoryUsername, domain, password)
            : user.BindSimple(userDn, password);

         if (outcome == LdapOperationOutcome::OutcomeSuccess)
         {
            LOG_DEBUG(Formatter::Format("LDAP - account {0} authenticated against {1} as {2}.",
               accountAddress, DescribeTarget_(configuration),
               configuration.bind_method == LdapBindMethod::BindNegotiate ? directoryUsername : userDn));

            return LdapAuthenticationResult::ResultAccepted;
         }

         if (outcome == LdapOperationOutcome::OutcomeRejected)
         {
            // Debug level on purpose: this fires for every wrong password on an
            // internet-facing server. What it adds is the REASON, which the client is
            // never told and which is the difference between "the user typed it wrong"
            // and "the account is locked out and no amount of retyping will help".
            // Active Directory only ever puts that reason in the diagnosticMessage.
            String reason = LdapClient::DescribeActiveDirectorySubStatus(user.GetLastDiagnostic());

            if (reason.IsEmpty())
               reason = user.DescribeLastError();

            LOG_DEBUG(Formatter::Format("LDAP - the directory refused the credentials for account {0}: {1} "
               "(LDAP error {2}).", accountAddress, reason, (int) user.GetLastLdapError()));

            return LdapAuthenticationResult::ResultRejected;
         }

         ReportUnavailable_(configuration, user, _T("binding as the user"));

         return LdapAuthenticationResult::ResultUnavailable;
      }
      catch (...)
      {
         // The barrier. Nothing above should throw, but this runs on a protocol
         // connection thread inside a logon, and an exception escaping into that path
         // would abort the command rather than refuse the login. Reported once per
         // minute so a repeating fault is visible without becoming the fault.
         if (ShouldReport_(5924, 0, _T("")))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5924,
               "LdapDirectoryAuthenticator::ValidateUser",
               _T("An unexpected error occurred during LDAP authentication. The logon has been treated as an ")
               _T("infrastructure failure rather than as a rejection. Further occurrences are suppressed for ")
               _T("one minute."));
         }

         return LdapAuthenticationResult::ResultUnavailable;
      }
   }
}
