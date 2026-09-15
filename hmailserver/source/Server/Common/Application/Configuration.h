// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Backup.h"

#include "../AntiSpam/AntiSpamConfiguration.h"
#include "../AntiVirus/AntiVirusConfiguration.h"
#include "../TCPIP/SocketConstants.h"

namespace HM
{

   class SMTPConfiguration;
   class POP3Configuration;
   class IMAPConfiguration;
   class AntiSpamConfiguration;
   class Logger;
   class PropertySet;
   class ServerMessages;
   class TCPIPPorts;
   class CacheConfiguration;
   class Settings;
   class ServerMessages;
   class TCPIPPorts;
   class BlockedAttachments;
   class SSLCertificates;

   class Configuration : public Singleton<Configuration>
   {
   public:

      Configuration();
      virtual ~Configuration();

      std::shared_ptr<POP3Configuration> GetPOP3Configuration() const { return pop3_configuration_; }
      std::shared_ptr<SMTPConfiguration> GetSMTPConfiguration() const { return smtp_configuration_; }
      std::shared_ptr<IMAPConfiguration> GetIMAPConfiguration() const { return imap_configuration_; }
      AntiSpamConfiguration &GetAntiSpamConfiguration() { return anti_spam_configuration_; }
      AntiVirusConfiguration &GetAntiVirusConfiguration() { return anti_virus_configuration_; }
      std::shared_ptr<CacheConfiguration> GetCacheConfiguration() const { return cache_configuration_; }

      bool Load();

      bool IsIPv6Available();

      void OnPropertyChanged(std::shared_ptr<Property> pProperty);

      bool GetUseSMTP() const;
      void SetUseSMTP(bool UseSMTP);
      bool GetUsePOP3() const;
      void SetUsePOP3(bool UsePOP3);
      bool GetUseIMAP() const;
      void SetUseIMAP(bool UseIMAP);

      bool GetAWStatsEnabled() const;
      void SetAWStatsEnabled(bool newVal);

      long GetLogFormat() const;
      void SetLogFormat(long lNewVal);

      bool GetUseLogging() const;
      void SetUseLogging(bool bLogging);

      void SetLogSMTPConversations(bool newVal);
      bool GetLogSMTPConversations() const;

      void SetLogPOP3Conversations(bool newVal);
      bool GetLogPOP3Conversations() const;

      void SetLogIMAPConversations(bool newVal);
      bool GetLogIMAPConversations() const;

      void SetLogTCPIP(bool newVal);
      bool GetLogTCPIP() const;

      void SetLogApplication(bool newVal);
      bool GetLogApplication() const;

      void SetLogDebug(bool newVal);
      bool GetLogDebug() const;

      long GetLogDevice() const;
      void SetLogDevice(long newVal);

      // The syslog sink. Seven settings, stored in hm_settings, read by
      // SyslogSink::Start - so a change takes effect at the next service start or
      // Reinitialize, which is what the Control Panel editor says on the page.
      bool GetSyslogEnabled() const;
      void SetSyslogEnabled(bool newVal);

      String GetSyslogHost() const;
      void SetSyslogHost(const String &newVal);

      long GetSyslogPort() const;
      void SetSyslogPort(long newVal);

      long GetSyslogTransport() const;
      void SetSyslogTransport(long newVal);

      long GetSyslogFacility() const;
      void SetSyslogFacility(long newVal);

      long GetSyslogMinimumSeverity() const;
      void SetSyslogMinimumSeverity(long newVal);

      long GetSyslogLogTypes() const;
      void SetSyslogLogTypes(long newVal);

      // One category of the SyslogLogTypes mask, by its Logger::LogSource bit.
      // The same shape as the six GetLog*Conversations above, which read bits of
      // PROPERTY_LOGGING: one stored integer, one switch per category, and no
      // second place for the two to disagree.
      bool GetSyslogLogType(int mask) const;
      void SetSyslogLogType(int mask, bool enabled);

      void SetUseScriptServer(bool newVal);
      bool GetUseScriptServer() const;

      void SetKeepFilesOpen(bool newVal);
      bool GetKeepFilesOpen() const;


      int GetWorkerThreadPriority() const;
      void SetWorkerThreadPriority(int iNewVal);

      int GetTCPIPThreads() const;
      void SetTCPIPThreads(int iNewVal);

      void SetScriptLanguage(const String &newVal);
      String GetScriptLanguage() const;

      void SetDefaultDomain(const String &newVal);
      String GetDefaultDomain() const;

      String GetMirrorAddress() const;
      void SetMirrorAddress(const String &sAddress);


      // BACKUP OPTIONS
      String GetBackupDestination() const;
      void SetBackupDestination(const String &sNewPath);

      bool GetBackupOption(Backup::BackupOptions iMode) const;
      int GetBackupOptions() const;
      void SetBackupOption(Backup::BackupOptions iMode, bool bSet);

      int GetRequiredDBVersion() { return REQUIRED_DB_VERSION; }

      String GetHostName() const;
      void SetHostName(String &NewVal);

      bool XMLStore(XNode *pBackupNode);
      bool XMLLoad(XNode *pBackupNode, int iRestoreOptions);

      bool GetDisconnectInvalidClients();
      void SetDisconnectInvalidClients(bool bNewVal);

      int GetMaximumIncorrectCommands();
      void SetMaximumIncorrectCommands(int iNewVal);

      bool GetAutoBanLogonEnabled();
      void SetAutoBanLogonEnabled(bool newValue);

      int GetMaxInvalidLogonAttempts();
      void SetMaxInvalidLogonAttempts(int iNewVal);

      int GetMaxLogonAttemptsWithin();
      void SetMaxLogonAttemptsWithin(int iNewVal);

      int GetAutoBanMinutes();
      void SetAutoBanMinutes(int iNewVal);

      int GetAsynchronousThreads() const;
      void SetAsynchronousThreads(int iNewVal);

      bool ClearOldLogonFailures();

      bool GetMessageIndexing();
      void SetMessageIndexing(bool enable);

      bool GetVerifyRemoteSslCertificate() const;
      void SetVerifyRemoteSslCertificate(bool newValue);

      String GetSslCipherList() const;
      void SetSslCipherList(String newValue);

      bool GetSslVersionEnabled(SslTlsVersion version) const;
      void SetSslVersionEnabled(SslTlsVersion version, bool enabled);

      bool GetTlsOptionEnabled(TlsOption option) const;
      void SetTlsOptionEnabled(TlsOption option, bool enabled);

      bool GetIPv6Preferred();
      void SetIPv6Preferred(bool enabled);

      bool GetCreateDefaultSpecialUseFolders();
      void SetCreateDefaultSpecialUseFolders(bool enabled);

      // The audit trail (see Common/Util/AuditTrail.h) and the alerts (see
      // Common/Util/AlertManager.h). All nine live in hm_settings, which is
      // where a setting belongs; none of them is an hMailServer.ini key.
      bool GetAuditTrailEnabled();
      void SetAuditTrailEnabled(bool enabled);
      int GetAuditRetentionDays();
      void SetAuditRetentionDays(int days);

      bool GetAlertsEnabled();
      void SetAlertsEnabled(bool enabled);
      String GetAlertRecipient();
      void SetAlertRecipient(const String &address);
      String GetAlertSenderAddress();
      void SetAlertSenderAddress(const String &address);
      bool GetAlertDigestEnabled();
      void SetAlertDigestEnabled(bool enabled);
      int GetAlertDigestHour();
      void SetAlertDigestHour(int hour);
      int GetAlertMaxPerHour();
      void SetAlertMaxPerHour(int maximum);
      int GetAlertWebhookMaxAttempts();
      void SetAlertWebhookMaxAttempts(int attempts);

      int GetCrashSimulationMode() const;
      void SetCrashSimulationMode(int mode);


      std::shared_ptr<PropertySet> GetSettings() const;

      std::shared_ptr<ServerMessages> GetServerMessages() {return server_messages_;}
      std::shared_ptr<TCPIPPorts> GetTCPIPPorts() const;
      std::shared_ptr<BlockedAttachments> GetBlockedAttachments() {return blocked_attachments_;}
      std::shared_ptr<SSLCertificates> GetSSLCertificates() {return ssl_certificates_;}
      std::shared_ptr<PropertySet> GetPropertySet() {return property_set_;}

   private:

      // Creates the hm_settings rows that a database made before a feature
      // existed has none of, each with the default that is today's behaviour.
      // Runs once per Load, on the start-up thread, before anything else can read
      // the property set. See PropertySet::EnsureLong for why it is needed at all.
      void EnsureSettingDefaults_();

      std::shared_ptr<POP3Configuration> pop3_configuration_;
      std::shared_ptr<SMTPConfiguration> smtp_configuration_;
      std::shared_ptr<IMAPConfiguration> imap_configuration_;
      
      std::shared_ptr<CacheConfiguration> cache_configuration_;

      AntiSpamConfiguration anti_spam_configuration_;
      AntiVirusConfiguration anti_virus_configuration_;

      std::shared_ptr<ServerMessages> server_messages_;
      std::shared_ptr<TCPIPPorts> tcpip_ports_;
      std::shared_ptr<BlockedAttachments> blocked_attachments_;
      std::shared_ptr<SSLCertificates> ssl_certificates_;

      std::shared_ptr<PropertySet> property_set_;

      int crash_simulation_mode_;

      bool ipv6_available_;
   };
}
