// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandDelete.h"
#include "IMAPConnection.h"
#include "IMAPFolderContainer.h"
#include "IMAPSimpleCommandParser.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/Persistence/PersistentIMAPFolder.h"
#include "../Common/BO/ACLPermission.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandDELETE::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      String sResponse = pArgument->Tag() + " OK Delete completed\r\n";
   
      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      
      pParser->Parse(pArgument);

      if (pParser->ParamCount() != 1)
         return IMAPResult(IMAPResult::ResultBad, "Command requires 1 parameter.");

      // Fetch the folder
      String sFolderName = pParser->GetParamValue(pArgument, 0);

      if (sFolderName.CompareNoCase(_T("Inbox")) == 0)
         return IMAPResult(IMAPResult::ResultNo, "You cannot delete the inbox.");
         
      std::shared_ptr<IMAPFolder> pFolder = pConnection->GetFolderByFullPath(sFolderName);
      if (!pFolder)
         return IMAPResult(IMAPResult::ResultNo, "Folder could not be found.");

      // Decided on the RESOLVED folder as well as the path text above. The text
      // compare answers "DELETE INBOX", but the inbox no longer has only one
      // name: "#Users.owner@domain.INBOX" names an inbox too - a delegating
      // owner's, or the caller's own - and DeleteObject would empty it of every
      // message and subfolder even where its "keep the inbox row" backstop
      // holds. An inbox is deletable by nobody, however it is spelled.
      if (pFolder->GetAccountID() != 0 &&
          pFolder->GetParentFolderID() == -1 &&
          pFolder->GetFolderName().CompareNoCase(_T("Inbox")) == 0)
      {
         return IMAPResult(IMAPResult::ResultNo, "You cannot delete the inbox.");
      }

      // Check if the user has access to rename this folder
      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionDeleteMailbox))
         return IMAPResult(IMAPResult::ResultNo, "ACL: DeleteMailbox permission denied (required for DELETE).");
      
      // Unchecked, and RemoveFolder_ below then took the folder out of the session's
      // view of the mailbox regardless - so a delete the database refused looked like
      // a successful DELETE until the next reconnect brought the folder back, with the
      // messages in it. Refused instead, before anything is removed in memory, so what
      // the client sees and what is stored do not disagree.
      if (!PersistentIMAPFolder::DeleteObject(pFolder))
         return IMAPResult(IMAPResult::ResultNo, "The folder could not be deleted.");

      RemoveFolder_(pFolder, pConnection);

      pConnection->SendAsciiData(sResponse);   

      return IMAPResult();
   }

   /*
      Removes the folder from the in-memory list.
   */
   void IMAPCommandDELETE::RemoveFolder_( std::shared_ptr<IMAPFolder> pFolder, std::shared_ptr<HM::IMAPConnection>  pConnection )
   {
      __int64 parentFolderID = pFolder->GetParentFolderID();

      // The parent lives in the tree that OWNS the folder. For the caller's own
      // folders that is the same object GetAccountFolders() holds (the container
      // caches one tree per account), but a delegated folder's parent is in the
      // owner's tree and is not in the caller's tree at all - looking it up
      // there answered nothing, and the parent was dereferenced unchecked.
      std::shared_ptr<IMAPFolders> owningTree;

      if (pFolder->IsPublicFolder())
         owningTree = pConnection->GetPublicFolders();
      else
         owningTree = IMAPFolderContainer::Instance()->GetFoldersForAccount(pFolder->GetAccountID());

      if (!owningTree)
         return;

      std::shared_ptr<IMAPFolders> parentFolderCollection;

      if (parentFolderID >= 0)
      {
         std::shared_ptr<IMAPFolder> pParentFolder = owningTree->GetItemByDBIDRecursive(parentFolderID);

         // No parent node in the in-memory tree. The database row is already
         // gone, which is the part that matters; the stale in-memory entry
         // disappears at the next refresh. Proceeding without the null check is
         // how a DELETE of a delegated subfolder took the whole session down.
         if (!pParentFolder)
            return;

         parentFolderCollection = pParentFolder->GetSubFolders();
      }
      else
      {
         parentFolderCollection = owningTree;
      }

      if (parentFolderCollection)
         parentFolderCollection->RemoveFolder(pFolder);
   }
}