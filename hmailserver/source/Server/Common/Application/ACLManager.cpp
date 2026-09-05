// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "ACLManager.h"

#include "../Cache/CacheContainer.h"
#include "../BO/IMAPFolders.h"
#include "../BO/ACLPermissions.h"
#include "../BO/Groups.h"
#include "../BO/Account.h"
#include "../../IMAP/IMAPConfiguration.h"
#include "../../IMAP/IMAPFolderContainer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif


namespace HM
{
   ACLManager::ACLManager(void)
   {

   }

   ACLManager::~ACLManager(void)
   {

   }

   // Sorts a list of ACL permissions based on their ID. Low ID will come before High ID.
   class ACLSortID {
   public:
      //Return true if s1 < s2; otherwise, return false.
      bool operator()(const std::shared_ptr<ACLPermission> p1, const std::shared_ptr<ACLPermission>  p2)
      {
         return p1->GetID() < p2->GetID();
      }

   private:
   };   

   /* 
      Example of folder structure:

      Folder A <- Shared
         Folder A1 <- Inherited
         Folder A2 <- Inherited
         Folder A3 <- Shared with separate permissions
      Folder B
         Folder B1 <- Shared
         Folder B2
   */


   bool
   ACLManager::GetAclEnforcementEnabled()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Whether folder access control is enforced. See the header for why this is one
   // function rather than a setting each caller reads for itself.
   //---------------------------------------------------------------------------()
   {
      return Configuration::Instance()->GetIMAPConfiguration()->GetUseIMAPACL();
   }

   bool
   ACLManager::CheckPermission(__int64 actorAccountId, std::shared_ptr<IMAPFolder> folder, int permission)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The one folder-access decision. See the header.
   //---------------------------------------------------------------------------()
   {
      if (!folder)
         return false;

      if (!GetAclEnforcementEnabled())
      {
         // Access control has been disabled. Allow everything.
         return true;
      }

      ACLManager aclManager;
      std::shared_ptr<ACLPermission> rights = aclManager.GetPermissionForFolder(actorAccountId, folder);
      if (!rights)
         return false;

      return rights->GetAllow((ACLPermission::ePermission) permission);
   }

   void
   ACLManager::GetReadWriteAccess(__int64 actorAccountId, std::shared_ptr<IMAPFolder> folder, bool &readAccess, bool &writeAccess)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The SELECT/EXAMINE summary of the decision above: may the account read, and
   // may it change anything - any one of the writing rights counts.
   //---------------------------------------------------------------------------()
   {
      readAccess = false;
      writeAccess = false;

      if (!folder)
         return;

      if (!GetAclEnforcementEnabled())
      {
         readAccess = true;
         writeAccess = true;
         return;
      }

      ACLManager aclManager;
      std::shared_ptr<ACLPermission> rights = aclManager.GetPermissionForFolder(actorAccountId, folder);
      if (!rights)
         return;

      readAccess = rights->GetAllow(ACLPermission::PermissionRead);
      writeAccess = rights->GetAllow(ACLPermission::PermissionWriteOthers) ||
         rights->GetAllow(ACLPermission::PermissionWriteSeen) ||
         rights->GetAllow(ACLPermission::PermissionWriteDeleted) ||
         rights->GetAllow(ACLPermission::PermissionInsert) ||
         rights->GetAllow(ACLPermission::PermissionExpunge);
   }

   bool
   ACLManager::CheckDelegatedRight(__int64 actorAccountId, std::shared_ptr<IMAPFolder> folder, int permission)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // A right one account grants another. Off means nothing is granted. See the
   // header.
   //---------------------------------------------------------------------------()
   {
      if (!folder)
         return false;

      if (!GetAclEnforcementEnabled())
         return false;

      if (folder->GetAccountID() == actorAccountId)
         return false;

      ACLManager aclManager;
      std::shared_ptr<ACLPermission> rights = aclManager.GetPermissionForFolder(actorAccountId, folder);
      if (!rights)
         return false;

      return rights->GetAllow((ACLPermission::ePermission) permission);
   }

   String
   ACLManager::GetOtherUsersFolderName()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The single definition of the "Other Users" namespace prefix. See the header.
   //---------------------------------------------------------------------------()
   {
      return "#Users";
   }

   bool
   ACLManager::GetOtherUsersNamespaceEnabled()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // No ACL enforcement means no "#Users" namespace - see the header for why
   // "disabled" must not mean "open".
   //---------------------------------------------------------------------------()
   {
      return GetAclEnforcementEnabled();
   }

   std::vector<__int64>
   ACLManager::GetAccountsWithFolderShares()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Candidate owners for the "#Users" namespace: accounts with at least one ACL
   // entry on one of their own folders. Not an access decision - see the header.
   //---------------------------------------------------------------------------()
   {
      std::vector<__int64> result;

      SQLCommand command(
         "SELECT DISTINCT f.folderaccountid AS ownerid "
         "FROM hm_acl a "
         "INNER JOIN hm_imapfolders f ON f.folderid = a.aclsharefolderid "
         "WHERE f.folderaccountid <> 0 "
         "ORDER BY f.folderaccountid");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
      {
         // The recordset itself failed - a database problem, not an empty result.
         // Report it: the symptom otherwise is shared mailboxes silently missing
         // from LIST, which looks exactly like a permissions mistake.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6252, "ACLManager::GetAccountsWithFolderShares",
            "The list of accounts with shared folders could not be read from the database.");
         return result;
      }

      while (!pRS->IsEOF())
      {
         result.push_back(pRS->GetInt64Value("ownerid"));
         pRS->MoveNext();
      }

      return result;
   }

   std::shared_ptr<ACLPermissions>
   ACLManager::GetPermissionsSetOnFolder(std::shared_ptr<IMAPFolder> pFolder)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The ACL entries stored for exactly this folder. IMAPFolder::GetPermissions()
   // deliberately skips the database read for account-level folders ("account
   // level folders never have permissions set") - which stopped being true the
   // day one user's folder could be shared with another. The read lives here,
   // in the one class that decides access, rather than in IMAPFolder, so the
   // business object keeps its cheap no-op for the overwhelmingly common
   // unshared case everywhere else it is called.
   //---------------------------------------------------------------------------()
   {
      if (pFolder->IsPublicFolder())
         return pFolder->GetPermissions();

      std::shared_ptr<ACLPermissions> pPermissions = std::shared_ptr<ACLPermissions>(new ACLPermissions(pFolder->GetID()));
      pPermissions->Refresh();

      return pPermissions;
   }

   std::shared_ptr<ACLPermission>
   ACLManager::GetPermissionForFolder(__int64 iAccountID, std::shared_ptr<IMAPFolder> pFolder)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Input:
   // iAccountID - The account which wants access to the folder
   // pFolder    - The folder the account wants access to
   //---------------------------------------------------------------------------()
   {
      if (pFolder->GetAccountID() == iAccountID)
      {
         // Folder is owned by requester. Full access.
         std::shared_ptr<ACLPermission> pFullPermissions = std::shared_ptr<ACLPermission>(new ACLPermission);
         pFullPermissions->GrantAll();
         return pFullPermissions;
      }

      // The requester does not own the folder: it is either a public folder or
      // another account's (shared) folder. Since not all folders have their own
      // permissions, the walk below locates the nearest ancestor that has any
      // and inherits from it - and the ancestors of a shared folder live in the
      // OWNER's tree, not the public tree, so the parent lookups must be made
      // against the tree the folder actually belongs to.
      std::shared_ptr<IMAPFolders> pOwnerTree;
      if (pFolder->GetAccountID() == 0)
         pOwnerTree = Configuration::Instance()->GetIMAPConfiguration()->GetPublicFolders();
      else
         pOwnerTree = IMAPFolderContainer::Instance()->GetFoldersForAccount(pFolder->GetAccountID());

      std::shared_ptr<IMAPFolder> pCheckFolder = pFolder;

      int maxRecursions = 250;
      while (pCheckFolder && maxRecursions > 0)
      {
         maxRecursions--;

         // Check if permissions is set for this folder. If it is, we need to check
         // if we have permissions to it.
         std::shared_ptr<ACLPermissions> pPermissions = GetPermissionsSetOnFolder(pCheckFolder);

         if (pPermissions && pPermissions->GetCount() > 0)
         {
            // We found permissions for this folder. Locate the permission for the given user.
            std::shared_ptr<ACLPermission> pPermission = GetPermissionForAccount_(pPermissions, iAccountID);

            return pPermission;
         }

         // Locate parent folder.
         __int64 iParentFolderID = pCheckFolder->GetParentFolderID();

         if (!pOwnerTree)
            break;

         pCheckFolder = pOwnerTree->GetItemByDBIDRecursive(iParentFolderID);
      }

      std::shared_ptr<ACLPermission> pNoPermission;
      return pNoPermission;

   }
   
   std::shared_ptr<ACLPermission> 
   ACLManager::GetPermissionForAccount_(std::shared_ptr<ACLPermissions> pPermissions, __int64 iAccountID)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Goes through the list of permissions (typically a list of permissions connected
   // to a specific IMAP folder) and returns the "best" permission which matches
   // the account ID. If the user has been given specific rights, these are used.
   // If not, we check if the user is a member of a group and if so uses the
   // rights for this group.
   // If not, we check whether "anyone" has been given permission to the folder. In that
   // case, we use those.
   //---------------------------------------------------------------------------()
   {
      std::vector<std::shared_ptr<ACLPermission> > vecObjects = pPermissions->GetVector();

      // Sort the list of permissions. If a user is a member of two groups set up in
      // separate ACL records, we should use the permissions from the last record.
      std::sort(vecObjects.begin(), vecObjects.end(), ACLSortID());

      auto iter = vecObjects.begin();
      auto iterEnd = vecObjects.end();

      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<ACLPermission> pPermission = (*iter);

         if (pPermission->GetPermissionType() == 0 && pPermission->GetPermissionAccountID() == iAccountID)
            return pPermission;
      }

      // Check which groups have been given access.
      iter = vecObjects.begin();
      iterEnd = vecObjects.end();

      std::list<std::pair<__int64, std::shared_ptr<ACLPermission> > > listGroupPermissions;
      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<ACLPermission> pPermission = (*iter);

         if (pPermission->GetPermissionType() == 1)
         {
            listGroupPermissions.push_back(std::make_pair(pPermission->GetPermissionGroupID(), pPermission));
         }
      }

      // Check if user is member of any of these groups.
      auto iterGroup = listGroupPermissions.begin();
      auto iterGroupEnd = listGroupPermissions.end();

      for (; iterGroup != iterGroupEnd; iterGroup++)
      {
         __int64 iGroupID = (*iterGroup).first;
         std::shared_ptr<ACLPermission> pPermission = (*iterGroup).second;

         // Fetch this group
         std::shared_ptr<Group> pGroup = Configuration::Instance()->GetIMAPConfiguration()->GetGroups()->GetItemByDBID(iGroupID);

         if (!pGroup)
         {
            String sMessage;
            sMessage.Format(_T("The group referenced by ACL ID %I64d (Group ID %I64d, Folder ID %I64d) does not exist. "), 
               pPermission->GetID(), iGroupID, pPermission->GetShareFolderID());

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5002, "ACLManager::GetPermissionForAccount_", sMessage);

            continue;
         }

         // Check if the user is a member of this group
         if (pGroup->UserIsMember(iAccountID))
            return pPermission;
      }

      // Check if anyone has been given permission to this group
      iter = vecObjects.begin();
      iterEnd = vecObjects.end();

      for (; iter != iterEnd; iter++)
      {
         std::shared_ptr<ACLPermission> pPermission = (*iter);

         if (pPermission->GetPermissionType() == ACLPermission::PTAnyone)
            return pPermission;
      }


      std::shared_ptr<ACLPermission> pEmpty;
      return pEmpty;
   }

   bool 
   ACLManager::SetACL(std::shared_ptr<IMAPFolder> pFolder, const String& sIdentifier, const String &sPermissions)
   {
      std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(sIdentifier);
      std::shared_ptr<Group> pGroup;

      if (!pAccount)
      {
         // No account was found. Check if it's a group.
         //
         // Assigns the pGroup declared above rather than declaring another one. It used
         // to read "std::shared_ptr<Group> pGroup = ...", which shadowed the outer
         // variable: the group was found and null-checked here, then went out of scope
         // at the end of this block, leaving the outer pGroup still empty. Line 248 then
         // called pGroup->GetID() on it, so SETACL naming a *group* - an ordinary thing
         // to do on a public folder - dereferenced a null shared_ptr and took the
         // session down.
         pGroup = Configuration::Instance()->GetIMAPConfiguration()->GetGroups()->GetItemByName(sIdentifier);

         if (!pGroup)
         {
            // Identifier was not found.
            return false;
         }
      }

      if (pAccount && pAccount->GetID() == pFolder->GetAccountID())
      {
         // The owner of a folder holds every right on it implicitly
         // (GetPermissionForFolder answers GrantAll before it reads a single
         // row), so a stored row for the owner could only ever disagree with
         // the truth. Refused, quietly: this stopped being an assert the day
         // account folders could be named in a SETACL at all, because a branch
         // any authenticated client can drive is not a programming error.
         return false;
      }

      // Read through the manager, not IMAPFolder::GetPermissions() - that
      // returns an EMPTY, unloaded collection for account folders, so an
      // existing grant would never be found here and every SETACL would insert
      // a fresh hm_acl row next to the one it should have updated.
      std::shared_ptr<ACLPermissions> pFolderPermissions = GetPermissionsSetOnFolder(pFolder);
      
      std::shared_ptr<ACLPermission> pPermission;
      
      if (pAccount)
         pPermission = pFolderPermissions->GetPermissionForAccount(pAccount->GetID());
      else
         pPermission = pFolderPermissions->GetPermissionForGroup(pGroup->GetID());

      if (!pPermission)
      {
         pPermission = std::shared_ptr<ACLPermission>(new ACLPermission);

         pPermission->SetShareFolderID(pFolder->GetID());

         if (pAccount)
         {
            pPermission->SetPermissionType(ACLPermission::PTUser);
            pPermission->SetPermissionAccountID(pAccount->GetID());
         }
         else if (pGroup)
         {
            pPermission->SetPermissionType(ACLPermission::PTGroup);
            pPermission->SetPermissionGroupID(pGroup->GetID());
         }
         else
            return false;

      }

      pPermission->AppendPermissions(sPermissions);

      if (!PersistentACLPermission::SaveObject(pPermission))
         return false;
      
      return true;
   }


  


}