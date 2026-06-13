// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandMove.h"
#include "IMAPMove.h"
#include "IMAPConnection.h"

#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/ACLPermission.h"

#include "IMAPSimpleCommandParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommandMOVE::IMAPCommandMOVE()
   {

   }

   IMAPCommandMOVE::~IMAPCommandMOVE()
   {

   }


   IMAPResult
   IMAPCommandMOVE::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");
      
      if (!pConnection->GetCurrentFolder())
         return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

      if (pConnection->GetCurrentFolderReadOnly())
         return IMAPResult(IMAPResult::ResultNo, "MOVE command on read-only folder.");

      if (!pConnection->CheckPermission(pConnection->GetCurrentFolder(), ACLPermission::PermissionExpunge))
         return IMAPResult(IMAPResult::ResultBad, "ACL: Expunge permission denied (Required for MOVE command).");

      std::shared_ptr<IMAPMove> pMove = std::shared_ptr<IMAPMove>(new IMAPMove());
      pMove->SetIsUID(false);

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());
      pParser->Parse(pArgument);
      if (pParser->ParamCount() != 2)
         return IMAPResult(IMAPResult::ResultBad, "Command requires 2 parameters.\r\n");

      String sMailNo = pParser->GetParamValue(pArgument, 0);
      String sFolderName = pParser->GetParamValue(pArgument, 1);

      pArgument->Command("\"" + sFolderName + "\"");

      std::shared_ptr<IMAPFolder> pFolder = pConnection->GetFolderByFullPath(sFolderName);

      if (!pFolder)
         return IMAPResult(IMAPResult::ResultNo, "[TRYCREATE] Can't find mailbox with that name.\r\n");

      IMAPResult result = pMove->DoForMails(pConnection, sMailNo, pArgument);

      if (result.GetResult() == IMAPResult::ResultOK)
      {
         String sUidPlus = pMove->GetUIDPlusResponseCode();
         pMove->ExpungeMovedMessages(pConnection);
         pConnection->SendAsciiData(pArgument->Tag() + " OK " + sUidPlus + "MOVE completed\r\n");
      }

      return result;
   }
}
