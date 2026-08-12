// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IniFileSettings.h"

#include "../Util/Crypt.h"
#include "../Util/Utilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String IniFileSettings::ini_file_;
  
   IniFileSettings::IniFileSettings() :
      is_internal_database_(false),
      dbport_(0),
      no_of_dbconnections_(0),
      add_xauth_user_header_(false),
      add_xoriginal_rcpt_to_header_(false),
      no_of_dbconnection_attempts_(6),
      no_of_dbconnection_attempts_Delay(5),
      max_no_of_external_fetch_threads_(15),
      greylisting_enabled_during_record_expiration_(true),
      greylisting_expiration_interval_(240),
      preferred_hash_algorithm_(3),
      minimum_accepted_hash_algorithm_(0),
      dnsbl_checks_after_mail_from_(false),
      log_level_(0),
      max_log_line_len_(500),
      quick_retries_(0),
      quick_retries_Minutes(0),
      queue_randomness_minutes_(0),
      mxtries_factor_(0),
      sqldbtype_(HM::DatabaseSettings::TypeUnknown),
      sep_svc_logs_(false),
	  rewrite_envelope_from_when_forwarding_(false),
      srs_enabled_(false),
      max_submissions_per_ip_per_minute_(0),
      max_outbound_per_destination_per_minute_(0),
      archive_hardlinks_(false),
      pop3dmin_timeout_(0),
      pop3dmax_timeout_(0),
      pop3cmin_timeout_(0),
      pop3cmax_timeout_(0),
      smtpdmin_timeout_(0),
      smtpdmax_timeout_(0),
      smtpcmin_timeout_(0),
      smtpcmax_timeout_(0),
      samin_timeout_(0),
      samax_timeout_(0),
      clam_min_timeout_(0),
      clam_max_timeout_(0),
      samove_vs_copy_(false),
      indexer_full_minutes_(0),
      indexer_full_limit_(0),
      indexer_quick_limit_(0),
      load_header_read_size_(0),
      load_body_read_size_(0),
      blocked_iphold_seconds_(0),
      smtpdmax_size_drop_(0),
      backup_messages_dbonly_(false),
      add_xauth_user_ip_(false),
      use_dns_cache_(true)
      
   {

   }

   IniFileSettings::~IniFileSettings()
   {  
   

   }

   void
   IniFileSettings::LoadSettings()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Load all settings from hMailServer.ini
   //---------------------------------------------------------------------------()
   {
      administrator_password_ = ReadIniSettingString_("Security", "AdministratorPassword", "");

      database_server_ = ReadIniSettingString_("Database", "Server", "");
      database_name_ = ReadIniSettingString_("Database", "Database", "");
      username_ = ReadIniSettingString_("Database", "Username", "");
      password_ = ReadIniSettingString_("Database", "Password", "");
      is_internal_database_ = ReadIniSettingInteger_("Database", "Internal", 0) == 1;
      database_server_FailoverPartner = ReadIniSettingString_("Database", "ServerFailoverPartner", "");
      database_provider_ = ReadIniSettingString_("Database", "Provider", "");

      String sDatabaseType = ReadIniSettingString_("Database", "Type", "");
      
      Crypt::EncryptionType iPWDEncryptionType = (Crypt::EncryptionType) ReadIniSettingInteger_("Database", "Passwordencryption", 0);

      // Decrypt password read from hmailserver.ini
      password_ = Crypt::Instance()->DeCrypt(password_, iPWDEncryptionType);

      if (sDatabaseType.CompareNoCase(_T("MSSQL")) == 0)
         sqldbtype_ = HM::DatabaseSettings::TypeMSSQLServer;
      else if (sDatabaseType.CompareNoCase(_T("MYSQL")) == 0)
         sqldbtype_ = HM::DatabaseSettings::TypeMYSQLServer;
      else if (sDatabaseType.CompareNoCase(_T("PostgreSQL")) == 0)
         sqldbtype_ = HM::DatabaseSettings::TypePGServer;
      else if (sDatabaseType.CompareNoCase(_T("MSSQLCE")) == 0)
         sqldbtype_ = HM::DatabaseSettings::TypeMSSQLCompactEdition;

      dbport_ = ReadIniSettingInteger_( "Database", "Port", 0);

      app_directory_ = ReadIniSettingString_("Directories", "ProgramFolder", "");
      if (app_directory_.Right(1) != _T("\\"))
         app_directory_ += "\\";

      data_directory_ = ReadIniSettingString_("Directories", "DataFolder", "");
      if (data_directory_.Right(1) == _T("\\"))
         data_directory_ = data_directory_.Left(data_directory_.GetLength() -1);

      temp_directory_ = ReadIniSettingString_("Directories", "TempFolder", "");
      if (temp_directory_.Right(1) == _T("\\"))
         temp_directory_ = temp_directory_.Left(temp_directory_.GetLength() -1);

      event_directory_ = ReadIniSettingString_("Directories", "EventFolder", "");

      dbscript_directory_ = ReadIniSettingString_("Directories", "ProgramFolder", "");
      if (dbscript_directory_.Right(1) != _T("\\"))
         dbscript_directory_ += "\\";
      dbscript_directory_ += "DBScripts";

      no_of_dbconnections_ = ReadIniSettingInteger_("Database", "NumberOfConnections", 5);            
      no_of_dbconnection_attempts_ = ReadIniSettingInteger_("Database", "ConnectionAttempts", 6);  
      no_of_dbconnection_attempts_Delay = ReadIniSettingInteger_("Database", "ConnectionAttemptsDelay", 5);  
      
      if (sqldbtype_ == HM::DatabaseSettings::TypeMSSQLCompactEdition)
      {
         // Always use one database connection when working with SQL CE. SQL CE is supposed
         // to be ACID, robust and so on but isn't really.
         // http://forums.microsoft.com/MSDN/ShowPost.aspx?PostID=4141097&SiteID=1
         no_of_dbconnections_ = 1;
      }

      max_no_of_external_fetch_threads_ = ReadIniSettingInteger_("Settings", "MaxNumberOfExternalFetchThreads", 15);
      add_xauth_user_header_ = ReadIniSettingInteger_("Settings", "AddXAuthUserHeader", 0) == 1;

      daemonaddress_domain_ = ReadIniSettingString_("Settings", "DaemonAddressDomain", "");
      
      greylisting_enabled_during_record_expiration_ = ReadIniSettingInteger_("Settings", "GreylistingEnabledDuringRecordExpiration", 1) == 1;
      greylisting_expiration_interval_ = ReadIniSettingInteger_("Settings", "GreylistingRecordExpirationInterval", 240);

      database_directory_ = ReadIniSettingString_("Directories", "DatabaseFolder", "");
      if (database_directory_.Right(1) == _T("\\"))
         database_directory_ = database_directory_.Left(database_directory_.GetLength() -1);

      String sValidLanguages = ReadIniSettingString_("GUILanguages", "ValidLanguages", "");
      valid_languages_ = StringParser::SplitString(sValidLanguages, ",");

      preferred_hash_algorithm_ = ReadIniSettingInteger_("Settings", "PreferredHashAlgorithm", 4);

      // Minimum password hash scheme an account may use to authenticate. Accounts
      // whose stored hash is weaker than this (Crypt::EncryptionType ordering) are
      // refused. 0 (ETNone) disables the policy and preserves prior behaviour.
      minimum_accepted_hash_algorithm_ = ReadIniSettingInteger_("Settings", "MinimumAcceptedHashAlgorithm", 0);

      // Optional server-wide secret ("pepper") HMAC-mixed into Argon2id password hashes
      // (see Crypt::EnCrypt/Validate). Empty disables it (no behaviour change). It only
      // affects Argon2id hashes so PBKDF2 stays usable as the SCRAM SaltedPassword.
      password_pepper_ = ReadIniSettingString_("Settings", "PasswordPepper", "");

      // OAuth2 bearer-token authentication (SASL XOAUTH2 / OAUTHBEARER). Disabled by
      // default. When enabled, presented JWT bearer tokens are verified locally against
      // the configured signing key(s); the mechanism is TLS-gated by default.
      oauth2_enabled_ = ReadIniSettingInteger_("Settings", "OAuth2Enabled", 0) == 1;
      oauth2_require_tls_ = ReadIniSettingInteger_("Settings", "OAuth2RequireTLS", 1) == 1;
      // Comma-separated allow-list of accepted JWT "alg" values (e.g. "RS256,HS256").
      // "none" is never accepted regardless of this list.
      oauth2_allowed_algorithms_ = ReadIniSettingString_("Settings", "OAuth2AllowedAlgorithms", "RS256");
      // Shared secret for HS256 verification (HMAC key).
      oauth2_hmac_secret_ = ReadIniSettingString_("Settings", "OAuth2HmacSecret", "");
      // PEM file holding the RSA/EC public key for RS256 verification.
      oauth2_rsa_public_key_file_ = ReadIniSettingString_("Settings", "OAuth2PublicKeyFile", "");
      // Expected token issuer (iss) and audience (aud); empty disables that check.
      oauth2_issuer_ = ReadIniSettingString_("Settings", "OAuth2Issuer", "");
      oauth2_audience_ = ReadIniSettingString_("Settings", "OAuth2Audience", "");
      // Claim that carries the account's e-mail address / login name.
      oauth2_username_claim_ = ReadIniSettingString_("Settings", "OAuth2UsernameClaim", "email");

      // Protect reversible secrets at rest (the database password in this INI plus
      // the DB-stored route/fetch/relayer passwords) with machine-scoped Windows
      // DPAPI so they cannot be decrypted off-box. Enabled by default. Set to 0 to
      // keep the legacy Blowfish scheme (e.g. to allow restoring a backup onto a
      // different machine). Existing legacy values are always still readable.
      protect_stored_secrets_with_dpapi_ = ReadIniSettingInteger_("Settings", "ProtectStoredSecretsWithDPAPI", 1) == 1;

      // Optional least-privilege Windows service account for the hMailServer service.
      // Empty (the default) leaves the service running under LocalSystem, exactly as
      // before. Set ServiceAccountName to run under a dedicated account - the
      // recommended choice is the password-less virtual account "NT SERVICE\hMailServer"
      // (leave ServiceAccountPassword empty for virtual/managed accounts). The chosen
      // account must be granted "Log on as a service" plus access to the hMailServer
      // program, data and database directories. Takes effect when the service is
      // (re)registered.
      service_account_name_ = ReadIniSettingString_("Settings", "ServiceAccountName", "");
      service_account_password_ = ReadIniSettingString_("Settings", "ServiceAccountPassword", "");

      dnsbl_checks_after_mail_from_ = ReadIniSettingInteger_("Settings", "DNSBLChecksAfterMailFrom", 1) == 1;

      sep_svc_logs_ = ReadIniSettingInteger_("Settings", "SepSvcLogs", 0) == 1;
      log_level_ = ReadIniSettingInteger_("Settings", "LogLevel", 9);
      max_log_line_len_ = ReadIniSettingInteger_("Settings", "MaxLogLineLen", 500);
      if (max_log_line_len_ < 100) max_log_line_len_ = 100;
      quick_retries_ = ReadIniSettingInteger_("Settings", "QuickRetries", 0);
      quick_retries_Minutes = ReadIniSettingInteger_("Settings", "QuickRetriesMinutes", 6);
      queue_randomness_minutes_ = ReadIniSettingInteger_("Settings", "QueueRandomnessMinutes", 0);
      // If queue_randomness_minutes_ out of range use 0 
      if (queue_randomness_minutes_ <= 0) queue_randomness_minutes_ = 0;
      mxtries_factor_ = ReadIniSettingInteger_("Settings", "MXTriesFactor", 0);
      if (mxtries_factor_ <= 0) mxtries_factor_ = 0;
      archive_dir_ = ReadIniSettingString_("Settings", "ArchiveDir", "");
      if (archive_dir_.Right(1) == _T("\\"))
         archive_dir_ = archive_dir_.Left(archive_dir_.GetLength() -1);
      archive_hardlinks_ =  ReadIniSettingInteger_("Settings", "ArchiveHardLinks", 0) == 1;
      pop3dmin_timeout_ =  ReadIniSettingInteger_("Settings", "POP3DMinTimeout", 10);
      pop3dmax_timeout_ =  ReadIniSettingInteger_("Settings", "POP3DMaxTimeout",600);
      pop3cmin_timeout_ =  ReadIniSettingInteger_("Settings", "POP3CMinTimeout", 30);
      pop3cmax_timeout_ =  ReadIniSettingInteger_("Settings", "POP3CMaxTimeout",900);
      smtpdmin_timeout_ =  ReadIniSettingInteger_("Settings", "SMTPDMinTimeout", 10);
      smtpdmax_timeout_ =  ReadIniSettingInteger_("Settings", "SMTPDMaxTimeout",1800);
      smtpcmin_timeout_ =  ReadIniSettingInteger_("Settings", "SMTPCMinTimeout", 30);
      smtpcmax_timeout_ =  ReadIniSettingInteger_("Settings", "SMTPCMaxTimeout",600);
      samin_timeout_ =  ReadIniSettingInteger_("Settings", "SAMinTimeout", 30);
      samax_timeout_ =  ReadIniSettingInteger_("Settings", "SAMaxTimeout",90);
      // Upper bound, in seconds, on the accept/save work that runs after end-of-data
      // and holds the thread that sends the "250". Past this the message is refused
      // with a temporary 451 so the sending MTA retries, rather than the reply
      // stalling past the sender's own timeout (discussion #18). 0 disables it.
      // Default 240s: comfortably under Postfix's 600s data-done timeout.
      finalization_timeout_ =  ReadIniSettingInteger_("Settings", "FinalizationTimeout", 240);
      clam_min_timeout_ =  ReadIniSettingInteger_("Settings", "ClamMinTimeout", 15);
      clam_max_timeout_ =  ReadIniSettingInteger_("Settings", "ClamMaxTimeout",90);

      // Bounds on operations that would otherwise wait forever and, because they
      // run on bounded thread pools, take the whole server down with them when
      // enough of them pile up. Each is in seconds and 0 disables that bound.
      //
      // A DNS query the OS resolver never answers; a database connection that
      // never becomes free; an administrator's event script that loops or blocks
      // in a COM call; an external virus scanner that hangs. All four were
      // unbounded, and all four sit on a path that a remote sender can drive.
      dns_query_timeout_ =  ReadIniSettingInteger_("Settings", "DNSQueryTimeout", 10);

      // Off by default, unlike its siblings. Bounding the wait is only half the
      // job: a caller has to be able to tell "the database was busy" from the
      // answer it would otherwise have got, and not every caller can yet. In
      // particular the recipient lookup behind RCPT TO reports a failed lookup as
      // "no such user", so a bounded wait that expired during a backup would
      // reject a valid recipient with a permanent 550. Until those callers
      // distinguish the two, blocking is the safer failure.
      db_connection_acquire_timeout_ =  ReadIniSettingInteger_("Settings", "DBConnectionAcquireTimeout", 0);
      script_timeout_ =  ReadIniSettingInteger_("Settings", "ScriptTimeout", 60);
      external_process_timeout_ =  ReadIniSettingInteger_("Settings", "ExternalProcessTimeout", 300);

      // Async work-queue health. The stall threshold is how long every worker may
      // be busy before the server reports which tasks are holding them; the
      // reserved count keeps that many threads out of the reach of scanning and
      // scripting so short work (saving an accepted message) always has one.
      async_queue_stall_threshold_ =  ReadIniSettingInteger_("Settings", "AsyncQueueStallThreshold", 120);
      async_queue_reserved_threads_ =  ReadIniSettingInteger_("Settings", "AsyncQueueReservedThreads", 2);
      samove_vs_copy_ = ReadIniSettingInteger_("Settings", "SAMoveVsCopy", 0) == 1;
      auth_user_replacement_ip_ = ReadIniSettingString_("Settings", "AuthUserReplacementIP", "");
      indexer_full_minutes_ =  ReadIniSettingInteger_("Settings", "IndexerFullMinutes",720);
      indexer_full_limit_ =  ReadIniSettingInteger_("Settings", "IndexerFullLimit",25000);
      indexer_quick_limit_ =  ReadIniSettingInteger_("Settings", "IndexerQuickLimit",1000);
      load_header_read_size_ =  ReadIniSettingInteger_("Settings", "LoadHeaderReadSize",4000);
      load_body_read_size_ =  ReadIniSettingInteger_("Settings", "LoadBodyReadSize",4000);
      blocked_iphold_seconds_ =  ReadIniSettingInteger_("Settings", "BlockedIPHoldSeconds",0);
      smtpdmax_size_drop_ =  ReadIniSettingInteger_("Settings", "SMTPDMaxSizeDrop",0);
      backup_messages_dbonly_ =  ReadIniSettingInteger_("Settings", "BackupMessagesDBOnly",0) == 1;
      add_xauth_user_ip_ =  ReadIniSettingInteger_("Settings", "AddXAuthUserIP",1) == 1;
      add_xoriginal_rcpt_to_header_ = ReadIniSettingInteger_("Settings", "AddXOriginalRcptTo", 0) == 1;
      use_dns_cache_ = ReadIniSettingInteger_("Settings", "UseDNSCache", 1) == 1;
      dns_server_ = ReadIniSettingString_("Settings", "DNSServer", "");
      mta_sts_enabled_ = ReadIniSettingInteger_("Settings", "MtaStsEnabled", 1) == 1;
      dane_enabled_ = ReadIniSettingInteger_("Settings", "DaneEnforcementEnabled", 1) == 1;
      dnssec_validation_enabled_ = ReadIniSettingInteger_("Settings", "DnssecValidationEnabled", 1) == 1;
      dnssec_trust_anchors_ = ReadIniSettingString_("Settings", "DnssecTrustAnchors", "");
      json_logging_ = ReadIniSettingInteger_("Settings", "JsonLogging", 0) == 1;
      log_delete_days_ = ReadIniSettingInteger_("Settings", "LogDeleteDays", 0);
      shutdown_drain_seconds_ = ReadIniSettingInteger_("Settings", "ShutdownDrainSeconds", 0);
      slow_query_log_ms_ = ReadIniSettingInteger_("Settings", "SlowQueryLogMilliseconds", 0);
      message_store_fsync_ = ReadIniSettingInteger_("Settings", "MessageStoreFsync", 0) == 1;
      message_store_consistency_check_ = ReadIniSettingInteger_("Settings", "MessageStoreConsistencyCheck", 0) == 1;
      metrics_server_port_ = ReadIniSettingInteger_("Settings", "MetricsServerPort", 0);
      metrics_server_bind_address_ = ReadIniSettingString_("Settings", "MetricsServerBindAddress", "127.0.0.1");
      otel_endpoint_ = ReadIniSettingString_("Settings", "OtelEndpoint", "");
      otel_service_name_ = ReadIniSettingString_("Settings", "OtelServiceName", "hmailserver");
      manage_sieve_server_port_ = ReadIniSettingInteger_("Settings", "ManageSieveServerPort", 0);
      manage_sieve_server_bind_address_ = ReadIniSettingString_("Settings", "ManageSieveServerBindAddress", "127.0.0.1");
      arc_sealing_enabled_ = ReadIniSettingInteger_("Settings", "ArcSealingEnabled", 0) == 1;
      tls_rpt_from_address_ = ReadIniSettingString_("Settings", "TlsRptFromAddress", "");
      tls_rpt_organization_name_ = ReadIniSettingString_("Settings", "TlsRptOrganizationName", "hMailServer");
      rest_api_port_ = ReadIniSettingInteger_("Settings", "RestApiPort", 0);
      rest_api_bind_address_ = ReadIniSettingString_("Settings", "RestApiBindAddress", "127.0.0.1");
      rest_api_certificate_file_ = ReadIniSettingString_("Settings", "RestApiCertificateFile", "");
      rest_api_private_key_file_ = ReadIniSettingString_("Settings", "RestApiPrivateKeyFile", "");
      acme_enabled_ = ReadIniSettingInteger_("Settings", "AcmeEnabled", 0) == 1;
      acme_directory_url_ = ReadIniSettingString_("Settings", "AcmeDirectoryUrl", "https://acme-v02.api.letsencrypt.org/directory");
      acme_contact_email_ = ReadIniSettingString_("Settings", "AcmeContactEmail", "");
      acme_domains_ = ReadIniSettingString_("Settings", "AcmeDomains", "");
      acme_certificate_directory_ = ReadIniSettingString_("Settings", "AcmeCertificateDirectory", "");
      acme_http_port_ = ReadIniSettingInteger_("Settings", "AcmeHttpPort", 80);
      acme_reuse_key_ = ReadIniSettingInteger_("Settings", "AcmeReuseKey", 1) == 1;
      web_services_http_port_ = ReadIniSettingInteger_("Settings", "WebServicesHttpPort", 0);
      web_services_https_port_ = ReadIniSettingInteger_("Settings", "WebServicesHttpsPort", 0);
      web_services_bind_address_ = ReadIniSettingString_("Settings", "WebServicesBindAddress", "0.0.0.0");
      web_services_certificate_file_ = ReadIniSettingString_("Settings", "WebServicesCertificateFile", "");
      web_services_private_key_file_ = ReadIniSettingString_("Settings", "WebServicesPrivateKeyFile", "");
      mta_sts_hosting_enabled_ = ReadIniSettingInteger_("Settings", "MtaStsHostingEnabled", 1) == 1;
      mta_sts_policy_mode_ = ReadIniSettingString_("Settings", "MtaStsPolicyMode", "enforce");
      mta_sts_policy_max_age_ = ReadIniSettingInteger_("Settings", "MtaStsPolicyMaxAge", 604800);
      mta_sts_policy_mx_ = ReadIniSettingString_("Settings", "MtaStsPolicyMx", "");
      autoconfig_enabled_ = ReadIniSettingInteger_("Settings", "AutoconfigEnabled", 1) == 1;
      autoconfig_client_host_ = ReadIniSettingString_("Settings", "AutoconfigClientHost", "");
      rewrite_envelope_from_when_forwarding_ = ReadIniSettingInteger_("Settings", "RewriteEnvelopeFromWhenForwarding", 0) == 1;
      srs_enabled_ = ReadIniSettingInteger_("Settings", "SRSEnabled", 0) == 1;
      srs_secret_ = ReadIniSettingString_("Settings", "SRSSecret", "");
      batv_enabled_ = ReadIniSettingInteger_("Settings", "BATVEnabled", 0) == 1;
      batv_secret_ = ReadIniSettingString_("Settings", "BATVSecret", "");
      max_submissions_per_ip_per_minute_ = ReadIniSettingInteger_("Settings", "MaxSubmissionsPerIPPerMinute", 0);
      max_outbound_per_destination_per_minute_ = ReadIniSettingInteger_("Settings", "MaxOutboundPerDestinationPerMinute", 0);
      m_sDisableAUTHList = ReadIniSettingString_("Settings", "DisableAUTHList", "");
   }

   bool 
   IniFileSettings::GetDatabaseSettingsExists()
   {
      if (sqldbtype_ == HM::DatabaseSettings::TypeUnknown)
         return false;

      return true;
   }


   void
   IniFileSettings::WriteIniSetting_(const String &sSection, const String &sKey, const String &sValue)
   {
      WritePrivateProfileString(sSection, sKey, sValue, GetInitializationFile() );
   }

   void
   IniFileSettings::WriteIniSetting_(const String &sSection, const String &sKey, int Value)
   {
      String sValue = StringParser::IntToString(Value);
      WritePrivateProfileString(sSection, sKey, sValue, GetInitializationFile() );
   }

   String 
   IniFileSettings::ReadIniSettingString_(const String &sSection, const String &sKey, const String &sDefault)
   {
      // The buffer must be large enough for the longest value we may store. Most
      // settings are short, but a DPAPI-protected secret (e.g. the database
      // password, OAuth2 HMAC secret or password pepper) is a base64 envelope that
      // easily exceeds 255 characters. A buffer that is too small silently
      // truncates the value, which corrupts the secret on read (a truncated DPAPI
      // blob fails to decrypt and yields an empty password, so the server can no
      // longer connect to its own database). Use a generous buffer to be safe.
      const DWORD nBufferSize = 4096;
      TCHAR Value[nBufferSize];
      GetPrivateProfileString( sSection, sKey, sDefault, Value, nBufferSize, GetInitializationFile() );
      return Value;
   }

   int 
   IniFileSettings::ReadIniSettingInteger_(const String &sSection, const String &sKey, int iDefault)
   {
      int iValue = GetPrivateProfileInt( sSection, sKey, iDefault, GetInitializationFile() );
      return iValue;
   }

   String
   IniFileSettings::GetInitializationFile() 
   {
      if (ini_file_.IsEmpty())
      {
         String AppPath = Utilities::GetBinDirectory();

         ini_file_ = AppPath;

         if (ini_file_.Right(1) != _T("\\"))
            ini_file_ += "\\";

         ini_file_ += "hMailServer.ini";
      }

      return ini_file_;
   }
   
   String
   IniFileSettings::GetLogDirectory() 
   { 
      if (log_directory_.IsEmpty())
      {
         TCHAR Value[255];
         GetPrivateProfileString( _T("Directories"), _T("LogFolder"), _T(""), Value, 255, GetInitializationFile() );
         log_directory_ = Value;
      }

      return log_directory_; 
   }

   String 
   IniFileSettings::GetLanguageDirectory() const
   {
      return app_directory_ + "Languages";
   }

   bool
   IniFileSettings::CheckSettings(String &sErrorMessage)
   {
      String sIniFile = GetInitializationFile();

      String sLog;
      sLog.Format(_T("Configuration::CheckSettings - %s"), sIniFile.c_str());
      LOG_DEBUG(sLog);

      switch (GetDatabaseType())
      {
      case HM::DatabaseSettings::TypeMSSQLCompactEdition:
         {
            if (database_name_.IsEmpty())
            {
               sErrorMessage.Format(_T("The setting Database in the section Database could not be read from %s"), sIniFile.c_str());
               return false;
            }
            break;
         }
      default:
         {
            if (database_server_.IsEmpty())
            {
               sErrorMessage.Format(_T("The setting Server in the section Database could not be read from %s"), sIniFile.c_str());
               return false;
            }
            break;
         }
      }


      return true;
   }

   String 
   IniFileSettings::GetUserInterfaceLanguage()
   {
      TCHAR Value[255];

      GetPrivateProfileString( _T("Settings"), _T("UseLanguage"), _T("English"), Value, 255, GetInitializationFile() );
      return Value;

   }

   void 
   IniFileSettings::SetUserInterfaceLanguage(String sLanguage)
   {
      WritePrivateProfileString(_T("Settings"), _T("UseLanguage"), sLanguage, GetInitializationFile());
   }

   int 
   IniFileSettings::GetNumberOfDatabaseConnections() const
   {
      return no_of_dbconnections_;
   }

   int 
   IniFileSettings::GetNumberOfDatabaseConnectionAttempts() const
   {
      return no_of_dbconnection_attempts_;
   }

   int 
   IniFileSettings::GetDBConnectionAttemptsDelay() const
   {
      return no_of_dbconnection_attempts_Delay;
   }


   String 
   IniFileSettings::GetAdministratorPassword()
   {
      return administrator_password_;
   }

   void 
   IniFileSettings::SetAdministratorPassword(const String &sNewPassword)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates the main hMailServer administration password found in hMailServer.ini
   //---------------------------------------------------------------------------()
   {
      administrator_password_ = HM::Crypt::Instance()->EnCrypt(sNewPassword, HM::Crypt::ETPBKDF2);

      WriteIniSetting_("Security", "AdministratorPassword", administrator_password_);
   }

   void 
   IniFileSettings::SetProgramDirectory(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates the main hMailServer administration password found in hMailServer.ini
   //---------------------------------------------------------------------------()
   {
      app_directory_ = sNewVal;
      WriteIniSetting_("Directories", "ProgramFolder", app_directory_);
   }

   void 
   IniFileSettings::SetDataDirectory(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      data_directory_ = sNewVal;
      WriteIniSetting_("Directories", "DataFolder", data_directory_);
   }

   void 
   IniFileSettings::SetTempDirectory(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      temp_directory_ = sNewVal;
      WriteIniSetting_("Directories", "TempFolder", temp_directory_);
   }

   void 
   IniFileSettings::SetEventDirectory(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      event_directory_ = sNewVal;
      WriteIniSetting_("Directories", "EventFolder", event_directory_);
   }

   void 
   IniFileSettings::SetDatabaseDirectory(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      database_directory_ = sNewVal;
      WriteIniSetting_("Directories", "DatabaseFolder", database_directory_);
   }

   void 
   IniFileSettings::SetLogDirectory(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      log_directory_ = sNewVal;
      WriteIniSetting_("Directories", "LogFolder", log_directory_);
   }

   void 
   IniFileSettings::SetDatabaseServer(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      database_server_ = sNewVal;
      WriteIniSetting_("Database", "Server", database_server_);
   }

   void 
   IniFileSettings::SetDatabaseName(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      database_name_ = sNewVal;
      WriteIniSetting_("Database", "Database", database_name_);
   }

   void 
   IniFileSettings::SetUsername(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      username_ = sNewVal;
      WriteIniSetting_("Database", "Username", username_);
   }

   void 
   IniFileSettings::SetPassword(const String &sNewVal)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      password_ = sNewVal;

      // Prefer machine-scoped DPAPI so the database password in hMailServer.ini
      // cannot be decrypted off-box. Fall back to the legacy Blowfish scheme when
      // DPAPI is disabled or unavailable (so a non-empty password is never lost).
      if (protect_stored_secrets_with_dpapi_)
      {
         String protectedValue = Crypt::Instance()->EnCrypt(password_, Crypt::ETDPAPI);
         if (!protectedValue.IsEmpty() || password_.IsEmpty())
         {
            WriteIniSetting_("Database", "Password", protectedValue);
            WriteIniSetting_("Database", "PasswordEncryption", Crypt::ETDPAPI);
            return;
         }
      }

      WriteIniSetting_("Database", "Password", Crypt::Instance()->EnCrypt(password_, Crypt::ETBlowFish));
      WriteIniSetting_("Database", "PasswordEncryption", Crypt::ETBlowFish);
   }

   void 
   IniFileSettings::SetDatabaseType(HM::DatabaseSettings::SQLDBType type)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      String sDatabaseType;
      switch (type)
      {
      case HM::DatabaseSettings::TypeMSSQLServer:
          sDatabaseType = _T("MSSQL");
          break;
      case HM::DatabaseSettings::TypeMYSQLServer:
         sDatabaseType = _T("MYSQL");
         break;
      case HM::DatabaseSettings::TypePGServer:
         sDatabaseType = _T("PostgreSQL");
         break;
      case HM::DatabaseSettings::TypeMSSQLCompactEdition:
         sDatabaseType = _T("MSSQLCE");
         break;
      default:
         return;
      }

      LOG_DEBUG("Setting database type to " + sDatabaseType);
      sqldbtype_ = type;

      WriteIniSetting_("Database", "Type", sDatabaseType);
   }

   void 
   IniFileSettings::SetDatabasePort(long lNewValue)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      dbport_ = lNewValue;
      WriteIniSetting_("Database", "Port", dbport_);
   }

   void 
   IniFileSettings::SetIsInternalDatabase(bool newValue)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Updates a directory in hMailServer.ini.
   //---------------------------------------------------------------------------()
   {
      is_internal_database_ = newValue;

      WriteIniSetting_("Database", "Internal", is_internal_database_ ? 1 : 0);
   }

   void
   IniFileSettings::SetRewriteEnvelopeFromWhenForwarding(bool value)
   {
      rewrite_envelope_from_when_forwarding_ = value;
      WriteIniSetting_("Settings", "RewriteEnvelopeFromWhenForwarding", value ? 1 : 0);
   }

   String
   IniFileSettings::GetBinDirectory()
   {
      return FileUtilities::Combine(app_directory_, "Bin");
   }

   std::set<int> 
   IniFileSettings::GetAuthDisabledOnPorts()
   {
      if (m_sDisableAUTHList.IsEmpty())
      {
         std::set<int> empty;
         return empty;
      }

      std::vector<String> authDisabledOnPortsStr = StringParser::SplitString(m_sDisableAUTHList, ",");

      std::set<int> authDisabledOnPorts;

      for (AnsiString port : authDisabledOnPortsStr)
      {
         authDisabledOnPorts.insert(atoi(port));
      }

      return authDisabledOnPorts;
   }
}
