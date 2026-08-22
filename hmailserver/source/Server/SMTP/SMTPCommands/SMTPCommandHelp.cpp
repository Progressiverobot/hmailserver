// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

// SMTPCommandHelp.cpp: implementation of the SMTPCommandHelp class.
//
//////////////////////////////////////////////////////////////////////

#include "stdafx.h"
#include "SMTPCommandHelp.h"
#include "../SMTPConnection.h"

//////////////////////////////////////////////////////////////////////
// Construction/Destruction
//////////////////////////////////////////////////////////////////////

namespace HM
{
   SMTPCommandHelp::SMTPCommandHelp()
   {

   }

   SMTPCommandHelp::~SMTPCommandHelp()
   {

   }

   void
   SMTPCommandHelp::ExecuteCommand(SMTPConnection* pSMTPConnection  )
   {
      pSMTPConnection->SendData("211 DATA HELO EHLO MAIL NOOP QUIT RCPT RSET SAML TURN VRFY\r\n");

   }
}
