// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "PersistenceMode.h"

namespace HM
{
   class RemoteDomainPolicy;

   class PersistentRemoteDomainPolicy
   {
   public:
      PersistentRemoteDomainPolicy();
      virtual ~PersistentRemoteDomainPolicy();

      static bool DeleteObject(std::shared_ptr<RemoteDomainPolicy> policy);

      static bool SaveObject(std::shared_ptr<RemoteDomainPolicy> policy);
      static bool SaveObject(std::shared_ptr<RemoteDomainPolicy> policy, String &errorMessage, PersistenceMode mode);
      static bool ReadObject(std::shared_ptr<RemoteDomainPolicy> policy, long id);
      static bool ReadObject(std::shared_ptr<RemoteDomainPolicy> policy, std::shared_ptr<DALRecordset> recordset);
   };
}
