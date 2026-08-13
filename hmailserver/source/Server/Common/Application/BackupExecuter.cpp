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
#include "BackupRestorer.h"
#include "ACLManager.h"
#include "Reinitializator.h"

#include "..\SQL\DatabaseUnavailableMarker.h"

#include "../../IMAP/IMAPConfiguration.h"

#include <boost/filesystem.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // WHY THIS FILE COPIES DIRECTORY TREES ITSELF INSTEAD OF CALLING
   // FileUtilities::CopyDirectory
   //
   // CopyDirectory copies each file with the throwing overload of
   // boost::filesystem::copy_file, with no copy_options, and lets whatever comes out
   // of it escape. Three ordinary things make it throw, and the consequence of any
   // of them is the same and is much worse than a failed backup:
   //
   //  * The destination file already exists. copy_options::none means "fail if the
   //    destination exists", so staging into a DataBackup folder left behind by an
   //    earlier run throws. With CompressDestinationFiles switched off nothing ever
   //    removes that folder - it *is* the backup - so on that configuration the
   //    second backup the server ever takes throws, every time.
   //
   //  * The source file no longer exists. The tree is enumerated and then copied,
   //    and this is a live mail server: a POP3 DELE, an IMAP EXPUNGE or a delivery
   //    finishing removes message files continuously. Between listing a file and
   //    copying it, it can be gone.
   //
   //  * Something else has the file open in a way that denies read sharing - an
   //    anti-virus scanner mid-scan being the usual one.
   //
   // Where the exception goes is the problem. ExceptionHandler::Run reports it and
   // rethrows; WorkQueue::ExecuteTask releases its blocking slot and rethrows;
   // io_context::run propagates it; WorkQueue::IoServiceRunWorker rethrows anything
   // that is not ERROR_ABANDONED_WAIT_0 - and it is then an exception escaping a
   // boost::thread function, which calls std::terminate. A message file being
   // deleted while a backup runs takes the whole service down with it, and because
   // OnBackupFailed is never reached there is no BACKUP ERROR line and no
   // OnBackupFailed event to explain why.
   //
   // So the backup does its own copy, with the error_code overloads, and gives each
   // of the three cases the answer it deserves: overwrite, skip and count, retry
   // briefly then fail the backup naming the file. FileUtilities::CopyDirectory is
   // left alone because its other callers are not backups and a shared file is not
   // this change's to redefine.

   // How long, in total, one copy may spend waiting for files something else has
   // open. Bounded because it is not: a store of two hundred thousand messages
   // behind a scanner that is holding all of them would otherwise retry for days.
   // Past the budget the next locked file fails the run, with its name.
   const unsigned int COPY_RETRY_BUDGET_MS = 30000;
   const unsigned int COPY_RETRY_PAUSE_MS = 250;

   struct TreeCopyOutcome
   {
      unsigned int copied;

      // Files that were listed and then were not there. Normal on a live server,
      // reported as a count so that a number which is not small is visible.
      unsigned int vanished;

      unsigned int retry_sleep_ms;

      String failed_file;
      String failure_detail;

      TreeCopyOutcome() :
         copied(0),
         vanished(0),
         retry_sleep_ms(0)
      {
      }
   };

   static bool
   CopyOneFile_(const boost::filesystem::path &from, const boost::filesystem::path &to, TreeCopyOutcome &outcome)
   {
      for (;;)
      {
         boost::system::error_code copyError;
         boost::filesystem::copy_file(from, to, boost::filesystem::copy_options::overwrite_existing, copyError);

         if (!copyError)
         {
            outcome.copied++;
            return true;
         }

         // Gone rather than unreadable. hMailServer opens its own message files with
         // _SH_DENYNO (see File::Open), so a file that is still there and still will
         // not copy is being held by something outside the server, which is worth
         // waiting for and then failing over. A file that has been deleted since the
         // directory was listed is just mail flow, and failing the nightly backup
         // because a user emptied their trash during it would be absurd.
         //
         // "Positively established as absent", not "could not be found": exists()
         // reports a failure to look through its error_code, and treating that as
         // absence would silently leave a message out of the backup - which is the
         // outcome all of this exists to prevent.
         boost::system::error_code existsError;
         bool sourceStillExists = boost::filesystem::exists(from, existsError);

         if (!existsError && !sourceStillExists)
         {
            outcome.vanished++;
            return true;
         }

         if (outcome.retry_sleep_ms >= COPY_RETRY_BUDGET_MS)
         {
            outcome.failed_file = String(from.wstring());
            outcome.failure_detail = copyError.message().c_str();
            return false;
         }

         Sleep(COPY_RETRY_PAUSE_MS);
         outcome.retry_sleep_ms += COPY_RETRY_PAUSE_MS;
      }
   }

   static bool
   CopyTree_(const boost::filesystem::path &from, const boost::filesystem::path &to, TreeCopyOutcome &outcome)
   {
      boost::system::error_code createError;
      boost::filesystem::create_directories(to, createError);

      if (createError)
      {
         outcome.failed_file = String(to.wstring());
         outcome.failure_detail = createError.message().c_str();
         return false;
      }

      boost::system::error_code iterateError;
      boost::filesystem::directory_iterator file(from, iterateError);

      if (iterateError)
      {
         // A source directory that cannot be listed is a real failure, including the
         // case where it does not exist. CopyDirectory threw std::logic_error for
         // that one, which no caller was catching.
         outcome.failed_file = String(from.wstring());
         outcome.failure_detail = iterateError.message().c_str();
         return false;
      }

      boost::filesystem::directory_iterator end;

      for (; file != end; ++file)
      {
         boost::filesystem::path current(file->path());

         boost::system::error_code statusError;
         bool isDirectory = boost::filesystem::is_directory(current, statusError);

         if (statusError)
         {
            // Listed, and then we could not ask what it was. Almost always because it
            // has just been deleted - but only "almost", so the entry has to be shown
            // to be gone before it is written off. Skipping a directory we simply
            // could not read would leave a whole account out of the backup.
            boost::system::error_code existsError;
            bool stillExists = boost::filesystem::exists(current, existsError);

            if (!existsError && !stillExists)
            {
               outcome.vanished++;
               continue;
            }

            outcome.failed_file = String(current.wstring());
            outcome.failure_detail = statusError.message().c_str();
            return false;
         }

         if (isDirectory)
         {
            if (!CopyTree_(current, to / current.filename(), outcome))
               return false;
         }
         else
         {
            if (!CopyOneFile_(current, to / current.filename(), outcome))
               return false;
         }
      }

      return true;
   }

   static bool
   CopyTreeGuarded_(const String &from, const String &to, TreeCopyOutcome &outcome)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // CopyTree_ with a backstop. Every operation inside it uses an error_code
   // overload, so nothing should reach here - but the whole point of this code is
   // that an exception escaping a work-queue task terminates the process, and that
   // property must not depend on having enumerated every throwing call correctly.
   //---------------------------------------------------------------------------()
   {
      try
      {
         return CopyTree_(boost::filesystem::path(from.c_str()), boost::filesystem::path(to.c_str()), outcome);
      }
      catch (boost::thread_interrupted const &)
      {
         // The service is shutting down. Task::Run handles this one and must go on
         // being allowed to.
         throw;
      }
      catch (std::exception const &exception)
      {
         outcome.failed_file = from;
         outcome.failure_detail = exception.what();
         return false;
      }
      catch (...)
      {
         outcome.failed_file = from;
         outcome.failure_detail = "an unrecognised error";
         return false;
      }
   }

   static String
   DescribeCopyFailure_(const TreeCopyOutcome &outcome)
   {
      return Formatter::Format("{0} could not be copied: {1}.", outcome.failed_file, outcome.failure_detail);
   }

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

         // Checked, which it was not - and until Configuration::XMLStore was made
         // capable of answering false there was nothing here to check. The two went
         // together: a settings section that could not fail, called by a caller that
         // would not have listened.
         if (!Configuration::Instance()->XMLStore(pBackupNode))
         {
            Application::Instance()->GetBackupManager()->OnBackupFailed("The server settings could not be written to the backup. Check the hMailServer error log. No archive has been created.");
            return false;
         }
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

      // The archive is read back before it is called a backup.
      //
      // Everything above checks that each step reported success. None of it checks
      // that the result can be opened - and "reported success but cannot be opened"
      // is exactly what a destination that ran out of space part-way through a write
      // produces, along with an archive on a share that was interrupted, and one
      // 7za's exit code called a warning rather than an error. An archive nobody has
      // ever opened is not a backup; the trailing zero in 3-2-1-1-0 is "verified
      // restores", and this is the smallest honest version of it.
      //
      // It happens before retention on purpose. An unverifiable archive must never be
      // the reason an older archive that could be restored was deleted.
      if (!VerifyArchive_(sZipFile))
      {
         DiscardIncompleteArchive_(sZipFile, zipExistedBeforeThisRun);
         return false;
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

      // See the comment at the top of this file for why this is not
      // FileUtilities::CopyDirectory any more. In short: that call terminated the
      // service if a message file was deleted while the copy was running, or if a
      // DataBackup folder from an earlier run was still there.
      TreeCopyOutcome outcome;

      bool bResult = CopyTreeGuarded_(sDataDir, sDataBackupDir, outcome);
      if (!bResult)
      {
         Logger::Instance()->LogBackup("Failed to copy data directory. Details: " + DescribeCopyFailure_(outcome));
         return bResult;
      }

      if (outcome.vanished > 0)
      {
         // Worth a line, and worth being honest about what it means: a backup of a
         // running server is not a snapshot. The message rows were read out of the
         // database before this copy started, so a message deleted in between leaves
         // a row in the archive whose file is not in it, and after a restore that
         // message is listed but cannot be fetched. Small numbers are simply what
         // mail flow looks like; a large number means the backup was taken during
         // something like a bulk expunge and is worth taking again.
         String line;
         line.Format(_T("Backed up %u file(s). %u file(s) were deleted by the running server while the copy was in progress and are not in this backup."),
            outcome.copied, outcome.vanished);

         Logger::Instance()->LogBackup(line);
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
   BackupExecuter::VerifyArchive_(const String &sZipFile)
   {
      Logger::Instance()->LogBackup("Verifying the archive...");

      int archiveContents = 0;
      String archiveVersion;
      String failureReason;

      // Deliberately the same code path a restore uses to open an archive, rather
      // than a private check of its own. A verification that passes where a restore
      // would fail is worth nothing, and the only way to keep the two honest is for
      // them to be one function.
      if (!BackupRestorer::ReadArchiveIndex(sZipFile, archiveContents, archiveVersion, failureReason))
      {
         Application::Instance()->GetBackupManager()->OnBackupFailed(
            "The backup was written but could not be read back afterwards, so it is not a usable backup. " + failureReason);

         return false;
      }

      if (archiveContents != backup_mode_)
      {
         // The index survived but does not say what this run put in it, which means
         // the archive on disk is not the archive this run intended to write.
         String message;
         message.Format(_T("The backup was written but reads back as containing %d rather than %d, so its index does not describe what was backed up. It is not a usable backup."),
            archiveContents, backup_mode_);

         Application::Instance()->GetBackupManager()->OnBackupFailed(message);

         return false;
      }

      return true;
   }

   bool
   BackupExecuter::StartRestore(std::shared_ptr<Backup> pBackup)
   {
      bool bMessagesDBOnly = IniFileSettings::Instance()->GetBackupMessagesDBOnly();

      Logger::Instance()->LogBackup("Reading XML file...");

      // PHASE ONE: everything that can be established without changing anything.
      //
      // A restore has no transaction and nothing can undo it, so every question that
      // has an answer before the first deletion has to be asked before the first
      // deletion - including "is the message store this restore needs actually
      // inside the archive?", which is what used to be asked forty lines too late.
      // See BackupRestorer for the four ways that ended in a server with nothing on
      // it. A false return here means the server has not been touched.
      BackupRestorer restorer;
      String failureReason;

      if (!restorer.Prepare(pBackup, failureReason))
      {
         ReportRestoreFailure_(failureReason);
         return false;
      }

      XNode *pBackupNode = restorer.GetBackupNode();
      int iRestoreOptions = restorer.GetRestoreOptions();

      // PHASE TWO: the destructive part. From here on, a failure leaves the server
      // part-way through a restore, which is why nothing that could have been
      // checked is checked below.
      Logger::Instance()->LogBackup("Backup archive accepted. Starting the restore...");

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
         {
            // Checked, unlike before, and this is the one place where stopping on a
            // failed deletion is better than carrying on. Collection::XMLLoad also
            // begins by calling DeleteAll, and when that fails it returns *true* and
            // loads nothing - so a restore that could not clear the existing domains
            // used to be reported as having completed successfully against a server
            // it had emptied. (That "return true" is in Collection.h and is not this
            // change's to make; refusing here means it cannot be reached this way.)
            if (!pDomains->DeleteAll())
            {
               ReportRestoreFailure_("Restore failed: the domains that are on this server now could not be deleted, so the backup's domains cannot be put in their place. The restore has been stopped part-way through - some domains may already have been removed. Check the hMailServer error log, then restore again.");
               return false;
            }
         }

         // We need to do the same with public folders - and to check it, which this
         // did not, one line below the domain delete that does. The consequence is
         // not symmetrical with the domains case: XMLLoad puts the backup's public
         // folders in on top of whatever survived, so a failed delete leaves the
         // server holding folders the backup never contained, alongside duplicates
         // of the ones it did, and reports a successful restore over the top of it.
         if (iRestoreOptions & Backup::BOSettings && !bMessagesDBOnly)
         {
            if (!Configuration::Instance()->GetIMAPConfiguration()->GetPublicFolders()->DeleteAll())
            {
               ReportRestoreFailure_("Restore failed: the public folders that are on this server now could not be deleted, so the backup's public folders cannot be put in their place. The restore has been stopped part-way through - the domains have already been removed. Check the hMailServer error log, then restore again.");
               return false;
            }
         }

         // Should we restore messages as well? Prepare staged the message store
         // exactly when the options and BackupMessagesDBOnly call for one, so asking
         // it is the same test as before and cannot disagree with what was staged.
         if (restorer.GetHasStagedMessageStore())
         {
            Logger::Instance()->LogBackup("Restoring data directory...");

            if (!RestoreDataDirectory_(restorer))
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
   BackupExecuter::RestoreDataDirectory_(BackupRestorer &restorer)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Replaces the live data directory with the message store the restorer has
   // already put on local disk.
   //
   // Everything that used to happen here before the deletion - working out where the
   // messages are, extracting them, confirming they exist - has moved into
   // BackupRestorer::Prepare, which runs before any domain is deleted. What is left
   // is the part that cannot be made safe by ordering: between the delete and the
   // end of the copy there is no complete copy of the mail store on this server, only
   // the one in the staging directory.
   //---------------------------------------------------------------------------()
   {
      String sDirContainingDataFiles = restorer.GetStagedMessageStore();

      // Delete all directories from the data directory
      // so that we're sure that we're doing a clean restore
      String sDataDirectory = IniFileSettings::Instance()->GetDataDirectory();

      FileUtilities::DeleteFilesInDirectory(sDataDirectory);
      FileUtilities::DeleteDirectoriesInDirectory(sDataDirectory);

      TreeCopyOutcome outcome;
      bool copied = CopyTreeGuarded_(sDirContainingDataFiles, sDataDirectory, outcome);

      if (!copied)
      {
         // The data directory has already been emptied, so the staged copy is
         // deliberately kept: it is the only copy of the messages that still exists,
         // and the administrator is told where it is. Without this the restorer's
         // destructor would delete it on the way out - which is what the previous
         // version of this code got right and is the reason RetainStagedFiles exists.
         restorer.RetainStagedFiles();

         ReportRestoreFailure_("Restore failed while copying messages into " + sDataDirectory + ". " + DescribeCopyFailure_(outcome) +
                               " The messages from the backup have been left in " + sDirContainingDataFiles +
                               " - do not delete that folder until they have been recovered.");
         return false;
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
