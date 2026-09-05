// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once


namespace HM
{
   class Domain;
   class IMAPFolders;
   class IMAPFolder;
   class Messages;
   class Message;
   class BackupManager;
   class BackupRestorer;

   class BackupExecuter
   {
   public:
      BackupExecuter();
      ~BackupExecuter(void);

      bool StartBackup();
      bool StartRestore(std::shared_ptr<Backup> pBackup);

   private:

      void LoadSettings_();

      bool BackupDomains_(XNode *pNode);
      bool BackupDataDirectory_(const String &sDataBackupDir);

      // Reads back the archive this run has just written and confirms it is one an
      // hMailServer of this version could restore. Runs before BackupRetention is
      // allowed to consider deleting anything, so an archive that cannot be read is
      // never the reason an older one that could be read was deleted.
      bool VerifyArchive_(const String &sZipFile);

      // The verified restore. Extracts the archive's message store to a scratch
      // directory through the code path a restore uses, holds it to exactly the
      // files this run staged for compression, and reconciles the message rows in
      // the index against those files. discardArchive says whether a false return
      // means the archive itself is bad (discard it) or only that it could not be
      // checked (keep it, but do not call the backup a success). Off with
      // BackupVerifyRestore=0.
      bool VerifyRestore_(const String &sZipFile, XNode *pBackupNode, bool &discardArchive);

      // Takes the restorer rather than the Backup and the XML node, because by the
      // time this is called the archive has been validated and the message store is
      // already on local disk - and because the one thing this function has to be
      // able to say, when the copy into an emptied data directory fails, is "keep
      // the staged files, they are now the only copy".
      bool RestoreDataDirectory_(BackupRestorer &restorer);
      void ReportRestoreFailure_(const String &message);
      
      int backup_mode_;

      // What BackupDataDirectory_ staged for compression - the count and the bytes
      // the extracted store is held to by VerifyRestore_.
      unsigned int staged_files_;
      unsigned __int64 staged_bytes_;
      
      // Backup properties
      String destination_;
      String xmlfile_;
   };
}