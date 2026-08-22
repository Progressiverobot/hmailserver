// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class ACLPermission;
   enum PersistenceMode;
   class PersistentACLPermission
   {
   public:
      PersistentACLPermission(void);
      ~PersistentACLPermission(void);
      
      static bool Validate(std::shared_ptr<ACLPermission> pObject);
      static bool DeleteOwnedByAccount(__int64 iAccountID);
      static bool DeleteOwnedByGroup(__int64 groupID);
      static bool DeleteObject(std::shared_ptr<ACLPermission> pObject);
      static bool SaveObject(std::shared_ptr<ACLPermission> pObject);
      static bool SaveObject(std::shared_ptr<ACLPermission> pObject, String &errorMessage, PersistenceMode mode);
      static bool ReadObject(std::shared_ptr<ACLPermission> pObject, std::shared_ptr<DALRecordset> pRS);

   };
}