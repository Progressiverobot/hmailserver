// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "DiagnosticResult.h"

namespace HM
{

   class TestInformationGatherer
   {
   public:
	   TestInformationGatherer();
	   virtual ~TestInformationGatherer();

      DiagnosticResult PerformTest();

   };


}
