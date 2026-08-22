// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <Winsvc.h>

namespace HM
{
   class ServiceManager  
   {
   public:
	   ServiceManager();
	   virtual ~ServiceManager();

      bool RegisterService(const String &ServiceName, const String &ServiceCaption);
      bool UnregisterService(const String &ServiceName);
      void MakeDependentOn(const String &ServiceName);

      bool StartServiceOnLocalComputer(const String &ServiceName);
      bool StopServiceOnLocalComputer(const String &ServiceName);

      SERVICE_STATUS GetServiceStatus(const String &ServiceName);

      bool UserControlService(const String &ServiceName, DWORD OpCode);
      bool DoesServiceExist(const String &ServiceName);

   private:

      bool ReconfigureService_(SC_HANDLE hSCMManager, const String &ServiceName);
   };
}
