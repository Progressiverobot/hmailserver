// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "DiagnosticResult.h"

namespace HM
{
   
   class Diagnostic
   {
   public:
	   Diagnostic();
	   virtual ~Diagnostic();
  
      void SetLocalDomain(String &sDomainName);
      void SetTestDomain(String &sTestDomainName);
      String GetLocalDomain() const;
      String GetTestDomain() const;

      std::vector<DiagnosticResult> PerformTests();

   private:

      String local_domain_name_;
      String local_test_domain_name_;
   };


}
