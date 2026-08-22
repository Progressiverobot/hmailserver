// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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
