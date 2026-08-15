// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"
#include "DirectorySync.h"

#include "LdapClient.h"
#include "LdapSettings.h"

#include "../BO/Account.h"
#include "../BO/Accounts.h"
#include "../BO/Domain.h"
#include "../BO/Domains.h"

#include "../Persistence/PersistenceMode.h"
#include "../Persistence/PersistentAccount.h"

#include "../Application/ErrorManager.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // One run at a time, for the whole process.
      //
      // Two overlapping runs would each read the accounts, each conclude that the same
      // absent account has to be created, and the second would fail on the duplicate -
      // which is survivable. What is not survivable is the pair where one run is
      // disabling accounts the other has just created, and neither report describes the
      // state anything ended up in. A synchronisation is an administrative action that
      // happens a few times a day at most, so serialising it costs nothing; a caller
      // that arrives second is told so rather than queued, because a COM caller waiting
      // an unbounded time on a directory read looks like a hang.
      boost::mutex run_mutex;

      // What an account should look like once the directory has been applied to it.
      // Assembled by the planner, carried in the report, and re-applied by the applier -
      // so the values that are written are literally the values that were previewed.
      struct DesiredAccountState
      {
         String directory_domain;
         String directory_username;
         String first_name;
         String last_name;
      };

      // One hMailServer domain, and everything the planner needs to know about it,
      // worked out once rather than once per directory entry.
      struct DomainScope
      {
         __int64 id = 0;
         String name;               // lower-cased, the map key
         String directory_domain;   // the domain's Active Directory domain name

         // Whether accounts may be provisioned into it. When false, exclusion_reason
         // says why, and every directory entry landing here is skipped with that reason
         // rather than with a generic one.
         bool in_scope = false;
         DirectorySyncSkipReason exclusion_reason = DirectorySyncSkipReason::ReasonNone;

         // The domain's size policy, carried here because a created account has to
         // satisfy it and the planner has to be able to say so BEFORE apply. A domain
         // with a per-account maximum refuses any account that has no size of its own,
         // so a preview that did not look at this would promise four hundred creates
         // and produce four hundred refusals.
         int max_account_size = 0;
         int max_size_mb = 0;

         // Existing accounts, keyed on the lower-cased address.
         std::map<String, std::shared_ptr<Account> > accounts;

         // The same accounts, keyed on their lower-cased Active Directory user name.
         //
         // Exists so that a directory entry which cannot be turned into an ADDRESS can
         // still be recognised as a PERSON. An entry whose mail attribute is missing or
         // malformed is skipped - correctly, there is no mailbox to create - but the
         // person is plainly still in the directory, and without this index their
         // account would be absent from `seen` and would be disabled by the pass below.
         // One typo in one directory attribute would take a working mailbox offline.
         std::map<String, String> accounts_by_directory_username;

         // Lower-cased addresses this run saw in the directory for this domain. An
         // account that is not in here is a candidate for being disabled - so entries
         // that were seen and then SKIPPED are added too. A person whose entry is
         // present but unusable has not left the company, and disabling their mailbox
         // because their sAMAccountName is missing would be a fresh outage caused by a
         // data quality problem.
         std::set<String> seen;
      };

      // Reports something that stopped the whole run, or that limited it in a way an
      // administrator has to know about.
      //
      // Deliberately not throttled, unlike LdapDirectoryAuthenticator's reporting. That
      // code runs on every logon and a repeating fault there would fill a disk; an apply
      // happens when somebody asks for it or when a schedule fires, so one entry per run
      // is one entry per action, and suppressing it would hide the run somebody is
      // looking at right now.
      //
      // A PREVIEW writes nothing here, and that is the point of the mode argument. A
      // preview changes nothing and hands its reason straight back to the caller, who is
      // by definition sitting there reading it - so an ERROR-severity entry adds no
      // information and costs something real. An administrator diagnosing a search base
      // will press the button five or six times in a minute, and a log that gains an
      // error every time it is pressed is a log people stop reading, which is how the
      // one entry that mattered gets missed. An apply is different in kind: it changed
      // state, or tried to, and that belongs in the record whether or not anybody was
      // watching.
      void Report_(DirectorySyncMode mode, int code, const String &source, const String &message)
      {
         if (mode != DirectorySyncMode::ModeApply)
            return;

         ErrorManager::Instance()->ReportError(ErrorManager::High, code, source, message);
      }

      // Adds a finished row and moves the counter that belongs to it.
      //
      // Every row goes through here, so the counts cannot disagree with the list. That
      // sounds like a small thing; it is the difference between a report that says
      // "12 created" over a list of 11 and a report that can be trusted enough to act on.
      void Append_(DirectorySyncResult &result, const DirectorySyncEntry &entry)
      {
         switch (entry.action)
         {
            case DirectorySyncAction::ActionCreate:        result.to_create++;  break;
            case DirectorySyncAction::ActionUpdate:        result.to_update++;  break;
            case DirectorySyncAction::ActionUnchanged:     result.unchanged++;  break;
            case DirectorySyncAction::ActionDisable:       result.to_disable++; break;
            case DirectorySyncAction::ActionReportMissing: result.missing++;    break;
            case DirectorySyncAction::ActionSkip:
               result.skipped++;
               result.skip_reasons[entry.reason]++;
               break;
         }

         result.entries.push_back(entry);
      }

      // The one form every address and domain name is compared, keyed and stored in.
      //
      // Lower-cased, and that is a decision rather than tidiness. An account address
      // becomes part of the message store's directory names and is compared against
      // whatever a client types at a logon prompt; keeping one canonical form is what
      // stops "J.Smith@example.com" and "j.smith@example.com" from becoming two accounts
      // that an administrator sees as one. Trimmed because a directory attribute that
      // was edited by hand quite often has a trailing space, and an address with one is
      // an address nothing will ever match.
      String Normalize_(const String &value)
      {
         String address = value;

         address.Trim();

         return address.ToLower();
      }

      // Whether a directory-domain name is one SSPIValidation would read as "this
      // computer" rather than as a domain.
      //
      // The three forms are taken from SSPIValidation::ValidateUser, which treats an
      // empty domain, "." and the local computer's name as identical - all of them mean
      // "validate a LOCAL Windows account". That is a legitimate way to configure an
      // individual account by hand, and it is NOT a legitimate thing to provision a
      // whole domain's worth of accounts into from a directory: the accounts would be
      // created because a directory says they should exist, and then authenticated
      // against something that is not that directory.
      bool NamesTheLocalComputer_(const String &domain)
      {
         String candidate = domain;
         candidate.Trim();

         if (candidate.IsEmpty() || candidate.Compare(_T(".")) == 0)
            return true;

         TCHAR computerName[MAX_COMPUTERNAME_LENGTH + 1];
         DWORD length = MAX_COMPUTERNAME_LENGTH + 1;

         if (!GetComputerName(computerName, &length))
         {
            // Cannot tell. Treated as NOT the local computer, because the alternative
            // is refusing to provision a correctly configured domain because a Win32
            // call failed - and the empty and "." forms, which are the ones anybody
            // actually types by accident, are already covered above.
            return false;
         }

         return candidate.CompareNoCase(String(computerName)) == 0;
      }

      // Whether hMailServer can hold this as an account address.
      //
      // Two checks, matching PreSaveLimitationsCheck, which is the code that will refuse
      // the save later. Matching it is the point: a preview that promises a create the
      // save would reject is a preview that lies, and the administrator finds out only
      // after pressing apply. The second check exists because the account address builds
      // a Windows path, and those characters are legal in an address and illegal in a
      // file name.
      //
      // Not every one of that check's refusals is predicted here, and it is worth being
      // straight about which. The address rules are, exactly. The domain QUOTAS - the
      // maximum number of accounts, and the total size already allocated - are not,
      // because both move as this run's own creates succeed, so a prediction made before
      // the first one would be wrong by the last. Those refusals arrive as a failed
      // entry carrying PreSaveLimitationsCheck's own sentence, which names the limit.
      bool IsUsableAccountAddress_(const String &address)
      {
         if (!StringParser::IsValidEmailAddress(address))
            return false;

         // The colon is in this set for the same reason as the rest of it: the local
         // part becomes a directory component of the message store's path, and
         // CreateDirectory refuses a name containing one. An account carrying a colon
         // saves, accepts mail at RCPT TO, and then cannot have its folder created.
         // PreSaveLimitationsCheck refuses it as of 15 August 2026; this predicts that
         // refusal rather than promising a create the save would reject.
         //
         // Reachable from a directory rather than theoretical: a proxyAddresses value
         // is "sip:" or "x500:" as often as it is "SMTP:", and PreferredAddress
         // deliberately strips only the smtp scheme so the others arrive here intact -
         // to be refused HERE, with a sentence, rather than provisioned.
         if (address.FindOneOf(_T(" \"\\/?*|:")) != -1)
            return false;

         return true;
      }

      // Splits a directory display name into the two fields an account has.
      //
      // At the FIRST space, because displayName is given-name-first in every directory
      // this is likely to meet, and because the alternative - splitting at the last -
      // turns "Anna van der Berg" into a first name of "Anna van der" rather than a
      // surname of "van der Berg". It is a guess either way; it is only ever written to
      // an account that has no name at all, so the worst case is a name nobody had
      // before being slightly wrong in an address book.
      void SplitDisplayName_(const String &displayName, String &firstName, String &lastName)
      {
         firstName.Empty();
         lastName.Empty();

         String value = displayName;
         value.Trim();

         if (value.IsEmpty())
            return;

         const int space = value.Find(_T(" "));

         if (space <= 0)
         {
            firstName = value;
            return;
         }

         firstName = value.Left(space);
         lastName = value.Mid(space + 1);

         lastName.Trim();
      }

      // Decides what an account needs, and - when writeChanges is set - makes the
      // changes it just decided on.
      //
      // ONE function for both, which is the whole reason a preview here can be believed.
      // The planner calls it with writeChanges false to find out whether there is
      // anything to do and what to call it; the applier calls it with writeChanges true
      // on a freshly read copy of the same account. There is no second implementation
      // that could describe one change and make another.
      //
      // What it deliberately does NOT touch:
      //
      //   - Active. It never enables an account. A directory-linked account that an
      //     administrator has disabled was disabled for a reason the directory knows
      //     nothing about - a suspension, an investigation, a leaver whose mail is being
      //     kept - and a nightly synchronisation quietly turning it back on is the kind
      //     of thing that is discovered afterwards. Disabling is handled separately and
      //     only on request.
      //
      //   - A name that is already set. Overwriting an administrator's typed-in name
      //     with the directory's displayName on every run is a change that keeps
      //     happening and that nobody asked for. The directory fills in a blank; it does
      //     not win an argument.
      //
      //   - The password, in any way at all. See the class comment.
      bool DecideUpdate_(std::shared_ptr<Account> account, const DesiredAccountState &desired,
         bool writeChanges, String &description)
      {
         std::vector<String> changes;

         if (!account->GetIsAD())
         {
            changes.push_back(_T("linked to the directory"));

            if (writeChanges)
               account->SetIsAD(true);
         }

         // Compared case-insensitively so that a directory which returns "EXAMPLE" today
         // and "example" tomorrow does not produce an update, and a log entry, for every
         // account on every run.
         if (account->GetADDomain().CompareNoCase(desired.directory_domain) != 0)
         {
            changes.push_back(Formatter::Format("directory domain set to {0}", desired.directory_domain));

            if (writeChanges)
               account->SetADDomain(desired.directory_domain);
         }

         if (account->GetADUsername().CompareNoCase(desired.directory_username) != 0)
         {
            changes.push_back(Formatter::Format("directory user name set to {0}", desired.directory_username));

            if (writeChanges)
               account->SetADUsername(desired.directory_username);
         }

         const bool hasName = !account->GetPersonFirstName().IsEmpty() ||
                              !account->GetPersonLastName().IsEmpty();

         const bool directoryHasName = !desired.first_name.IsEmpty() || !desired.last_name.IsEmpty();

         if (!hasName && directoryHasName)
         {
            changes.push_back(Formatter::Format("name set to {0} {1}", desired.first_name, desired.last_name));

            if (writeChanges)
            {
               account->SetPersonFirstName(desired.first_name);
               account->SetPersonLastName(desired.last_name);
            }
         }

         description = StringParser::JoinVector(changes, _T("; "));

         return !changes.empty();
      }

      DesiredAccountState DesiredFrom_(const DirectorySyncEntry &entry)
      {
         DesiredAccountState desired;

         desired.directory_domain = entry.directory_domain;
         desired.directory_username = entry.directory_username;
         desired.first_name = entry.first_name;
         desired.last_name = entry.last_name;

         return desired;
      }

      // "host:port", for a diagnostic that has to name the directory it is complaining
      // about - there is usually more than one in a migration.
      String DescribeTarget_(const LdapConfiguration &configuration)
      {
         return Formatter::Format("{0}:{1}", configuration.server, configuration.EffectivePort());
      }

      // Everything known about why an LDAP operation failed, in one sentence. The
      // server's own diagnosticMessage is appended when there is one, because on Active
      // Directory it is frequently the only text that names the real cause.
      String DescribeFailure_(const LdapClient &client)
      {
         String description = Formatter::Format("{0} (LDAP error {1})",
            client.DescribeLastError(), (int) client.GetLastLdapError());

         const String diagnostic = client.GetLastDiagnostic();

         if (!diagnostic.IsEmpty())
            description += Formatter::Format(". The directory said: {0}", diagnostic);

         return description;
      }

      // Marks the account belonging to a directory user as still present, for an entry
      // that could not be turned into an address.
      //
      // The disable pass asks one question of each account: did the directory mention
      // it? An entry with a missing or malformed mail attribute mentions a person
      // perfectly clearly and just cannot be made into a mailbox, so answering "no" for
      // it would take a working mailbox offline over one bad character in one attribute
      // - and would do it to the account most likely to be mid-edit.
      //
      // Searches every in-scope domain because there is no address to say which one it
      // is. That is not a licence to claim broadly: the key is the account's own stored
      // Active Directory user name, so only an account this directory already owns can
      // be claimed, and only ever one per domain.
      void ClaimByDirectoryUsername_(std::map<String, DomainScope> &scopes, const String &directoryUsername,
         String &detail)
      {
         if (directoryUsername.IsEmpty())
            return;

         const String key = Normalize_(directoryUsername);

         for (auto &scopeEntry : scopes)
         {
            DomainScope &scope = scopeEntry.second;

            if (!scope.in_scope)
               continue;

            auto found = scope.accounts_by_directory_username.find(key);

            if (found == scope.accounts_by_directory_username.end())
               continue;

            scope.seen.insert(found->second);

            detail += Formatter::Format(" The existing account {0} is linked to that directory user, so it has "
               "been left exactly as it is rather than treated as gone.", found->second);
         }
      }

      // Reads the whole hMailServer side: every domain, and the accounts of the ones
      // that are in scope.
      //
      // Domains::Refresh and Accounts::Refresh both return void and swallow a database
      // failure, which is worth stating rather than hoping about. A failed domain read
      // leaves no domains, so every directory entry is skipped with "no such domain
      // here" and nothing is created and nothing is disabled - noisy, and safe. A failed
      // account read leaves a domain looking empty, so its accounts would be planned as
      // creates; those creates then fail on PreSaveLimitationsCheck's duplicate check
      // rather than duplicating anything, and the disable pass finds nothing to disable
      // because it iterates the same empty list. Both failures are loud and neither
      // destroys anything.
      void LoadDomains_(const DirectorySyncOptions &options,
         std::map<String, DomainScope> &scopes, bool &requestedDomainFound)
      {
         // Normalised first, so that a value that is only whitespace means "every
         // domain" rather than "a domain whose name is a space", which no server has
         // and which would fail the run for no reason a reader could see.
         const String requested = Normalize_(options.domain_name);

         requestedDomainFound = requested.IsEmpty();

         Domains domains;
         domains.Refresh();

         for (int i = 0; i < domains.GetCount(); i++)
         {
            std::shared_ptr<Domain> domain = domains.GetItem(i);

            if (!domain)
               continue;

            DomainScope scope;

            scope.id = domain->GetID();
            scope.name = Normalize_(domain->GetName());
            scope.directory_domain = domain->GetADDomainName();
            scope.max_account_size = domain->GetMaxAccountSize();
            scope.max_size_mb = domain->GetMaxSizeMB();

            scope.directory_domain.Trim();

            if (!requested.IsEmpty() && scope.name.CompareNoCase(requested) != 0)
            {
               scope.in_scope = false;
               scope.exclusion_reason = DirectorySyncSkipReason::ReasonOutsideRequestedDomain;
            }
            else if (!domain->GetIsActive())
            {
               scope.in_scope = false;
               scope.exclusion_reason = DirectorySyncSkipReason::ReasonDomainInactive;
            }
            else if (scope.directory_domain.IsEmpty() || NamesTheLocalComputer_(scope.directory_domain))
            {
               // Empty is the obvious case. The other two are not, and reaching them
               // produces exactly the outcome the class comment argues a non-empty
               // domain prevents.
               //
               // SSPIValidation treats an empty domain, "." and this computer's own
               // name as the SAME thing: validate a LOCAL Windows account. So a domain
               // linked to "." would provision accounts that, the moment [LDAP]
               // Enabled=0 sends authentication back through SSPIValidation, are
               // checked against local Windows accounts - and a directory user whose
               // sAMAccountName is "Administrator" would then accept the local
               // Administrator password. Requiring non-empty is necessary and, on its
               // own, not sufficient.
               //
               // This does not touch the hand-configured local-account case at all.
               // An administrator may still link an ACCOUNT to a local Windows user;
               // what is refused is PROVISIONING accounts from a directory into a
               // domain whose stored directory name means "do not ask that directory".
               // Those two settings would contradict each other.
               scope.in_scope = false;
               scope.exclusion_reason = DirectorySyncSkipReason::ReasonDomainNotLinked;
            }
            else
            {
               scope.in_scope = true;
            }

            if (!requested.IsEmpty() && scope.name.CompareNoCase(requested) == 0)
               requestedDomainFound = true;

            // Only for domains that can actually be provisioned into. Reading every
            // account of every domain on a server that hosts hundreds, to answer
            // questions about one, is work nobody asked for.
            if (scope.in_scope)
            {
               Accounts accounts(scope.id);
               accounts.Refresh();

               for (int a = 0; a < accounts.GetCount(); a++)
               {
                  std::shared_ptr<Account> account = accounts.GetItem(a);

                  if (!account)
                     continue;

                  const String accountAddress = Normalize_(account->GetAddress());

                  scope.accounts[accountAddress] = account;

                  // Only directory-linked accounts, because only they can be claimed by
                  // a directory entry - and only the first of any duplicate, since two
                  // accounts sharing one directory user name is a configuration this
                  // class must not silently pick a winner for.
                  if (account->GetIsAD())
                  {
                     const String directoryUser = Normalize_(account->GetADUsername());

                     if (!directoryUser.IsEmpty())
                        scope.accounts_by_directory_username.insert(std::make_pair(directoryUser, accountAddress));
                  }
               }
            }

            scopes[scope.name] = scope;
         }
      }
   }

   int
   DirectorySyncResult::SkipCount(DirectorySyncSkipReason reason) const
   {
      auto found = skip_reasons.find(reason);

      if (found == skip_reasons.end())
         return 0;

      return found->second;
   }

   String
   DirectorySyncResult::Summary() const
   {
      if (!succeeded)
         return Formatter::Format("Directory synchronisation did not run: {0}", failure);

      const bool applying = mode == DirectorySyncMode::ModeApply;

      // Built by concatenation rather than one Format call because there are more
      // values than Formatter takes, and because the two moods - "will" and "was" -
      // differ in more places than a single substitution could carry.
      String summary = Formatter::Format("Directory synchronisation ({0}): {1} directory entries read",
         String(applying ? _T("applied") : _T("preview")), entries_read);

      summary += Formatter::Format(", {0} {1}", to_create, String(applying ? _T("created") : _T("to create")));
      summary += Formatter::Format(", {0} {1}", to_update, String(applying ? _T("updated") : _T("to update")));
      summary += Formatter::Format(", {0} unchanged", unchanged);
      summary += Formatter::Format(", {0} skipped", skipped);

      if (to_disable > 0)
      {
         summary += Formatter::Format(", {0} {1}", to_disable,
            String(applying ? _T("disabled") : _T("would be disabled")));
      }

      if (missing > 0)
         summary += Formatter::Format(", {0} no longer in the directory and left alone", missing);

      if (failed > 0)
         summary += Formatter::Format(", {0} could not be carried out", failed);

      if (truncated)
      {
         summary += _T(". The directory returned more entries than SyncMaxUsers allowed to be read, so this is ")
            _T("a partial view and nothing has been disabled");
      }

      summary += _T(".");

      return summary;
   }

   String
   DirectorySync::DescribeAction(DirectorySyncAction action)
   {
      switch (action)
      {
         case DirectorySyncAction::ActionCreate:        return _T("create");
         case DirectorySyncAction::ActionUpdate:        return _T("update");
         case DirectorySyncAction::ActionUnchanged:     return _T("unchanged");
         case DirectorySyncAction::ActionDisable:       return _T("disable");
         case DirectorySyncAction::ActionReportMissing: return _T("no longer in the directory");
         default:                                       return _T("skip");
      }
   }

   String
   DirectorySync::DescribeSkipReason(DirectorySyncSkipReason reason)
   {
      switch (reason)
      {
         case DirectorySyncSkipReason::ReasonNoMailAttribute:
            return _T("the directory entry carries no mail address");
         case DirectorySyncSkipReason::ReasonMalformedAddress:
            return _T("the address is not one hMailServer can hold");
         case DirectorySyncSkipReason::ReasonUnknownDomain:
            return _T("there is no domain of that name on this server");
         case DirectorySyncSkipReason::ReasonDomainInactive:
            return _T("the domain exists here but is not active");
         case DirectorySyncSkipReason::ReasonDomainNotLinked:
            return _T("the domain has no Active Directory domain name set");
         case DirectorySyncSkipReason::ReasonNoDirectoryUsername:
            return _T("the directory entry carries no user name");
         case DirectorySyncSkipReason::ReasonAccountNotDirectoryLinked:
            return _T("an account with this address exists and does not authenticate against a directory");
         case DirectorySyncSkipReason::ReasonAccountInOtherDirectory:
            return _T("an account with this address exists and is linked to a different directory domain");
         case DirectorySyncSkipReason::ReasonDuplicateInDirectory:
            return _T("another directory entry already claimed this address");
         case DirectorySyncSkipReason::ReasonOutsideRequestedDomain:
            return _T("the address is outside the domain this run was restricted to");
         case DirectorySyncSkipReason::ReasonDomainSizePolicy:
            return _T("the domain limits its total size but sets no maximum size per account");
         default:
            return _T("");
      }
   }

   DirectorySyncResult
   DirectorySync::Run(const DirectorySyncOptions &options)
   {
      DirectorySyncResult result;

      result.mode = options.mode;

      try
      {
         boost::unique_lock<boost::mutex> guard(run_mutex, boost::defer_lock);

         guard.try_lock();

         if (!guard.owns_lock())
         {
            result.failure = _T("A directory synchronisation is already running. Wait for it to finish and try ")
               _T("again; two overlapping runs would each act on the other's half-finished work.");

            Report_(options.mode, 6180, "DirectorySync::Run", result.failure);

            return result;
         }

         const LdapConfiguration configuration = LdapSettings::Instance()->GetConfiguration();

         if (!configuration.enabled)
         {
            result.failure = _T("LDAP is switched off (hMailServer.ini [LDAP] Enabled=0). Nothing has been read ")
               _T("and nothing has been changed.");

            Report_(options.mode, 6180, "DirectorySync::Run", result.failure);

            return result;
         }

         String missing;

         if (!configuration.IsComplete(missing))
         {
            result.failure = Formatter::Format("The [LDAP] section is not complete: {0} is not set. Nothing has "
               "been read and nothing has been changed.", missing);

            Report_(options.mode, 6180, "DirectorySync::Run", result.failure);

            return result;
         }

         // Checked here as well as in IsComplete, and this is not redundant.
         // IsComplete only requires SearchBase when the AUTHENTICATION path needs to
         // search, which it does not for BindMethod=1 - that mode finds the user by
         // name over SSPI. Reading the directory as an account source always needs a
         // base to enumerate from, so a configuration that authenticates perfectly well
         // can still have nothing to synchronise from, and the message has to say so
         // rather than let a subtree search from "" return whatever it returns.
         if (configuration.search_base.IsEmpty())
         {
            result.failure = _T("No search base is configured (hMailServer.ini [LDAP] SearchBase). ")
               _T("Authentication does not need one when BindMethod=1, but reading the directory to find out ")
               _T("which accounts should exist does: there is nowhere to enumerate from.");

            Report_(options.mode, 6180, "DirectorySync::Run", result.failure);

            return result;
         }

         if (!Plan_(configuration, options, result))
            return result;

         result.succeeded = true;

         if (options.mode == DirectorySyncMode::ModeApply)
            Apply_(result);

         LOG_APPLICATION(Formatter::Format("DirectorySync: {0}", result.Summary()));

         return result;
      }
      catch (...)
      {
         // The barrier. This is reachable from COM, and an exception crossing that
         // boundary is an access violation in whatever was hosting the caller. A run
         // that fell over has to come back as a failed run with a sentence in it.
         result.succeeded = false;
         result.failure = _T("An unexpected error occurred during directory synchronisation. Some of the ")
            _T("reported actions may have been carried out; check the entry list, in which every action that ")
            _T("completed is marked as applied.");

         Report_(options.mode, 6180, "DirectorySync::Run", result.failure);

         return result;
      }
   }

   bool
   DirectorySync::Plan_(const LdapConfiguration &configuration, const DirectorySyncOptions &options,
      DirectorySyncResult &result)
   {
      // ---- the hMailServer side ------------------------------------------------

      std::map<String, DomainScope> scopes;
      bool requestedDomainFound = false;

      LoadDomains_(options, scopes, requestedDomainFound);

      if (!requestedDomainFound)
      {
         result.failure = Formatter::Format("There is no domain called {0} on this server. Nothing has been read "
            "and nothing has been changed.", options.domain_name);

         return false;
      }

      // ---- the directory -------------------------------------------------------

      LdapClient client;

      if (client.Connect(configuration) != LdapOperationOutcome::OutcomeSuccess)
      {
         result.failure = Formatter::Format("Could not connect to the directory at {0}: {1}",
            DescribeTarget_(configuration), DescribeFailure_(client));

         Report_(options.mode, 6180, "DirectorySync::Plan_", result.failure);

         return false;
      }

      // BindService enforces the transport rules itself - it refuses to put a password
      // on an unprotected connection unless AllowUnprotectedPassword is set - so there
      // is no separate check here that could drift out of step with the one on the
      // authentication path.
      if (client.BindService(configuration) != LdapOperationOutcome::OutcomeSuccess)
      {
         result.failure = Formatter::Format("Could not bind to the directory at {0} in order to read it: {1}",
            DescribeTarget_(configuration), DescribeFailure_(client));

         if (configuration.service_username.IsEmpty())
         {
            result.failure += _T(" No ServiceUsername is configured, so the bind was anonymous; Active Directory ")
               _T("refuses anonymous searches by default.");
         }

         Report_(options.mode, 6180, "DirectorySync::Plan_", result.failure);

         return false;
      }

      // Deduplicated because the three attribute settings can name the same attribute -
      // a directory with no separate logon name is configured with mail in both - and
      // asking for the same attribute twice would allocate two buffers for it and read
      // it twice per entry, to no effect.
      const String mailAttribute = configuration.sync_mail_attribute;
      const String usernameAttribute = configuration.sync_username_attribute;
      const String displayNameAttribute = configuration.sync_display_name_attribute;

      std::vector<String> requestedAttributes;

      requestedAttributes.push_back(mailAttribute);
      requestedAttributes.push_back(usernameAttribute);
      requestedAttributes.push_back(displayNameAttribute);

      std::vector<String> attributes;
      std::set<String> attributeSeen;

      for (const String &name : requestedAttributes)
      {
         if (attributeSeen.insert(name).second)
            attributes.push_back(name);
      }

      std::vector<LdapDirectoryEntry> directoryEntries;
      bool truncated = false;

      if (client.SearchEntries(configuration.search_base, configuration.sync_filter, attributes,
             configuration.sync_max_users, directoryEntries, truncated) != LdapOperationOutcome::OutcomeSuccess)
      {
         result.failure = Formatter::Format("The directory search failed (base {0}): {1}",
            configuration.search_base, DescribeFailure_(client));

         Report_(options.mode, 6180, "DirectorySync::Plan_", result.failure);

         return false;
      }

      result.truncated = truncated;
      result.entries_read = (int) directoryEntries.size();

      if (truncated)
      {
         Report_(options.mode, 6182, "DirectorySync::Plan_",
            Formatter::Format("The directory search returned more than SyncMaxUsers ({0}) entries, so only the "
               "first {0} have been read. This is a partial view of the directory: nothing will be disabled, "
               "because every entry beyond the limit is indistinguishable from an entry that has been deleted. "
               "Raise [LDAP] SyncMaxUsers, or narrow SyncFilter or SearchBase.", configuration.sync_max_users));
      }

      // ---- decide, one directory entry at a time --------------------------------

      // Every address this run has already produced a row for. Not per domain: the
      // address is the key an account is stored under, so two entries claiming the same
      // address collide regardless of which domain scope they were resolved through.
      std::set<String> claimed;

      for (const LdapDirectoryEntry &directoryEntry : directoryEntries)
      {
         DirectorySyncEntry entry;

         entry.distinguished_name = directoryEntry.dn;
         entry.action = DirectorySyncAction::ActionSkip;

         // Preferred rather than "the first one": see LdapDirectoryEntry::Preferred for
         // why taking whichever value the directory happened to send first would let a
         // re-run pick a different address for the same person.
         //
         // PreferredAddress rather than Preferred is what makes
         // SyncMailAttribute=proxyAddresses usable. Those values carry their scheme -
         // "SMTP:alice@example.com" - and an address with a colon in it is not one
         // hMailServer can hold, so without it every entry in an Exchange-style
         // directory would be skipped as malformed and the report would blame the
         // addresses rather than the attribute choice.
         const String rawAddress = directoryEntry.PreferredAddress(mailAttribute);

         // Taken before the address is validated, because it is what identifies the
         // PERSON when the address cannot be used. See ClaimByDirectoryUsername_.
         String rawUsername = directoryEntry.Preferred(usernameAttribute);
         rawUsername.Trim();

         if (rawAddress.IsEmpty())
         {
            entry.reason = DirectorySyncSkipReason::ReasonNoMailAttribute;
            entry.detail = Formatter::Format("{0} has no {1} value, so there is no address to create a mailbox "
               "for.", directoryEntry.dn, mailAttribute);

            // This entry cannot become a mailbox, and the person is still in the
            // directory. Claiming their existing account keeps a data-quality problem
            // from being read as a departure and taking the mailbox offline.
            ClaimByDirectoryUsername_(scopes, rawUsername, entry.detail);

            Append_(result, entry);
            continue;
         }

         entry.address = Normalize_(rawAddress);

         if (!IsUsableAccountAddress_(entry.address))
         {
            entry.reason = DirectorySyncSkipReason::ReasonMalformedAddress;
            entry.detail = Formatter::Format("{0} carries the address {1}, which hMailServer cannot use as an "
               "account address.", directoryEntry.dn, entry.address);

            ClaimByDirectoryUsername_(scopes, rawUsername, entry.detail);

            Append_(result, entry);
            continue;
         }

         // Already lower-cased, because the address it was taken from was. That matters
         // because the scope map is keyed on the same normalised form, and a lookup that
         // missed on case alone would report every entry as belonging to a domain that
         // does not exist here.
         const String domainName = StringParser::ExtractDomain(entry.address);

         auto foundDomain = scopes.find(domainName);

         if (foundDomain == scopes.end())
         {
            entry.reason = DirectorySyncSkipReason::ReasonUnknownDomain;
            entry.detail = Formatter::Format("{0} is in the domain {1}, which does not exist on this server. "
               "Domains are never created automatically.", entry.address, domainName);

            Append_(result, entry);
            continue;
         }

         DomainScope &scope = foundDomain->second;

         if (!scope.in_scope)
         {
            entry.reason = scope.exclusion_reason;
            entry.detail = Formatter::Format("{0} was not provisioned: {1}.", entry.address,
               DescribeSkipReason(scope.exclusion_reason));

            Append_(result, entry);
            continue;
         }

         // Recorded before the remaining checks, not after. Everything below this line
         // is a reason not to ACT on the entry, and none of them is a reason to believe
         // the person has left - so the account keeps its place in the directory's view
         // of the world and stays out of the disable pass.
         scope.seen.insert(entry.address);

         entry.domain_id = scope.id;
         entry.directory_domain = scope.directory_domain;
         entry.directory_username = directoryEntry.Preferred(usernameAttribute);

         entry.directory_username.Trim();

         SplitDisplayName_(directoryEntry.Preferred(displayNameAttribute), entry.first_name, entry.last_name);

         if (!claimed.insert(entry.address).second)
         {
            entry.reason = DirectorySyncSkipReason::ReasonDuplicateInDirectory;
            entry.detail = Formatter::Format("{0} claims the address {1}, which an earlier directory entry "
               "already claimed. Only the first was acted on; the duplicate should be resolved in the directory.",
               directoryEntry.dn, entry.address);

            Append_(result, entry);
            continue;
         }

         if (entry.directory_username.IsEmpty())
         {
            entry.reason = DirectorySyncSkipReason::ReasonNoDirectoryUsername;
            entry.detail = Formatter::Format("{0} has no {1} value, so the account could not be linked to a "
               "directory identity. Falling back to the local part of the address would be a guess, and a wrong "
               "guess produces an account that exists and can never log in.", directoryEntry.dn, usernameAttribute);

            Append_(result, entry);
            continue;
         }

         auto foundAccount = scope.accounts.find(entry.address);

         if (foundAccount == scope.accounts.end())
         {
            // The domain's per-account maximum, which is the value an administrator
            // creating this account by hand would type in. Zero when the domain sets no
            // maximum, which is the same thing an account created by hand gets.
            entry.account_max_size = scope.max_account_size;

            // A domain that limits its TOTAL size refuses every account that has no size
            // of its own, and there is no per-account maximum here to take one from.
            // Caught in the plan rather than at the save, so the preview says "not
            // provisioned, and here is the setting to fix" instead of the apply
            // producing one refusal per person.
            if (scope.max_size_mb > 0 && entry.account_max_size == 0)
            {
               entry.action = DirectorySyncAction::ActionSkip;
               entry.reason = DirectorySyncSkipReason::ReasonDomainSizePolicy;
               entry.detail = Formatter::Format("{0} was not created: the domain has a total size limit of {1} MB "
                  "but no maximum size per account, and hMailServer refuses to save an account with no size into "
                  "such a domain. Set the domain's maximum account size.", entry.address, scope.max_size_mb);

               Append_(result, entry);
               continue;
            }

            entry.action = DirectorySyncAction::ActionCreate;
            entry.detail = Formatter::Format("{0} will be created, authenticating against the directory as {1}\\{2}.",
               entry.address, entry.directory_domain, entry.directory_username);

            Append_(result, entry);
            continue;
         }

         std::shared_ptr<Account> account = foundAccount->second;

         entry.account_id = account->GetID();

         if (!account->GetIsAD())
         {
            entry.action = DirectorySyncAction::ActionSkip;
            entry.reason = DirectorySyncSkipReason::ReasonAccountNotDirectoryLinked;
            entry.detail = Formatter::Format("{0} already exists and authenticates against its own stored "
               "password. It has been left alone: linking it to the directory would move who decides its "
               "password, and if the directory disagrees the owner loses access to mail they can read today.",
               entry.address);

            Append_(result, entry);
            continue;
         }

         if (!account->GetADDomain().IsEmpty() &&
             account->GetADDomain().CompareNoCase(scope.directory_domain) != 0)
         {
            entry.action = DirectorySyncAction::ActionSkip;
            entry.reason = DirectorySyncSkipReason::ReasonAccountInOtherDirectory;
            entry.detail = Formatter::Format("{0} already exists and is linked to the directory domain {1}, not "
               "{2}. Repointing it would change which directory owns that identity, which is not something a "
               "synchronisation should decide.", entry.address, account->GetADDomain(), scope.directory_domain);

            Append_(result, entry);
            continue;
         }

         String description;

         if (DecideUpdate_(account, DesiredFrom_(entry), false, description))
         {
            entry.action = DirectorySyncAction::ActionUpdate;
            entry.detail = Formatter::Format("{0}: {1}.", entry.address, description);
         }
         else
         {
            entry.action = DirectorySyncAction::ActionUnchanged;
            entry.detail = Formatter::Format("{0} already matches the directory.", entry.address);
         }

         // Said out loud rather than silently left alone. An administrator looking for
         // why somebody cannot collect their mail needs to see that the account is
         // there, is correct, and is switched off - and needs to know that this run did
         // not switch it back on.
         if (!account->GetActive())
         {
            entry.detail += _T(" The account is currently disabled; it has been left disabled, because a ")
               _T("directory does not know why an administrator disabled a mailbox.");
         }

         Append_(result, entry);
      }

      // ---- accounts the directory no longer has -------------------------------

      // The two refusals. Both come down to the same thing: absent and unread look
      // identical from here, and the only safe response to not knowing which one it is
      // is to do nothing.
      //
      // The zero case is not hypothetical. A filter edited to something that matches
      // nothing - a typo in an attribute name, an OU that was renamed - returns
      // LDAP_SUCCESS with no entries, which without this guard reads as "every single
      // person has left" and disables every directory-linked account on the server in
      // one pass.
      // PER DOMAIN, not across the run. The first version of this asked whether the
      // whole enumeration produced anything, which meant one domain's users authorised
      // disabling in another - and the truncation message itself recommends the change
      // that triggers it ("narrow SyncFilter or SearchBase").
      //
      // Worked through: a server hosts internal.example.com and sales.example.com, both
      // linked to EXAMPLE. The administrator narrows SearchBase to OU=Internal to get
      // under SyncMaxUsers. The next run reads plenty of internal people, so a run-wide
      // check passes - and every account in sales.example.com is now missing from its
      // own seen set and is disabled in one pass. Which is precisely the outcome the
      // zero-entry guard exists to prevent, arriving one level up.
      const bool enumerationUsable = !truncated;

      for (auto &scopeEntry : scopes)
      {
         DomainScope &scope = scopeEntry.second;

         if (!scope.in_scope)
            continue;

         // This domain saw nobody. Whether that is because everyone left or because the
         // search no longer reaches them is not answerable from here, so nothing in it
         // is touched.
         const bool mayDisable = enumerationUsable && !scope.seen.empty();

         for (auto &accountEntry : scope.accounts)
         {
            std::shared_ptr<Account> account = accountEntry.second;

            // Only accounts this directory is responsible for. An account that
            // authenticates against its own password, or against a different directory
            // domain, has nothing to do with what this enumeration did or did not
            // return.
            if (!account->GetIsAD())
               continue;

            if (account->GetADDomain().CompareNoCase(scope.directory_domain) != 0)
               continue;

            if (scope.seen.find(accountEntry.first) != scope.seen.end())
               continue;

            // Already disabled. Nothing to report and nothing to do: it is in the state
            // the missing entry would have put it in.
            if (!account->GetActive())
               continue;

            DirectorySyncEntry entry;

            entry.address = accountEntry.first;
            entry.account_id = account->GetID();
            entry.domain_id = scope.id;
            entry.directory_domain = scope.directory_domain;
            entry.directory_username = account->GetADUsername();

            if (mayDisable && options.disable_missing)
            {
               entry.action = DirectorySyncAction::ActionDisable;
               entry.detail = Formatter::Format("{0} is no longer in the directory and will be marked inactive. "
                  "No message is deleted and no account is removed; re-enabling it restores everything.",
                  entry.address);
            }
            else
            {
               entry.action = DirectorySyncAction::ActionReportMissing;

               if (!mayDisable && options.disable_missing)
               {
                  entry.detail = Formatter::Format("{0} was not found in the directory, but this enumeration was "
                     "not complete enough to act on: it has been left exactly as it is.", entry.address);
               }
               else
               {
                  entry.detail = Formatter::Format("{0} is no longer in the directory. It has been left exactly "
                     "as it is, because disabling was not requested.", entry.address);
               }
            }

            Append_(result, entry);
         }
      }

      return true;
   }

   void
   DirectorySync::Apply_(DirectorySyncResult &result)
   {
      for (DirectorySyncEntry &entry : result.entries)
      {
         // Nothing is re-decided here. The switch dispatches on the action the plan
         // already chose, and the only decisions this phase makes are refusals: when the
         // world has moved since the plan was made, it declines to act and says so.
         switch (entry.action)
         {
            case DirectorySyncAction::ActionCreate:
            {
               // Re-read immediately before the insert. Not paranoia about the plan
               // being wrong - it is about the gap between the plan and now, which for a
               // large directory is minutes: another administrator, the REST API or a
               // second server sharing the database can have created this address in
               // between. PreSaveLimitationsCheck would refuse the save anyway; asking
               // first turns a database error into a sentence that says what happened.
               std::shared_ptr<Account> existing = std::shared_ptr<Account>(new Account());

               if (PersistentAccount::ReadObject(existing, entry.address) && existing->GetID() > 0)
               {
                  entry.error = _T("An account with this address already existed by the time the change was ")
                     _T("made, so it was not created.");

                  result.failed++;
                  break;
               }

               std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());

               account->SetDomainID(entry.domain_id);
               account->SetAddress(entry.address);
               account->SetAccountMaxSize(entry.account_max_size);

               // Active on creation. An account that was provisioned inactive would be
               // invisible to the person it was provisioned for and would look, to the
               // next run, exactly like an account somebody had deliberately disabled.
               account->SetActive(true);

               String description;

               // The same function the preview used, against a blank account, so every
               // field it sets is a field the preview said it would set. No password is
               // set here or anywhere: the account is directory-authenticated from this
               // moment, and the empty stored value can never satisfy a comparison.
               DecideUpdate_(account, DesiredFrom_(entry), true, description);

               String saveError;

               // createInbox true. Without it the row is written and no INBOX folder is,
               // and a message delivered to such an account is accepted at RCPT TO and
               // then silently dropped - no bounce, no copy, nothing. The REST API
               // shipped with that defect for a while; provisioning hundreds of accounts
               // at a time is not the place to repeat it.
               if (!PersistentAccount::SaveObject(account, saveError, true, PersistenceModeNormal))
               {
                  // PreSaveLimitationsCheck writes a sentence for an administrator into
                  // saveError - a domain account limit, a size limit, an address another
                  // object already uses - and leaves it empty when the insert itself
                  // failed. Passing its text through unchanged is what turns "could not
                  // create 40 accounts" into forty rows that each say why.
                  entry.error = saveError;

                  if (entry.error.IsEmpty())
                     entry.error = _T("The database refused to create the account.");

                  result.failed++;
                  break;
               }

               entry.account_id = account->GetID();
               entry.applied = true;

               LOG_APPLICATION(Formatter::Format("DirectorySync: Account {0} created from directory entry {1}, "
                  "authenticating as {2}.", entry.address, entry.distinguished_name, entry.directory_username));

               break;
            }

            case DirectorySyncAction::ActionUpdate:
            {
               // Read fresh rather than reusing the object the planner held. A save
               // writes every column from the in-memory object, so writing back a
               // snapshot taken minutes ago would silently revert an auto-reply, a
               // forwarding address or a size limit that somebody changed in between.
               std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());

               if (!PersistentAccount::ReadObject(account, entry.account_id) || account->GetID() == 0)
               {
                  entry.error = _T("The account could not be found; it may have been deleted since the preview.");

                  result.failed++;
                  break;
               }

               // The planner's two refusals, re-checked against the account as it is NOW.
               //
               // Plan_ refuses to touch an account that is not directory-linked, and one
               // linked to a DIFFERENT directory domain, because both would move who
               // decides that mailbox's password - an administrator's decision, not a
               // synchronisation's. The disable branch below re-checks exactly these two
               // and this one did not, which left a window: LoadDomains_ runs before the
               // enumeration, and on a large directory that is minutes. An administrator
               // who converts an account to a local password, or repoints it at another
               // directory domain, during that window would have had it silently
               // converted back, because DecideUpdate_ sets IsAD, ADDomain and ADUsername
               // unconditionally.
               if (!account->GetIsAD())
               {
                  entry.error = _T("The account is no longer linked to a directory - it was changed after the ")
                     _T("plan was made - so it has been left alone rather than linked back.");

                  result.failed++;
                  break;
               }

               if (account->GetADDomain().CompareNoCase(entry.directory_domain) != 0)
               {
                  entry.error = Formatter::Format("The account now points at the directory domain {0} rather "
                     "than {1} - it was changed after the plan was made - so it has been left alone rather than "
                     "repointed.", account->GetADDomain(), entry.directory_domain);

                  result.failed++;
                  break;
               }

               String description;

               if (!DecideUpdate_(account, DesiredFrom_(entry), true, description))
               {
                  // Somebody else made the same change first. Nothing to write, and
                  // nothing wrong: the account is in the state the plan wanted.
                  entry.applied = true;
                  break;
               }

               String saveError;

               if (!PersistentAccount::SaveObject(account, saveError, false, PersistenceModeNormal))
               {
                  entry.error = saveError;

                  if (entry.error.IsEmpty())
                     entry.error = _T("The database refused to update the account.");

                  result.failed++;
                  break;
               }

               entry.applied = true;

               LOG_APPLICATION(Formatter::Format("DirectorySync: Account {0} updated from the directory: {1}.",
                  entry.address, description));

               break;
            }

            case DirectorySyncAction::ActionDisable:
            {
               std::shared_ptr<Account> account = std::shared_ptr<Account>(new Account());

               if (!PersistentAccount::ReadObject(account, entry.account_id) || account->GetID() == 0)
               {
                  entry.error = _T("The account could not be found; it may have been deleted since the preview.");

                  result.failed++;
                  break;
               }

               // Everything the plan relied on, checked again against current data. This
               // is the one action that takes a working mailbox away from somebody, so
               // it is also the one where a stale plan does the most damage: an account
               // that has been unlinked from the directory, or repointed at another one,
               // since the preview must not be disabled on the strength of a decision
               // made about a different configuration.
               if (!account->GetIsAD() ||
                   account->GetADDomain().CompareNoCase(entry.directory_domain) != 0)
               {
                  entry.error = _T("The account is no longer linked to this directory domain, so it has been ")
                     _T("left alone.");

                  result.failed++;
                  break;
               }

               if (!account->GetActive())
               {
                  // Already in the state the plan wanted it in.
                  entry.applied = true;
                  break;
               }

               account->SetActive(false);

               String saveError;

               if (!PersistentAccount::SaveObject(account, saveError, false, PersistenceModeNormal))
               {
                  entry.error = saveError;

                  if (entry.error.IsEmpty())
                     entry.error = _T("The database refused to disable the account.");

                  result.failed++;
                  break;
               }

               entry.applied = true;

               LOG_APPLICATION(Formatter::Format("DirectorySync: Account {0} has been marked inactive because it "
                  "is no longer present in the directory. No mail has been deleted.", entry.address));

               break;
            }

            default:
               // Unchanged, skipped, and reported-missing rows are the plan saying there
               // is nothing to do. There is nothing to do.
               break;
         }
      }

      // Nothing is done here to keep the account cache in step, and that is deliberate
      // rather than forgotten. PersistentAccount::SaveObject already evicts an updated
      // account from Cache<Account>, so a disable or a linkage change is visible to the
      // next logon rather than served stale for the cache's lifetime. A newly created
      // account needs no eviction at all, because CacheReaderWithDbFallback caches only
      // successful reads - there is no negative entry saying the address does not exist,
      // which is the thing that would otherwise have to be cleared.

      if (result.failed > 0)
      {
         Report_(DirectorySyncMode::ModeApply, 6181, "DirectorySync::Apply_",
            Formatter::Format("{0} of the {1} planned directory synchronisation changes could not be carried "
               "out. The rest were applied. Each failure is recorded against its own entry in the result; the "
               "usual causes are a domain limit on the number of accounts, and an address that another object "
               "already uses.", result.failed,
               result.to_create + result.to_update + result.to_disable));
      }
   }
}
