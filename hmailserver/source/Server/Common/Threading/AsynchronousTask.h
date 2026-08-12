// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
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
      {
         try
         {
            asynchronousFunction_();
         }
         catch (...)
         {
            // to be sure we release our pointer to the parent TCP connection below.
         }

         // Reset the shared_ptr to the parent object.
         parentHolder_.reset();
      }

   private:

      std::function<void()> asynchronousFunction_;

      std::shared_ptr<T> parentHolder_;
   };
}