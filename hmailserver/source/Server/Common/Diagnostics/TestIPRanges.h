// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "DiagnosticResult.h"

namespace HM
{

   class TestIPRanges
   {
   public:
      TestIPRanges();
      virtual ~TestIPRanges();

      DiagnosticResult PerformTest();

   private:
   };


}
