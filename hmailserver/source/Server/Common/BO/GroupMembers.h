// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Collection.h"

#include "../Persistence/PersistentGroupMember.h"

#include "GroupMember.h"

namespace HM
{
   class GroupMembers : public Collection<GroupMember, PersistentGroupMember>
   {
   public:
      GroupMembers(__int64 iGroupID);
      ~GroupMembers(void);

      // Refreshes this collection from the database.
      void Refresh();

      __int64 GetGroupID() {return group_id_; }
      bool UserIsMember(__int64 iAccountID);

   protected:

      virtual String GetCollectionName() const {return "GroupMembers"; }

      void PostStoreObject(std::shared_ptr<GroupMember> pGroupMember, XNode *pChildNode);
      bool PreSaveObject(std::shared_ptr<GroupMember> pGroupMember, XNode *pNode);

   private:
 
      __int64 group_id_;
   };
}