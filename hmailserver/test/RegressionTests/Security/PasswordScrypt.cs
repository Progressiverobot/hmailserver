// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.POP3;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    scrypt (RFC 7914) as a password-hashing scheme: PreferredHashAlgorithm=7.
   ///
   ///    The third strong KDF beside PBKDF2 and Argon2id, with OWASP's parameters
   ///    (N=2^17, r=8, p=1) and the same self-describing "$s2$..." hash shape the other
   ///    two use, so the stored row says what derived it. It sits at 7 because 6 is
   ///    the DPAPI scheme for stored secrets; a preference of 6 is refused, as it was.
   ///    Like Argon2id it is peppered and cannot serve SCRAM, and like every scheme a
   ///    logon never downgrades it.
   /// </summary>
   [TestFixture]
   public class PasswordScrypt : TestFixtureBase
   {
      private const string Password = "scrypt-test-password-9";
      private const int CryptPbkdf2 = 4;
      private const int CryptArgon2id = 5;
      private const int CryptScrypt = 7;

      [SetUp]
      public void DisableAutoBan()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
      }

      [TearDown]
      public void RestoreDefaults()
      {
         WriteSetting("PreferredHashAlgorithm", CryptPbkdf2.ToString());
         WriteSetting("MinimumAcceptedHashAlgorithm", "0");
         Apply();
      }

      [Test]
      public void Argon2idAndScryptArePeersSoNeitherPreferenceRewritesTheOther()
      {
         // Schemes are compared by strength, not by the number that identifies them:
         // 7 is not "more than" 5 here, so a preference for scrypt leaves an Argon2id
         // account as it is, and the other way round, rather than rewriting every
         // memory-hard hash to the other memory-hard hash on its next logon.
         WriteSetting("PreferredHashAlgorithm", CryptArgon2id.ToString());
         Apply();
         var argonAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt5@example.test", Password);
         StringAssert.StartsWith("$a2$", StoredPassword(argonAccount));

         WriteSetting("PreferredHashAlgorithm", CryptScrypt.ToString());
         Apply();
         var scryptAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt6@example.test", Password);
         StringAssert.StartsWith("$s2$", StoredPassword(scryptAccount));

         AssertLogonSucceeds(argonAccount);
         StringAssert.StartsWith("$a2$", StoredPassword(argonAccount));

         WriteSetting("PreferredHashAlgorithm", CryptArgon2id.ToString());
         Apply();
         AssertLogonSucceeds(scryptAccount);
         StringAssert.StartsWith("$s2$", StoredPassword(scryptAccount));
      }

      [Test]
      public void AMinimumOfEitherMemoryHardSchemeAcceptsBoth()
      {
         WriteSetting("PreferredHashAlgorithm", CryptScrypt.ToString());
         Apply();
         var scryptAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt7@example.test", Password);

         WriteSetting("PreferredHashAlgorithm", CryptArgon2id.ToString());
         Apply();
         var argonAccount = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt8@example.test", Password);

         foreach (var minimum in new[] { CryptArgon2id, CryptScrypt })
         {
            WriteSetting("MinimumAcceptedHashAlgorithm", minimum.ToString());
            Apply();

            AssertLogonSucceeds(scryptAccount);
            AssertLogonSucceeds(argonAccount);
         }
      }

      [Test]
      public void ANewPasswordIsStoredAsScryptAndVerifies()
      {
         WriteSetting("PreferredHashAlgorithm", CryptScrypt.ToString());
         Apply();

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt1@example.test", Password);

         var stored = StoredPassword(account);
         StringAssert.StartsWith("$s2$17$8$1$", stored);
         Assert.AreEqual(7, stored.Split('$').Length, "The stored hash is not the six-field scrypt shape: " + stored);

         AssertLogonSucceeds(account);
         AssertLogonFails(account, "not-the-password");
      }

      [Test]
      public void ALogonNeverDowngradesAScryptHash()
      {
         WriteSetting("PreferredHashAlgorithm", CryptScrypt.ToString());
         Apply();
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt2@example.test", Password);
         StringAssert.StartsWith("$s2$", StoredPassword(account));

         // The preference goes back to PBKDF2; the stronger hash stays.
         WriteSetting("PreferredHashAlgorithm", CryptPbkdf2.ToString());
         Apply();
         AssertLogonSucceeds(account);
         StringAssert.StartsWith("$s2$", StoredPassword(account));
      }

      [Test]
      public void APbkdf2AccountIsUpgradedToScryptOnLogonWhenPreferred()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt3@example.test", Password);
         StringAssert.StartsWith("$h1$", StoredPassword(account));

         WriteSetting("PreferredHashAlgorithm", CryptScrypt.ToString());
         Apply();

         AssertLogonSucceeds(account);
         StringAssert.StartsWith("$s2$", StoredPassword(account));
         AssertLogonSucceeds(account);
      }

      [Test]
      public void ScramIsNotServedFromAScryptHash()
      {
         // SCRAM needs the PBKDF2 SaltedPassword; a scrypt account answers the
         // mechanism the way an Argon2id one does - refused, without disclosing why.
         WriteSetting("PreferredHashAlgorithm", CryptScrypt.ToString());
         Apply();
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "scrypt4@example.test", Password);

         using (var tc = new TcpConnection())
         {
            Assert.IsTrue(tc.Connect(110));
            tc.ReadUntil("+OK");
            string final = Pop3SaslTestClient.AuthenticateScram(tc, account.Address, Password);
            Assert.IsTrue(final.StartsWith("-ERR"), "SCRAM must be refused for a scrypt account. Got: " + final);
            tc.Disconnect();
         }

         AssertLogonSucceeds(account);
      }

      // ----------------------------------------------------------------------

      private void WriteSetting(string key, string value)
      {
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
         var simulator = new Pop3ClientSimulator();
         string error;
         Assert.IsTrue(simulator.ConnectAndLogon(account.Address, Password, out error), error);
         simulator.QUIT();
      }

      private static void AssertLogonFails(Account account, string password)
      {
         var simulator = new Pop3ClientSimulator();
         string error;
         Assert.IsFalse(simulator.ConnectAndLogon(account.Address, password, out error));
      }
   }
}
