// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

// Mutex.h: interface for the Mutex class.
//
//////////////////////////////////////////////////////////////////////

#pragma once


namespace HM
{
   class Mutex  
   {
   public:
	   Mutex();
	   virtual ~Mutex();

      void Wait();
      void Release();

   private:
      HANDLE mutex_;
   };

}
