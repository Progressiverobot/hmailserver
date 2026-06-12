// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandID.h"
#include "IMAPConnection.h"

#include "../Common/Application/Application.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommandID::IMAPCommandID()
   {

   }

   IMAPCommandID::~IMAPCommandID()
   {

   }

   IMAPResult
   IMAPCommandID::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      // RFC 2971: the server responds with its own identification regardless
      // of the client-supplied parameter list (which may be NIL).
      String sResponse;
      sResponse.Format(_T("* ID (\"name\" \"hMailServer\" \"version\" \"%s\")\r\n%s OK ID completed\r\n"),
         Application::Instance()->GetVersionNumber().c_str(),
         pArgument->Tag().c_str());

      pConnection->SendAsciiData(sResponse);

      return IMAPResult();
   }
}
