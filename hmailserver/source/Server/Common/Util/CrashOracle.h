// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   /// The crash oracle. Its only job is to make a memory-safety fault visible to
   /// something outside the process, so that a test run cannot be green while the
   /// server is corrupting its own memory.
   ///
   /// Two hooks, doing two different jobs:
   ///
   /// 1. A vectored exception handler, installed as the *first* first-chance
   ///    handler. Windows delivers every exception to vectored handlers before it
   ///    looks for a frame-based handler, so this sees an access violation even
   ///    when a catch (...) further up the stack goes on to swallow it. That is the
   ///    case the project builds itself into: with <ExceptionHandling>Async</...>
   ///    (/EHa) a catch (...) catches structured exceptions too, so a null
   ///    dereference inside any try block can be logged as an ordinary failure and
   ///    forgotten. The observer never changes control flow - it always returns
   ///    EXCEPTION_CONTINUE_SEARCH - it only appends a record to the marker file.
   ///
   /// 2. An unhandled-exception filter, for the faults nobody catches. It writes a
   ///    minidump through the existing out-of-process writer and then fails loudly:
   ///    a Critical error in the hMailServer error log, a record in the marker file,
   ///    and process termination with the exception code as the exit code, so the
   ///    Windows service control manager records the death. It deliberately does
   ///    not let the process limp on as though nothing happened.
   ///
   /// Neither hook runs on the normal code path, and neither one fires on the
   /// shipped default configuration - a healthy server raises no memory-safety
   /// exceptions and has no unhandled ones, so the marker file is never created.
   class CrashOracle
   {
   public:

      /// Installs both hooks. Safe to call more than once; only the first call does
      /// anything. Call this as early as possible in the process - both hooks are
      /// process-wide, so one installation covers every thread created later.
      static void Install();

      /// Writes one LOG_APPLICATION line saying whether the oracle is armed and
      /// where its records go. Separate from Install() because Install() runs before
      /// the logging subsystem has a log mask, which would make the line vanish.
      static void LogInstallationStatus();

      /// Tells the oracle that an orderly service shutdown has begun, so that an
      /// exception thrown by a thread being torn down is recorded but is not
      /// escalated to a hard process kill that would race the SCM's stop
      /// notification. A memory-safety fault is still treated as fatal, shutdown or
      /// not, because that is a real defect either way.
      static void NotifyShutdownStarted();

      /// Number of memory-safety exceptions seen at first chance since process
      /// start, whether or not anything went on to swallow them. Intended for the
      /// metrics/health surface; see the note in CrashOracle.cpp.
      static long GetMemorySafetyEventCount();

      /// Number of unhandled exceptions the fatal path has reported.
      static long GetFatalEventCount();

      /// Full path of the marker file, or an empty string if no writable location
      /// could be found.
      static String GetMarkerFile();

   private:
      CrashOracle();

      static LONG CALLBACK FirstChanceExceptionObserver_(EXCEPTION_POINTERS *exception_pointers);
      static LONG WINAPI UnhandledExceptionReporter_(EXCEPTION_POINTERS *exception_pointers);

      static DWORD WINAPI FatalReportWatchdog_(LPVOID parameter);

      static bool IsMemorySafetyException_(DWORD exception_code);

      static void ResolveMarkerFile_();

      static void AppendMarkerRecord_(const char *kind, DWORD exception_code, const void *exception_address, DWORD thread_id, long event_number);
   };
}
