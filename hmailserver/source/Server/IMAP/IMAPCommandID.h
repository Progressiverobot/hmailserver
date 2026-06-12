// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"

namespace HM
{
   class IMAPConnection;

   // Implements the ID command (RFC 2971).
   class IMAPCommandID : public IMAPCommand
   {
   public:
      IMAPCommandID();
      virtual ~IMAPCommandID();

      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   };
}
