// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Collection.h"

namespace HM
{
   class ServerMessage;
   class PersistentServerMessage;

   class ServerMessages : public Collection<ServerMessage, PersistentServerMessage>
   {
   public:
      ServerMessages();
      ~ServerMessages(void);

      // Refreshes this collection from the database.
      void Refresh();

      String GetMessage(const String &sName) const;
   
   protected:
      virtual String GetCollectionName() const {return "ServerMessages"; }
   private:
     
   };
}