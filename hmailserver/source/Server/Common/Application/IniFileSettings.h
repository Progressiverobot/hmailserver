// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../SQL/DatabaseSettings.h"

namespace HM
{
   class IniFileSettings : public Singleton<IniFileSettings>
   {
   public:

	   IniFileSettings();
	   virtual ~IniFileSettings();

      void LoadSettings();

      bool CheckSettings(String &sErrorMessage);

      static String GetInitializationFile();

      bool GetDatabaseSettingsExists();

      String GetDatabaseProvider() const { return database_provider_; }
      String GetDatabaseServer() const { return database_server_; }
      String GetDatabaseName() const { return database_name_; }
      String GetUsername() const { return username_; }
      String GetPassword() const { return password_; }
      DatabaseSettings::SQLDBType GetDatabaseType() const { return sqldbtype_; }
      long GetDatabasePort() const { return dbport_;}
      bool GetIsInternalDatabase() const {return is_internal_database_; }

      void SetDatabaseServer(const String &sNewValue);
      void SetDatabaseName(const String &sNewValue);
      void SetUsername(const String &sNewValue);
      void SetPassword(const String &sNewValue);
      void SetDatabaseType(DatabaseSettings::SQLDBType type);
      void SetDatabasePort(long lNewValue);
      void SetIsInternalDatabase(bool newValue);

      void SetAdministratorPassword(const String &sNewPassword);
      String GetAdministratorPassword();
      String GetLogDirectory();

      String GetBinDirectory();
      String GetProgramDirectory() const { return app_directory_; }
      String GetDataDirectory() const { return data_directory_; }
      String GetTempDirectory() const { return temp_directory_; }
      String GetEventDirectory() const { return event_directory_; }
      String GetDBScriptDirectory() const { return dbscript_directory_; }
      String GetDatabaseDirectory() const { return database_directory_; }
      String GetLanguageDirectory() const;
      String GetDatabaseServerFailoverPartner() const { return database_server_FailoverPartner; }

      void SetProgramDirectory(const String &sNewVal);
      void SetDataDirectory(const String &sNewVal);
      void SetTempDirectory(const String &sNewVal);
      void SetEventDirectory(const String &sNewVal);
      void SetDatabaseDirectory(const String &sNewVal);
      void SetLogDirectory(const String &sLogDirectory);

      
      std::vector<String> GetValidLanguages() const {return valid_languages_; }

      String GetUserInterfaceLanguage();
      void SetUserInterfaceLanguage(String sLanguage);

      int GetNumberOfDatabaseConnections() const;
      int GetNumberOfDatabaseConnectionAttempts() const;
      int GetDBConnectionAttemptsDelay() const;
      
      bool GetAddXAuthUserHeader() {return add_xauth_user_header_; }
      String GetDaemonAddressDomain() const { return daemonaddress_domain_; }
      bool GetAddXOriginalRcptToHeader() { return add_xoriginal_rcpt_to_header_; }	  
      int GetMaxNumberOfExternalFetchThreads() {return max_no_of_external_fetch_threads_ ;}
      bool GetGreylistingEnabledDuringRecordExpiration() {return greylisting_enabled_during_record_expiration_;}
      int GetGreylistingExpirationInterval() {return greylisting_expiration_interval_; }
      int GetPreferredHashAlgorithm() {return preferred_hash_algorithm_;}
      int GetMinimumAcceptedHashAlgorithm() {return minimum_accepted_hash_algorithm_;}
      bool GetDNSBLChecksAfterMailFrom() {return dnsbl_checks_after_mail_from_; }
      bool GetSepSvcLogs() {return sep_svc_logs_; }
      int GetLogLevel() {return log_level_; }
      int GetMaxLogLineLen() {return max_log_line_len_; }
      int GetQuickRetries() {return quick_retries_; }
      int GetQuickRetriesMinutes() {return quick_retries_Minutes; }
      int GetQueueRandomnessMinutes () {return queue_randomness_minutes_; }
      int GetMXTriesFactor () {return mxtries_factor_; }
      String GetArchiveDir() const { return archive_dir_; }
      bool GetArchiveHardlinks() const { return archive_hardlinks_; }
      int GetPOP3DMinTimeout () {return pop3dmin_timeout_; }
      int GetPOP3DMaxTimeout () {return pop3dmax_timeout_; }
      int GetPOP3CMinTimeout () {return pop3cmin_timeout_; }
      int GetPOP3CMaxTimeout () {return pop3cmax_timeout_; }
      int GetSMTPDMinTimeout () {return smtpdmin_timeout_; }
      int GetSMTPDMaxTimeout () {return smtpdmax_timeout_; }
      int GetSMTPCMinTimeout () {return smtpcmin_timeout_; }
      int GetSMTPCMaxTimeout () {return smtpcmax_timeout_; }
      int GetSAMinTimeout () {return samin_timeout_; }
      int GetSAMaxTimeout () {return samax_timeout_; }
      int GetClamMinTimeout () {return clam_min_timeout_; }
      int GetClamMaxTimeout () {return clam_max_timeout_; }
      bool GetSAMoveVsCopy() const { return samove_vs_copy_; }
      String GetAuthUserReplacementIP() const { return auth_user_replacement_ip_; }
      int GetIndexerFullMinutes () {return indexer_full_minutes_; }
      int GetIndexerFullLimit () {return indexer_full_limit_; }
      int GetIndexerQuickLimit () {return indexer_quick_limit_; }
      int GetLoadHeaderReadSize () {return load_header_read_size_; }
      int GetLoadBodyReadSize () {return load_body_read_size_; }
      int GetBlockedIPHoldSeconds () {return blocked_iphold_seconds_; }
      int GetSMTPDMaxSizeDrop () {return smtpdmax_size_drop_; }
      bool GetBackupMessagesDBOnly () const { return backup_messages_dbonly_; }
      bool GetAddXAuthUserIP () const { return add_xauth_user_ip_; }
      bool GetRewriteEnvelopeFromWhenForwarding() const { return rewrite_envelope_from_when_forwarding_; }
      void SetRewriteEnvelopeFromWhenForwarding(bool value);
      bool GetUseDNSCache() const { return use_dns_cache_; }
      String GetDNSServer() const { return dns_server_; }

      bool GetMtaStsEnabled() const { return mta_sts_enabled_; }
      bool GetDaneEnabled() const { return dane_enabled_; }
      bool GetDnssecValidationEnabled() const { return dnssec_validation_enabled_; }
      String GetDnssecTrustAnchors() const { return dnssec_trust_anchors_; }
      bool GetJsonLogging() const { return json_logging_; }
      int GetMetricsServerPort() const { return metrics_server_port_; }
      String GetMetricsServerBindAddress() const { return metrics_server_bind_address_; }
      bool GetArcSealingEnabled() const { return arc_sealing_enabled_; }
      String GetTlsRptFromAddress() const { return tls_rpt_from_address_; }
      String GetTlsRptOrganizationName() const { return tls_rpt_organization_name_; }
      int GetRestApiPort() const { return rest_api_port_; }
      String GetRestApiBindAddress() const { return rest_api_bind_address_; }
      String GetRestApiCertificateFile() const { return rest_api_certificate_file_; }
      String GetRestApiPrivateKeyFile() const { return rest_api_private_key_file_; }
      bool GetAcmeEnabled() const { return acme_enabled_; }
      String GetAcmeDirectoryUrl() const { return acme_directory_url_; }
      String GetAcmeContactEmail() const { return acme_contact_email_; }
      String GetAcmeDomains() const { return acme_domains_; }
      String GetAcmeCertificateDirectory() const { return acme_certificate_directory_; }
      int GetAcmeHttpPort() const { return acme_http_port_; }
      bool GetAcmeReuseKey() const { return acme_reuse_key_; }
      int GetWebServicesHttpPort() const { return web_services_http_port_; }
      int GetWebServicesHttpsPort() const { return web_services_https_port_; }
      String GetWebServicesBindAddress() const { return web_services_bind_address_; }
      String GetWebServicesCertificateFile() const { return web_services_certificate_file_; }
      String GetWebServicesPrivateKeyFile() const { return web_services_private_key_file_; }
      bool GetMtaStsHostingEnabled() const { return mta_sts_hosting_enabled_; }
      String GetMtaStsPolicyMode() const { return mta_sts_policy_mode_; }
      int GetMtaStsPolicyMaxAge() const { return mta_sts_policy_max_age_; }
      String GetMtaStsPolicyMx() const { return mta_sts_policy_mx_; }
      bool GetAutoconfigEnabled() const { return autoconfig_enabled_; }
      String GetAutoconfigClientHost() const { return autoconfig_client_host_; }
      std::set<int> GetAuthDisabledOnPorts();

   private:   

      void WriteIniSetting_(const String &sSection, const String &sKey, const String &sValue);
      void WriteIniSetting_(const String &sSection, const String &sKey, int Value);
      String ReadIniSettingString_(const String &sSection, const String &sKey, const String &sDefault);
      int ReadIniSettingInteger_(const String &sSection, const String &sKey, int iDefault);

      String database_server_;
      String database_name_;
      String username_;
      String password_;
      String data_directory_;
      String app_directory_;
      String logs_directory_;
      String temp_directory_;
      String event_directory_;
      String dbscript_directory_;
      String database_directory_;
      String database_server_FailoverPartner;
      String administrator_password_;

      std::vector<String> valid_languages_;

      DatabaseSettings::SQLDBType sqldbtype_;
      long dbport_;

      int no_of_dbconnections_;
      int no_of_dbconnection_attempts_;
      int no_of_dbconnection_attempts_Delay;
      bool add_xauth_user_header_;
      String daemonaddress_domain_;
      bool add_xoriginal_rcpt_to_header_;	  
      int max_no_of_external_fetch_threads_;

      bool greylisting_enabled_during_record_expiration_;
      bool is_internal_database_;
      int greylisting_expiration_interval_;
      
      int preferred_hash_algorithm_;

      int minimum_accepted_hash_algorithm_;

      String log_directory_;
      static String ini_file_;

      bool dnsbl_checks_after_mail_from_;
      bool sep_svc_logs_;
      int log_level_;
      int max_log_line_len_;
      int quick_retries_;
      int quick_retries_Minutes;
      int queue_randomness_minutes_;
      int mxtries_factor_;
      String archive_dir_;
      bool archive_hardlinks_;
      int pop3dmin_timeout_;
      int pop3dmax_timeout_;
      int pop3cmin_timeout_;
      int pop3cmax_timeout_;
      int smtpdmin_timeout_;
      int smtpdmax_timeout_;
      int smtpcmin_timeout_;
      int smtpcmax_timeout_;
      int samin_timeout_;
      int samax_timeout_;
      int clam_min_timeout_;
      int clam_max_timeout_;
      bool samove_vs_copy_;
      String auth_user_replacement_ip_;
      int indexer_full_minutes_;
      int indexer_full_limit_;
      int indexer_quick_limit_;
      int load_header_read_size_;
      int load_body_read_size_;
      int blocked_iphold_seconds_;
      int smtpdmax_size_drop_;
      bool backup_messages_dbonly_;
      bool add_xauth_user_ip_;
      bool rewrite_envelope_from_when_forwarding_;
      bool use_dns_cache_;
      String dns_server_;

      bool mta_sts_enabled_ = true;
      bool dane_enabled_ = true;
      bool dnssec_validation_enabled_ = true;
      String dnssec_trust_anchors_;
      bool json_logging_ = false;
      int metrics_server_port_ = 0;
      String metrics_server_bind_address_;
      bool arc_sealing_enabled_ = false;
      String tls_rpt_from_address_;
      String tls_rpt_organization_name_;
      int rest_api_port_ = 0;
      String rest_api_bind_address_;
      String rest_api_certificate_file_;
      String rest_api_private_key_file_;
      bool acme_enabled_ = false;
      String acme_directory_url_;
      String acme_contact_email_;
      String acme_domains_;
      String acme_certificate_directory_;
      int acme_http_port_ = 80;
      bool acme_reuse_key_ = true;
      int web_services_http_port_ = 0;
      int web_services_https_port_ = 0;
      String web_services_bind_address_;
      String web_services_certificate_file_;
      String web_services_private_key_file_;
      bool mta_sts_hosting_enabled_ = true;
      String mta_sts_policy_mode_;
      int mta_sts_policy_max_age_ = 604800;
      String mta_sts_policy_mx_;
      bool autoconfig_enabled_ = true;
      String autoconfig_client_host_;
      String database_provider_;

      String m_sDisableAUTHList;
   };
}
