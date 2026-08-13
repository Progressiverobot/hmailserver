// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

namespace HM
{
   class Backup;

   // Everything a restore has to establish BEFORE it deletes anything.
   //
   // WHY THIS IS A SEPARATE PHASE
   //
   // A restore is the only operation in hMailServer that deletes every domain,
   // every account, every public folder and the whole message store on purpose,
   // and there is no transaction around any of it: Collection::DeleteAll issues one
   // DELETE per object and PersistentDomain::DeleteObject also removes the domain's
   // folder from disk. Nothing can put that back if the restore then fails. So a
   // restore has exactly one safe shape - check everything that can be checked, and
   // get everything that has to be copied onto local disk, before the first
   // deletion.
   //
   // That was not the shape it had. BackupExecuter::StartRestore dropped all the
   // domains first and only afterwards went looking inside the archive for the
   // things it needed, which is how each of these was reachable from the Control
   // Panel's three restore checkboxes and from BackupManager.LoadBackup over COM -
   // neither of which consults what the archive actually contains:
   //
   //  * Restoring "messages" from an archive that has no message store (any backup
   //    taken with BackupMessages off) dereferenced the null returned by
   //    GetChildAttr("DataFiles", "Format"). The access violation is trapped and
   //    logged by ExceptionHandler::Run rather than crashing the service, so the
   //    visible result was a server with every domain deleted, nothing restored, no
   //    BACKUP ERROR line in the backup log and no OnBackupFailed event - just an
   //    exception dump. The guard added later in RestoreDataDirectory_ for "the
   //    backup does not contain a message store" could never run, because the null
   //    dereference happened forty lines earlier.
   //
   //  * Restoring "domains" from an archive that contains no domains deleted every
   //    domain and reported success. Collection::XMLLoad deletes what is there and
   //    then loads from a child node that is simply absent, which is
   //    indistinguishable from a collection that was empty at backup time.
   //
   //  * Restoring "settings" from an archive that contains no settings did the same
   //    to the SSL certificates, TCP/IP ports, security ranges, global rules,
   //    blocked attachments, DNS black lists and white lists - every collection
   //    Configuration::XMLLoad touches.
   //
   //  * An archive written by a newer hMailServer was restored into an older one
   //    without a word. The backup index has carried a Version attribute since
   //    2010 and nothing had ever read it.
   //
   // The class only decides and stages. It reports its reasons back to the caller
   // as text instead of logging them itself, so that the one place that knows
   // whether this is a restore or a LoadBackup - and therefore whether the reason
   // belongs in the backup log, in an OnBackupFailed event, or in a COM error - is
   // the place that says so.
   class BackupRestorer
   {
   public:
      BackupRestorer();
      ~BackupRestorer();

      // Reads an archive's index and reports what the archive contains (a
      // combination of Backup::BackupOptions) and which hMailServer wrote it.
      // False, with a reason, when the archive cannot be read or is not one of
      // ours - which is what a truncated or half-copied archive looks like.
      //
      // Static and self-contained because two callers need exactly this and nothing
      // more: BackupManager::LoadBackup, which must not hand out a Backup object
      // for an archive that cannot be restored, and the check
      // BackupExecuter::StartBackup makes on the archive it has just written,
      // before retention is allowed to delete an older one.
      static bool ReadArchiveIndex(const String &archiveFile, int &archiveContents, String &archiveVersion, String &failureReason);

      // False, with a reason, when archiveVersion is a later hMailServer version
      // than the one running. An unrecognised or absent version is accepted: the
      // point is to stop a restore that is known to be backwards, not to refuse
      // every archive whose provenance cannot be proved.
      static bool VersionIsRestorable(const String &archiveVersion, String &failureReason);

      // Validates the archive against what the caller has asked to restore and
      // stages whatever has to be on local disk first. Nothing outside the
      // temporary directory is touched, so a false return leaves the server exactly
      // as it was.
      bool Prepare(std::shared_ptr<Backup> backup, String &failureReason);

      // The <Backup> element of the validated index. Owned by this object - it dies
      // with the BackupRestorer, so the restore has to finish inside its lifetime.
      XNode *GetBackupNode();

      // What Prepare accepted, which is what the restore should act on.
      int GetRestoreOptions() const;

      bool GetHasStagedMessageStore() const;

      // The directory holding the message store to copy into the data directory.
      // Either a directory this object extracted (deleted again by the destructor)
      // or, for an uncompressed backup, the DataBackup folder sitting next to the
      // archive - which is part of the backup itself and is never deleted here.
      String GetStagedMessageStore() const;

      // Keeps the extracted message store on disk instead of removing it. Called by
      // the one caller that has already emptied the live data directory: at that
      // point the extracted copy is the only copy of the mail that exists, and
      // tidying up would destroy it.
      void RetainStagedFiles();

   private:

      // Not copyable. This object owns an XDoc whose nodes are handed out as raw
      // pointers, and temporary directories that its destructor removes; a copy
      // would delete the other's directory and dangle the other's nodes.
      BackupRestorer(const BackupRestorer &);
      BackupRestorer &operator=(const BackupRestorer &);

      bool ReadIndex_(const String &archiveFile, String &failureReason);
      bool StageMessageStore_(const String &archiveFile, String &failureReason);

      XDoc document_;
      XNode *backup_node_;

      int archive_contents_;
      int restore_options_;

      String archive_version_;

      String index_directory_;
      String staging_directory_;
      String staged_message_store_;

      bool retain_staged_files_;
   };
}
