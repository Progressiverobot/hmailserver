// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Exercises Settings.TestLdapDirectory - the read-only preview an administrator
   ///    runs BEFORE pointing an account source at a directory, to see which people the
   ///    configured search base and filter actually select.
   ///
   ///    The value of a preview is entirely in what it says. A preview that answers
   ///    "failed" is worth nothing, because the administrator is then back to guessing
   ///    between a typo in the search base, a service credential the directory refused,
   ///    a firewall, and LDAP not being switched on at all - which is the same guessing
   ///    game the LogonUser path already forces on them. So every test here asserts on
   ///    the REPORT: that it names the setting to change, or names the class of failure,
   ///    and that it does not blame the credentials for something that never reached a
   ///    bind.
   ///
   ///    None of these tests need a directory, and that is deliberate. They cover the
   ///    paths that must answer honestly WITHOUT one: the pre-flight branches that
   ///    return before a socket is opened, and the one that opens a socket and finds
   ///    nothing there. The paths that need a directory to answer - the entry listing,
   ///    the truncation warning, the count of entries carrying no mail attribute, and
   ///    the MaxUsers ceiling - are not covered here, because the in-process LDAP server
   ///    that could answer them is private to LdapDirectoryAuthentication.
   ///
   ///    Note also what these tests establish about the SHAPE of the API: every failure
   ///    below comes back as S_OK with Result=false and a report, not as a COM
   ///    exception. That is what lets a caller show the administrator the reason. If any
   ///    of these paths started throwing, the tests would fail on the exception rather
   ///    than on an assertion, and the administration tool would be showing a generic
   ///    COM error where it used to show the fix.
   /// </summary>
   [TestFixture]
   public class LdapDirectoryPreview : TestFixtureBase
   {
      // Ports 9380-9389 are this fixture's, immediately after the 9370-9379 block that
      // LdapDirectoryAuthentication owns, so the two can never collide on a listener.
      //
      // Nothing is started on this one. That is the whole point of it: it is a port
      // where a connection attempt is refused at once, so a test can tell "the preview
      // tried to reach the directory" from "the preview returned before trying" without
      // running a directory, and without waiting out a timeout.
      private const int UnusedDirectoryPort = 9380;

      private const string SearchBase = "DC=example,DC=test";

      // Any syntactically plausible template will do. What matters is only that it is
      // NOT empty, because that is what makes LdapConfiguration::UsesSearch() false and
      // so reaches the branch the account-source test is about.
      private const string UserDnTemplate = "CN=%u,OU=hMailServer Test,DC=example,DC=test";

      // A preview ceiling, passed everywhere so that a MaxUsers of 0 - which means "use
      // the configured SyncMaxUsers" - is never what a test happens to exercise. It
      // cannot influence any path this fixture reaches, because nothing here gets as far
      // as a search, and no test asserts on it.
      private const int PreviewMaxUsers = 50;

      // The LDAP authentication path's own error codes. A preview must not produce any
      // of them: it is something an administrator runs on purpose, and a button that
      // writes to the ERROR log every time it is pressed teaches people to ignore that
      // log.
      private static readonly string[] LdapErrorCodes = { "HM5920", "HM5921", "HM5922", "HM5923", "HM5924" };

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern uint GetPrivateProfileString(string section, string key, string defaultValue,
                                                         StringBuilder returnedString, uint size, string filePath);

      #region ini plumbing

      /// <summary>
      ///    Writes one [LDAP] value to every hMailServer.ini that exists.
      ///
      ///    Shared.IniFileSetting cannot be used: it writes to [Settings], and the LDAP
      ///    configuration lives in its own section. Its CandidateDirectories() is reused,
      ///    so every class that writes an ini agrees on which files are live.
      ///
      ///    This is a copy of the identical helper in LdapDirectoryAuthentication, which
      ///    holds it privately. Duplication is the wrong answer and is called out in the
      ///    notes accompanying this file; it is here only because a fixture cannot reach
      ///    into another fixture's private members.
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
      ///    Reads one [LDAP] value back through the same API the server reads it with.
      ///    The first ini that exists wins, matching the order WriteLdapSetting uses.
      /// </summary>
      private static string ReadLdapSetting(string key)
      {
         foreach (string directory in IniFileSetting.CandidateDirectories())
         {
            string iniPath = Path.Combine(directory, "hMailServer.ini");

            if (!File.Exists(iniPath))
               continue;

            var buffer = new StringBuilder(4096);
            GetPrivateProfileString("LDAP", key, string.Empty, buffer, (uint) buffer.Capacity, iniPath);

            return buffer.ToString();
         }

         Assert.Fail("Could not locate an existing hMailServer.ini to read.");
         return string.Empty;
      }

      /// <summary>
      ///    Applies a whole [LDAP] configuration and waits for the server to notice.
      ///
      ///    There is no Reinitialize() here on purpose, and this is the one place this
      ///    fixture depends on an implementation detail. LdapSettings does not use
      ///    IniFileSettings' cache (which only Reinitialize refreshes); it keys its own
      ///    cache on the ini file's last-write time and re-examines that at most every
      ///    two seconds. So a change is picked up without a reinitialize, but not
      ///    instantly - hence the wait, which has to be longer than that recheck window.
      ///
      ///    Getting this wrong would not fail loudly: the preview would run against the
      ///    PREVIOUS test's configuration and report something perfectly sensible about
      ///    it, so an assertion would fail with a message pointing at the wrong test.
      /// </summary>
      private static void ConfigureLdap(Dictionary<string, string> values)
      {
         foreach (KeyValuePair<string, string> value in values)
            WriteLdapSetting(value.Key, value.Value);

         Thread.Sleep(2600);
      }

      /// <summary>
      ///    A configuration that is complete and switched on, pointed at a port where
      ///    nothing is listening.
      ///
      ///    Security=0 (cleartext) rather than the shipped Security=2, and that choice is
      ///    load-bearing for the connection test: against a dead port with LDAPS the
      ///    failure can arrive from inside the TLS handshake, which reports as a
      ///    certificate problem and would make an assertion about a CONNECTION failure
      ///    read true for the wrong reason. With no TLS in the picture the only thing
      ///    that can fail is the connection itself.
      ///
      ///    AllowUnprotectedPassword stays at the safe 0. The preview sends no password
      ///    on any path reached here, so it does not need relaxing - and a test that
      ///    relaxed it without needing to would leave that in the ini for the next
      ///    fixture if a restore were ever missed.
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

            // Set explicitly, and empty, rather than left alone. Omitting them does
            // NOT mean "no service credential": ConfigureLdap writes only the keys it
            // is given, so anything this fixture does not set is whatever the previous
            // fixture left in the [LDAP] section - and the sibling authentication
            // fixture leaves a ServiceUsername behind. Inheriting one turns the
            // connection test below into a test of the unprotected-password guard,
            // which refuses BEFORE any socket is opened and is a different assertion
            // entirely. A fixture must not depend on a key it does not write.
            { "ServiceUsername", "" },
            { "ServicePassword", "" },

            // Short, so that a genuinely unbounded operation stands out against the
            // bound the connection test asserts rather than hiding inside it.
            { "TimeoutSeconds", "3" },
         };
      }

      /// <summary>
      ///    Puts the [LDAP] section back to its shipped defaults, switched off.
      ///
      ///    ALWAYS run from a finally. Leaving Enabled=1 pointed at a dead port does not
      ///    break this fixture - it breaks every Active-Directory-linked account for the
      ///    remainder of the run, in a way that reports as an authentication failure in
      ///    somebody else's test.
      ///
      ///    Every key this fixture writes is written back explicitly rather than deleted,
      ///    so the restored state is the same whether or not the key existed beforehand.
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
         WriteLdapSetting("TimeoutSeconds", "10");
      }

      #endregion

      /// <summary>
      ///    The first thing an administrator does with a new preview button is press it
      ///    before switching the feature on. What comes back has to be the one-line
      ///    answer "[LDAP] Enabled is 0", because every other explanation sends them to
      ///    look at the firewall, the search base or the service account for a problem
      ///    that does not exist.
      ///
      ///    The configuration is otherwise complete and points at a port where nothing
      ///    listens, so the assertions cut both ways: an implementation that read the
      ///    Enabled flag from the wrong section, defaulted it the wrong way, or checked
      ///    it only after connecting would report a connection failure here instead, and
      ///    DoesNotContain("Could not connect") is what catches that. Its negative
      ///    control is TestPreviewReportsAnUnreachableDirectoryAsAConnectionFailure
      ///    below, which uses the same dead port and DOES see that message - so the pair
      ///    of them, and not either alone, is what shows the flag was obeyed.
      /// </summary>
      [Test]
      [Description("With [LDAP] Enabled=0 the preview reports that directory access is switched off and names " +
                   "the setting responsible, rather than reporting a connection failure against the directory " +
                   "the rest of the section still points at.")]
      public void TestPreviewReportsThatDirectoryAccessIsOff()
      {
         try
         {
            Dictionary<string, string> configuration = ReachableConfiguration();
            configuration["Enabled"] = "0";
            ConfigureLdap(configuration);

            string resultText;
            bool succeeded = _settings.TestLdapDirectory(PreviewMaxUsers, out resultText);

            Assert.IsFalse(succeeded,
               "A preview that read nothing must not report success. It said: " + resultText);

            StringAssert.Contains("[LDAP] Enabled", resultText,
               "The report must name the setting that is switched off, so the administrator knows what to " +
               "change. It said: " + resultText);
            StringAssert.Contains("Nothing was contacted", resultText,
               "The report must say that no directory was contacted, so a network problem is ruled out rather " +
               "than left open. It said: " + resultText);

            StringAssert.DoesNotContain("Could not connect", resultText,
               "The preview reached the network even though [LDAP] Enabled=0. It said: " + resultText);
         }
         finally
         {
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    This is the one report in the method that exists to explain a distinction
      ///    rather than a fault. A UserDnTemplate reaches a user's DN by construction, so
      ///    authentication needs no SearchBase at all and such a configuration is
      ///    complete and working. Reading the directory as an ACCOUNT SOURCE is a
      ///    different question with no answer: there is nowhere to enumerate from.
      ///
      ///    The defect this catches is the preview collapsing the two - either by
      ///    refusing a configuration that authenticates people perfectly well today, or
      ///    by treating the empty base as a wildcard and enumerating from the directory
      ///    root, which on a real forest is a subtree walk over every object there is.
      ///
      ///    DoesNotContain("incomplete") is the load-bearing assertion here, and it is
      ///    not pedantry. LdapConfiguration::IsComplete is consulted FIRST and produces
      ///    its own message naming SearchBase and UserDnTemplate too, so a test that
      ///    looked only for those two words would pass against either branch. What
      ///    separates them is that this one was reached because UsesSearch() correctly
      ///    came out false, and the word "incomplete" is absent from it.
      /// </summary>
      [Test]
      [Description("With LDAP enabled and a UserDnTemplate set but no SearchBase, the preview explains that " +
                   "authentication can work without a search base while an account source cannot - rather than " +
                   "reporting the configuration as incomplete, which for authentication it is not.")]
      public void TestPreviewSeparatesAuthenticationFromReadingTheDirectory()
      {
         try
         {
            Dictionary<string, string> configuration = ReachableConfiguration();
            configuration["SearchBase"] = "";
            configuration["UserDnTemplate"] = UserDnTemplate;
            ConfigureLdap(configuration);

            string resultText;
            bool succeeded = _settings.TestLdapDirectory(PreviewMaxUsers, out resultText);

            Assert.IsFalse(succeeded,
               "There is nowhere to search, so the preview must not report success. It said: " + resultText);

            StringAssert.Contains("SearchBase", resultText,
               "The report must name the setting that has to be filled in. It said: " + resultText);
            StringAssert.Contains("account source", resultText,
               "The report must say which capability is unavailable, because the other one - authenticating " +
               "people - is working. It said: " + resultText);

            StringAssert.DoesNotContain("incomplete", resultText,
               "A configuration that authenticates users through UserDnTemplate is not incomplete, and calling " +
               "it that sends the administrator to fix something that is not broken. It said: " + resultText);

            StringAssert.DoesNotContain("Could not connect", resultText,
               "Nothing can be usefully asked of the directory without a search base, so nothing should have " +
               "been asked. It said: " + resultText);
         }
         finally
         {
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    The failure an administrator actually hits first, and the one a preview has
      ///    to get right: wrong port, wrong host, or a firewall in between.
      ///
      ///    Two defects are in scope. The first is the misdiagnosis this whole LDAP path
      ///    exists to remove - reporting an unreachable directory as a rejected
      ///    credential, which sends the administrator off to reset a service account
      ///    password that was never wrong. DoesNotContain("credentials") is that
      ///    assertion, and it is meaningful precisely because the very next branch of the
      ///    implementation DOES report a refused service credential, in almost the same
      ///    sentence shape.
      ///
      ///    The second is a preview that never returns. It runs on whatever thread the
      ///    administration tool called it on, so an unbounded connect is a hung window;
      ///    the stopwatch bound is what fails instead of the whole run timing out.
      ///
      ///    The ERROR log assertion is the third thing being protected. This is a
      ///    read-only diagnostic invoked deliberately, so its failures belong in its
      ///    return value and nowhere else - if it reported HM5920 the way the
      ///    authentication path does, every press of the button would leave a permanent
      ///    error behind, and would fail the next fixture's setup into the bargain.
      /// </summary>
      [Test]
      [Description("A directory that cannot be reached at all is reported as a CONNECTION failure, bounded by " +
                   "TimeoutSeconds, without claiming success and without blaming the credentials for something " +
                   "that never got as far as a bind.")]
      public void TestPreviewReportsAnUnreachableDirectoryAsAConnectionFailure()
      {
         try
         {
            ConfigureLdap(ReachableConfiguration());

            Stopwatch stopwatch = Stopwatch.StartNew();

            string resultText;
            bool succeeded = _settings.TestLdapDirectory(PreviewMaxUsers, out resultText);

            stopwatch.Stop();

            Assert.IsFalse(succeeded,
               "The directory was never reached, so the preview must not report success. It said: " + resultText);

            // Bounded. TimeoutSeconds is 3 and a refused connection on the loopback
            // interface fails at once, so anything approaching half a minute means the
            // operation carried no bound at all.
            Assert.IsTrue(stopwatch.Elapsed.TotalSeconds < 30.0,
               "The preview was not bounded by TimeoutSeconds; it took " +
               stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " seconds.");

            StringAssert.Contains("Could not connect to the directory", resultText,
               "An unreachable directory must be reported as a connection failure, naming the stage that " +
               "failed. It said: " + resultText);
            StringAssert.Contains("could not be contacted", resultText,
               "The report must carry the reason as well as the stage. If this ever changes, check what " +
               "LdapClient::DescribeLastError returns for the result code the connect produced. It said: " +
               resultText);

            StringAssert.DoesNotContain("credentials", resultText,
               "A directory that was never reached cannot have rejected anything, and reporting it as a " +
               "credential problem is the misdiagnosis this path exists to remove. It said: " + resultText);

            if (File.Exists(LogHandler.GetErrorLogFileName()))
            {
               string reported = LogHandler.ReadErrorLog();

               foreach (string code in LdapErrorCodes)
               {
                  StringAssert.DoesNotContain(code, reported,
                     "The read-only preview reported " + code + " to the ERROR log. Its failures belong in the " +
                     "text it returns: " + reported);
               }
            }
         }
         finally
         {
            RestoreLdapSectionToOff();

            // Nothing above is expected to write here - the assertion just made is the
            // one that owns that claim - but the log is cleared anyway so that a failure
            // of that assertion cannot also fail every fixture that runs afterwards.
            LogHandler.DeleteErrorLog();
         }
      }

      /// <summary>
      ///    An empty Server is what a half-filled section looks like, and it is the one
      ///    case where the failure itself has no useful reason to report: connecting to
      ///    nothing yields a message about a host that was never named.
      ///
      ///    This asserts that the pre-flight check runs and says which key is empty.
      ///    Without it the administrator gets an error about a directory server they can
      ///    see perfectly well is not in the report, and has to work out for themselves
      ///    that the report is about an absence rather than about a machine.
      ///
      ///    SearchBase is deliberately filled in here so that the message names Server
      ///    and only Server. That is what makes this a check on the message naming the
      ///    RIGHT setting, rather than on it happening to mention every setting there is.
      /// </summary>
      [Test]
      [Description("With LDAP enabled but no Server, the preview names the missing setting instead of " +
                   "attempting a connection to an empty host name and reporting whatever that produces.")]
      public void TestPreviewNamesTheMissingServerSetting()
      {
         try
         {
            Dictionary<string, string> configuration = ReachableConfiguration();
            configuration["Server"] = "";
            ConfigureLdap(configuration);

            string resultText;
            bool succeeded = _settings.TestLdapDirectory(PreviewMaxUsers, out resultText);

            Assert.IsFalse(succeeded,
               "An incomplete configuration cannot have read anything. It said: " + resultText);

            StringAssert.Contains("Server", resultText,
               "The report must name the empty setting. It said: " + resultText);
            StringAssert.Contains("Nothing was contacted", resultText,
               "The report must say that nothing was attempted, so this is not confused with a directory that " +
               "failed to answer. It said: " + resultText);

            StringAssert.DoesNotContain("Could not connect", resultText,
               "With no Server configured there is nothing to connect to, and attempting it anyway produces a " +
               "report about a host name nobody entered. It said: " + resultText);
         }
         finally
         {
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    The counterpart to TestPreviewSeparatesAuthenticationFromReadingTheDirectory,
      ///    and the reason that test has to assert on the ABSENCE of the word
      ///    "incomplete": the same empty SearchBase produces a different report depending
      ///    on whether a UserDnTemplate is set, because LdapConfiguration::IsComplete is
      ///    consulted first and only requires a search base when UsesSearch() is true.
      ///
      ///    So the message asserted here is what the DEFAULT configuration produces -
      ///    BindMethod=0, no template - and the account-source message the other test
      ///    asserts on is reachable only once a template, or Negotiate binding, has
      ///    removed the need to search at all. Anyone editing either branch is likely to
      ///    reach for one of these two tests and not the other; together they pin the
      ///    ordering down.
      ///
      ///    That the report offers UserDnTemplate as well as SearchBase is not cosmetic
      ///    either. Where the mail server has no service credential to search with, the
      ///    template is the only one of the two that can be made to work, and a report
      ///    naming only SearchBase sends that administrator looking for a credential
      ///    nobody is going to give them.
      /// </summary>
      [Test]
      [Description("In search mode - a simple bind with no UserDnTemplate - an empty SearchBase is reported as " +
                   "an incomplete configuration that offers BOTH ways of fixing it, because either is a valid " +
                   "answer and only the administrator knows which suits their directory.")]
      public void TestPreviewOffersBothWaysToFixAMissingSearchBase()
      {
         try
         {
            Dictionary<string, string> configuration = ReachableConfiguration();
            configuration["SearchBase"] = "";
            configuration["UserDnTemplate"] = "";
            configuration["BindMethod"] = "0";
            ConfigureLdap(configuration);

            string resultText;
            bool succeeded = _settings.TestLdapDirectory(PreviewMaxUsers, out resultText);

            Assert.IsFalse(succeeded,
               "An incomplete configuration cannot have read anything. It said: " + resultText);

            StringAssert.Contains("incomplete", resultText,
               "In search mode the configuration genuinely is incomplete, and saying so is what distinguishes " +
               "this from the case where a UserDnTemplate makes it complete. It said: " + resultText);
            StringAssert.Contains("SearchBase", resultText,
               "The report must name the empty setting. It said: " + resultText);
            StringAssert.Contains("UserDnTemplate", resultText,
               "The report must offer the alternative, which is the only fix available to an administrator with " +
               "no service credential to search with. It said: " + resultText);

            StringAssert.DoesNotContain("Could not connect", resultText,
               "There is nothing to ask the directory yet, so nothing should have been asked. It said: " +
               resultText);
         }
         finally
         {
            RestoreLdapSectionToOff();
         }
      }

      /// <summary>
      ///    TestLdapDirectory is documented as read-only, and it sits on the Settings
      ///    object beside a great many methods whose whole purpose is to persist things.
      ///    The failure being guarded against is a preview that saves as a side effect -
      ///    most plausibly by writing its MaxUsers argument back as SyncMaxUsers, which
      ///    would mean that pressing "test with 50 users" quietly capped the real account
      ///    source at 50 and silently stopped provisioning everyone after them.
      ///
      ///    The values are read back through GetPrivateProfileString rather than through
      ///    the running server's view, because the question is what is on DISK: a change
      ///    that had not yet been re-read would still be there at the next service start,
      ///    and would be invisible to anything that asked the running server.
      ///
      ///    The configuration used is the one that goes furthest - enabled, complete, and
      ///    pointed at a dead port - so that the call actually constructs a client and
      ///    runs down the implementation rather than returning from the first branch.
      /// </summary>
      [Test]
      [Description("Running the preview changes no [LDAP] setting: it is a diagnostic, and neither the " +
                   "configuration it read nor the MaxUsers it was called with is written back to the ini.")]
      public void TestPreviewChangesNoSetting()
      {
         string[] keys =
         {
            "Enabled", "Server", "Port", "Security", "AllowUnprotectedPassword", "BindMethod",
            "SearchBase", "UserDnTemplate", "TimeoutSeconds", "SyncMaxUsers", "SyncFilter",
         };

         try
         {
            ConfigureLdap(ReachableConfiguration());

            var before = new Dictionary<string, string>();

            foreach (string key in keys)
               before[key] = ReadLdapSetting(key);

            string resultText;
            _settings.TestLdapDirectory(PreviewMaxUsers, out resultText);

            foreach (string key in keys)
            {
               Assert.AreEqual(before[key], ReadLdapSetting(key),
                  "The preview changed [LDAP] " + key + " in hMailServer.ini. It is a diagnostic and must " +
                  "persist nothing. It said: " + resultText);
            }
         }
         finally
         {
            RestoreLdapSectionToOff();

            // The call above provokes a connection failure on purpose. Nothing is
            // expected to be reported for it - the unreachable-directory test is the one
            // that owns that claim - so this only stops a regression there from
            // cascading into the rest of the run.
            LogHandler.DeleteErrorLog();
         }
      }
   }
}
