// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

namespace HM
{
   class BlockedSender;
   enum PersistenceMode;

   class PersistentBlockedSender
   {
   public:
      PersistentBlockedSender(void);
      ~PersistentBlockedSender(void);

      static bool DeleteObject(std::shared_ptr<BlockedSender> pObject);
      static bool SaveObject(std::shared_ptr<BlockedSender> pObject, String &errorMessage, PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<BlockedSender> pObject);
      static bool ReadObject(std::shared_ptr<BlockedSender> pObject, std::shared_ptr<DALRecordset> pRS);
   };
}
