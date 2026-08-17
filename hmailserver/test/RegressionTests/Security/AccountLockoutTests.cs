// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Per-name authentication lockout. The per-IP auto-ban cannot see a
   ///    distributed attack - a botnet spending one guess per address per
   ///    account never crosses any single address's threshold - so the lockout
   ///    counts by the name being guessed at, the one thing such an attack
   ///    cannot vary. A locked name refuses even the CORRECT password with the
   ///    ordinary invalid-credentials reply, deliberately: a distinct reply
   ///    would confirm to the attacker that the account exists and that they
   ///    locked it. Expiry arithmetic (window ageing, lock duration, clock
   ///    steps) is pinned by the server self-tests with an injected clock;
   ///    these tests cover the wiring end to end.
   /// </summary>
   [TestFixture]
   public class AccountLockoutTests : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "lockout@example.test", "test");
      }

      private static string PopLogon(string user, string password)
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(110));
         socket.ReadUntil("+OK");
         socket.Send("USER " + user + "\r\n");
         socket.ReadUntil("+OK");
         socket.Send("PASS " + password + "\r\n");
         var response = socket.ReadUntil("\r\n");
         socket.Send("QUIT\r\n");
         socket.Disconnect();
         return response;
      }

      [Test]
      [Description("Three failures lock the name: the CORRECT password is then refused with the ordinary reply, while another account is untouched")]
      public void TheNameLocksAndTheReplyLeaksNothing()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "bystander@example.test", "test");

         try
         {
            SetIniSetting("AccountLockoutThreshold", "3");
            _application.Reinitialize();

            // The ordinary wrong-password refusal, measured on an account that is
            // NOT locked. Comparing against this rather than against a substring
            // is the point: "Invalid user name or password" is a prefix of every
            // POP3 refusal the server can emit, including a lockout-announcing
            // one, so a Contains() assertion could not fail for the leak it exists
            // to rule out.
            string ordinaryRefusal = PopLogon("bystander@example.test", "alsowrong");
            StringAssert.Contains("-ERR", ordinaryRefusal);

            for (int i = 0; i < 3; i++)
               StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "wrong" + i));

            var locked = PopLogon("lockout@example.test", "test");
            ClassicAssert.AreEqual(ordinaryRefusal, locked,
               "The refusal for a locked name must be byte-identical to an ordinary wrong-password " +
               "refusal. Anything distinguishable tells an attacker both that the account exists and " +
               "that they have successfully locked it. Locked: '" + locked + "' ordinary: '" + ordinaryRefusal + "'");

            // The point of per-name: the neighbour is untouched by the attack.
            StringAssert.Contains("+OK", PopLogon("bystander@example.test", "test"),
               "Another account must be unaffected by this name's lockout.");
         }
         finally
         {
            SetIniSetting("AccountLockoutThreshold", null);
            _application.Reinitialize();

            // The lockout entry for the test name dies with the reinitialize?
            // No - the store is in-memory in the SERVICE process, which did not
            // restart. Clear it the way a real recovery does: a successful
            // logon after the threshold is disabled (threshold 0 means
            // IsLockedOut answers false, and the success erases the entry).
            StringAssert.Contains("+OK", PopLogon("lockout@example.test", "test"));
         }
      }

      [Test]
      [Description("The lock binds SCRAM too: once locked, a full SCRAM exchange with the CORRECT password is refused")]
      public void TheLockIsEnforcedOnTheScramPath()
      {
         try
         {
            SetIniSetting("AccountLockoutThreshold", "3");
            _application.Reinitialize();

            for (int i = 0; i < 3; i++)
               StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "scramwrong" + i));

            var socket = new TcpConnection();
            ClassicAssert.IsTrue(socket.Connect(110));
            socket.ReadUntil("+OK");

            // The correct password, through the mechanism that used to bypass the
            // lock entirely: SCRAM verifies the proof itself and never went through
            // AccountLogon, so before this was fixed an attacker simply chose SCRAM
            // and guessed freely - while their guesses still locked the victim out
            // of every password client. This assertion is that regression's control.
            string result = POP3.Pop3SaslTestClient.AuthenticateScram(
               socket, "lockout@example.test", "test");

            StringAssert.StartsWith("-ERR", result,
               "A locked name must be refused on the SCRAM path as well. Got: " + result);

            socket.Disconnect();
         }
         finally
         {
            SetIniSetting("AccountLockoutThreshold", null);
            _application.Reinitialize();
            StringAssert.Contains("+OK", PopLogon("lockout@example.test", "test"));
         }
      }

      [Test]
      [Description("A successful SCRAM logon clears the counters, so earlier typos cannot combine with later ones to lock the user")]
      public void ASuccessfulScramLogonClearsTheCounters()
      {
         try
         {
            SetIniSetting("AccountLockoutThreshold", "3");
            _application.Reinitialize();

            // Two typos, then a successful SCRAM logon.
            StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "typo1"));
            StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "typo2"));

            var socket = new TcpConnection();
            ClassicAssert.IsTrue(socket.Connect(110));
            socket.ReadUntil("+OK");
            string ok = POP3.Pop3SaslTestClient.AuthenticateScram(
               socket, "lockout@example.test", "test");
            StringAssert.StartsWith("+OK", ok, "The SCRAM logon should succeed. Got: " + ok);
            socket.Send("QUIT\r\n");
            socket.Disconnect();

            // Two more typos. Without the clearing on the SCRAM success path these
            // four failures span one window and the third one locks the account -
            // punishing a user who has demonstrably just proved their password.
            StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "typo3"));
            StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "typo4"));

            StringAssert.Contains("+OK", PopLogon("lockout@example.test", "test"),
               "The account must not be locked: the successful SCRAM logon cleared the earlier failures.");
         }
         finally
         {
            SetIniSetting("AccountLockoutThreshold", null);
            _application.Reinitialize();
            StringAssert.Contains("+OK", PopLogon("lockout@example.test", "test"));
         }
      }

      [Test]
      [Description("One mailbox is one bucket however it is spelled: failures against a domain alias lock the real address")]
      public void SpellingTheNameDifferentlyDoesNotEvadeTheLock()
      {
         DomainAlias alias = null;

         try
         {
            alias = _domain.DomainAliases.Add();
            alias.AliasName = "lockalias.test";
            alias.DomainID = _domain.ID;
            alias.Save();

            SetIniSetting("AccountLockoutThreshold", "3");
            _application.Reinitialize();

            // Guess three times at the alias spelling of the same mailbox.
            for (int i = 0; i < 3; i++)
               StringAssert.Contains("-ERR", PopLogon("lockout@lockalias.test", "aliaswrong" + i));

            // The real address must be locked. Keying on the string as it arrived
            // gave every spelling its own untouched threshold, so an attacker could
            // alternate spellings and never trip the lock at all.
            var locked = PopLogon("lockout@example.test", "test");
            StringAssert.Contains("-ERR", locked,
               "Failures against an alias of the same mailbox must count against one name. Got: " + locked);
         }
         finally
         {
            SetIniSetting("AccountLockoutThreshold", null);
            _application.Reinitialize();

            if (alias != null)
               alias.Delete();

            StringAssert.Contains("+OK", PopLogon("lockout@example.test", "test"));
         }
      }

      private bool AutoBanRangeExists()
      {
         var ranges = _settings.SecurityRanges;
         for (int i = 0; i < ranges.Count; i++)
         {
            if (ranges[i].Name.StartsWith("Auto-ban:", StringComparison.OrdinalIgnoreCase))
               return true;
         }

         return false;
      }

      private void DeleteAutoBanRanges()
      {
         var ranges = _settings.SecurityRanges;
         for (int i = ranges.Count - 1; i >= 0; i--)
         {
            if (ranges[i].Name.StartsWith("Auto-ban:", StringComparison.OrdinalIgnoreCase))
               ranges[i].Delete();
         }
      }

      [Test]
      [Description("A locked name's refusals are not charged to the source address, on either route to the lock - the owner retrying the correct password must not auto-ban their own address")]
      public void ALockedNameDoesNotGetTheClientAddressAutoBanned()
      {
         bool originalAutoBan = _settings.AutoBanOnLogonFailure;
         int originalMaxAttempts = _settings.MaxInvalidLogonAttempts;

         try
         {
            // Auto-ban armed, but with its threshold ABOVE the lockout threshold so
            // the three failures that cause the lock cannot trip it by themselves.
            // What is measured is whether the refusals AFTER the lock accumulate.
            _settings.AutoBanOnLogonFailure = true;
            _settings.MaxInvalidLogonAttempts = 8;
            _settings.ClearLogonFailureList();

            SetIniSetting("AccountLockoutThreshold", "3");
            _application.Reinitialize();

            for (int i = 0; i < 3; i++)
               StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "banwrong" + i));

            // Twelve retries with the CORRECT password - what a mail client does
            // while its user's mailbox is locked by somebody else's attack. Charged
            // to the address, the eighth would create a priority-100
            // deny-everything range over 127.0.0.1 and refuse every protocol for
            // everyone behind it.
            for (int i = 0; i < 12; i++)
               PopLogon("lockout@example.test", "test");

            ClassicAssert.IsFalse(AutoBanRangeExists(),
               "Refusals caused by the lock must not be charged to the client's address on the password path.");

            // And again over SCRAM, which reaches the lock by a different route -
            // the account lookup returning an empty handle - and lands in a
            // different refusal helper. Expressing 'locked' as 'cannot serve SCRAM'
            // silently reintroduced the per-IP charge here after it had been
            // removed from the password path.
            for (int i = 0; i < 12; i++)
            {
               var socket = new TcpConnection();
               ClassicAssert.IsTrue(socket.Connect(110));
               socket.ReadUntil("+OK");
               POP3.Pop3SaslTestClient.AuthenticateScram(socket, "lockout@example.test", "test");
               socket.Disconnect();
            }

            ClassicAssert.IsFalse(AutoBanRangeExists(),
               "Nor on the SCRAM path: the same refusal, reached another way, must cost the client's " +
               "address nothing. An attack on one mailbox must not become an outage for unrelated " +
               "users at the victim's address.");
         }
         finally
         {
            // First, whatever happened: never leave a ban on loopback for the rest
            // of the run. DCOM is not subject to IP ranges, so this always works.
            DeleteAutoBanRanges();

            _settings.AutoBanOnLogonFailure = originalAutoBan;
            _settings.MaxInvalidLogonAttempts = originalMaxAttempts;
            _settings.ClearLogonFailureList();

            SetIniSetting("AccountLockoutThreshold", null);
            _application.Reinitialize();

            StringAssert.Contains("+OK", PopLogon("lockout@example.test", "test"));
         }
      }

      [Test]
      [Description("Negative control: with no configuration there is no lockout, however many failures accumulate")]
      public void DefaultConfigurationImposesNoLockout()
      {
         for (int i = 0; i < 6; i++)
            StringAssert.Contains("-ERR", PopLogon("lockout@example.test", "stillwrong" + i));

         StringAssert.Contains("+OK", PopLogon("lockout@example.test", "test"),
            "With AccountLockoutThreshold unset the mechanism must be inert.");
      }

      private static void SetIniSetting(string key, string value)
      {
         var path = IniPath();
         var lines = new List<string>(File.ReadAllLines(path));
         lines.RemoveAll(line => line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
         var section = lines.FindIndex(line => line.Trim() == "[Settings]");
         Assert.Greater(section, -1, "hMailServer.ini has no [Settings] section: " + path);
         if (value != null)
            lines.Insert(section + 1, key + "=" + value);
         File.WriteAllLines(path, lines);
      }

      private static string IniPath()
      {
         var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
         while (directory != null)
         {
            var candidate = Path.Combine(directory.FullName,
               @"source\Server\hMailServer\x64\Release\hMailServer.ini");
            if (File.Exists(candidate))
               return candidate;
            directory = directory.Parent;
         }
         Assert.Fail("Could not locate the server's hMailServer.ini by searching upwards from " +
                     AppDomain.CurrentDomain.BaseDirectory);
         return null;
      }
   }
}
