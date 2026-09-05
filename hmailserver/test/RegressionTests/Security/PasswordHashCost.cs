// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    The work factor of a new password hash used to be a compile-time constant, and
   ///    a hash derived under an older, cheaper constant stayed that way for the life of
   ///    the account. Three [Settings] keys now set it - PasswordHashIterations for
   ///    PBKDF2, PasswordHashMemoryKB and PasswordHashTimeCost for Argon2id - and a hash
   ///    cheaper than the configured value is re-derived on the next successful logon,
   ///    the moment the clear text is known to match. Upward only: lowering a value
   ///    applies to new hashes and leaves stored ones alone, the same never-downgrade
   ///    rule the scheme upgrade follows.
   ///
   ///    The stored hash is self-describing ($h1$&lt;iterations&gt;$..., $a2$&lt;memory&gt;$&lt;passes&gt;$...)
   ///    and Account.Password returns it as stored, so the assertions read the prefix.
   /// </summary>
   [TestFixture]
   public class PasswordHashCost : TestFixtureBase
   {
      private const int CryptPbkdf2 = 4;
      private const int CryptArgon2id = 5;
      private const string Password = "SeC-r3t Pass!";

      private void WriteSetting(string key, string value)
      {
         // The same two candidates PasswordPepper writes: a registered install reads
         // {InstallLocation}\Bin\hMailServer.ini, a developer build the one beside the
         // executable. Every existing candidate is updated so the file the service
         // reads is the one that changed, without creating stray ini files.
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private void Apply()
      {
         _application.Reinitialize();
         _settings.ClearLogonFailureList();
      }

      /// <summary>The stored hash, read back past the account cache.</summary>
      private string StoredPassword(Account account)
      {
         _settings.Cache.Clear();
         return _domain.Accounts.get_ItemByAddress(account.Address).Password;
      }

      private void AssertLogonSucceeds(Account account)
      {
         // QUIT as well as connect: POP3 locks the mailbox for the session, and a test
         // that logs on twice would otherwise be told [IN-USE] the second time.
         var simulator = new Pop3ClientSimulator();
         string error;
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, Password, out error), error);
         simulator.QUIT();
      }

      [SetUp]
      public void DisableAutoBan()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
      }

      [TearDown]
      public void RestoreDefaults()
      {
         WriteSetting("PasswordHashIterations", "0");
         WriteSetting("PasswordHashMemoryKB", "0");
         WriteSetting("PasswordHashTimeCost", "0");
         WriteSetting("PreferredHashAlgorithm", CryptPbkdf2.ToString());
         Apply();
      }

      [Test]
      [Description("A new PBKDF2 hash is derived with the configured iteration count, and verifies")]
      public void NewPbkdf2HashCarriesTheConfiguredIterations()
      {
         WriteSetting("PasswordHashIterations", "300000");
         Apply();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "cost1@example.test", Password);

         // Fails against the build before this one, where the count was the compiled-in
         // 210,000 whatever the ini said.
         StringAssert.StartsWith("$h1$300000$", StoredPassword(account));
         AssertLogonSucceeds(account);
      }

      [Test]
      [Description("A hash derived under a lower iteration count than is configured now is re-derived on the next successful logon")]
      public void LogonReDerivesACheaperHashUnderTheRaisedIterations()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "cost2@example.test", Password);
         StringAssert.StartsWith("$h1$210000$", StoredPassword(account));

         WriteSetting("PasswordHashIterations", "300000");
         Apply();

         // Nothing is rewritten by the setting alone; it takes a successful logon.
         StringAssert.StartsWith("$h1$210000$", StoredPassword(account));

         AssertLogonSucceeds(account);

         StringAssert.StartsWith("$h1$300000$", StoredPassword(account));

         // And the re-derived hash is one the password still verifies against.
         AssertLogonSucceeds(account);
         StringAssert.StartsWith("$h1$300000$", StoredPassword(account));
      }

      [Test]
      [Description("Lowering the iteration count applies to new hashes only: a stored, costlier hash is left as it is")]
      public void LoweringTheIterationsLeavesStoredHashesAlone()
      {
         WriteSetting("PasswordHashIterations", "300000");
         Apply();

         var costly = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "cost3a@example.test", Password);
         StringAssert.StartsWith("$h1$300000$", StoredPassword(costly));

         WriteSetting("PasswordHashIterations", "150000");
         Apply();

         AssertLogonSucceeds(costly);
         StringAssert.StartsWith("$h1$300000$", StoredPassword(costly), "A logon must not rewrite a hash to something cheaper.");

         var fresh = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "cost3b@example.test", Password);
         StringAssert.StartsWith("$h1$150000$", StoredPassword(fresh));
         AssertLogonSucceeds(fresh);
      }

      [Test]
      [Description("A new Argon2id hash is derived with the configured memory and passes, and verifies")]
      public void NewArgon2idHashCarriesTheConfiguredMemoryAndPasses()
      {
         WriteSetting("PreferredHashAlgorithm", CryptArgon2id.ToString());
         WriteSetting("PasswordHashMemoryKB", "32768");
         WriteSetting("PasswordHashTimeCost", "3");
         Apply();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "cost4@example.test", Password);

         StringAssert.StartsWith("$a2$32768$3$1$", StoredPassword(account));
         AssertLogonSucceeds(account);
      }

      [Test]
      [Description("A work factor outside its bounds is reported and read as the default, so a typo can neither weaken a hash nor stall every logon")]
      public void OutOfRangeWorkFactorFallsBackToTheDefaultAndIsReported()
      {
         LogHandler.DeleteErrorLog();

         WriteSetting("PasswordHashIterations", "1");
         Apply();

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "cost5@example.test", Password);

            StringAssert.StartsWith("$h1$210000$", StoredPassword(account));
            AssertLogonSucceeds(account);

            CustomAsserts.AssertReportedError("PasswordHashIterations");
         }
         finally
         {
            // The report repeats on every settings load until the value is corrected,
            // which RestoreDefaults does; nothing after this test should see it.
            WriteSetting("PasswordHashIterations", "0");
            Apply();
            LogHandler.DeleteErrorLog();
         }
      }
   }
}
