// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Collection.h"

#include "../Persistence/PersistentDomain.h"
#include "Domain.h"

namespace HM
{
   class Domains : public Collection<Domain, PersistentDomain>
   {
   public:
	   Domains();
	   virtual ~Domains();

      void Refresh();
      void Refresh(__int64 iDomainID);
      String GetNames();

   protected:
      virtual String GetCollectionName() const {return "Domains"; }
   private:

   };
}
