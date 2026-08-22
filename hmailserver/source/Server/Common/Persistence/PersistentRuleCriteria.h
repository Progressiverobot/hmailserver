// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class RuleCriteria;
   enum PersistenceMode;

   class PersistentRuleCriteria
   {
   public:
      PersistentRuleCriteria(void);
      ~PersistentRuleCriteria(void);

      static bool ReadObject(std::shared_ptr<RuleCriteria> pRuleCriteria, const SQLCommand &sSQL);
      static bool ReadObject(std::shared_ptr<RuleCriteria> pRuleCriteria, std::shared_ptr<DALRecordset> pRS);

      
      static bool SaveObject(std::shared_ptr<RuleCriteria> pRuleCriteria, String &errorMessage, PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<RuleCriteria> pRuleCriteria);
      static bool DeleteObject(std::shared_ptr<RuleCriteria> pRuleCriteria);

      static bool DeleteObjects(__int64 iRuleID);
   };
}