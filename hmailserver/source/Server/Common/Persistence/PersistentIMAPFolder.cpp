// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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
      return DeleteByAccount(iAccountID, false);
   }

   bool
   PersistentIMAPFolder::DeleteByAccount(__int64 iAccountID, bool forceDelete)
   {
      if (iAccountID <= 0)
         return false;

      IMAPFolders accountFolders (iAccountID, -1);
      accountFolders.Refresh();

      // Walked explicitly rather than through Collection::DeleteAll, which knows
      // nothing of forceDelete and would keep the inbox even when the account itself
      // is going. This local tree is discarded afterwards, so what the walk leaves in
      // it does not matter; the callers uncache the account's folders.
      std::vector<std::shared_ptr<IMAPFolder> > &folders = accountFolders.GetVector();
      for (std::shared_ptr<IMAPFolder> folder : folders)
      {
         bool kept = false;
         if (!DeleteObject_(folder, forceDelete, !forceDelete, kept))
            return false;
      }

      return true;
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
      bool kept = false;
      return DeleteObject_(pFolder, forceDelete, false, kept);
   }

   bool
   PersistentIMAPFolder::DeleteObject_(std::shared_ptr<IMAPFolder> pFolder, bool forceDelete, bool keepSpecialUse, bool &kept)
   {
      kept = false;

      if (pFolder->GetID() <= 0)
         return false;

      // Delete sub folders first. Walked here rather than through the collection's
      // DeleteAll so that forceDelete and keepSpecialUse reach nested folders, and so
      // that a kept subfolder can keep its parent: a designated folder two levels
      // down whose parent row was deleted would be an orphan nothing could reach.
      bool subfolderKept = false;
      std::vector<std::shared_ptr<IMAPFolder> > &subFolders = pFolder->GetSubFolders()->GetVector();
      for (std::shared_ptr<IMAPFolder> subFolder : subFolders)
      {
         bool thisSubfolderKept = false;
         if (!DeleteObject_(subFolder, forceDelete, keepSpecialUse, thisSubfolderKept))
            return false;

         subfolderKept = subfolderKept || thisSubfolderKept;
      }

      // We must delete all email in this folder.
      pFolder->GetMessages()->Refresh(false);

      std::function<bool(int, std::shared_ptr<Message>)> filter = [](int index, std::shared_ptr<Message> message)
         {
            return true;
         };

      auto messages = MessagesContainer::Instance()->GetMessages(pFolder->GetAccountID(), pFolder->GetID());
      messages->DeleteMessages(filter);

      // The folder's stored ACL rows, loaded explicitly. IMAPFolder::
      // GetPermissions() returns an UNLOADED collection for account folders
      // (its comment says account folders never have permissions set, which
      // stopped being true when one user's folder became shareable with
      // another), so this DeleteAll iterated an empty vector and every deleted
      // shared folder left its hm_acl grants behind as orphan rows.
      std::shared_ptr<ACLPermissions> pFolderAcl = std::shared_ptr<ACLPermissions>(new ACLPermissions(pFolder->GetID()));
      pFolderAcl->Refresh();

      if (!pFolderAcl->DeleteAll())
         return false;

      bool isInbox = pFolder->GetParentFolderID() == -1 && pFolder->GetFolderName().CompareNoCase(_T("Inbox")) == 0;
      bool isDesignated = keepSpecialUse && pFolder->GetSpecialUseFlags() != 0;
      bool deleteActualFolder = forceDelete || !(isInbox || isDesignated || subfolderKept);

      kept = !deleteActualFolder;

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
   PersistentIMAPFolder::RemembersExpungesSince(__int64 folderID, __int64 sinceModSeq)
   {
      if (folderID == 0)
         return true;

      SQLCommand command("SELECT COUNT(*) AS expungedcount, MIN(expungedmodseq) AS lowestmodseq "
                         "FROM hm_imapexpunged WHERE expungedfolderid = @FOLDERID");
      command.AddParameter("@FOLDERID", folderID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);

      // A table that could not be read is not a table that has pruned anything.
      // Answering "no" to a transient database failure would put every QRESYNC
      // client on the complete-list path - a VANISHED response naming every gap in
      // the mailbox, on every SELECT - for as long as the failure lasted.
      if (!pRS || pRS->IsEOF())
         return true;

      // The count is read first because MIN over no rows is NULL, and how a NULL
      // reads back through this DAL differs by backend. With no rows there is
      // nothing to compare against anyway.
      if (pRS->GetInt64Value("expungedcount") == 0)
         return true;

      __int64 lowestRemembered = pRS->GetInt64Value("lowestmodseq");

      // RFC 7162 section 3.2.6: <minmodseq> is the smallest remembered expunged
      // mod-sequence minus one, and a client at or above it can still be answered
      // exactly - every record it needs has a higher mod-sequence than that, and
      // every record with a higher mod-sequence is still here.
      return sinceModSeq >= lowestRemembered - 1;
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

   std::vector<std::pair<__int64, __int64>>
   PersistentIMAPFolder::GetExpungedRecordCounts()
   {
      std::vector<std::pair<__int64, __int64>> result;

      SQLCommand command("SELECT expungedfolderid, COUNT(*) AS expungedcount FROM hm_imapexpunged "
                         "GROUP BY expungedfolderid");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);

      if (!pRS)
         return result;

      while (!pRS->IsEOF())
      {
         __int64 folderID = pRS->GetInt64Value("expungedfolderid");
         __int64 recordCount = pRS->GetInt64Value("expungedcount");

         if (folderID > 0)
            result.push_back(std::make_pair(folderID, recordCount));

         pRS->MoveNext();
      }

      return result;
   }

   __int64
   PersistentIMAPFolder::PruneExpungedForFolder(__int64 folderID, __int64 recordCount, int keepRecords, int batchRecords)
   {
      if (folderID <= 0 || keepRecords <= 0 || batchRecords <= 0)
         return 0;

      if (recordCount <= (__int64) keepRecords)
         return 0;

      SQLCommand boundsCommand("SELECT COUNT(*) AS expungedcount, MIN(expungedmodseq) AS lowestmodseq, "
                               "MAX(expungedmodseq) AS highestmodseq FROM hm_imapexpunged "
                               "WHERE expungedfolderid = @FOLDERID");
      boundsCommand.AddParameter("@FOLDERID", folderID);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(boundsCommand);

      if (!pRS || pRS->IsEOF())
         return 0;

      // Re-read rather than trusted from the caller's GROUP BY: that snapshot was
      // taken before the sweep started working through the other folders, and the
      // mailbox has been live throughout.
      __int64 total = pRS->GetInt64Value("expungedcount");

      if (total <= (__int64) keepRecords)
         return 0;

      __int64 lowest = pRS->GetInt64Value("lowestmodseq");
      __int64 highest = pRS->GetInt64Value("highestmodseq");

      if (highest <= lowest)
         return 0;

      /*
         Any cutoff at all is CORRECT here; only the size of the surviving queue
         depends on getting it close. That is what makes an interpolated estimate
         acceptable, and no portable "delete all but the newest N rows" exists across
         these four backends anyway - window functions, DELETE ... LIMIT and OFFSET
         are each missing from at least one of them.

         Correct, because of what the reader does with what is left:
         RemembersExpungesSince derives RFC 7162's <minmodseq> from the smallest
         mod-sequence still in the table. Delete more and that boundary simply moves
         up, and a client asking about anything older is told to expect the complete
         list of missing UIDs rather than a short one. There is no cutoff that makes
         a client believe a deleted message is still there.
      */
      __int64 toDelete = total - (__int64) keepRecords;
      __int64 span = highest - lowest + 1;

      // Done in floating point so a wide, sparse mod-sequence range cannot overflow
      // the multiplication. The precision that costs is beside the point: this is an
      // estimate whichever way it is computed.
      __int64 cutoff = lowest + (__int64) ((double) span * (double) toDelete / (double) total);

      /*
         Never the newest record.

         Keeping it is what lets RemembersExpungesSince tell "nothing was ever
         expunged from this folder" (no rows) apart from "everything I knew about has
         been pruned" (rows, but none as old as the client is asking about). It is
         also RFC 7162 section 5.3's own instruction for a queue that has been
         trimmed: "For all such 'expired' records, the server needs to store a single
         mod-sequence, which is the highest mod-sequence for all 'expired' expunged
         messages."
      */
      if (cutoff > highest)
         cutoff = highest;

      if (cutoff <= lowest)
         return 0;

      /*
         The estimate assumed the mod-sequences were spread evenly between the oldest
         and the newest. They are not - a folder's mod-sequence also advances on flag
         changes, so a burst of expunges among quiet months is dense and the months
         either side are empty - so the estimate can land too high and leave fewer
         than keepRecords behind. That costs QRESYNC precision for no benefit, so it
         is walked back down until enough survive.

         Landing too LOW needs no correction: less is deleted than intended and the
         next run carries on from there.
      */
      for (int attempt = 0; attempt < 4 && cutoff > lowest; attempt++)
      {
         SQLCommand survivorCommand("SELECT COUNT(*) AS expungedcount FROM hm_imapexpunged "
                                    "WHERE expungedfolderid = @FOLDERID AND expungedmodseq >= @CUTOFF");
         survivorCommand.AddParameter("@FOLDERID", folderID);
         survivorCommand.AddParameter("@CUTOFF", cutoff);

         std::shared_ptr<DALRecordset> pSurvivors = Application::Instance()->GetDBManager()->OpenRecordset(survivorCommand);

         if (!pSurvivors || pSurvivors->IsEOF())
            return 0;

         if (pSurvivors->GetInt64Value("expungedcount") >= (__int64) keepRecords)
            break;

         cutoff -= ((cutoff - lowest) / 2 + 1);
      }

      if (cutoff <= lowest)
         return 0;

      /*
         Deleted in slices rather than in one statement, and the reason is the other
         half of this change: DatabaseStatementTimeout is now real on PostgreSQL and
         MySQL, so a single DELETE over a table that has been growing since the
         installation was built would be aborted at thirty seconds having achieved
         nothing - every run, forever. Short statements also mean short locks on a
         server that is still delivering mail.

         The slice is a range of mod-sequence VALUES rather than a row count, because
         no row-limited DELETE is portable across these four backends. It is scaled
         by the density observed above so that a slice holds roughly batchRecords
         rows; where the values turn out to be sparser it simply removes fewer, which
         costs a round trip and nothing else. It can never hold MORE than it is units
         wide, because mod-sequences are unique within a folder - GetNextModSeq
         issues a fresh one per expunged message.
      */
      double density = (double) total / (double) span;
      __int64 sliceWidth = (density > 0.0) ? (__int64) ((double) batchRecords / density) : (__int64) batchRecords;

      if (sliceWidth < 1)
         sliceWidth = 1;

      // Cannot be wider than the whole range, which also keeps the addition below
      // away from the end of the type.
      if (sliceWidth > span)
         sliceWidth = span;

      // A ceiling on what one folder may do in one run, so that a single enormous
      // mailbox cannot hold up the rest of the sweep. What is left is picked up on
      // the next run.
      const int max_batches = 64;

      __int64 boundary = lowest;

      for (int batch = 0; batch < max_batches && boundary < cutoff; batch++)
      {
         boundary += sliceWidth;

         if (boundary > cutoff)
            boundary = cutoff;

         SQLCommand deleteCommand("DELETE FROM hm_imapexpunged WHERE expungedfolderid = @FOLDERID "
                                  "AND expungedmodseq < @BOUNDARY");
         deleteCommand.AddParameter("@FOLDERID", folderID);
         deleteCommand.AddParameter("@BOUNDARY", boundary);

         // A failed slice stops this folder rather than the sweep. The slices are
         // cumulative ("everything below this boundary"), so a run that stops part
         // way has removed a prefix and nothing is half-deleted.
         if (!Application::Instance()->GetDBManager()->Execute(deleteCommand))
            break;
      }

      // Counted afterwards rather than tracked, because none of the four backends
      // returns a row count through this DAL. One aggregate over an indexed column
      // is cheaper than counting before every slice.
      SQLCommand remainingCommand("SELECT COUNT(*) AS expungedcount FROM hm_imapexpunged "
                                  "WHERE expungedfolderid = @FOLDERID");
      remainingCommand.AddParameter("@FOLDERID", folderID);

      std::shared_ptr<DALRecordset> pRemaining = Application::Instance()->GetDBManager()->OpenRecordset(remainingCommand);

      if (!pRemaining || pRemaining->IsEOF())
         return 0;

      __int64 remaining = pRemaining->GetInt64Value("expungedcount");

      return total > remaining ? total - remaining : 0;
   }

   __int64
   PersistentIMAPFolder::DeleteOrphanedExpunged(const std::vector<std::pair<__int64, __int64>> &recordCounts)
   {
      /*
         Tombstones whose folder is gone.

         They exist because DeleteExpungedForFolder can fail - HM6117 reports exactly
         that, in as many words, and says the rows have been left behind - and
         because until now nothing ever came back for them. Nothing can read one
         either: GetExpungedUIDsSince is only ever called with the id of a folder a
         session currently has selected.

         Deliberately NOT written as "delete ... where not exists (select ...)",
         which is how PersistentMessageMetaData::DeleteOrphanedItems does the
         equivalent job. That statement only runs when message indexing is switched
         on, so it has never been exercised against all four backends here, and this
         one runs on every server start: a correlated subquery that one backend
         rejects would put a database error in the log of every installation. Two
         statements of a shape this file already uses cost more round trips and
         cannot be wrong.
      */
      if (recordCounts.empty())
         return 0;

      std::set<__int64> existingFolders;

      SQLCommand folderCommand("SELECT folderid FROM hm_imapfolders");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(folderCommand);

      if (!pRS)
         return 0;

      while (!pRS->IsEOF())
      {
         existingFolders.insert(pRS->GetInt64Value("folderid"));
         pRS->MoveNext();
      }

      // A folder table that reads back empty on a server that has expunge records is
      // very much more likely to be a read that went wrong than a server with no
      // mailboxes at all - and acting on it would delete every record there is.
      if (existingFolders.empty())
         return 0;

      __int64 removed = 0;

      for (const std::pair<__int64, __int64> &folder : recordCounts)
      {
         if (existingFolders.find(folder.first) != existingFolders.end())
            continue;

         if (DeleteExpungedForFolder(folder.first))
            removed += folder.second;
      }

      return removed;
   }
}