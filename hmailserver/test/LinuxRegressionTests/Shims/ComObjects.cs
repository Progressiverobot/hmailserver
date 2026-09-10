// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using RegressionTests.Shared;

// The hMailServer namespace, as the fixtures compile against it. On Windows these
// are the COM interop types generated from hMailServer.exe's type library; here they
// are the members the linked fixtures actually touch, and nothing more, each one
// either backed by the REST API or ending in NotOnThisServer.Ignore with the reason.
// A member that is missing from here is a fixture that was not meant to be linked.
namespace hMailServer
{
   public enum eConnectionSecurity
   {
      eCSNone = 0,
      eCSTLS = 1,
      eCSSTARTTLSOptional = 2,
      eCSSTARTTLSRequired = 3
   }

   public class Application
   {
      public Settings Settings { get; } = new Settings();
      public Utilities Utilities { get; } = new Utilities();
      public Status Status { get; } = new Status();
      public Domains Domains { get; } = new Domains();
      public Rules Rules { get; } = new Rules();

      /// <summary>
      ///    POST /api/v1/server/reinitialize: every service stopped, the configuration
      ///    reloaded and the services started again in the same process, which is what
      ///    the COM Reinitialize does. The wait for the listener to come back is
      ///    SslSetup.Reinitialize's, because the answer arrives before the restart.
      /// </summary>
      public void Reinitialize()
      {
         if (!ServerApi.HasRoute("/api/v1/server/reinitialize", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoReinitialize);

         RegressionTests.SSL.SslSetup.Reinitialize();
      }

      /// <summary>
      ///    What the COM SubmitEMail does - ask the delivery queue to run now - over
      ///    POST /api/v1/queue/{id}/retry for every message in the queue, which is
      ///    what TestSetup.SendMessagesInQueue already stands on.
      /// </summary>
      public void SubmitEMail()
      {
         RegressionTests.Shared.TestSetup.SendMessagesInQueue();
      }

      public void Start()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoServiceControl);
      }

      public void Stop()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoServiceControl);
      }

      public bool Authenticate(string user, string password)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoComAuthenticate);
         return false;
      }

      public Database Database
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDatabaseObject);
            return null;
         }
      }

      public Diagnostics Diagnostics
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDiagnostics);
            return null;
         }
      }

      public BackupManager BackupManager
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return null;
         }
      }

      public GlobalObjects GlobalObjects
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
            return null;
         }
      }

      public bool AuthenticateWithCode(string user, string password, string code)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAdministratorTotp);
         return false;
      }

      public bool AdministratorTOTPEnabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAdministratorTotp);
            return false;
         }
      }
   }

   /// <summary>Application.GlobalObjects, the script-visible object bag.</summary>
   public class GlobalObjects
   {
      public int Count => 0;

      public void Clear()
      {
      }

      public object Add()
      {
         return null;
      }

      public object get_ItemByName(string name)
      {
         return null;
      }
   }

   public enum eDBtype
   {
      hDBTypeUnknown = 0,
      hDBTypeMSSQL = 1,
      hDBTypeMySQL = 2,
      hDBTypePostgreSQL = 3,
      hDBTypeMSSQLCE = 4
   }

   /// <summary>Application.Database, which no REST route reports.</summary>
   public class Database
   {
      public eDBtype DatabaseType => eDBtype.hDBTypeUnknown;

      public void ExecuteSQL(string sql)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDatabaseObject);
      }

      public string ExecuteSQLWithReturn(string sql)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDatabaseObject);
         return null;
      }

      public int CurrentVersion
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDatabaseObject);
            return 0;
         }
      }

      public void PerformMaintenance(eMaintenanceOperation operation)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDatabaseObject);
      }
   }

   /// <summary>Application.BackupManager, which no REST route offers.</summary>
   public class BackupManager
   {
      public void StartBackup()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
      }

      public void StartRestore(string file)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
      }

      public bool InProgress
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return false;
         }
      }
   }

   /// <summary>Application.Diagnostics, which no REST route offers.</summary>
   public class Diagnostics
   {
      public bool AssertionsEnabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDiagnostics);
            return false;
         }
      }

      public object PerformTests()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDiagnostics);
         return null;
      }

      public void TriggerAssertion(int which = 0)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDiagnostics);
      }

      public DateTime AcmeRenewalTime
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDiagnostics);
            return DateTime.MinValue;
         }
      }

      public string DnssecChainStatus
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDiagnostics);
            return null;
         }
      }
   }

   public class Result
   {
      public string Message { get; set; }
      public bool Success { get; set; }
   }

   public class Status
   {
      public int ThreadID
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoStatusThreadId);
            return 0;
         }
      }

      /// <summary>
      ///    GET /api/v1/status reports sessions.smtp, sessions.imap and sessions.pop3:
      ///    the same three counters the COM SessionCount answers for the server
      ///    session types. The client session types - an outbound SMTP or POP3
      ///    connection the server itself makes - are not among them.
      /// </summary>
      public int get_SessionCount(eSessionType sessionType)
      {
         string name;
         switch (sessionType)
         {
            case eSessionType.eSTSMTP: name = "smtp"; break;
            case eSessionType.eSTIMAP: name = "imap"; break;
            case eSessionType.eSTPOP3: name = "pop3"; break;
            default:
               NotOnThisServer.Ignore(NotOnThisServer.NoClientSessionCount);
               return 0;
         }

         var answer = ServerApi.Get("/api/v1/status").Expect(200, "GET /api/v1/status");

         JsonElement sessions;
         if (!answer.Json.HasValue || !answer.Json.Value.TryGetProperty("sessions", out sessions))
            return 0;

         return (int) ServerApi.LongOf(sessions, name);
      }

      public DateTime StartTime
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoServerStartTime);
            return DateTime.MinValue;
         }
      }

      public int UndeliveredMessages
      {
         get { return RegressionTests.Shared.TestSetup.GetNumberOfMessagesInDeliveryQueue(); }
      }

      public string CheckForUpdate()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUpdateObject);
         return null;
      }

      public string DownloadUpdate()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUpdateObject);
         return null;
      }

      public string UpdateState
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoUpdateObject);
            return null;
         }
      }

      public string UpdateLastError
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoUpdateObject);
            return null;
         }
      }

      public string InstallUpdate()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUpdateObject);
         return null;
      }

      public eServerState State
      {
         get
         {
            var answer = ServerApi.Get("/api/v1/status").Expect(200, "GET /api/v1/status");
            return (eServerState) ServerApi.LongOf(answer.Json.Value, "state", (long) eServerState.hStateUnknown);
         }
      }
   }

   /// <summary>
   ///    Server settings. GET /api/v1/settings is a read-only snapshot of a few of
   ///    them and none of the ones these fixtures read, so every member here is a
   ///    write the API cannot make or a read it cannot serve.
   /// </summary>
   // A settings group over its REST resource: GET reads the whole group, PUT
   // writes one key. A member that the server's document does not carry, or a
   // server with no PUT, skips the test with the reason that names the gap.
   internal static class SettingsApi
   {
      public const string Server = "/api/v1/settings";
      public const string AntiSpam = "/api/v1/settings/antispam";
      public const string Logging = "/api/v1/settings/logging";

      public static JsonElement Read(string group, string key)
      {
         var answer = ServerApi.Get(group).Expect(200, "GET " + group);
         JsonElement value;
         if (!answer.Json.HasValue || !answer.Json.Value.TryGetProperty(key, out value))
            NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite + " (" + key + " is not in " + group + ")");
         return answer.Json.Value.GetProperty(key);
      }

      public static string GetString(string group, string key)
      {
         return Read(group, key).GetString();
      }

      public static bool GetBool(string group, string key)
      {
         return Read(group, key).GetBoolean();
      }

      public static int GetInt(string group, string key)
      {
         return Read(group, key).GetInt32();
      }

      public static void Put(string group, string key, string jsonValue)
      {
         if (!ServerApi.HasSettingsWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoSettingsWrite);
         ServerApi.Put(group, "{" + ServerApi.Quote(key) + ":" + jsonValue + "}").Expect(200, "PUT " + group + " " + key);
      }

      public static void Put(string group, string key, string value, bool asString)
      {
         Put(group, key, asString ? ServerApi.Quote(value) : value);
      }

      public static void Put(string group, string key, bool value)
      {
         Put(group, key, value ? "true" : "false");
      }

      public static void Put(string group, string key, int value)
      {
         Put(group, key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
      }
   }

   public class Settings
   {
      public AntiSpam AntiSpam { get; } = new AntiSpam();
      public Logging Logging { get; } = new Logging();
      public Directories Directories { get; } = new Directories();
      public TCPIPPorts TCPIPPorts { get; } = new TCPIPPorts();
      public SecurityRanges SecurityRanges { get; } = new SecurityRanges();
      public SSLCertificates SSLCertificates { get; } = new SSLCertificates();
      public Routes Routes { get; } = new Routes();
      public Scripting Scripting { get; } = new Scripting();
      public PublicFolders PublicFolders { get; } = new PublicFolders();
      public Backup Backup { get; } = new Backup();
      public Cache Cache { get; } = new Cache();
      public AntiVirus AntiVirus { get; } = new AntiVirus();
      public Groups Groups { get; } = new Groups();
      public ServerMessages ServerMessages { get; } = new ServerMessages();
      public MessageIndexing MessageIndexing { get; } = new MessageIndexing();
      public IncomingRelays IncomingRelays { get; } = new IncomingRelays();

      // ---- The keys GET/PUT /api/v1/settings carries, under their COM names ----

      private static string Str(string key)
      {
         return SettingsApi.GetString(SettingsApi.Server, key);
      }

      private static bool Flag(string key)
      {
         return SettingsApi.GetBool(SettingsApi.Server, key);
      }

      private static int Num(string key)
      {
         return SettingsApi.GetInt(SettingsApi.Server, key);
      }

      public string HostName
      {
         get { return Str("host_name"); }
         set { SettingsApi.Put(SettingsApi.Server, "host_name", value, true); }
      }

      public string DefaultDomain
      {
         get { return Str("default_domain"); }
         set { SettingsApi.Put(SettingsApi.Server, "default_domain", value, true); }
      }

      public string MirrorEMailAddress
      {
         get { return Str("mirror_email_address"); }
         set { SettingsApi.Put(SettingsApi.Server, "mirror_email_address", value, true); }
      }

      public int MaxMessageSize
      {
         get { return Num("max_message_size_kb"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_message_size_kb", value); }
      }

      public int MaxSMTPConnections
      {
         get { return Num("max_smtp_connections"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_smtp_connections", value); }
      }

      public int MaxIMAPConnections
      {
         get { return Num("max_imap_connections"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_imap_connections", value); }
      }

      public int MaxPOP3Connections
      {
         get { return Num("max_pop3_connections"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_pop3_connections", value); }
      }

      public int MaxDeliveryThreads
      {
         get { return Num("max_delivery_threads"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_delivery_threads", value); }
      }

      public int MaxAsynchronousThreads
      {
         get { return Num("max_asynchronous_threads"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_asynchronous_threads", value); }
      }

      public int TCPIPThreads
      {
         get { return Num("tcpip_threads"); }
         set { SettingsApi.Put(SettingsApi.Server, "tcpip_threads", value); }
      }

      public int WorkerThreadPriority
      {
         get { return Num("worker_thread_priority"); }
         set { SettingsApi.Put(SettingsApi.Server, "worker_thread_priority", value); }
      }

      public bool ServiceSMTP
      {
         get { return Flag("service_smtp"); }
         set { SettingsApi.Put(SettingsApi.Server, "service_smtp", value); }
      }

      public bool ServicePOP3
      {
         get { return Flag("service_pop3"); }
         set { SettingsApi.Put(SettingsApi.Server, "service_pop3", value); }
      }

      public bool ServiceIMAP
      {
         get { return Flag("service_imap"); }
         set { SettingsApi.Put(SettingsApi.Server, "service_imap", value); }
      }

      public string WelcomeSMTP
      {
         get { return Str("welcome_smtp"); }
         set { SettingsApi.Put(SettingsApi.Server, "welcome_smtp", value, true); }
      }

      public string WelcomePOP3
      {
         get { return Str("welcome_pop3"); }
         set { SettingsApi.Put(SettingsApi.Server, "welcome_pop3", value, true); }
      }

      public string WelcomeIMAP
      {
         get { return Str("welcome_imap"); }
         set { SettingsApi.Put(SettingsApi.Server, "welcome_imap", value, true); }
      }

      public string SMTPRelayer
      {
         get { return Str("smtp_relayer"); }
         set { SettingsApi.Put(SettingsApi.Server, "smtp_relayer", value, true); }
      }

      public int SMTPRelayerPort
      {
         get { return Num("smtp_relayer_port"); }
         set { SettingsApi.Put(SettingsApi.Server, "smtp_relayer_port", value); }
      }

      public eConnectionSecurity SMTPRelayerConnectionSecurity
      {
         get { return TCPIPPort.SecurityOf(Str("smtp_relayer_connection_security")); }
         set
         {
            SettingsApi.Put(SettingsApi.Server, "smtp_relayer_connection_security",
               RegressionTests.Shared.TestSetup.ConnectionSecurityName(value), true);
         }
      }

      public bool SMTPRelayerRequiresAuthentication
      {
         get { return Flag("smtp_relayer_requires_authentication"); }
         set { SettingsApi.Put(SettingsApi.Server, "smtp_relayer_requires_authentication", value); }
      }

      public string SMTPRelayerUsername
      {
         get { return Str("smtp_relayer_username"); }
         set { SettingsApi.Put(SettingsApi.Server, "smtp_relayer_username", value, true); }
      }

      public eConnectionSecurity SMTPConnectionSecurity
      {
         get { return TCPIPPort.SecurityOf(Str("smtp_connection_security")); }
         set
         {
            SettingsApi.Put(SettingsApi.Server, "smtp_connection_security",
               RegressionTests.Shared.TestSetup.ConnectionSecurityName(value), true);
         }
      }

      public int SMTPNoOfTries
      {
         get { return Num("smtp_no_of_tries"); }
         set { SettingsApi.Put(SettingsApi.Server, "smtp_no_of_tries", value); }
      }

      public int SMTPMinutesBetweenTry
      {
         get { return Num("smtp_minutes_between_try"); }
         set { SettingsApi.Put(SettingsApi.Server, "smtp_minutes_between_try", value); }
      }

      public string SMTPDeliveryBindToIP
      {
         get { return Str("smtp_delivery_bind_to_ip"); }
         set { SettingsApi.Put(SettingsApi.Server, "smtp_delivery_bind_to_ip", value, true); }
      }

      public int MaxSMTPRecipientsInBatch
      {
         get { return Num("max_smtp_recipients_in_batch"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_smtp_recipients_in_batch", value); }
      }

      public int MaxNumberOfMXHosts
      {
         get { return Num("max_number_of_mx_hosts"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_number_of_mx_hosts", value); }
      }

      public bool AllowSMTPAuthPlain
      {
         get { return Flag("allow_smtp_auth_plain"); }
         set { SettingsApi.Put(SettingsApi.Server, "allow_smtp_auth_plain", value); }
      }

      public bool DenyMailFromNull
      {
         get { return Flag("deny_mail_from_null"); }
         set { SettingsApi.Put(SettingsApi.Server, "deny_mail_from_null", value); }
      }

      public bool AllowIncorrectLineEndings
      {
         get { return Flag("allow_incorrect_line_endings"); }
         set { SettingsApi.Put(SettingsApi.Server, "allow_incorrect_line_endings", value); }
      }

      public bool AddDeliveredToHeader
      {
         get { return Flag("add_delivered_to_header"); }
         set { SettingsApi.Put(SettingsApi.Server, "add_delivered_to_header", value); }
      }

      public bool DisconnectInvalidClients
      {
         get { return Flag("disconnect_invalid_clients"); }
         set { SettingsApi.Put(SettingsApi.Server, "disconnect_invalid_clients", value); }
      }

      public int MaxNumberOfInvalidCommands
      {
         get { return Num("max_number_of_invalid_commands"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_number_of_invalid_commands", value); }
      }

      public int RuleLoopLimit
      {
         get { return Num("rule_loop_limit"); }
         set { SettingsApi.Put(SettingsApi.Server, "rule_loop_limit", value); }
      }

      public string IMAPHierarchyDelimiter
      {
         get { return Str("imap_hierarchy_delimiter"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_hierarchy_delimiter", value, true); }
      }

      public string IMAPPublicFolderName
      {
         get { return Str("imap_public_folder_name"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_public_folder_name", value, true); }
      }

      public string IMAPMasterUser
      {
         get { return Str("imap_master_user"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_master_user", value, true); }
      }

      public bool IMAPSASLPlainEnabled
      {
         get { return Flag("imap_sasl_plain_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_sasl_plain_enabled", value); }
      }

      public bool IMAPSASLInitialResponseEnabled
      {
         get { return Flag("imap_sasl_initial_response_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_sasl_initial_response_enabled", value); }
      }

      public bool IMAPSortEnabled
      {
         get { return Flag("imap_sort_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_sort_enabled", value); }
      }

      public bool IMAPQuotaEnabled
      {
         get { return Flag("imap_quota_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_quota_enabled", value); }
      }

      public bool IMAPIdleEnabled
      {
         get { return Flag("imap_idle_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_idle_enabled", value); }
      }

      public bool IMAPACLEnabled
      {
         get { return Flag("imap_acl_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "imap_acl_enabled", value); }
      }

      public bool AutoBanOnLogonFailure
      {
         get { return Flag("auto_ban_on_logon_failure"); }
         set { SettingsApi.Put(SettingsApi.Server, "auto_ban_on_logon_failure", value); }
      }

      public int MaxInvalidLogonAttempts
      {
         get { return Num("max_invalid_logon_attempts"); }
         set { SettingsApi.Put(SettingsApi.Server, "max_invalid_logon_attempts", value); }
      }

      public int MaxInvalidLogonAttemptsWithin
      {
         get { return Num("minutes_before_reset"); }
         set { SettingsApi.Put(SettingsApi.Server, "minutes_before_reset", value); }
      }

      public int AutoBanMinutes
      {
         get { return Num("minutes_to_ban"); }
         set { SettingsApi.Put(SettingsApi.Server, "minutes_to_ban", value); }
      }

      public bool TlsOptionPreferServerCiphersEnabled
      {
         get { return Flag("tls_prefer_server_ciphers"); }
         set { SettingsApi.Put(SettingsApi.Server, "tls_prefer_server_ciphers", value); }
      }

      public bool TlsOptionPrioritizeChaChaEnabled
      {
         get { return Flag("tls_prioritize_chacha"); }
         set { SettingsApi.Put(SettingsApi.Server, "tls_prioritize_chacha", value); }
      }

      public string SslCipherList
      {
         get { return Str("ssl_cipher_list"); }
         set { SettingsApi.Put(SettingsApi.Server, "ssl_cipher_list", value, true); }
      }

      public bool TlsVersion10Enabled
      {
         get { return Flag("tls_version_10_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "tls_version_10_enabled", value); }
      }

      public bool TlsVersion11Enabled
      {
         get { return Flag("tls_version_11_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "tls_version_11_enabled", value); }
      }

      public bool TlsVersion12Enabled
      {
         get { return Flag("tls_version_12_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "tls_version_12_enabled", value); }
      }

      public bool TlsVersion13Enabled
      {
         get { return Flag("tls_version_13_enabled"); }
         set { SettingsApi.Put(SettingsApi.Server, "tls_version_13_enabled", value); }
      }

      public bool VerifyRemoteSslCertificate
      {
         get { return Flag("verify_remote_ssl_certificate"); }
         set { SettingsApi.Put(SettingsApi.Server, "verify_remote_ssl_certificate", value); }
      }

      public bool IPv6PreferredEnabled
      {
         get { return Flag("ipv6_preferred"); }
         set { SettingsApi.Put(SettingsApi.Server, "ipv6_preferred", value); }
      }

      public bool RewriteEnvelopeFromWhenForwarding
      {
         get { return Flag("rewrite_envelope_from_when_forwarding"); }
         set { SettingsApi.Put(SettingsApi.Server, "rewrite_envelope_from_when_forwarding", value); }
      }

      public bool CreateDefaultSpecialUseFoldersEnabled
      {
         get { return Flag("create_default_special_use_folders"); }
         set { SettingsApi.Put(SettingsApi.Server, "create_default_special_use_folders", value); }
      }

      // ---- What no route offers ----

      public void SetAdministratorPassword(string password)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAdministratorPassword);
      }

      public void SetSMTPRelayerPassword(string password)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoRelayerPassword);
      }

      public void ClearLogonFailureList()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoLogonFailureList);
      }

      public string GetIniSetting(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoIniSettings);
         return null;
      }

      public void SetIniSetting(string name, string value)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoIniSettings);
      }

      public void DeleteIniSetting(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoIniSettings);
      }

      public string IniSettingNames
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoIniSettings);
            return null;
         }
      }

      public int CrashSimulationMode
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoCrashSimulation);
            return 0;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCrashSimulation); }
      }

      public string PublicFolderDiskName
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoServerDirectories);
            return null;
         }
      }

      public void DisableAdministratorTOTP()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAdministratorTotp);
      }

      public string EnrolAdministratorTOTP()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAdministratorTotp);
         return null;
      }

      public string TestLdapDirectory(string a = null, string b = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDirectorySync);
         return null;
      }

      public string PreviewDirectorySync(string a = null, string b = null, string c = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDirectorySync);
         return null;
      }

      public string ApplyDirectorySync(string a = null, string b = null, string c = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDirectorySync);
         return null;
      }

      public string UserInterfaceLanguage
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoUserInterfaceLanguage);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoUserInterfaceLanguage); }
      }
   }

   public enum eLogOutputFormat
   {
      hLogFormatDefault = 1,
      hLogFormatCSA = 2
   }

   public enum eLogDevice
   {
      hLogDeviceUnknown = 0,
      hLogDeviceSQL = 1,
      hLogDeviceFile = 2
   }

   /// <summary>
   ///    GET/PUT /api/v1/settings/logging, which carries the switches and, for
   ///    reading only, the log directory and the four current file names.
   /// </summary>
   public class Logging
   {
      public bool Enabled
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "enabled"); }
         set { SettingsApi.Put(SettingsApi.Logging, "enabled", value); }
      }

      public bool LogApplication
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "log_application"); }
         set { SettingsApi.Put(SettingsApi.Logging, "log_application", value); }
      }

      public bool LogSMTP
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "log_smtp"); }
         set { SettingsApi.Put(SettingsApi.Logging, "log_smtp", value); }
      }

      public bool LogPOP3
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "log_pop3"); }
         set { SettingsApi.Put(SettingsApi.Logging, "log_pop3", value); }
      }

      public bool LogIMAP
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "log_imap"); }
         set { SettingsApi.Put(SettingsApi.Logging, "log_imap", value); }
      }

      public bool LogTCPIP
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "log_tcpip"); }
         set { SettingsApi.Put(SettingsApi.Logging, "log_tcpip", value); }
      }

      public bool LogDebug
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "log_debug"); }
         set { SettingsApi.Put(SettingsApi.Logging, "log_debug", value); }
      }

      public bool AWStatsEnabled
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "log_awstats"); }
         set { SettingsApi.Put(SettingsApi.Logging, "log_awstats", value); }
      }

      public bool KeepFilesOpen
      {
         get { return SettingsApi.GetBool(SettingsApi.Logging, "keep_files_open"); }
         set { SettingsApi.Put(SettingsApi.Logging, "keep_files_open", value); }
      }

      public eLogOutputFormat LogFormat
      {
         get
         {
            return SettingsApi.GetString(SettingsApi.Logging, "log_format") == "ncsa"
               ? eLogOutputFormat.hLogFormatCSA
               : eLogOutputFormat.hLogFormatDefault;
         }
         set
         {
            SettingsApi.Put(SettingsApi.Logging, "log_format",
               value == eLogOutputFormat.hLogFormatCSA ? "ncsa" : "default", true);
         }
      }

      public string Directory => SettingsApi.GetString(SettingsApi.Logging, "directory");

      public string CurrentDefaultLog => SettingsApi.GetString(SettingsApi.Logging, "current_default_log");

      public string CurrentErrorLog => SettingsApi.GetString(SettingsApi.Logging, "current_error_log");

      public string CurrentEventLog => SettingsApi.GetString(SettingsApi.Logging, "current_event_log");

      public string CurrentAwstatsLog => SettingsApi.GetString(SettingsApi.Logging, "current_awstats_log");

      // The live log is a COM callback: the Control Panel subscribes and the server
      // pushes. Nothing in HTTP stands for it.
      public void EnableLiveLogging(bool enable)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoLiveLog);
      }

      public bool LiveLoggingEnabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoLiveLog);
            return false;
         }
      }

      public string LiveLog
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoLiveLog);
            return null;
         }
      }

      public bool MaskPasswordsInLog
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoSettingsKey("mask_passwords_in_log", "logging"));
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsKey("mask_passwords_in_log", "logging")); }
      }

      public eLogDevice Device
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoLogDeviceWrite);
            return eLogDevice.hLogDeviceFile;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoLogDeviceWrite); }
      }
   }

   /// <summary>
   ///    The server's own directories. Only the log directory has a route
   ///    (GET /api/v1/settings/logging reports it); the program, data and event
   ///    directories are reported by nothing, and the fixtures that read them - to
   ///    open hMailServer.ini, or to look at the message store - skip.
   /// </summary>
   public class Directories
   {
      public string LogDirectory => SettingsApi.GetString(SettingsApi.Logging, "directory");

      public string ProgramDirectory
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoServerDirectories);
            return null;
         }
      }

      public string DataDirectory
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoServerDirectories);
            return null;
         }
      }

      public string EventDirectory
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoServerDirectories);
            return null;
         }
      }

      public string TempDirectory
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoServerDirectories);
            return null;
         }
      }
   }

   /// <summary>The event-handler scripting, which no REST route configures or runs.</summary>
   public class Scripting
   {
      public bool Enabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoScripting); }
      }

      public string Language
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoScripting); }
      }

      public string CurrentScriptFile
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
            return null;
         }
      }

      public object FileSystemObject
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoScripting); }
      }

      public void Reload()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
      }

      public string CheckSyntax()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
         return null;
      }
   }

   public class PublicFolders
   {
      public IMAPFolder Add(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoPublicFolderWrite);
         return null;
      }

      public IMAPFolder get_ItemByName(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoPublicFolderWrite);
         return null;
      }

      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoPublicFolderWrite);
            return 0;
         }
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoPublicFolderWrite);
      }
   }

   public class Backup
   {
      public string Destination
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings); }
      }

      public string LogFile
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return null;
         }
      }

      public bool BackupMessages
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings); }
      }

      public bool BackupSettings
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings); }
      }

      public bool BackupDomains
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings); }
      }

      public bool CompressDestinationFiles
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoBackupSettings); }
      }
   }

   public class Cache
   {
      public bool Enabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl); }
      }

      public int DomainCacheSizeKb
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl);
            return 0;
         }
      }

      public int DomainCacheMaxSizeKb
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl);
            return 0;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl); }
      }

      public int AccountCacheSizeKb
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl);
            return 0;
         }
      }

      public int AccountCacheMaxSizeKb
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl);
            return 0;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl); }
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl);
      }
   }

   public class AntiVirus
   {
      public bool EnableAttachmentBlocking
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings); }
      }

      public BlockedAttachments BlockedAttachments { get; } = new BlockedAttachments();

      public eAntivirusAction Action
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
            return eAntivirusAction.hDeleteEmail;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings); }
      }

      public int ScannerFailurePolicy
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
            return 0;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings); }
      }

      public bool CustomScannerEnabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings); }
      }

      public string TestClamAVScanner(string host = null, int port = 0)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
         return null;
      }

      public bool ClamAVEnabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings); }
      }
   }

   public class Groups
   {
      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
            return 0;
         }
      }

      public int Length
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
            return 0;
         }
      }

      public Group Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
         return null;
      }

      public Group get_ItemByName(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
         return null;
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
      }
   }

   public class ServerMessages
   {
      public ServerMessage get_ItemByName(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoServerMessages);
         return null;
      }
   }

   public class ServerMessage
   {
      public string Name { get; set; }
      public string Text { get; set; }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoServerMessages);
      }
   }

   public class MessageIndexing
   {
      public bool Enabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageIndexing);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageIndexing); }
      }

      public void Index()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageIndexing);
      }

      public long TotalIndexedCount
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageIndexing);
            return 0;
         }
      }

      public long TotalMessageCount
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageIndexing);
            return 0;
         }
      }
   }

   public class IncomingRelays
   {
      public object Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoIncomingRelays);
         return null;
      }

      public int Count
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoIncomingRelays);
            return 0;
         }
      }
   }

   public class AntiSpam
   {
      public int SpamDeleteThreshold
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "spam_delete_threshold"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "spam_delete_threshold", value); }
      }

      public bool DKIMVerificationEnabled
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "dkim_verification_enabled"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "dkim_verification_enabled", value); }
      }

      public int DKIMVerificationFailureScore
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "dkim_verification_failure_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "dkim_verification_failure_score", value); }
      }

      public int SpamMarkThreshold
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "spam_mark_threshold"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "spam_mark_threshold", value); }
      }

      public bool AddHeaderSpam
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "add_header_spam"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "add_header_spam", value); }
      }

      public bool AddHeaderReason
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "add_header_reason"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "add_header_reason", value); }
      }

      public bool DMARCEnabled
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "dmarc_enabled"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "dmarc_enabled", value); }
      }

      public int DMARCFailureScore
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "dmarc_failure_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "dmarc_failure_score", value); }
      }

      public bool ArcFilteringEnabled
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "arc_filtering_enabled"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "arc_filtering_enabled", value); }
      }

      public string ArcTrustedSealers
      {
         get { return SettingsApi.GetString(SettingsApi.AntiSpam, "arc_trusted_sealers"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "arc_trusted_sealers", value, true); }
      }

      public bool PrependSubject
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "prepend_subject"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "prepend_subject", value); }
      }

      public string PrependSubjectText
      {
         get { return SettingsApi.GetString(SettingsApi.AntiSpam, "prepend_subject_text"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "prepend_subject_text", value, true); }
      }

      public bool UseSPF
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "use_spf"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "use_spf", value); }
      }

      public int UseSPFScore
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "use_spf_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "use_spf_score", value); }
      }

      public bool CheckHostInHelo
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "check_host_in_helo"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "check_host_in_helo", value); }
      }

      public int CheckHostInHeloScore
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "check_host_in_helo_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "check_host_in_helo_score", value); }
      }

      public bool UseMXChecks
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "check_mx_records"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "check_mx_records", value); }
      }

      public int UseMXChecksScore
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "check_mx_records_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "check_mx_records_score", value); }
      }

      public bool CheckPTR
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "check_ptr"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "check_ptr", value); }
      }

      public int CheckPTRScore
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "check_ptr_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "check_ptr_score", value); }
      }

      public bool SpamAssassinEnabled
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "spamassassin_enabled"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "spamassassin_enabled", value); }
      }

      public string SpamAssassinHost
      {
         get { return SettingsApi.GetString(SettingsApi.AntiSpam, "spamassassin_host"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "spamassassin_host", value, true); }
      }

      public int SpamAssassinPort
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "spamassassin_port"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "spamassassin_port", value); }
      }

      public int SpamAssassinScore
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "spamassassin_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "spamassassin_score", value); }
      }

      public bool SpamAssassinMergeScore
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "spamassassin_merge_score"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "spamassassin_merge_score", value); }
      }

      public int TarpitCount
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "tarpit_count"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "tarpit_count", value); }
      }

      public int TarpitDelay
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "tarpit_delay"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "tarpit_delay", value); }
      }

      public bool GreyListingEnabled
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "greylisting_enabled"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "greylisting_enabled", value); }
      }

      public int GreyListingInitialDelay
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "greylisting_initial_delay"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "greylisting_initial_delay", value); }
      }

      public int GreyListingInitialDelete
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "greylisting_initial_delete"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "greylisting_initial_delete", value); }
      }

      public int GreyListingFinalDelete
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "greylisting_final_delete"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "greylisting_final_delete", value); }
      }

      public bool BypassGreylistingOnSPFSuccess
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "bypass_greylisting_on_spf_success"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "bypass_greylisting_on_spf_success", value); }
      }

      public bool BypassGreylistingOnMailFromMX
      {
         get { return SettingsApi.GetBool(SettingsApi.AntiSpam, "bypass_greylisting_on_mail_from_mx"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "bypass_greylisting_on_mail_from_mx", value); }
      }

      public int MaximumMessageSize
      {
         get { return SettingsApi.GetInt(SettingsApi.AntiSpam, "maximum_message_size_kb"); }
         set { SettingsApi.Put(SettingsApi.AntiSpam, "maximum_message_size_kb", value); }
      }

      // ---- What no route offers ----

      public void ClearGreyListingTriplets()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoGreylistTriplets);
      }

      public string TestSpamAssassinConnection(string host, int port, bool useSpamAssassinUser = false)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoSpamAssassinProbe);
         return null;
      }

      public string DKIMVerify(string rawMessage)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDkimVerifyCall);
         return null;
      }

      public DNSBlackLists DNSBlackLists
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBlacklistCollections);
            return null;
         }
      }

      public SURBLServers SURBLServers
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBlacklistCollections);
            return null;
         }
      }

      public BlockedSenders BlockedSenders
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBlacklistCollections);
            return null;
         }
      }

      public WhiteListAddresses WhiteListAddresses
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBlacklistCollections);
            return null;
         }
      }

      public GreyListingWhiteAddresses GreyListingWhiteAddresses
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoBlacklistCollections);
            return null;
         }
      }

      public Quarantine Quarantine
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoQuarantineObject);
            return null;
         }
      }
   }

   // The anti-spam list collections: no REST route reads or writes any of them, so
   // the property above stops the test before one of these is touched. They exist
   // only so that a fixture naming their types compiles.
   public class DNSBlackLists
   {
      public DNSBlackList Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public DNSBlackList this[int index] => null;

      public DNSBlackList get_Item(int index)
      {
         return null;
      }

      public DNSBlackList get_ItemByName(string name)
      {
         return null;
      }

      public void Clear()
      {
      }

      public void Refresh()
      {
      }

      public int Count => 0;

      public void DeleteByDBID(long id)
      {
      }
   }

   public class DNSBlackList
   {
      public string DNSHost { get; set; }
      public string RejectMessage { get; set; }
      public string Result { get; set; }
      public int Score { get; set; }
      public bool Active { get; set; }

      public void Save()
      {
      }
   }

   public class SURBLServers
   {
      public SURBLServer Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public SURBLServer this[int index] => null;

      public SURBLServer get_Item(int index)
      {
         return null;
      }

      public SURBLServer get_ItemByName(string name)
      {
         return null;
      }

      public void Clear()
      {
      }

      public void Refresh()
      {
      }

      public int Count => 0;

      public void DeleteByDBID(long id)
      {
      }
   }

   public class SURBLServer
   {
      public long ID { get; set; }
      public string DNSHost { get; set; }
      public string RejectMessage { get; set; }
      public int Score { get; set; }
      public bool Active { get; set; }

      public void Save()
      {
      }
   }

   public class BlockedSenders
   {
      public BlockedSender Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public BlockedSender this[int index] => null;

      public BlockedSender get_Item(int index)
      {
         return null;
      }

      public BlockedSender get_ItemByName(string name)
      {
         return null;
      }

      public void Clear()
      {
      }

      public void Refresh()
      {
      }

      public int Count => 0;

      public void DeleteByDBID(long id)
      {
      }
   }

   public class BlockedSender
   {
      public long ID { get; set; }
      public string Address { get; set; }
      public string Domain { get; set; }
      public string Description { get; set; }
      public int Score { get; set; }
      public bool Active { get; set; }

      public void Save()
      {
      }
   }

   public class WhiteListAddresses
   {
      public WhiteListAddress Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public WhiteListAddress this[int index] => null;

      public WhiteListAddress get_Item(int index)
      {
         return null;
      }

      public WhiteListAddress get_ItemByName(string name)
      {
         return null;
      }

      public void Clear()
      {
      }

      public void Refresh()
      {
      }

      public int Count => 0;

      public void DeleteByDBID(long id)
      {
      }
   }

   public class WhiteListAddress
   {
      public string LowerIPAddress { get; set; }
      public string UpperIPAddress { get; set; }
      public string EmailAddress { get; set; }
      public string Description { get; set; }
      public long ID { get; set; }

      public void Save()
      {
      }
   }

   public class GreyListingWhiteAddresses
   {
      public GreyListingWhiteAddress Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public GreyListingWhiteAddress this[int index] => null;

      public GreyListingWhiteAddress get_Item(int index)
      {
         return null;
      }

      public GreyListingWhiteAddress get_ItemByName(string name)
      {
         return null;
      }

      public void Clear()
      {
      }

      public void Refresh()
      {
      }

      public int Count => 0;

      public void DeleteByDBID(long id)
      {
      }
   }

   public class GreyListingWhiteAddress
   {
      public long ID { get; set; }
      public string IPAddress { get; set; }
      public string Description { get; set; }

      public void Save()
      {
      }
   }

   public class Quarantine
   {
      public int Count => 0;

      public void Clear()
      {
      }

      public void Refresh()
      {
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public QuarantinedMessage this[int index] => null;

      public QuarantinedMessage get_Item(int index)
      {
         return null;
      }

      public void DeleteByDBID(long id)
      {
      }

      public void ReleaseByDBID(long id)
      {
      }
   }

   public class QuarantinedMessage
   {
      public long ID { get; set; }
      public string Sender { get; set; }
      public string Subject { get; set; }
      public string Reason { get; set; }
      public int Score { get; set; }

      public void Release()
      {
      }

      public void Delete()
      {
      }
   }

   /// <summary>
   ///    The Sieve fixtures reach this late-bound - utilities.GetType().InvokeMember(
   ///    "CheckSieveSyntax", ...) - because on Windows they must not depend on a
   ///    regenerated type library. The names and signatures here are therefore load
   ///    bearing: InvokeMember finds them by name.
   ///
   ///    CheckSieveSyntax has a REST equivalent: PUT /api/v1/me/filters puts a script
   ///    through the same validation ManageSieve's PUTSCRIPT uses and answers 400 with
   ///    the reason when it does not parse. The check needs an account to be made as,
   ///    so one is made for the purpose in the test domain; TearDown removes it with
   ///    everything else the test made. EvaluateSieveScript - run a script against a
   ///    raw message and report the action - has no REST equivalent.
   /// </summary>
   public class Utilities
   {
      private const string SyntaxAccountAddress = "sieve-syntax@example.test";
      private const string SyntaxAccountPassword = "sieve-syntax-secret";

      public string CheckSieveSyntax(string script)
      {
         var setup = SingletonProvider<TestSetup>.Instance;

         if (!setup.HasAccount(SyntaxAccountAddress))
            setup.AddAccount(setup.TestDomain, SyntaxAccountAddress, SyntaxAccountPassword);

         var answer = ServerApi.AsAccount(SyntaxAccountAddress, SyntaxAccountPassword, HttpMethod.Put,
            "/api/v1/me/filters", "{\"script\":" + ServerApi.Quote(script) + "}");

         if (answer.Ok)
            return string.Empty;

         if (answer.Status == 400)
            return answer.Error;

         throw new InvalidOperationException("PUT /api/v1/me/filters answered " + answer.Status + ": " + answer.Body);
      }

      public string EvaluateSieveScript(string script, string rawMessage)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoSieveEvaluate);
         return null;
      }

      public string GetMailServer(string address)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMailServerLookup);
         return null;
      }

      public string MD5(string text)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string BlowfishEncrypt(string text)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string BlowfishDecrypt(string text)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public void ImportMessageFromFile(string file, long accountId)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
      }

      public void ImportMessageFromFileWithFolderName(string file, long accountId, string folder)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
      }

      public string RunTestSuite(string name = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string SendDmarcReports(string date = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string SendTlsRptReports(string date = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string SearchArchive(string query, string from = null, string to = null)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string ResolveMXRecords(string domain)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string RunMessageRetention()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string PerformMaintenance(eMaintenanceOperation operation)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public void SampleMetricsNow()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
      }

      public string IsStrongPassword(string password)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }

      public string IsValidEmailAddress(string address)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
         return null;
      }
   }

   /// <summary>
   ///    A domain. PUT /api/v1/domains/{domain} takes active and postmaster and
   ///    nothing else, so those two save; every other property the COM object has is
   ///    remembered as set and Save() then stops the test naming it, rather than
   ///    dropping the change and letting the test assert on a server that never
   ///    received it.
   /// </summary>
   public class Domain : RestBackedObject
   {
      internal bool Unsaved;

      private string _name;
      private string _savedName;
      private bool _active = true;
      private string _postmaster = string.Empty;

      public string Name
      {
         get { return _name; }
         set
         {
            if (_savedName == null)
               _savedName = value;
            _name = value;
         }
      }

      public bool Active
      {
         get { return _active; }
         set { _active = value; }
      }

      public string Postmaster
      {
         get { return _postmaster; }
         set { _postmaster = value; }
      }

      public long ID
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDomainIds);
            return 0;
         }
      }

      public Accounts Accounts => new Accounts(Name);
      public Aliases Aliases => new Aliases(Name);
      public DistributionLists DistributionLists => new DistributionLists(Name);
      public DomainAliases DomainAliases { get; } = new DomainAliases();

      // What the COM object saves and PUT /api/v1/domains/{domain} does not take.
      public bool DKIMSignEnabled { get { Unsupported("DKIMSignEnabled"); return false; } set { Unsupported("DKIMSignEnabled"); } }
      public string DKIMSelector { get { Unsupported("DKIMSelector"); return null; } set { Unsupported("DKIMSelector"); } }
      public string DKIMPrivateKeyFile { get { Unsupported("DKIMPrivateKeyFile"); return null; } set { Unsupported("DKIMPrivateKeyFile"); } }
      public string DKIMSecondarySelector { get { Unsupported("DKIMSecondarySelector"); return null; } set { Unsupported("DKIMSecondarySelector"); } }
      public string DKIMSecondaryPrivateKeyFile { get { Unsupported("DKIMSecondaryPrivateKeyFile"); return null; } set { Unsupported("DKIMSecondaryPrivateKeyFile"); } }
      public int SignatureMethod { get { Unsupported("SignatureMethod"); return 0; } set { Unsupported("SignatureMethod"); } }
      public bool SignatureEnabled { get { Unsupported("SignatureEnabled"); return false; } set { Unsupported("SignatureEnabled"); } }
      public string SignaturePlainText { get { Unsupported("SignaturePlainText"); return null; } set { Unsupported("SignaturePlainText"); } }
      public string SignatureHTML { get { Unsupported("SignatureHTML"); return null; } set { Unsupported("SignatureHTML"); } }
      public bool AddSignaturesToLocalMail { get { Unsupported("AddSignaturesToLocalMail"); return false; } set { Unsupported("AddSignaturesToLocalMail"); } }
      public int MaxMessageSize { get { Unsupported("MaxMessageSize"); return 0; } set { Unsupported("MaxMessageSize"); } }
      public int MaxAccountSize { get { Unsupported("MaxAccountSize"); return 0; } set { Unsupported("MaxAccountSize"); } }
      public int MaxNumberOfAccounts { get { Unsupported("MaxNumberOfAccounts"); return 0; } set { Unsupported("MaxNumberOfAccounts"); } }
      public int MaxNumberOfAliases { get { Unsupported("MaxNumberOfAliases"); return 0; } set { Unsupported("MaxNumberOfAliases"); } }
      public int MaxNumberOfDistributionLists { get { Unsupported("MaxNumberOfDistributionLists"); return 0; } set { Unsupported("MaxNumberOfDistributionLists"); } }
      public int MessageRetentionDays { get { Unsupported("MessageRetentionDays"); return 0; } set { Unsupported("MessageRetentionDays"); } }
      public string RelayHost { get { Unsupported("RelayHost"); return null; } set { Unsupported("RelayHost"); } }
      public int RelayPort { get { Unsupported("RelayPort"); return 0; } set { Unsupported("RelayPort"); } }
      public bool VacationMessageIsOn { get { Unsupported("VacationMessageIsOn"); return false; } set { Unsupported("VacationMessageIsOn"); } }
      public string ADDomainName { get { Unsupported("ADDomainName"); return null; } set { Unsupported("ADDomainName"); } }
      public eDKIMAlgorithm DKIMSigningAlgorithm
      {
         get { Unsupported("DKIMSigningAlgorithm"); return eDKIMAlgorithm.eSHA256; }
         set { Unsupported("DKIMSigningAlgorithm"); }
      }
      public bool AntiSpamEnableGreylisting
      {
         get { Unsupported("AntiSpamEnableGreylisting"); return false; }
         set { Unsupported("AntiSpamEnableGreylisting"); }
      }
      public bool MaxNumberOfAccountsEnabled
      {
         get { Unsupported("MaxNumberOfAccountsEnabled"); return false; }
         set { Unsupported("MaxNumberOfAccountsEnabled"); }
      }
      public bool MaxNumberOfAliasesEnabled
      {
         get { Unsupported("MaxNumberOfAliasesEnabled"); return false; }
         set { Unsupported("MaxNumberOfAliasesEnabled"); }
      }
      public bool MaxNumberOfDistributionListsEnabled
      {
         get { Unsupported("MaxNumberOfDistributionListsEnabled"); return false; }
         set { Unsupported("MaxNumberOfDistributionListsEnabled"); }
      }
      public bool MaxMessageSizeEnabled
      {
         get { Unsupported("MaxMessageSizeEnabled"); return false; }
         set { Unsupported("MaxMessageSizeEnabled"); }
      }
      public bool MaxAccountSizeEnabled
      {
         get { Unsupported("MaxAccountSizeEnabled"); return false; }
         set { Unsupported("MaxAccountSizeEnabled"); }
      }
      public bool RelayRequiresAuthentication
      {
         get { Unsupported("RelayRequiresAuthentication"); return false; }
         set { Unsupported("RelayRequiresAuthentication"); }
      }
      public string VacationSubject
      {
         get { Unsupported("VacationSubject"); return null; }
         set { Unsupported("VacationSubject"); }
      }
      public string VacationMessage
      {
         get { Unsupported("VacationMessage"); return null; }
         set { Unsupported("VacationMessage"); }
      }

      public void DKIMPromoteSecondary()
      {
         Unsupported("DKIMPromoteSecondary");
         SkipIfAnythingUnsupported("PUT /api/v1/domains/{domain}");
      }
      public bool EnableLimitations { get { Unsupported("EnableLimitations"); return false; } set { Unsupported("EnableLimitations"); } }
      public bool PlusAddressingEnabled { get { Unsupported("PlusAddressingEnabled"); return false; } set { Unsupported("PlusAddressingEnabled"); } }
      public string PlusAddressingCharacter { get { Unsupported("PlusAddressingCharacter"); return null; } set { Unsupported("PlusAddressingCharacter"); } }

      public void Save()
      {
         SkipIfAnythingUnsupported("PUT /api/v1/domains/{domain}");

         if (Unsaved)
         {
            if (!ServerApi.HasDomainWriteRoutes)
               NotOnThisServer.Ignore(NotOnThisServer.NoDomainCreate);

            var created = ServerApi.Post("/api/v1/domains",
               "{\"name\":" + ServerApi.Quote(Name) + ",\"active\":" + (Active ? "true" : "false") +
               ",\"postmaster\":" + ServerApi.Quote(Postmaster ?? string.Empty) + "}");

            if (created.Status == 400 || created.Status == 409)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. " + created.Error);

            created.Expect(201, "POST /api/v1/domains " + Name);
            _savedName = Name;
            Unsaved = false;
            return;
         }

         if (!ServerApi.HasRoute("/api/v1/domains/{domain}", "put"))
            NotOnThisServer.Ignore(NotOnThisServer.NoDomainUpdate);

         // The route is addressed by name and says so: "The name cannot be changed
         // here". A fixture that renames a domain is asking for something it does
         // not do, and saying that is better than a 404 on the new name.
         if (!string.Equals(_savedName, Name, StringComparison.OrdinalIgnoreCase))
            NotOnThisServer.Ignore(NotOnThisServer.NoDomainRename);

         var answer = ServerApi.Put("/api/v1/domains/" + Name,
            "{\"active\":" + (Active ? "true" : "false") +
            ",\"postmaster\":" + ServerApi.Quote(Postmaster ?? string.Empty) + "}");

         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         answer.Expect(200, "PUT /api/v1/domains/" + Name);
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/domains/" + Name).Expect(200, "DELETE /api/v1/domains/" + Name);
      }
   }

   public class Alias
   {
      internal bool Unsaved;
      internal string DomainName;

      public string Name { get; set; }
      public string Value { get; set; }
      public bool Active { get; set; }

      public long ID
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAliasIds);
            return 0;
         }
      }

      public void Save()
      {
         if (!Unsaved)
            NotOnThisServer.Ignore(NotOnThisServer.NoAliasUpdate);

         if (!ServerApi.HasAliasWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoAliasCreate);

         var answer = ServerApi.Post("/api/v1/domains/" + DomainName + "/aliases",
            "{\"name\":" + ServerApi.Quote(Name) + ",\"value\":" + ServerApi.Quote(Value) +
            ",\"active\":" + (Active ? "true" : "false") + "}");

         if (answer.Status == 400 || answer.Status == 409)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         answer.Expect(201, "POST /api/v1/domains/" + DomainName + "/aliases " + Name);
         Unsaved = false;
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/aliases/" + Name).Expect(200, "DELETE /api/v1/aliases/" + Name);
      }
   }

   public class DistributionList
   {
      public string Address { get; set; }
      public bool Active { get; set; }

      public long ID
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListIds);
            return 0;
         }
      }

      public eDistributionListMode Mode
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return eDistributionListMode.eLMPublic;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public string RequireSMTPAuth
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public DistributionListRecipients Recipients
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return null;
         }
      }

      public string ModeratorAddress
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public bool RequireSenderAddress
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public bool ModerationEnabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public bool AnnouncementsOnly
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public string SenderAddress
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public string Name
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject); }
      }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoListObject);
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/lists/" + Address).Expect(200, "DELETE /api/v1/lists/" + Address);
      }
   }

   public class DistributionListRecipients
   {
      public int Count => 0;

      public DistributionListRecipient Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public DistributionListRecipient this[int index] => null;

      public DistributionListRecipient get_Item(int index)
      {
         return null;
      }

      public void Clear()
      {
      }

      public void Refresh()
      {
      }

      public void DeleteByDBID(long id)
      {
      }
   }

   public class DistributionListRecipient
   {
      public long ID { get; set; }
      public string RecipientAddress { get; set; }

      public void Save()
      {
      }
   }

   /// <summary>An SMTP route, over /api/v1/routes.</summary>
   public class Route
   {
      internal bool Existing;

      public long ID { get; set; }
      public string DomainName { get; set; }
      public string Description { get; set; }
      public string TargetSMTPHost { get; set; }
      public int TargetSMTPPort { get; set; } = 25;
      public int NumberOfTries { get; set; } = 3;
      public int MinutesBetweenTry { get; set; } = 10;
      public bool AllAddresses { get; set; } = true;
      public bool TreatRecipientAsLocalDomain { get; set; }
      public bool TreatSenderAsLocalDomain { get; set; }
      public bool TreatSecurityAsLocalDomain
      {
         get { return TreatRecipientAsLocalDomain; }
         set { TreatRecipientAsLocalDomain = value; }
      }
      public bool RelayerRequiresAuth { get; set; }
      public string RelayerAuthUsername { get; set; }
      public string RelayerAuthPassword { get; set; }

      public void SetRelayerAuthPassword(string password)
      {
         RelayerAuthPassword = password;
      }
      public eConnectionSecurity ConnectionSecurity { get; set; } = eConnectionSecurity.eCSNone;

      public void Save()
      {
         if (!ServerApi.HasRouteWriteRoutes)
            NotOnThisServer.Ignore(NotOnThisServer.NoRouteCreate);

         var body = "{\"domain_name\":" + ServerApi.Quote(DomainName) +
                    ",\"description\":" + ServerApi.Quote(Description ?? string.Empty) +
                    ",\"target_smtp_host\":" + ServerApi.Quote(TargetSMTPHost ?? string.Empty) +
                    ",\"target_smtp_port\":" + TargetSMTPPort +
                    ",\"number_of_tries\":" + NumberOfTries +
                    ",\"minutes_between_try\":" + MinutesBetweenTry +
                    ",\"all_addresses\":" + (AllAddresses ? "true" : "false") +
                    ",\"treat_recipient_as_local_domain\":" + (TreatRecipientAsLocalDomain ? "true" : "false") +
                    ",\"treat_sender_as_local_domain\":" + (TreatSenderAsLocalDomain ? "true" : "false") +
                    ",\"relayer_requires_authentication\":" + (RelayerRequiresAuth ? "true" : "false") +
                    (RelayerRequiresAuth
                       ? ",\"relayer_auth_username\":" + ServerApi.Quote(RelayerAuthUsername ?? string.Empty) +
                         ",\"relayer_auth_password\":" + ServerApi.Quote(RelayerAuthPassword ?? string.Empty)
                       : string.Empty) +
                    ",\"connection_security\":" +
                    ServerApi.Quote(RegressionTests.Shared.TestSetup.ConnectionSecurityName(ConnectionSecurity)) + "}";

         var answer = Existing
            ? ServerApi.Put("/api/v1/routes/" + ID, body)
            : ServerApi.Post("/api/v1/routes", body);

         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

         if (Existing)
         {
            answer.Expect(200, "PUT /api/v1/routes/" + ID);
            return;
         }

         answer.Expect(201, "POST /api/v1/routes " + DomainName);
         ID = ServerApi.LongOf(answer.Json.Value, "id");
         Existing = true;
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/routes/" + ID).Expect(200, "DELETE /api/v1/routes/" + ID);
      }

      public RouteAddresses Addresses
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoRouteAddresses);
            return null;
         }
      }
   }

   public class RouteAddresses
   {
      public int Count => 0;

      public RouteAddress Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public RouteAddress this[int index] => null;

      public RouteAddress get_Item(int index)
      {
         return null;
      }

      public void Clear()
      {
      }

      public void DeleteByDBID(long id)
      {
      }
   }

   public class RouteAddress
   {
      public long ID { get; set; }
      public string Address { get; set; }

      public void Save()
      {
      }
   }

   public class Account : RestBackedObject
   {
      internal bool Unsaved;
      internal string DomainName;

      /// <summary>
      ///    The /api/v1/me routes authenticate as the account itself, so every member
      ///    that goes to one needs the account's password. An account this run made
      ///    has it; one read back from a listing that the run did not make has none,
      ///    and the test says that rather than being refused 401 by the server.
      /// </summary>
      internal void RequireOwnCredentials(string doing)
      {
         if (string.IsNullOrEmpty(Password))
            NotOnThisServer.Ignore(doing + " through the account's own /api/v1/me routes, and the password of " +
                                   Address + " is not known to this run - the account listing carries no password");
      }

      // PUT /api/v1/accounts/{address} takes any subset and leaves what the body
      // does not name alone, so Save() sends exactly the fields the test assigned.
      // Sending the rest as well would write the shim's own idea of them over the
      // account - which is how a test that switched forwarding off and asserted the
      // address was kept came to fail: the object it saved had been read back from
      // the listing, which carries neither the address nor the names.
      private readonly Dictionary<string, string> _changed = new Dictionary<string, string>();

      // GET /api/v1/domains/{domain}/accounts reports an address and whether it is
      // active, and nothing else - no name, no forwarding, no signature, no admin
      // level. So an account object made from that listing knows those two things
      // and no more, and a fixture that reads one of the others off it is reading a
      // value nobody gave it. Rather than answer with this shim's own default, which
      // would be a test passing or failing on a value the server never said, each
      // field is answered only when the run knows it: because it was just written
      // here, or because it came from a route that reports it.
      private readonly HashSet<string> _known = new HashSet<string>();

      private void Set(string field, string json)
      {
         _changed[field] = json;
         _known.Add(field);
      }

      private void Require(string field, string comName)
      {
         if (!_known.Contains(field))
            NotOnThisServer.Ignore("reads Account." + comName + " of " + Address +
                                   ", and GET /api/v1/domains/{domain}/accounts reports an account's address " +
                                   "and active flag only - no route reports the rest of an account");
      }

      /// <summary>
      ///    Fills in what the account already is, without counting any of it as a
      ///    change: an object handed back by a listing or by a create has these
      ///    values on the server already, and Save() must not write them again.
      /// </summary>
      internal Account Seed(string address, string password, bool active, string domain)
      {
         Address = address;
         _password = password;
         _active = active;
         DomainName = domain;
         _changed.Clear();
         _known.Clear();
         _known.Add("active");
         if (password != null)
            _known.Add("password");
         return this;
      }

      public string Address { get; set; }
      public string Password
      {
         get { return _password; }
         set
         {
            _password = value;
            if (!Unsaved)
               Set("password", ServerApi.Quote(value ?? string.Empty));
         }
      }
      private string _password;

      public bool Active
      {
         get { return _active; }
         set { _active = value; Set("active", value ? "true" : "false"); }
      }
      private bool _active;

      public int MaxSize
      {
         get { Require("max_size_mb", "MaxSize"); return _maxSize; }
         set { _maxSize = value; Set("max_size_mb", value.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
      }
      private int _maxSize;

      public string PersonFirstName
      {
         get { Require("first_name", "PersonFirstName"); return _firstName; }
         set { _firstName = value; Set("first_name", ServerApi.Quote(value ?? string.Empty)); }
      }
      private string _firstName;

      public string PersonLastName
      {
         get { Require("last_name", "PersonLastName"); return _lastName; }
         set { _lastName = value; Set("last_name", ServerApi.Quote(value ?? string.Empty)); }
      }
      private string _lastName;

      public bool ForwardEnabled
      {
         get { Require("forward_enabled", "ForwardEnabled"); return _forwardEnabled; }
         set { _forwardEnabled = value; Set("forward_enabled", value ? "true" : "false"); }
      }
      private bool _forwardEnabled;

      public string ForwardAddress
      {
         get { Require("forward_address", "ForwardAddress"); return _forwardAddress; }
         set { _forwardAddress = value; Set("forward_address", ServerApi.Quote(value ?? string.Empty)); }
      }
      private string _forwardAddress;

      public bool ForwardKeepOriginal
      {
         get { Require("forward_keep_original", "ForwardKeepOriginal"); return _forwardKeepOriginal; }
         set { _forwardKeepOriginal = value; Set("forward_keep_original", value ? "true" : "false"); }
      }
      private bool _forwardKeepOriginal;

      public bool SignatureEnabled
      {
         get { Require("signature_enabled", "SignatureEnabled"); return _signatureEnabled; }
         set { _signatureEnabled = value; Set("signature_enabled", value ? "true" : "false"); }
      }
      private bool _signatureEnabled;

      public string SignaturePlainText
      {
         get { Require("signature_plain_text", "SignaturePlainText"); return _signaturePlain; }
         set { _signaturePlain = value; Set("signature_plain_text", ServerApi.Quote(value ?? string.Empty)); }
      }
      private string _signaturePlain;

      public string SignatureHTML
      {
         get { Require("signature_html", "SignatureHTML"); return _signatureHtml; }
         set { _signatureHtml = value; Set("signature_html", ServerApi.Quote(value ?? string.Empty)); }
      }
      private string _signatureHtml;

      public eAdminLevel AdminLevel
      {
         get { Require("admin_level", "AdminLevel"); return _adminLevel; }
         set { _adminLevel = value; Set("admin_level", ServerApi.Quote(AdminLevelName(value))); }
      }
      private eAdminLevel _adminLevel;

      public long ID
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAccountIds);
            return 0;
         }
      }

      /// <summary>
      ///    The account's folders, read through its own GET /api/v1/me/folders. The
      ///    top of the tree is parent 0, as the route reports it.
      /// </summary>
      public IMAPFolders IMAPFolders => new IMAPFolders(this, IMAPFolders.RootParent);

      public AccountRules Rules { get; } = new AccountRules();

      public FetchAccounts FetchAccounts { get; } = new FetchAccounts();

      public AppPasswords AppPasswords { get; } = new AppPasswords();

      public bool TOTPEnabled
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAccountTotp);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAccountTotp); }
      }

      public string EnrolTOTP()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAccountTotp);
         return null;
      }

      public bool IsAD
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink);
            return false;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink); }
      }

      public string ADUsername
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink); }
      }

      public string ADDomain
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink); }
      }

      public DateTime LastLogonTime
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoAccountLastLogon);
            return DateTime.MinValue;
         }
      }

      // Written by PUT /api/v1/me/vacation, as the account itself.
      private bool _vacationOn;
      private string _vacationSubject = string.Empty;
      private string _vacationMessage = string.Empty;
      private bool _vacationExpires;
      private string _vacationExpiresDate = string.Empty;
      private bool _vacationTouched;

      public bool VacationMessageIsOn
      {
         get { return _vacationOn; }
         set
         {
            _vacationOn = value;
            _vacationTouched = true;
         }
      }

      public string VacationSubject
      {
         get { return _vacationSubject; }
         set
         {
            _vacationSubject = value;
            _vacationTouched = true;
         }
      }

      public string VacationMessage
      {
         get { return _vacationMessage; }
         set
         {
            _vacationMessage = value;
            _vacationTouched = true;
         }
      }

      public bool VacationMessageExpires
      {
         get { return _vacationExpires; }
         set
         {
            _vacationExpires = value;
            _vacationTouched = true;
         }
      }

      public string VacationMessageExpiresDate
      {
         get { return _vacationExpiresDate; }
         set
         {
            _vacationExpiresDate = value;
            _vacationTouched = true;
         }
      }

      public bool VacationMessageAbortSpamFlagged
      {
         get { Unsupported("VacationMessageAbortSpamFlagged"); return false; }
         set { Unsupported("VacationMessageAbortSpamFlagged"); }
      }

      public string VacationMessageBeginDate
      {
         get { Unsupported("VacationMessageBeginDate"); return null; }
         set { Unsupported("VacationMessageBeginDate"); }
      }

      public int MessageRetentionDays { get { Unsupported("MessageRetentionDays"); return 0; } set { Unsupported("MessageRetentionDays"); } }
      public int SpamMarkThreshold { get { Unsupported("SpamMarkThreshold"); return 0; } set { Unsupported("SpamMarkThreshold"); } }
      public int SpamDeleteThreshold { get { Unsupported("SpamDeleteThreshold"); return 0; } set { Unsupported("SpamDeleteThreshold"); } }
      public bool PersonalSpamSettingsEnabled { get { Unsupported("PersonalSpamSettingsEnabled"); return false; } set { Unsupported("PersonalSpamSettingsEnabled"); } }
      public bool AntiSpamEnabled { get { Unsupported("AntiSpamEnabled"); return false; } set { Unsupported("AntiSpamEnabled"); } }
      public bool AntiVirusEnabled { get { Unsupported("AntiVirusEnabled"); return false; } set { Unsupported("AntiVirusEnabled"); } }

      /// <summary>
      ///    PUT /api/v1/accounts/{address} for the fields it takes; a create goes
      ///    through POST /api/v1/domains/{domain}/accounts. The vacation fields, which
      ///    that route does not take, go through the account's own PUT
      ///    /api/v1/me/vacation - the same rows under the same validation.
      /// </summary>
      public void Save()
      {
         SkipIfAnythingUnsupported("PUT /api/v1/accounts/{address}");

         if (Unsaved)
         {
            var domain = DomainName ?? Address.Substring(Address.IndexOf('@') + 1);
            var created = ServerApi.Post("/api/v1/domains/" + domain + "/accounts",
               "{\"address\":" + ServerApi.Quote(Address) + ",\"password\":" + ServerApi.Quote(_password ?? string.Empty) +
               ",\"active\":" + (_active ? "true" : "false") +
               (_maxSize != 0 ? ",\"max_size_mb\":" + _maxSize : string.Empty) + "}");

            if (created.Status == 400)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. " + created.Error);
            if (created.Status == 409)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. The account address is already in use.");

            created.Expect(201, "POST /api/v1/domains/" + domain + "/accounts " + Address);
            RegressionTests.Shared.TestSetup.RememberPassword(Address, _password);
            Unsaved = false;
            _changed.Clear();
         }
         else
         {
            if (!ServerApi.HasAccountUpdateRoute)
               NotOnThisServer.Ignore(NotOnThisServer.NoAccountUpdate);

            if (_changed.Count > 0)
            {
               var body = new System.Text.StringBuilder("{");
               foreach (var field in _changed)
               {
                  if (body.Length > 1)
                     body.Append(',');
                  body.Append(ServerApi.Quote(field.Key)).Append(':').Append(field.Value);
               }
               body.Append('}');

               var answer = ServerApi.Put("/api/v1/accounts/" + Address, body.ToString());

               // 400 is a refusal by the validation the COM Save applies; 409 is the
               // password-reuse history and the directory link. The COM Save reports
               // both as a failure to save, which is what the fixtures assert on.
               if (answer.Status == 400 || answer.Status == 409)
                  throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

               answer.Expect(200, "PUT /api/v1/accounts/" + Address);

               if (_changed.ContainsKey("password"))
                  RegressionTests.Shared.TestSetup.RememberPassword(Address, Password);

               _changed.Clear();
            }
         }

         if (!_vacationTouched)
            return;

         var vacation = ServerApi.AsAccount(Address, Password, HttpMethod.Put, "/api/v1/me/vacation",
            "{\"enabled\":" + (_vacationOn ? "true" : "false") +
            ",\"subject\":" + ServerApi.Quote(_vacationSubject ?? string.Empty) +
            ",\"message\":" + ServerApi.Quote(_vacationMessage ?? string.Empty) +
            ",\"expires\":" + (_vacationExpires ? "true" : "false") +
            (_vacationExpires ? ",\"expires_date\":" + ServerApi.Quote(_vacationExpiresDate ?? string.Empty) : string.Empty) +
            "}");

         if (vacation.Status == 400)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + vacation.Error);

         vacation.Expect(200, "PUT /api/v1/me/vacation as " + Address);

         // Read back out of the answer, which the route documents as "the state
         // as saved". These five were plain backing fields, so a test that set
         // them and read them back was reading its own input and would have read
         // the same thing if the route had stored nothing at all. There is no GET
         // for this state; the PUT's response is the only read there is, and it
         // is a real one.
         if (vacation.Json.HasValue)
         {
            var saved = vacation.Json.Value;

            _vacationOn = ServerApi.FlagOf(saved, "enabled", _vacationOn);
            _vacationSubject = ServerApi.StringOf(saved, "subject") ?? string.Empty;
            _vacationMessage = ServerApi.StringOf(saved, "message") ?? string.Empty;
            _vacationExpires = ServerApi.FlagOf(saved, "expires", _vacationExpires);
            _vacationExpiresDate = ServerApi.StringOf(saved, "expires_date") ?? string.Empty;
         }

         _vacationTouched = false;
      }

      private static string AdminLevelName(eAdminLevel level)
      {
         switch (level)
         {
            case eAdminLevel.hAdminLevelDomainAdmin: return "domain";
            case eAdminLevel.hAdminLevelServerAdmin: return "server";
            default: return "user";
         }
      }

      public void Delete()
      {
         ServerApi.Delete("/api/v1/accounts/" + Address).Expect(200, "DELETE /api/v1/accounts/" + Address);
      }

      public void DeleteMessages()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDeleteMessages);
      }

      public void ExportMessages(string directory)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoExportMessages);
      }

      /// <summary>
      ///    The mailbox's size in bytes, as GET /api/v1/me reports it under
      ///    quota.used_bytes - the same figure the COM property reads from the account
      ///    row, kept by the server as messages arrive and go.
      /// </summary>
      public long Size
      {
         get
         {
            RequireOwnCredentials("reads the mailbox size");

            var answer = ServerApi.AsAccount(Address, Password, HttpMethod.Get, "/api/v1/me")
               .Expect(200, "GET /api/v1/me as " + Address);

            JsonElement quota;
            if (answer.Json.HasValue && answer.Json.Value.TryGetProperty("quota", out quota))
               return ServerApi.LongOf(quota, "used_bytes");

            return 0;
         }
      }

      public Messages Messages => new Messages(this);

      /// <summary>
      ///    The account's active Sieve script, which the Sieve fixtures reach late-bound
      ///    (InvokeMember "SieveScript" with GetProperty / SetProperty). GET and PUT
      ///    /api/v1/me/filters are the same script under the same validation the COM
      ///    property applies; an empty script removes the filter on both.
      /// </summary>
      public string SieveScript
      {
         get
         {
            RequireOwnCredentials("reads the account's Sieve script");

            var answer = ServerApi.AsAccount(Address, Password, HttpMethod.Get, "/api/v1/me/filters")
               .Expect(200, "GET /api/v1/me/filters as " + Address);

            return answer.Json.HasValue ? ServerApi.StringOf(answer.Json.Value, "active") ?? string.Empty : string.Empty;
         }
         set
         {
            RequireOwnCredentials("sets the account's Sieve script");

            var answer = ServerApi.AsAccount(Address, Password, HttpMethod.Put, "/api/v1/me/filters",
               "{\"script\":" + ServerApi.Quote(value) + "}");

            if (answer.Status == 400)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);

            answer.Expect(200, "PUT /api/v1/me/filters as " + Address);
         }
      }
   }

   /// <summary>
   ///    The messages of an account's INBOX, read and deleted through the account's
   ///    own /api/v1/me routes. On Windows Account.Messages is the account's whole
   ///    message table in id order; the fixtures that use it here (API/Messages) put
   ///    everything in the INBOX, and the listing is put in ascending id order to
   ///    match. A delete is ?permanent=1, because the COM DeleteByDBID removes the
   ///    row rather than moving it to Trash.
   /// </summary>
   public class Messages
   {
      private readonly Account _account;
      private readonly long _folderId;

      public Messages(Account account)
      {
         _account = account;
      }

      internal Messages(Account account, long folderId)
      {
         _account = account;
         _folderId = folderId;
      }

      private List<Message> Load()
      {
         _account.RequireOwnCredentials("lists the account's messages");

         var inboxId = _folderId;

         if (inboxId == 0)
         {
            var folders = ServerApi.AsAccount(_account.Address, _account.Password, HttpMethod.Get, "/api/v1/me/folders")
               .Expect(200, "GET /api/v1/me/folders as " + _account.Address);

            foreach (var folder in ServerApi.Array(folders, "folders"))
            {
               if (string.Equals(ServerApi.StringOf(folder, "name"), "INBOX", StringComparison.OrdinalIgnoreCase))
                  inboxId = ServerApi.LongOf(folder, "id");
            }

            if (inboxId == 0)
               throw new InvalidOperationException(_account.Address + " has no INBOX in GET /api/v1/me/folders: " + folders.Body);
         }

         var listing = ServerApi.AsAccount(_account.Address, _account.Password, HttpMethod.Get,
            "/api/v1/me/folders/" + inboxId + "/messages?limit=200").Expect(200, "listing the INBOX of " + _account.Address);

         var result = new List<Message>();
         foreach (var entry in ServerApi.Array(listing, "messages"))
            result.Add(new Message { ID = ServerApi.LongOf(entry, "id"), Account = _account });

         result.Sort((a, b) => a.ID.CompareTo(b.ID));
         return result;
      }

      public int Count => Load().Count;

      public Message this[int index] => Load()[index];

      public Message get_ItemByDBID(long id)
      {
         foreach (var message in Load())
            if (message.ID == id)
               return message;
         return null;
      }

      public Message Add()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
         return null;
      }

      public void Refresh()
      {
      }

      public void Clear()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }

      public void DeleteByDBID(long id)
      {
         _account.RequireOwnCredentials("deletes one of the account's messages");

         ServerApi.AsAccount(_account.Address, _account.Password, HttpMethod.Delete,
            "/api/v1/me/messages/" + id + "?permanent=1").Expect(200, "DELETE /api/v1/me/messages/" + id);
      }
   }

   public class Message
   {
      internal Account Account;

      public long ID { get; set; }

      private JsonElement Read()
      {
         var answer = ServerApi.AsAccount(Account.Address, Account.Password, HttpMethod.Get,
            "/api/v1/me/messages/" + ID).Expect(200, "GET /api/v1/me/messages/" + ID);
         return answer.Json.Value;
      }

      public string Subject
      {
         get { return ServerApi.StringOf(Read(), "subject"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public string From
      {
         get { return ServerApi.StringOf(Read(), "from"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public string To
      {
         get { return ServerApi.StringOf(Read(), "to"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public string Body
      {
         get { return ServerApi.StringOf(Read(), "text"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public string HTMLBody
      {
         get { return ServerApi.StringOf(Read(), "html"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public long UID => ServerApi.LongOf(Read(), "uid");

      public long Size => ServerApi.LongOf(Read(), "size");

      // The file on the server's disk, its raw headers, its recipient collection and
      // its character set: the COM Message carries all of these and the REST message
      // routes carry none of them.
      public string Filename
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return null;
         }
      }

      public MessageHeaders Headers
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return null;
         }
      }

      public string FromAddress
      {
         get { return ServerApi.StringOf(Read(), "from"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public int FlagSeen
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return 0;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public string Date
      {
         get { return ServerApi.StringOf(Read(), "date"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public long FolderID
      {
         get { return ServerApi.LongOf(Read(), "folder_id"); }
      }

      public long AccountID
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return 0;
         }
      }

      public int State
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return 0;
         }
      }

      public int Flags
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return 0;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public void Delete()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }

      public string Charset
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return null;
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject); }
      }

      public DateTime InternalDate
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return DateTime.MinValue;
         }
      }

      public MessageRecipients Recipients
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return null;
         }
      }

      public Attachments Attachments
      {
         get
         {
            NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
            return null;
         }
      }

      public void set_HeaderValue(string name, string value)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }

      public string get_HeaderValue(string name)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
         return null;
      }

      public void RefreshContent()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }

      public void AddRecipient(string name, string address)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }

      public void Save()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }
   }

   public class MessageHeaders
   {
      public int Count => 0;

      public MessageHeader Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public MessageHeader this[int index] => null;

      public MessageHeader get_Item(int index)
      {
         return null;
      }

      public MessageHeader get_ItemByName(string name)
      {
         return null;
      }

      public void DeleteByName(string name)
      {
      }

      public void Clear()
      {
      }
   }

   public class MessageHeader
   {
      public string Name { get; set; }
      public string Value { get; set; }

      public void Save()
      {
      }
   }

   public class MessageRecipients
   {
      public int Count => 0;

      public MessageRecipient Add()
      {
         return null;
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public MessageRecipient this[int index] => null;

      public MessageRecipient get_Item(int index)
      {
         return null;
      }

      public void Clear()
      {
      }
   }

   public class MessageRecipient
   {
      public string Address { get; set; }
      public string OriginalAddress { get; set; }

      public void Save()
      {
      }
   }

   public class Attachments
   {
      public int Count => 0;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public Attachment this[int index] => null;

      public Attachment get_Item(int index)
      {
         return null;
      }

      public Attachment Add(string filename)
      {
         return null;
      }

      public void Clear()
      {
      }
   }

   public class Attachment
   {
      public string Filename { get; set; }
      public string ContentType { get; set; }
      public long Size { get; set; }

      public void SaveAs(string path)
      {
      }
   }
}
