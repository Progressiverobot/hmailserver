// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

// ADO and SQL Server Compact are COM. This class is declared only on Windows,
// because every part of it is written in terms of the ADO smart pointers the
// Windows precompiled header #imports and those have no POSIX shape; the
// roadmap section "Linux and AArch64" - the row "Database backends that
// survive" - leaves both backends out of the POSIX build, where
// DALConnectionFactory refuses them by name.
#ifndef HM_PLATFORM_POSIX

namespace HM
{
   class ADO64Helper 
   {
   public:
     ADO64Helper ();
     static void AddInt64Parameter(_CommandPtr &command, const String& parameterName, __int64 value);
      
   };
}

#endif
