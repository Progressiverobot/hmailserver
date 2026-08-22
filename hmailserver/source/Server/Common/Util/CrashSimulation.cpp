// Copyright (c) 2014 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "CrashSimulation.h"

#include "../TCPIP/DisconnectedException.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   void
   CrashSimulation::Execute(int simulation_mode)
   {
      switch (simulation_mode)
      {
         case 0:
            break;
         case 1:
            throw new int;
         case 2:
            throw std::logic_error("Crash simulation test");
         case 3:
            {
               memset(0, 1, 1);
            }
         case 4:
            throw DisconnectedException();
      }
   }


}