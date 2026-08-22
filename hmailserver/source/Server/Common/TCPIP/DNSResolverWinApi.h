// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "DNSRecord.h"

namespace HM
{


   class DNSResolverWinApi
   {
   public:
      DNSResolverWinApi();
      virtual ~DNSResolverWinApi();

      bool Query(const String &query, int resourceType, std::vector<DNSRecord> &foundRecords);
      bool Query(const String &query, int resourceType, std::vector<DNSRecord> &foundRecords, int &outStatus);

   private:

      bool RunQuery_(const String &query, int resourceType, unsigned long fOptions, std::vector<DNSRecord> &foundRecords, int &dnsStatus);

      bool IsDNSError_(int iErrorMessage);
   };


}
