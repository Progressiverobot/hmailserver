// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once


#include "IMAPCommand.h"

namespace HM
{
   class IMAPConnection;

   class IMAPCommandGetQuota : public IMAPCommand
   {
   public:
      IMAPCommandGetQuota(void);
      ~IMAPCommandGetQuota(void);

      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };
}