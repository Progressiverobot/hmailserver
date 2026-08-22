// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include ".\PersistentDNSBlackList.h"
#include "..\BO\DNSBlackList.h"
#include "..\SQL\SQLStatement.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentDNSBlackList::PersistentDNSBlackList(void)
   {
   }

   PersistentDNSBlackList::~PersistentDNSBlackList(void)
   {
   }

   bool
   PersistentDNSBlackList::DeleteObject(std::shared_ptr<DNSBlackList> pObject)
   {
      SQLCommand command("delete from hm_dnsbl where sblid = @SLBID");
      command.AddParameter("@SLBID", pObject->GetID());

      return Application::Instance()->GetDBManager()->Execute(command);
   }


   bool 
   PersistentDNSBlackList::ReadObject(std::shared_ptr<DNSBlackList> pObject, std::shared_ptr<DALRecordset> pRS)
   {
      pObject->SetID (pRS->GetLongValue("sblid"));
      pObject->SetIsActive(pRS->GetLongValue("sblactive") == 1);
      pObject->SetDNSHost(pRS->GetStringValue("sbldnshost"));
      pObject->SetRejectMessage(pRS->GetStringValue("sblrejectmessage"));
      pObject->SetExpectedResult(pRS->GetStringValue("sblresult"));
      pObject->SetScore(pRS->GetLongValue("sblscore"));

      return true;
   }

   bool 
   PersistentDNSBlackList::SaveObject(std::shared_ptr<DNSBlackList> pObject, String &errorMessage, PersistenceMode mode)
   {
      return SaveObject(pObject);
   }

   bool 
   PersistentDNSBlackList::SaveObject(std::shared_ptr<DNSBlackList> pObject)
   {
      SQLStatement oStatement;
      oStatement.SetTable("hm_dnsbl");
      
      if (pObject->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("sblid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);
         String sWhere;
         sWhere.Format(_T("sblid = %I64d"), pObject->GetID());
         oStatement.SetWhereClause(sWhere);
         
      }

      oStatement.AddColumn("sblactive", pObject->GetIsActive() == 1);
      oStatement.AddColumn("sbldnshost", pObject->GetDNSHost());
      oStatement.AddColumn("sblresult", pObject->GetExpectedResult());
      oStatement.AddColumn("sblrejectmessage", pObject->GetRejectMessage());
      oStatement.AddColumn("sblscore", pObject->GetScore());

      bool bNewObject = pObject->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pObject->SetID((int) iDBID);

      // bRetVal, not true. This computed whether the statement had succeeded and then
      // reported success regardless, so every caller that checked was checking a
      // constant - including the ones hardened earlier this week, whose checks were
      // therefore correct code that could never fire. Found by the database fault
      // injector on its first run, which is the entire argument for having built it.
      return bRetVal;
   }
}