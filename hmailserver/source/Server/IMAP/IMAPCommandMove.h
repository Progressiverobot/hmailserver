// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "IMAPCommand.h"

namespace HM
{

   class IMAPConnection;

   // Implements the MOVE command (RFC 6851).
   class IMAPCommandMOVE : public IMAPCommand
   {
   public:
      IMAPCommandMOVE();
      virtual ~IMAPCommandMOVE();

      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   };
}
