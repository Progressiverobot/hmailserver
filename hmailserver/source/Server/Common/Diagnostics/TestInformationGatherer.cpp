// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "TestInformationGatherer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   TestInformationGatherer::TestInformationGatherer()
   {

   }

   TestInformationGatherer::~TestInformationGatherer()
   {

   }

   DiagnosticResult
   TestInformationGatherer::PerformTest()
   {
      DiagnosticResult diagResult;
      diagResult.SetName("Server details");
      diagResult.SetDescription("Collects basic server details");

      String result;

      String formattedLine;
      formattedLine.Format(_T("hMailServer version: %s\r\n"), Application::Instance()->GetVersionNumber().c_str());
      result.append(formattedLine);

#ifdef HM_PLATFORM_POSIX
      // sysconf answers both of these questions here, and answers them with the
      // same two numbers the Win32 calls below report: the processors the
      // scheduler will actually run a thread on, and the physical memory the
      // kernel says the machine has. _SC_PHYS_PAGES multiplied by the page size is
      // that total, which is what ullTotalPhys holds.
      //
      // sysconf returns -1 when it does not know, and a diagnostic that printed -1
      // processors would be worse than one that prints 0, so an unknown answer is
      // reported as zero rather than as a negative count.
      const long logical_processors = ::sysconf(_SC_NPROCESSORS_ONLN);

      formattedLine = Formatter::Format("Logical processors: {0}\r\n", (int) (logical_processors > 0 ? logical_processors : 0));
      result.append(formattedLine);

      const long physical_pages = ::sysconf(_SC_PHYS_PAGES);
      const long page_size = ::sysconf(_SC_PAGE_SIZE);

      const unsigned __int64 total_physical_bytes = (physical_pages > 0 && page_size > 0)
         ? (unsigned __int64) physical_pages * (unsigned __int64) page_size
         : 0;

      formattedLine = Formatter::Format("System memory: {0} MB\r\n", total_physical_bytes/1024/1024);
      result.append(formattedLine);
#else
      SYSTEM_INFO system_info;
      GetSystemInfo(&system_info);

      formattedLine = Formatter::Format("Logical processors: {0}\r\n", (int) system_info.dwNumberOfProcessors);
      result.append(formattedLine);

      MEMORYSTATUSEX memory_status;
      memory_status.dwLength = sizeof(memory_status);
      GlobalMemoryStatusEx(&memory_status);
      memory_status.ullTotalPhys;

      formattedLine = Formatter::Format("System memory: {0} MB\r\n", memory_status.ullTotalPhys/1024/1024);
      result.append(formattedLine);
#endif

      String databaseType = DatabaseSettings::GetDatabaseTypeName(IniFileSettings::Instance()->GetDatabaseType());
      formattedLine.Format(_T("Database type: %s\r\n"), databaseType.c_str());
      result.append(formattedLine);

      diagResult.SetSuccess(true);
      diagResult.SetDetails(result);

      return diagResult;
   }


   
      
}
