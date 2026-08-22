// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later
// Password reuse history and password age.
//
// The two halves of "password expiry" that are worth having, and they are worth
// having together rather than separately: expiry without history teaches people
// to alternate between two passwords, which is measurably worse than not
// expiring at all because it converts one secret into two known ones.
//
// HISTORY is unambiguous. When a password is changed, the OLD hash is kept and
// the new password is refused if it matches one of the last N. Nothing about
// that can lock anybody out - it only ever refuses a change, at the moment
// somebody is standing there able to pick something else.
//
// EXPIRY is not unambiguous, and the honest version of the caveat belongs here
// rather than in a release note. This server has no self-service password
// change: IMAP, POP3 and SMTP have no mechanism for it, there is no web surface,
// and the Control Panel is an administrator's tool. So an expired password means
// an administrator has to reset it. That is a real workflow in an organisation
// with a helpdesk, and a very bad one in a two-person company on a Sunday.
//
// It is therefore off by default, and the Control Panel says the consequence in
// the setting's own description rather than leaving it to be discovered. What is
// NOT done here is the thing that would make it silently dangerous: an account
// whose password predates this feature is stamped at upgrade time rather than
// treated as ancient, so switching expiry on starts everybody's clock from that
// moment instead of locking out every mailbox at once.
//
// Active Directory accounts are exempt, and not as an oversight. Their password
// does not live here - it lives in the directory, which has its own expiry
// policy and its own change mechanism - so this server aging it out would refuse
// a credential it does not own and cannot help anybody renew.

#pragma once

namespace HM
{
   class Account;

   class PasswordHistory
   {
   public:
      // True when the new password matches one of the account's last N hashes, or
      // its current one. Compared with the same verifier that authenticates, so a
      // password stored under an older scheme is still recognised as a repeat.
      static bool IsReuse(std::shared_ptr<const Account> account, const String &newPassword);

      // Records the account's CURRENT hash as history, then trims to the configured
      // depth. Called immediately before the new password replaces it.
      static void Record(std::shared_ptr<const Account> account);

      // Everything for one account, for when the account is deleted.
      static void DeleteByAccountID(__int64 accountID);

      // True when the account's password is older than the configured maximum age.
      // False when expiry is off, when the account is an Active Directory one, or
      // when the stored date cannot be read - never lock somebody out on the
      // strength of an unparseable timestamp.
      static bool HasExpired(std::shared_ptr<const Account> account);

      // The reply an expired account gets. Deliberately the SAME text as a wrong
      // password: a caller that could distinguish them would learn which accounts
      // exist and which are dormant, and no mail client would do anything useful
      // with the difference anyway. The distinction is made in the LOG, where an
      // administrator can act on it.
      static void ReportExpired(std::shared_ptr<const Account> account);
   };
}
