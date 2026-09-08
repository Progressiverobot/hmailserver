// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "TestErrorLogs.h"
#include "../Util/FileInfo.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   TestErrorLogs::TestErrorLogs()
   {

   }

   TestErrorLogs::~TestErrorLogs()
   {

   }

   DiagnosticResult
   TestErrorLogs::PerformTest()
   {
      DiagnosticResult diagResult;
      diagResult.SetName("Error logs");
      diagResult.SetDescription("Checks for error logs");

      String result;

      auto log_dir = IniFileSettings::Instance()->GetLogDirectory();

      auto all_error_logs = FileUtilities::GetFilesInDirectory(log_dir, "^ERROR.*$");

      if (all_error_logs.size() > 0)
      {
         // The cast names FormatArgument's unsigned __int64 constructor, which is the
         // one a size_t binds to exactly on Windows. On a 64-bit POSIX build size_t is
         // unsigned long, which matches none of the constructors exactly and is no
         // closer to one than to the others. The cast is an identity on Windows.
         diagResult.SetDetails(Formatter::Format(_T("There are {0} error logs in the log directory."), (unsigned __int64) all_error_logs.size()));
         diagResult.SetSuccess(false);
      }
      else
      {
         diagResult.SetDetails("There are no error logs in the log directory.");
         diagResult.SetSuccess(true);
      }

      return diagResult;
   }


   
      
}
