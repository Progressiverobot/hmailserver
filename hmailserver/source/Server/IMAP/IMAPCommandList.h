// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"

namespace HM
{
   
   class IMAPCommandLIST  : public IMAPCommand
   {
   public:
	   IMAPCommandLIST();
	   virtual ~IMAPCommandLIST();

      virtual IMAPResult ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   private:
      // RFC 5258: split a parenthesised mailbox-pattern list into individual patterns.
      static void ExtractPatterns_(const String &sParenContent, std::vector<String> &patterns);
   };

}
