// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "Rules.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   Rules::Rules(__int64 iAccountID) :
      account_id_(iAccountID)
   {
      
   }

   Rules::~Rules(void)
   {
   }

   void 
   Rules::Refresh()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Refreshes this collection from the database.
   //---------------------------------------------------------------------------()
   {
      String sSQL;
      sSQL.Format(_T("select * from hm_rules where ruleaccountid = %I64d order by rulesortorder asc"), account_id_);
      DBLoad_(sSQL);
   }

   std::vector<std::shared_ptr<Rule> >::iterator 
   Rules::GetRuleIterator_(__int64 iRuleID)
   {
      auto iter = vecObjects.begin();
      auto iterEnd = vecObjects.end();

      int iCurrentSortOrder = -1;
      std::shared_ptr<Rule> pRule;
      for (; iter != iterEnd; iter++)
      {
         pRule = (*iter);

         if (pRule->GetID() == iRuleID)
         {
            return iter;
         }
      }

      return iterEnd;
   }

   void
   Rules::MoveUp(__int64 iRuleID)
   {
      auto iter = GetRuleIterator_(iRuleID);
      auto iterEnd = vecObjects.end();

      if (iter == iterEnd || iter == vecObjects.begin())
         return;

      auto iterPrevious = iter - 1;
         
      // Move the rule to the previous position in the vector.
      std::shared_ptr<Rule> pRule = (*iter);
      
      // Delete it from the current position
      vecObjects.erase(iter);

      // Insert the rule one step back
      vecObjects.insert(iterPrevious, pRule);

      // Check that sort order is correct.
      UpdateSortOrder_();

      Refresh();
   }

   void
   Rules::MoveDown(__int64 iRuleID)
   {
      auto iter = GetRuleIterator_(iRuleID);
      auto iterEnd = vecObjects.end();
     
      auto iterNext = iter +1;

      if (iter == iterEnd || iterNext == vecObjects.end())
         return;

      // Move the rule to the next position in the vector.
      std::shared_ptr<Rule> pNextRule = (*iterNext);

      // Delete the next rule from the current position.
      vecObjects.erase(iterNext);

      // Insert the next rule before this one.
      vecObjects.insert(iter, pNextRule);

      // Check that sort order is correct.
      UpdateSortOrder_();

      Refresh();
   }

   void 
   Rules::UpdateSortOrder_()
   {
      auto iter = vecObjects.begin();
      auto iterEnd = vecObjects.end();

      int iSortOrder = 1;

      for (; iter != iterEnd; iter++, iSortOrder ++)
      {
         std::shared_ptr<Rule> pRule = (*iter);

         if (pRule->GetSortOrder() != iSortOrder)
         {
            // We need to update the sort order of this rule.
            //
            // Reported rather than propagated: this function returns void, it is
            // renumbering a whole list, and one row failing is not a reason to abandon
            // the rest. But rules run in sort order and that order decides which of
            // two conflicting rules wins, so a silent failure here changes what the
            // server does with mail while the administration tool shows the order the
            // administrator asked for.
            pRule->SetSortOrder(iSortOrder);

            if (!PersistentRule::SaveObject(pRule))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6096, "Rules::UpdateSortOrder_",
                  Formatter::Format("The sort order of rule {0} could not be saved, so the rules will run in a different order than the one shown.", pRule->GetName()));
            }
         }
         
      }

   }

   bool
   Rules::PreSaveObject(std::shared_ptr<Rule> pRule, XNode *node)
   {
      pRule->SetAccountID(account_id_);
      return true;
   }
}