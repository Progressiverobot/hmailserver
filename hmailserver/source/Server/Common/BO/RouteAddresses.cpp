// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "RouteAddresses.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   RouteAddresses::RouteAddresses(__int64 iRouteID) :
      route_id_(iRouteID)
   {
      
   }

   RouteAddresses::~RouteAddresses()
   {

   }

   void 
   RouteAddresses::Refresh()
   {
      String sSQL;
      sSQL.Format(_T("select * from hm_routeaddresses where routeaddressrouteid = %I64d"), route_id_);;

      DBLoad_(sSQL);
   }

   void
   RouteAddresses::DeleteByAddress(const String &sAddress)
   {
      auto iterRoute = vecObjects.begin();

      while (iterRoute != vecObjects.end())
      {  
         std::shared_ptr<RouteAddress> pRoute = (*iterRoute);

         if (pRoute->GetAddress().CompareNoCase(sAddress) == 0)
         {
            // Unchecked, while the erase happened regardless - so an address the
            // database would not delete disappeared from the route in memory and came
            // back at the next refresh. A route address decides which recipients a
            // route accepts, so the two views disagreeing changes what the server does
            // with mail. Kept in place on failure so they do not.
            if (!PersistentRouteAddress::DeleteObject(pRoute))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6110, "RouteAddresses::DeleteByAddress",
                  "The route address '" + sAddress + "' could not be deleted and is still in effect.");

               return;
            }

            vecObjects.erase(iterRoute);
            return;
         }

         iterRoute++;
      }
   }

   bool
   RouteAddresses::PreSaveObject(std::shared_ptr<RouteAddress> routeAddress, XNode *node)
   {
      routeAddress->SetRouteID(route_id_);

      return true;
   }

}
