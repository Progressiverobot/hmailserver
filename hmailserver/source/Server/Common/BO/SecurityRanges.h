// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Collection.h"

#include "../Persistence/PersistentSecurityRange.h"
#include "../BO/SecurityRange.h"

namespace HM
{
   class SecurityRanges : public Collection<SecurityRange, PersistentSecurityRange> 
   {
   public:
	   SecurityRanges();
	   virtual ~SecurityRanges();

      void Refresh();

      void SetDefault();

   protected:
      virtual String GetCollectionName() const {return "SecurityRanges"; } 
   private:

   };

}