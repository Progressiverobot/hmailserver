// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "OutOfMemoryHandler.h"
#include "../../IMAP/IMAPFolderContainer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
#ifdef HM_PLATFORM_POSIX
   std::new_handler OutOfMemoryHandler::pOriginalNewHandler = nullptr;
#else
   _PNH OutOfMemoryHandler::pOriginalNewHandler = 0;
#endif
   boost::recursive_mutex _outOfMemoryHandlerMutex;
   
   BYTE * pMemoryChunk;

   OutOfMemoryHandler::OutOfMemoryHandler(void)
   {
   }

   OutOfMemoryHandler::~OutOfMemoryHandler(void)
   {
   }

   int OnOutOfMemory( size_t )
   {
     // Hopefully the scope is smaller then the buffer we've attempted to allocate
     boost::lock_guard<boost::recursive_mutex> guard(_outOfMemoryHandlerMutex);

     // Start of by deleting the chunk of memory
     // to ensure that we got something to work with.
     delete [] pMemoryChunk;
     pMemoryChunk = 0;

     LOG_APPLICATION("OutOfMemoryHandler - hMailServer has run out of memory, clearing caches.");

     // And now try to free up some memory.
     bool bCleared = false;

     bCleared = IMAPFolderContainer::Instance()->Clear() ? true : bCleared;

     // If memory was cleared, allocate up the memory chunk again,
     // if we get here a second time.
     pMemoryChunk = new BYTE[1024 * 1024];

     // Return 1 if something was removed from cache.
     return bCleared ? 1 : 0;

   }

#ifdef HM_PLATFORM_POSIX
   // The standard new handler takes no arguments and returns nothing: it is
   // called after the allocation has already failed, and the runtime retries if
   // it returns and gives up if it throws. MSVC's handler is told the size and
   // says by its return value whether to retry. So the decision is still made
   // once, in OnOutOfMemory above, and this adapter only turns "nothing was
   // freed" into the std::bad_alloc that a standard handler must throw rather
   // than return - returning without freeing anything would spin operator new
   // in a loop forever.
   void OnOutOfMemoryStandard()
   {
      if (OnOutOfMemory(0) == 0)
         throw std::bad_alloc();
   }
#endif

   void 
   OutOfMemoryHandler::Initialize()
   {     
     pMemoryChunk = new BYTE[5 * 1024 * 1024];

#ifdef HM_PLATFORM_POSIX
      pOriginalNewHandler = std::set_new_handler( OnOutOfMemoryStandard );
      // _set_new_mode(1) on Windows sends malloc's failures through the new
      // handler as well. The C library here has no such switch - malloc returns
      // null and never consults the new handler - so the POSIX build hooks
      // operator new alone, which is where every allocation this handler was
      // written to rescue comes from.
#else
      pOriginalNewHandler = _set_new_handler( OnOutOfMemory );
      _set_new_mode(1);
#endif
   }

   void 
   OutOfMemoryHandler::Terminate()
   {
#ifdef HM_PLATFORM_POSIX
      std::set_new_handler(pOriginalNewHandler);
#else
      _set_new_handler(pOriginalNewHandler);
#endif

      delete [] pMemoryChunk;
   }
}