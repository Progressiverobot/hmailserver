// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Route;
   enum PersistenceMode;

   class PersistentRoute
   {
   public:
      PersistentRoute();
      virtual ~PersistentRoute();

      static bool DeleteObject(std::shared_ptr<Route> pRoute);

      static bool SaveObject(std::shared_ptr<Route> pRoute);
      static bool SaveObject(std::shared_ptr<Route> pRoute, String &sErrorMessage, PersistenceMode mode);
      static bool ReadObject(std::shared_ptr<Route> pRoute, long lID);
      static bool ReadObject(std::shared_ptr<Route> pRoute, std::shared_ptr<DALRecordset> pRS);
   };
   
}

