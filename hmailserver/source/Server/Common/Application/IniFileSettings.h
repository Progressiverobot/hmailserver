// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
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

      /// <summary>
      /// Reconciles the [Settings] section with the hm_inisettings table and makes
      /// the result the source for the next LoadSettings(). Call once, after the
      /// database is open AND its schema version has been accepted - the table only
      /// exists at schema 6011, and querying it on an older database would replace
      /// the clear "run DBUpdater" message with a SQL error.
      ///
      /// Nothing else needs to change to benefit: the values are reconciled INTO the
      /// ini file as well as out of it, so every reader that goes to the file
      /// directly - the two DAV redirects in WebServicesServer, UseLanguage, the
      /// Control Panel, hmconfig.ps1, hMailServer.exe /Register, which runs with no
      /// database at all - keeps seeing the right value.
      /// </summary>
      void LoadDatabaseSettings();

      /// <summary>
      /// Forgets the reconciled values, so the next LoadSettings() reads the file
      /// alone. Used when the process is torn down and rebuilt in place
      /// (Application::Reinitialize), where the previous cycle's map would otherwise
      /// outlive the database connection it came from.
      /// </summary>
      void ForgetDatabaseSettings();

      /// <summary>
      /// Records a [Settings] value in the database as well as in the file, so a
      /// change made through the COM API is not lost from the mirror - and therefore
      /// from the backup - until the next restart. Silent no-op before
      /// LoadDatabaseSettings has run.
      /// </summary>
      void SaveDatabaseSetting(const String &key, const String &value);

      // ---- the administrative surface behind the COM API --------------------
      //
      // These are what let a Control Panel on another machine administer the
      // [Settings] section at all. Before them, IniFeatureStore had to open the file
      // itself, which only works when it is running on the server - which is why the
      // log-folder card has to say "not readable from this machine".
      //
      // None of them makes the value take effect in the running process. Almost
      // every one of these settings is latched into a typed member by LoadSettings()
      // at start-up, and re-running that here would rewrite around 150 members
      // underneath live sessions. Persist now, apply on the next start - the same
      // contract an administrator editing the file by hand has always had, and the
      // one the Control Panel's "applies after a service restart" wording already
      // describes.

      /// <summary>
      /// Reads one [Settings] value as it stands right now - through the reconciled
      /// map when one has been loaded, and from the file otherwise.
      /// </summary>
      String GetSettingsValue(const String &key);

      /// <summary>
      /// Writes one [Settings] value to the file and mirrors it. False means the FILE
      /// could not be written and nothing was changed; see IniSettingStore::WriteSetting
      /// for why that ordering matters.
      /// </summary>
      bool WriteSettingsValue(const String &key, const String &value);

      /// <summary>
      /// Removes one [Settings] key, returning the setting to its default, and drops
      /// its row to match. Deliberately distinct from writing an empty string: an
      /// absent key falls back to the caller's default, while "Key=" reads as 0
      /// through GetPrivateProfileInt.
      /// </summary>
      bool RemoveSettingsValue(const String &key);

      /// <summary>Every [Settings] name currently in the file.</summary>
      void GetSettingsNames(std::vector<String> &names);

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
      String GetPasswordPepper() {return password_pepper_;}
      bool GetOAuth2Enabled() {return oauth2_enabled_;}
      bool GetOAuth2RequireTLS() {return oauth2_require_tls_;}
      String GetOAuth2AllowedAlgorithms() {return oauth2_allowed_algorithms_;}
      String GetOAuth2HmacSecret() {return oauth2_hmac_secret_;}
      String GetOAuth2RsaPublicKeyFile() {return oauth2_rsa_public_key_file_;}
      String GetOAuth2Issuer() {return oauth2_issuer_;}

      // Outbound XOAUTH2 (relaying through a provider that has switched Basic
      // auth off, Microsoft 365 first among them). TokenUrl/ClientId/Secret/
      // Scope drive the client_credentials fetch; Hosts is the comma-separated
      // list of relay hosts the token is presented to; FixedToken, when set, is
      // used verbatim instead of fetching - for tokens obtained outside this
      // server, and for tests.
      String GetOutboundOAuth2TokenUrl() {return outbound_oauth2_token_url_;}
      String GetOutboundOAuth2ClientId() {return outbound_oauth2_client_id_;}
      String GetOutboundOAuth2ClientSecret() {return outbound_oauth2_client_secret_;}
      String GetOutboundOAuth2Scope() {return outbound_oauth2_scope_;}
      String GetOutboundOAuth2Hosts() {return outbound_oauth2_hosts_;}
      String GetOutboundOAuth2FixedToken() {return outbound_oauth2_fixed_token_;}

      // The POP3-fetch counterpart of OutboundOAuth2Hosts: external accounts
      // whose server is listed here authenticate with XOAUTH2 using the same
      // token client and credentials as the outbound relay.
      String GetFetchOAuth2Hosts() {return fetch_oauth2_hosts_;}
      String GetOAuth2Audience() {return oauth2_audience_;}
      String GetOAuth2UsernameClaim() {return oauth2_username_claim_;}
      bool GetProtectStoredSecretsWithDPAPI() {return protect_stored_secrets_with_dpapi_;}
      String GetServiceAccountName() {return service_account_name_;}
      String GetServiceAccountPassword() {return service_account_password_;}
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
      int GetFinalizationTimeout () {return finalization_timeout_; }
      int GetClamMinTimeout () {return clam_min_timeout_; }
      int GetClamMaxTimeout () {return clam_max_timeout_; }

      // Seconds; 0 disables the bound. See the comment at the read site.
      int GetDNSQueryTimeout () {return dns_query_timeout_; }
      int GetClientSessionCeiling () {return client_session_ceiling_; }
      int GetDBConnectionAcquireTimeout () {return db_connection_acquire_timeout_; }
      int GetScriptTimeout () {return script_timeout_; }
      int GetExternalProcessTimeout () {return external_process_timeout_; }
      int GetAsyncQueueStallThreshold () {return async_queue_stall_threshold_; }
      int GetAsyncQueueReservedThreads () {return async_queue_reserved_threads_; }

      // Absolute ceilings on a single IMAP SEARCH / SORT, measured from the start
      // of the search. Seconds and megabytes-of-message-content respectively; 0
      // disables that half. See the comment at the read site for the defaults and
      // for why there are two of them.
      int GetIMAPSearchTimeout () {return imap_search_timeout_; }
      int GetIMAPSearchMaxMegabytes () {return imap_search_max_megabytes_; }
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
      // SRS (Sender Rewriting Scheme) for forwarded mail (SPF alignment).
      bool GetSRSEnabled() const { return srs_enabled_; }
      String GetSRSSecret() const { return srs_secret_; }
      // BATV (Bounce Address Tag Validation, prvs) for backscatter protection.
      bool GetBATVEnabled() const { return batv_enabled_; }
      String GetBATVSecret() const { return batv_secret_; }
      // Rate shaping (0 = unlimited / disabled).
      int GetMaxSubmissionsPerIPPerMinute() const { return max_submissions_per_ip_per_minute_; }
      int GetMaxOutboundPerDestinationPerMinute() const { return max_outbound_per_destination_per_minute_; }
      bool GetUseDNSCache() const { return use_dns_cache_; }
      String GetDNSServer() const { return dns_server_; }

      bool GetMtaStsEnabled() const { return mta_sts_enabled_; }
      bool GetDaneEnabled() const { return dane_enabled_; }
      bool GetDnssecValidationEnabled() const { return dnssec_validation_enabled_; }
      String GetDnssecTrustAnchors() const { return dnssec_trust_anchors_; }
      bool GetJsonLogging() const { return json_logging_; }
      int GetLogDeleteDays() const { return log_delete_days_; }
      int GetShutdownDrainSeconds() const { return shutdown_drain_seconds_; }
      // Statements that take at least this many milliseconds are counted as slow and
      // logged (redacted). 0 disables the slow-query log (latency metrics still run).
      int GetSlowQueryLogMilliseconds() const { return slow_query_log_ms_; }
      bool GetMessageStoreFsync() const { return message_store_fsync_; }

      // Fault injection, for the regression suite only: makes writes of received message
      // data to the spool file report failure without writing anything.
      //
      //   0  off.
      //   1  every write fails, so the spool file stays empty.
      //   2  the first write succeeds and every write after it fails, so the spool file
      //      is left non-empty and truncated.
      //
      // Two modes because the two shapes are caught by different things, and only one of
      // them was ever dangerous. An empty spool file was already refused before this
      // work - not by any check that meant to catch it, but because the accept path
      // failed downstream on a message with no content, with nothing reported. A
      // *truncated* file is the shape a disk that fills up mid-message actually has, and
      // it passes every downstream guard: it has content, it has headers, its size is
      // non-zero. That one was accepted with a 250 and delivered short, which is the
      // defect. Mode 2 is therefore the mode that matters, and the one whose test fails
      // against the unfixed build.
      //
      // It exists because the alternative is filling a real disk, which is not a test.
      // CrashSimulationMode is the existing precedent for a switch of this kind.
      // Deliberately not exposed in the Control Panel, and read from the ini rather than
      // over COM, so that turning it on takes a deliberate edit plus a reinitialize.
      int GetSimulateSpoolWriteFailure() const { return simulate_spool_write_failure_; }

      // Test-only fault injection for the database layer, and the reason it exists is
      // worth stating: three sweeps in August 2026 fixed 51 places where the result of
      // a database write was discarded, and every one of those defects lived in error
      // handling that had never executed. Adding 51 more error branches that also never
      // execute repeats the mistake at one remove. This is what lets a test make one
      // specific statement fail so the branch actually runs.
      //
      // A substring of the statement rather than a switch, so a test can name exactly
      // the write it wants to break - "update hm_imapfolders set foldercurrentuid" -
      // and every other statement on every other thread carries on normally. That
      // precision is what makes it usable at all on a running server with delivery
      // threads working.
      //
      // Empty out of the box, which is the whole of its safety: the check is one
      // IsEmpty() per statement, it can only be turned on by editing hMailServer.ini
      // and reinitialising, and Application::Reinitialize reports HM6119 whenever it
      // is set - because a fault injector that is silently on is far worse than not
      // having one. Follows SimulateSpoolWriteFailure and CrashSimulationMode, which
      // are the same idea for the disk and the crash handler.
      // Asked before every single database statement, so it must cost nothing when the
      // facility is off. Every String getter in this class returns by value, which is
      // fine for settings read once at startup and quite wrong for one read per
      // statement - a heap copy per query is not a price a mail server should pay for a
      // test hook. Hence the bool: the string is only fetched once it says yes.
      bool GetSimulateDatabaseFailureEnabled() const { return simulate_database_failure_enabled_; }

      String GetSimulateDatabaseFailureFor() const { return simulate_database_failure_for_; }
      bool GetMessageStoreConsistencyCheck() const { return message_store_consistency_check_; }
      int GetMetricsServerPort() const { return metrics_server_port_; }
      String GetMetricsServerBindAddress() const { return metrics_server_bind_address_; }

      // Access control on the metrics listener. All five are absent by default, so a
      // loopback scrape behaves exactly as it always has, and /livez, /readyz and
      // /healthz are never authenticated whatever these say - a load balancer cannot
      // present a credential and a probe cannot hold a secret.
      //
      // A credential is REQUIRED for /metrics when the bind address is not loopback:
      // without one /metrics answers 503 and names these settings, while the listener
      // and the probes keep serving. Refusing to start instead would have taken the
      // probes down with the exposition, which is the configuration
      // docs/HighAvailabilityRunbook.md section 3 actually ships.
      String GetMetricsServerAuthToken() const { return metrics_server_auth_token_; }
      String GetMetricsServerAuthUsername() const { return metrics_server_auth_username_; }
      String GetMetricsServerAuthPassword() const { return metrics_server_auth_password_; }

      // Both must be set to serve HTTPS on the metrics port. Never inferred: a
      // non-loopback bind without TLS is warned about in the application log and served,
      // because a monitoring endpoint that silently stops answering is worse than one
      // that answers over plain HTTP on a network the operator chose.
      String GetMetricsServerCertificateFile() const { return metrics_server_certificate_file_; }
      String GetMetricsServerPrivateKeyFile() const { return metrics_server_private_key_file_; }

      // Scheduled backups. Absent means the task is never constructed and no archive is
      // ever deleted, so an upgrade changes nothing until an administrator asks for it.
      //
      // ScheduledBackupTime is 24-hour local "HH:MM" and wins over the interval when
      // both are set. What goes into a backup and where it is written still come from the
      // existing backup settings; these only decide WHEN a backup happens and which old
      // archives survive.
      String GetScheduledBackupTime() const { return scheduled_backup_time_; }
      int GetScheduledBackupIntervalMinutes() const { return scheduled_backup_interval_minutes_; }
      int GetScheduledBackupKeepCount() const { return scheduled_backup_keep_count_; }
      int GetScheduledBackupMaxAgeDays() const { return scheduled_backup_max_age_days_; }
      // OTLP/HTTP collector endpoint for OpenTelemetry trace export (e.g.
      // http://127.0.0.1:4318/v1/traces). Empty disables tracing.
      String GetOtelEndpoint() const { return otel_endpoint_; }
      String GetOtelServiceName() const { return otel_service_name_; }
      int GetManageSieveServerPort() const { return manage_sieve_server_port_; }
      String GetManageSieveServerBindAddress() const { return manage_sieve_server_bind_address_; }
      bool GetArcSealingEnabled() const { return arc_sealing_enabled_; }

      // RFC 6376 3.5 signature timestamps.
      //
      // How long a signature we PRODUCE claims to stay valid. 0 - the default - emits
      // no x= at all, keeping the emitted tag set as it was. An expiry is a promise
      // about our own outbound mail that cannot be withdrawn once sent, and a window
      // shorter than the delay a legitimate retry, greylist or mailing list introduces
      // costs the message its DKIM pass and its DMARC alignment at the far end. A
      // sender who wants one usually picks around 604800 (a week).
      int GetDKIMSignatureValiditySeconds() const { return dkim_signature_validity_seconds_; }

      // Whether a signature we VERIFY whose x= has passed is refused. On by default:
      // x= is inside the bytes b= covers, so it is the signer's own instruction rather
      // than something a third party can inject, and a check that ships off ships dead.
      bool GetDKIMEnforceSignatureExpiry() const { return dkim_enforce_signature_expiry_; }

      // RFC 6376 3.5's "fudge factor" for clock drift between us and the signer.
      // Without it, our own clock running a few minutes fast turns other people's
      // valid mail into a DKIM failure.
      int GetDKIMExpiryClockSkewSeconds() const { return dkim_expiry_clock_skew_seconds_; }

      // Header field names to oversign (RFC 6376 5.4): listed in h= once more often
      // than the message carries them, so an instance ADDED after signing lands where
      // nothing was hashed and the signature fails. Colon, comma or semicolon
      // separated. Empty by default - oversigning a field the message does not carry
      // also forbids a legitimate intermediary from adding a first one, and a
      // discussion list adding Reply-To or Sender is normal, so the administrator says.
      String GetDkimOversignHeaders() const { return dkim_oversign_headers_; }

      // RFC 8601 Authentication-Results and RFC 7208 9.1 Received-SPF on inbound mail.
      //
      // Off by default. The header is a statement other systems act on, and it is only
      // worth anything if the reader trusts this server's name - so switching it on is
      // a deliberate act, not something an upgrade does quietly.
      bool GetAuthenticationResultsEnabled() const { return authentication_results_enabled_; }
      bool GetReceivedSpfHeaderEnabled() const { return received_spf_header_enabled_; }

      // The authserv-id: the name this server authenticates as. Empty means use the
      // computer name, which is what the Received header already says the message was
      // received "by", so the two agree. Set it explicitly when several servers share
      // one administrative identity.
      String GetAuthenticationResultsIdentity() const { return authentication_results_identity_; }
      String GetTlsRptFromAddress() const { return tls_rpt_from_address_; }
      String GetTlsRptOrganizationName() const { return tls_rpt_organization_name_; }
      String GetDmarcRptFromAddress() const { return dmarc_rpt_from_address_; }
      String GetDmarcRptOrganizationName() const { return dmarc_rpt_organization_name_; }
      // 0 = deliver a message the scanner could not examine (the shipped default,
      // and what this server has always done); 1 = hold it, retry on the ordinary
      // schedule, and bounce it if the scanner is still unreachable when the retry
      // budget runs out. See SMTPDeliverer::HandleUnscannableMessage_.
      int GetAVFailAction() const { return av_fail_action_; }

      // How long a held message waits before the next scan attempt, and how many
      // such attempts it gets before it is returned to the sender. Both are the
      // hold's own settings rather than the SMTP delivery retry budget, because
      // `smtpnooftries` ships as 0: borrowing it would make AVFailAction=1 bounce
      // on the first scanner blip and never hold at all, so the feature would not
      // do what its name says on any stock installation.
      int GetAVFailRetryMinutes() const { return av_fail_retry_minutes_; }

      // Counted in ATTEMPTS, deliberately not in elapsed time. An earlier version
      // bounded the hold by the message's age, which is anchored to its ARRIVAL:
      // any message already older than the window - a backlog released after an
      // outage, an ETRN batch, POP3-fetched mail carrying an upstream Received
      // date - would then have had its whole budget spent before the scanner ever
      // failed, and would be bounced on the first attempt with no hold at all.
      // Restarting a server whose scanner takes a minute longer to come up than
      // the mail queue does would have mass-bounced the entire backlog. Attempts
      // are something this policy itself advances, so they cannot already be
      // spent, and a future-dated arrival time cannot make the hold unbounded.
      //
      // 0 means do not hold at all - tell the sender immediately. A legitimate
      // choice for an operator who never wants unscanned mail sitting in a queue.
      int GetAVFailMaxHolds() const { return av_fail_max_holds_; }
      int GetAccountLockoutThreshold() const { return account_lockout_threshold_; }
      int GetAccountLockoutWindowMinutes() const { return account_lockout_window_minutes_; }
      int GetAccountLockoutMinutes() const { return account_lockout_minutes_; }
      // TLS key-exchange groups, in OpenSSL group-list syntax, most preferred
      // first. The default puts the hybrid post-quantum KEMs ahead of the
      // classical curves: a PQC-capable peer then gets a quantum-resistant
      // handshake, and a peer that does not know them simply negotiates one of
      // the classical groups that follow. An empty value, or a value OpenSSL
      // rejects, falls back to the classical list.
      //
      // Scope: this reaches the SMTP, POP3, IMAP and ManageSieve listeners, which
      // all get their context from SslContextInitializer via TCPServer, plus
      // outbound SMTP delivery. It does NOT reach the REST API listener or the
      // Web Services HTTPS listener - both build their own SSL_CTX directly and
      // never consult SslContextInitializer. Unifying that is worth doing, but it
      // is a wider change than this setting.
      String GetTlsKeyExchangeGroups() const { return tls_key_exchange_groups_; }
      // TLS 1.3 cipher suites, colon separated, most preferred first, in the RFC 8446
      // names: TLS_AES_256_GCM_SHA384, TLS_CHACHA20_POLY1305_SHA256,
      // TLS_AES_128_GCM_SHA256, TLS_AES_128_CCM_SHA256, TLS_AES_128_CCM_8_SHA256.
      //
      // These are NOT the OpenSSL cipher-list names SslCipherList uses. OpenSSL keeps
      // the two lists apart and neither constrains the other, so SslCipherList - which
      // goes through SSL_CTX_set_cipher_list - governs TLS 1.2 and below only and
      // restricts nothing on TLS 1.3, the version most peers now negotiate and one this
      // server ships with enabled. Before this setting existed there was no way to
      // influence the TLS 1.3 suites at all.
      //
      // Empty is the default and means "leave OpenSSL's own defaults alone". It has to
      // be handled by not calling the setter: OpenSSL accepts an empty list, returns
      // success, and leaves the context with no TLS 1.3 suite at all, after which every
      // TLS 1.3 handshake fails with no shared cipher.
      //
      // Scope matches GetTlsKeyExchangeGroups above: every context built by
      // SslContextInitializer, i.e. the SMTP, POP3, IMAP and ManageSieve listeners and
      // outbound delivery, but NOT the REST API or Web Services listeners, which build
      // their own SSL_CTX.
      String GetTlsCipherSuites13() const { return tls_cipher_suites13_; }

      // TLS session resumption and session-ticket management, on the listener contexts
      // SslContextInitializer builds. Every one of these defaults to "make no OpenSSL
      // call at all", which is the house pattern here and the only way to mean "leave
      // the library's own behaviour alone".
      //
      // Tickets on is today's behaviour. Turning them off sets SSL_OP_NO_TICKET AND
      // SSL_CTX_set_num_tickets(0), because the option alone only makes TLS 1.3 tickets
      // stateful rather than absent.
      bool GetTlsSessionTicketsEnabled() const { return tls_session_tickets_enabled_; }

      // 0 leaves OpenSSL's cache size; a negative value turns the server-side
      // session-ID cache off. Bounds what a client churning handshakes can make this
      // server remember.
      int GetTlsSessionCacheSize() const { return tls_session_cache_size_; }

      // 0 leaves OpenSSL's timeout. Bounds how long a stolen resumption secret stays
      // usable.
      int GetTlsSessionTimeoutSeconds() const { return tls_session_timeout_seconds_; }

      // 0 - the default - installs no ticket-key callback, so OpenSSL keeps its own key:
      // one per context, generated at startup and never rotated, so every ticket the
      // process ever issues is sealed under it and a key recovered later decrypts all of
      // them. A positive interval rotates, keeping the previous key so tickets issued
      // under it still resume, which bounds that exposure to two intervals.
      int GetTlsTicketKeyRotationSeconds() const { return tls_ticket_key_rotation_seconds_; }
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

      /// <summary>
      /// The reconciled [Settings] values, or empty before LoadDatabaseSettings has
      /// run. Consulted ONLY for the [Settings] section: the keys that are needed to
      /// reach the database in the first place - everything in [Database],
      /// [Directories] and the administrator password - can never be overridden from
      /// the database, which is the ordering answer and the security answer at once.
      ///
      /// Guarded because the two readers are called from LoadSettings, which runs on
      /// the startup thread and again on a COM thread (get_Directories,
      /// CreateInternalDatabase). Recursive because Synchronize writes the map's
      /// source while holding nothing, and the house type for this is
      /// boost::recursive_mutex.
      /// </summary>
      std::map<String, String> database_settings_;
      bool database_settings_loaded_;
      mutable boost::recursive_mutex database_settings_mutex_;

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

      String password_pepper_;

      bool oauth2_enabled_;
      bool oauth2_require_tls_;
      String outbound_oauth2_token_url_;
      String outbound_oauth2_client_id_;
      String outbound_oauth2_client_secret_;
      String outbound_oauth2_scope_;
      String outbound_oauth2_hosts_;
      String outbound_oauth2_fixed_token_;
      String fetch_oauth2_hosts_;
      String oauth2_allowed_algorithms_;
      String oauth2_hmac_secret_;
      String oauth2_rsa_public_key_file_;
      String oauth2_issuer_;
      String oauth2_audience_;
      String oauth2_username_claim_;

      bool protect_stored_secrets_with_dpapi_;
      String service_account_name_;
      String service_account_password_;

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
      int finalization_timeout_;
      int clam_min_timeout_;
      int clam_max_timeout_;
      int dns_query_timeout_;
      int client_session_ceiling_;
      int db_connection_acquire_timeout_;
      int script_timeout_;
      int external_process_timeout_;
      int async_queue_stall_threshold_;
      int async_queue_reserved_threads_;
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
      bool srs_enabled_;
      String srs_secret_;
      bool batv_enabled_;
      String batv_secret_;
      int max_submissions_per_ip_per_minute_;
      int max_outbound_per_destination_per_minute_;
      bool use_dns_cache_;
      String dns_server_;

      bool mta_sts_enabled_ = true;
      bool dane_enabled_ = true;
      bool dnssec_validation_enabled_ = true;
      String dnssec_trust_anchors_;
      bool json_logging_ = false;
      int log_delete_days_ = 0;
      int shutdown_drain_seconds_ = 0;
      int slow_query_log_ms_ = 0;
      bool message_store_fsync_ = false;
      int simulate_spool_write_failure_ = 0;
      String simulate_database_failure_for_;
      bool simulate_database_failure_enabled_ = false;
      bool message_store_consistency_check_ = false;
      int metrics_server_port_ = 0;
      String metrics_server_bind_address_;
      String metrics_server_auth_token_;
      String metrics_server_auth_username_;
      String metrics_server_auth_password_;
      String metrics_server_certificate_file_;
      String metrics_server_private_key_file_;

      String scheduled_backup_time_;
      int scheduled_backup_interval_minutes_ = 0;
      int scheduled_backup_keep_count_ = 0;
      int scheduled_backup_max_age_days_ = 0;
      String otel_endpoint_;
      String otel_service_name_;
      int manage_sieve_server_port_ = 0;
      String manage_sieve_server_bind_address_;
      bool arc_sealing_enabled_ = false;
      int dkim_signature_validity_seconds_ = 0;
      bool dkim_enforce_signature_expiry_ = true;
      int dkim_expiry_clock_skew_seconds_ = 300;
      String dkim_oversign_headers_;
      bool authentication_results_enabled_ = false;
      bool received_spf_header_enabled_ = false;
      String authentication_results_identity_;
      String tls_rpt_from_address_;
      String tls_rpt_organization_name_;
      String dmarc_rpt_from_address_;
      String dmarc_rpt_organization_name_;
      int av_fail_action_ = 0;
      int av_fail_retry_minutes_ = 15;
      int av_fail_max_holds_ = 16;
      int account_lockout_threshold_ = 0;
      int account_lockout_window_minutes_ = 30;
      int account_lockout_minutes_ = 30;
      String tls_key_exchange_groups_;
      String tls_cipher_suites13_;
      bool tls_session_tickets_enabled_ = true;
      int tls_session_cache_size_ = 0;
      int tls_session_timeout_seconds_ = 0;
      int tls_ticket_key_rotation_seconds_ = 0;
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
      int imap_search_timeout_ = 60;
      int imap_search_max_megabytes_ = 2048;
      String database_provider_;

      String m_sDisableAUTHList;
   };
}
