// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

// Mutex.cpp: implementation of the Mutex class.
//
//////////////////////////////////////////////////////////////////////

#include "stdafx.h"
#include "Mutex.h"

#ifdef HM_PLATFORM_POSIX
#include <pthread.h>
#endif

//////////////////////////////////////////////////////////////////////
// Construction/Destruction
//////////////////////////////////////////////////////////////////////
namespace HM
{
   Mutex::Mutex()
   {
#ifdef HM_PLATFORM_POSIX
      // mutex_ is a HANDLE on Windows and a void* here, so on this platform it
      // holds the pthread mutex that Wait and Release below actually use. It is
      // created here because there is nowhere else: the Windows constructor
      // leaves the member alone, which is a defect of its own and not one this
      // port is the place to fix - see the note at the foot of this file.
      pthread_mutex_t *lock = new pthread_mutex_t;
      ::pthread_mutex_init(lock, nullptr);
      mutex_ = lock;
#endif
   }

   Mutex::~Mutex()
   {
#ifdef HM_PLATFORM_POSIX
      if (mutex_)
      {
         pthread_mutex_t *lock = (pthread_mutex_t *) mutex_;
         ::pthread_mutex_destroy(lock);
         delete lock;
         mutex_ = nullptr;
      }
#endif
   }

   void
   Mutex::Wait()
   {
#ifdef HM_PLATFORM_POSIX
      // WaitForSingleObject on a mutex with INFINITE is pthread_mutex_lock: it
      // does not come back until the lock is held. The Windows branch below
      // throws its result away, so there is nothing to report here either.
      ::pthread_mutex_lock((pthread_mutex_t *) mutex_);
#else
      DWORD dwWaitResult;
      dwWaitResult = ::WaitForSingleObject(mutex_, INFINITE); 

      if (dwWaitResult == WAIT_OBJECT_0)
      {
         //return true;
         return;
      }
      else
      {
         //return false;
         return;
      }
#endif

   }

   void
   Mutex::Release()
   {
#ifdef HM_PLATFORM_POSIX
      ::pthread_mutex_unlock((pthread_mutex_t *) mutex_);
#else
      ::ReleaseMutex(mutex_);
#endif
   }
}

// A note for whoever reads this next: nothing in the server includes Mutex.h.
// The class has no callers, is not listed in hMailServer.vcxproj, and its
// Windows implementation never calls CreateMutex - so mutex_ there is an
// uninitialised handle and a Wait on it would fail rather than wait. It is
// compiled here only because the portable build globs Common/*.cpp. Deleting
// the pair is the right answer; that is a decision about the Windows product,
// not about this port, so it is written down rather than taken.