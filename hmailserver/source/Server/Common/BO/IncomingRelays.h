// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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