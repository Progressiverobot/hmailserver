// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "Collection.h"
#include "AppPassword.h"
#include "../Persistence/PersistentAppPassword.h"

namespace HM
{
   class AppPasswords : public Collection<AppPassword, PersistentAppPassword>
   {
   public:
      AppPasswords(void);
      ~AppPasswords(void);

      void Refresh(__int64 accountID);

      bool PreSaveObject(std::shared_ptr<AppPassword> password, XNode *node);

   protected:
      virtual String GetCollectionName() const { return "AppPasswords"; }

   private:

      __int64 account_id_;
   };
}
