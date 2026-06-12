// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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
