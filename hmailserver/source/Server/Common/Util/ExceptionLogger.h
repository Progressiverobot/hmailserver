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
      static void Log(int exception_code, EXCEPTION_POINTERS* pExp);

   private:

      static void CreateMiniDump_(EXCEPTION_POINTERS* pExp, const String &file_name);

      static bool TryToMakeRoom();

      static boost::mutex logging_mutex_;
   };
}