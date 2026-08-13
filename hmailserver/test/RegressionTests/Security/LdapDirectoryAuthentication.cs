// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
   ///    Exercises LDAP directory authentication (hMailServer.ini [LDAP] section).
   ///
   ///    An account flagged as Active-Directory-linked is authenticated by binding to
   ///    an LDAP directory as the user, instead of through LogonUser. That matters
   ///    because LogonUser requires the mail server HOST to be domain-joined: on a
   ///    workgroup host it returns ERROR_LOGON_FAILURE for a nonexistent domain, a real
   ///    but unreachable one, and a wrong password alike, so an AD-linked account on a
   ///    server in a DMZ can never log in and can never be told why.
   ///
   ///    None of these tests need a domain controller. A throwaway in-process LDAP
   ///    server speaks just enough of the protocol (BER-encoded bindRequest,
   ///    searchRequest, unbindRequest) to answer the real Windows LDAP client, which
   ///    lets the fixture assert on things a live directory could not be made to do on
   ///    demand: an ambiguous search, a server that accepts a connection and never
   ///    answers, and a filter carrying an injection attempt.
   /// </summary>
   [TestFixture]
   public class LdapDirectoryAuthentication : TestFixtureBase
   {
      // Ports 9370-9379 are this fixture's. A port per scenario rather than one
      // shared port, so that two of these tests running at the same time cannot
      // collide on a listener.
      private const int DirectoryPort = 9370;
      private const int SilentDirectoryPort = 9371;
      private const int MustStayUntouchedPort = 9372;
      private const int InjectionDirectoryPort = 9373;
      private const int AmbiguousDirectoryPort = 9374;
      private const int DisabledDirectoryPort = 9375;

      private const string ServiceDn = "CN=hmail-svc,OU=Service Accounts,DC=example,DC=test";
      private const string ServicePassword = "Serv1ce-Cred!";
      private const string SearchBase = "DC=example,DC=test";

      // Just the one component, deliberately: it keeps the assertion in the injection
      // test unambiguous, because any extra equality component seen on the wire came
      // from the username rather than from the template.
      private const string SearchFilter = "(sAMAccountName=%u)";

      private const string DirectoryUsername = "alice.morgan";
      private const string DirectoryUserDn = "CN=Alice Morgan,OU=hMailServer Test,DC=example,DC=test";
      private const string DirectoryPassword = "D1rectory-Pass!";

      // The password stored in hMailServer's own account row. It must stop working the
      // moment the account is authenticated by the directory; that is the assertion
      // that proves the bind actually happened rather than the local hash being
      // compared and the LDAP code being dead.
      private const string LocalPassword = "local-hash-only";

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      #region ini plumbing

      /// <summary>
      ///    Writes one [LDAP] value to every hMailServer.ini that exists.
      ///
      ///    Shared.IniFileSetting cannot be used: it writes to [Settings], and the LDAP
      ///    configuration lives in its own section so that it needs no change to
      ///    IniFileSettings.cpp. Its CandidateDirectories() is reused, so both classes
      ///    agree on which ini files are live.
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
      ///    There is no Reinitialize() here on purpose, and this is the one piece of the
      ///    fixture that depends on an implementation detail. LdapSettings does not use
      ///    IniFileSettings' cache (which is only refreshed by Reinitialize); it keys its
      ///    own cache on the ini file's last-write time and re-examines that at most
      ///    every two seconds. So a change is picked up without a reinitialize, but not
      ///    instantly - hence the wait, which has to be longer than that recheck window.
      /// </summary>
      private static void ConfigureLdap(Dictionary<string, string> values)
      {
         foreach (KeyValuePair<string, string> value in values)
            WriteLdapSetting(value.Key, value.Value);

         Thread.Sleep(2600);
      }

      private static Dictionary<string, string> SearchModeConfiguration(int port)
      {
         return new Dictionary<string, string>
         {
            { "Enabled", "1" },
            { "Server", "127.0.0.1" },
            { "Port", port.ToString() },

            // Security=0 with AllowUnprotectedPassword=1 is the only combination the
            // in-process directory can serve, because it speaks no TLS. It is NOT the
            // shipped default (that is Security=2, LDAPS, with the password refused on
            // an unprotected connection) and TestLdapRefusesToSendThePasswordOver-
            // AnUnprotectedConnection is what covers the default.
            { "Security", "0" },
            { "AllowUnprotectedPassword", "1" },
            { "BindMethod", "0" },
            { "SearchBase", SearchBase },
            { "UserSearchFilter", SearchFilter },
            { "UserDnTemplate", "" },
            { "ServiceUsername", ServiceDn },
            { "ServicePassword", ServicePassword },
            { "TimeoutSeconds", "5" },
            { "FallbackToWindowsLogon", "0" },
         };
      }

      /// <summary>
      ///    Puts the [LDAP] section back to "off" so that no later fixture consults a
      ///    directory. Always run from a finally: leaving Enabled=1 pointed at a dead
      ///    port would break every Active Directory account in the rest of the run.
      /// </summary>
      private static void DisableLdap()
      {
         WriteLdapSetting("Enabled", "0");
         WriteLdapSetting("Server", "");
         WriteLdapSetting("AllowUnprotectedPassword", "0");
         WriteLdapSetting("Security", "2");
      }

      #endregion

      #region account plumbing

      /// <summary>
      ///    An account with a local password AND an Active Directory link, which is the
      ///    shape the LDAP path applies to.
      /// </summary>
      private Account AddDirectoryLinkedAccount(string address, string adUsername)
      {
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, LocalPassword);

         account.IsAD = true;
         account.ADDomain = "EXAMPLE";
         account.ADUsername = adUsername;
         account.Save();

         return account;
      }

      #endregion

      #region log plumbing

      private static string WaitForErrorLogContaining(string text, int timeoutSeconds)
      {
         DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
         string content = "";

         while (DateTime.UtcNow < deadline)
         {
            string file = LogHandler.GetErrorLogFileName();

            if (File.Exists(file))
            {
               try
               {
                  content = LogHandler.ReadErrorLog();

                  if (content.Contains(text))
                     return content;
               }
               catch (IOException)
               {
                  // The server is writing to it; try again.
               }
            }

            Thread.Sleep(250);
         }

         return content;
      }

      /// <summary>
      ///    Clears the ERROR log after a test that provoked errors on purpose. Without
      ///    this the next fixture's PerformBasicSetup fails - it treats the existence of
      ///    an ERROR log as a failure - and every test after it reports somebody else's
      ///    deliberate error.
      /// </summary>
      private static void ClearDeliberateErrors()
      {
         Assert.IsTrue(LogHandler.ClearErrorLogUntilSettled(),
            "ERROR log entries were still arriving after the LDAP test finished.");
      }

      #endregion

      [Test]
      [Description("With [LDAP] Enabled=0, an Active-Directory-linked account is validated exactly as before: " +
                   "the configured directory is never contacted at all, even though every other LDAP setting " +
                   "points at a directory that would accept the password.")]
      public void TestLdapDisabledLeavesTheDirectoryUntouched()
      {
         // Auto-ban would refuse the later attempts at the connection, before they ever
         // reach the code under test, so the assertions would pass for the wrong reason.
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         using (FakeLdapDirectory directory = new FakeLdapDirectory(DisabledDirectoryPort))
         {
            directory.AllowBind(ServiceDn, ServicePassword);
            directory.AllowBind(DirectoryUserDn, DirectoryPassword);
            directory.AddUser(DirectoryUsername, DirectoryUserDn);

            try
            {
               Dictionary<string, string> configuration = SearchModeConfiguration(DisabledDirectoryPort);
               configuration["Enabled"] = "0";
               ConfigureLdap(configuration);

               Account account = AddDirectoryLinkedAccount("ldapoff@example.test", DirectoryUsername);

               // The directory password must not work, because the directory is not
               // being consulted.
               string error;
               Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, DirectoryPassword, out error),
                  "The directory password must not authenticate anyone while [LDAP] Enabled=0.");

               // And nothing may have been sent to the directory. This is the assertion
               // that fails if the Enabled flag is ignored, defaulted the wrong way, or
               // read from the wrong section.
               Assert.AreEqual(0, directory.ConnectionCount,
                  "The directory was contacted even though [LDAP] Enabled=0. Transcript: " + directory.Transcript);

               // A disabled LDAP section must report nothing OF ITS OWN. Narrowed from
               // "the ERROR log must not exist" during integration, because that was
               // asserting something this fixture does not own and which is now
               // legitimately false: the AD fallback path reports HM5911 when an account
               // is linked to a domain this computer cannot reach, which is exactly the
               // case this test sets up - a workgroup development machine and an account
               // pointed at "EXAMPLE". That diagnostic is correct and wanted, and this
               // test must not forbid it.
               //
               // What the test genuinely means is "the LDAP code stayed out of it", so it
               // now asserts on the LDAP error codes specifically. The stronger form would
               // pass or fail depending on whether the machine happens to be domain-joined.
               if (File.Exists(LogHandler.GetErrorLogFileName()))
               {
                  var reported = LogHandler.ReadErrorLog();

                  foreach (var code in new[] { "HM5920", "HM5921", "HM5922", "HM5923", "HM5924" })
                  {
                     StringAssert.DoesNotContain(code, reported,
                        "LDAP reported " + code + " while it was disabled: " + reported);
                  }
               }
            }
            finally
            {
               DisableLdap();
               _settings.ClearLogonFailureList();

               // The AD fallback's HM5911 is expected here (see above) and would otherwise
               // fail the setup of whatever fixture runs next, so it is cleared rather than
               // left for someone else to trip over.
               LogHandler.ClearErrorLogUntilSettled();
            }
         }
      }

      [Test]
      [Description("With LDAP enabled, an Active-Directory-linked account authenticates with its DIRECTORY " +
                   "password and no longer with the password stored in hMailServer; a wrong directory password " +
                   "is refused without anything being reported, because a rejection is not an outage.")]
      public void TestLdapBindAuthenticatesAndRejectsWithoutReportingAnError()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         using (FakeLdapDirectory directory = new FakeLdapDirectory(DirectoryPort))
         {
            directory.AllowBind(ServiceDn, ServicePassword);
            directory.AllowBind(DirectoryUserDn, DirectoryPassword);
            directory.AddUser(DirectoryUsername, DirectoryUserDn);

            try
            {
               ConfigureLdap(SearchModeConfiguration(DirectoryPort));

               Account account = AddDirectoryLinkedAccount("ldapbind@example.test", DirectoryUsername);

               Assert.IsTrue(new Pop3ClientSimulator().ConnectAndLogon(account.Address, DirectoryPassword),
                  "The directory password must authenticate the account. Transcript: " + directory.Transcript);

               // The service credential was used to find the user, and the user's own
               // bind was a separate one. Both must have reached the directory.
               List<string> bindsSeen = directory.BindDnsSeen;

               Assert.IsTrue(bindsSeen.Contains(ServiceDn),
                  "The configured ServiceUsername should have been used to search. Transcript: " + directory.Transcript);
               Assert.IsTrue(bindsSeen.Contains(DirectoryUserDn),
                  "The user's own DN should have been bound as. Transcript: " + directory.Transcript);

               // The account's own stored password must no longer work. If it does, the
               // LDAP path is dead code and every other assertion here is decoration.
               string error;
               Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, LocalPassword, out error),
                  "The locally stored password must not authenticate a directory-authenticated account.");

               Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, "not-the-password", out error),
                  "A wrong directory password must be refused.");

               // Nothing reported. A refused password is an ordinary event on an
               // internet-facing server; reporting it would fill the ERROR log from the
               // outside and would make a real outage invisible among the noise.
               Assert.IsFalse(File.Exists(LogHandler.GetErrorLogFileName()),
                  "A rejected password must not be reported as an error. ERROR log: " +
                  (File.Exists(LogHandler.GetErrorLogFileName()) ? LogHandler.ReadErrorLog() : ""));
            }
            finally
            {
               DisableLdap();
               _settings.ClearLogonFailureList();
            }
         }
      }

      [Test]
      [Description("A directory that accepts the connection and then never answers is reported as an " +
                   "infrastructure failure (HM5920) rather than as a wrong password, and the attempt is bounded " +
                   "by TimeoutSeconds instead of hanging the connection thread.")]
      public void TestLdapDirectoryOutageIsReportedAsAnInfrastructureFailure()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         // Accepts the TCP connection and then says nothing at all, holding the socket
         // open. That is the shape that hangs an unbounded client: a refused connection
         // fails immediately and proves nothing about the timeout.
         using (SilentListener silent = new SilentListener(SilentDirectoryPort))
         {
            try
            {
               Dictionary<string, string> configuration = SearchModeConfiguration(SilentDirectoryPort);
               configuration["TimeoutSeconds"] = "3";
               ConfigureLdap(configuration);

               Account account = AddDirectoryLinkedAccount("ldapdown@example.test", DirectoryUsername);

               Stopwatch stopwatch = Stopwatch.StartNew();

               string error;
               Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, DirectoryPassword, out error),
                  "A logon must be refused while the directory is unreachable.");

               stopwatch.Stop();

               Assert.IsTrue(silent.ConnectionCount > 0,
                  "The server should have connected to the configured directory.");

               // Bounded. TimeoutSeconds is 3 and at most two connections are made, so
               // anything approaching a minute means an operation was not bounded at
               // all - which is how one dead directory exhausts the connection threads.
               Assert.IsTrue(stopwatch.Elapsed.TotalSeconds < 60.0,
                  "The LDAP attempt was not bounded by TimeoutSeconds; it took " +
                  stopwatch.Elapsed.TotalSeconds.ToString("0.0") + " seconds.");

               string errorLog = WaitForErrorLogContaining("HM5920", 20);

               StringAssert.Contains("HM5920", errorLog,
                  "An unreachable directory must be reported as an infrastructure failure (HM5920), not " +
                  "silently treated as a wrong password.");
               StringAssert.Contains("unrelated to the credentials", errorLog,
                  "The report must say the failure is not about the credentials supplied.");
            }
            finally
            {
               DisableLdap();
               _settings.ClearLogonFailureList();
               ClearDeliberateErrors();
            }
         }
      }

      [Test]
      [Description("With Security=0 and AllowUnprotectedPassword=0 - which is the shipped default for the " +
                   "second of those - the password is never put on the wire: the logon is refused, HM5921 " +
                   "names both settings, and the directory is not even connected to.")]
      public void TestLdapRefusesToSendThePasswordOverAnUnprotectedConnection()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         // Counts connections and answers nothing. If a single connection arrives, the
         // password was about to be sent in the clear.
         using (SilentListener recorder = new SilentListener(MustStayUntouchedPort))
         {
            try
            {
               Dictionary<string, string> configuration = SearchModeConfiguration(MustStayUntouchedPort);
               configuration["AllowUnprotectedPassword"] = "0";
               ConfigureLdap(configuration);

               Account account = AddDirectoryLinkedAccount("ldapplain@example.test", DirectoryUsername);

               string error;
               Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, DirectoryPassword, out error),
                  "A logon must be refused rather than send the password over an unprotected connection.");

               // The assertion the whole test exists for. An implementation that binds
               // anyway would have opened a connection here.
               Assert.AreEqual(0, recorder.ConnectionCount,
                  "The server connected to the directory even though it must not send a password over an " +
                  "unprotected connection.");

               string errorLog = WaitForErrorLogContaining("HM5921", 20);

               StringAssert.Contains("HM5921", errorLog,
                  "Refusing to send a password unprotected must be reported so the administrator can fix the " +
                  "configuration rather than hunt a password problem.");
               StringAssert.Contains("AllowUnprotectedPassword", errorLog,
                  "The report must name the setting that would permit it.");
            }
            finally
            {
               DisableLdap();
               _settings.ClearLogonFailureList();
               ClearDeliberateErrors();
            }
         }
      }

      [Test]
      [Description("A username carrying an LDAP filter injection cannot make the search select a different " +
                   "directory entry: the injected text is escaped per RFC 4515, so it arrives as one literal " +
                   "assertion value and matches nothing.")]
      public void TestLdapFilterInjectionCannotSelectAnotherDirectoryEntry()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         using (FakeLdapDirectory directory = new FakeLdapDirectory(InjectionDirectoryPort))
         {
            directory.AllowBind(ServiceDn, ServicePassword);
            directory.AllowBind(DirectoryUserDn, DirectoryPassword);
            directory.AddUser(DirectoryUsername, DirectoryUserDn);

            try
            {
               ConfigureLdap(SearchModeConfiguration(InjectionDirectoryPort));

               // Closes the (sAMAccountName=...) component and opens a second one
               // naming a real directory user. Unescaped, the filter becomes
               // (&(sAMAccountName=nobody)(sAMAccountName=alice.morgan)) - a perfectly
               // valid filter, which the directory answers with Alice's DN. The bind
               // that follows uses ALICE'S OWN PASSWORD, supplied below, so an
               // unescaped implementation authenticates this account as Alice.
               string injected = "nobody)(sAMAccountName=" + DirectoryUsername;

               Account account = AddDirectoryLinkedAccount("ldapinject@example.test", injected);

               string error;
               Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, DirectoryPassword, out error),
                  "An LDAP filter injection in the directory user name must not authenticate anyone. " +
                  "Transcript: " + directory.Transcript);

               // The directory must have seen exactly one equality value, and it must be
               // the whole injected string rather than two separate components.
               Assert.AreEqual(1, directory.AssertionValuesSeen.Count,
                  "The search filter should carry exactly one equality component; the username added another. " +
                  "Values seen: " + string.Join(" | ", directory.AssertionValuesSeen.ToArray()));
               Assert.AreEqual(injected, directory.AssertionValuesSeen[0],
                  "The injected text should have arrived as a single literal assertion value.");
            }
            finally
            {
               DisableLdap();
               _settings.ClearLogonFailureList();

               // An escaped filter matches nothing, which is a rejection and reports
               // nothing - but clear the log anyway so a failure here cannot leak into
               // the next fixture.
               LogHandler.DeleteErrorLog();
            }
         }
      }

      [Test]
      [Description("A user search that matches more than one directory entry authenticates nobody - even with " +
                   "the correct password for one of them - and is reported as HM5923, because binding as one " +
                   "of several matches would authenticate a user against an entry that may not be theirs.")]
      public void TestLdapAmbiguousUserSearchAuthenticatesNobody()
      {
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         using (FakeLdapDirectory directory = new FakeLdapDirectory(AmbiguousDirectoryPort))
         {
            directory.AllowBind(ServiceDn, ServicePassword);
            directory.AllowBind(DirectoryUserDn, DirectoryPassword);

            // The same search key resolves to two entries, which is what a filter that
            // is not selective enough looks like on a real directory with two forests,
            // a duplicated OU or a migrated account left in place.
            directory.AddUser(DirectoryUsername, DirectoryUserDn);
            directory.AddUser(DirectoryUsername, "CN=Alice Morgan,OU=Migrated,DC=example,DC=test");

            try
            {
               ConfigureLdap(SearchModeConfiguration(AmbiguousDirectoryPort));

               Account account = AddDirectoryLinkedAccount("ldapdupe@example.test", DirectoryUsername);

               string error;
               Assert.IsFalse(new Pop3ClientSimulator().ConnectAndLogon(account.Address, DirectoryPassword, out error),
                  "An ambiguous user search must authenticate nobody, even with a correct password. " +
                  "Transcript: " + directory.Transcript);

               // No bind may have been attempted as either candidate: the ambiguity has
               // to be caught before the password is used.
               Assert.IsFalse(directory.BindDnsSeen.Contains(DirectoryUserDn),
                  "No user bind may be attempted when the search was ambiguous. Transcript: " + directory.Transcript);

               string errorLog = WaitForErrorLogContaining("HM5923", 20);

               StringAssert.Contains("HM5923", errorLog,
                  "An ambiguous user search must be reported so the filter can be made selective.");
               StringAssert.Contains("more than one", errorLog,
                  "The report must say what was ambiguous.");
            }
            finally
            {
               DisableLdap();
               _settings.ClearLogonFailureList();
               ClearDeliberateErrors();
            }
         }
      }

      #region the in-process directory

      /// <summary>
      ///    A TCP listener that accepts connections and answers nothing, holding each
      ///    socket open so the client waits rather than seeing a reset.
      ///
      ///    Two jobs, both about what does NOT happen: standing in for a directory that
      ///    has stopped answering, and proving that no connection was made at all.
      /// </summary>
      private sealed class SilentListener : IDisposable
      {
         private readonly TcpListener _listener;
         private readonly Thread _thread;
         private readonly List<TcpClient> _accepted = new List<TcpClient>();
         private readonly object _lock = new object();
         private volatile bool _running = true;

         public SilentListener(int port)
         {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();

            _thread = new Thread(Run) { IsBackground = true, Name = "Silent LDAP listener " + port };
            _thread.Start();
         }

         public int ConnectionCount
         {
            get
            {
               lock (_lock)
                  return _accepted.Count;
            }
         }

         private void Run()
         {
            while (_running)
            {
               TcpClient client;

               try
               {
                  client = _listener.AcceptTcpClient();
               }
               catch
               {
                  return; // the listener was stopped
               }

               // Held open rather than closed. A closed socket makes the client fail
               // immediately with a reset, which proves nothing about whether the
               // operation is bounded; a socket that stays open and says nothing is the
               // shape that hangs an unbounded client for ever.
               lock (_lock)
                  _accepted.Add(client);
            }
         }

         public void Dispose()
         {
            _running = false;

            try { _listener.Stop(); }
            catch { }

            lock (_lock)
            {
               foreach (TcpClient client in _accepted)
               {
                  try { client.Close(); }
                  catch { }
               }
            }

            try { _thread.Join(2000); }
            catch { }
         }
      }

      /// <summary>
      ///    Enough of an LDAP v3 server to answer the Windows LDAP client: simple binds,
      ///    one subtree search, and unbind. BER is hand-encoded because the whole point
      ///    is to avoid a dependency, and because the interesting assertions are about
      ///    the exact bytes the server received.
      ///
      ///    It does NOT speak TLS and does NOT implement SASL, so a Negotiate bind is
      ///    answered with authMethodNotSupported. That is a real limit of this fixture,
      ///    not of the code under test: BindMethod=1 is the mode that works against the
      ///    lab domain controller, and only a live directory can exercise it.
      /// </summary>
      private sealed class FakeLdapDirectory : IDisposable
      {
         private readonly TcpListener _listener;
         private readonly Thread _thread;
         private readonly object _lock = new object();

         private readonly Dictionary<string, string> _validBinds =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

         private readonly List<KeyValuePair<string, string>> _users = new List<KeyValuePair<string, string>>();
         private readonly List<string> _bindDnsSeen = new List<string>();
         private readonly List<string> _assertionValuesSeen = new List<string>();
         private readonly StringBuilder _transcript = new StringBuilder();

         private int _connectionCount;
         private volatile bool _running = true;

         public FakeLdapDirectory(int port)
         {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();

            _thread = new Thread(Run) { IsBackground = true, Name = "Fake LDAP directory " + port };
            _thread.Start();
         }

         /// <summary>A DN whose simple bind succeeds when the password matches.</summary>
         public void AllowBind(string dn, string password)
         {
            lock (_lock)
               _validBinds[dn] = password;
         }

         /// <summary>
         ///    An entry the search can find. Registering the same key twice is how the
         ///    ambiguous-match case is produced.
         /// </summary>
         public void AddUser(string searchKey, string dn)
         {
            lock (_lock)
               _users.Add(new KeyValuePair<string, string>(searchKey, dn));
         }

         public int ConnectionCount
         {
            get
            {
               lock (_lock)
                  return _connectionCount;
            }
         }

         public List<string> BindDnsSeen
         {
            get
            {
               lock (_lock)
                  return new List<string>(_bindDnsSeen);
            }
         }

         /// <summary>
         ///    Every equality assertion value seen in a search filter, in order. The
         ///    injection test asserts on this: an unescaped username shows up as an
         ///    extra value, an escaped one as a single literal.
         /// </summary>
         public List<string> AssertionValuesSeen
         {
            get
            {
               lock (_lock)
                  return new List<string>(_assertionValuesSeen);
            }
         }

         public string Transcript
         {
            get
            {
               lock (_lock)
                  return _transcript.ToString();
            }
         }

         private void Record(string line)
         {
            lock (_lock)
               _transcript.Append(line).Append("; ");
         }

         private void Run()
         {
            while (_running)
            {
               TcpClient client;

               try
               {
                  client = _listener.AcceptTcpClient();
               }
               catch
               {
                  return; // the listener was stopped
               }

               lock (_lock)
                  _connectionCount++;

               // A thread per connection: the search connection and the user bind may
               // overlap, and so may two tests.
               // ThreadStart named explicitly: an anonymous delegate with no parameter list
               // is convertible to both ThreadStart and ParameterizedThreadStart, so the
               // constructor call is ambiguous without it.
               Thread worker = new Thread(new ThreadStart(delegate { Serve(client); })) { IsBackground = true };
               worker.Start();
            }
         }

         private void Serve(TcpClient client)
         {
            try
            {
               using (client)
               using (NetworkStream stream = client.GetStream())
               {
                  stream.ReadTimeout = 30000;

                  while (true)
                  {
                     byte[] message = ReadMessage(stream);

                     if (message == null)
                        return;

                     Tlv envelope;
                     if (!ReadTlv(message, 0, out envelope) || envelope.Tag != 0x30)
                     {
                        Record("malformed envelope");
                        return;
                     }

                     Tlv messageIdTlv;
                     if (!ReadTlv(message, envelope.Start, out messageIdTlv) || messageIdTlv.Tag != 0x02)
                     {
                        Record("malformed message id");
                        return;
                     }

                     int messageId = ReadIntegerValue(message, messageIdTlv);

                     Tlv operation;
                     if (!ReadTlv(message, messageIdTlv.End, out operation))
                     {
                        Record("malformed operation");
                        return;
                     }

                     switch (operation.Tag)
                     {
                        case 0x60: // bindRequest
                           HandleBind(stream, message, messageId, operation);
                           break;

                        case 0x63: // searchRequest
                           HandleSearch(stream, message, messageId, operation);
                           break;

                        case 0x42: // unbindRequest
                           Record("unbind");
                           return;

                        default:
                           Record("unsupported operation 0x" + operation.Tag.ToString("x2"));
                           return;
                     }
                  }
               }
            }
            catch (Exception e)
            {
               // Never allowed to take the test process down; the transcript is what a
               // failing assertion prints.
               Record("exception " + e.GetType().Name + ": " + e.Message);
            }
         }

         private void HandleBind(NetworkStream stream, byte[] message, int messageId, Tlv operation)
         {
            Tlv version;
            Tlv name;
            Tlv credential;

            if (!ReadTlv(message, operation.Start, out version) ||
                !ReadTlv(message, version.End, out name) ||
                !ReadTlv(message, name.End, out credential))
            {
               Record("malformed bindRequest");
               return;
            }

            string dn = Text(message, name);
            int resultCode;

            if (credential.Tag == 0x80) // simple [0]
            {
               string password = Text(message, credential);

               lock (_lock)
                  _bindDnsSeen.Add(dn);

               if (dn.Length == 0 && password.Length == 0)
               {
                  // Anonymous bind. Accepted, so that a configuration with no
                  // ServiceUsername can still be exercised.
                  resultCode = 0;
                  Record("bind anonymous -> success");
               }
               else
               {
                  string expected;

                  bool known;
                  lock (_lock)
                     known = _validBinds.TryGetValue(dn, out expected);

                  resultCode = known && expected == password ? 0 : 49; // invalidCredentials
                  Record("bind '" + dn + "' -> " + (resultCode == 0 ? "success" : "49"));
               }
            }
            else
            {
               // sasl [3], which this fake does not implement.
               resultCode = 7; // authMethodNotSupported
               Record("bind sasl -> 7 (not implemented by the fake)");
            }

            Send(stream, Envelope(messageId, Encode(0x61, ResultBody(resultCode))));
         }

         private void HandleSearch(NetworkStream stream, byte[] message, int messageId, Tlv operation)
         {
            // baseObject, scope, derefAliases, sizeLimit, timeLimit, typesOnly, then
            // the filter.
            int offset = operation.Start;

            for (int field = 0; field < 6; field++)
            {
               Tlv skipped;
               if (!ReadTlv(message, offset, out skipped))
               {
                  Record("malformed searchRequest");
                  return;
               }

               offset = skipped.End;
            }

            Tlv filter;
            if (!ReadTlv(message, offset, out filter))
            {
               Record("missing search filter");
               return;
            }

            List<string> values = new List<string>();
            CollectEqualityValues(message, filter, values);

            List<string> matches = new List<string>();

            lock (_lock)
            {
               _assertionValuesSeen.AddRange(values);

               foreach (KeyValuePair<string, string> user in _users)
               {
                  foreach (string value in values)
                  {
                     // Exact equality, not a substring test. A substring test would
                     // make the injection assertion meaningless: the escaped filter
                     // still CONTAINS the injected user name, it just carries it as one
                     // literal value that no attribute equals.
                     if (string.Equals(value, user.Key, StringComparison.OrdinalIgnoreCase))
                     {
                        matches.Add(user.Value);
                        break;
                     }
                  }
               }
            }

            Record("search [" + string.Join(",", values.ToArray()) + "] -> " + matches.Count + " entries");

            foreach (string dn in matches)
               Send(stream, Envelope(messageId, Encode(0x64, SearchEntryBody(dn))));

            Send(stream, Envelope(messageId, Encode(0x65, ResultBody(0))));
         }

         private static void CollectEqualityValues(byte[] data, Tlv node, List<string> values)
         {
            if (node.Tag == 0xA3) // equalityMatch [3]: AttributeDescription, AssertionValue
            {
               Tlv attribute;
               if (!ReadTlv(data, node.Start, out attribute))
                  return;

               Tlv assertion;
               if (!ReadTlv(data, attribute.End, out assertion))
                  return;

               values.Add(Text(data, assertion));
               return;
            }

            // Primitive: nothing nested to walk. Checked after the equalityMatch case
            // because 0xA3 has the constructed bit set too.
            if ((node.Tag & 0x20) == 0)
               return;

            int offset = node.Start;

            while (offset < node.End)
            {
               Tlv child;
               if (!ReadTlv(data, offset, out child))
                  return;

               CollectEqualityValues(data, child, values);
               offset = child.End;
            }
         }

         private static void Send(NetworkStream stream, byte[] data)
         {
            stream.Write(data, 0, data.Length);
            stream.Flush();
         }

         #region BER

         private struct Tlv
         {
            public byte Tag;
            public int Start;  // offset of the first content byte
            public int Length;
            public int End;    // offset one past the last content byte
         }

         private static bool ReadTlv(byte[] data, int offset, out Tlv tlv)
         {
            tlv = new Tlv();

            if (offset < 0 || offset + 2 > data.Length)
               return false;

            byte tag = data[offset];
            int position = offset + 1;
            int lengthByte = data[position++];
            int length;

            if (lengthByte < 0x80)
            {
               length = lengthByte;
            }
            else
            {
               int count = lengthByte & 0x7f;

               if (count == 0 || count > 4 || position + count > data.Length)
                  return false;

               length = 0;

               for (int i = 0; i < count; i++)
                  length = (length << 8) | data[position++];
            }

            if (length < 0 || position + length > data.Length)
               return false;

            tlv.Tag = tag;
            tlv.Start = position;
            tlv.Length = length;
            tlv.End = position + length;

            return true;
         }

         private static int ReadIntegerValue(byte[] data, Tlv tlv)
         {
            int value = 0;

            for (int i = 0; i < tlv.Length; i++)
               value = (value << 8) | data[tlv.Start + i];

            return value;
         }

         private static string Text(byte[] data, Tlv tlv)
         {
            return Encoding.UTF8.GetString(data, tlv.Start, tlv.Length);
         }

         /// <summary>
         ///    Reads exactly one LDAPMessage: the tag, the BER length, and that many
         ///    content bytes. Length-prefixed rather than read-what-arrives, because a
         ///    message can be split across TCP segments and two can share one.
         /// </summary>
         private static byte[] ReadMessage(Stream stream)
         {
            List<byte> header = new List<byte>();

            int tag = stream.ReadByte();
            if (tag < 0)
               return null;

            header.Add((byte) tag);

            int lengthByte = stream.ReadByte();
            if (lengthByte < 0)
               return null;

            header.Add((byte) lengthByte);

            int length;

            if (lengthByte < 0x80)
            {
               length = lengthByte;
            }
            else
            {
               int count = lengthByte & 0x7f;

               if (count == 0 || count > 4)
                  return null;

               length = 0;

               for (int i = 0; i < count; i++)
               {
                  int next = stream.ReadByte();
                  if (next < 0)
                     return null;

                  header.Add((byte) next);
                  length = (length << 8) | next;
               }
            }

            // A sanity ceiling: nothing this fixture sends is anywhere near it, and
            // without one a corrupt length allocates whatever it says.
            if (length < 0 || length > 65536)
               return null;

            byte[] body = new byte[length];
            int read = 0;

            while (read < length)
            {
               int got = stream.Read(body, read, length - read);
               if (got <= 0)
                  return null;

               read += got;
            }

            byte[] all = new byte[header.Count + length];
            header.CopyTo(all, 0);
            Buffer.BlockCopy(body, 0, all, header.Count, length);

            return all;
         }

         private static byte[] Encode(byte tag, byte[] content)
         {
            List<byte> result = new List<byte> { tag };

            if (content.Length < 0x80)
            {
               result.Add((byte) content.Length);
            }
            else if (content.Length < 0x100)
            {
               result.Add(0x81);
               result.Add((byte) content.Length);
            }
            else
            {
               result.Add(0x82);
               result.Add((byte) (content.Length >> 8));
               result.Add((byte) (content.Length & 0xff));
            }

            result.AddRange(content);

            return result.ToArray();
         }

         private static byte[] Concat(params byte[][] parts)
         {
            List<byte> result = new List<byte>();

            foreach (byte[] part in parts)
               result.AddRange(part);

            return result.ToArray();
         }

         /// <summary>Minimal-length, non-negative BER integer under the given tag.</summary>
         private static byte[] EncodeInteger(byte tag, int value)
         {
            List<byte> raw = new List<byte>();

            if (value == 0)
            {
               raw.Add(0);
            }
            else
            {
               int remaining = value;

               while (remaining != 0)
               {
                  raw.Insert(0, (byte) (remaining & 0xff));
                  remaining = remaining >> 8;
               }

               // A leading byte with the high bit set would read as negative.
               if ((raw[0] & 0x80) != 0)
                  raw.Insert(0, 0);
            }

            return Encode(tag, raw.ToArray());
         }

         private static byte[] EncodeOctetString(string value)
         {
            return Encode(0x04, Encoding.UTF8.GetBytes(value));
         }

         private static byte[] Envelope(int messageId, byte[] operation)
         {
            return Encode(0x30, Concat(EncodeInteger(0x02, messageId), operation));
         }

         /// <summary>LDAPResult: resultCode, matchedDN, diagnosticMessage.</summary>
         private static byte[] ResultBody(int resultCode)
         {
            return Concat(EncodeInteger(0x0A, resultCode), EncodeOctetString(""), EncodeOctetString(""));
         }

         /// <summary>SearchResultEntry: objectName plus a one-attribute PartialAttributeList.</summary>
         private static byte[] SearchEntryBody(string dn)
         {
            byte[] valueSet = Encode(0x31, EncodeOctetString(dn));
            byte[] attribute = Encode(0x30, Concat(EncodeOctetString("distinguishedName"), valueSet));
            byte[] attributes = Encode(0x30, attribute);

            return Concat(EncodeOctetString(dn), attributes);
         }

         #endregion

         public void Dispose()
         {
            _running = false;

            try { _listener.Stop(); }
            catch { }

            try { _thread.Join(2000); }
            catch { }
         }
      }

      #endregion
   }
}
