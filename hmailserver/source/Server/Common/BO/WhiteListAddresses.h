// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Collection.h"

#include "WhiteListAddress.h"
#include "../Persistence/PersistentWhiteListAddress.h"

namespace HM
{
   class WhiteListAddresses : public Collection<WhiteListAddress, PersistentWhiteListAddress>
   {
   public:
      WhiteListAddresses();
      ~WhiteListAddresses(void);

      // Refreshes this collection from the database. Returns false if the
      // database could not be read; the collection is left untouched then.
      bool Refresh();

   protected:
   
      virtual String GetCollectionName() const {return "WhiteListAddresses"; }

   private:
     
   };
}