// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "ProcessLauncher.h"
#include "Utilities.h"

#ifdef HM_PLATFORM_POSIX
// fork, execvp and the wait that bounds them; see Launch.
#include <sys/wait.h>
#include <signal.h>
#include <vector>
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
#ifdef HM_PLATFORM_POSIX
   namespace
   {
      // CreateProcess is handed one string and splits it into arguments itself.
      // execvp is handed the arguments already separated, so the split has to
      // happen here.
      //
      // Deliberately NOT by passing the line to /bin/sh -c. A command line in
      // hMailServer.ini names a virus scanner or an archiver and is composed with
      // a file name the server was given by somebody else; a shell would treat a
      // semicolon, a backtick or a $( in that file name as something to run. The
      // Windows path does not go through a shell and neither does this one.
      //
      // The rule is CreateProcess's, reduced to the part that is meaningful here:
      // arguments are separated by white space, and a double quote starts and ends
      // a stretch in which white space is ordinary. A backslash is left alone
      // rather than treated as an escape - on Windows it is a path separator and
      // CommandLineToArgvW's escaping rules exist to cope with that, while here it
      // is an ordinary character in a file name and eating it would corrupt the
      // name.
      std::vector<AnsiString> SplitCommandLine_(const String &commandLine)
      {
         std::vector<AnsiString> arguments;

         const AnsiString line = AnsiString(commandLine);

         AnsiString current;
         bool inArgument = false;
         bool inQuotes = false;

         for (size_t i = 0; i < line.size(); i++)
         {
            // at(), not operator[]: the string class overloads the subscript for
            // int and for unsigned int and neither is a better match for a size_t.
            const char character = line.at(i);

            if (character == '"')
            {
               // A quote marks the argument as started even when it encloses
               // nothing, so that an empty quoted argument survives.
               inQuotes = !inQuotes;
               inArgument = true;
               continue;
            }

            if (!inQuotes && (character == ' ' || character == '\t'))
            {
               if (inArgument)
               {
                  arguments.push_back(current);
                  current.erase();
                  inArgument = false;
               }

               continue;
            }

            current += character;
            inArgument = true;
         }

         if (inArgument)
            arguments.push_back(current);

         return arguments;
      }

      // Waits for one child for at most timeoutMilliseconds, or without bound when
      // that is INFINITE.
      //
      // Returns false only when the wait itself failed - the child is gone in a
      // way that cannot be asked about. Otherwise it returns true and sets exited
      // to say whether the child has finished, which is the same two answers
      // WaitForSingleObject gives with WAIT_OBJECT_0 and WAIT_TIMEOUT.
      //
      // A bounded poll rather than a signal wait: waitpid has no timeout, and
      // sigtimedwait on SIGCHLD would change the disposition of a signal for the
      // whole process, which in a server with dozens of threads is not this
      // function's to change. Twenty milliseconds is short enough that a fast
      // scanner is not noticeably delayed and long enough that a slow one costs
      // nothing measurable.
      bool WaitForChild_(pid_t child, DWORD timeoutMilliseconds, int &status, bool &exited)
      {
         const DWORD poll_interval_ms = 20;

         status = 0;
         exited = false;

         const ULONGLONG deadline = GetTickCount64() + (ULONGLONG) timeoutMilliseconds;

         for (;;)
         {
            const pid_t result = ::waitpid(child, &status, WNOHANG);

            if (result == child)
            {
               exited = true;
               return true;
            }

            if (result < 0)
            {
               if (errno == EINTR)
                  continue;

               return false;
            }

            if (timeoutMilliseconds != INFINITE && GetTickCount64() >= deadline)
               return true;

            Sleep(poll_interval_ms);
         }
      }
   }
#endif

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

#ifdef HM_PLATFORM_POSIX
      // fork and execvp are what CreateProcess is here, with two differences that
      // have to be handled rather than glossed over:
      //
      //   * The arguments are split by this program instead of by the system call;
      //     see SplitCommandLine_ above for why that is not done with a shell.
      //
      //   * fork SUCCEEDS even when the program cannot be started, because the
      //     failure happens in the child after the fork has returned. Without the
      //     pipe below, a mistyped scanner path would look like a child that ran
      //     and exited, and the administrator would be told about a bad exit code
      //     rather than about a path that does not exist. The child writes its
      //     errno into the pipe if execvp fails and the pipe is closed on a
      //     successful exec, so a read of nothing means the program started.
      std::vector<AnsiString> arguments = SplitCommandLine_(command_line_);

      if (arguments.empty())
      {
         String errorMessage = Formatter::Format("There was an error launching external process {0}. The command line is empty.", command_line_);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5401, "ProcessLauncher::Launch", errorMessage);

         failureReason = FailureReason::StartFailed;
         return false;
      }

      std::vector<char *> argv;

      for (size_t i = 0; i < arguments.size(); i++)
         argv.push_back(const_cast<char *>(arguments[i].c_str()));

      argv.push_back(nullptr);

      const AnsiString workingDirectory = AnsiString(working_directory_);

      int failurePipe[2] = { -1, -1 };

      if (::pipe(failurePipe) != 0)
      {
         String errorMessage = Formatter::Format("There was an error launching external process {0}. Process start failed. Error code: {1}", command_line_, (int) GetLastError());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5401, "ProcessLauncher::Launch", errorMessage);

         failureReason = FailureReason::StartFailed;
         return false;
      }

      // Closed automatically by a successful exec, which is what makes the pipe
      // able to say whether the exec happened.
      ::fcntl(failurePipe[1], F_SETFD, FD_CLOEXEC);

      const pid_t child = ::fork();

      if (child < 0)
      {
         const int forkError = errno;

         ::close(failurePipe[0]);
         ::close(failurePipe[1]);

         String errorMessage = Formatter::Format("There was an error launching external process {0}. Process start failed. Error code: {1}", command_line_, forkError);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5401, "ProcessLauncher::Launch", errorMessage);

         failureReason = FailureReason::StartFailed;
         return false;
      }

      if (child == 0)
      {
         // In the child, between fork and exec. Only async-signal-safe calls are
         // allowed here: this process shares the parent's address space image and
         // any lock the parent's other threads happened to be holding when fork
         // was called is held for ever in this copy. Nothing below allocates,
         // takes a lock or reports an error through the server's own machinery -
         // it writes a number down the pipe and stops.
         ::close(failurePipe[0]);

         if (!workingDirectory.empty() && ::chdir(workingDirectory.c_str()) != 0)
         {
            const int reason = errno;
            const ssize_t ignored = ::write(failurePipe[1], &reason, sizeof(reason));
            (void) ignored;
            ::_exit(127);
         }

         ::execvp(argv[0], &argv[0]);

         const int reason = errno;
         const ssize_t ignored = ::write(failurePipe[1], &reason, sizeof(reason));
         (void) ignored;
         ::_exit(127);
      }

      ::close(failurePipe[1]);

      int childError = 0;
      ssize_t errorBytes = 0;

      for (;;)
      {
         errorBytes = ::read(failurePipe[0], &childError, sizeof(childError));

         if (errorBytes >= 0 || errno != EINTR)
            break;
      }

      ::close(failurePipe[0]);

      if (errorBytes == (ssize_t) sizeof(childError))
      {
         // The child never became the program. Reap it so that it does not stay a
         // zombie, then report the same failure the Windows path reports when
         // CreateProcess returns false.
         int discardedStatus = 0;
         bool discardedExited = false;
         WaitForChild_(child, INFINITE, discardedStatus, discardedExited);

         String errorMessage = Formatter::Format("There was an error launching external process {0}. Process start failed. Error code: {1}", command_line_, childError);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5401, "ProcessLauncher::Launch", errorMessage);

         failureReason = FailureReason::StartFailed;
         return false;
      }

      // The timeout arithmetic is the Windows path's, unchanged, because it is the
      // policy rather than the mechanism: an unbounded wait unless the caller set
      // an error-log timeout, a warning once that timeout passes, and a hard
      // ceiling from ExternalProcessTimeout after it.
      int processTimeoutSeconds = error_log_timeout_ > 0
         ? IniFileSettings::Instance()->GetExternalProcessTimeout()
         : 0;

      DWORD remainingTimeout = INFINITE;

      if (processTimeoutSeconds > 0)
      {
         const DWORD maxTimeoutSeconds = (INFINITE - 1) / 1000;

         remainingTimeout = ((DWORD) processTimeoutSeconds >= maxTimeoutSeconds) ?
            (INFINITE - 1) : ((DWORD) processTimeoutSeconds * 1000);
      }

      int status = 0;
      bool exited = false;

      if (error_log_timeout_ > 0 && (remainingTimeout == INFINITE || error_log_timeout_ < remainingTimeout))
      {
         if (!WaitForChild_(child, error_log_timeout_, status, exited))
         {
            String errorMessage = Formatter::Format("Failed to wait. Error code: {0}.", (int) GetLastError());
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5403, "ProcessLauncher::Launch", errorMessage);
         }
         else if (!exited)
         {
            String errorMessage = Formatter::Format("A launched process did not exit within an expected time. The command line is {0}. The timeout occurred after {1} milliseconds. hMailServer will continue to wait for process to finish.", command_line_, error_log_timeout_);
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5400, "ProcessLauncher::Launch", errorMessage);
         }

         if (remainingTimeout != INFINITE)
            remainingTimeout -= error_log_timeout_;
      }

      if (!exited)
      {
         if (!WaitForChild_(child, remainingTimeout, status, exited))
         {
            String errorMessage = Formatter::Format("Failed to wait. Error code: {0}.", (int) GetLastError());
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5405, "ProcessLauncher::Launch", errorMessage);
         }
      }

      if (!exited)
      {
         const DWORD TerminationGraceMilliseconds = 5000;

         // Holding the calling thread for the lifetime of a hung child costs a
         // delivery thread permanently, so the child is killed instead. SIGKILL
         // rather than SIGTERM for the same reason TerminateProcess is used on
         // Windows: this process has already been given the whole configured
         // timeout and has not finished, so asking it politely is one more wait.
         String errorMessage = Formatter::Format("A launched process did not exit within the maximum allowed time and has been terminated. The command line is {0}. The maximum time is {1} seconds, configured using ExternalProcessTimeout in hMailServer.ini.", command_line_, processTimeoutSeconds);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5416, "ProcessLauncher::Launch", errorMessage);

         ::kill(child, SIGKILL);

         // Bounded, so that a process which cannot be killed at all - one wedged in
         // an uninterruptible read from a dead network mount - still releases this
         // thread. The child is then left unreaped, which costs one process table
         // entry and is the lesser of the two.
         WaitForChild_(child, TerminationGraceMilliseconds, status, exited);

         failureReason = FailureReason::TimedOut;
         return false;
      }

      if (WIFEXITED(status))
      {
         exitCode = (unsigned int) WEXITSTATUS(status);
      }
      else if (WIFSIGNALED(status))
      {
         // A child killed by a signal has no exit code of its own. 128 + n is the
         // shell's long-standing spelling of "died on signal n", it is what an
         // administrator reading the number will recognise, and it is outside the
         // 0-127 range a program can return so it cannot be mistaken for one.
         exitCode = (unsigned int) (128 + WTERMSIG(status));
      }

      return true;
#else
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
#endif
   }

}
