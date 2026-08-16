// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandSubscribe.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "IMAPConfiguration.h"

#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolder.h"

#include "../Common/Persistence/PersistentIMAPFolder.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandSUBSCRIBE::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);

      if (pParser->ParamCount() < 1)
         return IMAPResult(IMAPResult::ResultBad, "Command requires at least 1 parameter.");

      // Fetch the folder name.
      String sFolderName = pParser->GetParamValue(pArgument, 0);

      if (sFolderName == Configuration::Instance()->GetIMAPConfiguration()->GetIMAPPublicFolderName())
      {
         // Silently accept this. If we don't, some email clients (such as OE) will complain that it won't
         // be possible to subscribe to sub folders
         String sResponse = pArgument->Tag() + " OK Subscribe completed\r\n";
         pConnection->SendAsciiData(sResponse);   

         return IMAPResult();
      }

      std::shared_ptr<IMAPFolder> pFolder = pConnection->GetFolderByFullPath(sFolderName);
      if (!pFolder)
         return IMAPResult(IMAPResult::ResultNo, "Folder could not be found.");
      
      // Check if ther user has access to see this folder.
      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionLookup))
         return IMAPResult(IMAPResult::ResultNo, "ACL: Lookup permission denied (required for SUBSCRIBE).");

      if (!pFolder)
         return IMAPResult(IMAPResult::ResultNo, "That mailbox does not exist.");

      pFolder->SetIsSubscribed(true);

      // Unchecked, so a failed save still answered "OK Subscribe completed" and the
      // client had every reason to believe the subscription would be there next time.
      // The in-memory flag is put back as well, so LIST and LSUB in this same session
      // report what is actually stored rather than what was attempted.
      //
      // No error record here: the DAL has already reported the underlying SQL failure,
      // and unlike the delivery paths there is somebody to tell - the client asked for
      // this and is getting an answer.
      if (!PersistentIMAPFolder::SaveObject(pFolder))
      {
         pFolder->SetIsSubscribed(false);
         return IMAPResult(IMAPResult::ResultNo, "The subscription could not be saved.");
      }

      String sResponse = pArgument->Tag() + " OK Subscribe completed\r\n";
      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }

}