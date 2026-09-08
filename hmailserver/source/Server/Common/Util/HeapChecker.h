// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

// The debug heap is Microsoft's C runtime, and _CrtMemState with it. Everything
// this class does is a no-op away from a debug MSVC build, so on any other
// platform the header is simply not read and the member is not declared.
#if defined(_MSC_VER)
#include <crtdbg.h>
#endif

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
#if defined(_MSC_VER) && defined(_DEBUG)
       _CrtMemState start_;
#endif
   };
}