// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Collection.h"

#include "../Persistence/PersistentIncomingRelay.h"
#include "../BO/IncomingRelay.h"

namespace HM
{
   class IncomingRelays : public Collection<IncomingRelay, PersistentIncomingRelay> 
   {
   public:
	   IncomingRelays();
	   virtual ~IncomingRelays();

      bool Refresh();

      bool IsIncomingRelay(const IPAddress &address) const;

   protected:
      virtual String GetCollectionName() const {return "IncomingRelays"; } 
   private:

   };

}