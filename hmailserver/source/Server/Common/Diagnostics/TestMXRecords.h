// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "DiagnosticResult.h"

namespace HM
{

   class TestMXRecords
   {
   public:
	   TestMXRecords(const String &localDomainName);
	   virtual ~TestMXRecords();

      DiagnosticResult PerformTest();

   private:

      String local_domain_name_;
   };


}
