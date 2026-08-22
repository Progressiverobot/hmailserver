// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "ErrorManager.h"

#include "WindowsEventLog.h"

#include <oledb.h>

#include <boost/thread/thread.hpp>


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif


#ifndef _ERROR_LOGGING_IN_MESSAGE_BOXES
   #include "../Scripting/ScriptServer.h"
   #include "../Scripting/ScriptObjectContainer.h"
#endif

namespace HM
{
   thread_local bool ErrorManager::dispatching_on_error_ = false;

   namespace
   {
      // Sets the flag for the duration of the OnError dispatch and clears it however
      // the dispatch ends. An early return or an escaping exception must not leave
      // the flag set: this thread would then never fire OnError again, and the
      // administrator's forwarding hook would go quiet on one thread only, which is
      // about the worst failure shape available.
      class OnErrorDispatchGuard
      {
      public:
         explicit OnErrorDispatchGuard(bool &flag) :
            flag_(flag)
         {
            flag_ = true;
         }

         ~OnErrorDispatchGuard()
         {
            flag_ = false;
         }

      private:

         bool &flag_;

         OnErrorDispatchGuard(const OnErrorDispatchGuard &);
         OnErrorDispatchGuard& operator=(const OnErrorDispatchGuard &);
      };
   }

   ErrorManager::ErrorManager()
   {

   }

   ErrorManager::~ErrorManager()
   {

   }

   String
   GetSeverity(ErrorManager::eSeverity iSev)
   {
      switch (iSev)
      {
      case ErrorManager::Critical:
         return "Critical";
      case ErrorManager::High:
         return "High";
      case ErrorManager::Medium:
         return "Medium";
      case ErrorManager::Low:
         return "Low";
      default:
         return "Unknown";
      }
   }

   int 
   ErrorManager::GetNativeErrorCode(IErrorInfo *pIErrorInfo)
   {
      int iRetValue = 0;
      HRESULT hr                          = S_OK;
      IErrorRecords    *pIErrorRecords    = NULL;
      ERRORINFO        errorInfo          = { 0 };
      IErrorInfo       *pIErrorInfoRecord = NULL;

      try
      {
         hr = pIErrorInfo->QueryInterface(IID_IErrorRecords, (void **) &pIErrorRecords);
         if ( FAILED(hr) || NULL == pIErrorRecords )
            return -1;

         pIErrorInfo->Release();
         pIErrorInfo = NULL;

         ULONG ulNumErrorRecs = 0;

         hr = pIErrorRecords->GetRecordCount(&ulNumErrorRecs);
         if ( FAILED(hr) )
            return -1;


         for (DWORD dwErrorIndex = 0; dwErrorIndex < ulNumErrorRecs; dwErrorIndex++)
         {
            hr = pIErrorRecords->GetBasicErrorInfo(dwErrorIndex, &errorInfo);

            if ( FAILED(hr) )
               return -1;

            iRetValue = errorInfo.dwMinor;
         }
      }
      catch( ... )
      {
         iRetValue = errorInfo.dwMinor;
      }

      if( pIErrorInfoRecord )
         pIErrorInfoRecord->Release();

      if ( pIErrorInfo )
         pIErrorInfo->Release();

      if ( pIErrorRecords )
         pIErrorRecords->Release();

      return iRetValue;
   }

   void
   ErrorManager::ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription, const boost::system::system_error &error)
   {
      String formatted_message
         = Formatter::Format(_T("{0}, Error code: {1}, Message: {2}"), sDescription, error.code().value(), error.what());

      ReportError(iSeverity, iErrorID, sSource, formatted_message);
   }

   void
   ErrorManager::ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription, const boost::system::error_code &error)
   {
      String formatted_message
         = Formatter::Format(_T("{0}, Error code: {1}, Message: {2}"), sDescription, error.value(), error.message().c_str());

      ReportError(iSeverity, iErrorID, sSource, formatted_message);
   }

   void
   ErrorManager::ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription, const std::exception &error)
   {
      String formatted_message
         = Formatter::Format(_T("{0}, Message: {1}"), sDescription, error.what());

      ReportError(iSeverity, iErrorID, sSource, formatted_message);
   }


   String
   ErrorManager::GetWindowsErrorText(int windows_error_code)
   {
      LPTSTR message_buf = 0;

      FormatMessage(
         FORMAT_MESSAGE_ALLOCATE_BUFFER | 
         FORMAT_MESSAGE_FROM_SYSTEM |
         FORMAT_MESSAGE_IGNORE_INSERTS,
         NULL,
         windows_error_code,
         MAKELANGID(LANG_NEUTRAL, SUBLANG_DEFAULT),
         (LPTSTR) &message_buf,
         0, NULL );

      if (message_buf == 0)
      {
         // FormatMessage leaves the buffer alone when it fails, and assigning a null
         // pointer into a String yields an empty one - so this used to answer a
         // question about a failure with nothing at all, and the caller logged a
         // Windows error with no explanation after it, which reads as though the
         // operation had succeeded. Failure here is not exotic: the system message
         // table has no text for Winsock secondary codes, for OpenSSL codes, or for
         // any code belonging to a module whose message DLL was not passed in.
         const DWORD format_message_error = ::GetLastError();

         String fallback;
         fallback.Format(_T("Windows error code %d. The system has no description for this code (FormatMessage failed with %u)."),
            windows_error_code,
            (unsigned int) format_message_error);

         return fallback;
      }

      String windows_error_message = message_buf;
      windows_error_message.TrimRight(_T("\r\n "));

      /*
          http://msdn.microsoft.com/en-us/library/windows/desktop/ms679351(v=vs.85).aspx
          The caller should use the LocalFree function to free the buffer when it is no longer needed.
      */

      LocalFree(message_buf);

      return windows_error_message;
   }

   String
   ErrorManager::NormalizeForLog_(const String &value)
   //---------------------------------------------------------------------------//
   // DESCRIPTION:
   // Flattens one error description to a single printable line.
   //
   // The old code replaced "\r\n" with "[nl]" and nothing else, so a lone LF - which
   // is what arrives from a peer's banner, from a resolver, from FormatMessage
   // output and from std::exception::what() on anything that built its text from
   // those - still broke one ERROR entry into two. The second half then looks like a
   // record with no severity and no code, which is worse than a long line: it is a
   // record that lies about its own shape to anything parsing the log.
   //---------------------------------------------------------------------------//
   {
      String result;
      result.reserve(value.GetLength() + 8);

      const int length = value.GetLength();

      for (int i = 0; i < length; i++)
      {
         wchar_t character = value[i];

         if (character == '\r')
         {
            // A CRLF pair collapses to one marker, so existing text reads exactly as
            // it did before; a lone CR gets the same marker instead of being left to
            // truncate the line.
            if (i + 1 < length && value[i + 1] == '\n')
               i++;

            result += _T("[nl]");
         }
         else if (character == '\n')
         {
            result += _T("[nl]");
         }
         else if (character == '\t')
         {
            // Tabs are left alone: they cannot end a record, and the log's own fields
            // are quoted.
            result += character;
         }
         else if (character < 0x20 || character == 0x7F)
         {
            result += _T(' ');
         }
         else
         {
            result += character;
         }
      }

      return result;
   }

   String
   ErrorManager::EscapeForScriptLiteral_(const String &value, const String &script_language)
   {
      String result = value;

      if (script_language == _T("JScript"))
      {
         // Backslash first, and it was not escaped at all before this. Error
         // descriptions are full of Windows paths, and JScript reads the escapes: a
         // handler was handed "C:\temp\..." with a real tab character in it, and a
         // description ending in a backslash escaped the closing quote and turned the
         // generated call into a syntax error. Doing the quote first instead would
         // then double the backslash this pass adds, which is why the order matters.
         result.Replace(_T("\\"), _T("\\\\"));
         result.Replace(_T("'"), _T("\\'"));
      }
      else
      {
         // VBScript: a doubled quote is the only escape a string literal has.
         result.Replace(_T("\""), _T("\"\""));
      }

      return result;
   }

   void
   ErrorManager::ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription)
   {
      try
      {
         ReportError_(iSeverity, iErrorID, sSource, sDescription);
      }
      catch (boost::thread_interrupted&)
      {
         // Shutdown. Never swallowed: the interruption is how the server stops, and
         // absorbing it here would leave a thread running after a stop request
         // because it happened to be reporting an error at the time.
         throw;
      }
      catch (...)
      {
         // Everything else stops here. Reporting an error is what a catch block does
         // when it has already lost, and a reporter that throws from inside one turns
         // a handled failure into an unhandled one - which, on the paths that report
         // from an exception filter, costs the process.
         try
         {
            String message;
            message.Format(_T("Severity: %d, Code: HM%d, Source: %s, Description: (the description could not be rendered; reporting this error threw an exception)"),
               iSeverity, iErrorID, sSource.c_str());

            Logger::Instance()->LogError(message);
         }
         catch (...)
         {
            // The logger is the thing that failed. This is the last place an
            // administrator can still be reached from, and it allocates nothing.
            ::OutputDebugString(_T("hMailServer: ErrorManager::ReportError failed to record an error.\r\n"));
         }
      }
   }

   void
   ErrorManager::ReportError_(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription)
   {
      String sSeverityStr = GetSeverity(iSeverity);

      // One normalisation, used for the log line AND for the script event. Those two
      // used to disagree: the description was flattened for the log and the RAW one
      // was pasted into generated script source, so any error whose text contained a
      // newline produced an OnError call with a line break inside a string literal.
      // Neither VBScript nor JScript permits that, so the call did not parse and the
      // handler never ran - and the errors that carry newlines are the substantial
      // ones (compile reports, resolver text, anything built from FormatMessage). The
      // one hook an administrator has for forwarding errors to a pager or a SIEM
      // silently skipped exactly the errors worth forwarding, leaving only a "Script
      // Error" line about a call that appears in nobody's script.
      String normalized_source = NormalizeForLog_(sSource);
      String normalized_description = NormalizeForLog_(sDescription);

      String sErrorToLog;
      sErrorToLog.Format(_T("Severity: %d (%s), Code: HM%d, Source: %s, Description: %s"), 
                            iSeverity, sSeverityStr.c_str(), iErrorID, normalized_source.c_str(), normalized_description.c_str());

      Logger::Instance()->LogError(sErrorToLog);

      // Mirror into the Windows event log, after the ERROR log write above: the
      // file entry is the record, the event is the signal that points at it. One
      // unconditional call, because everything that keeps this a signal rather
      // than a firehose - the enabled switch, the severity gate, the per-id
      // throttle - lives inside the sink, exactly as the recursion guards for the
      // OnError script event below live here. The sink itself never reports and
      // never logs an error, for the reason this comment sits between those two
      // lines: it is called from inside both of them.
      WindowsEventLog::Instance()->OnError(iSeverity, iErrorID, normalized_source, normalized_description);

      // Send an event if we've been able to load our settings. During database
      // creation, we don't have any PropertySet in the cache but we should still
      // be able to report errors. During server start up, we have a property set (?)
      // but it's not initialized. So we need to be a bit careful here.
      if (!(Configuration::Instance() &&
            Configuration::Instance()->GetPropertySet() &&
            Configuration::Instance()->GetUseScriptServer()))
      {
         return;
      }

      if (dispatching_on_error_)
      {
         // An error reported from inside the OnError event, on the thread running it.
         // Firing OnError again would report the same failure one frame deeper and
         // repeat until the stack is exhausted, and a stack overflow on a worker
         // thread is the whole service rather than one connection. Two paths reach
         // here today with nothing to stop them: ScriptServer::FireEvent (HM5018,
         // when a script site cannot be created - so, under memory pressure) and
         // ScriptServer::ScriptWatchdog::Disarm (HM5022, when the watchdog timer
         // cannot be cancelled).
         //
         // Two OTHER sites in ScriptServer avoid ReportError entirely, each with a
         // comment describing this same recursion. That is the fix applied by hand at
         // the sites somebody thought of; this is it applied once, where it also
         // holds for the sites nobody thought of.
         //
         // Logged rather than reported, for the reason being described.
         Logger::Instance()->LogError(Formatter::Format(
            "The OnError event was not fired for error HM{0} above, because that error was reported from inside the OnError event on the same thread. Firing it again would recurse until the stack was exhausted. The OnError handler in the event script has to be able to run without itself causing an error to be reported.",
            iErrorID));

         return;
      }

      String sScriptLanguage = Configuration::Instance()->GetScriptLanguage();

      String sEventCaller;

      if (sScriptLanguage == _T("VBScript"))
      {
         sEventCaller.Format(_T("OnError(%d, %d, \"%s\", \"%s\")"),
            iSeverity,
            iErrorID,
            EscapeForScriptLiteral_(normalized_source, sScriptLanguage).c_str(),
            EscapeForScriptLiteral_(normalized_description, sScriptLanguage).c_str());
      }
      else if (sScriptLanguage == _T("JScript"))
      {
         sEventCaller.Format(_T("OnError(%d, %d, '%s', '%s')"),
            iSeverity,
            iErrorID,
            EscapeForScriptLiteral_(normalized_source, sScriptLanguage).c_str(),
            EscapeForScriptLiteral_(normalized_description, sScriptLanguage).c_str());
      }
      else
      {
         // A language we cannot quote for is a language we must not build source for.
         // Previously an empty caller was handed to FireEvent regardless, which
         // appended a bare "Call" to the script and produced a script error about a
         // line nobody wrote.
         return;
      }

      std::shared_ptr<ScriptObjectContainer> pContainer  = std::shared_ptr<ScriptObjectContainer>(new ScriptObjectContainer);

      OnErrorDispatchGuard on_error_guard(dispatching_on_error_);

      ScriptServer::Instance()->FireEvent(ScriptServer::EventOnError, sEventCaller, pContainer);
   }


}
