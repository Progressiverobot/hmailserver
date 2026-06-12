// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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