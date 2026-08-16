// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "IMAPCommand.h"

namespace HM
{
   class IMAPCommandEXPUNGE : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);
   };


}

