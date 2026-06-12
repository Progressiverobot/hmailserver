// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Collection.h"
#include "BlockedAttachment.h"

#include "../Persistence/PersistentBlockedAttachment.h"

namespace HM
{
   class BlockedAttachments : public Collection<BlockedAttachment, PersistentBlockedAttachment>
   {
   public:
      BlockedAttachments();
      ~BlockedAttachments(void);

      // Refreshes this collection from the database.
      void Refresh();

   protected:
      virtual String GetCollectionName() const {return "BlockedAttachments"; }

   private:
     
   };
}