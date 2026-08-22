// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Exercises Settings.PreviewDirectorySync and Settings.ApplyDirectorySync on the
   ///    paths that must answer WITHOUT a directory to read.
   ///
   ///    Those paths are the ones that matter most, because they are the ones an
   ///    administrator hits by accident. Provisioning is the most destructive-looking
   ///    button in the product - it creates mailboxes in bulk and can mark them inactive
   ///    - so the question these tests ask is not "does it work", which the lab
   ///    directory answers, but "when it cannot work, does it refuse cleanly and change
   ///    nothing".
   ///
   ///    Two properties are asserted throughout, and they are the reason the fixture
   ///    exists:
   ///
   ///    FIRST, every refusal comes back as S_OK with Result=false and a report that
   ///    names the setting to change. Not a COM exception: a caller that gets one has
   ///    nothing to show the administrator except a generic error, and the whole point
   ///    of a preview is to replace guessing with a sentence.
   ///
   ///    SECOND, a run that refused created nothing. That is asserted against a domain
   ///    which IS linked to a directory - so it is a live provisioning target and would
   ///    be populated by a run that got as far as acting - rather than against an
   ///    unlinked one, where an empty account list would prove only that the domain was
   ///    never eligible. A failure that half-ran is the defect worth catching here.
   ///
   ///    The paths that need a directory to answer - what gets created, what gets
   ///    updated, the disable guards, the skip-reason breakdown - are verified against a
   ///    real domain controller and are not reachable from here.
   /// </summary>
   [TestFixture]
   public class DirectorySyncPreflight : TestFixtureBase
   {
      // Ports 9390-9399 are this fixture's, after the 9380-9389 block LdapDirectoryPreview
      // owns and the 9370-9379 block LdapDirectoryAuthentication owns, so no two of the
      // three can collide on a listener.
      //
      // Nothing is ever started on this one, deliberately: it is a port where a
      // connection is refused at once, which is what lets a test tell "the run reached
      // the network" from "the run refused before trying" without waiting out a timeout.
      private const int UnusedDirectoryPort = 9390;

      private const string SearchBase = "DC=example,DC=test";

      // The directory domain the test domain is linked to for the duration of a test.
      // Its name never has to resolve or exist: no test here gets far enough to read a
      // directory, and what the link is for is making the domain a provisioning TARGET
      // so that "nothing was created" is a meaningful assertion.
      private const string LinkedDirectoryDomain = "directory.example.test";

      // The LDAP authentication path's error codes. Neither a preview nor an apply that
      // refused may produce one: these are operations an administrator runs on purpose,
      // and a button that writes an authentication error to the log every time it is
      // pressed teaches people to ignore that log.
      private static readonly string[] LdapErrorCodes = { "HM5920", "HM5921", "HM5922", "HM5923", "HM5924" };

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      #region ini plumbing

      /// <summary>
      ///    Writes one [LDAP] value to every hMailServer.ini that exists.
      ///
      ///    Shared.IniFileSetting cannot be used: it writes to [Settings], and the LDAP
      ///    configuration lives in its own section. Its CandidateDirectories() is reused
      ///    so that every class which writes an ini agrees on which files are live.
      /// </summary>
      private static void WriteLdapSetting(string key, string value)
      {
         bool wroteAny = false;

         foreach (string directory in IniFileSetting.CandidateDirectories())
         {
            string iniPath = Path.Combine(directory, "hMailServer.ini");

            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(WritePrivateProfileString("LDAP", key, value, iniPath),
               "Failed to write [LDAP] " + key + " to " + iniPath + ".");

            // The value has to be on disk before the server looks at the file's
            // timestamp; WritePrivateProfileString caches otherwise.
            WritePrivateProfileString(null, null, null, iniPath);

            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      /// <summary>
      ///    Applies a whole [LDAP] configuration and waits for the server to notice.
      ///
      ///    The wait is not padding. LdapSettings does not use IniFileSettings' cache,
      ///    which only Reinitialize refreshes; it keys its own cache on the ini file's
      ///    last-write time and re-examines that at most every two seconds. So a change
      ///    is picked up without a reinitialize, but not instantly.
      ///
      ///    Getting this wrong would not fail loudly: the run would use the PREVIOUS
      ///    test's configuration and report something perfectly sensible about it, so an
      ///    assertion would fail with a message pointing at the wrong test.
      /// </summary>
      private static void ConfigureLdap(Dictionary<string, string> values)
      {
         foreach (KeyValuePair<string, string> value in values)
            WriteLdapSetting(value.Key, value.Value);

         Thread.Sleep(2600);
      }

      /// <summary>
      ///    A complete, switched-on configuration pointed at a port where nothing listens.
      ///
      ///    Security=0 (cleartext) rather than the shipped Security=2, and that choice is
      ///    load-bearing: against a dead port with LDAPS the failure can arrive from
      ///    inside the TLS handshake and report as a certificate problem, which would
      ///    make an assertion about a CONNECTION failure read true for the wrong reason.
      ///
      ///    Every key is written explicitly, including the empty ones. Omitting a key
      ///    does not mean "unset": ConfigureLdap writes only what it is given, so
      ///    anything not written here is whatever the previous fixture left behind - and
      ///    the sibling authentication fixture leaves a ServiceUsername in place. A
      ///    fixture must not depend on a key it does not write.
      /// </summary>
      private static Dictionary<string, string> ReachableConfiguration()
      {
         return new Dictionary<string, string>
         {
            { "Enabled", "1" },
            { "Server", "127.0.0.1" },
            { "Port", UnusedDirectoryPort.ToString() },
            { "Security", "0" },
            { "AllowUnprotectedPassword", "0" },
            { "BindMethod", "0" },
            { "SearchBase", SearchBase },
            { "UserDnTemplate", "" },
            { "ServiceUsername", "" },
            { "ServicePassword", "" },
            { "TimeoutSeconds", "3" },
         };
      }

      /// <summary>
      ///    Puts the [LDAP] section back to its shipped defaults, switched off.
      ///
      ///    ALWAYS from a finally. Leaving Enabled=1 pointed at a dead port does not
      ///    break this fixture - it breaks every Active-Directory-linked account for the
      ///    rest of the run, reported as an authentication failure in somebody else's
      ///    test.
      /// </summary>
      private static void RestoreLdapSectionToOff()
      {
         WriteLdapSetting("Enabled", "0");
         WriteLdapSetting("Server", "");
         WriteLdapSetting("Port", "0");
         WriteLdapSetting("Security", "2");
         WriteLdapSetting("AllowUnprotectedPassword", "0");
         WriteLdapSetting("BindMethod", "0");
         WriteLdapSetting("SearchBase", "");
         WriteLdapSetting("UserDnTemplate", "");
         WriteLdapSetting("ServiceUsername", "");
         WriteLdapSetting("ServicePassword", "");
         WriteLdapSetting("TimeoutSeconds", "10");
      }

      #endregion

      /// <summary>
      ///    Links the per-test domain to a directory, making it a provisioning target.
      ///
      ///    Without this every "nothing was created" assertion below would pass on a
      ///    server whose provisioning worked perfectly, because an unlinked domain is
      ///    skipped by design and would have received nothing either way.
      /// </summary>
      private void LinkTestDomainToDirectory()
      {
         _domain.ADDomainName = LinkedDirectoryDomain;
         _domain.Save();
      }

      /// <summary>
      ///    Asserts that a refusal left the account list exactly as it was, and that it
      ///    did not log an authentication error on the way out.
      /// </summary>
      private void AssertRefusedCleanly(bool succeeded, string report, int accountsBefore, string context)
      {
         Assert.IsFalse(succeeded,
            context + ": a run that read no directory must not report success. It said: " + report);

         Assert.IsNotEmpty(report,
            context + ": a refusal with an empty report leaves the administrator with nothing to act on.");

         Assert.AreEqual(accountsBefore, _domain.Accounts.Count,
            context + ": the run refused but changed the account list, which means it acted before it checked. " +
            "It said: " + report);

         string log = LogHandler.ReadCurrentDefaultLog();

         foreach (string code in LdapErrorCodes)
            StringAssert.DoesNotContain(code, log,
               context + ": a refusal wrote the authentication error " + code + " to the log. Provisioning is run " +
               "on purpose and must not fill the error log with failures the administrator asked to see.");
      }

      /// <summary>
      ///    Asserts that a PREVIEW wrote nothing to the error log, and that is a
      ///    guarantee rather than tidiness.
      ///
      ///    A preview changes nothing and returns its reason to the caller, who is by
      ///    definition reading it. An administrator hunting a search base will press that
      ///    button five or six times in a minute, and a log that gains an ERROR every
      ///    time it is pressed is a log people stop reading - which is how the one entry
      ///    that mattered gets missed. An APPLY is different in kind and does log; that
      ///    asymmetry is asserted from both sides, here and in
      ///    AssertErrorLoggedAndClear.
      /// </summary>
      private static void AssertPreviewLoggedNoError(string context, string report)
      {
         // File.Exists, not LogHandler.ReadErrorLog(). The absence of the file is the
         // expected outcome here, and ReadErrorLog asserts the file EXISTS - retrying for
         // five seconds first - so using it would turn every pass into a slow failure.
         // This is the same test CustomAsserts.AssertNoReportedError makes.
         string path = LogHandler.GetErrorLogFileName();

         if (!File.Exists(path))
            return;

         string errorLog = LogHandler.ReadErrorLog();

         Assert.IsTrue(string.IsNullOrEmpty(errorLog),
            context + ": a preview wrote to the ERROR log. A read-only dry run whose reason is handed straight " +
            "back to the caller has nothing to report there. It said: " + report + "\r\nError log: " + errorLog);
      }

      /// <summary>
      ///    Asserts that an APPLY recorded its refusal, then clears the entry.
      ///
      ///    The clear is not housekeeping: TestFixtureBase's SetUp fails any test that
      ///    starts with a non-empty error log, so an expected entry left behind would
      ///    fail whichever test happened to run next and point the blame at it.
      /// </summary>
      private static void AssertErrorLoggedAndClear(string code, string context)
      {
         string errorLog = LogHandler.ReadAndDeleteErrorLog();

         StringAssert.Contains(code, errorLog,
            context + ": an apply that could not be carried out left no record. A scheduled run has nobody " +
            "watching it, so the error log is the only place its failure can appear. Error log: " + errorLog);
      }

      /// <summary>
      ///    The first thing anyone does with a new provisioning button is press it before
      ///    switching LDAP on. Both calls have to answer "[LDAP] Enabled=0" and nothing
      ///    else, because every other explanation sends the administrator to look at a
      ///    firewall, a search base or a service account for a problem that does not
      ///    exist.
      ///
      ///    The configuration is otherwise complete and points at a dead port, so the
      ///    assertions cut both ways: an implementation that read the flag from the wrong
      ///    section, defaulted it the wrong way, or checked it only after connecting
      ///    would report a connection failure here instead. Its negative control is
      ///    TestAnUnreachableDirectoryIsReportedAsAConnectionFailure below, which uses
      ///    the same dead port and DOES see that message - the pair, not either alone, is
      ///    what shows the flag was obeyed.
      /// </summary>
      [Test]
      [Description("With [LDAP] Enabled=0 both preview and apply name the setting that is off, report no network " +
                   "activity, and create no accounts.")]
      public void TestBothCallsRefuseWhenDirectoryAccessIsOff()
      {
         try
         {
            LinkTestDomainToDirectory();

            Dictionary<string, string> configuration = ReachableConfiguration();
            configuration["Enabled"] = "0";
            ConfigureLdap(configuration);

            int before = _domain.Accounts.Count;

            string previewText;
            bool previewSucceeded = _settings.PreviewDirectorySync("", false, out previewText);

            AssertRefusedCleanly(previewSucceeded, previewText, before, "Preview");

            StringAssert.Contains("[LDAP] Enabled", previewText,
               "The report must name the setting that is switched off. It said: " + previewText);
            StringAssert.DoesNotContain("Could not connect", previewText,
               "The preview reached the network even though [LDAP] Enabled=0. It said: " + previewText);

            AssertPreviewLoggedNoError("Preview with LDAP off", previewText);

            // The same assertion against APPLY, and it is not redundant. Apply is the
            // call that writes, so a guard present in one and missing in the other is
            // exactly the asymmetry that provisions accounts from a directory the
            // administrator switched off.
            string applyText;
            bool applySucceeded = _settings.ApplyDirectorySync("", false, out applyText);

            AssertRefusedCleanly(applySucceeded, applyText, before, "Apply");

            StringAssert.Contains("[LDAP] Enabled", applyText,
               "The apply report must name the setting that is switched off. It said: " + applyText);
            StringAssert.DoesNotContain("Could not connect", applyText,
               "The apply reached the network even though [LDAP] Enabled=0. It said: " + applyText);

            AssertErrorLoggedAndClear("HM6180", "Apply with LDAP off");
         }
         finally
         {
            LogHandler.DeleteErrorLog();
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    Provisioning enumerates a subtree, and a subtree search needs somewhere to
      ///    start. An empty SearchBase is not "search everything" - it is a request the
      ///    directory answers with whatever it feels like, which for Active Directory is
      ///    the RootDSE and for other servers can be the entire DIT. Refusing is the only
      ///    safe reading, and the report has to name SearchBase or the administrator has
      ///    no way to know that is what is missing.
      /// </summary>
      [Test]
      [Description("With no SearchBase configured the run names SearchBase rather than attempting a search from " +
                   "an empty base.")]
      public void TestAMissingSearchBaseIsNamedRatherThanSearchedFrom()
      {
         try
         {
            LinkTestDomainToDirectory();

            Dictionary<string, string> configuration = ReachableConfiguration();
            configuration["SearchBase"] = "";
            ConfigureLdap(configuration);

            int before = _domain.Accounts.Count;

            string resultText;
            bool succeeded = _settings.PreviewDirectorySync("", false, out resultText);

            AssertRefusedCleanly(succeeded, resultText, before, "Preview without a search base");

            StringAssert.Contains("SearchBase", resultText,
               "The report must name the setting that is missing. It said: " + resultText);
            StringAssert.DoesNotContain("Could not connect", resultText,
               "The run opened a connection before noticing it had nowhere to search. It said: " + resultText);

            AssertPreviewLoggedNoError("Preview without a search base", resultText);
         }
         finally
         {
            LogHandler.DeleteErrorLog();
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    The negative control for the two tests above, and a test in its own right.
      ///
      ///    A complete configuration pointed at a dead port must produce a CONNECTION
      ///    failure - naming the address, so the administrator can see which host was
      ///    tried - and must not blame the service credential. Blaming the credential for
      ///    a connection that never opened is the specific failure that sends people to
      ///    reset a service account password for a firewall problem.
      /// </summary>
      [Test]
      [Description("An unreachable directory is reported as a connection failure naming the address tried, not as " +
                   "a credential problem, and nothing is created.")]
      public void TestAnUnreachableDirectoryIsReportedAsAConnectionFailure()
      {
         try
         {
            LinkTestDomainToDirectory();

            ConfigureLdap(ReachableConfiguration());

            int before = _domain.Accounts.Count;

            string resultText;
            bool succeeded = _settings.ApplyDirectorySync("", false, out resultText);

            AssertRefusedCleanly(succeeded, resultText, before, "Apply against a dead port");

            StringAssert.Contains("Could not connect", resultText,
               "The report must say the directory could not be reached. It said: " + resultText);
            StringAssert.Contains(UnusedDirectoryPort.ToString(), resultText,
               "The report must name the address that was tried, or the administrator cannot tell which host " +
               "is unreachable. It said: " + resultText);

            StringAssert.DoesNotContain("password", resultText,
               "The report blamed the credential for a connection that was never opened. It said: " + resultText);

            // The other half of the preview/apply asymmetry. This one WROTE, or tried
            // to, so its failure belongs in the record - the identical run through
            // PreviewDirectorySync must leave the error log empty, which the tests above
            // assert.
            AssertErrorLoggedAndClear("HM6180", "Apply against a dead port");
         }
         finally
         {
            LogHandler.DeleteErrorLog();
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    A run restricted to a domain that does not exist on this server is a typo,
      ///    and it has to be reported as one BEFORE the directory is contacted.
      ///
      ///    That ordering is the assertion. If the run connected first and only then
      ///    discovered the domain was unknown, the administrator with a mistyped domain
      ///    name would be shown a connection or credential error and would go and debug
      ///    the directory. DoesNotContain("Could not connect") is what pins the order,
      ///    and it is only meaningful because the configuration used here points at a
      ///    dead port and WOULD produce that message if the connection were attempted -
      ///    which the test above proves it does.
      /// </summary>
      [Test]
      [Description("Restricting a run to a domain that does not exist on this server is reported as such before " +
                   "the directory is contacted.")]
      public void TestAnUnknownDomainIsReportedBeforeTheDirectoryIsContacted()
      {
         try
         {
            LinkTestDomainToDirectory();

            ConfigureLdap(ReachableConfiguration());

            int before = _domain.Accounts.Count;

            const string missingDomain = "no-such-domain-on-this-server.test";

            string resultText;
            bool succeeded = _settings.PreviewDirectorySync(missingDomain, false, out resultText);

            AssertRefusedCleanly(succeeded, resultText, before, "Preview of an unknown domain");

            StringAssert.Contains(missingDomain, resultText,
               "The report must quote the domain name that was asked for, so a typo is visible. It said: " +
               resultText);

            StringAssert.DoesNotContain("Could not connect", resultText,
               "The run contacted the directory before checking that the requested domain exists, so a mistyped " +
               "domain name is reported as a directory problem. It said: " + resultText);

            AssertPreviewLoggedNoError("Preview of an unknown domain", resultText);
         }
         finally
         {
            LogHandler.DeleteErrorLog();
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    Preview must never write, and this asserts it on the one path where preview
      ///    gets furthest without a directory.
      ///
      ///    It is deliberately paired with a DisableMissing of true. That is the argument
      ///    which makes apply capable of taking a working mailbox away, and a preview
      ///    that honoured it as an instruction rather than as a question would disable
      ///    accounts from a button labelled "preview". The account created here is
      ///    directory-linked and absent from any directory the run could read, which is
      ///    precisely the shape that gets disabled - so if preview ever acted, this is
      ///    the test that would catch it.
      /// </summary>
      [Test]
      [Description("A preview run with DisableMissing set changes nothing at all, including the Active flag of a " +
                   "directory-linked account that no directory backs.")]
      public void TestPreviewWithDisableMissingLeavesAccountsAlone()
      {
         try
         {
            LinkTestDomainToDirectory();

            Account account = SingletonProvider<TestSetup>.Instance.AddAccount(
               _domain, "sync-preview@" + _domain.Name, "test");

            account.IsAD = true;
            account.ADDomain = LinkedDirectoryDomain;
            account.ADUsername = "sync-preview";
            account.Active = true;
            account.Save();

            ConfigureLdap(ReachableConfiguration());

            int before = _domain.Accounts.Count;

            string resultText;
            bool succeeded = _settings.PreviewDirectorySync("", true, out resultText);

            AssertRefusedCleanly(succeeded, resultText, before, "Preview with DisableMissing");

            // Re-read rather than trusting the proxy: _application and _settings are
            // proxies to an out-of-process COM server, and the local object would happily
            // report the value it was given rather than the value that is stored.
            Account reread = _domain.Accounts.ItemByAddress["sync-preview@" + _domain.Name];

            Assert.IsTrue(reread.Active,
               "A preview disabled an account. It said: " + resultText);
            Assert.IsTrue(reread.IsAD,
               "A preview changed an account's directory linkage. It said: " + resultText);

            AssertPreviewLoggedNoError("Preview with DisableMissing", resultText);
         }
         finally
         {
            LogHandler.DeleteErrorLog();
            RestoreLdapSectionToOff();
         }
      }
   }
}
