// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "../Util/Time.h"
#include "../Util/File.h"

#include "NcsaLogFormatter.h"
#include "SqlLogDevice.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   
   Logger::Logger()
   {
      log_mask_ = 0;
      enable_live_log_ = false;

      // Files and the default line format until the stored settings arrive, which
      // keeps the entries logged during startup - before Configuration::Load has
      // read anything - exactly where they have always been.
      log_device_ = DeviceFile;
      log_format_ = FormatDefault;

      auto ini_file_settings = IniFileSettings::Instance();

      log_dir_ = ini_file_settings->GetLogDirectory();
      sep_svc_logs_ = ini_file_settings->GetSepSvcLogs();
   }

   Logger::~Logger(void)
   {
   }

   void 
   Logger::SetLogMask(int iMask)
   {
      if (iMask & LSEnabled)
         log_mask_ = iMask;
      else
         log_mask_ = 0;
   }

   void
   Logger::SetLogDevice(int device)
   {
      // An out-of-range value can only come from a hand-edited or corrupted
      // logdevice row. Files are the safe answer and LOG_APPLICATION is the right
      // channel for it: an ErrorManager report here would fire on the way to
      // reporting itself, and this is a configuration observation, not a fault.
      if (device != DeviceUnknown && device != DeviceSQL && device != DeviceFile)
      {
         String message;
         message.Format(_T("Unknown log device %d configured. Logging to file."), device);
         LOG_APPLICATION(message);

         device = DeviceFile;
      }

      log_device_ = device;

      // Started lazily, and only ever when SQL has actually been selected: an
      // installation that leaves the setting alone never creates the flush thread
      // and never touches the log table.
      SqlLogDevice::Instance()->SetEnabled(device == DeviceSQL);
   }

   void
   Logger::SetLogFormat(int format)
   {
      if (format != FormatDefault && format != FormatNCSA)
      {
         String message;
         message.Format(_T("Unknown log format %d configured. Using the default log format."), format);
         LOG_APPLICATION(message);

         format = FormatDefault;
      }

      log_format_ = format;
   }

   bool
   Logger::GetLoggingEnabled() const
   {
      if (log_mask_ & LSEnabled)
         return true;
      else
         return false;
   }

   Logger::Entry
   Logger::MakeEntry_(const String &category, int session, const String &remoteHost, const String &message)
   {
      Entry entry;

      entry.category = category;
      entry.thread = GetThreadID_();
      entry.session = session;
      entry.remote_host = remoteHost;
      entry.time = GetCurrentTime();
      entry.message = message;

      return entry;
   }

   String
   Logger::Render_(const Entry &entry)
   {
      // NCSA wins over JsonLogging when both are configured. It is the choice an
      // administrator makes in the Control Panel, and this whole change exists
      // because a choice made there was being silently ignored. JsonLogging is an
      // ini-file override that predates a working format setting and stays in
      // effect for everyone who has not picked a format, because the stored
      // default is FormatDefault and only an explicit selection reaches
      // FormatNCSA.
      if (log_format_ == FormatNCSA)
         return NcsaLogFormatter::Format(entry.category, entry.session, entry.remote_host, entry.time, entry.message);

      if (UseJsonFormat_())
         return BuildJsonEntry_(entry.category, entry.thread, entry.session, entry.remote_host, entry.time, entry.message);

      // The two shapes the default format has always had, byte for byte. Which
      // one applies is decided by the entry rather than by the caller: a negative
      // session means an entry that belongs to no conversation, and those have
      // never carried the session column.
      String result;

      if (entry.session >= 0)
      {
         result.Format(_T("\"%s\"\t%d\t%d\t\"%s\"\t\"%s\"\t\"%s\"\r\n"),
            entry.category.c_str(), entry.thread, entry.session, entry.time.c_str(),
            entry.remote_host.c_str(), CleanLogMessage_(entry.message).c_str());
      }
      else
      {
         result.Format(_T("\"%s\"\t%d\t\"%s\"\t\"%s\"\r\n"),
            entry.category.c_str(), entry.thread, entry.time.c_str(),
            CleanLogMessage_(entry.message).c_str());
      }

      return result;
   }

   void
   Logger::Write_(const Entry &entry, const String &line, LogType lt)
   {
      if (log_device_ == DeviceSQL)
      {
         if (SqlLogDevice::Instance()->Enqueue(entry.category, (int) lt, entry.thread, entry.session,
                                              entry.remote_host, entry.time, entry.message, line))
         {
            return;
         }
      }

      // The SQL device refused it (off, degraded, buffer full, or this is its own
      // flush thread), or files were selected in the first place. Either way the
      // entry is written, never discarded.
      WriteData_(line, lt);
   }

   void
   Logger::WriteLineToFile(const String &line, LogType lt)
   {
      WriteData_(line, lt);
   }

   void
   Logger::LogSMTPConversation(int iSessionID, const String &sRemoteHost, const String &sMessage, bool bClient)
   {
      if (!(log_mask_ & LSSMTP))
         return; // not intressted in this...

      Entry entry = MakeEntry_(bClient ? _T("SMTPC") : _T("SMTPD"), iSessionID, sRemoteHost, sMessage);
      String sData = Render_(entry);

      if (enable_live_log_)
         LogLive_(sData);

      Write_(entry, sData, SMTP);

   }

   void
   Logger::LogPOP3Conversation(int iSessionID, const String &sRemoteHost, const String &sMessage, bool bClient)
   {
      if (!(log_mask_ & LSPOP3))
         return; // not intressted in this...

      // Seems this was never done so now external account activity logs as client
      Entry entry = MakeEntry_(bClient ? _T("POP3C") : _T("POP3D"), iSessionID, sRemoteHost, sMessage);
      String sData = Render_(entry);

      if (enable_live_log_)
         LogLive_(sData);

      Write_(entry, sData, POP3);

   }

   void
   Logger::LogIMAPConversation(int iSessionID, const String &sRemoteHost, const String &sMessage)
   {
      if (!(log_mask_ & LSIMAP))
         return; // not intressted in this...

      Entry entry = MakeEntry_(_T("IMAPD"), iSessionID, sRemoteHost, sMessage);
      String sData = Render_(entry);

      if (enable_live_log_)
         LogLive_(sData);

      Write_(entry, sData, IMAP);

   }

   void 
   Logger::LogApplication(const String &sMessage)
   {
      LogApplication(sMessage, false);
   }

   void 
   Logger::LogApplication(const String &sMessage, bool isError)
   {
#ifndef _DEBUG
      if (!(log_mask_ & LSApplication))
         return; // not intressted in this...
#endif

      Entry entry = MakeEntry_(_T("APPLICATION"), -1, _T(""), sMessage);
      String sData = Render_(entry);

#ifdef _DEBUG
      OutputDebugString(sData);
#endif

      if (enable_live_log_)
         LogLive_(sData);

      if (log_mask_ & LSApplication)
         Write_(entry, sData, Normal);
   }

   void
   Logger::LogDebug(const String &sMessage)
   {
      if (!(log_mask_ & LSDebug))
         return; // not intressted in this...

      Entry entry = MakeEntry_(_T("DEBUG"), -1, _T(""), sMessage);
      String sData = Render_(entry);

      if (enable_live_log_)
         LogLive_(sData);


      Write_(entry, sData, Normal);

   }


   void
   Logger::LogError(const String &sMessage)
   {
      Entry entry = MakeEntry_(_T("ERROR"), -1, _T(""), sMessage);
      String sData = Render_(entry);

      if (enable_live_log_)
         LogLive_(sData);

      // The error log goes to a file whatever device is selected, and that is a
      // deliberate exception to the rule that the device decides. The error log is
      // what you read when the database is the thing that broke: routing it
      // exclusively into the database means the report "the database is
      // unavailable" has nowhere to go, and the one record that could have
      // explained the outage is the one record that could not be written.
      WriteData_(sData, Error);

      // Also log this in the application log if some other logging is enabled.
      // This copy does follow the device, so an installation logging to SQL still
      // gets its errors in the table - carried by the copy whose destination is
      // the main log, not by a second row for the same event.
      if (GetLoggingEnabled())
         Write_(entry, sData, Normal);
   }


   void
   Logger::LogTCPIP(const String &sMessage)
   {
      if (!(log_mask_ & LSTCPIP))
         return; // not intressted in this...

      Entry entry = MakeEntry_(_T("TCPIP"), -1, _T(""), sMessage);
      String sData = Render_(entry);

      if (enable_live_log_)
         LogLive_(sData);

      Write_(entry, sData, Normal);
   }

   void 
   Logger::LogEvent(const String &sMessage)
   {
      long lThread = GetThreadID_();
      String sTime = GetCurrentTime();

      String sData;
      if (UseJsonFormat_())
         sData = BuildJsonEntry_(_T("EVENT"), lThread, -1, "", sTime, sMessage);
      else
         sData.Format(_T("%d\t\"%s\"\t\"%s\"\r\n"), lThread, sTime.c_str(), CleanLogMessage_(sMessage).c_str());

      WriteData_(sData, Events);
   }

   bool
   Logger::UseJsonFormat_() const
   {
      return IniFileSettings::Instance()->GetJsonLogging();
   }

   String
   Logger::EscapeJson_(const String &value)
   {
      String result;
      result.reserve(value.GetLength() + 16);

      for (int i = 0; i < value.GetLength(); i++)
      {
         wchar_t character = value[i];

         switch (character)
         {
         case '\"':
            result += _T("\\\"");
            break;
         case '\\':
            result += _T("\\\\");
            break;
         case '\b':
            result += _T("\\b");
            break;
         case '\f':
            result += _T("\\f");
            break;
         case '\n':
            result += _T("\\n");
            break;
         case '\r':
            result += _T("\\r");
            break;
         case '\t':
            result += _T("\\t");
            break;
         default:
            if (character < 0x20)
            {
               String encoded;
               encoded.Format(_T("\\u%04X"), static_cast<int>(character));
               result += encoded;
            }
            else
            {
               result += character;
            }
            break;
         }
      }

      return result;
   }

   String
   Logger::BuildJsonEntry_(const String &category, long thread, int session, const String &remoteHost, const String &time, const String &message)
   {
      String result;
      result.reserve(message.GetLength() + 128);

      result += _T("{\"ts\":\"");
      result += EscapeJson_(time);
      result += _T("\",\"category\":\"");
      result += EscapeJson_(category);
      result += _T("\",\"thread\":");
      result += StringParser::IntToString(static_cast<int>(thread));

      if (session >= 0)
      {
         result += _T(",\"session\":");
         result += StringParser::IntToString(session);
      }

      if (!remoteHost.IsEmpty())
      {
         result += _T(",\"remoteip\":\"");
         result += EscapeJson_(remoteHost);
         result += _T("\"");
      }

      result += _T(",\"message\":\"");
      result += EscapeJson_(message);
      result += _T("\"}\r\n");

      return result;
   }

   String 
   Logger::CleanLogMessage_(const String &message)
   {
      String result = message;
      result.Replace(_T("\r\n"), _T("[nl]"));
      return result;
   }

   String 
   Logger::GetCurrentLogFileName(LogType lt) 
   {
      String sFilename;
      String theTime = Time::GetCurrentDate();

      switch (lt)
      {
      case Normal:
         sFilename.Format(_T("%s\\hmailserver_%s.log"), log_dir_.c_str(), theTime.c_str());
         break;
      case Error:
         sFilename.Format(_T("%s\\ERROR_hmailserver_%s.log"), log_dir_.c_str(), theTime.c_str());
         break;
      case AWStats:
         sFilename.Format(_T("%s\\hmailserver_awstats.log"), log_dir_.c_str());
         break;
      case Backup:
         sFilename.Format(_T("%s\\hmailserver_backup.log"), log_dir_.c_str());
         break;
      case Events:
         sFilename.Format(_T("%s\\hmailserver_events.log"), log_dir_.c_str());
         break;
      case IMAP:
         if (sep_svc_logs_) 
            sFilename.Format(_T("%s\\hmailserver_IMAP_%s.log"), log_dir_.c_str(), theTime.c_str());
         else
            sFilename.Format(_T("%s\\hmailserver_%s.log"), log_dir_.c_str(), theTime.c_str());
         break;
      case POP3:
         if (sep_svc_logs_) 
            sFilename.Format(_T("%s\\hmailserver_POP3_%s.log"), log_dir_.c_str(), theTime.c_str());
         else
            sFilename.Format(_T("%s\\hmailserver_%s.log"), log_dir_.c_str(), theTime.c_str());
         break;
      case SMTP:
         if (sep_svc_logs_) 
            sFilename.Format(_T("%s\\hmailserver_SMTP_%s.log"), log_dir_.c_str(), theTime.c_str());
         else
            sFilename.Format(_T("%s\\hmailserver_%s.log"), log_dir_.c_str(), theTime.c_str());
         break;
      }

      return sFilename;
   }

   File*
   Logger::GetCurrentLogFile_(LogType lt)
   {
      String fileName = GetCurrentLogFileName(lt);
      sep_svc_logs_ = IniFileSettings::Instance()->GetSepSvcLogs();

      bool writeUnicode = false;

      File *file = 0;
      switch (lt)
      {
      case Normal:
         file = &normal_log_file_;
         writeUnicode = false;
         break;
      case Error:
         file = &error_log_file_;
         writeUnicode = false;
         break;
      case AWStats:
         file = &awstats_log_file_;
         writeUnicode = false;
         break;
      case Backup:
         file = &backup_log_file_;
         writeUnicode = true;
         break;
      case Events:
         file = &events_log_file_;
         writeUnicode = true;
         break;
      case IMAP:
         if (sep_svc_logs_) 
            file = &imaplog_file_;
         else
            file = &normal_log_file_;
         writeUnicode = false;
         break;
      case POP3:
         if (sep_svc_logs_) 
            file = &pop3log_file_;
         else
            file = &normal_log_file_;
         writeUnicode = false;
         break;
      case SMTP:
         if (sep_svc_logs_) 
            file = &smtplog_file_;
         else
            file = &normal_log_file_;
         writeUnicode = false;
         break;
      }

      if (file == nullptr)
      {
         // Every LogType above maps to a member file. An out-of-range value has no
         // destination, and dropping the line is preferable to crashing a caller
         // which was only trying to log.
         return nullptr;
      }

      // Sampled before Open(OTAppend), which creates the file if it is missing. This
      // drives both the reopen-after-rotation check and whether a BOM is needed.
      bool fileExists = false;

      try
      {
         fileExists = FileUtilities::Exists(fileName);
      }
      catch (boost::system::system_error&)
      {
         // If the log is not accessible for some reason, such as error code 5 - access is denied,
         // then we can't open the log file. This will result in nothing being logged.
         return nullptr;
      }

      if (file->IsOpen())
      {
         if (file->GetName() != fileName)
            file->Close();
         else if (!fileExists)
            file->Close();
      }
      
      if (!file->IsOpen())
      {
         if (!file->Open(fileName, File::OTAppend))
            return nullptr;

         if (!fileExists && writeUnicode)
         {
            file->WriteBOF();
         }
      }

      return file;
   }

   void
   Logger::WriteData_(const String &sData, LogType lt)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mtx_);

      File *file = GetCurrentLogFile_(lt);
      if (file == nullptr)
         return;

      bool writeUnicode = false;
      bool keepFileOpen = (log_mask_ & LSKeepFilesOpen) && (lt == Normal || lt == SMTP || lt == POP3 || lt == IMAP);

      switch (lt)
      {
      case Normal:
      case Error:
      case AWStats:
      case IMAP:
      case POP3:
      case SMTP:
         writeUnicode = false;
         break;
      case Backup:
      case Events:
         writeUnicode = true;
         break;
      }

      if (writeUnicode)
      {
         file->Write(sData);
      }
      else
      {
         AnsiString sAnsiString;
         // Let's truncate some of those crazy long log lines
         // only when debug is not enabled or loglevel <= 2
         // Only do it if long enough default is 500 by set by MaxLogLineLen
         
         int iDataLenTmp = sData.GetLength();
         log_level_ = IniFileSettings::Instance()->GetLogLevel();
         max_log_line_len_ = IniFileSettings::Instance()->GetMaxLogLineLen();

         if ((Logger::Instance()->GetLogDebug()) || (log_level_ > 2) || (iDataLenTmp < max_log_line_len_ ))
            sAnsiString = sData;
         else
            sAnsiString = sData.Mid(0, max_log_line_len_ - 30) + " ... " + sData.Mid(iDataLenTmp - 25);
            // We keep 25 of end which includes crlf but need to account for middle ... too

         file->Write(sAnsiString);
      }

      if (!keepFileOpen)
         file->Close();
   }


   String
   Logger::GetCurrentTime()
   {
      return Time::GetCurrentDateTimeWithMilliseconds();

   }

   int 
   Logger::GetProcessID_()
   {
      DWORD dwProcessID = GetCurrentProcessId();
      return dwProcessID;
   }

   int 
   Logger::GetThreadID_()
   {
      DWORD dwThreadID = GetCurrentThreadId();
      return dwThreadID;

   }

   void
   Logger::LogLive_(String &sMessage)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mtx_LiveLog);

      // Check if we are still enabled. It could be that 
      // we have just disabled ourselves.
      if (!enable_live_log_)
         return;

      live_log_ += sMessage;

      // Check if the live log listeners has stopped listening without telling us.
      if (live_log_.GetLength() > LiveLogMaxSize)
      {
         live_log_.Empty();
         enable_live_log_ = false;
      }
   }

   void 
   Logger::EnableLiveLogging(bool bEnable)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mtx_LiveLog);
      live_log_.Empty();

      enable_live_log_ = bEnable;
   }

   bool 
   Logger::GetLogDebug() const
   {
      return (log_mask_  & LSDebug) > 0; 
   }

   bool 
   Logger::GetLogApplication() const
   {
      return (log_mask_  & LSApplication) > 0; 
   }

   bool 
   Logger::GetLogTCPIP() const
   {
      return (log_mask_  & LSTCPIP) > 0; 
   }

   bool 
   Logger::GetLiveLogEnabled() const
   {
      return enable_live_log_; 
   }
   
   String 
   Logger::GetLiveLog()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mtx_LiveLog);

      String sResult;
      sResult = live_log_;
      
      live_log_.Empty();

      return sResult;
   }

   void
   Logger::LogAWStats(const String &sData)
   {
      WriteData_(sData, AWStats);
   }

   void
   Logger::LogBackup(const String &sData)
   {
      String sTime = GetCurrentTime();
      String sLogMessage;

      sLogMessage.Format(_T("%s\t%s\r\n"), sTime.c_str(), sData.c_str());
      WriteData_(sLogMessage, Backup);
   }
}
