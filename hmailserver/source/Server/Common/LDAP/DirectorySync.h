// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class LdapConfiguration;

   // Whether a run is allowed to change anything.
   //
   // An enum rather than a bool, because the call site is the last place this
   // decision is visible and Run(true) says nothing about what the true means. A
   // caller that gets the argument the wrong way round here provisions or disables
   // mailboxes it only meant to look at, so the value is spelled out at every call.
   enum class DirectorySyncMode
   {
      ModePreview = 0,
      ModeApply = 1
   };

   // What one row of the report says happened, or would happen.
   //
   // ActionDisable and ActionReportMissing describe the SAME situation - an account
   // that exists here and no longer matches anything in the directory - and are
   // separate because only one of them is going to be acted on. Folding them into one
   // value and consulting the options at apply time is precisely the defect this class
   // is built to avoid: the preview would then promise a disable that apply would not
   // perform, or worse, the other way round. The plan decides once, and apply obeys it.
   //
   // There is deliberately no delete. A directory entry that has disappeared can mean
   // the person has left, or it can mean somebody moved an OU, mistyped the search
   // base, or changed the filter; the first is worth disabling an account for and none
   // of them is worth destroying a mailbox for. Mail that was never deleted can always
   // be deleted later.
   enum class DirectorySyncAction
   {
      ActionCreate = 0,
      ActionUpdate = 1,

      // Already exists and already matches the directory. Reported so that the counts
      // add up to the number of entries read - an administrator who is told "12 users"
      // and given 9 mailboxes is owed the other three.
      ActionUnchanged = 2,

      // Could not be provisioned. reason says which of the several quite different
      // causes it was.
      ActionSkip = 3,

      // Present here, absent from the directory, and DisableMissing was set: this
      // account will be (or has been) marked inactive. Its mail is untouched.
      ActionDisable = 4,

      // The same situation with DisableMissing clear. Reported and nothing more.
      ActionReportMissing = 5
   };

   // Why an entry could not be provisioned.
   //
   // Enumerated rather than left as free text because these are counted, and an
   // administrator diagnosing "412 users, 390 mailboxes" needs to know whether the
   // twenty-two missing ones are in a domain that does not exist here, in a domain
   // that exists but has not been linked to the directory, or carry no address at all.
   // Those have three different fixes.
   enum class DirectorySyncSkipReason
   {
      ReasonNone = 0,

      // The entry carries no value for SyncMailAttribute. With the shipped filter this
      // cannot happen (it requires mail=*), so it means the filter has been changed or
      // SyncMailAttribute names an attribute the directory does not populate.
      ReasonNoMailAttribute = 1,

      // The address is not one hMailServer can hold. Includes the characters that are
      // legal in an address and illegal in a Windows path, because the account address
      // becomes part of the message store's directory names.
      ReasonMalformedAddress = 2,

      // No hMailServer domain of that name. Creating one implicitly is out of scope and
      // would be dangerous: a mistyped search base pointing at a partner organisation
      // would silently make this server authoritative for their domain, at which point
      // it starts accepting - and never relaying - their mail.
      ReasonUnknownDomain = 3,

      // The domain exists but is switched off. Mailboxes in it can neither receive nor
      // collect mail, so provisioning into it would manufacture accounts that do not
      // work and hide the reason.
      ReasonDomainInactive = 4,

      // The domain exists and is active but has no Active Directory domain name set.
      // That field is what links an hMailServer domain to a directory, and it is
      // required rather than defaulted - see the comment on DirectorySync.
      ReasonDomainNotLinked = 5,

      // The entry carries no value for SyncUsernameAttribute, so there is no directory
      // identity to store against the account. Guessing one from the local part of the
      // address is not good enough for an account that is being created: the guess is
      // silently wrong on any directory where the mail address and the logon name
      // differ, and the failure surfaces months later as a user who cannot log in.
      ReasonNoDirectoryUsername = 6,

      // An account with this address exists and is NOT directory-linked. Taking it over
      // would move a working mailbox's authentication to the directory; if the
      // directory has no matching user, or a different password, the owner is locked
      // out of mail they can read today. That conversion is an administrator's decision.
      ReasonAccountNotDirectoryLinked = 7,

      // An account with this address exists, is directory-linked, and names a DIFFERENT
      // Active Directory domain. Repointing it would move which directory decides who
      // that mailbox belongs to, which is an identity change, not a synchronisation.
      ReasonAccountInOtherDirectory = 8,

      // Two directory entries resolved to the same address. Only the first is acted on;
      // the second is reported so the duplicate in the directory gets fixed rather than
      // producing a create that the database refuses.
      ReasonDuplicateInDirectory = 9,

      // The run was restricted to one hMailServer domain and this entry belongs to
      // another. Not a fault - reported so the counts still add up.
      ReasonOutsideRequestedDomain = 10,

      // The domain has a total size limit but no per-account size limit, and
      // PreSaveLimitationsCheck refuses to save any account into such a domain unless
      // that account carries a size of its own. There is no value this code could pick
      // that would not be an invention: the administrator has said how big the domain
      // may get and has not said how that is to be divided.
      ReasonDomainSizePolicy = 11
   };

   // One row of the report. Carries everything the apply phase needs, so that applying
   // is a matter of carrying out the plan rather than working it out again.
   struct DirectorySyncEntry
   {
      // The mailbox address, lower-cased. Empty only when the directory entry carried
      // no address at all, which is what ReasonNoMailAttribute means.
      String address;

      // The directory entry this came from, so an administrator can find the object
      // that produced a surprising row. Empty for rows that come from the hMailServer
      // side (ActionDisable, ActionReportMissing).
      String distinguished_name;

      // What will be stored in the account's Active Directory user name and domain.
      // These two are the whole point of the exercise: they are what
      // LdapDirectoryAuthenticator uses to authenticate the account afterwards.
      String directory_username;
      String directory_domain;

      // Split out of SyncDisplayNameAttribute. Only ever written to an account that
      // has no name at all - see DirectorySync.cpp.
      String first_name;
      String last_name;

      __int64 account_id = 0;    // 0 for an account that does not exist yet
      __int64 domain_id = 0;

      // The mailbox size limit a created account will carry, in MB, taken from the
      // owning domain's per-account maximum. Zero means unlimited, which is what an
      // account created by hand gets when the domain sets no maximum.
      int account_max_size = 0;

      DirectorySyncAction action = DirectorySyncAction::ActionSkip;
      DirectorySyncSkipReason reason = DirectorySyncSkipReason::ReasonNone;

      // A sentence written for an administrator, naming the values involved. This is
      // what a report should show; the enums above are for counting and filtering.
      String detail;

      // Apply only. applied is true when the action was carried out; error says why
      // not when it was not. Both stay at their defaults for a preview, which is how a
      // renderer can tell "will be created" from "was created".
      bool applied = false;
      String error;
   };

   // What the caller is asking for.
   class DirectorySyncOptions
   {
   public:
      DirectorySyncMode mode = DirectorySyncMode::ModePreview;

      // Whether an account whose directory entry has disappeared should be marked
      // inactive. Off by default: an account that stops being visible in the directory
      // is much more often a search that changed than a person who left, and the cost
      // of the two mistakes is not symmetric.
      //
      // This class never deletes an account or a message, whatever this is set to: the
      // most it does is clear Active, and a disabled account keeps every message it
      // holds until somebody removes it by hand. Nothing on the server ages a mailbox
      // out on its own - the retention tasks that exist prune log files and backup
      // archives, not mail - so "disabled" stays recoverable for as long as the account
      // row is there. What it does NOT promise is that the account is safe from
      // everything else: an administrator, a script or the COM API can still delete it,
      // and this option changes nothing about that.
      bool disable_missing = false;

      // Restricts the run to a single hMailServer domain. Empty means every domain
      // that is linked to a directory. Useful for bringing one domain over at a time,
      // which is how anybody sane does this the first time.
      String domain_name;
   };

   // The whole outcome, in a shape a COM layer can render without re-deciding anything.
   class DirectorySyncResult
   {
   public:
      DirectorySyncMode mode = DirectorySyncMode::ModePreview;

      // False when the run could not be attempted or could not be completed - LDAP
      // switched off, no search base, the directory unreachable. failure then holds a
      // sentence saying which. This is NOT about individual entries failing: a run that
      // read the directory and could not provision one account out of four hundred
      // succeeded, and the one is counted in failed and explained in that entry's error.
      bool succeeded = false;
      String failure;

      // The directory returned more entries than SyncMaxUsers allowed to be read, so
      // this is a PARTIAL view. Nothing is disabled when this is set - see the comment
      // on DirectorySync::Run - because every entry beyond the limit is indistinguishable
      // from an entry that has been deleted.
      bool truncated = false;

      int entries_read = 0;

      // Planned counts. Identical in preview and apply, because the same code decided
      // them; in apply mode they are what was attempted, and failed says how many of
      // those attempts did not go through.
      int to_create = 0;
      int to_update = 0;
      int unchanged = 0;
      int skipped = 0;

      // Present here, gone from the directory, and left exactly as they are because
      // disable_missing was not set.
      int missing = 0;

      // Present here, gone from the directory, and being disabled.
      int to_disable = 0;

      // Apply only: planned actions that could not be carried out. Each one has its
      // reason in the corresponding entry's error.
      int failed = 0;

      std::vector<DirectorySyncEntry> entries;

      // How many entries were skipped for each reason. The breakdown is the useful
      // half of the skipped count - "22 skipped" is not actionable, "22 in a domain
      // that does not exist here" is.
      std::map<DirectorySyncSkipReason, int> skip_reasons;

      int SkipCount(DirectorySyncSkipReason reason) const;

      // One line, for the application log and for a caller that wants a headline.
      String Summary() const;
   };

   // Works out which hMailServer accounts an LDAP directory says should exist, and, on
   // request, creates and updates them.
   //
   // Two design decisions are worth stating here because they are the ones that will
   // look wrong until the reasoning is known.
   //
   // FIRST: an hMailServer domain takes part only if its Active Directory domain name
   // is set. That field already exists, is already in the administration tools, and is
   // already the place an administrator writes down which directory domain a mail
   // domain corresponds to - so requiring it costs nothing and buys two things. It
   // makes provisioning opt-in per domain, so a server hosting one internal domain and
   // three unrelated customer domains cannot have the customers provisioned into by a
   // search base that was pointed one level too high. And it guarantees every account
   // this class creates has a non-empty Active Directory domain, which matters more
   // than it looks: PasswordValidator falls back to SSPIValidation when LDAP is
   // switched off, SSPIValidation passes the domain straight to LogonUser, and
   // LogonUser with an EMPTY domain validates against this computer's own local Windows
   // accounts. An account provisioned with an empty domain and the user name
   // "administrator" would, the day somebody sets [LDAP] Enabled=0, start accepting the
   // local Administrator password. Deriving the domain from something more convenient
   // and possibly blank is not worth that.
   //
   // SECOND: no password is ever stored, and none is ever generated. The accounts are
   // marked IsAD with the directory user name and domain, which is the linkage the
   // existing LdapDirectoryAuthenticator already reads - so these accounts authenticate
   // through the directory from the moment they are created, and the password column
   // stays empty.
   //
   // The stored empty value is not a way in. PasswordValidator rejects an empty SUPPLIED
   // password outright, so the empty string can only ever be compared against a
   // non-empty one; and it never reaches a comparison anyway, because the IsAD branch
   // sends the account to the directory rather than to the stored hash. The SCRAM paths
   // in IMAP and POP3 exclude IsAD accounts before they look up a hash at all.
   //
   // One exception, stated because "cannot authenticate anybody" would otherwise be a
   // guarantee this code is not in a position to make: an OnClientValidatePassword
   // script runs BEFORE the empty-password check in PasswordValidator::ValidatePassword
   // and can return 0 to admit the logon. A server whose script accepts an empty
   // password already accepts one for every account on it, provisioned or not, so this
   // is not a hazard provisioning introduces - but it is the reason the sentence above
   // says the stored value is not a way IN rather than that no way in exists.
   class DirectorySync
   {
   public:

      // Never throws. Reads the [LDAP] section itself, so a caller cannot run this
      // against a configuration the server is not using.
      //
      // Two things it refuses to do, both for the same reason - an enumeration that saw
      // less than the whole directory makes absent look identical to deleted:
      //
      //   - Nothing is disabled when the enumeration was truncated (more matching
      //     entries than SyncMaxUsers).
      //   - Nothing is disabled when the enumeration produced no provisionable entry at
      //     all. A filter that has been edited into something that matches nothing
      //     returns success with zero entries, and acting on that would disable every
      //     directory-linked account on the server in one pass.
      static DirectorySyncResult Run(const DirectorySyncOptions &options);

      // Text for the two enums, for a report. Here rather than in the caller so that
      // every renderer says the same thing.
      static String DescribeAction(DirectorySyncAction action);
      static String DescribeSkipReason(DirectorySyncSkipReason reason);

   private:

      // The split that makes a preview trustworthy. Plan_ makes every decision and
      // writes nothing; Apply_ writes and decides nothing. A preview is Plan_ on its
      // own, so it cannot describe an action that apply would not take - not because
      // the two were written to agree, but because there is only one of them.
      static bool Plan_(const LdapConfiguration &configuration, const DirectorySyncOptions &options,
         DirectorySyncResult &result);

      static void Apply_(DirectorySyncResult &result);
   };
}
