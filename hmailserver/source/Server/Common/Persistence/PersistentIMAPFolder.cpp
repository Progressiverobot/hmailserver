// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "PersistentIMAPFolder.h"
#include "PersistentACLPermission.h"

#include "../BO/ACLPermissions.h"
#include "../BO/IMAPFolders.h"
#include "..\BO\IMAPFolder.h"

#include "..\..\IMAP\IMAPFolderContainer.h"
#include "..\..\IMAP\MessagesContainer.h"

#include "..\Tracking\ChangeNotification.h"
#include "..\Tracking\NotificationServer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentIMAPFolder::PersistentIMAPFolder()
   {

   }

   PersistentIMAPFolder::~PersistentIMAPFolder()
   {

   }

   bool
   PersistentIMAPFolder::DeleteByAccount(__int64 iAccountID)
   {
      if (iAccountID <= 0)
         return false;

      IMAPFolders accountFolders (iAccountID, -1);
      accountFolders.Refresh();
      return accountFolders.DeleteAll();
   }

   bool
   PersistentIMAPFolder::DeleteObject(std::shared_ptr<IMAPFolder> pFolder)
   {
      return DeleteObject  (pFolder, false);
   }

   /*
      Deletes a specific IMAP folder.

      If forceDelete is false, the user Inbox won't be deleted.

   */
   bool
   PersistentIMAPFolder::DeleteObject(std::shared_ptr<IMAPFolder> pFolder, bool forceDelete)
   {
      if (pFolder->GetID() <= 0)
         return false;
      
      // Delete sub folders first...
      if (!pFolder->GetSubFolders()->DeleteAll())
         return false;

      // We must delete all email in this folder.
      pFolder->GetMessages()->Refresh(false);

      std::function<bool(int, std::shared_ptr<Message>)> filter = [](int index, std::shared_ptr<Message> message)
         {
            return true;
         };

      auto messages = MessagesContainer::Instance()->GetMessages(pFolder->GetAccountID(), pFolder->GetID());
      messages->DeleteMessages(filter);
            
      if (!pFolder->GetPermissions()->DeleteAll())
         return false;

      bool isInbox = pFolder->GetParentFolderID() == -1 && pFolder->GetFolderName().CompareNoCase(_T("Inbox")) == 0;
      bool deleteActualFolder = forceDelete || !isInbox;

      if (deleteActualFolder)
      {
         SQLCommand command("delete from hm_imapfolders where folderid = @FOLDERID");
         command.AddParameter("@FOLDERID", pFolder->GetID());

         bool result = Application::Instance()->GetDBManager()->Execute(command);

         // RFC 7162 (QRESYNC): the folder is gone, so its expunge tombstones are no longer
         // meaningful (a recreated folder gets a new UIDVALIDITY). Discard them.
         //
         // Checked, which it was not. The tombstones are what a QRESYNC client is given
         // to reconcile a mailbox it has not seen for a while, so a table of them
         // belonging to folders that no longer exist is not merely untidy - it grows
         // every time a folder is deleted and is never read or cleaned again.
         if (!DeleteExpungedForFolder(pFolder->GetID()))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6117, "PersistentIMAPFolder::DeleteObject",
               Formatter::Format("The QRESYNC expunge records for deleted folder {0} could not be removed and have been left behind as orphan rows.",
                  pFolder->GetID()));
         }

         return result;
      }
      else
         return true;
   }

   bool
   PersistentIMAPFolder::SaveObject(std::shared_ptr<IMAPFolder> pFolder, String &errorMessage, PersistenceMode mode)
   {
      // errorMessage not supported yet.
      return SaveObject(pFolder);
   }

   bool
   PersistentIMAPFolder::SaveObject(std::shared_ptr<IMAPFolder> pFolder)
   {
      bool bNewObject = true;
      if (pFolder->GetID())
         bNewObject = false;
      
      SQLStatement oStatement;
      
      oStatement.SetTable("hm_imapfolders");
      
      if (bNewObject)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("folderid");

         DateTime creationTime = pFolder->GetCreationTime();
         if (pFolder->GetCreationTime().GetStatus() == DateTime::invalid)
            pFolder->SetCreationTime(DateTime::GetCurrentTime());

         // This column is always updated by GetUniqueMessageID below
         // but we still need to create it.
         oStatement.AddColumn("foldercurrentuid", pFolder->GetCurrentUID());
         oStatement.AddColumnInt64("foldercurrentmodseq", pFolder->GetCurrentModSeq());
         oStatement.AddColumnDate("foldercreationtime", pFolder->GetCreationTime());

         // RFC 6154 (SPECIAL-USE), on the insert path only, and the asymmetry is
         // deliberate.
         //
         // It has to be here at all because a backup restore inserts folders through
         // Collection::XMLLoad, and a restore that dropped the designation would leave
         // the user's client guessing again in the one mailbox where guessing does not
         // work. A CREATE always inserts zero here; the designation is written
         // afterwards by IMAPFolder::StoreSpecialUseFlags.
         //
         // It must NOT be in the update column list below. Every caller that saves an
         // existing folder - RENAME, the COM Subscribed setter, the folder collection -
         // would then write back whatever designation its cached copy happens to hold,
         // so a stale object from before a CREATE ... USE would silently clear the
         // attribute. Leaving the column out of the UPDATE means the only writer of a
         // live row is the single-column statement in StoreSpecialUseFlags, which is
         // also the only code that has been asked to change it.
         oStatement.AddColumn("folderspecialuse", (long) pFolder->GetSpecialUseFlags());
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);

         String sWhere;
         sWhere.Format(_T("folderid = %I64d"), pFolder->GetID());

         oStatement.SetWhereClause(sWhere);
      }

      oStatement.AddColumnInt64("folderaccountid", pFolder->GetAccountID());
      oStatement.AddColumnInt64("folderparentid", pFolder->GetParentFolderID());
      oStatement.AddColumn("foldername", pFolder->GetFolderName());
      oStatement.AddColumn("folderissubscribed", pFolder->GetIsSubscribed() ? 1 : 0);

      
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pFolder->SetID((int) iDBID);

      // Report the database result. Returning true unconditionally cached a
      // folder with id 0 after a failed insert, and every message filed into it
      // was then written to disk with no row to find it by.
      return bRetVal;
   }

   __int64 
   PersistentIMAPFolder::GetUserInboxFolder(__int64 accountID)
   {
      SQLCommand command("SELECT folderid FROM hm_imapfolders WHERE folderaccountid = @FOLDERACCOUNTID and folderparentid = -1 and foldername = 'INBOX'");
      command.AddParameter("@FOLDERACCOUNTID", accountID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
      {
         String message;
         message.Format(_T("The inbox for account %I64d could not be looked up"), accountID);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5206, "PersistentIMAPFolder::GetUserInboxFolder", message);
         return 0;
      }

      __int64 folderID = pRS->GetInt64Value("folderid");

      return folderID;
   }


   bool 
   PersistentIMAPFolder::GetExistsFolderContainingCharacter(String theChar)
   {
      theChar = SQLStatement::Escape(theChar);

      SQLCommand command(_T("select count(*) as c from hm_imapfolders where foldername like '%" + theChar + "%'"));

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      long count = pRS->GetLongValue("c");

      return count > 0;
   }

   unsigned int 
   PersistentIMAPFolder::GetCurrentUID_(__int64 folderID)
   {
      if (folderID == 0)
         return 0;

      SQLCommand command("SELECT foldercurrentuid FROM hm_imapfolders WHERE folderid = @FOLDERID");
      command.AddParameter("@FOLDERID", folderID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
      {
         String message;
         message.Format(_T("Current UID for folder %I64d could not be looked up"), folderID);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5207, "PersistentIMAPFolder::GetCurrentUID_", message);

         return 0;
      }

      if (pRS->IsEOF())
      {
         String message;
         message.Format(_T("Current UID for folder %I64d could not be looked up. Folder does not eixst."), folderID);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5207, "PersistentIMAPFolder::GetCurrentUID_", message);

         return 0;
      }

      unsigned int lastUID = (unsigned int) pRS->GetInt64Value("foldercurrentuid");

      return lastUID;
   }

   bool 
   PersistentIMAPFolder::IncreaseCurrentUID_(__int64 folderID)
   {
      SQLCommand command("UPDATE hm_imapfolders SET foldercurrentuid = foldercurrentuid + 1 WHERE folderid = @FOLDERID");
      command.AddParameter("@FOLDERID", folderID);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   unsigned int 
   PersistentIMAPFolder::GetUniqueMessageID(__int64 accountID, __int64 folderID)
   {
      if (folderID == 0)
         return 0;

      // The increase was unchecked, and the line below reads the counter back - so when
      // the UPDATE failed, GetCurrentUID_ returned the value it had before and this
      // function handed out the SAME UID it handed out last time, from a function whose
      // name is GetUniqueMessageID.
      //
      // RFC 3501 requires UIDs to be unique and strictly ascending within a mailbox,
      // and clients rely on it completely: a client that has cached UID N and is then
      // offered a different message with UID N will not fetch it, because as far as it
      // is concerned it already has it. The message is delivered, visible on the server,
      // and invisible in the client - which is the worst shape a mail bug can take,
      // because nothing anywhere looks wrong.
      //
      // Zero is already this function's "no UID" answer, and PersistentMessage::AddObject
      // already handles it properly: it reports HM5205 and refuses to store the message.
      // So the fix is to be honest here and let the machinery that exists do its job.
      if (!IncreaseCurrentUID_(folderID))
         return 0;

      unsigned int newUID = GetCurrentUID_(folderID);

      IMAPFolderContainer::Instance()->UpdateCurrentUID(accountID, folderID, newUID);

      return newUID;
   }

   bool 
   PersistentIMAPFolder::IncreaseCurrentModSeq_(__int64 folderID)
   {
      SQLCommand command("UPDATE hm_imapfolders SET foldercurrentmodseq = foldercurrentmodseq + 1 WHERE folderid = @FOLDERID");
      command.AddParameter("@FOLDERID", folderID);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   __int64
   PersistentIMAPFolder::GetCurrentModSeq_(__int64 folderID)
   {
      if (folderID == 0)
         return 0;

      SQLCommand command("SELECT foldercurrentmodseq FROM hm_imapfolders WHERE folderid = @FOLDERID");
      command.AddParameter("@FOLDERID", folderID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS || pRS->IsEOF())
         return 0;

      return pRS->GetInt64Value("foldercurrentmodseq");
   }

   __int64
   PersistentIMAPFolder::GetNextModSeq(__int64 accountID, __int64 folderID)
   {
      if (folderID == 0)
         return 0;

      // RFC 7162: per-mailbox mod-sequence values must increase strictly and never
      // decrease (even across expunges), so we use a monotonic per-folder counter
      // mirroring the existing UID counter.
      //
      // Which is exactly what a discarded result here broke: the counter did not move,
      // GetCurrentModSeq_ read back the old value, and this handed out a mod-sequence
      // a client may already have synced past - so the change it marks is one the
      // client will never ask for again.
      //
      // Zero rather than the stale value, because both callers already write
      // "if (newModSeq > 0)" and keep the message's previous mod-sequence otherwise.
      // No error record: the DAL has already reported the SQL failure, and one report
      // per message during a database outage would bury it.
      if (!IncreaseCurrentModSeq_(folderID))
         return 0;

      __int64 newModSeq = GetCurrentModSeq_(folderID);

      IMAPFolderContainer::Instance()->UpdateCurrentModSeq(accountID, folderID, newModSeq);

      return newModSeq;
   }

   bool
   PersistentIMAPFolder::AddExpunged(__int64 accountID, __int64 folderID, __int64 uid, __int64 modSeq)
   {
      if (folderID == 0)
         return false;

      SQLCommand command("INSERT INTO hm_imapexpunged (expungedaccountid, expungedfolderid, expungeduid, expungedmodseq) "
                         "VALUES (@ACCOUNTID, @FOLDERID, @UID, @MODSEQ)");
      command.AddParameter("@ACCOUNTID", accountID);
      command.AddParameter("@FOLDERID", folderID);
      command.AddParameter("@UID", uid);
      command.AddParameter("@MODSEQ", modSeq);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   std::vector<__int64>
   PersistentIMAPFolder::GetExpungedUIDsSince(__int64 folderID, __int64 sinceModSeq)
   {
      std::vector<__int64> result;

      if (folderID == 0)
         return result;

      SQLCommand command("SELECT expungeduid FROM hm_imapexpunged WHERE expungedfolderid = @FOLDERID "
                         "AND expungedmodseq > @MODSEQ ORDER BY expungeduid");
      command.AddParameter("@FOLDERID", folderID);
      command.AddParameter("@MODSEQ", sinceModSeq);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return result;

      while (!pRS->IsEOF())
      {
         result.push_back(pRS->GetInt64Value("expungeduid"));
         pRS->MoveNext();
      }

      return result;
   }

   bool
   PersistentIMAPFolder::DeleteExpungedForFolder(__int64 folderID)
   {
      if (folderID == 0)
         return false;

      SQLCommand command("DELETE FROM hm_imapexpunged WHERE expungedfolderid = @FOLDERID");
      command.AddParameter("@FOLDERID", folderID);

      return Application::Instance()->GetDBManager()->Execute(command);
   }
}