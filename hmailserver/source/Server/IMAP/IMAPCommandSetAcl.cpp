// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "IMAPCommandSetAcl.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "IMAPACLHelper.h"
#include "IMAPConfiguration.h"

#include "../Common/Application/ACLManager.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/Cache/CacheContainer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandSetAcl::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      if (!Configuration::Instance()->GetIMAPConfiguration()->GetUseIMAPACL())
         return IMAPResult(IMAPResult::ResultBad, "ACL is not enabled.");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());
      pParser->Parse(pArgument);

      if (pParser->ParamCount() < 3)
         return IMAPResult(IMAPResult::ResultBad, "SETACL command requires 3 parameter.");

      String sOriginalFolderName;
      String sFolderName;

      if (pParser->Word(1)->Clammerized())
      {
         sFolderName = pArgument->Literal(0);
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

      // Account folders are grantable here too, not just public ones: this is
      // how an owner shares a mailbox of their own, and how a delegate the
      // owner trusted with "a" administers it. The gate is the RFC 4314
      // administer right, which the folder's owner holds implicitly
      // (ACLManager::GetPermissionForFolder answers GrantAll for the owner) -
      // so an owner needs no stored grant to manage their own folder, and no
      // stored grant can ever take that management away.
      //
      // A folder the caller may not look up never reaches this point: path
      // resolution already answered "Folder could not be found", identically to
      // a folder that does not exist.
      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionAdminister))
         return IMAPResult(IMAPResult::ResultNo, "Permission denied.");

      String sIdentifier = pParser->Word(2)->Value();
      String sAccessRights = pParser->Word(3)->Value();

      // Check if the given access rights are really valid.
      if (!IMAPACLHelper::IsValidPermissionString(sAccessRights))
         return IMAPResult(IMAPResult::ResultNo, "SetACL failed. Invalid access right string");

      // The owner's rights are implicit and total; they are not a row that can
      // be written, and a stored row for the owner could only disagree with the
      // truth. Refusing here (rather than letting ACLManager::SetACL fail
      // generically) tells the client WHY - and it is one half of the property
      // that no SETACL/DELETEACL sequence can lock an owner out of their own
      // folder. The other half is that the implicit grant is not stored, so
      // there is nothing to delete.
      if (pFolder->GetAccountID() != 0)
      {
         std::shared_ptr<const Account> pIdentifierAccount = CacheContainer::Instance()->GetAccount(sIdentifier);
         if (pIdentifierAccount && pIdentifierAccount->GetID() == pFolder->GetAccountID())
            return IMAPResult(IMAPResult::ResultNo, "SETACL: The folder owner's rights are implicit and cannot be changed.");
      }

	  ACLManager aclManager;
      if (!aclManager.SetACL(pFolder, sIdentifier, sAccessRights))
         return IMAPResult(IMAPResult::ResultNo, "SetACL failed.");

      /*
         3.1 SETACL command, RFC 4314
         Note that an unrecognized right MUST cause the command to return the
         BAD response. 
      */

      String sResponse = pArgument->Tag() + _T(" OK SETACL complete\r\n");
      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }
}