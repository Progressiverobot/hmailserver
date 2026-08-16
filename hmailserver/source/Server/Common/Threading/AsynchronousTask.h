// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Task.h"

namespace HM
{
   template <class T>
   class AsynchronousTask : public Task
   {
   public:
      // The name appears in the work queue diagnostics and in the stall report,
      // so pass something that identifies the originating session where one is
      // available. Callers with nothing to identify fall back to the shared
      // default and are then only distinguishable by thread id.
      AsynchronousTask(std::function<void()> functionToRun, std::shared_ptr<T> parentHolder, const String &name = "AsynchronousTask") :
         Task(name),
         asynchronousFunction_(functionToRun),
         parentHolder_(parentHolder)
      {

      }

      virtual void DoWork()
      //---------------------------------------------------------------------------
      // DESCRIPTION:
      // Runs the function, and releases the parent TCP connection on every path out
      // of here - it is that shared_ptr which keeps the connection and its socket
      // alive, so a task that dies without dropping it leaks the connection for the
      // life of the process.
      //
      // What is new is that the failure paths now say something. This was a bare
      // catch (...) with an empty body, and the server builds with /EHa, so it took
      // structured exceptions as readily as thrown ones: an access violation inside
      // message finalization was caught here, the task was abandoned, the session
      // was left unanswered, and not one line was written anywhere. That is the
      // exact shape of defect this pass exists to remove.
      //
      // Reported to the application log rather than through ErrorManager on purpose.
      // These tasks run the accept-and-save path for every received message, script
      // events included, and an error record here would appear in the ERROR log of a
      // correctly configured server. The application log is where an operator can
      // see it without that cost.
      //---------------------------------------------------------------------------
      {
         try
         {
            asynchronousFunction_();
         }
         catch (const boost::thread_interrupted&)
         {
            // The server is shutting down. Passed on rather than eaten: swallowing
            // it consumes the interruption request, and the work queue is then
            // waiting on a thread that no longer knows it was asked to stop.
            parentHolder_.reset();
            throw;
         }
         catch (const std::exception &error)
         {
            parentHolder_.reset();

            LOG_APPLICATION(Formatter::Format("Asynchronous task {0} did not finish: {1}",
                                              GetName(), error.what()));
            return;
         }
         catch (...)
         {
            parentHolder_.reset();

            LOG_APPLICATION(Formatter::Format("Asynchronous task {0} did not finish: unrecognized exception.",
                                              GetName()));
            return;
         }

         // Reset the shared_ptr to the parent object.
         parentHolder_.reset();
      }

   private:

      std::function<void()> asynchronousFunction_;

      std::shared_ptr<T> parentHolder_;
   };
}