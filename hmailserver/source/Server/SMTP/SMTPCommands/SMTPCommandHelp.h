// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

// SMTPCommandHelp.h: interface for the SMTPCommandHelp class.
//
//////////////////////////////////////////////////////////////////////

#pragma once

#include "ISMTPCommand.h"

namespace HM
{
   class SMPTConnection;
   
   class SMTPCommandHelp : public ISMTPCommand
   {
   public:
      SMTPCommandHelp();
      virtual ~SMTPCommandHelp();


      virtual void ExecuteCommand(SMTPConnection* pSMTPConnection );

   };

}
