// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Linq;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Exercises the at-rest protection of reversible stored secrets (B3). The
   ///    DB-stored route/fetch/relayer passwords are written through the shared
   ///    Crypt secret envelope (machine-scoped Windows DPAPI by default, legacy
   ///    Blowfish when <c>ProtectStoredSecretsWithDPAPI=0</c>) and read back
   ///    transparently. The fetch-account password is used as the witness because
   ///    it is the only one of the three the COM API exposes for both read and
   ///    write; it travels through the identical encrypt-on-save / decrypt-on-load
   ///    path as the route and relayer secrets.
   ///
   ///    These tests assert the value survives the full COM -> encrypt -> database
   ///    -> decrypt -> COM round-trip. The DPAPI primitive itself (and its
   ///    machine-binding / tamper behaviour) is covered by the in-server
   ///    DataProtector self-test.
   /// </summary>
   [TestFixture]
   public class SecretProtection : TestFixtureBase
   {

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

      private void SetSecretProtection(bool dpapiEnabled)
      {
         WriteSetting("ProtectStoredSecretsWithDPAPI", dpapiEnabled ? "1" : "0");
         _application.Reinitialize();
      }

      private static string SaveFetchAccountPasswordAndReload(Account account, string password)
      {
         // Add and persist a fetch account carrying the secret.
         var fa = account.FetchAccounts.Add();
         fa.Name = "secret-test";
         fa.ServerAddress = "127.0.0.1";
         fa.Port = 1110;
         fa.Username = "remote-user";
         fa.Password = password;
         fa.Save();

         // account.FetchAccounts re-reads the collection from the database on every
         // access (see InterfaceAccount::get_FetchAccounts -> Refresh), so this is a
         // genuine decrypt-on-load, not the cached in-memory value.
         return account.FetchAccounts.get_Item(0).Password;
      }

      [Test]
      [Description("With DPAPI protection on (the default), a stored fetch-account password survives the " +
                   "encrypt-to-database and decrypt-on-load round-trip unchanged.")]
      public void TestStoredSecretRoundTripsUnderDpapi()
      {
         SetSecretProtection(true);
         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "secret-dpapi@example.test", "pw");
            const string secret = "R0ute-Relay-Secret!_2026";
            Assert.AreEqual(secret, SaveFetchAccountPasswordAndReload(account, secret));
         }
         finally
         {
            SetSecretProtection(true);
         }
      }

      [Test]
      [Description("A non-ASCII stored secret round-trips byte-for-byte under DPAPI (UTF-8 path).")]
      public void TestStoredUnicodeSecretRoundTripsUnderDpapi()
      {
         SetSecretProtection(true);
         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "secret-unicode@example.test", "pw");
            const string secret = "пароль-Ω-密码-2026";
            Assert.AreEqual(secret, SaveFetchAccountPasswordAndReload(account, secret));
         }
         finally
         {
            SetSecretProtection(true);
         }
      }

      [Test]
      [Description("With DPAPI protection disabled the legacy Blowfish scheme is used and the stored secret " +
                   "still round-trips, proving the backward-compatible path keeps working.")]
      public void TestStoredSecretRoundTripsWithDpapiDisabled()
      {
         SetSecretProtection(false);
         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "secret-blowfish@example.test", "pw");
            const string secret = "Legacy-Blowfish-Secret!_2026";
            Assert.AreEqual(secret, SaveFetchAccountPasswordAndReload(account, secret));
         }
         finally
         {
            // Restore the secure default for subsequent tests.
            SetSecretProtection(true);
         }
      }

      [Test]
      [Description("A route relayer auth password (write-only over COM) is accepted and persisted through the " +
                   "DPAPI envelope, and the route reloads from the database without error.")]
      public void TestRouteAuthSecretPersists()
      {
         SetSecretProtection(true);
         try
         {
            var route = _settings.Routes.Add();
            route.DomainName = "secret-route.example.test";
            route.TargetSMTPHost = "127.0.0.1";
            route.TargetSMTPPort = 25;
            route.RelayerRequiresAuth = true;
            route.RelayerAuthUsername = "relay-user";
            route.SetRelayerAuthPassword("Relay-Auth-Secret!_2026");
            route.Save();

            // Reload from the database (fresh collection) and confirm the non-secret
            // fields survived; the password itself is write-only over COM.
            var reloaded = _settings.Routes.get_ItemByName("secret-route.example.test");
            Assert.AreEqual("relay-user", reloaded.RelayerAuthUsername);
            Assert.IsTrue(reloaded.RelayerRequiresAuth);

            _settings.Routes.DeleteByDBID(reloaded.ID);
         }
         finally
         {
            SetSecretProtection(true);
         }
      }
   }
}
