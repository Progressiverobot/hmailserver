// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Collection.h"

#include "../Persistence/PersistentAlias.h"
#include "../BO/Alias.h"

namespace HM
{
  
   class Aliases : public Collection<Alias, PersistentAlias>
   {
   public:
	   Aliases(__int64 iDomainID);
	   virtual ~Aliases();

      // Deletes all aliases in this collection
      void Refresh();
            
   protected:
      virtual bool PreSaveObject(std::shared_ptr<Alias> pAlias, XNode *node);
      virtual String GetCollectionName() const {return "Aliases"; }
   private:

      __int64 domain_id_;

   };
}
