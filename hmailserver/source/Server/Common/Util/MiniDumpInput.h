// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class MiniDumpInput
   {
   public:
      int ProcessId;
      int ThreadId;
      EXCEPTION_RECORD ExceptionRecord;
      CONTEXT ContextRecord;

      wchar_t DumpFile[2048];

      static const std::string SharedMemoryName;
   };

   
}