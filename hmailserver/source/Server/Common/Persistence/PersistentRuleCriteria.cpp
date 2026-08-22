// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include ".\persistentrulecriteria.h"
#include "..\BO\RuleCriteria.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentRuleCriteria::PersistentRuleCriteria(void)
   {
   }

   PersistentRuleCriteria::~PersistentRuleCriteria(void)
   {
   }

   bool
   PersistentRuleCriteria::ReadObject(std::shared_ptr<RuleCriteria> pRuleCriteria, const SQLCommand &command)
   {

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      bool bRetVal = false;
      if (!pRS->IsEOF())
      {
         bRetVal = ReadObject(pRuleCriteria, pRS);
      }

      return bRetVal;
   }

   bool
   PersistentRuleCriteria::ReadObject(std::shared_ptr<RuleCriteria> pRuleCriteria, std::shared_ptr<DALRecordset> pRS)
   {
      if (pRS->IsEOF())
         return false;

      pRuleCriteria->SetID(pRS->GetLongValue("criteriaid"));
      pRuleCriteria->SetRuleID(pRS->GetLongValue("criteriaruleid"));
      pRuleCriteria->SetMatchValue(pRS->GetStringValue("criteriamatchvalue"));
      pRuleCriteria->SetPredefinedField((RuleCriteria::PredefinedField) pRS->GetLongValue("criteriapredefinedfield"));
      pRuleCriteria->SetMatchType((RuleCriteria::MatchType) pRS->GetLongValue("criteriamatchtype"));
      pRuleCriteria->SetHeaderField(pRS->GetStringValue("criteriaheadername"));
      pRuleCriteria->SetUsePredefined(pRS->GetLongValue("criteriausepredefined") ? true : false);

      return true;
   }

   bool
   PersistentRuleCriteria::SaveObject(std::shared_ptr<RuleCriteria> pRuleCriteria, String &errorMessage, PersistenceMode mode)
   {
      // The column is 2000 characters (widened from 255 in schema 6012, where a
      // long regular expression was silently truncated or refused depending on
      // backend - and a truncated regex usually still compiles, matching
      // something other than what was typed). The limit is enforced HERE so the
      // administrator is told, with the number, instead of the driver deciding.
      if (pRuleCriteria->GetMatchValue().GetLength() > 2000)
      {
         errorMessage = _T("The criterion's value is longer than 2000 characters, which is the most the database stores. Shorten the value; it has not been saved.");
         return false;
      }

      return SaveObject(pRuleCriteria);
   }

   bool
   PersistentRuleCriteria::SaveObject(std::shared_ptr<RuleCriteria> pRuleCriteria)
   {
      // The same 2000-character bound as the overload above, enforced HERE as
      // well because this plain path is what a rule-save cascade calls - a
      // criterion added before its rule was first saved never passes through the
      // overload at all. This path has no message channel, so the refusal is
      // reported rather than returned; either way the value is never truncated.
      if (pRuleCriteria->GetMatchValue().GetLength() > 2000)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5903, "PersistentRuleCriteria::SaveObject",
            "A rule criterion's value is longer than 2000 characters, which is the most the database stores. It has not been saved.");
         return false;
      }

      SQLStatement oStatement;
      oStatement.SetTable("hm_rule_criterias");

      bool bNewObject = pRuleCriteria->GetID() == 0;

      if (bNewObject)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("criteriaid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);
         
         String sWhere;
         sWhere.Format(_T("criteriaid = %I64d"), pRuleCriteria->GetID());
         oStatement.SetWhereClause(sWhere);

      }

      oStatement.AddColumnInt64("criteriaruleid", pRuleCriteria->GetRuleID());
      oStatement.AddColumn("criteriausepredefined", pRuleCriteria->GetUsePredefined());
      oStatement.AddColumn("criteriapredefinedfield", pRuleCriteria->GetPredefinedField());
      oStatement.AddColumn("criteriaheadername", pRuleCriteria->GetHeaderField());
      oStatement.AddColumn("criteriamatchtype", pRuleCriteria->GetMatchType());
      oStatement.AddColumn("criteriamatchvalue", pRuleCriteria->GetMatchValue());

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pRuleCriteria->SetID((int) iDBID);

      return bRetVal;

   }

   bool
   PersistentRuleCriteria::DeleteObject(std::shared_ptr<RuleCriteria> pRuleCriteria)
   {
      SQLCommand command("delete from hm_rule_criterias where criteriaid = @CRITERIAID");
      command.AddParameter("@CRITERIAID", pRuleCriteria->GetID());

      bool bRet = Application::Instance()->GetDBManager()->Execute(command);

      return bRet; 
   }

   bool
   PersistentRuleCriteria::DeleteObjects(__int64 iRuleID)
   {
      SQLCommand command("delete from hm_rule_criterias where criteriaruleid = @RULEID");
      command.AddParameter("@RULEID", iRuleID);

      bool bRet = Application::Instance()->GetDBManager()->Execute(command);

      return bRet; 
   }
}