// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Domains
{
   /// <summary>
   ///    Each of the three per-domain count limits is a number plus a flag, and the
   ///    Control Panel's domain editor draws them that way: a checkbox
   ///    (MaxNumberOfDistributionListsEnabled) beside a number
   ///    (MaxNumberOfDistributionLists), see ControlPanel/Views/DomainDialog.cs.
   ///    Clearing the checkbox leaves the number alone, exactly as it does for the
   ///    account and alias limits.
   ///
   ///    PreSaveLimitationsCheck tested the flag for accounts and for aliases but
   ///    tested the NUMBER for distribution lists, so clearing that one checkbox did
   ///    nothing at all - the old number went on being enforced - while ticking it
   ///    with the number left at 0 enforced nothing. Both columns arrived in the
   ///    same schema step (Upgrade4301to4400), so there is no legacy state in which
   ///    a number exists without the flag: the only way to reach it is for an
   ///    administrator to have deliberately switched the limit off.
   ///
   ///    NOTE FOR THE LEAD: the first test here fails until the one-line change to
   ///    Common/Persistence/PreSaveLimitationsCheck.cpp is applied. That file is
   ///    outside this change set's file ownership, so the change is reported rather
   ///    than made, and this test arrives with it.
   /// </summary>
   [TestFixture]
   public class DomainLimitEnableFlags : TestFixtureBase
   {
      [Test]
      public void ClearingTheDistributionListLimitStopsItBeingEnforced()
      {
         _domain.MaxNumberOfDistributionLists = 2;
         _domain.MaxNumberOfDistributionListsEnabled = false;
         _domain.Save();

         var recipients = new List<string>();

         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "list1@example.test", recipients);
         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "list2@example.test", recipients);

         // The limit is switched off, so this one must be allowed. Against the
         // unfixed server it is refused with "The maximum number of distribution
         // lists have been created."
         SingletonProvider<TestSetup>.Instance.AddDistributionList(_domain, "list3@example.test", recipients);

         Assert.AreEqual(3, _domain.DistributionLists.Count);
      }

      /// <summary>
      ///    Parity guards, not proof: these two paths already read the flag, and both
      ///    tests pass with and without the change above. They are here so that
      ///    anyone who resolves the inconsistency in the other direction - by making
      ///    accounts and aliases ignore their flags too - is told about it.
      /// </summary>
      [Test]
      public void ClearingTheAccountLimitStopsItBeingEnforced()
      {
         _domain.MaxNumberOfAccounts = 2;
         _domain.MaxNumberOfAccountsEnabled = false;
         _domain.Save();

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test1@example.test", "secret1");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test2@example.test", "secret1");
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test3@example.test", "secret1");

         Assert.AreEqual(3, _domain.Accounts.Count);
      }

      [Test]
      public void ClearingTheAliasLimitStopsItBeingEnforced()
      {
         _domain.MaxNumberOfAliases = 2;
         _domain.MaxNumberOfAliasesEnabled = false;
         _domain.Save();

         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "alias1@example.test", "target@example.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "alias2@example.test", "target@example.test");
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, "alias3@example.test", "target@example.test");

         Assert.AreEqual(3, _domain.Aliases.Count);
      }
   }
}
