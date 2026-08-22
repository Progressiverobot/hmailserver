// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Server-enforced password policy.
   ///
   ///    Until this shipped, the server had opinions about passwords and enforced none:
   ///    Utilities.IsStrongPassword existed, carried a hard-coded deny-list, and nothing
   ///    in the server ever called it. Every mailbox could be "test".
   ///
   ///    The fixture's centre of gravity is not the rules - those are arithmetic - but
   ///    the boundary: a policy is enforced where a password is CHOSEN and nowhere else.
   ///    An account whose password predates the policy must still be able to log in,
   ///    because the alternative is locking people out of mailboxes they can open today
   ///    in the name of making them safer. That is what
   ///    AnExistingPasswordStillWorksAfterThePolicyTightens pins, and it is the test most
   ///    likely to catch a future change that moves the check somewhere convenient.
   /// </summary>
   [TestFixture]
   public class PasswordPolicyTests : TestFixtureBase
   {
      private const string PolicyMinimumLength = "PasswordPolicyMinimumLength";
      private const string PolicyMixedCase = "PasswordPolicyRequireMixedCase";
      private const string PolicyDigit = "PasswordPolicyRequireDigit";
      private const string PolicySymbol = "PasswordPolicyRequireNonAlphanumeric";
      private const string PolicyCommon = "PasswordPolicyRejectCommon";

      [TearDown]
      public new void TearDown()
      {
         foreach (string key in new[] { PolicyMinimumLength, PolicyMixedCase, PolicyDigit, PolicySymbol, PolicyCommon })
            ServerIniFile.SetSetting(key, null);

         _application.Reinitialize();
      }

      private void SetPolicy(params (string Key, string Value)[] settings)
      {
         foreach (var setting in settings)
            ServerIniFile.SetSetting(setting.Key, setting.Value);

         _application.Reinitialize();
      }

      private Account NewAccount(string localPart)
      {
         return SingletonProvider<TestSetup>.Instance.AddAccount(_domain, localPart + "@example.test", "initial-password");
      }

      /// <summary>The COM error text, or null if the assignment was accepted.</summary>
      private static string Refusal(Account account, string password)
      {
         try
         {
            account.Password = password;
            account.Save();
            return null;
         }
         catch (COMException ex)
         {
            return ex.Message;
         }
      }

      [Test]
      [Description("With nothing configured the server behaves exactly as it always has - an existing installation must not start refusing passwords on upgrade")]
      public void OffByDefault()
      {
         var account = NewAccount("policyoff");

         ClassicAssert.IsNull(Refusal(account, "x"),
            "No policy is configured, so even a one-character password must be accepted. " +
            "Anything else means an upgrade silently started rejecting passwords.");
      }

      [Test]
      [Description("A minimum length is enforced, and the refusal names the requirement rather than just saying no")]
      public void MinimumLengthIsEnforcedAndExplained()
      {
         SetPolicy((PolicyMinimumLength, "12"));

         var account = NewAccount("policylength");

         string refusal = Refusal(account, "short");
         ClassicAssert.IsNotNull(refusal, "An eight-character password must be refused when twelve are required.");
         StringAssert.Contains("12", refusal,
            "The refusal must name the requirement: an administrator told only 'not acceptable' " +
            "guesses again at random. Got: " + refusal);

         ClassicAssert.IsNull(Refusal(account, "long-enough-to-pass"),
            "A password that meets the requirement must be accepted.");
      }

      [Test]
      [Description("Character-class rules each refuse independently and accept together")]
      public void CharacterClassRulesAreEnforced()
      {
         // Created BEFORE the policy, which is both the realistic order and the only
         // one that works: the fixture's own setup password would itself be refused.
         var account = NewAccount("policyclasses");

         SetPolicy((PolicyMixedCase, "1"), (PolicyDigit, "1"), (PolicySymbol, "1"));

         ClassicAssert.IsNotNull(Refusal(account, "alllowercase1!"), "No upper case: must be refused.");
         ClassicAssert.IsNotNull(Refusal(account, "MixedCaseOnly!"), "No digit: must be refused.");
         ClassicAssert.IsNotNull(Refusal(account, "MixedCase1234"), "No symbol: must be refused.");

         ClassicAssert.IsNull(Refusal(account, "MixedCase1!"),
            "All three requirements met, so it must be accepted.");
      }

      [Test]
      [Description("The common-password list is compared case-insensitively - Password1 is not meaningfully stronger than password1")]
      public void CommonPasswordsAreRefusedRegardlessOfCase()
      {
         SetPolicy((PolicyCommon, "1"));

         var account = NewAccount("policycommon");

         ClassicAssert.IsNotNull(Refusal(account, "password"), "The most common password of all must be refused.");
         ClassicAssert.IsNotNull(Refusal(account, "PASSWORD"), "Case does not make it less guessable.");
         ClassicAssert.IsNotNull(Refusal(account, "Qwerty123"), "Nor does capitalising the first letter.");

         ClassicAssert.IsNull(Refusal(account, "not-on-any-list-of-common-ones"),
            "A password that is not on the list must be accepted.");
      }

      [Test]
      [Description("A password containing the account name is refused - no length rule catches the most guessable choice there is")]
      public void APasswordContainingTheAccountNameIsRefused()
      {
         SetPolicy((PolicyMinimumLength, "8"));

         var account = NewAccount("harrison");

         string refusal = Refusal(account, "harrison2026");
         ClassicAssert.IsNotNull(refusal,
            "It is twelve characters and passes the length rule, which is exactly why this rule exists.");
         StringAssert.Contains("account name", refusal, "Got: " + refusal);
      }

      [Test]
      [Description("THE boundary: a password chosen before the policy existed still authenticates afterwards")]
      public void AnExistingPasswordStillWorksAfterThePolicyTightens()
      {
         var account = NewAccount("policyexisting");

         // Chosen while nothing was configured, and deliberately something the policy
         // below would refuse outright.
         ClassicAssert.IsNull(Refusal(account, "weak"), "Setting it must succeed while no policy is configured.");

         SetPolicy((PolicyMinimumLength, "16"), (PolicyMixedCase, "1"), (PolicyDigit, "1"), (PolicyCommon, "1"));

         // The policy is now far stricter than the stored password. The account must
         // still open: a policy applied to VERIFICATION rather than to CHOICE locks
         // people out of mailboxes that worked yesterday, which is a worse outcome than
         // the weak password it was trying to correct.
         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3.ConnectAndLogon(account.Address, "weak"),
            "The stored password must still authenticate. If this fails, the policy has been " +
            "applied to a verification path and every account with an older password is locked out.");

         var imap = new ImapClientSimulator();
         ClassicAssert.IsTrue(imap.ConnectAndLogon(account.Address, "weak"),
            "...over IMAP as well as POP3.");
         imap.Disconnect();

         // ...and only a NEW choice is held to it.
         ClassicAssert.IsNotNull(Refusal(account, "still-too-weak"),
            "Choosing a new password must be held to the policy that is now configured.");
      }

      [Test]
      [Description("An app password is held to the same policy - a credential opening the same mailbox must not be the way round it")]
      public void AppPasswordsAreHeldToTheSamePolicy()
      {
         var account = NewAccount("policyapp");

         SetPolicy((PolicyMinimumLength, "20"), (PolicyDigit, "1"));
         var passwords = account.AppPasswords;
         var appPassword = passwords.Add();
         appPassword.Name = "Policy check";

         Assert.Throws<COMException>(() => appPassword.SetPassword("sixteencharacters"),
            "Sixteen characters clears this credential's own floor of twelve but not the " +
            "configured policy, and an app password opens the mailbox exactly as the account " +
            "password does.");

         // The generated form must still satisfy whatever is configured, or the feature
         // becomes unusable the moment a policy is set.
         string generated = appPassword.Generate();
         appPassword.Save();

         ClassicAssert.GreaterOrEqual(generated.Replace("-", "").Length, 20,
            "A generated secret must clear a 20-character minimum on its own. Got: " + generated);

         var pop3 = new Pop3ClientSimulator();
         ClassicAssert.IsTrue(pop3.ConnectAndLogon(account.Address, generated),
            "And it must still authenticate.");
      }
   }
}
