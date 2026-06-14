// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Exercises the MinimumAcceptedHashAlgorithm policy (hMailServer.ini [Settings]).
   ///    An administrator can refuse authentication for accounts whose stored password
   ///    hash is weaker than a configured Crypt::EncryptionType threshold, forcing those
   ///    passwords to be reset to a strong scheme rather than continuing to be accepted
   ///    (and exposed in any database leak). Accounts created through the COM API are
   ///    stored with the preferred PBKDF2 (4) scheme, so a minimum of Argon2id (5) must
   ///    refuse them while a minimum of PBKDF2 (4) or 0 (disabled) accepts them.
   /// </summary>
   [TestFixture]
   public class HashPolicy : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private const int CryptPbkdf2 = 4;
      private const int CryptArgon2id = 5;

      private void SetMinimumAcceptedHashAlgorithm(int value)
      {
         // The server reads hMailServer.ini from its bin directory (Utilities::GetBinDirectory):
         // a registered install resolves to {InstallLocation}\Bin, while a developer build that
         // is not registered reads the ini next to the running executable (the ProgramFolder).
         // Write the setting to every existing candidate so the file the service actually reads
         // is updated regardless of layout, without creating stray ini files.
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
               WritePrivateProfileString("Settings", "MinimumAcceptedHashAlgorithm", value.ToString(), iniPath),
               "Failed to write MinimumAcceptedHashAlgorithm to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");

         // The setting is cached in IniFileSettings at startup; Reinitialize reloads it.
         _application.Reinitialize();
      }

      [Test]
      [Description("MinimumAcceptedHashAlgorithm refuses an account whose stored hash (PBKDF2) is weaker " +
                   "than the configured minimum (Argon2id) even with the correct password, and accepts it " +
                   "again once the minimum is lowered to the account's own scheme or disabled.")]
      public void TestMinimumAcceptedHashAlgorithmPolicy()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         // Accounts created through the COM API are stored with the preferred PBKDF2 scheme.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "hashpolicy@example.test", "SeC-r3t Pass!");

         try
         {
            // Require Argon2id: the account's PBKDF2 hash is now below the minimum and
            // must be refused even though the password is correct.
            SetMinimumAcceptedHashAlgorithm(CryptArgon2id);
            string error;
            Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, "SeC-r3t Pass!", out error),
               "A PBKDF2 account must be refused when the minimum hash type is Argon2id.");

            // Lower the minimum to PBKDF2: the account meets the policy and logs in.
            SetMinimumAcceptedHashAlgorithm(CryptPbkdf2);
            Assert.IsTrue(new Pop3ClientSimulator().ConnectAndLogon(account.Address, "SeC-r3t Pass!"),
               "A PBKDF2 account must be accepted when the minimum hash type is PBKDF2.");

            // Disable the policy: the account still logs in.
            SetMinimumAcceptedHashAlgorithm(0);
            Assert.IsTrue(new Pop3ClientSimulator().ConnectAndLogon(account.Address, "SeC-r3t Pass!"),
               "A PBKDF2 account must be accepted when the policy is disabled.");
         }
         finally
         {
            // Restore the default (disabled) so later tests are unaffected.
            SetMinimumAcceptedHashAlgorithm(0);
            _settings.ClearLogonFailureList();
         }
      }
   }
}
