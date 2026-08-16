// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class ADO64Helper 
   {
   public:
     ADO64Helper ();
     static void AddInt64Parameter(_CommandPtr &command, const String& parameterName, __int64 value);
      
   };
}
