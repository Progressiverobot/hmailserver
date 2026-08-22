// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Server-enforced password policy.
//
// Until now this server had opinions about passwords and enforced none of them:
// PasswordGenerator::IsStrongPassword exists, carries a hard-coded seven-entry
// deny-list and a longer-than-four-characters rule, is reachable over COM as
// Utilities.IsStrongPassword - and nothing in the server has ever called it. An
// administrator could set every mailbox to "test" and the server would agree.
//
// Enforced where a password is CHOSEN, and nowhere else. That is one place:
// InterfaceAccount::put_Password, which every route to setting an account
// password goes through - the Control Panel, DBSetup, scripts, the API. It is
// deliberately NOT applied where a password is verified, re-hashed by the
// upgrade path, or read back from the database, because those are not choices:
// a policy that rejected an existing password at logon would lock people out of
// mailboxes they can still open today, which is a worse outcome than the weak
// password it was trying to fix.
//
// Everything is off by default (minimum length 0, every flag false). An existing
// installation therefore behaves exactly as it did until an administrator
// decides otherwise. Retroactive policy is the failure mode this whole area is
// famous for.
//
// Evaluate() is pure - configuration in, verdict out, no INI access and no
// clock - so the rules can be pinned by tests without a configured server, in
// the same shape OAuth2TokenValidator::ValidateWithConfig uses for the same
// reason.
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   struct PasswordPolicyConfig
   {
      int minimum_length = 0;
      bool require_mixed_case = false;
      bool require_digit = false;
      bool require_non_alphanumeric = false;
      bool reject_common = false;
   };

   class PasswordPolicy
   {
   public:
      // True when the password satisfies the policy. On false, out_reason carries a
      // sentence naming the requirement that was not met - and NOT the password.
      //
      // The reason is written to be shown to whoever is choosing the password: an
      // administrator told only "the password is not acceptable" tries again at
      // random, and the requirement is not a secret. What it never does is say which
      // requirements the attempt DID satisfy, which would turn a rejection into a
      // description of the string that was tried.
      static bool Evaluate(const PasswordPolicyConfig &config, const String &username,
                           const String &password, String &out_reason);

      // The configured policy, read from the INI.
      static PasswordPolicyConfig GetConfiguredPolicy();

      // Convenience for the call sites: reads the configuration and evaluates. Returns
      // true when there is nothing to enforce.
      static bool IsAcceptable(const String &username, const String &password, String &out_reason);

   private:
      // A password identical to the local part of the address, or containing it, is
      // refused whenever any policy is active. It is the single most guessable choice
      // an account holder can make and no length requirement catches it.
      static bool ContainsUsername_(const String &username, const String &password);
   };
}
