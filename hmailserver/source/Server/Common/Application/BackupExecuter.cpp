// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "BackupExecuter.h"
#include "Backup.h"

#include "..\Util\Utilities.h"
#include "..\Util\Time.h"
#include "..\BO\Domains.h"
#include "..\BO\Domain.h"
#include "..\BO\IMAPFolders.h"
#include "..\BO\Accounts.h"
#include "..\BO\Aliases.h"
#include "..\BO\DomainAliases.h"
#include "..\BO\DistributionLists.h"

#include "..\Persistence\PersistentMessage.h"
#include "..\Util\Compression.h"
#include "..\Util\ServiceManager.h"

#include "BackupManager.h"
#include "BackupRetention.h"
#include "ACLManager.h"
#include "Reinitializator.h"

#include "..\SQL\DatabaseUnavailableMarker.h"

#include "../../IMAP/IMAPConfiguration.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   static bool
   DatabaseReadsFailed_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // True, and the backup has been failed, when at least one database read since
   // the enclosing DatabaseUnavailableMarker::Scope did not run.
   //
   // Note the distinction the marker exists to draw: "did not run" is not the same
   // as "ran and returned nothing". A server with no domains configured must still
   // produce a (correctly empty) backup, so an empty result on its own cannot be
   // treated as a failure - which is precisely why the empty archive described at
   // the call site was indistinguishable from a legitimate one.
   //
   // A file-scope function rather than a member because BackupExecuter.h is not
   // part of this change, and the message must be identical at both call sites: an
   // administrator comparing two backup logs should not have to work out whether
   // two different wordings mean the same thing.
   //---------------------------------------------------------------------------()
   {
      if (!DatabaseUnavailableMarker::IsMarked())
         return false;

      Application::Instance()->GetBackupManager()->OnBackupFailed(
         "The database stopped answering while the domains, accounts and settings were being read, so the backup would have been incomplete. No backup file has been written and the previous backups have not been touched.");

      return true;
   }

   static void
   DiscardIncompleteArchive_(const String &sZipFile, bool existedBeforeThisRun)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Removes the archive this run created, once it is established that it can never
   // be completed.
   //
   // Leaving it behind is worse than it looks. It carries a name BackupRetention
   // recognises as one of ours and a timestamp newer than every real archive in the
   // directory, so from then on it counts towards ScheduledBackupKeepCount and is
   // treated as the most recent backup. A destination that fails late in the run -
   // which is what running out of space looks like - would therefore accumulate
   // truncated archives that push the good ones out of the keep count, and
   // BackupScheduleTask would conclude on the next service start that a recent
   // backup already exists. It is also the file an administrator would reach for
   // first in a disaster, being the newest one there.
   //
   // existedBeforeThisRun is false in every reachable case; the flag is here so
   // that this can never be the code that deletes an archive it did not create. See
   // where it is computed. A file-scope function rather than a member because
   // BackupExecuter.h is not part of this change.
   //---------------------------------------------------------------------------()
   {
      if (existedBeforeThisRun)
         return;

      if (!FileUtilities::Exists(sZipFile))
         return;

      if (FileUtilities::DeleteFile(sZipFile))
      {
         Logger::Instance()->LogBackup("Removed the incomplete archive " + sZipFile + ".");
         return;
      }

      // Not escalated to the error log: the caller already reports the backup
      // failure itself as critical, and this line tells whoever reads that report
      // which file to remove by hand.
      Logger::Instance()->LogBackup("The incomplete archive " + sZipFile + " could not be removed. Delete it by hand - while it is there it counts as a backup for retention purposes.");
   }

   BackupExecuter::BackupExecuter()
   {
      backup_mode_ = 0;
   }

   BackupExecuter::~BackupExecuter(void)
   {

   }

   void
   BackupExecuter::LoadSettings_()
   {
      destination_ = Configuration::Instance()->GetBackupDestination();
      if (destination_.Right(1) == _T("\\"))
         destination_ = destination_.Left(destination_.GetLength() - 1);

      backup_mode_ = Configuration::Instance()->GetBackupOptions();
   }

   bool
   BackupExecuter::StartBackup()
   {
      Logger::Instance()->LogBackup("Loading backup settings....");

      LoadSettings_();

      // Special temp setting to skip files during backup/restore while still storing/restoring db file/message info.
      bool bMessagesDBOnly = IniFileSettings::Instance()->GetBackupMessagesDBOnly();


      if (backup_mode_ & Backup::BOMessages)
      {
         if (!PersistentMessage::GetAllMessageFilesAreInDataFolder())
         {
            Application::Instance()->GetBackupManager()->OnBackupFailed("All messages are not located in the data folder.");
            return false;
         }
      }

      if (!FileUtilities::Exists(destination_))
      {
         Application::Instance()->GetBackupManager()->OnBackupFailed("The specified backup directory is not accessible: " + destination_);
         return false;
      }

      // Refuse, before writing anything, if the parts of the backup that come out
      // of the database cannot come out of the database.
      //
      // Domains::Refresh - and every collection underneath it - returns void and
      // discards the result of its load. A query that fails because the database is
      // down therefore leaves an empty collection, and an empty collection stores no
      // XML at all. So a backup taken during a database outage used to be written,
      // reported as "Backup completed successfully", and contain no domains, no
      // accounts and no aliases. Restoring that archive deletes every domain on the
      // server - Domains::DeleteAll runs first, by design, so that the data
      // directory is not restored and then dropped - and then puts nothing back.
      //
      // That was survivable while a backup only ever happened because somebody
      // asked for one at a moment they had chosen. On a schedule it happens
      // unattended, at the same time as whatever else runs at 02:00, and with
      // retention enabled the hollow archive is also the newest one - so it is the
      // one somebody reaches for, and the good ones are the ones retention has been
      // deleting.
      //
      // GetIsConnected only reports whether the pool holds any connections, so it
      // catches "the pool never came up" and not "the server stopped answering a
      // moment ago". That second case is what the marker checks below are for; this
      // check is here because it is free and it turns the common case into a clean
      // refusal instead of a partly-done backup.
      //
      // Ordered after the destination check deliberately: a missing destination is
      // by far the more common misconfiguration, and its existing message is the one
      // callers and the OnBackupFailed scripting event already expect for it.
      if (backup_mode_ & (Backup::BODomains | Backup::BOSettings))
      {
         std::shared_ptr<DatabaseConnectionManager> databaseManager = Application::Instance()->GetDBManager();

         if (!databaseManager || !databaseManager->GetIsConnected())
         {
            Application::Instance()->GetBackupManager()->OnBackupFailed("The database is not available, so the domains, accounts and settings could not be read. No backup file has been written and the previous backups have not been touched.");
            return false;
         }
      }

      // Everything read out of the database from here on is watched. The marker is
      // thread-local and this whole function runs on one work-queue thread, so the
      // scope covers the reads below without any of them having to be moved; it also
      // clears any stale failure left on this thread by earlier work, which is why
      // it is a Scope and not a bare IsMarked() call.
      DatabaseUnavailableMarker::Scope databaseScope;

      String sTime = Time::GetCurrentDateTime();
      sTime.Replace(_T(":"), _T(""));

      // Generate name for zip file. We always create zip
      // file
      String sZipFile;
      sZipFile.Format(_T("%s\\HMBackup %s.7z"), destination_.c_str(), sTime.c_str());

      // Whether the archive already existed before this run touched it. Nothing
      // should be able to make that true - the name carries the local time to the
      // second and only one backup may run at a time - but the failure paths below
      // delete the archive they created, and "did it exist before?" is what makes
      // that provably a deletion of our own half-written file rather than of
      // somebody's backup.
      bool zipExistedBeforeThisRun = FileUtilities::Exists(sZipFile);

      String sXMLFile;
      sXMLFile.Format(_T("%s\\hMailServerBackup.xml"), destination_.c_str());

      // The name of the backup directory that
      // contains all the data files.
      String sDataBackupDir = destination_ + "\\DataBackup";

      // Backup all properties.
      XDoc oDoc;

      XNode *pBackupNode = oDoc.AppendChild(_T("Backup"));
      XNode *pBackupInfoNode = pBackupNode->AppendChild(_T("BackupInformation"));

      // Store backup mode
      pBackupInfoNode->AppendAttr(_T("Mode"), StringParser::IntToString(backup_mode_));
      pBackupInfoNode->AppendAttr(_T("Version"), Application::Instance()->GetVersionNumber());

      // Backup business objects
      if (backup_mode_ & Backup::BODomains)
      {
         Logger::Instance()->LogBackup("Backing up domains...");

         if (!BackupDomains_(pBackupNode))
         {
            Application::Instance()->GetBackupManager()->OnBackupFailed("Could not backup domains.");
            return false;
         }

         // Checked here as well as at the end, and not only to fail earlier: the
         // data-directory copy below can run for hours on a real message store, and
         // copying an entire mail store to produce an archive we already know will
         // be refused is hours of disk contention on a live server for nothing.
         if (DatabaseReadsFailed_())
            return false;

         // Backup message files
         if (backup_mode_ & Backup::BOMessages && !bMessagesDBOnly)
         {
            Logger::Instance()->LogBackup("Backing up data directory...");
            if (!BackupDataDirectory_(sDataBackupDir))
            {
               Application::Instance()->GetBackupManager()->OnBackupFailed("Could not backup data directory.");
               return false;
            }


         }
      }

      // Save information in the XML file where messages can be found.
      if (backup_mode_ & Backup::BOMessages)
      {
         XNode *pMessageFile = pBackupInfoNode->AppendChild(_T("DataFiles"));

         if (backup_mode_ & Backup::BOCompression)
         {
            pMessageFile->AppendAttr(_T("Format"), _T("7z"));
            pMessageFile->AppendAttr(_T("Size"), StringParser::IntToString(FileUtilities::FileSize(sZipFile)));
         }
         else
         {
            pMessageFile->AppendAttr(_T("Format"), _T("Raw"));
            pMessageFile->AppendAttr(_T("FolderName"), _T("DataBackup"));
         }
      }

      if (backup_mode_ & Backup::BOSettings)
      {
         Logger::Instance()->LogBackup("Backing up settings...");
         Configuration::Instance()->XMLStore(pBackupNode);
      }

      // The last database read has happened. Nothing has been written to the
      // destination yet, which is the property that makes refusing here safe: the
      // previous backups are untouched and no partial archive exists to confuse the
      // next retention pass.
      if (DatabaseReadsFailed_())
         return false;

      Logger::Instance()->LogBackup(_T("Writing XML file..."));
      String sXMLData = oDoc.GetXML();
      if (!FileUtilities::WriteToFile(sXMLFile, sXMLData, true))
      {
         Application::Instance()->GetBackupManager()->OnBackupFailed("Could not write to the XML file.");
         return false;
      }

      // Compress the XML file. The result is checked before the source is deleted:
      // an archive that could not be written must fail the backup, not be reported
      // as complete and then have the only other copy removed.
      Compression oComp;
      bool xmlCompressed = oComp.AddFile(sZipFile, sXMLFile);

      // Delete the XML file
      FileUtilities::DeleteFile(sXMLFile);

      if (!xmlCompressed)
      {
         DiscardIncompleteArchive_(sZipFile, zipExistedBeforeThisRun);

         Application::Instance()->GetBackupManager()->OnBackupFailed("Could not add the backup settings to the archive. The backup is incomplete.");
         return false;
      }

      // Should we compress the message files?
      if (backup_mode_ & Backup::BOMessages &&
          backup_mode_ & Backup::BOCompression && !bMessagesDBOnly)
      {
         Logger::Instance()->LogBackup("Compressing message files...");

         // Same again, and it matters more here: the staged copy below is deleted
         // straight afterwards, so a compression failure that went unnoticed would
         // leave a truncated archive and no message files at all, announced as a
         // successful backup.
         if (backup_mode_ & Backup::BOMessages)
         {
            if (!oComp.AddDirectory(sZipFile, sDataBackupDir + "\\"))
            {
               DiscardIncompleteArchive_(sZipFile, zipExistedBeforeThisRun);

               Application::Instance()->GetBackupManager()->OnBackupFailed("Could not add the message files to the archive. The backup is incomplete; the staged message files have been left in " + sDataBackupDir + ".");
               return false;
            }
         }

         // Since the files are now compressed, we can deleted
         // the data backup directory
         if (!FileUtilities::DeleteDirectory(sDataBackupDir, true))
         {
            // The archive itself is complete at this point, so it is deliberately
            // left alone: this failure is about tidying up the staging folder, and
            // deleting a good archive because we could not delete a temporary
            // directory would be a far worse outcome than the untidy destination.
            Application::Instance()->GetBackupManager()->OnBackupFailed("Could not delete files from the destination directory.");
            return false;
         }
       }

      // Retention runs here and nowhere else. This is the one point that every
      // failure path above has already returned past, so the new archive is
      // complete on disk before anything old is even considered for deletion. That
      // ordering is the whole difference between retention and a data-loss feature:
      // a destination with room for one more archive but not two would otherwise
      // have had yesterday's deleted and today's fail to write, leaving nothing at
      // all.
      //
      // It runs before OnBackupCompleted rather than after, so that "Backup
      // completed successfully" in the backup log means the destination is already
      // in its final state. Anything watching for that line - the administrative
      // UI, the regression tests, an administrator's OnBackupCompleted script -
      // would otherwise be racing the deletions.
      //
      // Deliberately not conditional on the run having been started by the
      // schedule. A policy that only bounded scheduled runs would stop bounding the
      // directory the moment somebody took a backup by hand into the same place,
      // which is exactly when the destination is fullest.
      BackupRetention::Apply(destination_, sZipFile,
         IniFileSettings::Instance()->GetScheduledBackupKeepCount(),
         IniFileSettings::Instance()->GetScheduledBackupMaxAgeDays());

      Application::Instance()->GetBackupManager()->OnBackupCompleted();

      return true;
   }

   bool
   BackupExecuter::BackupDataDirectory_(const String &sDataBackupDir)
   {
      String sDataDir = IniFileSettings::Instance()->GetDataDirectory();

      String errorMessage;

      bool bResult = FileUtilities::CopyDirectory(sDataDir, sDataBackupDir, errorMessage);
      if (!bResult)
      {
         Logger::Instance()->LogBackup("Failed to copy data directory. Details: " + errorMessage);
         return bResult;
      }

      bResult = FileUtilities::DeleteFilesInDirectory(sDataBackupDir);

      if (!bResult)
      {
         Logger::Instance()->LogBackup("Failed to delete files in backup root directory. Please see hMailServer error log.");
      }

      return bResult;
   }

   bool
   BackupExecuter::BackupDomains_(XNode *pBackupNode)
   {
      std::shared_ptr<Domains> pDomains = std::shared_ptr<Domains>(new Domains);
      pDomains->Refresh();

      // The result is returned rather than discarded. Collection::XMLStore stops and
      // returns false the first time one of its items cannot be stored, so
      // discarding it meant a domain whose accounts or aliases could not be written
      // produced an archive containing the domains up to that point - and that was
      // still reported as a successful backup.
      //
      // Refresh itself returns void and cannot be checked here: at this level a
      // failed load is indistinguishable from a server that has no domains, which is
      // why the caller consults DatabaseUnavailableMarker instead.
      return pDomains->XMLStore(pBackupNode, backup_mode_);
   }

   bool
   BackupExecuter::StartRestore(std::shared_ptr<Backup> pBackup)
   {
      bool bMessagesDBOnly = IniFileSettings::Instance()->GetBackupMessagesDBOnly();

      Logger::Instance()->LogBackup("Reading XML file...");
      String sZipFile = pBackup->GetBackupFile();

      String sTempDir = IniFileSettings::Instance()->GetTempDirectory();
      String sXMLFile = sTempDir + "\\hMailServerBackup.xml";
      FileUtilities::DeleteFile(sXMLFile);

      Compression oComp;
      if (!oComp.Uncompress(sZipFile, sTempDir, "hMailServerBackup.xml"))
      {
         String sErrorMessage = Formatter::Format("Unable to uncompress hMailServerBackup.xml from {0} to {1}. Please confirm that hMailServer has permissions to {0} and {1}.", sZipFile, sTempDir);
         Application::Instance()->GetBackupManager()->OnBackupFailed(sErrorMessage);
         return false;
      }

      String sXMLData = FileUtilities::ReadCompleteTextFile(sXMLFile);
      if (sXMLData.IsEmpty())
      {
         String sErrorMessage = Formatter::Format("The file {0} could not be read.", sXMLFile);
         Application::Instance()->GetBackupManager()->OnBackupFailed(sErrorMessage);
         return false;
      }

      XDoc oDoc;
      oDoc.Load(sXMLData);

      FileUtilities::DeleteFile(sXMLFile);

      String sBackup = "Backup";
      XNode *pBackupNode = oDoc.GetChild(sBackup);
      if (!pBackupNode)
      {
         String sErrorMessage = "The supplied XML file is not a valid hMailServer backup file";
         Application::Instance()->GetBackupManager()->OnBackupFailed(sErrorMessage);
         return false;
      }

      int iRestoreOptions = pBackup->GetRestoreOptions();


      if (iRestoreOptions & Backup::BODomains)
      {
         // First drop all domains. We need to do this prior to restoring
         // the data directory. When we drop the domains, we will also
         // drop the domain folders from the data directory. If we do this
         // in the wrong order, we'll hence first restore the data directory
         // and then drop it.
         std::shared_ptr<Domains> pDomains = std::shared_ptr<Domains>(new Domains);

         pDomains->Refresh();
         if (!bMessagesDBOnly)
            pDomains->DeleteAll();

         // We need to do the same with public folders.
         if (iRestoreOptions & Backup::BOSettings && !bMessagesDBOnly)
            Configuration::Instance()->GetIMAPConfiguration()->GetPublicFolders()->DeleteAll();

         // Should we restore messages as well?
         if (iRestoreOptions & Backup::BOMessages && !bMessagesDBOnly)
         {
            Logger::Instance()->LogBackup("Restoring data directory...");

            if (!RestoreDataDirectory_(pBackup, pBackupNode))
               return false;
         }

         Logger::Instance()->LogBackup("Restoring domains...");

         if (!pDomains->XMLLoad(pBackupNode, iRestoreOptions))
         {
            String sErrorMessage = "Restore of domains failed. Please check hMailServer log.";
            Logger::Instance()->LogBackup(sErrorMessage);
            Application::Instance()->GetBackupManager()->OnBackupFailed(sErrorMessage);

            return false;
         }
      }

      // Backup settings last since they may be referring to objects in the domains.
      if (iRestoreOptions & Backup::BOSettings)
      {
         Logger::Instance()->LogBackup("Restoring settings...");
         if (!Configuration::Instance()->XMLLoad(pBackupNode, iRestoreOptions))
         {
            String sErrorMessage = "Restore of settings failed. Please check hMailServer log.";
            Logger::Instance()->LogBackup(sErrorMessage);
            Application::Instance()->GetBackupManager()->OnBackupFailed(sErrorMessage);
            return false;
         }
      }


      // Reinitialize server since everything may have changed.
      // We can't run Application::ReInitialize here, since this
      // thread is owned by the backup manager, which is owned
      // by the application. And the backup manager is recreated
      // upon reinitialization.
      Logger::Instance()->LogBackup("Reinitializing server (async)...");

      Reinitializator::Instance()->ReInitialize();

      Logger::Instance()->LogBackup("Restore completed successfully.");

      return true;
   }

   bool
   BackupExecuter::RestoreDataDirectory_(std::shared_ptr<Backup> pBackup, XNode *pBackupNode)
   {
      XNode *pBackupInfoNode = pBackupNode->GetChild(_T("BackupInformation"));

      // Create the path to the zip file.
      String sBackupFile = pBackup->GetBackupFile();
      String sPath = sBackupFile.Mid(0, sBackupFile.ReverseFind(_T("\\")));

      String sDirContainingDataFiles;
      String sDataFileFormat = pBackupInfoNode->GetChildAttr(_T("DataFiles"), _T("Format"))->value;

      bool extractedToTempDirectory = sDataFileFormat.CompareNoCase(_T("7Z")) == 0;

      String sExtractedFilesDirectory;
      if (extractedToTempDirectory)
      {
         // Create the path to the directory that will contain the extracted files.
         //  This directory is temporary and will be removed when we're done.
         sExtractedFilesDirectory = Utilities::GetUniqueTempDirectory();

         // Extract the files to this directory.
         Compression oComp;
         if (!oComp.Uncompress(sBackupFile, sExtractedFilesDirectory))
         {
            FileUtilities::DeleteDirectory(sExtractedFilesDirectory, true);

            ReportRestoreFailure_("Restore failed: the messages could not be extracted from " + sBackupFile +
                                  ". The existing data directory has not been touched.");
            return false;
         }

         // The data files in the zip file are stored in
         // a directory called DataBackup.
         sDirContainingDataFiles = sExtractedFilesDirectory + "\\DataBackup";
      }
      else
      {
         // Fetch the path to the data files.
         String sFolderName = pBackupInfoNode->GetChildAttr(_T("DataFiles"), _T("FolderName"))->value;
         sDirContainingDataFiles = sPath + "\\" + sFolderName;
      }

      // Confirm the replacement exists before deleting what is there now. The copy
      // below throws when its source is missing - which is what a settings-only
      // backup restored with the messages option set looks like - and by then the
      // deletion had already run, so the live data directory was emptied and the
      // only remaining copy of the mail was in a temporary folder that nothing
      // cleans up and nobody would think to look in.
      if (!FileUtilities::DirectoryExists(sDirContainingDataFiles))
      {
         if (extractedToTempDirectory)
            FileUtilities::DeleteDirectory(sExtractedFilesDirectory, true);

         ReportRestoreFailure_("Restore failed: the backup does not contain a message store (" + sDirContainingDataFiles +
                               " does not exist). The existing data directory has not been touched.");
         return false;
      }

      // Delete all directories from the data directory
      // so that we're sure that we're doing a clean restore
      String sDataDirectory = IniFileSettings::Instance()->GetDataDirectory();

      FileUtilities::DeleteFilesInDirectory(sDataDirectory);
      FileUtilities::DeleteDirectoriesInDirectory(sDataDirectory);

      String errorMessage;
      bool copied = FileUtilities::CopyDirectory(sDirContainingDataFiles, sDataDirectory, errorMessage);

      if (!copied)
      {
         // From here on the data directory has already been emptied, so the
         // extracted copy is deliberately left in place: it is the only copy of
         // the messages that still exists, and the administrator is told where.
         ReportRestoreFailure_("Restore failed while copying messages into " + sDataDirectory + ". " + errorMessage +
                               " The messages from the backup have been left in " + sDirContainingDataFiles +
                               " - do not delete that folder until they have been recovered.");
         return false;
      }

      if (extractedToTempDirectory)
      {
         // The temporary directory we created while
         // unzipping should be deleted now.
         FileUtilities::DeleteDirectory(sExtractedFilesDirectory, true);
      }

      return true;
   }

   void
   BackupExecuter::ReportRestoreFailure_(const String &message)
   {
      Logger::Instance()->LogBackup(message);
      Application::Instance()->GetBackupManager()->OnBackupFailed(message);
   }
}
