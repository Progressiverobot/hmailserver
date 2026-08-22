// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "Event.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   Event::Event(void) :
      is_set_(false)
   {

   }

   Event::~Event(void)
   {
      
   }

   void 
   Event::Wait()
   {
      boost::mutex::scoped_lock lock(mutex_);

      if (!is_set_)
         set_condition_.wait(lock);

      is_set_ = false;
   }

   bool
   Event::WaitFor(boost::chrono::milliseconds milliseconds)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Waits until the event is set or the time runs out. Answers true if it was
   // set, false if it timed out.
   //
   // The answer used to be thrown away - the old comment here said "result will be
   // false if there's a timeout" beside a call whose result nothing read - so a
   // caller could not bound a wait and then find out whether the thing it was
   // waiting for had actually happened. That is the whole value of a bounded wait.
   //
   // The predicate form also fixes a second, quieter bug: a bare wait_for returns
   // on a spurious wakeup as well as on a signal, and the old code then set
   // is_set_ = false and carried on as though the event had fired.
   //---------------------------------------------------------------------------
   {
      boost::mutex::scoped_lock lock(mutex_);

      const bool signalled = set_condition_.wait_for(lock, milliseconds, [this] { return is_set_; });

      is_set_ = false;

      return signalled;
   }

   void 
   Event::Set()
   {  
      boost::mutex::scoped_lock lock(mutex_);
      is_set_ = true;
      set_condition_.notify_one();
   }


}