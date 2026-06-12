// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

      void WaitFor(boost::chrono::milliseconds milliseconds);

      void Set();

   private:

      boost::mutex mutex_;
      boost::condition_variable set_condition_;
      bool is_set_;
   };
}