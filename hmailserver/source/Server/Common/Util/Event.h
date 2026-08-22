// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Event
   {
   public:
      Event(void);
      Event(const Event& p);
      ~Event(void);
   
      void Wait();

      // True if the event was set, false if the wait timed out. Existing callers use
      // this as a sleep with a wake-up and ignore the answer, which is why it was void;
      // a caller bounding a wait needs to know which of the two happened.
      bool WaitFor(boost::chrono::milliseconds milliseconds);

      void Set();

   private:

      boost::mutex mutex_;
      boost::condition_variable set_condition_;
      bool is_set_;
   };
}