// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "ProcessLauncher.h"
#include "Utilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ProcessLauncher::ProcessLauncher(const String &commandLine, const String &workingDirectory) :
      command_line_(commandLine),
      working_directory_(workingDirectory),
      error_log_timeout_(0)
   {
   }

   ProcessLauncher::ProcessLauncher(const String &commandLine) :
      command_line_(commandLine),
      error_log_timeout_(0)
   {
      working_directory_ = IniFileSettings::Instance()->GetTempDirectory();
   }

   ProcessLauncher::~ProcessLauncher(void)
   {
   }

   void
   ProcessLauncher::SetErrorLogTimeout(unsigned int milliseconds)
   {
      error_log_timeout_ = milliseconds;
   }

   bool
   ProcessLauncher::Launch(unsigned int &exitCode)
   {
      FailureReason ignored = FailureReason::None;
      return Launch(exitCode, ignored);
   }

   bool
   ProcessLauncher::Launch(unsigned int &exitCode, FailureReason &failureReason)
   {
      exitCode = 0;
      failureReason = FailureReason::None;

      STARTUPINFO si;
      PROCESS_INFORMATION pi;

      ZeroMemory( &si, sizeof(si) );
      si.cb = sizeof(si);

      si.dwFlags = STARTF_USESHOWWINDOW;
      si.wShowWindow = SW_HIDE;

      ZeroMemory( &pi, sizeof(pi) );

      DWORD creationFlags = 0;

      // Start the child process. 
      if ( !CreateProcess( NULL,    // No module name (use command line). 
         command_line_.GetBuffer(0), // Command line. 
         NULL,                      // Process handle not inheritable. 
         NULL,                      // Thread handle not inheritable. 
         FALSE,                     // Set handle inheritance to FALSE. 
         creationFlags,                         // No creation flags. 
         NULL,                      // Use parent's environment block. 
         working_directory_.GetBuffer(0),        // Use parent's starting directory. 
         &si,                       // Pointer to STARTUPINFO structure.
         &pi )                      // Pointer to PROCESS_INFORMATION structure.
         ) 
      {
         String errorMessage = Formatter::Format("There was an error launching external process {0}. Process start failed. Windows error code: {1}", command_line_, (int) GetLastError());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5401, "ProcessLauncher::Launch", errorMessage); 
         
         failureReason = FailureReason::StartFailed;
         return false;
      }

      command_line_.ReleaseBuffer();
      working_directory_.ReleaseBuffer();

      // A value of zero means the administrator has asked for an unbounded wait.
      //
      // The bound applies only to callers that set an error-log timeout, which is
      // how the virus scanners mark themselves as running on a thread something
      // else is waiting behind. Compression deliberately does not: it is used to
      // build the backup archive, where 7za legitimately runs for far longer than
      // any per-message bound on a large message store, and killing it would
      // truncate the archive.
      int processTimeoutSeconds = error_log_timeout_ > 0
         ? IniFileSettings::Instance()->GetExternalProcessTimeout()
         : 0;

      DWORD remainingTimeout = INFINITE;

      if (processTimeoutSeconds > 0)
      {
         // INFINITE is reserved as the "no bound" marker for WaitForSingleObject, so a
         // configured value is clamped below it rather than being allowed to wrap.
         const DWORD maxTimeoutSeconds = (INFINITE - 1) / 1000;

         remainingTimeout = ((DWORD) processTimeoutSeconds >= maxTimeoutSeconds) ?
            (INFINITE - 1) : ((DWORD) processTimeoutSeconds * 1000);
      }

      DWORD waitResult = 0;

      // Only warn about a slow process if there is time left to keep waiting for it
      // afterwards. If the bound expires first, the wait below reports it instead, so
      // that a single hung process does not produce two errors.
      if (error_log_timeout_ > 0 && (remainingTimeout == INFINITE || error_log_timeout_ < remainingTimeout))
      {
         // If it takes too long time, we should report an error. After that, we
         // should continue to wait.
         waitResult = WaitForSingleObject( pi.hProcess, error_log_timeout_ );
         switch (waitResult)
         {
         case WAIT_ABANDONED:
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5402, "ProcessLauncher::Launch", "Wait abandoned."); 
            break;
         case WAIT_TIMEOUT:
            {
               String errorMessage = Formatter::Format("A launched process did not exit within an expected time. The command line is {0}. The timeout occurred after {1} milliseconds. hMailServer will continue to wait for process to finish.", command_line_, error_log_timeout_);
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5400, "ProcessLauncher::Launch", errorMessage); 
               break;
            }
         case WAIT_FAILED:
            {
               String errorMessage = Formatter::Format("Failed to wait. Windows error code: {0}.", (int) GetLastError());
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5403, "ProcessLauncher::Launch", errorMessage); 
               break;
            }
         }

         if (remainingTimeout != INFINITE)
         {
            remainingTimeout -= error_log_timeout_;
         }
      }

      // Wait until child process exits.
      waitResult = WaitForSingleObject( pi.hProcess, remainingTimeout);

      switch (waitResult)
      {
      case WAIT_ABANDONED:
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5404, "ProcessLauncher::Launch", "Wait abandoned (infinite wait)."); 
            break;
         }
      case WAIT_TIMEOUT:
         {
            const UINT TerminatedProcessExitCode = 0xFFFFFFFF;
            const DWORD TerminationGraceMilliseconds = 5000;

            // Holding the calling thread for the lifetime of a hung child costs a
            // delivery thread permanently, so the child is killed instead.
            String errorMessage = Formatter::Format("A launched process did not exit within the maximum allowed time and has been terminated. The command line is {0}. The maximum time is {1} seconds, configured using ExternalProcessTimeout in hMailServer.ini.", command_line_, processTimeoutSeconds);
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5416, "ProcessLauncher::Launch", errorMessage);

            TerminateProcess(pi.hProcess, TerminatedProcessExitCode);

            // Termination is asynchronous. This wait is bounded so that a process which
            // cannot be killed at all still releases the thread.
            WaitForSingleObject(pi.hProcess, TerminationGraceMilliseconds);

            CloseHandle( pi.hProcess );
            CloseHandle( pi.hThread );

            failureReason = FailureReason::TimedOut;
            return false;
         }
      case WAIT_FAILED:
         {
            String errorMessage = Formatter::Format("Failed to wait. Windows error code: {0}.", (int) GetLastError());
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5405, "ProcessLauncher::Launch", errorMessage); 
            break;
         }
      }

      int result = 0;

      ULONG rc;
      if (!GetExitCodeProcess(pi.hProcess, &rc))
      {
         String errorMessage = Formatter::Format("There was an error determining the exit code of {0}. Windows error: {1}", command_line_, (int) GetLastError());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5400, "ProcessLauncher::Launch", errorMessage); 

         rc = 0;
      }

      exitCode = rc;

      // Close process and thread handles. 
      CloseHandle( pi.hProcess );
      CloseHandle( pi.hThread );

      return true;
   }

}
