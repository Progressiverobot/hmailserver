// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../Util/File.h"
#include "..\Application\IniFileSettings.h"

namespace HM
{

   #define LOG_DEBUG(s)                                       \
   if (Logger::Instance()->GetLogMask() & Logger::LSDebug)    \
      Logger::Instance()->LogDebug(s);                        \

   #define LOG_TCPIP(s)                                       \
   if (Logger::Instance()->GetLogMask() & Logger::LSTCPIP)    \
      Logger::Instance()->LogTCPIP(s);                        \

   #define LOG_APPLICATION(s)                                 \
   if (Logger::Instance()->GetLogMask() & Logger::LSApplication)   \
      Logger::Instance()->LogApplication(s);                   \

   #define LOG_SMTP(iSession, sIP, sMsg)                       \
   if (Logger::Instance()->GetLogMask() & Logger::LSSMTP)      \
      Logger::Instance()->LogSMTPConversation(iSession, sIP, sMsg);      \

   #define LOG_SMTP_CLIENT(iSession, sIP, sMsg)                          \
   if (Logger::Instance()->GetLogMask() & Logger::LSSMTP)      \
      Logger::Instance()->LogSMTPConversation(iSession, sIP, sMsg,true); \

   #define LOG_POP3(iSession,sIP, sMsg)                                 \
   if (Logger::Instance()->GetLogMask() & Logger::LSPOP3)      \
      Logger::Instance()->LogPOP3Conversation(iSession,sIP, sMsg);      \

   #define LOG_POP3_CLIENT(iSession,sIP, sMsg)                          \
   if (Logger::Instance()->GetLogMask() & Logger::LSPOP3)      \
      Logger::Instance()->LogPOP3Conversation(iSession,sIP, sMsg, true);\

   #define LOG_IMAP(iSession,sIP, sMsg)                                 \
   if (Logger::Instance()->GetLogMask() & Logger::LSIMAP)      \
      Logger::Instance()->LogIMAPConversation(iSession,sIP, sMsg);      \

   class Logger : public Singleton<Logger>
   {
   public:
      Logger();
      ~Logger(void);

      enum LogSource
      {
         LSEnabled = 1,
         LSSMTP = 2,
         LSPOP3 = 4,
         LSTCPIP = 8,
         LSApplication = 16,
         LSDebug = 32,
         LSIMAP = 64,
         LSEvents = 128,
         LSKeepFilesOpen = 256
      };

      enum LogType
      {
         Normal = 1,
         Error = 2,
         AWStats = 3,
         Backup = 4,
         Events = 5,
         IMAP = 6,
         POP3 = 7,
         SMTP = 8
      };
   
      enum Constants
      {
         LiveLogMaxSize = 1000000
      };

      // Where log entries are written. Mirrors eLogDevice in the IDL after the
      // COM layer's mapping (hLogDeviceUnknown -> 0, hLogDeviceSQL -> 1,
      // hLogDeviceFile -> 2), which is the form stored in the logdevice setting.
      // Unknown - the value every CreateTables script inserts, so the value every
      // installation that has never touched the setting still has - means file, so
      // that installation behaves exactly as it always has.
      enum LogDevice
      {
         DeviceUnknown = 0,
         DeviceSQL = 1,
         DeviceFile = 2
      };

      // How each entry is rendered. Mirrors eLogOutputFormat after the COM
      // layer's mapping (hLogFormatDefault -> 0, hLogFormatCSA -> 1). Note that
      // the IDL numbers those 1 and 2; the stored values are 0 and 1.
      enum LogOutputFormat
      {
         FormatDefault = 0,
         FormatNCSA = 1
      };

      void SetLogMask(int iMask);

      // Both are driven by Configuration::OnPropertyChanged, which
      // PropertySet::Refresh() raises for every setting at load time as well as
      // on change - so these also carry the stored value at startup. Reading the
      // settings from inside the Logger instead is not an option: the Logger logs
      // during startup, before the property set exists.
      void SetLogDevice(int device);
      void SetLogFormat(int format);

      int GetLogDevice() const { return log_device_; }
      int GetLogFormat() const { return log_format_; }

      // Writes an already-rendered line straight to the file device, bypassing
      // device selection. Used by the SQL log device when it has to fall back to
      // files, which is the whole of its degradation path.
      void WriteLineToFile(const String &line, LogType lt);

      void LogApplication(const String &sMessage);
      void LogApplication(const String &sMessage, bool isError);
      void LogSMTPConversation(int iSessionID, const String &sRemoteHost, const String &sMessage, bool bClient = false);
      void LogPOP3Conversation(int iSessionID, const String &sRemoteHost, const String &sMessage, bool bClient = false);
      void LogIMAPConversation(int iSessionID, const String &sRemoteHost, const String &sMessage);
      void LogEvent(const String &sMessage);
      void LogTCPIP(const String &sMessage);
      void LogDebug(const String &sMessage);
      void LogError(const String &sMessage);

      void LogAWStats(const String &sData);
      void LogBackup(const String &sData);

      bool GetLogPOP3() { return (GetLogMask() & Logger::LSPOP3) != 0; }
      bool GetLogIMAP() { return (GetLogMask() & Logger::LSIMAP) != 0; }
      bool GetLogSMTP() { return (GetLogMask() & Logger::LSSMTP) != 0; }
      bool GetLogDebug() const; 
      bool GetLogApplication() const; 
      bool GetLogTCPIP() const;
      bool GetLoggingEnabled() const;
 	   bool GetLiveLogEnabled() const;

      void EnableLiveLogging(bool bEnable);   
      String GetLiveLog();

      int GetLogMask() 
      {
         return log_mask_;
      }

      String GetCurrentLogFileName(LogType lt) ;

   private:

      // One log record before it has been rendered for any particular device.
      // Introduced so that format selection and device selection each live in one
      // place: every Log* function used to repeat the "JSON or default?" choice
      // inline, and adding a third format plus a second device to seven copies of
      // that decision is how renderings end up disagreeing with each other.
      //
      // LogEvent, LogAWStats and LogBackup deliberately do NOT go through this.
      // Each of those three writes a separate file with an external consumer that
      // parses a fixed field list - an AWStats installation, a backup report, an
      // event log an administrator's own tooling reads - so neither the format
      // setting nor the device setting touches them. They are logs of a different
      // kind that happen to share a writer.
      struct Entry
      {
         Entry() : thread(0), session(-1) {}

         String category;      // "SMTPD", "APPLICATION", "DEBUG", ...
         long thread;
         int session;          // -1 when the entry belongs to no protocol session
         String remote_host;   // empty when the entry has no peer
         String time;
         String message;       // unescaped; each rendering escapes as it needs to
      };

      Entry MakeEntry_(const String &category, int session, const String &remoteHost, const String &message);

      // Renders one entry in whichever format is configured.
      String Render_(const Entry &entry);

      // Hands the rendered line to the configured device. The entry is always
      // written somewhere: whenever the SQL device will not take it - off,
      // degraded, buffer full, or the caller is its own flush thread - it goes to
      // the file instead, so there is no path through here that discards it and
      // nothing for a caller to check.
      void Write_(const Entry &entry, const String &line, LogType lt);

      String CleanLogMessage_(const String &message);
      File* GetCurrentLogFile_(LogType lt);

      // Structured (JSON Lines) logging support. Enabled with the
      // JsonLogging=1 setting in hMailServer.ini.
      bool UseJsonFormat_() const;
      static String EscapeJson_(const String &value);
      static String BuildJsonEntry_(const String &category, long thread, int session, const String &remoteHost, const String &time, const String &message);

      void LogLive_(String &sMessage);
      void WriteData_(const String &sData, LogType = Normal);

      // A log line that could not be written cannot be reported through the log. Once
      // per process, to the debugger and the Windows event log. See the definition.
      void ReportWriteFailure_(const String &fileName);
   
      String log_dir_;
      String GetCurrentTime();

      int GetProcessID_();
      int GetThreadID_();


      bool enable_live_log_;
      bool sep_svc_logs_;      
      int  log_level_;      
      int  max_log_line_len_;      

      String live_log_;
      int log_mask_;

      // Written by whichever thread applies a settings change, read by every
      // thread that logs. Deliberately plain ints and not synchronised, exactly
      // as log_mask_ above already is: both are aligned 32-bit values that cannot
      // tear, and the worst a racing reader can see is one entry rendered or
      // routed by the previous setting.
      int log_device_;
      int log_format_;

      File normal_log_file_;
      File error_log_file_;
      File awstats_log_file_;
      File backup_log_file_;
      File events_log_file_;
      File imaplog_file_;
      File pop3log_file_;
      File smtplog_file_;

      boost::recursive_mutex mtx_;
      boost::recursive_mutex mtx_LiveLog;
   };

}

