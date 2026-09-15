// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "PersistentRemoteDomainPolicy.h"

#include "../BO/RemoteDomainPolicy.h"
#include "PreSaveLimitationsCheck.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentRemoteDomainPolicy::PersistentRemoteDomainPolicy()
   {

   }

   PersistentRemoteDomainPolicy::~PersistentRemoteDomainPolicy()
   {

   }

   bool
   PersistentRemoteDomainPolicy::DeleteObject(std::shared_ptr<RemoteDomainPolicy> policy)
   {
      if (policy->GetID() == 0)
         return false;

      SQLCommand command("delete from hm_remotedomainpolicies where policyid = @POLICYID");
      command.AddParameter("@POLICYID", policy->GetID());
      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   PersistentRemoteDomainPolicy::SaveObject(std::shared_ptr<RemoteDomainPolicy> policy)
   {
      String errorMessage;
      return SaveObject(policy, errorMessage, PersistenceModeNormal);
   }

   bool
   PersistentRemoteDomainPolicy::SaveObject(std::shared_ptr<RemoteDomainPolicy> policy, String &errorMessage, PersistenceMode mode)
   {
      if (!PreSaveLimitationsCheck::CheckLimitations(mode, policy, errorMessage))
         return false;

      SQLStatement statement;

      statement.SetTable("hm_remotedomainpolicies");

      statement.AddColumn("policydomainname", policy->GetDomainName());
      statement.AddColumn("policydescription", policy->GetDescription());
      statement.AddColumn("policyactive", policy->GetActive() ? 1 : 0);
      statement.AddColumn("policyoutboundtls", (long) policy->GetOutboundTlsRequirement());
      statement.AddColumn("policyinboundtls", policy->GetRequireInboundTls() ? 1 : 0);
      statement.AddColumn("policymaxmessagesizekb", policy->GetMaxMessageSizeKB());
      statement.AddColumn("policymaxconnections", policy->GetMaxConnections());
      statement.AddColumn("policymaxperminute", policy->GetMaxMessagesPerMinute());
      statement.AddColumn("policyallowreplies", policy->GetAllowAutomaticReplies() ? 1 : 0);
      statement.AddColumn("policyallowforwarding", policy->GetAllowForwarding() ? 1 : 0);
      statement.AddColumn("policycalloutenabled", policy->GetCalloutEnabled() ? 1 : 0);
      statement.AddColumn("policycallouthost", policy->GetCalloutHost());
      statement.AddColumn("policycalloutport", policy->GetCalloutPort());
      statement.AddColumn("policycallouttimeout", policy->GetCalloutTimeoutSeconds());
      statement.AddColumn("policycalloutcacheminutes", policy->GetCalloutCacheMinutes());
      statement.AddColumn("policycalloutperminute", policy->GetCalloutMaxPerMinute());

      bool isNewObject = policy->GetID() == 0;

      if (isNewObject)
      {
         statement.SetStatementType(SQLStatement::STInsert);
         statement.SetIdentityColumn("policyid");
      }
      else
      {
         statement.SetStatementType(SQLStatement::STUpdate);

         String where;
         where.Format(_T("policyid = %I64d"), policy->GetID());
         statement.SetWhereClause(where);
      }

      __int64 databaseID = 0;
      bool result = Application::Instance()->GetDBManager()->Execute(statement, isNewObject ? &databaseID : 0);

      if (result && isNewObject)
         policy->SetID((int) databaseID);

      return result;
   }

   bool
   PersistentRemoteDomainPolicy::ReadObject(std::shared_ptr<RemoteDomainPolicy> policy, long id)
   {
      SQLCommand command("select * from hm_remotedomainpolicies where policyid = @POLICYID");
      // The cast names the overload Windows already selects: AddParameter is
      // overloaded on int, unsigned int and __int64, MSVC binds a long to the
      // int one because they are the same 32-bit type, and clang finds all three
      // equally distant. The overload decides the bound SQL parameter's type, so
      // the choice is not cosmetic.
      command.AddParameter("@POLICYID", (int) id);

      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!recordset)
         return false;

      if (recordset->IsEOF())
         return false;

      return ReadObject(policy, recordset);
   }

   bool
   PersistentRemoteDomainPolicy::ReadObject(std::shared_ptr<RemoteDomainPolicy> policy, std::shared_ptr<DALRecordset> recordset)
   {
      policy->SetID(recordset->GetLongValue("policyid"));
      policy->SetDomainName(recordset->GetStringValue("policydomainname"));
      policy->SetDescription(recordset->GetStringValue("policydescription"));
      policy->SetActive(recordset->GetLongValue("policyactive") ? true : false);

      // A value the column should not hold - a hand-edited row, a future version
      // written back by an older server - lands on RemoteTlsDefault rather than
      // on whatever the cast produced. The strict values are the ones that hold
      // up mail, so an unrecognised number must not be one of them.
      long outboundTls = recordset->GetLongValue("policyoutboundtls");
      if (outboundTls < RemoteTlsDefault || outboundTls > RemoteTlsDane)
         outboundTls = RemoteTlsDefault;
      policy->SetOutboundTlsRequirement((RemoteTlsRequirement) outboundTls);

      policy->SetRequireInboundTls(recordset->GetLongValue("policyinboundtls") ? true : false);
      policy->SetMaxMessageSizeKB(recordset->GetLongValue("policymaxmessagesizekb"));
      policy->SetMaxConnections(recordset->GetLongValue("policymaxconnections"));
      policy->SetMaxMessagesPerMinute(recordset->GetLongValue("policymaxperminute"));
      policy->SetAllowAutomaticReplies(recordset->GetLongValue("policyallowreplies") ? true : false);
      policy->SetAllowForwarding(recordset->GetLongValue("policyallowforwarding") ? true : false);
      policy->SetCalloutEnabled(recordset->GetLongValue("policycalloutenabled") ? true : false);
      policy->SetCalloutHost(recordset->GetStringValue("policycallouthost"));
      policy->SetCalloutPort(recordset->GetLongValue("policycalloutport"));
      policy->SetCalloutTimeoutSeconds(recordset->GetLongValue("policycallouttimeout"));
      policy->SetCalloutCacheMinutes(recordset->GetLongValue("policycalloutcacheminutes"));
      policy->SetCalloutMaxPerMinute(recordset->GetLongValue("policycalloutperminute"));

      return true;
   }
}
