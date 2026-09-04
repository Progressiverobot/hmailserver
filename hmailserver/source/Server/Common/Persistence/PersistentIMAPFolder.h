// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{

   class IMAPFolder;
   class ACL;
   enum PersistenceMode;

   class PersistentIMAPFolder
   {
   private:
	   PersistentIMAPFolder();
	   virtual ~PersistentIMAPFolder();
   public:

      // Deletes one folder, its subfolders and everything in them. Unless
      // forceDelete is set the inbox is emptied and kept rather than deleted: it is
      // the one folder an account cannot be without. This is the path IMAP DELETE
      // and IMAPFolder.Delete take, so a folder that carries a special-use
      // designation IS deleted here - a client that asks for its \Trash to go gets
      // what it asked for. Keeping designated folders is what DeleteByAccount does.
      static bool DeleteObject(std::shared_ptr<IMAPFolder> pFolder);
      static bool DeleteObject(std::shared_ptr<IMAPFolder> pFolder, bool forceDelete);
      static bool SaveObject(std::shared_ptr<IMAPFolder> pFolder, String &errorMessage, PersistenceMode mode);
      static bool SaveObject(std::shared_ptr<IMAPFolder> pFolder);

      // Empties an account. Every message goes; so does every folder except the inbox
      // and the folders with a special-use designation, which are emptied and kept -
      // the client that created (or was given) its Sent and Trash expects them to
      // outlive Account.DeleteMessages, the way the inbox always has. A parent that
      // has a kept folder beneath it is kept too, emptied, so that the kept folder is
      // never left pointing at a parent row that no longer exists.
      static bool DeleteByAccount(__int64 iAccountID);

      // The same with forceDelete set: nothing is kept. Used when the account itself
      // is being deleted.
      static bool DeleteByAccount(__int64 iAccountID, bool forceDelete);

      static bool GetExistsFolderContainingCharacter(String theChar);

   private:

      // The one implementation behind the three public entry points. keepSpecialUse
      // is only ever set by DeleteByAccount; kept reports whether this folder's row
      // survived, which a parent needs to know to decide about its own.
      static bool DeleteObject_(std::shared_ptr<IMAPFolder> pFolder, bool forceDelete, bool keepSpecialUse, bool &kept);

   public:

      static unsigned int GetUniqueMessageID(__int64 accountID, __int64 folderID);

      // RFC 7162 (CONDSTORE/QRESYNC): atomically bumps and returns the next
      // per-mailbox mod-sequence value, used when a message arrives or its flags change.
      static __int64 GetNextModSeq(__int64 accountID, __int64 folderID);

      // RFC 7162 (QRESYNC): record a tombstone for an expunged message so the server can
      // later report it via "* VANISHED (EARLIER)" to clients resyncing after a disconnect.
      static bool AddExpunged(__int64 accountID, __int64 folderID, __int64 uid, __int64 modSeq);
      // Returns the UIDs expunged from the folder at a mod-sequence greater than sinceModSeq.
      //
      // Only meaningful when RemembersExpungesSince agrees: this reads the tombstone
      // table, so once tombstones have been pruned it answers with what is left,
      // which for an old enough sinceModSeq is a SHORT answer and not a wrong-looking
      // one. Ask first.
      static std::vector<__int64> GetExpungedUIDsSince(__int64 folderID, __int64 sinceModSeq);

      /*
         Whether the tombstone table can still answer a client resynchronising from
         sinceModSeq exactly.

         RFC 7162 section 3.2.6:

            "Note: A server that receives a mod-sequence smaller than <minmodseq>,
            where <minmodseq> is the value of the smallest expunged mod-sequence it
            remembers minus one, MUST behave as if it was requested to report all
            expunged messages from the provided UID set parameter."

         So this is that boundary, and when it returns false the caller MUST report
         every UID in the requested set that the mailbox no longer holds, rather than
         the rows that happen to be left. Reporting the rows that are left is the
         silent-data-loss case: the client is told a subset vanished, believes the
         rest are still there, and never asks again.

         True when the folder has no tombstones at all. That is not a guess: the
         retention sweep never removes a folder's newest record, so an empty result
         means nothing was ever expunged from this folder rather than that everything
         has been forgotten.
      */
      static bool RemembersExpungesSince(__int64 folderID, __int64 sinceModSeq);

      // Removes all expunge tombstones belonging to a folder (used when a folder is deleted).
      static bool DeleteExpungedForFolder(__int64 folderID);

      // One (folder id, record count) pair for every folder that has tombstones.
      static std::vector<std::pair<__int64, __int64>> GetExpungedRecordCounts();

      // Prunes one folder's tombstones down to about keepRecords, oldest first,
      // removing roughly batchRecords rows per statement. Returns how many rows went.
      static __int64 PruneExpungedForFolder(__int64 folderID, __int64 recordCount, int keepRecords, int batchRecords);

      // Removes tombstones whose folder no longer exists, given the folders that have
      // tombstones. Returns how many rows went.
      static __int64 DeleteOrphanedExpunged(const std::vector<std::pair<__int64, __int64>> &recordCounts);

      static __int64 GetUserInboxFolder(__int64 accountID);

   private:

      static bool IncreaseCurrentUID_(__int64 folderID);
      static unsigned int GetCurrentUID_(__int64 folderID);

      static bool IncreaseCurrentModSeq_(__int64 folderID);
      static __int64 GetCurrentModSeq_(__int64 folderID);


   };
}
