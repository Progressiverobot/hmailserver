// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Collection.h"

#include "../Persistence/PersistentRule.h"
#include "Rule.h"

namespace HM
{
   class Rules : public Collection<Rule, PersistentRule>
   {
   public:
      Rules(__int64 iAccountID);
      ~Rules(void);

      void Refresh();

      __int64 GetAccountID() const {return account_id_; }
 
      void MoveUp(__int64 iRuleID);
      void MoveDown(__int64 iRuleID);

   protected:
      virtual String GetCollectionName() const {return "Rules"; }
      virtual bool PreSaveObject(std::shared_ptr<Rule> pRule, XNode *node);
   private:
      
      std::vector<std::shared_ptr<Rule> >::iterator GetRuleIterator_(__int64 iRuleID);
      void UpdateSortOrder_();

      __int64 account_id_;
   };
}