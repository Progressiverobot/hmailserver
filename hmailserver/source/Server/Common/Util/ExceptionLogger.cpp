// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "ExceptionLogger.h"

#include "FileInfo.h"
#include "Time.h"
#include "GUIDCreator.h"
#include "FileUtilities.h"
#include "MiniDumpInput.h"
#include "ProcessLauncher.h"

#include <DbgHelp.h>

#include <boost/interprocess/windows_shared_memory.hpp>
#include <boost/interprocess/mapped_region.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

using namespace boost::interprocess;

namespace HM
{
   boost::mutex ExceptionLogger::logging_mutex_;

   void
   ExceptionLogger::Log(int exception_code, EXCEPTION_POINTERS* pExp)
   //---------------------------------------------------------------------------//
   // DESCRIPTION:
   // Exception barrier for the crash reporter. Nothing may escape from here.
   //
   // ExceptionHandler::Run calls this from inside the __except FILTER expression,
   // which runs while Windows is still dispatching the original exception. A C++
   // exception thrown from there does not unwind to any handler of ours; it costs
   // the whole service, and it costs it at the one moment when the diagnostic is
   // the only thing of value left. Three calls in Log_ can throw on a machine that
   // is in trouble, which is exactly the machine that crashes:
   //
   //   * boost::mutex::scoped_lock, on resource exhaustion.
   //   * windows_shared_memory(create_only, ...), which throws
   //     interprocess_exception if a segment of that name already exists. The name
   //     is the fixed, unqualified "hMailServerMiniDumpMemory", so a second
   //     hMailServer process - a console-mode instance run by an administrator
   //     beside the service, say - is enough to make every crash report kill the
   //     server instead of describing it.
   //   * the directory enumeration and the regular-expression match inside
   //     TryToMakeRoom.
   //
   // CrashOracle::UnhandledExceptionReporter_ already wraps its call in a
   // try/catch, which tells the same story from the other side. The barrier belongs
   // here, once, rather than at each caller.
   //---------------------------------------------------------------------------//
   {
      try
      {
         Log_(exception_code, pExp);
      }
      catch (...)
      {
         try
         {
            // Deliberately not naming the exception: a std::exception::what() call
            // is one more thing that can fault while we are already this far into
            // failure. The exception code and the address are what matters and they
            // are held in locals here, not behind another pointer dereference.
            String message = Formatter::Format(
               "An error has been detected, but hMailServer failed while trying to record it, so no mini dump has been written. {0}",
               DescribeException_(exception_code, pExp));

            ErrorManager::Instance()->ReportError(ErrorManager::Critical, 6016, "ExceptionLogger::Log", message);
         }
         catch (...)
         {
            // Out of options. Better than terminating inside an exception filter.
         }
      }
   }

   void
   ExceptionLogger::Log_(int exception_code, EXCEPTION_POINTERS* pExp)
   {
      boost::mutex::scoped_lock lock(logging_mutex_);

      String exception_description = DescribeException_(exception_code, pExp);

      // The dump writer needs both records. Checked rather than assumed: a null here
      // used to be an access violation raised while an access violation was being
      // dispatched, which ends the process with no record of either fault.
      if (pExp == 0 || pExp->ContextRecord == 0 || pExp->ExceptionRecord == 0)
      {
         String message = Formatter::Format(
            "An error has been detected, but no exception information was supplied, so no mini dump could be written. {0}",
            exception_description);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 6016, "ExceptionLogger::Log", message);
         return;
      }

      // limit the number of logs crated to prevent disk from becoming full.
      String refusal_reason;
      if (!TryToMakeRoom(refusal_reason))
      {
         // Reported, not just skipped. Until this was written, a crash which could
         // not be dumped produced NOTHING an operator would see on a default
         // configuration: the refusal went to LOG_DEBUG, which is off, and the
         // function returned before the error-log entry below. So the eleventh fault
         // in a four-hour window - and every fault after it - was invisible, and the
         // eleventh is often the one that finally made somebody look.
         String message = Formatter::Format(
            "An error has been detected. No mini dump has been written: {0} {1}",
            refusal_reason,
            exception_description);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 6015, "ExceptionLogger::Log", message);
         return;
      }

      String log_directory = IniFileSettings::Instance()->GetLogDirectory();
      String current_date_time = Time::GetCurrentDateTime();
      current_date_time.Replace(_T(":"), _T(""));

      String log_identifier = GUIDCreator::GetGUID();

      String minidump_file_name = "minidump_" + current_date_time + "_" + log_identifier + ".dmp";

      String full_path_to_minidump_file = FileUtilities::Combine(log_directory, minidump_file_name);

      MiniDumpInput view;

      // Bounded before the copy rather than after. _tcscpy_s does not truncate: given
      // a source longer than the destination it calls the invalid parameter handler,
      // which ends the process - so an over-long log path would turn a recoverable
      // fault into a silent death of the service.
      const size_t dump_file_capacity = sizeof(view.DumpFile) / sizeof(view.DumpFile[0]);

      if ((size_t) full_path_to_minidump_file.GetLength() >= dump_file_capacity)
      {
         String message = Formatter::Format(
            "An error has been detected. No mini dump has been written: the path {0} is too long to hand to the mini dump writer. {1}",
            full_path_to_minidump_file,
            exception_description);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 6015, "ExceptionLogger::Log", message);
         return;
      }

      view.ProcessId = ::GetCurrentProcessId();
      view.ThreadId = ::GetCurrentThreadId();
      view.ContextRecord = *pExp->ContextRecord;
      view.ExceptionRecord = *pExp->ExceptionRecord;

      _tcscpy_s(view.DumpFile, dump_file_capacity, full_path_to_minidump_file.c_str());

      windows_shared_memory shm (create_only, MiniDumpInput::SharedMemoryName.c_str(), read_write, 20000);

      mapped_region region(shm, read_write);

      // The region is asked for 20000 bytes, but the size that matters is the one the
      // mapping actually gave us. Checked because the alternative is a memcpy past
      // the end of a shared mapping, inside the crash reporter.
      if (region.get_size() < sizeof(MiniDumpInput))
      {
         String message = Formatter::Format(
            "An error has been detected. No mini dump has been written: the shared memory region is {0} bytes, which is too small for the mini dump input. {1}",
            (unsigned __int64) region.get_size(),
            exception_description);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 6015, "ExceptionLogger::Log", message);
         return;
      }

      //Write all the memory to 1
      std::memcpy(region.get_address(), &view, sizeof(MiniDumpInput));

      String command_line = "hMailServer.minidump.exe";

      unsigned int exit_code;
      ProcessLauncher launcher(command_line);

      // Bounds the wait on the dump writer. Without this, error_log_timeout_ is zero,
      // which makes ProcessLauncher wait INFINITE for the child: a dump writer that
      // wedges - dbghelp on a thrashing machine, or a symbol path pointing at a share
      // that has gone away - held this thread for ever, holding logging_mutex_ with
      // it, so every later crash on every other thread queued behind it and never
      // returned either. A wedged dump writer must cost a dump, not the pool.
      //
      // The value is the warning point; the hard ceiling behind it is
      // ExternalProcessTimeout (300 seconds by default), after which ProcessLauncher
      // kills the child and returns false, and we report HM5521 below.
      launcher.SetErrorLogTimeout(15000);

      if (!launcher.Launch(exit_code))
      {
         String message = Formatter::Format(
            "An error has been detected. hMailServer was unable to launch minidump generator. {0}",
            exception_description);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5521, "ExceptionLogger::Log", message);
         return;
      }

      if (exit_code == 0)
      {
         // The leading sentence and the "written to <path>" shape are load-bearing:
         // ExceptionHandlerTests matches on both, and an operator's own log scraping
         // may well do the same. The exception detail is appended rather than woven
         // in for that reason.
         String message = Formatter::Format(
            "An error has been detected. A mini dump has been written to {0}. {1}",
            full_path_to_minidump_file,
            exception_description);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5519, "ExceptionLogger::Log", message);
      }
      else
      {
         String message = Formatter::Format(
            "An error has been detected. hMailServer attempted to generate minidump, but hMailServer.minidump.exe returned {0}. {1}",
            exit_code,
            exception_description);

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5521, "ExceptionLogger::Log", message);
         return;
      }

   }

   String
   ExceptionLogger::DescribeExceptionCode_(int exception_code)
   {
      // Only the codes that actually turn up in a mail server, and each one is a
      // different conversation with the operator: an access violation is a defect
      // here, a stack overflow is usually unbounded recursion, and an in-page error
      // is normally failing storage rather than our bug at all.
      // The casts follow the same precaution CrashOracle takes with the same
      // constants: minwinbase.h defines each of these as a STATUS_ value, and whether
      // that arrives as a DWORD or as a signed NTSTATUS depends on which of winnt.h
      // and ntstatus.h won the include race in this translation unit. Casting makes
      // the case labels unsigned either way, which /W3 /WX requires.
      switch ((DWORD) exception_code)
      {
      case static_cast<DWORD>(EXCEPTION_ACCESS_VIOLATION):         return "ACCESS_VIOLATION";
      case static_cast<DWORD>(EXCEPTION_STACK_OVERFLOW):           return "STACK_OVERFLOW";
      case static_cast<DWORD>(EXCEPTION_IN_PAGE_ERROR):            return "IN_PAGE_ERROR";
      case static_cast<DWORD>(EXCEPTION_ILLEGAL_INSTRUCTION):      return "ILLEGAL_INSTRUCTION";
      case static_cast<DWORD>(EXCEPTION_PRIV_INSTRUCTION):         return "PRIV_INSTRUCTION";
      case static_cast<DWORD>(EXCEPTION_ARRAY_BOUNDS_EXCEEDED):    return "ARRAY_BOUNDS_EXCEEDED";
      case static_cast<DWORD>(EXCEPTION_DATATYPE_MISALIGNMENT):    return "DATATYPE_MISALIGNMENT";
      case static_cast<DWORD>(EXCEPTION_INT_DIVIDE_BY_ZERO):       return "INT_DIVIDE_BY_ZERO";
      case static_cast<DWORD>(EXCEPTION_INT_OVERFLOW):             return "INT_OVERFLOW";
      case static_cast<DWORD>(EXCEPTION_FLT_DIVIDE_BY_ZERO):       return "FLT_DIVIDE_BY_ZERO";
      case static_cast<DWORD>(EXCEPTION_NONCONTINUABLE_EXCEPTION): return "NONCONTINUABLE_EXCEPTION";
      case static_cast<DWORD>(EXCEPTION_INVALID_HANDLE):           return "INVALID_HANDLE";

      // 0xE06D7363 is 0xE0000000 | 'msc' - the code the MSVC runtime raises for a C++
      // throw. It reaches the crash reporter when an exception escapes a thread that
      // has no matching handler, which is a different conversation from a fault.
      case 0xE06D7363UL:                                           return "C++ exception";

      default:                                                     return "unrecognised";
      }
   }

   String
   ExceptionLogger::DescribeException_(int exception_code, EXCEPTION_POINTERS* pExp)
   //---------------------------------------------------------------------------//
   // DESCRIPTION:
   // The facts a crash report has to carry to be worth reading.
   //
   // Before this existed, exception_code was accepted as a parameter of Log and
   // then never referenced anywhere in this file, so every crash - an access
   // violation, a stack overflow, a divide by zero - was announced with the same
   // sentence and a file name. An administrator who no longer had the .dmp, or had
   // no debugger to open it with, could not tell one fault from another, could not
   // say whether two reports were the same bug, and had nothing to paste into a
   // bug report. The code, the faulting instruction and the address being touched
   // cost nothing to log and are most of what a triager asks for first.
   //---------------------------------------------------------------------------//
   {
      const void *exception_address = 0;
      DWORD number_of_parameters = 0;
      ULONG_PTR access_operation = 0;
      ULONG_PTR access_address = 0;

      if (pExp != 0 && pExp->ExceptionRecord != 0)
      {
         exception_address = pExp->ExceptionRecord->ExceptionAddress;
         number_of_parameters = pExp->ExceptionRecord->NumberParameters;

         if (number_of_parameters >= 2U)
         {
            access_operation = pExp->ExceptionRecord->ExceptionInformation[0];
            access_address = pExp->ExceptionRecord->ExceptionInformation[1];
         }
      }

      String description;
      description.Format(_T("Exception code: 0x%08X (%s). Address: 0x%p. Thread: %u."),
         (unsigned int) exception_code,
         DescribeExceptionCode_(exception_code).c_str(),
         const_cast<void*>(exception_address),
         (unsigned int) ::GetCurrentThreadId());

      // For an access violation and an in-page error, ExceptionInformation says what
      // was attempted and where - which is the difference between "a null pointer was
      // read" and "a write went somewhere it should not have", and that difference is
      // the first question anybody asks.
      if (((DWORD) exception_code == static_cast<DWORD>(EXCEPTION_ACCESS_VIOLATION) ||
           (DWORD) exception_code == static_cast<DWORD>(EXCEPTION_IN_PAGE_ERROR)) &&
          number_of_parameters >= 2U)
      {
         // The values are the ones documented for EXCEPTION_RECORD: 0 read, 1 write,
         // 8 data execution prevention.
         String operation_text = "accessing";

         if (access_operation == 0U)
            operation_text = "reading";
         else if (access_operation == 1U)
            operation_text = "writing";
         else if (access_operation == 8U)
            operation_text = "executing";

         String access_detail;
         access_detail.Format(_T(" The thread was %s address 0x%p."),
            operation_text.c_str(),
            (void*) access_address);

         description += access_detail;
      }

      return description;
   }

   bool 
   ExceptionLogger::TryToMakeRoom(String &refusal_reason)
   {
      // error logs are saved for 4 hours, rolling.

      refusal_reason.Empty();

      String log_directory = IniFileSettings::Instance()->GetLogDirectory();

      std::vector<FileInfo> existing_files = FileUtilities::GetFilesInDirectory(log_directory, "^minidump_.*$");

      const size_t max_count = 10;

      // A second ceiling, on bytes rather than on files, because the count on its own
      // does not bound the disk cost - it only appears to. Ten dumps is a bound in
      // megabytes solely because hMailServer.Minidump.exe passes MiniDumpNormal
      // (stacks and thread contexts, no heap) in a different executable, three
      // directories away. Change that one flag to MiniDumpWithFullMemory and ten
      // dumps of a mail server with a large message store is tens of gigabytes,
      // written onto the volume that holds the mail, while the server is already in
      // trouble. The ceiling is stated here, with the rest of the retention policy,
      // so that it cannot be lost by an edit somewhere else.
      const __int64 max_total_bytes = 100LL * 1024LL * 1024LL;

      __int64 total_bytes = 0;
      for (size_t i = 0; i < existing_files.size(); i++)
         total_bytes += (__int64) FileUtilities::FileSize(FileUtilities::Combine(log_directory, existing_files[i].GetName()));

      while (existing_files.size() >= max_count || total_bytes >= max_total_bytes)
      {
         const size_t none = existing_files.size();
         size_t oldest_index = none;

         // check if there's files older than 4 hours.
         for (size_t i = 0; i < existing_files.size(); i++)
         {
            if (existing_files[i].GetCreateTime().GetStatus() == DateTime::invalid)
               continue;

            if (oldest_index == none ||
                existing_files[i].GetCreateTime() < existing_files[oldest_index].GetCreateTime())
            {
               oldest_index = i;
            }
         }

         if (oldest_index == none)
         {
            // Every candidate has an unreadable creation time, so nothing can be aged
            // out. Deleting one anyway would be a coin toss over which crash report
            // survives.
            refusal_reason = "The creation time of the existing mini dumps could not be read, so none of them can be rotated away.";
            LOG_DEBUG(Formatter::Format("Minidump creation aborted. {0}", refusal_reason));
            return false;
         }

         DateTime now = DateTime::GetCurrentTime();
         DateTimeSpan age = now - existing_files[oldest_index].GetCreateTime();
         double hoursOld = age.GetNumberOfHours();

         if (hoursOld < 4)
         {
            // we keep all error detail logs for 4 hours.
            //
            // This holds for the byte ceiling as well as for the count, and that is
            // deliberate: during a fault storm the first dump is the one that explains
            // the fault and the tenth is another photograph of the symptom. Refusing
            // the new dump loses less than erasing the useful one. The caller reports
            // the crash either way, so nothing goes unrecorded - only undumped.
            if (existing_files.size() >= max_count)
            {
               refusal_reason = Formatter::Format("The max count ({0}) is reached and no log is older than 4 hours.", (int) max_count);
            }
            else
            {
               refusal_reason = Formatter::Format("The mini dumps in the log directory total {0} bytes, which is at or above the {1} byte ceiling, and no log is older than 4 hours.",
                  total_bytes,
                  max_total_bytes);
            }

            LOG_DEBUG(Formatter::Format("Minidump creation aborted. {0}", refusal_reason));
            return false;
         }

         String full_path = FileUtilities::Combine(log_directory, existing_files[oldest_index].GetName());
         __int64 removed_bytes = (__int64) FileUtilities::FileSize(full_path);

         if (!FileUtilities::DeleteFile(full_path))
         {
            // The result is checked so that this loop cannot spin: a file that will
            // not delete would otherwise be selected as the oldest for ever, inside
            // an exception filter. DeleteFile has already reported HM5047.
            refusal_reason = Formatter::Format("The oldest mini dump {0} could not be deleted, so there is no room for another.", full_path);
            LOG_DEBUG(Formatter::Format("Minidump creation aborted. {0}", refusal_reason));
            return false;
         }

         total_bytes -= removed_bytes;
         if (total_bytes < 0)
            total_bytes = 0;

         existing_files.erase(existing_files.begin() + static_cast<std::vector<FileInfo>::difference_type>(oldest_index));
      }

      return true;
   }



} 