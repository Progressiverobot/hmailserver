// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "IMAPCommandStartTls.h"
#include "IMAPConnection.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommandStartTls::IMAPCommandStartTls()
   {

   }

   IMAPCommandStartTls::~IMAPCommandStartTls()
   {

   }

   IMAPResult
   IMAPCommandStartTls::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      // RFC 4978 section 3: TLS is not started under compression.
      if (pConnection->IsCompressed())
         return IMAPResult(IMAPResult::ResultBad, "STARTTLS is not available once COMPRESS is active");

      if (pConnection->GetConnectionSecurity() == CSSTARTTLSOptional ||
         pConnection->GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         pConnection->SendAsciiData(pArgument->Tag() + " OK Begin TLS negotiation now\r\n");

         pConnection->StartHandshake();

         return IMAPResult(IMAPResult::ResultOKSupressRead, "");
      }
      else
      {
         return IMAPResult(IMAPResult::ResultBad, "Unknown or NULL command");
      }
      
   }
}
