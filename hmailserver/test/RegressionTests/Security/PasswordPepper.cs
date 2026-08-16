// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Exercises the optional server-side password pepper (hMailServer.ini
   ///    [Settings] PasswordPepper). When set, the pepper is applied as an
   ///    HMAC-SHA-256 keyed transform of the password before the Argon2id hash is
   ///    computed and verified, so a stolen password database cannot be brute-forced
   ///    without also stealing the pepper (which lives outside the database). The
   ///    pepper deliberately applies to Argon2id only: PBKDF2 hashes double as the
   ///    SCRAM SaltedPassword, which clients reconstruct from the raw password, so
   ///    peppering PBKDF2 would break SCRAM. The pepper therefore requires
   ///    PreferredHashAlgorithm = Argon2id (5) to take effect.
   /// </summary>
   [TestFixture]
   public class PasswordPepper : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private const int CryptPbkdf2 = 4;
      private const int CryptArgon2id = 5;

      private void WriteSetting(string key, string value)
      {
         // The server reads hMailServer.ini from its bin directory (Utilities::GetBinDirectory):
         // a registered install resolves to {InstallLocation}\Bin, while a developer build that
         // is not registered reads the ini next to the running executable (the ProgramFolder).
         // Write to every existing candidate so the file the service actually reads is updated
         // regardless of layout, without creating stray ini files.
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      [Test]
      [Description("A configured PasswordPepper is applied to Argon2id password hashing: an account " +
                   "created while the pepper is set authenticates with the correct password, fails once " +
                   "the pepper is changed (the stored hash no longer matches), and authenticates again " +
                   "once the original pepper is restored.")]
      public void TestPasswordPepperAffectsArgon2idVerification()
      {
         const string pepper = "pepper-secret-A";
         const string password = "SeC-r3t Pass!";

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         try
         {
            // Prefer Argon2id and configure a pepper, then create the account so it is
            // stored as a peppered Argon2id hash.
            WriteSetting("PreferredHashAlgorithm", CryptArgon2id.ToString());
            WriteSetting("PasswordPepper", pepper);
            _application.Reinitialize();

            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "pepper@example.test", password);

            string error;
            Assert.IsTrue(new Pop3ClientSimulator().ConnectAndLogon(account.Address, password, out error),
               "Login should succeed with the configured pepper. " + error);

            // Change the pepper: the stored hash was computed with the old pepper, so the
            // correct password no longer reproduces it.
            WriteSetting("PasswordPepper", "pepper-secret-B");
            _application.Reinitialize();
            _settings.ClearLogonFailureList();
            Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, password, out error),
               "Login must fail when the pepper changes.");

            // Restore the original pepper: the correct password reproduces the stored hash again.
            WriteSetting("PasswordPepper", pepper);
            _application.Reinitialize();
            _settings.ClearLogonFailureList();
            Assert.IsTrue(new Pop3ClientSimulator().ConnectAndLogon(account.Address, password, out error),
               "Login should succeed again once the original pepper is restored. " + error);
         }
         finally
         {
            // Restore defaults so later tests are unaffected.
            WriteSetting("PasswordPepper", "");
            WriteSetting("PreferredHashAlgorithm", CryptPbkdf2.ToString());
            _application.Reinitialize();
            _settings.ClearLogonFailureList();
         }
      }
   }
}
