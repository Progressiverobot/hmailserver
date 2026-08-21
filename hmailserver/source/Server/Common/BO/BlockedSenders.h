// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "Collection.h"

#include "BlockedSender.h"
#include "../Persistence/PersistentBlockedSender.h"

namespace HM
{
   class BlockedSenders : public Collection<BlockedSender, PersistentBlockedSender>
   {
   public:
      BlockedSenders();
      ~BlockedSenders(void);

      // Refreshes this collection from the database. Returns false if the
      // database could not be read; the collection is left untouched then.
      bool Refresh();

   protected:

      virtual String GetCollectionName() const {return "BlockedSenders"; }

   private:

   };
}
