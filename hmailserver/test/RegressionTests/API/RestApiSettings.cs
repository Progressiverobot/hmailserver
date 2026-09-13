// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    The settings write routes: PUT /api/v1/settings, and GET and PUT of
   ///    /api/v1/settings/antispam and /api/v1/settings/logging.
   ///
   ///    Every value a PUT claims to have changed is read back through COM, as
   ///    the Control Panel reads it, and never only from the response; every
   ///    refusal is checked to have changed nothing; and where the running
   ///    server behaves differently afterwards - the SMTP banner, the size a
   ///    MAIL FROM may declare, the public namespace IMAP advertises - that is
   ///    observed on the wire. A snapshot of every setting a test may touch is
   ///    taken after the suite's own setup and restored in TearDown, because
   ///    the whole suite runs on these settings after this fixture.
   /// </summary>
   [TestFixture]
   public class RestApiSettings : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9120;
      // A [Settings] key no shipped setting uses, as IniSettingsOverCom's probe is.
      private const string IniProbeKey = "RestApiIniProbe";
      private const string AdminPassword = "testar";

      private Snapshot _before;

      // The settings the tests below change, read and written through the
      // same COM properties the Control Panel uses.
      private sealed class Snapshot
      {
         public string HostName;
         public string DefaultDomain;
         public int MaxMessageSize;
         public string SmtpRelayer;
         public int SmtpRelayerPort;
         public eConnectionSecurity SmtpRelayerConnectionSecurity;
         public bool SmtpRelayerRequiresAuthentication;
         public string SmtpRelayerUsername;
         public string ImapPublicFolderName;
         public bool ImapSortEnabled;
         public bool AutoBanOnLogonFailure;
         public int MaxInvalidLogonAttempts;
         public int MaxInvalidLogonAttemptsWithin;
         public int AutoBanMinutes;
         public int SmtpNoOfTries;
         public int SmtpMinutesBetweenTry;

         public int SpamMarkThreshold;
         public int SpamDeleteThreshold;
         public bool PrependSubject;
         public string PrependSubjectText;
         public bool UseSpf;
         public int UseSpfScore;
         public string SpamAssassinHost;
         public int SpamAssassinPort;
         public int TarpitCount;
         public int TarpitDelay;
         public int GreyListingInitialDelay;
         public bool DkimVerificationEnabled;
         public int AntiSpamMaximumMessageSize;

         public bool LogDebug;
         public bool LogTcpIp;
         public bool KeepFilesOpen;
         public eLogOutputFormat LogFormat;
      }

      private Snapshot Take()
      {
         hMailServer.AntiSpam antiSpam = _settings.AntiSpam;
         Logging logging = _settings.Logging;

         return new Snapshot
         {
            HostName = _settings.HostName,
            DefaultDomain = _settings.DefaultDomain,
            MaxMessageSize = _settings.MaxMessageSize,
            SmtpRelayer = _settings.SMTPRelayer,
            SmtpRelayerPort = _settings.SMTPRelayerPort,
            SmtpRelayerConnectionSecurity = _settings.SMTPRelayerConnectionSecurity,
            SmtpRelayerRequiresAuthentication = _settings.SMTPRelayerRequiresAuthentication,
            SmtpRelayerUsername = _settings.SMTPRelayerUsername,
            ImapPublicFolderName = _settings.IMAPPublicFolderName,
            ImapSortEnabled = _settings.IMAPSortEnabled,
            AutoBanOnLogonFailure = _settings.AutoBanOnLogonFailure,
            MaxInvalidLogonAttempts = _settings.MaxInvalidLogonAttempts,
            MaxInvalidLogonAttemptsWithin = _settings.MaxInvalidLogonAttemptsWithin,
            AutoBanMinutes = _settings.AutoBanMinutes,
            SmtpNoOfTries = _settings.SMTPNoOfTries,
            SmtpMinutesBetweenTry = _settings.SMTPMinutesBetweenTry,

            SpamMarkThreshold = antiSpam.SpamMarkThreshold,
            SpamDeleteThreshold = antiSpam.SpamDeleteThreshold,
            PrependSubject = antiSpam.PrependSubject,
            PrependSubjectText = antiSpam.PrependSubjectText,
            UseSpf = antiSpam.UseSPF,
            UseSpfScore = antiSpam.UseSPFScore,
            SpamAssassinHost = antiSpam.SpamAssassinHost,
            SpamAssassinPort = antiSpam.SpamAssassinPort,
            TarpitCount = antiSpam.TarpitCount,
            TarpitDelay = antiSpam.TarpitDelay,
            GreyListingInitialDelay = antiSpam.GreyListingInitialDelay,
            DkimVerificationEnabled = antiSpam.DKIMVerificationEnabled,
            AntiSpamMaximumMessageSize = antiSpam.MaximumMessageSize,

            LogDebug = logging.LogDebug,
            LogTcpIp = logging.LogTCPIP,
            KeepFilesOpen = logging.KeepFilesOpen,
            LogFormat = logging.LogFormat
         };
      }

      // Each value is written back only when it differs, so a test that
      // changed three settings costs three writes here and not forty.
      private void Restore(Snapshot s)
      {
         if (_settings.HostName != s.HostName) _settings.HostName = s.HostName;
         if (_settings.DefaultDomain != s.DefaultDomain) _settings.DefaultDomain = s.DefaultDomain;
         if (_settings.MaxMessageSize != s.MaxMessageSize) _settings.MaxMessageSize = s.MaxMessageSize;
         if (_settings.SMTPRelayer != s.SmtpRelayer) _settings.SMTPRelayer = s.SmtpRelayer;
         if (_settings.SMTPRelayerPort != s.SmtpRelayerPort) _settings.SMTPRelayerPort = s.SmtpRelayerPort;
         if (_settings.SMTPRelayerConnectionSecurity != s.SmtpRelayerConnectionSecurity) _settings.SMTPRelayerConnectionSecurity = s.SmtpRelayerConnectionSecurity;
         if (_settings.SMTPRelayerRequiresAuthentication != s.SmtpRelayerRequiresAuthentication) _settings.SMTPRelayerRequiresAuthentication = s.SmtpRelayerRequiresAuthentication;
         if (_settings.SMTPRelayerUsername != s.SmtpRelayerUsername) _settings.SMTPRelayerUsername = s.SmtpRelayerUsername;
         if (_settings.IMAPPublicFolderName != s.ImapPublicFolderName) _settings.IMAPPublicFolderName = s.ImapPublicFolderName;
         if (_settings.IMAPSortEnabled != s.ImapSortEnabled) _settings.IMAPSortEnabled = s.ImapSortEnabled;
         if (_settings.AutoBanOnLogonFailure != s.AutoBanOnLogonFailure) _settings.AutoBanOnLogonFailure = s.AutoBanOnLogonFailure;
         if (_settings.MaxInvalidLogonAttempts != s.MaxInvalidLogonAttempts) _settings.MaxInvalidLogonAttempts = s.MaxInvalidLogonAttempts;
         if (_settings.MaxInvalidLogonAttemptsWithin != s.MaxInvalidLogonAttemptsWithin) _settings.MaxInvalidLogonAttemptsWithin = s.MaxInvalidLogonAttemptsWithin;
         if (_settings.AutoBanMinutes != s.AutoBanMinutes) _settings.AutoBanMinutes = s.AutoBanMinutes;
         if (_settings.SMTPNoOfTries != s.SmtpNoOfTries) _settings.SMTPNoOfTries = s.SmtpNoOfTries;
         if (_settings.SMTPMinutesBetweenTry != s.SmtpMinutesBetweenTry) _settings.SMTPMinutesBetweenTry = s.SmtpMinutesBetweenTry;

         // The relayer password has no COM getter; the tests set it to a value
         // no other fixture depends on, and clearing it here leaves the store
         // as the suite expects it: a relayer that needs no credential.
         _settings.SetSMTPRelayerPassword("");

         hMailServer.AntiSpam antiSpam = _settings.AntiSpam;
         if (antiSpam.SpamMarkThreshold != s.SpamMarkThreshold) antiSpam.SpamMarkThreshold = s.SpamMarkThreshold;
         if (antiSpam.SpamDeleteThreshold != s.SpamDeleteThreshold) antiSpam.SpamDeleteThreshold = s.SpamDeleteThreshold;
         if (antiSpam.PrependSubject != s.PrependSubject) antiSpam.PrependSubject = s.PrependSubject;
         if (antiSpam.PrependSubjectText != s.PrependSubjectText) antiSpam.PrependSubjectText = s.PrependSubjectText;
         if (antiSpam.UseSPF != s.UseSpf) antiSpam.UseSPF = s.UseSpf;
         if (antiSpam.UseSPFScore != s.UseSpfScore) antiSpam.UseSPFScore = s.UseSpfScore;
         if (antiSpam.SpamAssassinHost != s.SpamAssassinHost) antiSpam.SpamAssassinHost = s.SpamAssassinHost;
         if (antiSpam.SpamAssassinPort != s.SpamAssassinPort) antiSpam.SpamAssassinPort = s.SpamAssassinPort;
         if (antiSpam.TarpitCount != s.TarpitCount) antiSpam.TarpitCount = s.TarpitCount;
         if (antiSpam.TarpitDelay != s.TarpitDelay) antiSpam.TarpitDelay = s.TarpitDelay;
         if (antiSpam.GreyListingInitialDelay != s.GreyListingInitialDelay) antiSpam.GreyListingInitialDelay = s.GreyListingInitialDelay;
         if (antiSpam.DKIMVerificationEnabled != s.DkimVerificationEnabled) antiSpam.DKIMVerificationEnabled = s.DkimVerificationEnabled;
         if (antiSpam.MaximumMessageSize != s.AntiSpamMaximumMessageSize) antiSpam.MaximumMessageSize = s.AntiSpamMaximumMessageSize;

         Logging logging = _settings.Logging;
         if (logging.LogDebug != s.LogDebug) logging.LogDebug = s.LogDebug;
         if (logging.LogTCPIP != s.LogTcpIp) logging.LogTCPIP = s.LogTcpIp;
         if (logging.KeepFilesOpen != s.KeepFilesOpen) logging.KeepFilesOpen = s.KeepFilesOpen;
         if (logging.LogFormat != s.LogFormat) logging.LogFormat = s.LogFormat;
      }

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

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);
         _before = Take();

         RestPort = RestListener.Start(RestPort);

         _application.Reinitialize();

         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         try
         {
            Restore(_before);
            _settings.ClearLogonFailureList();
            _settings.DeleteIniSetting(IniProbeKey);
         }
         finally
         {
            RestListener.Stop();
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("The server group keeps its ten original keys, a PUT of sixteen keys is read back through COM as the Control Panel would read it, the relayer password is accepted and never emitted, and a GET afterwards agrees with the PUT.")]
      public void ServerGroupRoundTripsThroughCom()
      {
         (int status, string body) = Http("GET", "/api/v1/settings");
         Assert.AreEqual(200, status, body);

         // The keys the snapshot has always had, with COM's values, so that
         // the coverage fixture and the Control Deck keep reading what they read.
         StringAssert.Contains("\"host_name\":\"" + _settings.HostName + "\"", body);
         StringAssert.Contains("\"default_domain\":\"" + _settings.DefaultDomain + "\"", body);
         StringAssert.Contains("\"max_message_size_kb\":" + _settings.MaxMessageSize + ",", body);
         StringAssert.Contains("\"max_smtp_connections\":" + _settings.MaxSMTPConnections + ",", body);
         StringAssert.Contains("\"max_imap_connections\":" + _settings.MaxIMAPConnections + ",", body);
         StringAssert.Contains("\"max_pop3_connections\":" + _settings.MaxPOP3Connections + ",", body);
         StringAssert.Contains("\"smtp_relayer\":\"" + _settings.SMTPRelayer + "\"", body);
         StringAssert.Contains("\"smtp_relayer_port\":" + _settings.SMTPRelayerPort + ",", body);
         StringAssert.Contains("\"log_smtp_conversations\":" + (_settings.Logging.LogSMTP ? "true" : "false"), body);
         StringAssert.Contains("\"log_imap_conversations\":" + (_settings.Logging.LogIMAP ? "true" : "false"), body);

         // The extended group, a sample of each type.
         StringAssert.Contains("\"imap_hierarchy_delimiter\":\"" + _settings.IMAPHierarchyDelimiter + "\"", body);
         StringAssert.Contains("\"tcpip_threads\":" + _settings.TCPIPThreads + ",", body);
         StringAssert.Contains("\"imap_sort_enabled\":" + (_settings.IMAPSortEnabled ? "true" : "false"), body);
         StringAssert.Contains("\"smtp_relayer_connection_security\":\"", body);
         StringAssert.DoesNotContain("password", body, "No password is ever emitted.");

         (int putStatus, string putBody) = Http("PUT", "/api/v1/settings",
            "{\"host_name\":\"rest-settings.test\"," +
            "\"max_message_size_kb\":1234," +
            "\"smtp_relayer\":\"relay.rest-settings.test\"," +
            "\"smtp_relayer_port\":2587," +
            "\"smtp_relayer_connection_security\":\"starttls_required\"," +
            "\"smtp_relayer_requires_authentication\":true," +
            "\"smtp_relayer_username\":\"relayuser\"," +
            "\"smtp_relayer_password\":\"relay-secret-7\"," +
            "\"imap_public_folder_name\":\"RestPublic\"," +
            "\"imap_sort_enabled\":false," +
            "\"auto_ban_on_logon_failure\":true," +
            "\"max_invalid_logon_attempts\":7," +
            "\"minutes_before_reset\":11," +
            "\"minutes_to_ban\":13," +
            "\"smtp_no_of_tries\":4," +
            "\"smtp_minutes_between_try\":9}");
         Assert.AreEqual(200, putStatus, putBody);

         // The answer is the whole group, with the new values and no secret.
         StringAssert.Contains("\"host_name\":\"rest-settings.test\"", putBody);
         StringAssert.Contains("\"max_message_size_kb\":1234,", putBody);
         StringAssert.Contains("\"smtp_relayer_connection_security\":\"starttls_required\"", putBody);
         StringAssert.Contains("\"imap_sort_enabled\":false", putBody);
         StringAssert.Contains("\"tcpip_threads\":", putBody, "A PUT answers with the whole group, not only the keys it changed.");
         StringAssert.DoesNotContain("relay-secret-7", putBody);
         StringAssert.DoesNotContain("password", putBody);

         // COM reads back what the Control Panel would show.
         Assert.AreEqual("rest-settings.test", _settings.HostName);
         Assert.AreEqual(1234, _settings.MaxMessageSize);
         Assert.AreEqual("relay.rest-settings.test", _settings.SMTPRelayer);
         Assert.AreEqual(2587, _settings.SMTPRelayerPort);
         Assert.AreEqual(eConnectionSecurity.eCSSTARTTLSRequired, _settings.SMTPRelayerConnectionSecurity);
         Assert.IsTrue(_settings.SMTPRelayerRequiresAuthentication);
         Assert.AreEqual("relayuser", _settings.SMTPRelayerUsername);
         Assert.AreEqual("RestPublic", _settings.IMAPPublicFolderName);
         Assert.IsFalse(_settings.IMAPSortEnabled);
         Assert.IsTrue(_settings.AutoBanOnLogonFailure);
         Assert.AreEqual(7, _settings.MaxInvalidLogonAttempts);
         Assert.AreEqual(11, _settings.MaxInvalidLogonAttemptsWithin);
         Assert.AreEqual(13, _settings.AutoBanMinutes);
         Assert.AreEqual(4, _settings.SMTPNoOfTries);
         Assert.AreEqual(9, _settings.SMTPMinutesBetweenTry);

         // And a read afterwards is the same document the PUT answered with.
         Assert.AreEqual(putBody, Http("GET", "/api/v1/settings").body);

         // A PUT that names nothing changes nothing and still answers the group.
         (int emptyStatus, string emptyBody) = Http("PUT", "/api/v1/settings", "{}");
         Assert.AreEqual(200, emptyStatus, emptyBody);
         Assert.AreEqual(putBody, emptyBody);
      }

      [Test]
      [Description("The anti-spam group is read, a PUT of thirteen keys including the two INI-backed tarpit values is read back through COM, and the response is the whole group.")]
      public void AntiSpamGroupRoundTripsThroughCom()
      {
         hMailServer.AntiSpam antiSpam = _settings.AntiSpam;

         (int status, string body) = Http("GET", "/api/v1/settings/antispam");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"spam_mark_threshold\":" + antiSpam.SpamMarkThreshold + ",", body);
         StringAssert.Contains("\"spamassassin_host\":\"" + antiSpam.SpamAssassinHost + "\"", body);
         StringAssert.Contains("\"tarpit_delay\":" + antiSpam.TarpitDelay + ",", body);
         StringAssert.Contains("\"greylisting_enabled\":" + (antiSpam.GreyListingEnabled ? "true" : "false"), body);
         StringAssert.DoesNotContain("host_name", body, "The anti-spam group is its own resource.");

         (int putStatus, string putBody) = Http("PUT", "/api/v1/settings/antispam",
            "{\"spam_mark_threshold\":7," +
            "\"spam_delete_threshold\":21," +
            "\"prepend_subject\":true," +
            "\"prepend_subject_text\":\"[REST-SPAM]\"," +
            "\"use_spf\":true," +
            "\"use_spf_score\":3," +
            "\"spamassassin_host\":\"sa.rest-settings.test\"," +
            "\"spamassassin_port\":7830," +
            "\"tarpit_count\":5," +
            "\"tarpit_delay\":2," +
            "\"greylisting_initial_delay\":17," +
            "\"dkim_verification_enabled\":true," +
            "\"maximum_message_size_kb\":2048}");
         Assert.AreEqual(200, putStatus, putBody);
         StringAssert.Contains("\"spam_mark_threshold\":7,", putBody);
         StringAssert.Contains("\"prepend_subject_text\":\"[REST-SPAM]\"", putBody);
         StringAssert.Contains("\"tarpit_delay\":2,", putBody);
         StringAssert.Contains("\"check_ptr\":", putBody, "A PUT answers with the whole group.");

         Assert.AreEqual(7, antiSpam.SpamMarkThreshold);
         Assert.AreEqual(21, antiSpam.SpamDeleteThreshold);
         Assert.IsTrue(antiSpam.PrependSubject);
         Assert.AreEqual("[REST-SPAM]", antiSpam.PrependSubjectText);
         Assert.IsTrue(antiSpam.UseSPF);
         Assert.AreEqual(3, antiSpam.UseSPFScore);
         Assert.AreEqual("sa.rest-settings.test", antiSpam.SpamAssassinHost);
         Assert.AreEqual(7830, antiSpam.SpamAssassinPort);
         Assert.AreEqual(5, antiSpam.TarpitCount);
         Assert.AreEqual(2, antiSpam.TarpitDelay);
         Assert.AreEqual(17, antiSpam.GreyListingInitialDelay);
         Assert.IsTrue(antiSpam.DKIMVerificationEnabled);
         Assert.AreEqual(2048, antiSpam.MaximumMessageSize);

         Assert.AreEqual(putBody, Http("GET", "/api/v1/settings/antispam").body);
      }

      [Test]
      [Description("The logging group is read with the log directory and file names, a PUT is read back through COM, and the read-only keys are refused with a 400 that names them.")]
      public void LoggingGroupRoundTripsThroughCom()
      {
         Logging logging = _settings.Logging;

         (int status, string body) = Http("GET", "/api/v1/settings/logging");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"enabled\":" + (logging.Enabled ? "true" : "false"), body);
         StringAssert.Contains("\"log_debug\":" + (logging.LogDebug ? "true" : "false"), body);
         StringAssert.Contains("\"log_format\":\"" + (logging.LogFormat == eLogOutputFormat.hLogFormatCSA ? "csa" : "default") + "\"", body);

         // The directory is a Windows path, whose backslashes JSON doubles.
         StringAssert.Contains("\"directory\":\"" + logging.Directory.Replace("\\", "\\\\") + "\"", body);
         StringAssert.Contains("\"current_error_log\":\"" + logging.CurrentErrorLog.Replace("\\", "\\\\") + "\"", body);

         (int putStatus, string putBody) = Http("PUT", "/api/v1/settings/logging",
            "{\"log_debug\":true,\"log_tcpip\":true,\"keep_files_open\":false,\"log_format\":\"csa\"}");
         Assert.AreEqual(200, putStatus, putBody);
         StringAssert.Contains("\"log_debug\":true", putBody);
         StringAssert.Contains("\"log_format\":\"csa\"", putBody);
         StringAssert.Contains("\"directory\":", putBody, "A PUT answers with the whole group, read-only keys included.");

         Assert.IsTrue(logging.LogDebug);
         Assert.IsTrue(logging.LogTCPIP);
         Assert.IsFalse(logging.KeepFilesOpen);
         Assert.AreEqual(eLogOutputFormat.hLogFormatCSA, logging.LogFormat);

         // The facts about the log are not settings.
         (int readOnlyStatus, string readOnlyBody) = Http("PUT", "/api/v1/settings/logging", "{\"log_debug\":false,\"directory\":\"C:\\\\elsewhere\"}");
         Assert.AreEqual(400, readOnlyStatus, readOnlyBody);
         StringAssert.Contains("directory is read-only", readOnlyBody);
         Assert.IsTrue(logging.LogDebug, "A refused PUT applies none of its keys, not even the ones that were fine.");
      }

      [Test]
      [Description("An unknown key, a value of the wrong type, a word outside an enumeration, a body that is not an object, and a value a setting refuses are each 400, name the key or carry the setting's own sentence, and change nothing - including the acceptable keys sent beside them.")]
      public void RefusalsChangeNothing()
      {
         string hostName = _settings.HostName;
         int maxMessageSize = _settings.MaxMessageSize;
         string defaultDomain = _settings.DefaultDomain;
         string delimiter = _settings.IMAPHierarchyDelimiter;
         hMailServer.AntiSpam antiSpam = _settings.AntiSpam;
         int spamMarkThreshold = antiSpam.SpamMarkThreshold;
         int tarpitDelay = antiSpam.TarpitDelay;

         (int status, string body) unknown = Http("PUT", "/api/v1/settings", "{\"host_name\":\"changed.test\",\"no_such_setting\":1}");
         Assert.AreEqual(400, unknown.status, unknown.body);
         StringAssert.Contains("no_such_setting", unknown.body);
         Assert.AreEqual(hostName, _settings.HostName, "The acceptable key beside an unknown one must not have been applied.");

         (int status, string body) wrongType = Http("PUT", "/api/v1/settings", "{\"max_message_size_kb\":\"big\"}");
         Assert.AreEqual(400, wrongType.status, wrongType.body);
         StringAssert.Contains("max_message_size_kb", wrongType.body);
         Assert.AreEqual(maxMessageSize, _settings.MaxMessageSize);

         (int status, string body) fraction = Http("PUT", "/api/v1/settings", "{\"max_message_size_kb\":1.5}");
         Assert.AreEqual(400, fraction.status, fraction.body);
         StringAssert.Contains("max_message_size_kb must be an integer", fraction.body);

         (int status, string body) wrongBool = Http("PUT", "/api/v1/settings", "{\"imap_sort_enabled\":\"yes\"}");
         Assert.AreEqual(400, wrongBool.status, wrongBool.body);
         StringAssert.Contains("imap_sort_enabled must be true or false", wrongBool.body);

         (int status, string body) wrongWord = Http("PUT", "/api/v1/settings", "{\"smtp_relayer_connection_security\":\"ssl\"}");
         Assert.AreEqual(400, wrongWord.status, wrongWord.body);
         StringAssert.Contains("starttls_optional", wrongWord.body, "The refusal lists the words the key takes.");

         (int status, string body) notObject = Http("PUT", "/api/v1/settings", "[1,2]");
         Assert.AreEqual(400, notObject.status, notObject.body);

         (int status, string body) otherGroup = Http("PUT", "/api/v1/settings/antispam", "{\"host_name\":\"x\"}");
         Assert.AreEqual(400, otherGroup.status, otherGroup.body);
         StringAssert.Contains("host_name", otherGroup.body);

         // The anti-spam setters' own sentences, and nothing applied beside them.
         (int status, string body) tarpit = Http("PUT", "/api/v1/settings/antispam", "{\"spam_mark_threshold\":9,\"tarpit_delay\":31}");
         Assert.AreEqual(400, tarpit.status, tarpit.body);
         StringAssert.Contains("TarpitDelay must be between 0 and 30 seconds.", tarpit.body);
         Assert.AreEqual(spamMarkThreshold, antiSpam.SpamMarkThreshold);
         Assert.AreEqual(tarpitDelay, antiSpam.TarpitDelay);

         (int status, string body) tarpitCount = Http("PUT", "/api/v1/settings/antispam", "{\"tarpit_count\":-1}");
         Assert.AreEqual(400, tarpitCount.status, tarpitCount.body);
         StringAssert.Contains("TarpitCount cannot be negative.", tarpitCount.body);

         // The hierarchy delimiter is refused while a folder contains the new
         // character, in the sentence the Control Panel shows, and the
         // default domain named in the same body stays as it was.
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "delimiter@example.test", "test");
         IMAPFolder folder = account.IMAPFolders.Add("Rest-Delim");
         folder.Save();

         (int status, string body) refused = Http("PUT", "/api/v1/settings", "{\"default_domain\":\"changed.test\",\"imap_hierarchy_delimiter\":\"-\"}");
         Assert.AreEqual(400, refused.status, refused.body);
         StringAssert.Contains("not possible to change the IMAP hierarchy delimiter", refused.body);
         Assert.AreEqual(defaultDomain, _settings.DefaultDomain, "A refused PUT applies nothing.");
         Assert.AreEqual(delimiter, _settings.IMAPHierarchyDelimiter);
      }

      [Test]
      [Description("A key restricted to named domains is refused every settings route, a read-only key may read and not write, and an unrestricted full key writes as the administrator does.")]
      public void KeysAreHeldToTheirScope()
      {
         Logging logging = _settings.Logging;
         bool logDebug = logging.LogDebug;

         (string scopedId, string scopedKey) = CreateKey("restset - scoped", "full", "example.test");
         (string readOnlyId, string readOnlyKey) = CreateKey("restset - readonly", "readonly", null);
         (string fullId, string fullKey) = CreateKey("restset - full", "full", null);

         try
         {
            foreach (string path in new[] { "/api/v1/settings", "/api/v1/settings/antispam", "/api/v1/settings/logging" })
            {
               Assert.AreEqual(403, Bearer("GET", path, scopedKey).status, "A domain-restricted key must be refused GET " + path);
               Assert.AreEqual(403, Bearer("PUT", path, scopedKey, "{\"log_debug\":" + (!logDebug).ToString().ToLowerInvariant() + "}").status,
                  "A domain-restricted key must be refused PUT " + path);

               Assert.AreEqual(200, Bearer("GET", path, readOnlyKey).status, "A read-only key may read " + path);
               Assert.AreEqual(403, Bearer("PUT", path, readOnlyKey, "{}").status, "A read-only key must be refused PUT " + path);
            }

            Assert.AreEqual(logDebug, logging.LogDebug, "No refused PUT may have changed a setting.");

            (int status, string body) = Bearer("PUT", "/api/v1/settings/logging", fullKey, "{\"log_debug\":" + (!logDebug).ToString().ToLowerInvariant() + "}");
            Assert.AreEqual(200, status, body);
            Assert.AreEqual(!logDebug, logging.LogDebug, "An unrestricted full key carries the administrator's authority.");

            Assert.AreEqual(401, Http("PUT", "/api/v1/settings/logging", null, "{\"log_debug\":" + logDebug.ToString().ToLowerInvariant() + "}").status);
            Assert.AreEqual(!logDebug, logging.LogDebug);
         }
         finally
         {
            Http("DELETE", "/api/v1/apikeys/" + scopedId);
            Http("DELETE", "/api/v1/apikeys/" + readOnlyId);
            Http("DELETE", "/api/v1/apikeys/" + fullId);
         }
      }

      [Test]
      [Description("A change over the API is seen by the running server: the SMTP banner carries the new host name, a MAIL FROM declaring more than the new size limit is refused with 552, and IMAP advertises the new public namespace.")]
      public void ChangesAreSeenByTheRunningServer()
      {
         (int status, string body) = Http("PUT", "/api/v1/settings",
            "{\"host_name\":\"rest-settings.test\",\"max_message_size_kb\":1,\"imap_public_folder_name\":\"RestPublic\"}");
         Assert.AreEqual(200, status, body);

         using (var socket = new TcpConnection())
         {
            Assert.IsTrue(socket.Connect(25));
            string banner = socket.Receive();
            StringAssert.StartsWith("220 rest-settings.test", banner, "The banner names the host the API just set.");

            socket.Send("EHLO example.test\r\n");
            string ehlo = socket.ReadUntil("250 HELP");
            StringAssert.Contains("250", ehlo);

            string response = socket.SendAndReceive("MAIL FROM:<sender@external.example> SIZE=500000\r\n");
            StringAssert.StartsWith("552", response, "A SIZE above the limit the API just set is refused (RFC 1870). Got: " + response);
            socket.Send("QUIT\r\n");
         }

         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "namespace@example.test", "test");
         var imap = new ImapClientSimulator();
         Assert.IsTrue(imap.ConnectAndLogon(account.Address, "test"));
         string namespaces = imap.Send("A1 NAMESPACE");
         StringAssert.Contains("RestPublic", namespaces, "NAMESPACE advertises the public folder name the API just set.");
         imap.Disconnect();
      }

      [Test]
      [Description("GET /api/v1/settings/directories reports the seven directories Settings.Directories reports over COM, and the hMailServer.ini the settings were read from; the group has no PUT.")]
      public void DirectoriesGroupMatchesCom()
      {
         hMailServer.Directories directories = _settings.Directories;

         (int status, string body) = Http("GET", "/api/v1/settings/directories");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"program\":\"" + JsonText(directories.ProgramDirectory) + "\"", body);
         StringAssert.Contains("\"data\":\"" + JsonText(directories.DataDirectory) + "\"", body);
         StringAssert.Contains("\"log\":\"" + JsonText(directories.LogDirectory) + "\"", body);
         StringAssert.Contains("\"event\":\"" + JsonText(directories.EventDirectory) + "\"", body);
         StringAssert.Contains("\"temp\":\"" + JsonText(directories.TempDirectory) + "\"", body);
         StringAssert.Contains("\"database\":\"" + JsonText(directories.DatabaseDirectory) + "\"", body);
         StringAssert.Contains("\"db_scripts\":\"" + JsonText(directories.DBScriptDirectory) + "\"", body);

         // The ini is the one beside the binary, and it exists: the file this
         // fixture's SetUp wrote the listener's port into.
         string bin = Regex.Unescape(Extract(body, "bin"));
         string iniFile = Regex.Unescape(Extract(body, "ini_file"));
         Assert.IsTrue(File.Exists(iniFile), "ini_file names a file that exists: " + iniFile);
         Assert.AreEqual(Paths.Combine(bin, "hMailServer.ini").ToLowerInvariant(), iniFile.ToLowerInvariant());

         (int putStatus, string putBody) = Http("PUT", "/api/v1/settings/directories", "{\"data\":\"C:\\\\elsewhere\"}");
         Assert.AreEqual(404, putStatus, "A directory is chosen at install time; the group has no PUT: " + putBody);
      }

      [Test]
      [Description("The [Settings] section of hMailServer.ini one key at a time: COM reads what the API wrote, the API refuses what SetIniSetting refuses in its words, and deleting removes the line.")]
      public void IniSettingRoundTripsThroughCom()
      {
         string path = "/api/v1/settings/ini/" + IniProbeKey;

         (int absentStatus, string absentBody) = Http("GET", path);
         Assert.AreEqual(200, absentStatus, absentBody);
         StringAssert.Contains("\"present\":false", absentBody, "A key that is not in the file is a setting at its default, not an error.");
         StringAssert.Contains("\"value\":\"\"", absentBody);

         (int putStatus, string putBody) = Http("PUT", path, "{\"value\":\"one two\"}");
         Assert.AreEqual(200, putStatus, putBody);
         StringAssert.Contains("\"name\":\"" + IniProbeKey + "\"", putBody);
         StringAssert.Contains("\"value\":\"one two\"", putBody);
         StringAssert.Contains("\"present\":true", putBody);

         Assert.AreEqual("one two", _settings.GetIniSetting(IniProbeKey), "COM reads what the API wrote.");
         StringAssert.Contains(IniProbeKey, _settings.IniSettingNames);
         Assert.AreEqual("one two", IniFileSetting.Read(IniProbeKey), "The value reached the file itself, not only the database mirror.");

         (int listStatus, string listBody) = Http("GET", "/api/v1/settings/ini");
         Assert.AreEqual(200, listStatus, listBody);
         StringAssert.Contains("\"" + IniProbeKey + "\"", listBody);
         StringAssert.StartsWith("{\"names\":[", listBody);

         // The refusals are SetIniSetting's, and a refused write changes nothing.
         (int typeStatus, string typeBody) = Http("PUT", path, "{\"value\":5}");
         Assert.AreEqual(400, typeStatus, typeBody);
         StringAssert.Contains("value must be a string", typeBody);

         (int lineStatus, string lineBody) = Http("PUT", path, "{\"value\":\"two\\nlines\"}");
         Assert.AreEqual(400, lineStatus, lineBody);
         StringAssert.Contains("contains a line break", lineBody);

         (int longStatus, string longBody) = Http("PUT", path, "{\"value\":\"" + new string('v', 4001) + "\"}");
         Assert.AreEqual(400, longStatus, longBody);
         StringAssert.Contains("longer than 4000 characters", longBody);
         Assert.AreEqual("one two", _settings.GetIniSetting(IniProbeKey), "A refused value changes nothing.");

         foreach (string badName in new[] { "has=equals", "has[bracket", "has]bracket", "%20leadingspace", new string('n', 101) })
         {
            (int nameStatus, string nameBody) = Http("PUT", "/api/v1/settings/ini/" + badName, "{\"value\":\"x\"}");
            if (badName.StartsWith("%"))
            {
               // The path is not decoded: %20leadingspace is a key spelled that
               // way, and the file can hold it. Written and removed again.
               Assert.AreEqual(200, nameStatus, nameBody);
               Assert.AreEqual(200, Http("DELETE", "/api/v1/settings/ini/" + badName).status);
               continue;
            }
            Assert.AreEqual(400, nameStatus, "The name '" + badName + "' cannot be stored and read back as itself: " + nameBody);
            StringAssert.Contains("The setting name is empty, longer than 100 characters", nameBody);
         }

         (int deleteStatus, string deleteBody) = Http("DELETE", path);
         Assert.AreEqual(200, deleteStatus, deleteBody);
         StringAssert.Contains("\"present\":false", deleteBody);
         Assert.AreEqual("", _settings.GetIniSetting(IniProbeKey), "COM sees the key gone.");
         StringAssert.DoesNotContain(IniProbeKey, _settings.IniSettingNames, "The line was removed, not emptied.");

         (int againStatus, string againBody) = Http("DELETE", path);
         Assert.AreEqual(200, againStatus, "Removing a key that is not there is not an error: " + againBody);
      }

      [Test]
      [Description("POST /api/v1/settings/logon-failures/clear forgets the failures the auto-ban counts, as Settings.ClearLogonFailureList does: after it the count starts again at none, so four wrong passwords around a clear ban nothing where three in a row would.")]
      public void LogonFailuresClearStartsTheCountAgain()
      {
         _settings.ClearLogonFailureList();
         _settings.AutoBanOnLogonFailure = true;
         _settings.MaxInvalidLogonAttempts = 3;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 3;

         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "failures@example.test", "test");
         int rangesBefore = _settings.SecurityRanges.Count;

         var imap = new ImapClientSimulator();
         string errorMessage;
         for (int i = 0; i < 2; i++)
         {
            Assert.IsFalse(imap.ConnectAndLogon(account.Address, "wrong", out errorMessage));
            imap.Disconnect();
            StringAssert.DoesNotContain("Too many invalid logon attempts.", errorMessage);
         }

         (int status, string body) = Http("POST", "/api/v1/settings/logon-failures/clear");
         Assert.AreEqual(200, status, body);
         StringAssert.Contains("\"cleared\":true", body);

         // The third failure in a row is the one that bans. These are the third
         // and fourth since the account existed, and the first and second since
         // the clear.
         for (int i = 0; i < 2; i++)
         {
            Assert.IsFalse(imap.ConnectAndLogon(account.Address, "wrong", out errorMessage));
            imap.Disconnect();
            StringAssert.DoesNotContain("Too many invalid logon attempts.", errorMessage, "The count started again at none after the clear.");
         }

         Assert.IsTrue(imap.ConnectAndLogon(account.Address, "test"), "The right password still logs on: nothing was banned.");
         imap.Disconnect();
         Assert.AreEqual(rangesBefore, _settings.SecurityRanges.Count, "No auto-ban range was added.");
      }

      // A path as JSON shows it: the backslashes doubled.
      private static string JsonText(string path)
      {
         return path.Replace("\\", "\\\\");
      }

      private static (string id, string key) CreateKey(string label, string scope, string domains)
      {
         string body = "{\"label\":\"" + label + "\",\"scope\":\"" + scope + "\"" +
                       (domains == null ? "" : ",\"domains\":\"" + domains + "\"") + "}";
         (int status, string created) = Http("POST", "/api/v1/apikeys", body);
         Assert.AreEqual(201, status, "POST /api/v1/apikeys must create a key. Body: " + created);
         return (Extract(created, "id"), Extract(created, "key"));
      }

      // The string value of a top-level JSON property, enough for the bodies this API returns.
      private static string Extract(string json, string key)
      {
         Match match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
         Assert.IsTrue(match.Success, "No '" + key + "' in: " + json);
         return match.Groups[1].Value;
      }

      private static (int status, string body) Http(string method, string path, string requestBody = null)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));
         return Http(method, path, "Basic " + credentials, requestBody);
      }

      private static (int status, string body) Bearer(string method, string path, string token, string requestBody = null)
      {
         return Http(method, path, "Bearer " + token, requestBody);
      }

      // Issues one HTTP/1.0 request against the REST listener and returns the
      // parsed status code and body. authorization is the complete header value
      // or null to send none. The connect is retried briefly to absorb the
      // listener bind race right after a reinitialize.
      private static (int status, string body) Http(string method, string path, string authorization, string requestBody)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", RestPort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }

            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var headers = new StringBuilder();
               headers.Append(method + " " + path + " HTTP/1.0\r\n");
               headers.Append("Host: 127.0.0.1\r\n");
               if (authorization != null)
                  headers.Append("Authorization: " + authorization + "\r\n");

               byte[] bodyBytes = requestBody == null ? new byte[0] : Encoding.UTF8.GetBytes(requestBody);
               if (requestBody != null)
               {
                  headers.Append("Content-Type: application/json\r\n");
                  headers.Append("Content-Length: " + bodyBytes.Length + "\r\n");
               }

               headers.Append("Connection: close\r\n\r\n");

               byte[] headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
               stream.Write(headerBytes, 0, headerBytes.Length);
               if (bodyBytes.Length > 0)
                  stream.Write(bodyBytes, 0, bodyBytes.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());

               int statusCode = 0;
               string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out statusCode);
               }

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string body = separator >= 0 ? raw.Substring(separator + 4) : "";

               return (statusCode, body);
            }
         }
      }
   }
}
