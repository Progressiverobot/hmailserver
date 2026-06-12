// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "IMAPCommand.h"

namespace HM
{

   class IMAPFolder;

   class IMAPCommandUNSUBSCRIBE : public IMAPCommand
   {
      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   private:

      IMAPResult ConfirmPossibleToUnsubscribe(std::shared_ptr<IMAPFolder> pFolder);
   };
 

}

