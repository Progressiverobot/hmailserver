// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <crtdbg.h>

namespace HM
{
   class HeapChecker
   {
   public:
      HeapChecker(void);
      ~HeapChecker(void);

      static void CheckHeapOnAllocation();

      void Reset();
      void Report();

   private:
#ifdef _DEBUG
       _CrtMemState start_;
#endif
   };
}