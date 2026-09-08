// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class MiniDumpInput
   {
   public:
      int ProcessId;
      int ThreadId;
#ifdef HM_PLATFORM_POSIX
      // EXCEPTION_RECORD and CONTEXT are the Win32 machine state a mini dump is
      // written from: the record of the fault, and the register file as it stood
      // when the fault was raised. Neither has a POSIX shape to alias, because a
      // core dump here is written by the kernel from a signal context rather than
      // by the program out of a structure it filled in itself. The two members are
      // therefore absent on this platform, and the equivalent arrives with the
      // roadmap row "The Win32 tail" - a core-dump policy. The rest of the class
      // is portable and is left alone so that the file compiles and the name below
      // stays defined.
#else
      EXCEPTION_RECORD ExceptionRecord;
      CONTEXT ContextRecord;
#endif

      wchar_t DumpFile[2048];

      static const std::string SharedMemoryName;
   };

   
}