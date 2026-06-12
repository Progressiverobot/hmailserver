// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class BlockedAttachment;
   enum PersistenceMode;

   class PersistentBlockedAttachment
   {
   public:
      PersistentBlockedAttachment(void);
      ~PersistentBlockedAttachment(void);
      
      static bool DeleteObject(std::shared_ptr<BlockedAttachment> pObject);
      static bool SaveObject(std::shared_ptr<BlockedAttachment> pObject);
      static bool SaveObject(std::shared_ptr<BlockedAttachment> pObject, String &errorMessage, PersistenceMode mode);
      static bool ReadObject(std::shared_ptr<BlockedAttachment> pObject, std::shared_ptr<DALRecordset> pRS);

   };
}