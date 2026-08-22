// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class DNSRecord
   {
   public:

      DNSRecord(AnsiString value, int recordType, int preference);

      int GetPreference() { return preference_; }
      AnsiString GetValue() { return value_;  }
   private:
      
      AnsiString value_;
      int record_type_;
      int preference_;

   };


}
