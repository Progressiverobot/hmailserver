// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "PersistentRule.h"
#include "PersistentRuleCriteria.h"
#include "PersistentRuleAction.h"

#include "..\BO\Rule.h"

#include "..\BO\RuleActions.h"
#include "..\BO\RuleAction.h"
#include "..\BO\RuleCriterias.h"
#include "..\BO\RuleCriteria.h"

#include "..\Application\ObjectCache.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentRule::PersistentRule(void)
   {
   }

   PersistentRule::~PersistentRule(void)
   {
   }

   bool
   PersistentRule::ReadObject(std::shared_ptr<Rule> pRule, const SQLCommand& sSQL)
   {
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(sSQL);
      if (!pRS)
         return false;

      bool bRetVal = false;
      if (!pRS->IsEOF())
      {
         bRetVal = ReadObject(pRule, pRS);
      }

      return bRetVal;
   }

   bool
   PersistentRule::ReadObject(std::shared_ptr<Rule> pRule, std::shared_ptr<DALRecordset> pRS)
   {
      if (pRS->IsEOF())
         return false;

      pRule->SetID(pRS->GetLongValue("ruleid"));
      pRule->SetAccountID(pRS->GetLongValue("ruleaccountid"));
      pRule->SetName(pRS->GetStringValue("rulename"));
      pRule->SetActive(pRS->GetLongValue("ruleactive") ? true : false);
      pRule->SetUseAND(pRS->GetLongValue("ruleuseand") ? true : false);
      pRule->SetSortOrder(pRS->GetLongValue("rulesortorder"));

      // Read actions
      pRule->GetActions();

      // Read criterias
      pRule->GetCriterias();

      return true;
   }

   bool
   PersistentRule::SaveObject(std::shared_ptr<Rule> pRule, String &errorMessage, PersistenceMode mode)
   {
      // errorMessage not supported yet.
      return SaveObject(pRule);
   }


   bool
   PersistentRule::SaveObject(std::shared_ptr<Rule> pRule)
   {
      SQLStatement oStatement;
      oStatement.SetTable("hm_rules");
      
      bool bNewObject = pRule->GetID() == 0;

      if (bNewObject)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("ruleid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);
         String sWhere;
         sWhere.Format(_T("ruleid = %I64d"), pRule->GetID());
         oStatement.SetWhereClause(sWhere);
      }

      oStatement.AddColumnInt64("ruleaccountid", pRule->GetAccountID());
      oStatement.AddColumn("rulename", pRule->GetName());
      oStatement.AddColumn("ruleactive", 0);
      oStatement.AddColumn("ruleuseand", pRule->GetUseAND());
      oStatement.AddColumn("rulesortorder", pRule->GetSortOrder());

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pRule->SetID((int) iDBID);

      // This was not checked before the criteria and actions were saved beneath it,
      // so a new rule whose own insert failed left GetID() at 0 and every criterion
      // and action below was written against ruleid 0 - rows belonging to no rule,
      // which nothing ever reads and nothing ever cleans up.
      if (!bRetVal)
         return false;

      // Save criterias.
      //
      // The whole function writes ruleactive = 0 above and sets it back at the end,
      // which is the right shape: a rule is not live while it is half written. What
      // it did not do was check any of the parts before reactivating, and the
      // consequence is specific rather than general. Criteria are combined with AND
      // or OR, and under AND every criterion is a *restriction* - so a rule that was
      // meant to read "delete if from X and the subject contains Y" and lost the
      // second criterion becomes "delete if from X", which is a strictly broader rule
      // than the administrator wrote, applied to live mail, with the tool still
      // displaying the criterion that is not there.
      //
      // A rule with no criteria at all is inert - ApplyRule_ leaves bDoActions false -
      // so the empty case was never the danger. The partial case was.
      bool allPartsSaved = true;

      __int64 iRuleID = pRule->GetID();
      std::shared_ptr<RuleCriterias> pRuleCriterias = pRule->GetCriterias();
      for (int i = 0; i < pRuleCriterias->GetCount(); i++)
      {
         std::shared_ptr<RuleCriteria> pRuleCriteria = pRuleCriterias->GetItem(i);
         pRuleCriteria->SetRuleID(iRuleID);

         if (!PersistentRuleCriteria::SaveObject(pRuleCriteria))
            allPartsSaved = false;
      }

      // Save actions
      std::shared_ptr<RuleActions> pRuleActions = pRule->GetActions();
      for (int i = 0; i < pRuleActions->GetCount(); i++)
      {
         std::shared_ptr<RuleAction> pRuleAction = pRuleActions->GetItem(i);
         pRuleAction->SetRuleID(iRuleID);

         if (!PersistentRuleAction::SaveObject(pRuleAction))
            allPartsSaved = false;
      }

      // Leaving it inactive is the safe half of the choice, and it is not a silent
      // one: the rule is still there to be looked at and saved again once the
      // database is working. A rule that does not run is a nuisance; a rule that runs
      // with half its conditions is a rule doing something nobody asked for.
      if (!allPartsSaved)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6101, "PersistentRule::SaveObject",
            Formatter::Format("Rule {0} was saved but some of its criteria or actions were not, so it has been left INACTIVE rather than run with conditions the administrator did not write. Correct the database problem and save the rule again.",
               pRule->GetName()));

         return false;
      }

      // Set the rule to active again.
      SQLCommand command("update hm_rules set ruleactive = @ACTIVE where ruleid = @RULEID");
      command.AddParameter("@ACTIVE", pRule->GetActive());
      command.AddParameter("@RULEID", iRuleID);

      bRetVal = Application::Instance()->GetDBManager()->Execute(command);

      NotifyReload_(pRule);

      return bRetVal;

   }

   void
   PersistentRule::DeleteByAccountID(__int64 iAccountID)
   {
      SQLCommand selectCommand("select * from hm_rules where ruleaccountid = @ACCOUNTID");
      selectCommand.AddParameter("@ACCOUNTID", iAccountID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(selectCommand);
      if (!pRS)
         return ;

      bool bRetVal = false;
      while (!pRS->IsEOF())
      {
         // Create and read the fetch account.
         std::shared_ptr<Rule> oRule = std::shared_ptr<Rule>(new Rule);

         if (ReadObject(oRule, pRS))
         {
            // Delete this fetch account and all the 
            // UID's connected to it.
            DeleteObject(oRule);
         }

         pRS->MoveNext();
      }

      // All the fetch accounts have been deleted.

   }

   bool
   PersistentRule::DeleteObject(std::shared_ptr<Rule> pRule)
   {
      SQLCommand command("delete from hm_rules where ruleid = @RULEID");
      command.AddParameter("@RULEID", pRule->GetID());

      Application::Instance()->GetDBManager()->Execute(command);

      PersistentRuleAction::DeleteObjects(pRule->GetID());
      PersistentRuleCriteria::DeleteObjects(pRule->GetID());

      NotifyReload_(pRule);

      return true;
   }

   void
   PersistentRule::NotifyReload_(std::shared_ptr<Rule> pRule)
   {
      if (pRule->GetAccountID() == 0)
         ObjectCache::Instance()->SetGlobalRulesNeedsReload();
      else
         ObjectCache::Instance()->SetAccountRulesNeedsReload(pRule->GetAccountID());
   }
}