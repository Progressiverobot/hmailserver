// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "IMAPACLHelper.h"

#include "../Common/Application/ACLManager.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/ACLPermissions.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/Cache/CacheContainer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPACLHelper::IMAPACLHelper()
   {

   }

   IMAPACLHelper::~IMAPACLHelper()
   {

   }

   String
   IMAPACLHelper::CreateACLList(std::shared_ptr<IMAPFolder> pFolder, const String &sEscapedFolderName)
   {
      // Retrieve a list of all rights for this folder - through ACLManager,
      // because IMAPFolder::GetPermissions() returns an empty, unloaded
      // collection for account folders, and a GETACL that lists nothing for a
      // folder with stored grants is worse than an error: it reads as "nobody
      // has been granted anything".
      ACLManager aclManager;
      std::shared_ptr<ACLPermissions> pPermissions = aclManager.GetPermissionsSetOnFolder(pFolder);

      String sACLList = "* ACL \"" + sEscapedFolderName + "\"";
      for (int i = 0; i < pPermissions->GetCount(); i++)
      {
         std::shared_ptr<ACLPermission> pPermission = pPermissions->GetItem(i);

         // Determine the name of this account
         __int64 iAccountID = pPermission->GetPermissionAccountID();

         std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(iAccountID);

         // Null-checked, which it was not. A permission row is not necessarily a user
         // row: ACLPermission has three types - PTUser, PTGroup and PTAnyone - and for
         // the latter two permission_account_id_ is 0, so GetAccount(0) hands back an
         // empty shared_ptr and this dereferenced it.
         //
         // Granting a *group* rights on a public folder is supported and ordinary, so
         // one GETACL against such a folder was an access violation. It does not go
         // unnoticed - the server builds with /EHa, so the catch(...) in
         // TCPConnection::AsyncReadCompleted swallows it, logs 5136, drops the session
         // and rethrows for a minidump - but a client could take a session down at
         // will. IMAPCommandDeleteAcl null-checks the same call.
         //
         // Skipped rather than named: identifying group and "anyone" rows in the ACL
         // response needs the RFC 4314 identifier forms, which is a feature rather than
         // a crash fix, and listing them wrongly would be worse than omitting them.
         if (!pAccount)
            continue;

         String sIdentifier = pAccount->GetAddress();
         String sRights = pPermission->GetRights();

         sACLList += " " + sIdentifier + " " + sRights;
      }

      return sACLList + "\r\n";
   }

   bool 
   IMAPACLHelper::IsValidPermissionString(const String &sPermissions)
   {
      for (int i = 0; i < sPermissions.GetLength(); i++)
      {
         wchar_t w = sPermissions.GetAt(i);

         if (w == '+' || w == '-')
            continue;

         if (ACLPermission::GetPermission(w) == ACLPermission::PermissionNone)
            return false;
      }

      return true;
   }




}
