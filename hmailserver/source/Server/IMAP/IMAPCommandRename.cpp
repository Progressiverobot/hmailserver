// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandRename.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "IMAPConfiguration.h"
#include "IMAPFolderUtilities.h"

#include "IMAPFolderContainer.h"

#include "../Common/Application/ACLManager.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/Persistence/PersistentIMAPFolder.h"
#include "../Common/Util/FolderManipulationLock.h"
#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandRENAME::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {

      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      
      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);

      if (pParser->ParamCount() != 2)
         return IMAPResult(IMAPResult::ResultBad, "RENAME command requires 2 parameters.");

      // Fetch parameters
      String sOldFolderName = pParser->GetParamValue(pArgument, 0);
      String sNewFolderName = pParser->GetParamValue(pArgument, 1);
            
      std::shared_ptr<IMAPFolder> pFolderToRename = pConnection->GetFolderByFullPath(sOldFolderName);
      if (!pFolderToRename)
         return IMAPResult(IMAPResult::ResultBad, "Folder could not be found.");

      // Decided on the RESOLVED folder, not the path text. The historical check in
      // ConfirmPossibleToRename compares the path against the single word "INBOX",
      // which the inbox no longer has to be named by: "#Users.owner@domain.INBOX"
      // names an inbox too - the caller's own, or a delegating owner's - and
      // renaming an inbox out from under its account loses the folder every
      // message is delivered into.
      if (pFolderToRename->GetAccountID() != 0 &&
          pFolderToRename->GetParentFolderID() == -1 &&
          pFolderToRename->GetFolderName().CompareNoCase(_T("INBOX")) == 0)
      {
         return IMAPResult(IMAPResult::ResultNo, "Cannot rename INBOX.");
      }

      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      std::vector<String> vecOldPath = StringParser::SplitString(sOldFolderName, hierarchyDelimiter);
      std::vector<String> vecNewPath = StringParser::SplitString(sNewFolderName, hierarchyDelimiter);

      // Anything touching the "#Users" namespace takes the ownership-aware flow:
      // a source folder owned by another account, or either path shaped like the
      // namespace. The classic flow below draws only the public/non-public
      // distinction, and its in-memory manipulation is hard-wired to the
      // CALLER's folder tree - correct for every rename that existed before
      // delegated folders, and wrong for every rename involving one.
      const __int64 callerAccountID = pConnection->GetAccount()->GetID();
      const bool sourceIsDelegated =
         pFolderToRename->GetAccountID() != 0 &&
         pFolderToRename->GetAccountID() != callerAccountID;

      if (sourceIsDelegated || IsOtherUsersPath_(vecOldPath) || IsOtherUsersPath_(vecNewPath))
         return ExecuteOtherUsersRename_(pConnection, pArgument, pFolderToRename, vecNewPath);

      bool bSourceIsPublic = IMAPFolderUtilities::IsPublicFolder(vecOldPath);
      bool bDestinationIsPublic = IMAPFolderUtilities::IsPublicFolder(vecNewPath);

      IMAPResult result = ConfirmPossibleToRename(pConnection, pFolderToRename, vecOldPath, vecNewPath);
      if (result.GetResult() != IMAPResult::ResultOK)
         return result;

      // Get the old and new parent folders.
      std::shared_ptr<IMAPFolder> pOldParentFolder = GetParentFolder(pConnection, vecOldPath);
      std::shared_ptr<IMAPFolder> pNewParentFolder = GetParentFolder(pConnection, vecNewPath);

      if (vecNewPath.size() > 1 && !pNewParentFolder)
      {
         // we're renaming to a sub folder, but the new parent folder does not exist. We need
         // to create the new parent folder before we try to add the new folder to it.
         std::vector<String> vecNewFolderParent = vecNewPath;
         vecNewFolderParent.erase(vecNewFolderParent.end()-1);

         pConnection->GetAccountFolders()->CreatePath(pConnection->GetAccountFolders(), vecNewFolderParent, false);

         // fetch the newly created folder
         pNewParentFolder  = GetParentFolder(pConnection, vecNewPath);
      }

      // Create global for manipulating folders for this account.
      FolderManipulationLock oFolderLock ((int) pConnection->GetAccount()->GetID(), -1);
      oFolderLock.Lock();

      // Get the new folder name
      String sFolderName = vecNewPath[vecNewPath.size()-1];

      // We are at the last position. Get folder name here

      // 1) Remove from old parent.
      if (pOldParentFolder)
         pOldParentFolder->GetSubFolders()->RemoveFolder(pFolderToRename);
      else
      {
         if (bSourceIsPublic)
            pConnection->GetPublicFolders()->RemoveFolder(pFolderToRename);  
         else
            pConnection->GetAccountFolders()->RemoveFolder(pFolderToRename);
      }

      // 2a) Set the new namepNewParentFolder of the folder
      pFolderToRename->SetFolderName(sFolderName);

      // 2a) Add to new parent, and update all paths recursivly.
      if (pNewParentFolder)
      {
         pFolderToRename->SetParentFolderID(pNewParentFolder->GetID());
         pNewParentFolder->GetSubFolders()->AddItem(pFolderToRename);
      }
      else
      {
         // Make sure that the parent folder is set to nothing here.
         // it could be that it's set to something else at this stage,
         // since the folder have been moved from one location to
         // another...

         pFolderToRename->SetParentFolderID(-1);
         
         if (bDestinationIsPublic)
         {
            // We are not allowed to add new root folders.
            HM_ASSERT(0);
         }
         else
         {
            pConnection->GetAccountFolders()->AddItem(pFolderToRename);
         }
      }

      // Unchecked, so a failed save still answered "OK Rename completed" for a folder
      // that still has its old name in the database.
      //
      // The in-memory re-parenting above is not unwound here, unlike the SUBSCRIBE and
      // UNSUBSCRIBE cases where putting a single flag back is exact. Reversing a move
      // between folder containers correctly is not, and getting it half right would
      // leave the session's folder tree in a state no reconnect produces. Telling the
      // client NO is the part that matters - it will not treat the rename as done -
      // and the next refresh reads the tree back from the database.
      if (!PersistentIMAPFolder::SaveObject(pFolderToRename))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6098, "IMAPCommandRENAME::ExecuteCommand",
            "A folder rename could not be saved and has been refused. The folder tree held by this connection may show the new name until the client reconnects.");

         return IMAPResult(IMAPResult::ResultNo, "The folder could not be renamed.");
      }

      String sResponse = pArgument->Tag() + " OK Rename completed\r\n";

      pConnection->SendAsciiData(sResponse);   
 
      return IMAPResult();
   }
   
   std::shared_ptr<IMAPFolder> 
   IMAPCommandRENAME::GetParentFolder(std::shared_ptr<HM::IMAPConnection> pConnection, const std::vector<String> &vecFolderPath)
   {
      // Get the old parent folder
      if (vecFolderPath.size() < 1)
      {
         std::shared_ptr<IMAPFolder> pEmpty;
         return pEmpty;
      }
      
      // Remove everythin but the last...
      std::vector<String> vecParentFolder = vecFolderPath;
      vecParentFolder.resize(vecParentFolder.size() -1);

      std::shared_ptr<IMAPFolder> pParentFolder = pConnection->GetFolderByFullPath(vecParentFolder);

      return pParentFolder;
   }

   IMAPResult
   IMAPCommandRENAME::ConfirmPossibleToRename(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPFolder> pFolderToRename, const std::vector<String> &vecOldPath, const std::vector<String> &vecNewPath)
   {


      if (!pFolderToRename)
         return IMAPResult(IMAPResult::ResultNo, "Folder to rename not found.");

      if ((vecOldPath.size() == 1 && vecOldPath[0].CompareNoCase(_T("INBOX")) == 0) ||
         (vecNewPath.size() == 1 && vecNewPath[0].CompareNoCase(_T("INBOX")) == 0))
      {
         return IMAPResult(IMAPResult::ResultNo, "Cannot rename INBOX.");
      }

      bool bSourceIsPublic = IMAPFolderUtilities::IsPublicFolder(vecOldPath);
      bool bDestinationIsPublic = IMAPFolderUtilities::IsPublicFolder(vecNewPath);

      if ((bSourceIsPublic && !bDestinationIsPublic) ||
         (!bSourceIsPublic && bDestinationIsPublic))
      {
         return IMAPResult(IMAPResult::ResultNo, "RENAME: Cannot rename a public folder to local or vice versa.");
      }
      
      // The user is trying to rename to a root public folder, such as Public folders.Something
      // This isn't allowed at the moment, since permissions for this is undefined.
      if (bDestinationIsPublic && vecNewPath.size() == 2)
         return IMAPResult(IMAPResult::ResultNo, "Root public folders can only be created using administration tools.");

      // Check if the user has access to rename this folder
      if (!pConnection->CheckPermission(pFolderToRename, ACLPermission::PermissionDeleteMailbox))
         return IMAPResult(IMAPResult::ResultNo, "ACL DeleteMailbox permission denied (required for RENAME).");
         
      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();
      String sNewFolderName = StringParser::JoinVector(vecNewPath, hierarchyDelimiter);
      std::shared_ptr<IMAPFolder> pTargetFolder = pConnection->GetFolderByFullPath(sNewFolderName);
      if (pTargetFolder)
         return IMAPResult(IMAPResult::ResultNo, "Target folder already exist.");


      int iRecursion = 0;
      int iOldFolderDepth = pFolderToRename->GetFolderDepth(iRecursion);
      int iNewMaxFolderDepth = (int) (iOldFolderDepth + (vecNewPath.size()-1));
      
      if (iNewMaxFolderDepth > 25)
         return IMAPResult(IMAPResult::ResultNo, "To many sub-folders in structure.");
         
       if (!IMAPFolder::IsValidFolderName(vecNewPath, bDestinationIsPublic))
          return IMAPResult(IMAPResult::ResultNo, "The new folder name is invalid.");

       // Prevent user from moving parent folder into sub folder.
       // For instance, the user should not be able to move A.B into A.B.A.
       // or Folder.Sub1 into Folder.Sub1.Sub2. Because if the user did this, what
       // would happen to Folder.Sub1?
       String sOldFolderName = StringParser::JoinVector(vecOldPath, hierarchyDelimiter);

       // Compare using the configured hierarchy delimiter; hard-coding "." let a
       // folder become its own parent on a server using a different delimiter,
       // which loses the folder and its mail from LIST.
       if (sNewFolderName.FindNoCase(sOldFolderName + hierarchyDelimiter) == 0)
       {
          // The new path starts with the entire old path. The user is trying to move an existing
          // folder into its own sub folder. This is not allowed, for reason stated above.
          return IMAPResult(IMAPResult::ResultNo, "A folder cannot be moved into one of its subfolders.");
       }

       return IMAPResult();
   }

   bool
   IMAPCommandRENAME::IsOtherUsersPath_(const std::vector<String> &vecPath)
   {
      return ACLManager::GetOtherUsersNamespaceEnabled() &&
             vecPath.size() > 0 &&
             ACLManager::GetOtherUsersFolderName().CompareNoCase(vecPath[0]) == 0;
   }

   IMAPResult
   IMAPCommandRENAME::ExecuteOtherUsersRename_(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument, std::shared_ptr<IMAPFolder> pFolderToRename, const std::vector<String> &vecNewPath)
   {
      // Everything here is decided against the account that OWNS the source
      // folder. The caller's rights on that account's folders come from
      // ACLManager, exactly as they do for SELECT and DELETE; nothing in this
      // flow makes a second, parallel access decision.

      const __int64 callerAccountID = pConnection->GetAccount()->GetID();
      const __int64 ownerAccountID = pFolderToRename->GetAccountID();

      // A public folder cannot be renamed into the "#Users" namespace: the
      // destination is some account's mailbox and the source belongs to no
      // account. Same refusal the classic flow gives the public/local mix.
      if (ownerAccountID == 0)
         return IMAPResult(IMAPResult::ResultNo, "RENAME: Cannot rename a public folder to local or vice versa.");

      std::shared_ptr<const Account> pOwner = CacheContainer::Instance()->GetAccount(ownerAccountID);
      if (!pOwner)
         return IMAPResult(IMAPResult::ResultNo, "The folder could not be renamed.");

      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      // Which account does the destination path name? Decided from the path
      // SHAPE plus facts the caller already holds (the source folder's owner),
      // never by probing the account database - so the refusals below cannot be
      // used to learn whether some other account exists.
      //
      //   - public-shaped                          -> no account (refused above/below)
      //   - "#Users" + the source owner's address  -> the same owner's tree
      //   - "#Users" + anything else               -> a different owner (refused)
      //   - anything else                          -> the caller's own tree
      std::vector<String> vecOwnerRelativeNewPath;

      if (IMAPFolderUtilities::IsPublicFolder(vecNewPath))
         return IMAPResult(IMAPResult::ResultNo, "RENAME: Cannot rename a public folder to local or vice versa.");

      if (IsOtherUsersPath_(vecNewPath))
      {
         std::vector<String> vecExpectedPrefix = StringParser::SplitString(pOwner->GetAddress(), hierarchyDelimiter);
         vecExpectedPrefix.insert(vecExpectedPrefix.begin(), ACLManager::GetOtherUsersFolderName());

         bool prefixMatches = vecNewPath.size() > vecExpectedPrefix.size();
         if (prefixMatches)
         {
            for (size_t i = 0; i < vecExpectedPrefix.size(); i++)
            {
               if (vecExpectedPrefix[i].CompareNoCase(vecNewPath[i]) != 0)
               {
                  prefixMatches = false;
                  break;
               }
            }
         }

         if (!prefixMatches)
            return IMAPResult(IMAPResult::ResultNo, "RENAME: A folder cannot be renamed into a different account's mailbox.");

         vecOwnerRelativeNewPath.assign(vecNewPath.begin() + vecExpectedPrefix.size(), vecNewPath.end());
      }
      else
      {
         // A plain destination path names the caller's own tree. That is a
         // rename between owners unless the source folder is the caller's own
         // (which it can be: the caller's folders resolve under
         // "#Users.<own address>" too).
         if (ownerAccountID != callerAccountID)
            return IMAPResult(IMAPResult::ResultNo, "RENAME: A folder cannot be renamed into a different account's mailbox.");

         vecOwnerRelativeNewPath = vecNewPath;
      }

      if (vecOwnerRelativeNewPath.size() == 1 &&
          vecOwnerRelativeNewPath[0].CompareNoCase(_T("INBOX")) == 0)
      {
         return IMAPResult(IMAPResult::ResultNo, "Cannot rename INBOX.");
      }

      if (!IMAPFolder::IsValidFolderName(vecOwnerRelativeNewPath, false))
         return IMAPResult(IMAPResult::ResultNo, "The new folder name is invalid.");

      std::shared_ptr<IMAPFolders> pOwnerTree = IMAPFolderContainer::Instance()->GetFoldersForAccount(ownerAccountID);
      if (!pOwnerTree)
         return IMAPResult(IMAPResult::ResultNo, "The folder could not be renamed.");

      // RFC 4314: RENAME demands the "x" right on the mailbox being renamed and
      // the "k" right on its new parent - it is a delete at the old name and a
      // create at the new one. The "x" half:
      if (!pConnection->CheckPermission(pFolderToRename, ACLPermission::PermissionDeleteMailbox))
         return IMAPResult(IMAPResult::ResultNo, "ACL DeleteMailbox permission denied (required for RENAME).");

      // The "k" half. The destination parent must already exist in the owner's
      // tree - unlike the classic flow this one never creates missing parent
      // folders, because each of those creations would itself need a rights
      // decision against a folder that does not exist yet. A parent the caller
      // holds no lookup right on answers exactly like a parent that is not
      // there, so this refusal is not an oracle for the owner's folder names.
      std::shared_ptr<IMAPFolder> pNewParentFolder;

      if (vecOwnerRelativeNewPath.size() > 1)
      {
         std::vector<String> vecNewParentPath(vecOwnerRelativeNewPath.begin(), vecOwnerRelativeNewPath.end() - 1);

         pNewParentFolder = pOwnerTree->GetFolderByFullPath(vecNewParentPath);

         if (!pNewParentFolder || !pConnection->CheckPermission(pNewParentFolder, ACLPermission::PermissionLookup))
            return IMAPResult(IMAPResult::ResultNo, "RENAME: The destination folder does not exist.");

         if (!pConnection->CheckPermission(pNewParentFolder, ACLPermission::PermissionCreate))
            return IMAPResult(IMAPResult::ResultNo, "ACL CreateMailbox permission denied (required for RENAME).");
      }
      else
      {
         // A root-level destination has no parent folder to carry the "k"
         // right, so only the owner may put a folder there. There is no
         // folder-that-grants-create at the root of somebody else's account.
         if (callerAccountID != ownerAccountID)
            return IMAPResult(IMAPResult::ResultNo, "ACL CreateMailbox permission denied (required for RENAME).");
      }

      // Existence is checked in the OWNER's tree. The connection's resolver
      // hides folders the caller lacks the lookup right on, and a collision
      // with a hidden folder must still be refused - AddItem below would
      // otherwise put two folders with one name under one parent. The caller
      // holds "k" on the parent here, and a name collision is exactly what the
      // "k" right already reveals through CREATE.
      if (pOwnerTree->GetFolderByFullPath(vecOwnerRelativeNewPath))
         return IMAPResult(IMAPResult::ResultNo, "Target folder already exist.");

      // The owner-relative path of the source folder, walked up by parent id in
      // the owner's tree. Derived from the resolved folder rather than from the
      // path the client sent, which may name the same folder several ways.
      std::vector<String> vecOwnerRelativeOldPath;
      {
         std::shared_ptr<IMAPFolder> pWalk = pFolderToRename;
         int recursionGuard = 0;

         while (pWalk && recursionGuard++ <= IMAPFolder::MaxFolderDepth)
         {
            vecOwnerRelativeOldPath.insert(vecOwnerRelativeOldPath.begin(), pWalk->GetFolderName());

            __int64 walkParentID = pWalk->GetParentFolderID();
            pWalk = walkParentID >= 0 ? pOwnerTree->GetItemByDBIDRecursive(walkParentID) : nullptr;
         }
      }

      // Same two structural rules the classic flow enforces.
      String sOwnerRelativeOldName = StringParser::JoinVector(vecOwnerRelativeOldPath, hierarchyDelimiter);
      String sOwnerRelativeNewName = StringParser::JoinVector(vecOwnerRelativeNewPath, hierarchyDelimiter);

      if (sOwnerRelativeNewName.FindNoCase(sOwnerRelativeOldName + hierarchyDelimiter) == 0)
         return IMAPResult(IMAPResult::ResultNo, "A folder cannot be moved into one of its subfolders.");

      int iRecursion = 0;
      int iOldFolderDepth = pFolderToRename->GetFolderDepth(iRecursion);
      int iNewMaxFolderDepth = (int) (iOldFolderDepth + (vecOwnerRelativeNewPath.size() - 1));

      if (iNewMaxFolderDepth > IMAPFolder::MaxFolderDepth)
         return IMAPResult(IMAPResult::ResultNo, "To many sub-folders in structure.");

      // The tree being manipulated is the owner's, so the lock is the owner's
      // too - the same lock the owner's own sessions take.
      FolderManipulationLock oFolderLock((int) ownerAccountID, -1);
      oFolderLock.Lock();

      std::shared_ptr<IMAPFolder> pOldParentFolder;
      __int64 oldParentFolderID = pFolderToRename->GetParentFolderID();
      if (oldParentFolderID >= 0)
         pOldParentFolder = pOwnerTree->GetItemByDBIDRecursive(oldParentFolderID);

      if (pOldParentFolder)
         pOldParentFolder->GetSubFolders()->RemoveFolder(pFolderToRename);
      else
         pOwnerTree->RemoveFolder(pFolderToRename);

      pFolderToRename->SetFolderName(vecOwnerRelativeNewPath[vecOwnerRelativeNewPath.size() - 1]);

      if (pNewParentFolder)
      {
         pFolderToRename->SetParentFolderID(pNewParentFolder->GetID());
         pNewParentFolder->GetSubFolders()->AddItem(pFolderToRename);
      }
      else
      {
         pFolderToRename->SetParentFolderID(-1);
         pOwnerTree->AddItem(pFolderToRename);
      }

      // Same contract as the classic flow: a refused save is answered NO, the
      // in-memory re-parenting is not unwound (see the comment there), and the
      // next refresh reads the tree back from the database.
      if (!PersistentIMAPFolder::SaveObject(pFolderToRename))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6300, "IMAPCommandRENAME::ExecuteOtherUsersRename_",
            "A rename of a delegated folder could not be saved and has been refused. The owner's folder tree held in memory may show the new name until it is reloaded.");

         return IMAPResult(IMAPResult::ResultNo, "The folder could not be renamed.");
      }

      String sResponse = pArgument->Tag() + " OK Rename completed\r\n";
      pConnection->SendAsciiData(sResponse);

      return IMAPResult();
   }
}