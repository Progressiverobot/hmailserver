// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Active Directory account authentication - the path where an account carries no
   ///    local password hash at all and every logon is replayed to Windows through
   ///    LogonUser.
   ///
   ///    This fixture exists because that path had no test of any kind and one line of it
   ///    was undefined behaviour on every failed login:
   ///
   ///        HANDLE token;
   ///        BOOL result = LogonUser(..., &amp;token);
   ///        CloseHandle(token);
   ///
   ///    LogonUser does not write the out parameter when it fails, so CloseHandle received
   ///    whatever was on the stack. Anyone able to attempt a logon against an AD-linked
   ///    account - so anyone on the internet, over SMTP, POP3 or IMAP - could reach it
   ///    just by getting a password wrong. If the stack value happened to match a live
   ///    handle, an unrelated socket, log file or database connection was closed under
   ///    another thread and the resulting crash surfaced somewhere with no connection to
   ///    the cause; with handle verification on, it raises STATUS_INVALID_HANDLE and ends
   ///    the process.
   ///
   ///    Neither test needs a domain controller, which is the point of choosing these two
   ///    cases: both provocations are refused by the local machine, so they run anywhere,
   ///    including on a build agent that has never heard of a domain.
   ///
   ///    They also pin something the code could not have got right by reasoning, only by
   ///    measurement. LogonUser returns the identical ERROR_LOGON_FAILURE (1326) for a
   ///    wrong password, for a domain that does not exist, and for a real domain
   ///    controller answering LDAP on the same subnet - Windows will not tell an unjoined
   ///    computer whether a domain exists. So one Windows error has to produce two
   ///    different behaviours, and which one depends on a question answered locally rather
   ///    than on the error code: is this computer in a domain at all, and is the account
   ///    pointed at this computer or elsewhere.
   ///
   ///    What is deliberately NOT covered here is a SUCCESSFUL Active Directory logon.
   ///    That needs a reachable domain controller and a known-good account, which the
   ///    suite cannot assume, and faking it would mean testing a stub instead of Windows.
   ///    The success branch is one CloseHandle on a handle LogonUser is documented to
   ///    have written; the branches that were actually wrong are the ones below.
   /// </summary>
   [TestFixture]
   public class ActiveDirectoryAuthentication : TestFixtureBase
   {
      // A domain name that cannot resolve, in a namespace that cannot exist: .invalid is
      // reserved by RFC 2606 exactly so a test cannot collide with a real name.
      //
      // Unique per run, which is not decoration. The server reports a misconfigured domain
      // once per minute per domain, so a fixed name would make this test depend on when it
      // last ran: run it twice inside a minute and the second run sees the report
      // suppressed and fails, which is precisely how it failed while this was a constant.
      // A fresh name each run means the assertion is about the server's behaviour rather
      // than about the clock.
      private static readonly string UnreachableDomain =
         "no-such-domain-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".invalid";

      private Account CreateAdAccount(string address, string adDomain, string adUsername)
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant");

         account.IsAD = true;
         account.ADDomain = adDomain;
         account.ADUsername = adUsername;
         account.Save();

         return account;
      }

      private static bool TryLogon(string address, string password)
      {
         var client = new Pop3ClientSimulator();

         bool loggedOn = client.ConnectAndLogon(address, password, out string _);

         if (loggedOn)
            client.Disconnect();

         return loggedOn;
      }

      [Test]
      [Description("Repeated wrong passwords on an AD account are refused quietly, without a handle fault")]
      public void WrongPasswordsOnAnAdAccountAreRefusedQuietlyAndSafely()
      {
         // This is the regression test for the uninitialised handle, and it is a burst
         // rather than a single attempt on purpose. The old code's fault was
         // probabilistic - it depended on the stack holding a value that happened to be
         // a valid handle - so one attempt could pass by luck. A burst that leaves the
         // crash oracle silent and the error log empty is the strongest statement the
         // suite can make from outside the process.
         var account = CreateAdAccount("adwrongpassword@example.test", Environment.MachineName, "hm-no-such-local-user");

         CrashOracleAsserts.Clear();

         // Auto-ban off for the duration, and this is not the test avoiding an
         // inconvenience. A burst of deliberately wrong passwords from one address is
         // exactly what auto-ban exists to stop, so with it on, the later attempts are
         // refused at the connection and never reach the code under test - which is how
         // the first version of the fixture below silently tested nothing at all. Auto-ban
         // has its own fixture; this one needs the attempts to arrive.
         bool autoBan = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         try
         {
            for (int attempt = 0; attempt < 8; attempt++)
            {
               Assert.IsFalse(TryLogon(account.Address, "not the password " + attempt),
                  "An Active Directory account accepted a password Windows had rejected.");
            }

            // The credential branch: a wrong password is an ordinary event on any
            // internet-facing server, so it must not write to the error log. If it did,
            // a password-guessing run would fill the disk and bury the entries that
            // matter - and every fixture that ran afterwards would fail its setup, since
            // PerformBasicSetup fails if the error log exists at all.
            CustomAsserts.AssertNoReportedError();

            // And the fault itself. A first-chance access violation is recorded here even
            // when a catch (...) swallows it, which is the only way this bug would have
            // been visible from a test at all: the server is built /EHa, so an access
            // violation inside a try block is catchable and was being caught.
            CrashOracleAsserts.AssertNoMemorySafetyEvents();
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = autoBan;
            _settings.ClearLogonFailureList();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("A domain this computer cannot reach explains itself once, rather than failing as a bad password")]
      public void AnUnreachableDomainOnAnUnjoinedComputerIsExplainedOnceRatherThanPerAttempt()
      {
         // This test is here because the obvious version of it was wrong, and the
         // measurement that corrected it is worth keeping.
         //
         // The first attempt asserted that an unreachable domain produces a different
         // Windows error than a wrong password. It does not. LogonUser was called
         // directly on this machine with four domain values - a syntactically impossible
         // name, a plausible name that does not exist, the real name of a domain
         // controller answering LDAP on the same subnet, and this computer's own name
         // with a nonexistent user - and every one returned ERROR_LOGON_FAILURE (1326).
         // Windows will not tell a computer that is not domain-joined whether a domain
         // exists, because that would be an information leak.
         //
         // The consequence for a mail server is worse than an unhelpful error code. On a
         // workgroup computer an Active Directory account can NEVER authenticate, and
         // every attempt is indistinguishable from a mistyped password - so the user is
         // told to check their password forever and the administrator has nothing to go
         // on. The fix is to answer the question locally, with NetGetJoinInformation,
         // and say the thing Windows will not.
         //
         // The pair of tests in this fixture therefore pins a distinction, not just a
         // message: the SAME Windows error must stay silent when the account targets this
         // computer's own local accounts (legitimate in a workgroup, and covered by the
         // test above) and must explain itself when it targets a domain this computer has
         // no way to reach. On a domain-joined server this test's provocation does not
         // arise, and the fixture says so rather than pretending to cover it.
         if (IsThisComputerDomainJoined())
         {
            Assert.Ignore("This computer is domain-joined, so the unjoined-computer diagnostic cannot be provoked. " +
                          "It fires only where it is needed, which is the point of it.");
         }

         var account = CreateAdAccount("adnodomain@example.test", UnreachableDomain, "someone");

         // Same reason as the test above, plus one specific to running them together: the
         // eight rejected logons there are enough to have this address banned already, and
         // a banned connection never authenticates - so this test passed its
         // "logon must fail" assertion while never once reaching the code it exists to
         // check. That is exactly the failure mode this project keeps finding in its own
         // work: a test that cannot fail, passing.
         bool autoBan = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         LogHandler.DeleteErrorLog();

         try
         {
            const int attempts = 4;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
               Assert.IsFalse(TryLogon(account.Address, "any password at all"),
                  "A logon against a domain this computer cannot reach was accepted.");
            }

            // Waited for, then read once, and in that order for a reason worth writing
            // down: the report is written from the thread that failed the logon, so it can
            // arrive a moment after the POP3 session has already been told no.
            // CustomAsserts.AssertReportedError does that waiting, but it also DELETES the
            // log in its finally - so calling it and then reading the log fails on a file
            // the assertion just consumed, which is exactly how this test failed twice
            // while the server behaviour under it was correct the whole time.
            string log = null;

            RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            {
               log = LogHandler.ReadErrorLog();

               RetryableAssert.StringContains("this computer is not joined to any domain", log);
            });

            // Both names belong in the message: the domain, because an administrator with
            // several linked domains needs to know which one, and this computer's name,
            // because it is half of the two remedies the message offers.
            StringAssert.Contains(UnreachableDomain, log,
               "The report does not name the domain that cannot be reached.");
            StringAssert.Contains(Environment.MachineName, log,
               "The report does not name this computer, so neither of the two remedies it suggests is actionable.");

            // The throttle. Four attempts, one line: without it this is one error per
            // logon attempt from every client on the system, for as long as the
            // misconfiguration lasts, which is how a log fills a disk and buries the
            // entry that explains it.
            int reports = CountOccurrences(log, "HM5911");

            Assert.AreEqual(1, reports,
               "Expected exactly one throttled report for " + attempts + " attempts, found " + reports +
               ". A misconfigured domain must not write one error per logon attempt.");
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = autoBan;
            _settings.ClearLogonFailureList();

            // Settled rather than deleted once: the reported error arrives on a different
            // thread than the one the POP3 session ran on, so a plain delete can race with
            // a straggler and leave the file for the next fixture to fail on.
            LogHandler.ClearErrorLogUntilSettled();
         }
      }

      // NetGetJoinInformation, asked the same way the server asks it, so the test agrees
      // with the code under test rather than guessing from an environment variable.
      private const int NetSetupDomainName = 3;

      [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
      private static extern int NetGetJoinInformation(string server, out IntPtr name, out int status);

      [DllImport("netapi32.dll")]
      private static extern int NetApiBufferFree(IntPtr buffer);

      private static bool IsThisComputerDomainJoined()
      {
         if (NetGetJoinInformation(null, out IntPtr name, out int status) != 0)
            return false;

         if (name != IntPtr.Zero)
            NetApiBufferFree(name);

         return status == NetSetupDomainName;
      }

      private static int CountOccurrences(string text, string value)
      {
         int count = 0;

         for (int index = text.IndexOf(value, StringComparison.Ordinal);
              index >= 0;
              index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
         {
            count++;
         }

         return count;
      }
   }
}
