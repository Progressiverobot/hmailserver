// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Rule;
   enum PersistenceMode;

   class PersistentRule
   {
   public:
      PersistentRule(void);
      ~PersistentRule(void);

      static bool ReadObject(std::shared_ptr<Rule> pRule, const SQLCommand& sSQL);
      static bool ReadObject(std::shared_ptr<Rule> pRule, std::shared_ptr<DALRecordset> pRS);

      static bool SaveObject(std::shared_ptr<Rule> pRule, String &errorMessage, PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<Rule> pRule);
      static bool DeleteObject(std::shared_ptr<Rule> pRule);

      static void DeleteByAccountID(__int64 ID);

   private:

      static void NotifyReload_(std::shared_ptr<Rule> pRule);
   };
}