// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

// The fuzz harness (fuzz/harness/shim/stdafx.h) supplies a stand-in ErrorManager
// of the same name, because the real one reaches the logger, the configuration
// and the INI settings - an entire server - and defines HM_FUZZ_HARNESS before
// any source includes this header. Mime.cpp includes it directly, so the shim
// cannot shadow it through the include path; the guard is what keeps the two
// definitions apart.
#ifndef HM_FUZZ_HARNESS

namespace HM
{

   class ErrorManager : public Singleton<ErrorManager>
   {
   public:
      ErrorManager();
      virtual ~ErrorManager();

      enum eSeverity
      {
         Critical = 1,
         High = 2,
         Medium = 3,
         Low = 4
      };

      // Records one error: a line in the ERROR log, and the OnError script event so
      // that an administrator's own forwarding can act on it.
      //
      // Does not throw, except for boost::thread_interrupted, which is passed through
      // because it means the server is shutting down. Almost every one of the ~900
      // call sites in the server is inside a catch block or an exception filter, and
      // a reporter that can throw from there turns a handled error into an unhandled
      // one. Four call sites had already wrapped this call in a try/catch of their
      // own, which is the same conclusion reached one file at a time.
      void ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription);
      void ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription, const boost::system::system_error &error);
      void ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription, const boost::system::error_code &error);
      void ReportError(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription, const std::exception &error);

      String GetWindowsErrorText(int windows_error_code);

      static int GetNativeErrorCode(IErrorInfo *pIErrorInfo);

   private:

      void ReportError_(eSeverity iSeverity, int iErrorID, const String &sSource, const String &sDescription);

      // One error record, on one line. Every control character - including a lone CR
      // or a lone LF, which the old "\r\n" -> "[nl]" replacement left alone - becomes
      // printable, so a description carrying text from a peer, a resolver or
      // FormatMessage cannot split one ERROR entry into two.
      static String NormalizeForLog_(const String &value);

      // Makes a value safe to paste into a script source literal in the given
      // language. The OnError event is invoked by building script source text, so
      // anything the value can do to that text, it does.
      static String EscapeForScriptLiteral_(const String &value, const String &script_language);

      // True while this thread is inside the OnError event fired from ReportError.
      // Reporting an error from within that event would fire it again, on the same
      // thread, for ever - see ReportError_.
      static thread_local bool dispatching_on_error_;

   };
}

#endif // HM_FUZZ_HARNESS
