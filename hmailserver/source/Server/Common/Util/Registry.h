// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class Registry
   {
   public:
      Registry(void);
      ~Registry(void);

      bool GetStringValue(HKEY hive, String key, String valueName, String &value);

   private:

   };
}