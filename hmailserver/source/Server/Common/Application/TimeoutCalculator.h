// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class TimeoutCalculator  
   {
   public:
      TimeoutCalculator();

      enum Constants
      {
         MaxConnectionCountOptimized = 20000
      };

      int Calculate(int minSecs, int maxSecs);
      int Calculate(int connectionCount, int minSecs, int maxSecs);

   private:
   };

   class TimeoutCalculatorTester
   {
   public:
      void Test();


   };
}
