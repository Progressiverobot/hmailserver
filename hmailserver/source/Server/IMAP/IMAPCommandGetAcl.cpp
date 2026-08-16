// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "IMAPCommandGetAcl.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "IMAPACLHelper.h"
#include "IMAPConfiguration.h"


#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/ACLPermission.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   IMAPResult
   IMAPCommandGetAcl::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      if (!Configuration::Instance()->GetIMAPConfiguration()->GetUseIMAPACL())
         return IMAPResult(IMAPResult::ResultBad, "ACL is not enabled.");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());
      pParser->Parse(pArgument);

      if (pParser->WordCount() != 2)
         return IMAPResult(IMAPResult::ResultBad, "MYRIGHTS command requires 1 parameter.");

      String sOriginalFolderName;
      String sFolderName;

      if (pParser->Word(1)->Clammerized())
      {
         sOriginalFolderName = pArgument->Literal(0);
         sOriginalFolderName = sFolderName;
      }
      else
      {
         sOriginalFolderName = pParser->Word(1)->Value();
         
         sFolderName = sOriginalFolderName;
         IMAPFolder::UnescapeFolderString(sFolderName);
      }
      
      std::shared_ptr<IMAPFolder> pFolder = pConnection->GetFolderByFullPath(sFolderName);
      if (!pFolder)
         return IMAPResult(IMAPResult::ResultBad, "Folder could not be found.");

      // RFC 4314 section 3.3 requires the "a" (administer) right for GETACL, and this
      // was the one ACL command without the check - SETACL and DELETEACL both have it,
      // three lines after their own folder lookup.
      //
      // Without it any authenticated user could read the complete access list of any
      // folder they could name, which on a public folder means the email address of
      // every account granted rights on it and exactly what each may do. Folder
      // existence too: the reply distinguishes a folder they have no rights on from
      // one that is not there.
      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionAdminister))
         return IMAPResult(IMAPResult::ResultNo, "Permission denied.");

      String sResponse = IMAPACLHelper::CreateACLList(pFolder, sOriginalFolderName);
      sResponse += pArgument->Tag() + _T(" OK GetAcl complete\r\n");

      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }
}