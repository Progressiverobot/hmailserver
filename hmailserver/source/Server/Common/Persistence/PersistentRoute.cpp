// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "PersistentRoute.h"

#include "../BO/Route.h"
#include "../BO/RouteAddresses.h"
#include "../Util/Crypt.h"
#include "PersistentRouteAddress.h"
#include "PreSaveLimitationsCheck.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentRoute::PersistentRoute()
   {

   }

   PersistentRoute::~PersistentRoute()
   {

   }

   bool
   PersistentRoute::DeleteObject(std::shared_ptr<Route> pRoute)
   {
      if (pRoute->GetID() == 0)
         return false;

      // First delete the route addresses
      PersistentRouteAddress::DeleteByRoute(pRoute->GetID());

      // Delete the route object itself.
      SQLCommand command("delete from hm_routes where routeid = @ROUTEID");
      command.AddParameter("@ROUTEID", pRoute->GetID());
      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool 
   PersistentRoute::SaveObject(std::shared_ptr<Route> pRoute)
   {
      String errorMessage;
      return SaveObject(pRoute, errorMessage, PersistenceModeNormal);
   }

   bool 
   PersistentRoute::SaveObject(std::shared_ptr<Route> pRoute, String &sErrorMessage, PersistenceMode mode)
   {
      if (!PreSaveLimitationsCheck::CheckLimitations(mode, pRoute, sErrorMessage))
         return false;

      SQLStatement oStatement;
      
      oStatement.SetTable("hm_routes");

      oStatement.AddColumn("routedomainname", pRoute->DomainName());
      oStatement.AddColumn("routedescription", pRoute->GetDescription());
      oStatement.AddColumn("routetargetsmthost", pRoute->TargetSMTPHost());
      oStatement.AddColumn("routetargetsmtport", pRoute->TargetSMTPPort());
      oStatement.AddColumn("routenooftries", pRoute->NumberOfTries());
      oStatement.AddColumn("routeminutesbetweentry", pRoute->MinutesBetweenTry());
      oStatement.AddColumn("routealladdresses", pRoute->ToAllAddresses() ? 1 : 0);

      oStatement.AddColumn("routeuseauthentication", pRoute->GetRelayerRequiresAuth() ? 1 : 0);
      oStatement.AddColumn("routeauthenticationusername", pRoute->GetRelayerAuthUsername());
      oStatement.AddColumn("routeauthenticationpassword", Crypt::Instance()->ProtectSecret(pRoute->GetRelayerAuthPassword()));
      oStatement.AddColumn("routetreatsecurityaslocal", pRoute->GetTreatRecipientAsLocalDomain() ? 1 : 0);
      oStatement.AddColumn("routetreatsenderaslocaldomain", pRoute->GetTreatSenderAsLocalDomain() ? 1 : 0);
      oStatement.AddColumn("routeconnectionsecurity", pRoute->GetConnectionSecurity() );

      if (pRoute->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("routeid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);

         String sWhere;
         sWhere.Format(_T("routeid = %I64d"), pRoute->GetID());
         oStatement.SetWhereClause(sWhere);
      }

      bool bNewObject = pRoute->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pRoute->SetID((int) iDBID);

      // bRetVal, not true. This computed whether the statement had succeeded and then
      // reported success regardless, so every caller that checked was checking a
      // constant - including the ones hardened earlier this week, whose checks were
      // therefore correct code that could never fire. Found by the database fault
      // injector on its first run, which is the entire argument for having built it.
      return bRetVal;
   }

   bool
   PersistentRoute::ReadObject(std::shared_ptr<Route> pRoute, long lID)
   {
      SQLCommand command("select * from hm_routes where routeid = @ROUTEID");
      command.AddParameter("@ROUTEID", lID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      bool bRetVal = false;
      if (!pRS->IsEOF())
      {
         bRetVal = ReadObject(pRoute, pRS);
      }

      return true;
   }

   bool 
   PersistentRoute::ReadObject(std::shared_ptr<Route> pRoute, std::shared_ptr<DALRecordset> pRS)
   {
      pRoute->SetID(pRS->GetLongValue("routeid"));
      pRoute->DomainName(pRS->GetStringValue("routedomainname"));
      pRoute->SetDescription(pRS->GetStringValue("routedescription"));
      pRoute->TargetSMTPHost(pRS->GetStringValue("routetargetsmthost"));
      pRoute->TargetSMTPPort(pRS->GetLongValue("routetargetsmtport"));
      pRoute->NumberOfTries(pRS->GetLongValue("routenooftries"));
      pRoute->MinutesBetweenTry(pRS->GetLongValue("routeminutesbetweentry"));
      pRoute->ToAllAddresses(pRS->GetLongValue("routealladdresses") ? true : false);

      pRoute->SetTreatRecipientAsLocalDomain(pRS->GetLongValue("routetreatsecurityaslocal") ? true : false);
      pRoute->SetTreatSenderAsLocalDomain(pRS->GetLongValue("routetreatsenderaslocaldomain") ? true : false);
      pRoute->SetRelayerRequiresAuth(pRS->GetLongValue("routeuseauthentication") ? true : false);

      pRoute->SetRelayerAuthUsername(pRS->GetStringValue("routeauthenticationusername"));
      pRoute->SetRelayerAuthPassword(Crypt::Instance()->UnprotectSecret(pRS->GetStringValue("routeauthenticationpassword")));
      pRoute->SetConnectionSecurity((ConnectionSecurity) pRS->GetLongValue("routeconnectionsecurity"));

      return true;
   }
}
