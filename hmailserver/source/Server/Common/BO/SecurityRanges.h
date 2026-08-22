// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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

      // False if the defaults could not be written. The existing ranges are deleted
      // first, so a false return means the server may now be left with fewer ranges
      // than it started with - the caller is expected to say so rather than report
      // that the defaults were restored.
      bool SetDefault();

   protected:
      virtual String GetCollectionName() const {return "SecurityRanges"; } 
   private:

   };

}