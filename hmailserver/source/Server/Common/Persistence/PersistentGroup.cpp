// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "PersistentGroup.h"
#include "PersistentACLPermission.h"

#include "PreSaveLimitationsCheck.h"

#include "..\BO\Group.h"
#include "..\SQL\SQLStatement.h"
#include "../Cache/Cache.h"

#include "PersistenceMode.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentGroup::PersistentGroup(void)
   {
   }

   PersistentGroup::~PersistentGroup(void)
   {
   }

   bool
   PersistentGroup::DeleteObject(std::shared_ptr<Group> pObject)
   {
      SQLCommand command("delete from hm_groups where groupid = @GROUPID");
      command.AddParameter("@GROUPID", pObject->GetID());

      if (!Application::Instance()->GetDBManager()->Execute(command))
      {
         // The group is still there, so leave its memberships and its ACL grants
         // alone. Stripping the members and the permissions off a group that still
         // exists is a worse outcome than the delete that did not happen, and that
         // is what the unconditional cleanup below used to do.
         return false;
      }

      // The membership rows have to go with the group. There is no foreign key
      // anywhere in the schema, so nothing else was ever going to remove them:
      // before this, deleting a group left one hm_group_members row per member
      // naming a group id that no longer exists, for the life of the database.
      // They are dead weight while the id stays unused, and worse than that if it
      // is ever handed out again - an identity counter reseeded by a restore, or a
      // backend that reuses ids, would silently give a brand new group the old
      // group's members, and a group is an ACL principal.
      //
      // Written here rather than as PersistentGroupMember::DeleteByGroup, which is
      // where it belongs, only because that would need a change to a header this
      // change set does not own.
      SQLCommand deleteMembersCommand("delete from hm_group_members where membergroupid = @GROUPID");
      deleteMembersCommand.AddParameter("@GROUPID", pObject->GetID());

      if (!Application::Instance()->GetDBManager()->Execute(deleteMembersCommand))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5982, "PersistentGroup::DeleteObject",
            Formatter::Format("Group {0} was deleted but its membership rows could not be removed. hm_group_members now holds rows for group id {1}, which no longer exists.", pObject->GetName(), pObject->GetID()));
      }

      PersistentACLPermission::DeleteOwnedByGroup(pObject->GetID());

      return true;
   }

   bool 
   PersistentGroup::ReadObject(std::shared_ptr<Group> pObject, std::shared_ptr<DALRecordset> pRS)
   {
      pObject->SetID(pRS->GetInt64Value("groupid"));
      pObject->SetName(pRS->GetStringValue("groupname"));

      return true;
   }

   bool
   PersistentGroup::ReadObject(std::shared_ptr<Group> pGroup, const String & sName)
   {
      SQLStatement statement;

      statement.SetStatementType(SQLStatement::STSelect);
      statement.SetTable("hm_groups");
      statement.AddWhereClauseColumn("groupname", sName);

      return ReadObject(pGroup, statement.GetCommand());
   }

   bool
   PersistentGroup::ReadObject(std::shared_ptr<Group> pGroup, __int64 ObjectID)
   {
      SQLCommand command("select * from hm_groups where groupid = @GROUPID");
      command.AddParameter("@GROUPID", ObjectID);

      return ReadObject(pGroup, command);

   }

   bool
   PersistentGroup::ReadObject(std::shared_ptr<Group> pGroup, const SQLCommand &command)
   {
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      bool bRetVal = false;
      if (!pRS->IsEOF())
      {
         bRetVal = ReadObject(pGroup, pRS);
      }

      return bRetVal;
   }

   bool 
   PersistentGroup::SaveObject(std::shared_ptr<Group> pGroup)
   {
      String sErrorMessage;
      return SaveObject(pGroup, sErrorMessage, PersistenceModeNormal);
   }

   bool 
   PersistentGroup::SaveObject(std::shared_ptr<Group> pGroup, String &sErrorMessage, PersistenceMode mode)
   {
      if (!PreSaveLimitationsCheck::CheckLimitations(mode, pGroup, sErrorMessage))
         return false;

      SQLStatement oStatement;

      oStatement.AddColumn("groupname", pGroup->GetName());

      oStatement.SetTable("hm_groups");


      if (pGroup->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("groupid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);

         String sWhere;
         sWhere.Format(_T("groupid = %I64d"), pGroup->GetID());

         oStatement.SetWhereClause(sWhere);

      }

      bool bNewObject = pGroup->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pGroup->SetID((long) iDBID);

      return bRetVal;      
   }
}