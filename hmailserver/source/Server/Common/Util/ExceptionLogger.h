// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class ExceptionLogger
   {
   private:
      ExceptionLogger();

   public:

      // Records a crash: an out-of-process minidump when there is room for one, and
      // an error-log entry naming the exception either way.
      //
      // Guaranteed not to throw. Both callers are inside a structured-exception
      // context - ExceptionHandler::Run calls this from the __except filter
      // EXPRESSION - and a C++ exception thrown while an exception is being
      // dispatched does not unwind to anything useful. See Log_ for the three calls
      // in here that can throw.
      static void Log(int exception_code, EXCEPTION_POINTERS* pExp);

   private:

      static void Log_(int exception_code, EXCEPTION_POINTERS* pExp);

      // True when a dump may be written. When it returns false, refusal_reason says
      // why in a form fit to hand to an administrator - the caller reports the crash
      // regardless, because a crash nobody was told about is worse than a crash with
      // no dump.
      static bool TryToMakeRoom(String &refusal_reason);

      // "Exception code: 0x... (ACCESS_VIOLATION). Address: 0x... Thread: ..." -
      // the facts a crash report is worth once the dump has been rotated away.
      static String DescribeException_(int exception_code, EXCEPTION_POINTERS* pExp);
      static String DescribeExceptionCode_(int exception_code);

      static boost::mutex logging_mutex_;
   };
}