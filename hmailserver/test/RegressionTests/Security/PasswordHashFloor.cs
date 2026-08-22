// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    PreferredHashAlgorithm names the scheme a NEW secret is stored under, and
   ///    three of its six legal values are not password hashes at all: 0 is no
   ///    hashing, 1 is reversible, 2 is MD5.
   ///
   ///    Two of the five places that read it hand the value straight to Crypt as the
   ///    scheme to store an account password or an app password under, so a 0 or a 1
   ///    there writes credentials in a form anybody holding the database can read
   ///    back. The value is therefore clamped where it is read, once, rather than
   ///    guarded at each call site - a new call site added later inherits the floor
   ///    instead of having to remember it.
   ///
   ///    The test that matters is not that a bad value is refused. It is that a
   ///    password set while the bad value was configured is still stored safely, and
   ///    still works - because "the setting was ignored" is only a good outcome if
   ///    the account is usable afterwards.
   /// </summary>
   [TestFixture]
   public class PasswordHashFloor : TestFixtureBase
   {
      [Test]
      [Description("A password set while PreferredHashAlgorithm names a non-hashing scheme is stored under the fallback and the account still authenticates - the setting is refused, not obeyed and not fatal")]
      public void ANonHashingPreferredAlgorithmIsRefusedAndTheAccountStillWorks()
      {
         string address = SingletonProvider<TestSetup>.Instance
            .AddAccount(_domain, "hashfloor@example.test", "test").Address;

         try
         {
            // 1 is Blowfish: reversible, and the worst of the three because it looks
            // like encryption. Anything below SHA256 must be refused the same way.
            ServerIniFile.SetSetting("PreferredHashAlgorithm", "1");
            RestartServerAndReacquireCom();

            // Setting a password is what exercises the value - it is the scheme the
            // new secret is stored under.
            var account = _application.Domains[0].Accounts.get_ItemByAddress(address);
            account.Password = "floor-test-password";
            account.Save();

            // The account has to be usable. A floor that stored something unusable
            // would be a worse failure than the setting it refused.
            Pop3ClientSimulator.AssertMessageCount(address, "floor-test-password", 0);

            // And the refusal is announced rather than silent: an administrator who
            // set this deliberately is owed the reason it did not take.
            string errorLog = LogHandler.ReadErrorLog();

            StringAssert.Contains("PreferredHashAlgorithm", errorLog,
               "The refusal must name the setting that was ignored. Error log: " + errorLog);
         }
         finally
         {
            ServerIniFile.SetSetting("PreferredHashAlgorithm", null);
            RestartServerAndReacquireCom();
            LogHandler.DeleteErrorLog();
         }
      }
   }
}
