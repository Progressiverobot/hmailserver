// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#ifndef HM_PLATFORM_POSIX

// Windows only, in its entirety. Every member of this class is a call into the
// Service Control Manager - OpenSCManager, CreateService, ChangeServiceConfig,
// ControlService - and one of them hands back the SCM's own SERVICE_STATUS. There
// is nothing here to translate: on a POSIX machine the job this class does belongs
// to the init system, and a program does not register itself with systemd - a unit
// file does, written by whoever installs the package. That is the roadmap section
// "Linux and AArch64", and it is packaging work rather than a compilation fix.
//
// Guarded away rather than given stubs for the same reason ExceptionLogger is: a
// StartServiceOnLocalComputer that answered anything at all would be answering
// about a service that does not exist. Every caller is already Windows-only -
// Server/COM, which is the ATL administration API, and hMailServer.exe - and
// BackupExecuter.cpp includes this header under the same guard without using
// anything from it. A POSIX build that acquired a caller would fail to link and
// say so, which is the honest failure.

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

#endif
