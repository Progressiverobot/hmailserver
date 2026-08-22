// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../TCPIP/IPAddress.h"

namespace HM
{
   class SecurityRange;
   enum  PersistenceMode;

   class PersistentSecurityRange
   {
   public:
	   PersistentSecurityRange();
	   virtual ~PersistentSecurityRange();

      static bool DeleteObject(std::shared_ptr<SecurityRange> pSR);
      static bool SaveObject(std::shared_ptr<SecurityRange> pSR);
      static bool SaveObject(std::shared_ptr<SecurityRange> pSR, String &result,  PersistenceMode mode);

      static bool ReadObject(std::shared_ptr<SecurityRange> pSR, std::shared_ptr<DALRecordset> pRS);
      static bool ReadObject(std::shared_ptr<SecurityRange> pSR, const SQLCommand &command);
      static bool ReadObject(std::shared_ptr<SecurityRange> pSR, __int64 ObjectID);
      static bool ReadObject(std::shared_ptr<SecurityRange> pSR, const String &name);

      static std::shared_ptr<SecurityRange> ReadMatchingIP(const IPAddress &ipaddress);
      
      static bool DeleteExpired();

      static bool Exists(const String &name);

   private:

      static bool Validate(std::shared_ptr<SecurityRange> pSR, String &result);
   };

}
