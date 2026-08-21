// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "PersistentWhiteListAddress.h"
#include "..\BO\WhiteListAddress.h"
#include "..\AntiSpam\WhiteListCache.h"
#include "..\SQL\SQLStatement.h"
#include "../SQL/IPAddressSQLHelper.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentWhiteListAddress::PersistentWhiteListAddress(void)
   {
   }

   PersistentWhiteListAddress::~PersistentWhiteListAddress(void)
   {
   }

   bool
   PersistentWhiteListAddress::DeleteObject(std::shared_ptr<WhiteListAddress> pObject)
   {
      SQLCommand command("delete from hm_whitelist where whiteid = @WHITEID");
      command.AddParameter("@WHITEID", pObject->GetID());

      bool bRetVal = Application::Instance()->GetDBManager()->Execute(command);

      // A deleted entry must leave the cache too. This was missing, masked
      // by the cache reloading on every message (WhiteListCache::Refresh
      // never cleared its flag); now that the cache actually caches, a
      // delete without this line would keep whitelisting the removed
      // sender until the service restarted.
      WhiteListCache::SetNeedRefresh();

      return bRetVal;
   }

   bool 
   PersistentWhiteListAddress::ReadObject(std::shared_ptr<WhiteListAddress> pObject, std::shared_ptr<DALRecordset> pRS)
   {
      IPAddressSQLHelper helper;

      pObject->SetID (pRS->GetLongValue("whiteid"));
      pObject->SetLowerIPAddress(helper.Construct(pRS, "whiteloweripaddress1", "whiteloweripaddress2"));
      pObject->SetUpperIPAddress(helper.Construct(pRS, "whiteupperipaddress1", "whiteupperipaddress2"));
      pObject->SetEMailAddress(pRS->GetStringValue("whiteemailaddress"));
      pObject->SetDescription(pRS->GetStringValue("whitedescription"));
      
      return true;
   }

   bool 
   PersistentWhiteListAddress::SaveObject(std::shared_ptr<WhiteListAddress> pObject, String &errorMessage, PersistenceMode mode)
   {
      return SaveObject(pObject);
   }

   bool 
   PersistentWhiteListAddress::SaveObject(std::shared_ptr<WhiteListAddress> pObject)
   {
      SQLStatement oStatement;
      oStatement.SetTable("hm_whitelist");
      
      if (pObject->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("whiteid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);
         String sWhere;
         sWhere.Format(_T("whiteid = %I64d"), pObject->GetID());
         oStatement.SetWhereClause(sWhere);
      }

      IPAddressSQLHelper helper;
      helper.AppendStatement(oStatement, pObject->GetLowerIPAddress(), "whiteloweripaddress1", "whiteloweripaddress2");
      helper.AppendStatement(oStatement, pObject->GetUpperIPAddress(), "whiteupperipaddress1", "whiteupperipaddress2");

      oStatement.AddColumn("whiteemailaddress", pObject->GetEmailAddress());
      oStatement.AddColumn("whitedescription", pObject->GetDescription());

      bool bNewObject = pObject->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pObject->SetID((int) iDBID);

      WhiteListCache::SetNeedRefresh();

      // bRetVal, not true. This computed whether the statement had succeeded and then
      // reported success regardless, so every caller that checked was checking a
      // constant - including the ones hardened earlier this week, whose checks were
      // therefore correct code that could never fire. Found by the database fault
      // injector on its first run, which is the entire argument for having built it.
      return bRetVal;
   }
}