// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// ISMTPCommand.h: interface for the ISMTPCommand class.
//
//////////////////////////////////////////////////////////////////////


#pragma once

namespace HM
{
   class SMTPConnection;

   class ISMTPCommand  
   {
   public:
      ISMTPCommand();
      virtual ~ISMTPCommand();

      virtual void ExecuteCommand(SMTPConnection* pSMTPConnection ) = 0;

   };
}
