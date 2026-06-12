// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class EventTester
   {
   public:
      void Test();
   private:

      void TestSetBeforeWait();
      void TestWaitTimeoutNotSet();
      void TestWaitTimeoutSet();
   };
}