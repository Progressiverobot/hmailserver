// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "ExceptionHandler.h"
#include "../Util/ExceptionLogger.h"

#include <boost/thread/thread.hpp>


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ExceptionHandler::ExceptionHandler(void)
   {

   }

#ifndef HM_PLATFORM_POSIX
   LONG WINAPI ExceptionFilterWithLogging(EXCEPTION_POINTERS* pExp, DWORD dwExpCode)
   {
      // if an error occurs when shutting down, we want to log it completely before
      // the shut down completes.
      boost::this_thread::disable_interruption shutdown_temporarily_disabled;

      LOG_DEBUG("Logging exception..");

      ExceptionLogger::Log(dwExpCode, pExp);

      LOG_DEBUG("Completed logging of exception...");

      return EXCEPTION_EXECUTE_HANDLER;
   }
#endif
   
   
   bool
   ExceptionHandler::Run(const String &descriptive_name, boost::function<void()>& func)
   {
#ifdef HM_PLATFORM_POSIX
      // The C++ half of this function is unchanged; the hardware half is absent
      // rather than stubbed, and that is the whole of the difference.
      //
      // __try/__except is an MSVC extension, and there is no POSIX equivalent to
      // guard a call with: a fault arrives here as a signal, not as something a
      // filter expression can inspect and swallow. So an exception thrown by the
      // task is still caught, reported and rethrown by RunWithStandardExceptions
      // exactly as it is on Windows, and an access violation or a divide by zero
      // ends the process instead of being logged and turned into a false return.
      // That is not a silent no-op - a false return on Windows means "the task
      // faulted and the fault has been recorded", and here a task that faults does
      // not return at all - and an administrator is told once at start-up by
      // CrashOracle::LogInstallationStatus rather than being left to assume the
      // server is catching its own faults. Closing the gap needs a signal handler
      // and a core-dump policy, which is the roadmap row "The Win32 tail".
      RunWithStandardExceptions(descriptive_name, func);
      return true;
#else
      __try
      {
         RunWithStandardExceptions(descriptive_name, func);
         return true;
      }
      __except (ExceptionFilterWithLogging(GetExceptionInformation(), GetExceptionCode()))
      {
         // this has been logged in the exception filter.
         return false;
      }   
#endif
   }

   void
   ExceptionHandler::RunWithStandardExceptions(const String &descriptive_name, boost::function<void()>& func)
   {
      try
      {
         func();
      }
      catch (boost::thread_interrupted&)
      {
         // shutting down
      }
      catch (boost::system::system_error& error)
      {
         try
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 4208, "ExceptionHandler::Run", GetExceptionText(descriptive_name), error);
         }
         catch (...)
         {
            // Don't swallow the original exception.
         }

         throw;
      }
      catch (std::exception& error)
      {
         String sErrorMessage = 
            Formatter::Format("An error occured while executing '{0}'", descriptive_name);

         try
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 4208, "ExceptionHandler::Run", GetExceptionText(descriptive_name), error);
         }
         catch (...)
         {
            // Don't swallow the origial exception
         }
         
         throw;
      }
      catch (...)
      {
         String sErrorMessage = 
            Formatter::Format("An error occured while executing '{0}'", descriptive_name);

         try
         {
            ErrorManager::Instance()->ReportError(ErrorManager::High, 4208, "ExceptionHandler::Run", GetExceptionText(descriptive_name));
         }
         catch (...)
         {
            // Don't swallow the original exception
         }

         throw;
      }
   }



   String 
   ExceptionHandler::GetExceptionText(const String &descriptive_name)
   {
      String sErrorMessage = 
         Formatter::Format("An error occured while executing '{0}'", descriptive_name);

      return sErrorMessage;

   }

   
} 