// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include ".\AppPasswords.h"

#include "../Persistence/PersistentAppPassword.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   AppPasswords::AppPasswords(void) :
      account_id_(0)
   {
   }

   AppPasswords::~AppPasswords(void)
   {
   }

   void
   AppPasswords::Refresh(__int64 accountID)
   {
      account_id_ = accountID;

      vecObjects.clear();

      SQLCommand command("select * from hm_apppasswords where apaccountid = @APACCOUNTID order by apid asc");
      command.AddParameter("@APACCOUNTID", account_id_);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return;

      while (!recordset->IsEOF())
      {
         std::shared_ptr<AppPassword> password = std::shared_ptr<AppPassword>(new AppPassword(
            recordset->GetLongValue("apid"),
            account_id_,
            recordset->GetStringValue("apname"),
            recordset->GetStringValue("aphash"),
            recordset->GetLongValue("apencryption"),
            recordset->GetStringValue("apcreated"),
            recordset->GetStringValue("aplastused"),
            recordset->GetLongValue("apactive") == 1));

         vecObjects.push_back(password);

         recordset->MoveNext();
      }
   }

   bool
   AppPasswords::PreSaveObject(std::shared_ptr<AppPassword> password, XNode *node)
   {
      password->SetAccountID(account_id_);
      return true;
   }
}
