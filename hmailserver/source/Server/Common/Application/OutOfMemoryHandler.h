// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#ifdef HM_PLATFORM_POSIX
// std::new_handler, which is what the POSIX build stores the previous handler
// in. See the member declaration below for why the type differs by platform.
#include <new>
#endif

namespace HM
{
   class OutOfMemoryHandler
   {
   public:
      OutOfMemoryHandler(void);
      ~OutOfMemoryHandler(void);

      static void Initialize();
      static void Terminate();

   private:

      // MSVC's _PNH is the type _set_new_handler takes and returns: an
      // int(*)(size_t) that is handed the size of the failed allocation and
      // returns non-zero to have the allocation retried. The C++ standard's own
      // hook is std::new_handler - a void(*)() that retries by returning and
      // gives up by throwing - so that is the type the previous handler has to
      // be kept in here. The two are the same decision written differently, and
      // the adapter in the .cpp is where the translation is made.
#ifdef HM_PLATFORM_POSIX
      static std::new_handler pOriginalNewHandler;
#else
      static _PNH pOriginalNewHandler;
#endif
   };
}