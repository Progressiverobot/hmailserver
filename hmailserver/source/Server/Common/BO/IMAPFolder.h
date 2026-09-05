// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later


#pragma once

#include "Messages.h"
#include "..\Util\VariantDateTime.h"

namespace HM
{
   class IMAPFolders;
   //class IMAPFolder;
   class ACLPermissions;

   class IMAPFolder 
   {
   public:

      enum
      {
         MaxFolderDepth = 25
      };
   
      IMAPFolder();
      IMAPFolder(__int64 iAccountID, __int64 iParentFolderID);

      virtual ~IMAPFolder();

      __int64 GetID() const { return dbid_; }
      void SetID(__int64 lNewVal) { dbid_ = lNewVal;}

      __int64 GetParentFolderID() const;
      void SetParentFolderID(__int64 value) {parent_folder_id_ = value;}

      __int64 GetAccountID() const { return account_id_;} 
      void SetAccountID(__int64 newVal) {account_id_ = newVal;}

      unsigned int GetCurrentUID() const { return current_uid_;} 
      void SetCurrentUID(unsigned int currentUID) {current_uid_ = currentUID;}

      // RFC 7162 (CONDSTORE/QRESYNC): the mailbox HIGHESTMODSEQ, i.e. the largest
      // mod-sequence ever assigned to a message in (or removed from) this folder.
      __int64 GetCurrentModSeq() const { return current_modseq_;}
      void SetCurrentModSeq(__int64 currentModSeq) {current_modseq_ = currentModSeq;}

      const DateTime &GetCreationTime() const { return create_time_;} 
      void SetCreationTime(const DateTime &currentUID) {create_time_ = currentUID;}


      bool GetIsSubscribed() const { return folder_is_subscribed_;}
      void SetIsSubscribed(bool bNewVal) { folder_is_subscribed_ = bNewVal;}

      // RFC 6154 (SPECIAL-USE): the special-use attributes this folder has been
      // explicitly designated with, as an IMAPSpecialUse::Designation bitmask. Zero
      // means "no explicit designation", in which case LIST falls back to guessing
      // from the folder name exactly as it did before 6.2.19.
      //
      // This is stored on the folder row (hm_imapfolders.folderspecialuse) rather than
      // as a settings row: the designation is per folder and per account, it must
      // disappear when the folder is deleted and follow it when it is renamed, and it
      // is read on every LIST. A row in hm_settings would have given none of that -
      // that table has a 30 character unique name column, is cached in its entirety in
      // memory at startup, and nothing would ever clean up the entry for a folder that
      // no longer exists.
      //
      // SetSpecialUseFlags only touches memory and exists for the two places that
      // load a folder from somewhere (IMAPFolders::Refresh reading the row, XMLLoad
      // reading a backup). Anything that wants to CHANGE a live folder's designation
      // must use StoreSpecialUseFlags below, or the value will not survive a restart.
      int GetSpecialUseFlags() const { return special_use_flags_;}
      void SetSpecialUseFlags(int newVal) { special_use_flags_ = newVal;}

      // Writes this folder's special-use designation to the database and, only on
      // success, updates the cached value. Returns false when the folder has no row
      // yet or the write failed; the caller must then refuse the command, because a
      // designation that exists only in the shared folder cache would be advertised
      // by LIST until the next restart and then silently vanish - the hardest kind of
      // bug to get a report for.
      //
      // This is a targeted single-column UPDATE keyed on folderid, deliberately NOT
      // PersistentIMAPFolder::SaveObject, for two reasons that both cost data:
      //
      //   * SaveObject decides between INSERT and UPDATE by asking the in-memory
      //     object for its id. IMAPFolders::CreatePath ignores the result of its own
      //     save, so a folder whose insert failed is still put into the per-account
      //     cache with id 0; a designation written through SaveObject would then be
      //     an INSERT, producing a second hm_imapfolders row for a folder that
      //     already had one (or being rejected outright by the unique index on
      //     folderaccountid/folderparentid/foldername, which is no better - the
      //     command fails after the folder was created).
      //   * SaveObject rewrites foldername, folderparentid and folderaccountid from
      //     the cached object as well. A RENAME committed by another connection
      //     between the CREATE and the designation write would be silently undone.
      //
      // The SQL lives here rather than in PersistentIMAPFolder because IMAPFolders,
      // this object's own collection, already issues its SELECT the same way; keeping
      // the two halves of one column together beats spreading them across two files.
      bool StoreSpecialUseFlags(int designations);

      String GetFolderName() const { return folder_name_;}
      void SetFolderName(const String & sNewVal) { folder_name_ =sNewVal; }

      String GetName() const {return GetFolderName(); }
      
      std::shared_ptr<Messages> GetMessages();

      std::shared_ptr<IMAPFolders> GetSubFolders();
      std::shared_ptr<ACLPermissions> GetPermissions();

      static void EscapeFolderString(String &sFolderString);
      static void UnescapeFolderString(String &sFolderString);
      
      int GetFolderDepth(int &iRecursion);

      bool XMLStore(XNode *pParentNode, int iBackupOptions);
      bool XMLLoad(XNode *pFolderNode, int iRestoreOptions);
      bool XMLLoadSubItems(XNode *pFolderNode, int iRestoreOptions);

      static bool IsValidFolderName(const std::vector<String> &vecPath, bool bIsPublicFolder);

      bool IsPublicFolder();

   protected:

      __int64 dbid_;
      __int64 account_id_;
      __int64 parent_folder_id_;
      unsigned int current_uid_;
      __int64 current_modseq_;

      bool folder_is_subscribed_;
      int special_use_flags_;
      AnsiString folder_name_;

      std::shared_ptr<Messages> messages_;
      std::shared_ptr<IMAPFolders> sub_folders_;

      // Connections share IMAPFolder instances, so the lazy creation of the
      // subfolder collection has to be serialised (upstream #566).
      boost::recursive_mutex sub_folders_mutex_;
 
      DateTime create_time_;
   };

}

