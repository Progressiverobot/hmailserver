// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandUnsubscribe.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "../Common/Persistence/PersistentIMAPFolder.h"
#include "../Common/BO/IMAPFolder.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandUNSUBSCRIBE::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      // Parse the command
      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);
      if (pParser->ParamCount() != 1)
         return IMAPResult(IMAPResult::ResultBad, "Command requires 1 parameter.");

      // Fetch the folder name. 
      String sFolderName = pParser->GetParamValue(pArgument, 0);

      std::shared_ptr<IMAPFolder> pFolder = pConnection->GetFolderByFullPath(sFolderName);

      IMAPResult result = ConfirmPossibleToUnsubscribe(pFolder);
      if (result.GetResult() != IMAPResult::ResultOK)
         return result;

      if (pFolder)
      {
         pFolder->SetIsSubscribed(false);

         // As in SUBSCRIBE: answering OK for a change that was not stored is the one
         // thing a client cannot recover from, because it will not ask again.
         if (!PersistentIMAPFolder::SaveObject(pFolder))
         {
            pFolder->SetIsSubscribed(true);
            return IMAPResult(IMAPResult::ResultNo, "The unsubscription could not be saved.");
         }
      }


      String sResponse = pArgument->Tag() + " OK Unsubscribe completed\r\n";
      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandUNSUBSCRIBE::ConfirmPossibleToUnsubscribe(std::shared_ptr<IMAPFolder> pFolder)
   {
      if (!pFolder)
         return IMAPResult(IMAPResult::ResultNo, "That mailbox does not exist.");

      if (pFolder->GetAccountID() == 0)
         return IMAPResult(IMAPResult::ResultNo, "It is not possible to unsubscribe from public folders.");

      return IMAPResult();
   }
}