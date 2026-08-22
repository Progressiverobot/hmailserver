// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class TCPIPPort;
   enum PersistenceMode;

   class PersistentTCPIPPort
   {
   public:
      PersistentTCPIPPort(void);
      ~PersistentTCPIPPort(void);
      
      static bool DeleteObject(std::shared_ptr<TCPIPPort> pObject);
      static bool SaveObject(std::shared_ptr<TCPIPPort> pObject, String &errorMessage, PersistenceMode mode);
      static bool ReadObject(std::shared_ptr<TCPIPPort> pObject, std::shared_ptr<DALRecordset> pRS);

   };
}