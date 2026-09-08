// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "PersistenceMode.h"

namespace HM
{
   class DNSBlackList;

   class PersistentDNSBlackList
   {
   public:
      PersistentDNSBlackList(void);
      ~PersistentDNSBlackList(void);
      
      static bool DeleteObject(std::shared_ptr<DNSBlackList> pObject);
      static bool SaveObject(std::shared_ptr<DNSBlackList> pObject, String &errorMessage, PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<DNSBlackList> pObject);
      static bool ReadObject(std::shared_ptr<DNSBlackList> pObject, std::shared_ptr<DALRecordset> pRS);

   };
}