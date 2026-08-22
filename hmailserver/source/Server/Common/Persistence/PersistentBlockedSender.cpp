// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "PersistentBlockedSender.h"
#include "..\BO\BlockedSender.h"
#include "..\AntiSpam\BlockedSenderCache.h"
#include "..\SQL\SQLStatement.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentBlockedSender::PersistentBlockedSender(void)
   {
   }

   PersistentBlockedSender::~PersistentBlockedSender(void)
   {
   }

   bool
   PersistentBlockedSender::DeleteObject(std::shared_ptr<BlockedSender> pObject)
   {
      SQLCommand command("delete from hm_blocked_senders where bsid = @BSID");
      command.AddParameter("@BSID", pObject->GetID());

      bool bRetVal = Application::Instance()->GetDBManager()->Execute(command);

      // Invalidated on delete as well as on save. The whitelist's persister used
      // to invalidate only on save, which went unnoticed because its cache was
      // reloading on every message anyway - both halves of that were fixed in the
      // same change that added this file. A cache that actually caches must hear
      // about deletes, or a removed entry keeps blocking mail until a restart.
      BlockedSenderCache::SetNeedRefresh();

      return bRetVal;
   }

   bool
   PersistentBlockedSender::ReadObject(std::shared_ptr<BlockedSender> pObject, std::shared_ptr<DALRecordset> pRS)
   {
      pObject->SetID(pRS->GetLongValue("bsid"));
      pObject->SetAddress(pRS->GetStringValue("bsaddress"));
      pObject->SetScore(pRS->GetLongValue("bsscore"));
      pObject->SetDescription(pRS->GetStringValue("bsdescription"));

      return true;
   }

   bool
   PersistentBlockedSender::SaveObject(std::shared_ptr<BlockedSender> pObject, String &errorMessage, PersistenceMode mode)
   {
      return SaveObject(pObject);
   }

   bool
   PersistentBlockedSender::SaveObject(std::shared_ptr<BlockedSender> pObject)
   {
      SQLStatement oStatement;
      oStatement.SetTable("hm_blocked_senders");

      if (pObject->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("bsid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);
         String sWhere;
         sWhere.Format(_T("bsid = %I64d"), pObject->GetID());
         oStatement.SetWhereClause(sWhere);
      }

      oStatement.AddColumn("bsaddress", pObject->GetAddress());
      oStatement.AddColumn("bsscore", pObject->GetScore());
      oStatement.AddColumn("bsdescription", pObject->GetDescription());

      bool bNewObject = pObject->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pObject->SetID((int) iDBID);

      BlockedSenderCache::SetNeedRefresh();

      return bRetVal;
   }
}
