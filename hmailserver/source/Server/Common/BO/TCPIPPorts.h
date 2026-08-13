// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "Collection.h"
#include "TCPIPPort.h"

#include "../Persistence/PersistentTCPIPPort.h"

namespace HM
{
   class TCPIPPorts : public Collection<TCPIPPort, PersistentTCPIPPort>
   {
   public:
      TCPIPPorts();
      ~TCPIPPorts(void);

      std::shared_ptr<TCPIPPort> GetPort(const IPAddress &iIPAddress, int iPort);

      void Refresh();
      // Refreshes this collection from the database.

      // False if the defaults could not be written. The existing ports are deleted
      // first, so a false return means the server may now be listening on fewer
      // ports than it started with - possibly none.
      bool SetDefault();
      // Generates a default set of items in this collection.

   protected:
      virtual String GetCollectionName() const {return "TCPIPPorts"; }

   };
}