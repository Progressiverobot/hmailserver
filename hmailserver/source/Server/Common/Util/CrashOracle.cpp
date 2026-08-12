// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "CrashOracle.h"

#include "ExceptionLogger.h"
#include "FileUtilities.h"
#include "Utilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The file the regression suite reads to find out whether a memory-safety
      // fault happened during a test. Deliberately not named hmailserver_*.log:
      // LogRetentionTask only ever deletes files with that prefix, so the crash
      // record cannot be tidied away underneath a test run.
      const wchar_t *marker_file_name = L"crash-oracle.log";

      // Hard ceiling on the marker file. A fault inside a retry loop can raise the
      // same access violation thousands of times a second, and the one thing a
      // diagnostic must never do is fill the disk of a mail server. Past this size
      // records are counted but not written; the counters keep the true total, and
      // deleting the file (which is what the test helper does) restores writing.
      const LONGLONG max_marker_file_size = 256 * 1024;

      // How long the fatal path is allowed to spend reporting before the watchdog
      // kills the process anyway. Generous, because it includes launching
      // hMailServer.minidump.exe and waiting for it, but finite: a wedged reporter
      // in a dying process leaves the service "running" and broken, which is worse
      // than a crash.
      const DWORD fatal_report_deadline_ms = 60 * 1000;

      wchar_t marker_file_[1024] = { 0 };

      PVOID vectored_handler_ = 0;

      volatile LONG installed_ = 0;
      volatile LONG memory_safety_events_ = 0;
      volatile LONG fatal_events_ = 0;
      volatile LONG reporting_fatal_ = 0;
      volatile LONG fatal_report_finished_ = 0;
      volatile LONG shutdown_started_ = 0;

      // Set while this thread is inside the first-chance observer. If the observer
      // itself faults the flag stays set, which permanently disables observation on
      // that one thread - the safe direction to fail, since the alternative is
      // unbounded recursion inside an exception handler.
      __declspec(thread) bool in_first_chance_observer_ = false;

      void
      AppendAscii_(char *buffer, size_t buffer_size, size_t &offset, const char *text)
      {
         while (*text != 0 && offset + 1 < buffer_size)
         {
            buffer[offset] = *text;
            offset++;
            text++;
         }
      }

      void
      AppendUnsigned_(char *buffer, size_t buffer_size, size_t &offset, unsigned __int64 value, unsigned int base_value, int minimum_digits)
      {
         char digits[24];
         int count = 0;

         do
         {
            unsigned int digit = static_cast<unsigned int>(value % base_value);
            digits[count] = static_cast<char>(digit < 10 ? ('0' + digit) : ('A' + digit - 10));
            count++;
            value /= base_value;
         }
         while (value != 0 && count < 24);

         while (count < minimum_digits && count < 24)
         {
            digits[count] = '0';
            count++;
         }

         while (count > 0 && offset + 1 < buffer_size)
         {
            count--;
            buffer[offset] = digits[count];
            offset++;
         }
      }

      void
      AppendTimestamp_(char *buffer, size_t buffer_size, size_t &offset)
      {
         // GetLocalTime rather than a HM::Time helper: everything in this file has
         // to be callable on a thread whose stack has just been corrupted, so it
         // sticks to Win32 calls that neither allocate nor take a CRT lock. Local
         // time to match the timestamps in the ordinary logs.
         SYSTEMTIME local_time;
         ::GetLocalTime(&local_time);

         AppendUnsigned_(buffer, buffer_size, offset, local_time.wYear, 10U, 4);
         AppendAscii_(buffer, buffer_size, offset, "-");
         AppendUnsigned_(buffer, buffer_size, offset, local_time.wMonth, 10U, 2);
         AppendAscii_(buffer, buffer_size, offset, "-");
         AppendUnsigned_(buffer, buffer_size, offset, local_time.wDay, 10U, 2);
         AppendAscii_(buffer, buffer_size, offset, " ");
         AppendUnsigned_(buffer, buffer_size, offset, local_time.wHour, 10U, 2);
         AppendAscii_(buffer, buffer_size, offset, ":");
         AppendUnsigned_(buffer, buffer_size, offset, local_time.wMinute, 10U, 2);
         AppendAscii_(buffer, buffer_size, offset, ":");
         AppendUnsigned_(buffer, buffer_size, offset, local_time.wSecond, 10U, 2);
         AppendAscii_(buffer, buffer_size, offset, ".");
         AppendUnsigned_(buffer, buffer_size, offset, local_time.wMilliseconds, 10U, 3);
      }
   }

   CrashOracle::CrashOracle()
   {

   }

   void
   CrashOracle::Install()
   {
      if (::InterlockedCompareExchange(&installed_, 1, 0) != 0)
         return;

      // Resolve the destination up front. The handlers must never have to work out
      // where to write while an exception is in flight.
      ResolveMarkerFile_();

      // First = 1: run before any other vectored handler, and long before any
      // frame-based handler. This is the whole point - a catch (...) compiled under
      // /EHa will swallow an access violation, but it cannot stop this from seeing
      // it first.
      vectored_handler_ = ::AddVectoredExceptionHandler(1, FirstChanceExceptionObserver_);

      // The return value is the filter that was installed before us. Grep says there
      // was none anywhere in hmailserver/source/Server, so there is nothing to chain
      // to and the previous value is deliberately discarded. If a filter is ever
      // added elsewhere, this is the line that has to start chaining instead.
      ::SetUnhandledExceptionFilter(UnhandledExceptionReporter_);
   }

   void
   CrashOracle::LogInstallationStatus()
   {
      // LOG_APPLICATION, never ErrorManager: this runs on every start of a healthy
      // default installation, and a diagnostic that writes to the error log on a
      // default configuration makes the error log useless.
      if (vectored_handler_ == 0)
      {
         LOG_APPLICATION(_T("Crash oracle: the first-chance exception observer could not be installed. Memory-safety faults swallowed by a catch-all handler will not be recorded."));
         return;
      }

      String message;

      if (marker_file_[0] == 0)
      {
         message = _T("Crash oracle armed, but no writable location for its records was found. Faults will be reported to the error log only.");
      }
      else
      {
         message.Format(_T("Crash oracle armed. Memory-safety faults are recorded in %s."),
            static_cast<const wchar_t *>(marker_file_));
      }

      LOG_APPLICATION(message);
   }

   void
   CrashOracle::NotifyShutdownStarted()
   {
      ::InterlockedExchange(&shutdown_started_, 1);
   }

   long
   CrashOracle::GetMemorySafetyEventCount()
   {
      return memory_safety_events_;
   }

   long
   CrashOracle::GetFatalEventCount()
   {
      return fatal_events_;
   }

   String
   CrashOracle::GetMarkerFile()
   {
      if (marker_file_[0] == 0)
         return String();

      return String(marker_file_);
   }

   bool
   CrashOracle::IsMemorySafetyException_(DWORD exception_code)
   {
      // An allow-list, not a deny-list. The server raises tens of thousands of
      // structured exceptions in a normal run - every C++ throw is one - so the
      // observer has to recognise the small set of codes that always mean a defect
      // and ignore everything else.
      //
      // Deliberately NOT in this list:
      //   0xE06D7363 C++ throw          - the normal mechanism, constantly raised
      //   0xE0434352 CLR exception      - same, from managed callers
      //   0x80000001 guard page         - how Windows grows a thread stack
      //   0x80000003 breakpoint         - debugger, and __debugbreak in tools
      //   0x4000001E / 0x80000004 step  - debugger
      //   0x40010006 OutputDebugString  - raised by every OutputDebugString call
      //   0x406D1388 thread name        - raised to name a thread in a debugger
      //   0x80000002 misalignment       - continuable, and not raised at all on x64
      //   0xC0000008 invalid handle     - raised on demand by handle tracing
      //   any float exception           - masked by default, so never raised, and
      //                                   deliberately unmasked by some libraries
      //
      // Fast reject first: the customer/application bit (0x20000000) is set on every
      // application-defined code, which covers both of the codes this handler is
      // asked about most often - a C++ throw and a CLR exception - so they cost one
      // bit test each.
      if ((exception_code & 0x20000000) != 0)
         return false;

      const DWORD memory_safety_codes[] =
      {
         static_cast<DWORD>(EXCEPTION_ACCESS_VIOLATION),       // 0xC0000005
         static_cast<DWORD>(EXCEPTION_IN_PAGE_ERROR),          // 0xC0000006
         static_cast<DWORD>(EXCEPTION_ILLEGAL_INSTRUCTION),    // 0xC000001D
         static_cast<DWORD>(EXCEPTION_ARRAY_BOUNDS_EXCEEDED),  // 0xC000008C
         static_cast<DWORD>(EXCEPTION_INT_DIVIDE_BY_ZERO),     // 0xC0000094
         static_cast<DWORD>(EXCEPTION_PRIV_INSTRUCTION),       // 0xC0000096
         static_cast<DWORD>(EXCEPTION_STACK_OVERFLOW),         // 0xC00000FD
         0xC0000374,                                           // STATUS_HEAP_CORRUPTION
         0xC0000409,                                           // STATUS_STACK_BUFFER_OVERRUN
         0xC0000420                                            // STATUS_ASSERTION_FAILURE
      };

      const size_t count = sizeof(memory_safety_codes) / sizeof(memory_safety_codes[0]);

      for (size_t i = 0; i < count; i++)
      {
         if (memory_safety_codes[i] == exception_code)
            return true;
      }

      return false;
   }

   void
   CrashOracle::ResolveMarkerFile_()
   {
      marker_file_[0] = 0;

      String candidate;

      try
      {
         // First choice: the log directory. It is the one directory the service is
         // guaranteed to be able to write to - every log line and the existing
         // minidumps already go there - so a record written next to the dump it
         // describes needs no new permission and no new configuration.
         String log_directory = IniFileSettings::Instance()->GetLogDirectory();

         if (!log_directory.IsEmpty())
         {
            if (!FileUtilities::DirectoryExists(log_directory))
            {
               // Win32 directly rather than FileUtilities::CreateDirectory, which
               // reports to the error log on failure. This runs before the
               // application is initialised and a failure here is not worth an
               // error-log entry; we simply fall through to the next candidate.
               ::CreateDirectoryW(log_directory.c_str(), NULL);
            }

            if (FileUtilities::DirectoryExists(log_directory))
               candidate = FileUtilities::Combine(log_directory, String(marker_file_name));
         }

         // Second choice: next to hMailServer.exe. If the log directory is
         // misconfigured, this is where the ini file already lives.
         if (candidate.IsEmpty())
         {
            String bin_directory = Utilities::GetBinDirectory();

            if (!bin_directory.IsEmpty() && FileUtilities::DirectoryExists(bin_directory))
               candidate = FileUtilities::Combine(bin_directory, String(marker_file_name));
         }
      }
      catch (...)
      {
         // Nothing here is allowed to stop the server starting.
         candidate = _T("");
      }

      // Last resort: the temp directory. Always writable, easy to miss, but better
      // than losing the record.
      if (candidate.IsEmpty())
      {
         wchar_t temp_path[MAX_PATH + 1];
         temp_path[0] = 0;

         DWORD length = ::GetTempPathW(MAX_PATH, temp_path);

         if (length > 0 && length <= MAX_PATH)
         {
            try
            {
               candidate = FileUtilities::Combine(String(temp_path), String(marker_file_name));
            }
            catch (...)
            {
               candidate = _T("");
            }
         }
      }

      const size_t capacity = sizeof(marker_file_) / sizeof(marker_file_[0]);

      if (!candidate.IsEmpty() && static_cast<size_t>(candidate.GetLength()) < capacity)
         ::wcscpy_s(marker_file_, capacity, candidate.c_str());
   }

   void
   CrashOracle::AppendMarkerRecord_(const char *kind, DWORD exception_code, const void *exception_address, DWORD thread_id, long event_number)
   {
      if (marker_file_[0] == 0)
         return;

      // One line, built in a small stack buffer with hand-rolled formatting. No
      // allocation, no CRT locale lock, no HM::String: this has to work on a thread
      // that has just overrun a buffer, and on a thread whose stack is nearly
      // exhausted (EXCEPTION_STACK_OVERFLOW).
      //
      // The faulting address is recorded raw, with no module or symbol lookup.
      // GetModuleHandleEx would take the loader lock, and a fault raised during DLL
      // initialisation holds that lock already - deadlocking the observer would turn
      // a diagnostic into an outage. Resolving the address is the minidump's job.
      char line[256];
      size_t offset = 0;

      AppendTimestamp_(line, sizeof(line), offset);
      AppendAscii_(line, sizeof(line), offset, " ");
      AppendAscii_(line, sizeof(line), offset, kind);
      AppendAscii_(line, sizeof(line), offset, " code=0x");
      AppendUnsigned_(line, sizeof(line), offset, exception_code, 16U, 8);
      AppendAscii_(line, sizeof(line), offset, " address=0x");
      AppendUnsigned_(line, sizeof(line), offset, reinterpret_cast<ULONG_PTR>(exception_address), 16U, 16);
      AppendAscii_(line, sizeof(line), offset, " thread=");
      AppendUnsigned_(line, sizeof(line), offset, thread_id, 10U, 1);
      AppendAscii_(line, sizeof(line), offset, " pid=");
      AppendUnsigned_(line, sizeof(line), offset, ::GetCurrentProcessId(), 10U, 1);
      AppendAscii_(line, sizeof(line), offset, " event=");
      AppendUnsigned_(line, sizeof(line), offset, static_cast<unsigned __int64>(event_number), 10U, 1);
      AppendAscii_(line, sizeof(line), offset, "\r\n");

      // FILE_APPEND_DATA with a shared handle: a single write of well under 4 KB to
      // a handle opened for append is atomic, so concurrent faults on several
      // threads cannot interleave half-lines. FILE_SHARE_* so that the test suite
      // (and an administrator with a text editor) can read the file while the server
      // holds it.
      HANDLE file = ::CreateFileW(
         marker_file_,
         FILE_APPEND_DATA | FILE_READ_ATTRIBUTES,
         FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
         NULL,
         OPEN_ALWAYS,
         FILE_ATTRIBUTE_NORMAL,
         NULL);

      if (file == INVALID_HANDLE_VALUE)
         return;

      LARGE_INTEGER size;
      size.QuadPart = 0;

      if (::GetFileSizeEx(file, &size) && size.QuadPart < max_marker_file_size)
      {
         DWORD written = 0;
         ::WriteFile(file, line, static_cast<DWORD>(offset), &written, NULL);

         // Flushed on purpose. The interesting case is a process that is about to
         // die, or one that is about to be killed by a frustrated administrator;
         // a record still sitting in the file cache is a record that never existed.
         ::FlushFileBuffers(file);
      }

      ::CloseHandle(file);
   }

   LONG CALLBACK
   CrashOracle::FirstChanceExceptionObserver_(EXCEPTION_POINTERS *exception_pointers)
   //---------------------------------------------------------------------------//
   // Records memory-safety faults at first chance and changes nothing else. Runs
   // before any frame-based handler, which is what makes a swallowed access
   // violation visible.
   //---------------------------------------------------------------------------//
   {
      if (exception_pointers == 0 || exception_pointers->ExceptionRecord == 0)
         return EXCEPTION_CONTINUE_SEARCH;

      const DWORD exception_code = exception_pointers->ExceptionRecord->ExceptionCode;

      if (!IsMemorySafetyException_(exception_code))
         return EXCEPTION_CONTINUE_SEARCH;

      if (in_first_chance_observer_)
         return EXCEPTION_CONTINUE_SEARCH;

      in_first_chance_observer_ = true;

      const LONG event_number = ::InterlockedIncrement(&memory_safety_events_);

      AppendMarkerRecord_(
         "FIRSTCHANCE",
         exception_code,
         exception_pointers->ExceptionRecord->ExceptionAddress,
         ::GetCurrentThreadId(),
         event_number);

      in_first_chance_observer_ = false;

      // Always. This handler is an observer; deciding the fate of the exception is
      // somebody else's job, and returning EXCEPTION_CONTINUE_EXECUTION here would
      // silently resume a corrupted process.
      return EXCEPTION_CONTINUE_SEARCH;
   }

   DWORD WINAPI
   CrashOracle::FatalReportWatchdog_(LPVOID /*parameter*/)
   //---------------------------------------------------------------------------//
   // Guarantees that the fatal path terminates. The reporting it guards takes a
   // mutex and launches a child process; if the crash happened inside that same
   // reporting code the mutex is already held by this thread and the wait would
   // never end, leaving a wedged process that the service control manager still
   // believes is running.
   //---------------------------------------------------------------------------//
   {
      ::Sleep(fatal_report_deadline_ms);

      if (fatal_report_finished_ != 0)
         return 0;

      ::TerminateProcess(::GetCurrentProcess(), static_cast<UINT>(0xC0000420));

      return 0;
   }

   LONG WINAPI
   CrashOracle::UnhandledExceptionReporter_(EXCEPTION_POINTERS *exception_pointers)
   //---------------------------------------------------------------------------//
   // Last stop for an exception nobody handled. Writes a dump, says so loudly, and
   // then makes sure the process really does die.
   //---------------------------------------------------------------------------//
   {
      const bool have_record = (exception_pointers != 0 && exception_pointers->ExceptionRecord != 0);

      const DWORD exception_code = have_record ? exception_pointers->ExceptionRecord->ExceptionCode : 0;
      const void *exception_address = have_record ? exception_pointers->ExceptionRecord->ExceptionAddress : 0;
      const DWORD thread_id = ::GetCurrentThreadId();

      if (::InterlockedCompareExchange(&reporting_fatal_, 1, 0) != 0)
      {
         // Another thread is already reporting a fatal exception. Do not queue
         // behind it - two dying threads reporting at once is how a crash becomes a
         // hang. The first reporter owns the record; this one just leaves.
         ::TerminateProcess(::GetCurrentProcess(), exception_code != 0 ? exception_code : 1U);
         return EXCEPTION_EXECUTE_HANDLER;
      }

      // Started before anything that touches the heap, so that it exists even if the
      // heap is the thing that is broken.
      HANDLE watchdog = ::CreateThread(NULL, 0, FatalReportWatchdog_, NULL, 0, NULL);
      if (watchdog != NULL)
         ::CloseHandle(watchdog);

      const LONG event_number = ::InterlockedIncrement(&fatal_events_);

      AppendMarkerRecord_("UNHANDLED", exception_code, exception_address, thread_id, event_number);

      // Reuse the existing minidump writer rather than adding a second one.
      // ExceptionLogger::Log fills shared memory with a copy of the exception and
      // context records and launches hMailServer.Minidump.exe, which calls
      // MiniDumpWriteDump from outside the faulted process. That out-of-process
      // design is the reason to keep it: writing a dump of yourself means running
      // dbghelp inside the address space you are trying to photograph. It also
      // already owns the flags and the rotation policy -
      //   * MiniDumpNormal: stacks and thread contexts, no heap. A few hundred KB
      //     instead of hundreds of MB, and - the reason it matters here - no message
      //     bodies, passwords or TLS keys copied out of the heap into a file
      //     an administrator will happily email to a mailing list.
      //   * ten dumps kept, and none deleted until it is four hours old, so a fault
      //     storm cannot fill the disk and cannot erase the first, most useful dump.
      // If a dump with heap ever becomes necessary it belongs behind an explicit,
      // off-by-default setting, not as the standing behaviour.
      if (have_record && exception_pointers->ContextRecord != 0)
      {
         try
         {
            ExceptionLogger::Log(static_cast<int>(exception_code), exception_pointers);
         }
         catch (...)
         {
            // A dump that could not be written must not cost us the one thing that
            // always works: the marker record and the error-log entry below.
            AppendMarkerRecord_("DUMPFAILED", exception_code, exception_address, thread_id, event_number);
         }
      }

      // Loud. ErrorManager is right here and not a rule 6 violation: this cannot
      // fire on a healthy server, by definition - it took an unhandled exception to
      // get here.
      try
      {
         String message;
         message.Format(
            _T("hMailServer is terminating because of an unhandled exception. Exception code: 0x%08X. Address: 0x%p. Thread: %u. This is a defect in hMailServer, not a configuration problem. A crash oracle record has been written to %s."),
            exception_code,
            exception_address,
            thread_id,
            marker_file_[0] == 0
               ? static_cast<const wchar_t *>(_T("(nowhere - no writable location was found)"))
               : static_cast<const wchar_t *>(marker_file_));

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5820, "CrashOracle::UnhandledExceptionReporter_", message);
      }
      catch (...)
      {
         AppendMarkerRecord_("REPORTFAILED", exception_code, exception_address, thread_id, event_number);
      }

      ::InterlockedExchange(&fatal_report_finished_, 1);

      if (shutdown_started_ != 0 && !IsMemorySafetyException_(exception_code))
      {
         // An orderly shutdown is already under way and this is not a memory-safety
         // fault - most likely an exception escaping a thread that is being torn
         // down. It has been recorded and reported; killing the process here would
         // race the SCM stop notification and turn a clean stop into a reported
         // service failure. Let the normal path finish.
         return EXCEPTION_EXECUTE_HANDLER;
      }

      // The process must not continue as though nothing happened. The exception code
      // becomes the exit code, so the Windows event log entry for the terminated
      // service names the fault (3221225477 / 0xC0000005 for an access violation)
      // and the regression suite's ServiceRestartDetector sees the process id change
      // at the very next test.
      ::TerminateProcess(::GetCurrentProcess(), exception_code != 0 ? exception_code : 1U);

      // Not reached. Never EXCEPTION_CONTINUE_EXECUTION: that would resume at the
      // faulting instruction and fault again, forever.
      return EXCEPTION_EXECUTE_HANDLER;
   }
}
