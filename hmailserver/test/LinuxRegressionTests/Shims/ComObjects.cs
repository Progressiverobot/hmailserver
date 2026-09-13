// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Linq;
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
         throw NotOnThisServer.Skipped(NotOnThisServer.NoComAuthenticate);
      }

      public Database Database
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoDatabaseObject); }
      }

      public Diagnostics Diagnostics
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoDiagnostics); }
      }

      public BackupManager BackupManager
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoBackupSettings); }
      }

      public GlobalObjects GlobalObjects
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoScripting); }
      }

      public bool AuthenticateWithCode(string user, string password, string code)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAdministratorTotp);
      }

      public bool AdministratorTOTPEnabled
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAdministratorTotp);
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

      public object Add() { throw NotOnThisServer.Skipped("adds a object, which no REST route carries yet"); }

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
         throw NotOnThisServer.Skipped(NotOnThisServer.NoDatabaseObject);
      }

      public int CurrentVersion
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoDatabaseObject);
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoBackupSettings);
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoDiagnostics);
         }
      }

      public object PerformTests()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoDiagnostics);
      }

      public void TriggerAssertion(int which = 0)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoDiagnostics);
      }

      public DateTime AcmeRenewalTime
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoDiagnostics);
         }
      }

      public string DnssecChainStatus
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoDiagnostics); }
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoStatusThreadId);
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
               throw NotOnThisServer.Skipped(NotOnThisServer.NoClientSessionCount);
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoServerStartTime);
         }
      }

      public int UndeliveredMessages
      {
         get { return RegressionTests.Shared.TestSetup.GetNumberOfMessagesInDeliveryQueue(); }
      }

      public string CheckForUpdate()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUpdateObject);
      }

      public string DownloadUpdate()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUpdateObject);
      }

      public string UpdateState
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoUpdateObject); }
      }

      public string UpdateLastError
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoUpdateObject); }
      }

      public string InstallUpdate()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUpdateObject);
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
      public const string Directories = "/api/v1/settings/directories";
      public const string Ini = "/api/v1/settings/ini";
      public const string LogonFailuresClear = "/api/v1/settings/logon-failures/clear";
      public const string Scripting = "/api/v1/settings/scripting";
      public const string Backup = "/api/v1/settings/backup";
      public const string Messages = "/api/v1/settings/messages";
      public const string SieveEvaluate = "/api/v1/sieve/evaluate";

      /// <summary>A group's value, or the skip that names the group when this server has no such route.</summary>
      public static JsonElement ReadOrSkip(string group, string key, string reason)
      {
         if (!ServerApi.HasRoute(group, "get"))
            throw NotOnThisServer.Skipped(reason);
         return Read(group, key);
      }

      public static void PutOrSkip(string group, string key, string jsonValue, string reason)
      {
         if (!ServerApi.HasRoute(group, "put"))
            NotOnThisServer.Ignore(reason);
         var answer = ServerApi.Put(group, "{" + ServerApi.Quote(key) + ":" + jsonValue + "}");
         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException(answer.Error);
         answer.Expect(200, "PUT " + group + " " + key);
      }

      public static JsonElement Read(string group, string key)
      {
         var answer = ServerApi.Get(group).Expect(200, "GET " + group);
         JsonElement value;
         if (!answer.Json.HasValue || !answer.Json.Value.TryGetProperty(key, out value))
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSettingsWrite + " (" + key + " is not in " + group + ")");

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

      /// <summary>
      ///    The REST fixtures set the password every request of this run already
      ///    carries, in their SetUp, so that the bench is in the state they assume;
      ///    here that state is proven by every request that has succeeded, and
      ///    setting it to what it is is nothing to do. Any other value is a change
      ///    no route makes.
      /// </summary>
      public void SetAdministratorPassword(string password)
      {
         if (password == TestTarget.AdminPassword)
            return;
         NotOnThisServer.Ignore(NotOnThisServer.NoAdministratorPassword, password);
      }

      public void SetSMTPRelayerPassword(string password)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoRelayerPassword);
      }

      /// <summary>
      ///    POST /api/v1/settings/logon-failures/clear: the failures the auto-ban
      ///    counts are forgotten, which is what the COM call does.
      /// </summary>
      public void ClearLogonFailureList()
      {
         if (!ServerApi.HasRoute(SettingsApi.LogonFailuresClear, "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoLogonFailureList);
         ServerApi.Post(SettingsApi.LogonFailuresClear, "{}").Expect(200, "POST " + SettingsApi.LogonFailuresClear);
      }

      // ---- The [Settings] section of hMailServer.ini, over /api/v1/settings/ini ----
      //
      // The routes refuse what the COM members refuse, in the same sentences,
      // and a refusal here is thrown as the COMException the fixtures expect
      // (IniSettingsOverCom asserts the type), carrying the route's sentence.

      private static void IniRouteOrSkip()
      {
         if (!ServerApi.HasRoute(SettingsApi.Ini, "get"))
            throw NotOnThisServer.Skipped(NotOnThisServer.NoIniSettings);
      }

      private static ApiAnswer IniRefusalAsComException(ApiAnswer answer, string doing)
      {
         if (answer.Status == 400 || answer.Status == 500)
            throw new System.Runtime.InteropServices.COMException(answer.Error);
         return answer.Expect(200, doing);
      }

      public string GetIniSetting(string name)
      {
         IniRouteOrSkip();
         var answer = IniRefusalAsComException(ServerApi.Get(SettingsApi.Ini + "/" + name), "GET " + SettingsApi.Ini + "/" + name);
         return ServerApi.StringOf(answer.Json.Value, "value");
      }

      public void SetIniSetting(string name, string value)
      {
         IniRouteOrSkip();
         IniRefusalAsComException(ServerApi.Put(SettingsApi.Ini + "/" + name, "{\"value\":" + ServerApi.Quote(value) + "}"), "PUT " + SettingsApi.Ini + "/" + name);
      }

      public void DeleteIniSetting(string name)
      {
         IniRouteOrSkip();
         IniRefusalAsComException(ServerApi.Delete(SettingsApi.Ini + "/" + name), "DELETE " + SettingsApi.Ini + "/" + name);
      }

      /// <summary>The names one per line, joined with CRLF as the COM property joins them.</summary>
      public string IniSettingNames
      {
         get
         {
            IniRouteOrSkip();
            var answer = ServerApi.Get(SettingsApi.Ini).Expect(200, "GET " + SettingsApi.Ini);
            var names = ServerApi.Array(answer, "names").Select(n => n.GetString());
            return string.Join("\r\n", names);
         }
      }

      public int CrashSimulationMode
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoCrashSimulation);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCrashSimulation, value); }
      }

      public string PublicFolderDiskName
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoPublicFolderDiskName); }
      }

      public void DisableAdministratorTOTP()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoAdministratorTotp);
      }

      public string EnrolAdministratorTOTP()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAdministratorTotp);
      }

      public string TestLdapDirectory(string a = null, string b = null)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoDirectorySync);
      }

      public string PreviewDirectorySync(string a = null, string b = null, string c = null)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoDirectorySync);
      }

      public string ApplyDirectorySync(string a = null, string b = null, string c = null)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoDirectorySync);
      }

      public string UserInterfaceLanguage
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoUserInterfaceLanguage); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoUserInterfaceLanguage, value); }
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoLiveLog);
         }
      }

      public string LiveLog
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoLiveLog); }
      }

      public bool MaskPasswordsInLog
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSettingsKey("mask_passwords_in_log", "logging"));
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoSettingsKey("mask_passwords_in_log", "logging"), value); }
      }

      /// <summary>The device row of the logging group: unknown, sql or file.</summary>
      public eLogDevice Device
      {
         get
         {
            switch (SettingsApi.GetString(SettingsApi.Logging, "device"))
            {
               case "sql": return eLogDevice.hLogDeviceSQL;
               case "file": return eLogDevice.hLogDeviceFile;
               default: return eLogDevice.hLogDeviceUnknown;
            }
         }
         set
         {
            string word = value == eLogDevice.hLogDeviceSQL ? "sql" : value == eLogDevice.hLogDeviceFile ? "file" : "unknown";
            SettingsApi.PutOrSkip(SettingsApi.Logging, "device", ServerApi.Quote(word), NotOnThisServer.NoLogDeviceWrite);
         }
      }
   }

   /// <summary>
   ///    The server's own directories, from GET /api/v1/settings/directories -
   ///    the same seven InterfaceDirectories reports. A fixture reads them to
   ///    open hMailServer.ini (ProgramDirectory, where the CI tree and the
   ///    Windows bench both keep it) or to look at the message store
   ///    (DataDirectory), which is why the tests run on the machine the server
   ///    runs on.
   /// </summary>
   public class Directories
   {
      private static string Get(string key)
      {
         if (!ServerApi.HasRoute(SettingsApi.Directories, "get"))
            throw NotOnThisServer.Skipped(NotOnThisServer.NoServerDirectories);
         return SettingsApi.GetString(SettingsApi.Directories, key);
      }

      public string ProgramDirectory => Get("program");
      public string DataDirectory => Get("data");
      public string LogDirectory => Get("log");
      public string EventDirectory => Get("event");
      public string TempDirectory => Get("temp");
      public string DatabaseDirectory => Get("database");
      public string DBScriptDirectory => Get("db_scripts");
   }

   /// <summary>The event-handler scripting, which no REST route configures or runs.</summary>
   /// <summary>
   ///    Settings.Scripting over GET/PUT /api/v1/settings/scripting and the two
   ///    POSTs beside it; the file-system object the COM script host exposes has
   ///    no HTTP equivalent and skips.
   /// </summary>
   public class Scripting
   {
      public bool Enabled
      {
         get { return SettingsApi.ReadOrSkip(SettingsApi.Scripting, "enabled", NotOnThisServer.NoScripting).GetBoolean(); }
         set { SettingsApi.PutOrSkip(SettingsApi.Scripting, "enabled", value ? "true" : "false", NotOnThisServer.NoScripting); }
      }

      public string Language
      {
         get { return SettingsApi.ReadOrSkip(SettingsApi.Scripting, "language", NotOnThisServer.NoScripting).GetString(); }
         set { SettingsApi.PutOrSkip(SettingsApi.Scripting, "language", ServerApi.Quote(value ?? string.Empty), NotOnThisServer.NoScripting); }
      }

      public string CurrentScriptFile => SettingsApi.ReadOrSkip(SettingsApi.Scripting, "current_script_file", NotOnThisServer.NoScripting).GetString();

      public string Directory => SettingsApi.ReadOrSkip(SettingsApi.Scripting, "directory", NotOnThisServer.NoScripting).GetString();

      public object FileSystemObject
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoScripting); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoScripting, value); }
      }

      public void Reload()
      {
         if (!ServerApi.HasRoute(SettingsApi.Scripting + "/reload", "post"))
            NotOnThisServer.Ignore(NotOnThisServer.NoScripting);
         ServerApi.Post(SettingsApi.Scripting + "/reload", "{}").Expect(200, "POST " + SettingsApi.Scripting + "/reload");
      }

      public string CheckSyntax()
      {
         if (!ServerApi.HasRoute(SettingsApi.Scripting + "/check", "post"))
            throw NotOnThisServer.Skipped(NotOnThisServer.NoScripting);
         var answer = ServerApi.Post(SettingsApi.Scripting + "/check", "{}").Expect(200, "POST " + SettingsApi.Scripting + "/check");
         return ServerApi.StringOf(answer.Json.Value, "result") ?? string.Empty;
      }
   }

   public class PublicFolders
   {
      public IMAPFolder Add(string name)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoPublicFolderWrite);
      }

      public IMAPFolder get_ItemByName(string name)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoPublicFolderWrite);
      }

      public int Count
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoPublicFolderWrite);
         }
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoPublicFolderWrite);
      }
   }

   /// <summary>Settings.Backup over GET/PUT /api/v1/settings/backup.</summary>
   public class Backup
   {
      private static string Text(string key) => SettingsApi.ReadOrSkip(SettingsApi.Backup, key, NotOnThisServer.NoBackupSettings).GetString();
      private static bool Flag(string key) => SettingsApi.ReadOrSkip(SettingsApi.Backup, key, NotOnThisServer.NoBackupSettings).GetBoolean();
      private static void Put(string key, string json) => SettingsApi.PutOrSkip(SettingsApi.Backup, key, json, NotOnThisServer.NoBackupSettings);

      public string Destination
      {
         get { return Text("destination"); }
         set { Put("destination", ServerApi.Quote(value ?? string.Empty)); }
      }

      public string LogFile => Text("log_file");

      public bool BackupMessages
      {
         get { return Flag("backup_messages"); }
         set { Put("backup_messages", value ? "true" : "false"); }
      }

      public bool BackupSettings
      {
         get { return Flag("backup_settings"); }
         set { Put("backup_settings", value ? "true" : "false"); }
      }

      public bool BackupDomains
      {
         get { return Flag("backup_domains"); }
         set { Put("backup_domains", value ? "true" : "false"); }
      }

      public bool CompressDestinationFiles
      {
         get { return Flag("compress"); }
         set { Put("compress", value ? "true" : "false"); }
      }
   }

   public class Cache
   {
      public bool Enabled
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoCacheControl);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl, value); }
      }

      public int DomainCacheSizeKb
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoCacheControl);
         }
      }

      public int DomainCacheMaxSizeKb
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoCacheControl);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl, value); }
      }

      public int AccountCacheSizeKb
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoCacheControl);
         }
      }

      public int AccountCacheMaxSizeKb
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoCacheControl);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoCacheControl, value); }
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings, value); }
      }

      public BlockedAttachments BlockedAttachments { get; } = new BlockedAttachments();

      public eAntivirusAction Action
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings, value); }
      }

      public int ScannerFailurePolicy
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings, value); }
      }

      public bool CustomScannerEnabled
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings, value); }
      }

      public string TestClamAVScanner(string host = null, int port = 0)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
      }

      public bool ClamAVEnabled
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAntiVirusSettings);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAntiVirusSettings, value); }
      }
   }

   public class Groups
   {
      public int Count
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoGroups);
         }
      }

      public int Length
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoGroups);
         }
      }

      public Group Add()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoGroups);
      }

      public Group get_ItemByName(string name)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoGroups);
      }

      public void DeleteByDBID(long id)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoGroups);
      }
   }

   /// <summary>Settings.ServerMessages over GET /api/v1/settings/messages and PUT .../messages/{name}.</summary>
   public class ServerMessages
   {
      private static List<JsonElement> All()
      {
         if (!ServerApi.HasRoute(SettingsApi.Messages, "get"))
            throw NotOnThisServer.Skipped(NotOnThisServer.NoServerMessages);
         return ServerApi.Array(ServerApi.Get(SettingsApi.Messages).Expect(200, "GET " + SettingsApi.Messages));
      }

      private static ServerMessage From(JsonElement element)
      {
         return new ServerMessage
         {
            ID = ServerApi.LongOf(element, "id"),
            Name = ServerApi.StringOf(element, "name"),
            Text = ServerApi.StringOf(element, "text")
         };
      }

      public int Count => All().Count;

      [System.Runtime.CompilerServices.IndexerName("At")]
      public ServerMessage this[int index] => From(All()[index]);

      public ServerMessage get_Item(int index)
      {
         return From(All()[index]);
      }

      public ServerMessage get_ItemByName(string name)
      {
         var found = All().FirstOrDefault(element => string.Equals(ServerApi.StringOf(element, "name"), name, StringComparison.OrdinalIgnoreCase));
         if (found.ValueKind == JsonValueKind.Undefined)
            throw new System.Runtime.InteropServices.COMException("Item not found. " + name);
         return From(found);
      }
   }

   public class ServerMessage
   {
      public long ID { get; set; }
      public string Name { get; set; }
      public string Text { get; set; }

      public void Save()
      {
         if (!ServerApi.HasRoute(SettingsApi.Messages + "/{name}", "put"))
            NotOnThisServer.Ignore(NotOnThisServer.NoServerMessages);
         var answer = ServerApi.Put(SettingsApi.Messages + "/" + Name, "{\"text\":" + ServerApi.Quote(Text ?? string.Empty) + "}");
         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException(answer.Error);
         answer.Expect(200, "PUT " + SettingsApi.Messages + "/" + Name);
      }
   }

   public class MessageIndexing
   {
      public bool Enabled
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageIndexing);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageIndexing, value); }
      }

      public void Index()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageIndexing);
      }

      public long TotalIndexedCount
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageIndexing + " (TotalIndexedCount)");
         }
      }

      public long TotalMessageCount
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageIndexing + " (TotalMessageCount)");
         }
      }
   }

   public class IncomingRelays
   {
      public object Add()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoIncomingRelays);
      }

      public int Count
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoIncomingRelays);
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
         throw NotOnThisServer.Skipped(NotOnThisServer.NoSpamAssassinProbe);
      }

      public string DKIMVerify(string rawMessage)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoDkimVerifyCall);
      }

      public DNSBlackLists DNSBlackLists
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoBlacklistCollections); }
      }

      public SURBLServers SURBLServers
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoBlacklistCollections); }
      }

      public BlockedSenders BlockedSenders
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoBlacklistCollections); }
      }

      public WhiteListAddresses WhiteListAddresses
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoBlacklistCollections); }
      }

      public GreyListingWhiteAddresses GreyListingWhiteAddresses
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoBlacklistCollections); }
      }

      public Quarantine Quarantine
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoQuarantineObject); }
      }
   }

   // The anti-spam list collections: no REST route reads or writes any of them, so
   // the property above stops the test before one of these is touched. They exist
   // only so that a fixture naming their types compiles.
   public class DNSBlackLists
   {
      public DNSBlackList Add() { throw NotOnThisServer.Skipped("adds a DNSBlackList, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public DNSBlackList this[int index] => throw NotOnThisServer.Skipped("indexes a DNSBlackList, which no REST route carries yet");

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
      public SURBLServer Add() { throw NotOnThisServer.Skipped("adds a SURBLServer, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public SURBLServer this[int index] => throw NotOnThisServer.Skipped("indexes a SURBLServer, which no REST route carries yet");

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
      public string ExpectedResult { get; set; }
      public int Score { get; set; }
      public bool Active { get; set; }

      public void Save()
      {
      }
   }

   public class BlockedSenders
   {
      public BlockedSender Add() { throw NotOnThisServer.Skipped("adds a BlockedSender, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public BlockedSender this[int index] => throw NotOnThisServer.Skipped("indexes a BlockedSender, which no REST route carries yet");

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
      public WhiteListAddress Add() { throw NotOnThisServer.Skipped("adds a WhiteListAddress, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public WhiteListAddress this[int index] => throw NotOnThisServer.Skipped("indexes a WhiteListAddress, which no REST route carries yet");

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
      public GreyListingWhiteAddress Add() { throw NotOnThisServer.Skipped("adds a GreyListingWhiteAddress, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public GreyListingWhiteAddress this[int index] => throw NotOnThisServer.Skipped("indexes a GreyListingWhiteAddress, which no REST route carries yet");

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
      public QuarantinedMessage this[int index] => throw NotOnThisServer.Skipped("indexes a QuarantinedMessage, which no REST route carries yet");

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

      /// <summary>POST /api/v1/sieve/evaluate: the same verdict, nothing delivered.</summary>
      public string EvaluateSieveScript(string script, string rawMessage)
      {
         if (!ServerApi.HasRoute(SettingsApi.SieveEvaluate, "post"))
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSieveEvaluate);
         var answer = ServerApi.Post(SettingsApi.SieveEvaluate,
            "{\"script\":" + ServerApi.Quote(script ?? string.Empty) + ",\"message\":" + ServerApi.Quote(rawMessage ?? string.Empty) + "}");
         answer.Expect(200, "POST " + SettingsApi.SieveEvaluate);
         return ServerApi.StringOf(answer.Json.Value, "result") ?? string.Empty;
      }

      public string GetMailServer(string address)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoMailServerLookup);
      }

      public string MD5(string text)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string BlowfishEncrypt(string text)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string BlowfishDecrypt(string text)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
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
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string SendDmarcReports(string date = null)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string SendTlsRptReports(string date = null)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string SearchArchive(string query, string from = null, string to = null)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string ResolveMXRecords(string domain)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string RunMessageRetention()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string PerformMaintenance(eMaintenanceOperation operation)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public void SampleMetricsNow()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoUtilityCall);
      }

      public string IsStrongPassword(string password)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }

      public string IsValidEmailAddress(string address)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoUtilityCall);
      }
   }

   /// <summary>
   ///    A domain. PUT /api/v1/domains/{domain} takes active and postmaster and
   ///    nothing else, so those two save; every other property the COM object has is
   ///    remembered as set and Save() then stops the test naming it, rather than
   ///    dropping the change and letting the test assert on a server that never
   ///    received it.
   /// </summary>
   /// <summary>
   ///    A domain over /api/v1/domains: created with POST, changed with PUT
   ///    /api/v1/domains/{domain}, which since the fifth wave of the route
   ///    backlog takes every scalar InterfaceDomain saves, and a new name. A
   ///    setter records what the fixture set; Save sends the create, and then a
   ///    PUT of what was set, so that a field the fixture never touched keeps
   ///    the server's own default rather than this object's. A domain read
   ///    from the listing carries every value the entry shows.
   /// </summary>
   public class Domain : RestBackedObject
   {
      internal bool Unsaved;
      private string _name;
      private string _savedName;

      // What the fixture set since the last Save, as JSON values by API key.
      private readonly Dictionary<string, string> _pending = new Dictionary<string, string>();

      private void Pend(string key, string json)
      {
         _pending[key] = json;
      }

      private static string Q(string value) => ServerApi.Quote(value ?? string.Empty);
      private static string B(bool value) => value ? "true" : "false";
      private static string N(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

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

      private bool _active = true;
      public bool Active
      {
         get { return _active; }
         set { _active = value; }
      }

      private string _postmaster = string.Empty;
      public string Postmaster
      {
         get { return _postmaster; }
         set { _postmaster = value; Pend("postmaster", Q(value)); }
      }

      public long ID
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoDomainIds);
         }
      }

      public Accounts Accounts => new Accounts(Name);
      public Aliases Aliases => new Aliases(Name);
      public DistributionLists DistributionLists => new DistributionLists(Name);
      public DomainAliases DomainAliases => new DomainAliases(Name);

      private int _maxMessageSize; public int MaxMessageSize { get { return _maxMessageSize; } set { _maxMessageSize = value; Pend("max_message_size_kb", N(value)); } }
      private int _maxSize; public int MaxSize { get { return _maxSize; } set { _maxSize = value; Pend("max_size_mb", N(value)); } }
      private int _maxAccountSize; public int MaxAccountSize { get { return _maxAccountSize; } set { _maxAccountSize = value; Pend("max_account_size_mb", N(value)); } }
      private int _maxAccounts; public int MaxNumberOfAccounts { get { return _maxAccounts; } set { _maxAccounts = value; Pend("max_accounts", N(value)); } }
      private int _maxAliases; public int MaxNumberOfAliases { get { return _maxAliases; } set { _maxAliases = value; Pend("max_aliases", N(value)); } }
      private int _maxLists; public int MaxNumberOfDistributionLists { get { return _maxLists; } set { _maxLists = value; Pend("max_lists", N(value)); } }
      private bool _maxAccountsEnabled; public bool MaxNumberOfAccountsEnabled { get { return _maxAccountsEnabled; } set { _maxAccountsEnabled = value; Pend("max_accounts_enabled", B(value)); } }
      private bool _maxAliasesEnabled; public bool MaxNumberOfAliasesEnabled { get { return _maxAliasesEnabled; } set { _maxAliasesEnabled = value; Pend("max_aliases_enabled", B(value)); } }
      private bool _maxListsEnabled; public bool MaxNumberOfDistributionListsEnabled { get { return _maxListsEnabled; } set { _maxListsEnabled = value; Pend("max_lists_enabled", B(value)); } }
      private bool _plusAddressing; public bool PlusAddressingEnabled { get { return _plusAddressing; } set { _plusAddressing = value; Pend("plus_addressing_enabled", B(value)); } }
      private string _plusChar = "+"; public string PlusAddressingCharacter { get { return _plusChar; } set { _plusChar = value; Pend("plus_addressing_character", Q(value)); } }
      private bool _greylisting = true; public bool AntiSpamEnableGreylisting { get { return _greylisting; } set { _greylisting = value; Pend("use_greylisting", B(value)); } }
      private bool _signatureEnabled; public bool SignatureEnabled { get { return _signatureEnabled; } set { _signatureEnabled = value; Pend("signature_enabled", B(value)); } }
      private eDomainSignatureMethod _signatureMethod; public eDomainSignatureMethod SignatureMethod { get { return _signatureMethod; } set { _signatureMethod = value; Pend("signature_method", Q(SignatureMethodWord((int) value))); } }
      private string _signaturePlain = string.Empty; public string SignaturePlainText { get { return _signaturePlain; } set { _signaturePlain = value; Pend("signature_plain_text", Q(value)); } }
      private string _signatureHtml = string.Empty; public string SignatureHTML { get { return _signatureHtml; } set { _signatureHtml = value; Pend("signature_html", Q(value)); } }
      private bool _signatureReplies; public bool AddSignaturesToReplies { get { return _signatureReplies; } set { _signatureReplies = value; Pend("signature_add_to_replies", B(value)); } }
      private bool _signatureLocal; public bool AddSignaturesToLocalMail { get { return _signatureLocal; } set { _signatureLocal = value; Pend("signature_add_to_local_mail", B(value)); } }
      private bool _dkim; public bool DKIMSignEnabled { get { return _dkim; } set { _dkim = value; Pend("dkim_enabled", B(value)); } }
      private string _dkimSelector = string.Empty; public string DKIMSelector { get { return _dkimSelector; } set { _dkimSelector = value; Pend("dkim_selector", Q(value)); } }
      private string _dkimKeyFile = string.Empty; public string DKIMPrivateKeyFile { get { return _dkimKeyFile; } set { _dkimKeyFile = value; Pend("dkim_private_key_file", Q(value)); } }
      private eDKIMAlgorithm _dkimAlgorithm = eDKIMAlgorithm.eSHA256; public eDKIMAlgorithm DKIMSigningAlgorithm { get { return _dkimAlgorithm; } set { _dkimAlgorithm = value; Pend("dkim_signing_algorithm", Q(value == eDKIMAlgorithm.eSHA1 ? "sha1" : "sha256")); } }
      private int _retention; public int MessageRetentionDays { get { return _retention; } set { _retention = value; Pend("message_retention_days", N(value)); } }
      private string _relayHost = string.Empty; public string RelayHost { get { return _relayHost; } set { _relayHost = value; Pend("relay_host", Q(value)); } }
      private int _relayPort; public int RelayPort { get { return _relayPort; } set { _relayPort = value; Pend("relay_port", N(value)); } }
      private bool _relayAuth; public bool RelayRequiresAuthentication { get { return _relayAuth; } set { _relayAuth = value; Pend("relay_requires_auth", B(value)); } }
      private string _relayUser = string.Empty; public string RelayUsername { get { return _relayUser; } set { _relayUser = value; Pend("relay_username", Q(value)); } }
      private string _relayPassword; public string RelayPassword { get { return _relayPassword; } set { _relayPassword = value; Pend("relay_password", Q(value)); } }
      private eConnectionSecurity _relaySecurity = eConnectionSecurity.eCSNone; public eConnectionSecurity RelayConnectionSecurity { get { return _relaySecurity; } set { _relaySecurity = value; Pend("relay_connection_security", Q(RegressionTests.Shared.TestSetup.ConnectionSecurityName(value))); } }
      private bool _vacationOn; public bool VacationMessageIsOn { get { return _vacationOn; } set { _vacationOn = value; Pend("vacation_enabled", B(value)); } }
      private string _vacationSubject = string.Empty; public string VacationSubject { get { return _vacationSubject; } set { _vacationSubject = value; Pend("vacation_subject", Q(value)); } }
      private string _vacationMessage = string.Empty; public string VacationMessage { get { return _vacationMessage; } set { _vacationMessage = value; Pend("vacation_message", Q(value)); } }

      // What the route does not carry.
      public string DKIMSecondarySelector { get { Unsupported("DKIMSecondarySelector"); return null; } set { Unsupported("DKIMSecondarySelector", value); } }
      public string DKIMSecondaryPrivateKeyFile { get { Unsupported("DKIMSecondaryPrivateKeyFile"); return null; } set { Unsupported("DKIMSecondaryPrivateKeyFile", value); } }
      public string ADDomainName { get { Unsupported("ADDomainName"); return null; } set { Unsupported("ADDomainName", value); } }
      public bool MaxMessageSizeEnabled { get { Unsupported("MaxMessageSizeEnabled"); return false; } set { Unsupported("MaxMessageSizeEnabled", value); } }
      public bool MaxAccountSizeEnabled { get { Unsupported("MaxAccountSizeEnabled"); return false; } set { Unsupported("MaxAccountSizeEnabled", value); } }
      public bool EnableLimitations { get { Unsupported("EnableLimitations"); return false; } set { Unsupported("EnableLimitations", value); } }

      public void DKIMPromoteSecondary()
      {
         Unsupported("DKIMPromoteSecondary");
         SkipIfAnythingUnsupported("PUT /api/v1/domains/{domain}");
      }

      private static string SignatureMethodWord(int method)
      {
         switch (method)
         {
            case 1: return "set_if_not_specified";
            case 2: return "overwrite";
            case 3: return "append";
            default: return "unknown";
         }
      }

      private static int SignatureMethodOf(string word)
      {
         switch (word)
         {
            case "set_if_not_specified": return 1;
            case "overwrite": return 2;
            case "append": return 3;
            default: return 0;
         }
      }

      /// <summary>Every value the listing entry shows, without marking any of it as set.</summary>
      internal void Read(JsonElement element)
      {
         _name = ServerApi.StringOf(element, "name");
         _savedName = _name;
         _active = ServerApi.FlagOf(element, "active");
         _postmaster = ServerApi.StringOf(element, "postmaster") ?? string.Empty;
         _maxMessageSize = (int) ServerApi.LongOf(element, "max_message_size_kb");
         _maxSize = (int) ServerApi.LongOf(element, "max_size_mb");
         _maxAccountSize = (int) ServerApi.LongOf(element, "max_account_size_mb");
         _maxAccounts = (int) ServerApi.LongOf(element, "max_accounts");
         _maxAliases = (int) ServerApi.LongOf(element, "max_aliases");
         _maxLists = (int) ServerApi.LongOf(element, "max_lists");
         _maxAccountsEnabled = ServerApi.FlagOf(element, "max_accounts_enabled");
         _maxAliasesEnabled = ServerApi.FlagOf(element, "max_aliases_enabled");
         _maxListsEnabled = ServerApi.FlagOf(element, "max_lists_enabled");
         _plusAddressing = ServerApi.FlagOf(element, "plus_addressing_enabled");
         _plusChar = ServerApi.StringOf(element, "plus_addressing_character") ?? "+";
         _greylisting = ServerApi.FlagOf(element, "use_greylisting");
         _signatureEnabled = ServerApi.FlagOf(element, "signature_enabled");
         _signatureMethod = (eDomainSignatureMethod) SignatureMethodOf(ServerApi.StringOf(element, "signature_method"));
         _signaturePlain = ServerApi.StringOf(element, "signature_plain_text") ?? string.Empty;
         _signatureHtml = ServerApi.StringOf(element, "signature_html") ?? string.Empty;
         _signatureReplies = ServerApi.FlagOf(element, "signature_add_to_replies");
         _signatureLocal = ServerApi.FlagOf(element, "signature_add_to_local_mail");
         _dkim = ServerApi.FlagOf(element, "dkim_enabled");
         _dkimSelector = ServerApi.StringOf(element, "dkim_selector") ?? string.Empty;
         _dkimKeyFile = ServerApi.StringOf(element, "dkim_private_key_file") ?? string.Empty;
         _dkimAlgorithm = ServerApi.StringOf(element, "dkim_signing_algorithm") == "sha1" ? eDKIMAlgorithm.eSHA1 : eDKIMAlgorithm.eSHA256;
         _retention = (int) ServerApi.LongOf(element, "message_retention_days");
         _relayHost = ServerApi.StringOf(element, "relay_host") ?? string.Empty;
         _relayPort = (int) ServerApi.LongOf(element, "relay_port");
         _relayAuth = ServerApi.FlagOf(element, "relay_requires_auth");
         _relayUser = ServerApi.StringOf(element, "relay_username") ?? string.Empty;
         _relaySecurity = TCPIPPort.SecurityOf(ServerApi.StringOf(element, "relay_connection_security"));
         _vacationOn = ServerApi.FlagOf(element, "vacation_enabled");
         _vacationSubject = ServerApi.StringOf(element, "vacation_subject") ?? string.Empty;
         _vacationMessage = ServerApi.StringOf(element, "vacation_message") ?? string.Empty;
         _pending.Clear();
      }

      public void Save()
      {
         SkipIfAnythingUnsupported("PUT /api/v1/domains/{domain}");

         if (Unsaved)
         {
            if (!ServerApi.HasDomainWriteRoutes)
               NotOnThisServer.Ignore(NotOnThisServer.NoDomainCreate);

            var created = ServerApi.Post("/api/v1/domains",
               "{\"name\":" + Q(Name) + ",\"active\":" + B(Active) + ",\"postmaster\":" + Q(Postmaster) + "}");
            if (created.Status == 400 || created.Status == 409)
               throw new System.Runtime.InteropServices.COMException("Failed to save object. " + created.Error);
            created.Expect(201, "POST /api/v1/domains " + Name);
            _savedName = Name;
            Unsaved = false;
            _pending.Remove("postmaster");
            if (_pending.Count == 0)
               return;
         }

         if (!ServerApi.HasRoute("/api/v1/domains/{domain}", "put"))
            NotOnThisServer.Ignore(NotOnThisServer.NoDomainUpdate);

         bool renamed = !string.Equals(_savedName, Name, StringComparison.OrdinalIgnoreCase);
         if (renamed && !ServerApi.HasRoute("/api/v1/domains/{domain}/domain-aliases", "get"))
            NotOnThisServer.Ignore(NotOnThisServer.NoDomainRename);

         var fields = new List<string> { "\"active\":" + B(Active) };
         if (renamed)
            fields.Add("\"name\":" + Q(Name));
         foreach (var pair in _pending)
            fields.Add(ServerApi.Quote(pair.Key) + ":" + pair.Value);

         var answer = ServerApi.Put("/api/v1/domains/" + _savedName, "{" + string.Join(",", fields) + "}");
         if (answer.Status == 400)
            throw new System.Runtime.InteropServices.COMException("Failed to save object. " + answer.Error);
         answer.Expect(200, "PUT /api/v1/domains/" + _savedName);
         Read(answer.Json.Value);
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAliasIds);
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoListIds);
         }
      }

      public eDistributionListMode Mode
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
      }

      public string RequireSMTPAuth
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
      }

      public DistributionListRecipients Recipients
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject); }
      }

      public string ModeratorAddress
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
      }

      public bool RequireSenderAddress
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
      }

      public bool ModerationEnabled
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
      }

      public bool AnnouncementsOnly
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
      }

      public string SenderAddress
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
      }

      public string Name
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoListObject); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoListObject, value); }
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

      public DistributionListRecipient Add() { throw NotOnThisServer.Skipped("adds a DistributionListRecipient, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public DistributionListRecipient this[int index] => throw NotOnThisServer.Skipped("indexes a DistributionListRecipient, which no REST route carries yet");

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
                   ",\"addresses\":[" + string.Join(",", AddressList.Select(a => ServerApi.Quote(a))) + "]" +
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

      /// <summary>The addresses the route carries, kept here and sent whole with every Save.</summary>
      internal List<string> AddressList = new List<string>();

      public RouteAddresses Addresses => new RouteAddresses(this);
   }

   /// <summary>
   ///    Route.Addresses over the route's own body: the REST route carries its
   ///    address list in the document that creates or replaces it, so adding or
   ///    removing an address is a Save of the route with the list changed.
   /// </summary>
   public class RouteAddresses
   {
      private readonly Route _route;

      internal RouteAddresses(Route route)
      {
         _route = route;
      }

      public int Count => _route.AddressList.Count;

      public RouteAddress Add()
      {
         return new RouteAddress(_route);
      }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public RouteAddress this[int index] => new RouteAddress(_route) { Address = _route.AddressList[index], ID = index + 1 };

      public RouteAddress get_Item(int index)
      {
         return this[index];
      }

      public RouteAddress get_ItemByName(string address)
      {
         var found = _route.AddressList.FirstOrDefault(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));
         return found == null ? null : new RouteAddress(_route) { Address = found, ID = _route.AddressList.IndexOf(found) + 1 };
      }

      public void Clear()
      {
         _route.AddressList.Clear();
         _route.Save();
      }

      public void DeleteByDBID(long id)
      {
         if (id >= 1 && id <= _route.AddressList.Count)
         {
            _route.AddressList.RemoveAt((int) id - 1);
            _route.Save();
         }
      }
   }

   public class RouteAddress
   {
      private readonly Route _route;

      internal RouteAddress(Route route)
      {
         _route = route;
      }

      /// <summary>One-based position in the route's list; the API has no id for an address.</summary>
      public long ID { get; set; }
      public string Address { get; set; }

      public void Save()
      {
         if (ID == 0)
         {
            _route.AddressList.Add(Address);
            ID = _route.AddressList.Count;
         }
         else
         {
            _route.AddressList[(int) ID - 1] = Address;
         }
         _route.Save();
      }

      public void Delete()
      {
         if (ID >= 1 && ID <= _route.AddressList.Count)
         {
            _route.AddressList.RemoveAt((int) ID - 1);
            ID = 0;
            _route.Save();
         }
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
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountIds);
         }
      }

      /// <summary>
      ///    The account's folders, read through its own GET /api/v1/me/folders. The
      ///    top of the tree is parent 0, as the route reports it.
      /// </summary>
      public IMAPFolders IMAPFolders => new IMAPFolders(this, IMAPFolders.RootParent);

      public AccountRules Rules { get; } = new AccountRules();

      /// <summary>The account's external accounts, under its address on the API.</summary>
      public FetchAccounts FetchAccounts => new FetchAccounts(this);

      public AppPasswords AppPasswords { get; } = new AppPasswords();

      public bool TOTPEnabled
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountTotp);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoAccountTotp, value); }
      }

      public string EnrolTOTP()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountTotp);
      }

      public bool IsAD
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoDirectoryLink);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink, value); }
      }

      public string ADUsername
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoDirectoryLink); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink, value); }
      }

      public string ADDomain
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoDirectoryLink); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoDirectoryLink, value); }
      }

      public DateTime LastLogonTime
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoAccountLastLogon);
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
         set { Unsupported("VacationMessageAbortSpamFlagged", value); }
      }

      public string VacationMessageBeginDate
      {
         get { Unsupported("VacationMessageBeginDate"); return null; }
         set { Unsupported("VacationMessageBeginDate", value); }
      }

      public int MessageRetentionDays { get { Unsupported("MessageRetentionDays"); return 0; } set { Unsupported("MessageRetentionDays", value); } }
      public int SpamMarkThreshold { get { Unsupported("SpamMarkThreshold"); return 0; } set { Unsupported("SpamMarkThreshold", value); } }
      public int SpamDeleteThreshold { get { Unsupported("SpamDeleteThreshold"); return 0; } set { Unsupported("SpamDeleteThreshold", value); } }
      public bool PersonalSpamSettingsEnabled { get { Unsupported("PersonalSpamSettingsEnabled"); return false; } set { Unsupported("PersonalSpamSettingsEnabled", value); } }
      public bool AntiSpamEnabled { get { Unsupported("AntiSpamEnabled"); return false; } set { Unsupported("AntiSpamEnabled", value); } }
      public bool AntiVirusEnabled { get { Unsupported("AntiVirusEnabled"); return false; } set { Unsupported("AntiVirusEnabled", value); } }

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

            inboxId = ServerApi.Array(folders, "folders")
               .Where(folder => string.Equals(ServerApi.StringOf(folder, "name"), "INBOX", StringComparison.OrdinalIgnoreCase))
               .Select(folder => ServerApi.LongOf(folder, "id"))
               .LastOrDefault();

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
         return Load().FirstOrDefault(message => message.ID == id);
      }

      public Message Add()
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject);
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
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public string From
      {
         get { return ServerApi.StringOf(Read(), "from"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public string To
      {
         get { return ServerApi.StringOf(Read(), "to"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public string Body
      {
         get { return ServerApi.StringOf(Read(), "text"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public string HTMLBody
      {
         get { return ServerApi.StringOf(Read(), "html"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public long UID => ServerApi.LongOf(Read(), "uid");

      public long Size => ServerApi.LongOf(Read(), "size");

      // The file on the server's disk, its raw headers, its recipient collection and
      // its character set: the COM Message carries all of these and the REST message
      // routes carry none of them.
      public string Filename
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject); }
      }

      public MessageHeaders Headers
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject); }
      }

      public string FromAddress
      {
         get { return ServerApi.StringOf(Read(), "from"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public int FlagSeen
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public string Date
      {
         get { return ServerApi.StringOf(Read(), "date"); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public long FolderID
      {
         get { return ServerApi.LongOf(Read(), "folder_id"); }
      }

      public long AccountID
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject);
         }
      }

      public int State
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject);
         }
      }

      public int Flags
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject);
         }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public void Delete()
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }

      public string Charset
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject); }
         set { NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject, value); }
      }

      public DateTime InternalDate
      {
         get
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject);
         }
      }

      public MessageRecipients Recipients
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject); }
      }

      public Attachments Attachments
      {
         get { throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject); }
      }

      public void set_HeaderValue(string name, string value)
      {
         NotOnThisServer.Ignore(NotOnThisServer.NoMessageObject);
      }

      public string get_HeaderValue(string name)
      {
         throw NotOnThisServer.Skipped(NotOnThisServer.NoMessageObject);
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

      public MessageHeader Add() { throw NotOnThisServer.Skipped("adds a MessageHeader, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public MessageHeader this[int index] => throw NotOnThisServer.Skipped("indexes a MessageHeader, which no REST route carries yet");

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

      public MessageRecipient Add() { throw NotOnThisServer.Skipped("adds a MessageRecipient, which no REST route carries yet"); }

      [System.Runtime.CompilerServices.IndexerName("At")]
      public MessageRecipient this[int index] => throw NotOnThisServer.Skipped("indexes a MessageRecipient, which no REST route carries yet");

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
      public Attachment this[int index] => throw NotOnThisServer.Skipped("indexes a Attachment, which no REST route carries yet");

      public Attachment get_Item(int index)
      {
         return null;
      }

      public Attachment Add(string filename) { throw NotOnThisServer.Skipped("adds a Attachment, which no REST route carries yet"); }

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
