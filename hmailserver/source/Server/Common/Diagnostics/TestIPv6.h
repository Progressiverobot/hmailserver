// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "DiagnosticResult.h"

namespace HM
{

   class TestIPv6
   {
   public:
	   TestIPv6();
	   virtual ~TestIPv6();

      DiagnosticResult PerformTest();

   private:
   };


}
