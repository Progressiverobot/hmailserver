// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

namespace HM
{
   // What an LDAP authentication attempt concluded.
   //
   // Four values rather than a bool, because the caller has to make a different
   // decision in each case and the existing Active Directory path proves what happens
   // when it cannot. ResultNotConfigured is not a failure at all - it means LDAP had
   // no opinion and the caller should carry on as it did before this code existed.
   enum class LdapAuthenticationResult
   {
      ResultAccepted = 0,

      // The directory answered and refused: wrong password, no such user, disabled or
      // locked-out account. A normal event on an internet-facing server.
      ResultRejected = 1,

      // The directory could not answer, or answered something unrelated to the
      // credentials. Every account authenticated this way is affected, and the reason
      // has been reported (throttled) rather than logged per attempt.
      ResultUnavailable = 2,

      // LDAP is switched off, or switched on without enough configuration to attempt
      // anything. The caller falls back to its existing behaviour.
      ResultNotConfigured = 3
   };

   // Authenticates a mail account against an LDAP directory by binding as the user.
   //
   // Why this exists, measured rather than assumed. hMailServer's existing Active
   // Directory support calls LogonUser, which requires the mail server HOST to be
   // domain-joined. LogonUser was called on a workgroup machine with four different
   // domain values - an impossible name, a plausible name that does not exist, the
   // real name of a domain controller answering LDAP on the same subnet, and the
   // machine's own name with a nonexistent user - and all four returned the identical
   // ERROR_LOGON_FAILURE (1326). Windows will not tell an unjoined computer whether a
   // domain even exists. So on a mail server that is not domain-joined, which is most
   // of them because a mail server usually sits in a DMZ, an AD-linked account can
   // never authenticate and every attempt is indistinguishable from a mistyped
   // password. An LDAP bind has no such requirement.
   class LdapDirectoryAuthenticator
   {
   public:

      // Never throws: an exception escaping here would abort a protocol command, and
      // an authentication that cannot be completed has to be a refusal rather than a
      // dropped session. domain and username are the account's Active Directory
      // domain and user name; accountAddress is the mail address, used for the %m
      // substitution and for diagnostics.
      static LdapAuthenticationResult ValidateUser(const String &domain, const String &username,
         const String &accountAddress, const String &password);
   };
}
