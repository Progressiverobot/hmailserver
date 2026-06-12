// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "DiagnosticResult.h"

namespace HM
{

   class TestOutboundPort
   {
   public:
	   TestOutboundPort(const String &TestDomainName);
	   virtual ~TestOutboundPort();

      DiagnosticResult PerformTest();

   private:

      String local_test_domain_name_;

   };


}
