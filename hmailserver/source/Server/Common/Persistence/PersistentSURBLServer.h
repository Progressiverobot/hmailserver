// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class SURBLServer;
   enum PersistenceMode;

   class PersistentSURBLServer
   {
   public:
      PersistentSURBLServer(void);
      ~PersistentSURBLServer(void);
      
      static bool DeleteObject(std::shared_ptr<SURBLServer> pObject);
      static bool SaveObject(std::shared_ptr<SURBLServer> pObject);
      static bool SaveObject(std::shared_ptr<SURBLServer> pObject, String &errorMessage, PersistenceMode mode);
      static bool ReadObject(std::shared_ptr<SURBLServer> pObject, std::shared_ptr<DALRecordset> pRS);

   };
}