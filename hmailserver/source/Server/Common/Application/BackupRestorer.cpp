// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"

#include "BackupRestorer.h"

#include "Backup.h"

#include "..\Util\Compression.h"
#include "..\Util\Utilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // The one file inside every archive that says what the archive is. Written by
   // BackupExecuter::StartBackup and, before this change, extracted to a fixed path
   // in the temp directory by three different callers - see ExtractIndex_.
   const String BACKUP_INDEX_FILE_NAME = _T("hMailServerBackup.xml");

   // The folder name the message store is stored under, both inside a compressed
   // archive and beside an uncompressed one. Generated in BackupExecuter.
   const String BACKUP_DATA_FOLDER_NAME = _T("DataBackup");

   static bool
   AllDigits_(const String &value)
   {
      if (value.IsEmpty())
         return false;

      for (int index = 0; index < value.GetLength(); index++)
      {
         wchar_t character = value.SafeGetAt((unsigned int) index);

         if (character < L'0' || character > L'9')
            return false;
      }

      return true;
   }

   static bool
   ParseVersion_(const String &value, int &major, int &minor, int &revision)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Splits an hMailServer version string into its three numbers. The strings this
   // sees are whatever Application::GetVersionNumber produced on the machine that
   // took the backup - "6.2.18-B20" today, "5.6.6-B2383" from a stock 5.x - so the
   // build suffix is dropped and only the version itself is compared. Two builds of
   // one version restore into each other, which is the point: a build number rising
   // is not a schema change.
   //---------------------------------------------------------------------------()
   {
      String numeric = value;

      int dash = numeric.Find(_T("-"));
      if (dash >= 0)
         numeric = numeric.Left(dash);

      std::vector<String> parts = StringParser::SplitString(numeric, _T("."));
      if (parts.size() < 3)
         return false;

      if (!AllDigits_(parts[0]) || !AllDigits_(parts[1]) || !AllDigits_(parts[2]))
         return false;

      major = _ttoi(parts[0].c_str());
      minor = _ttoi(parts[1].c_str());
      revision = _ttoi(parts[2].c_str());

      return true;
   }

   static bool
   ExtractIndex_(const String &archiveFile, const String &targetDirectory, String &failureReason)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Pulls hMailServerBackup.xml out of an archive into a directory of this run's
   // own, and says why if it could not.
   //
   // The directory is the caller's unique temporary one rather than the temp
   // directory itself, which is a fix in its own right. Every caller used to extract
   // this file to the same fixed path, <temp>\hMailServerBackup.xml, and delete it
   // afterwards: BackupManager::LoadBackup, which any COM client may call at any
   // time and which two administrators may call at once, and
   // BackupExecuter::StartRestore, which does it while a restore is under way. Two
   // of them overlapping meant one read the other's index - a restore of archive A
   // driven by the contents of archive B, or by nothing at all if the other caller
   // had already deleted the file. It never had to be a shared path.
   //---------------------------------------------------------------------------()
   {
      if (!FileUtilities::CreateDirectory(targetDirectory))
      {
         failureReason = "The temporary directory " + targetDirectory + " could not be created, so the backup archive could not be examined.";
         return false;
      }

      Compression compression;

      // Checked, unlike before. LoadBackup discarded this result and then read the
      // file it was meant to have produced, so a truncated or half-copied archive -
      // the exact case a restore has to survive - produced an empty read and a
      // Backup object that claimed to contain nothing at all, with no error
      // anywhere.
      if (!compression.Uncompress(archiveFile, targetDirectory, BACKUP_INDEX_FILE_NAME))
      {
         failureReason = Formatter::Format("The backup archive {0} could not be read. 7za could not extract {1} from it, which is what a truncated, incomplete or corrupt archive looks like. Nothing has been changed on this server.",
            archiveFile, BACKUP_INDEX_FILE_NAME);
         return false;
      }

      if (!FileUtilities::Exists(FileUtilities::Combine(targetDirectory, BACKUP_INDEX_FILE_NAME)))
      {
         // 7za reports success for "nothing matched the wildcard", so the absence of
         // the file has to be checked separately from the exit code. This is what an
         // archive that is readable but is not an hMailServer backup looks like.
         failureReason = Formatter::Format("The archive {0} does not contain {1}, so it is not an hMailServer backup. Nothing has been changed on this server.",
            archiveFile, BACKUP_INDEX_FILE_NAME);
         return false;
      }

      return true;
   }

   BackupRestorer::BackupRestorer() :
      backup_node_(0),
      archive_contents_(0),
      restore_options_(0),
      retain_staged_files_(false)
   {
   }

   BackupRestorer::~BackupRestorer()
   {
      // The index directory holds one small XML file and is always ours to remove.
      if (!index_directory_.IsEmpty())
         FileUtilities::DeleteDirectory(index_directory_, true);

      // The staging directory is not, if the restore got far enough to empty the
      // live data directory: what is in here is then the only copy of the mail that
      // exists anywhere. staging_directory_ is only ever set to a directory this
      // object created, so an uncompressed backup's own DataBackup folder - which
      // lives beside the archive and is part of the backup - is never a candidate.
      if (retain_staged_files_)
         return;

      if (!staging_directory_.IsEmpty())
         FileUtilities::DeleteDirectory(staging_directory_, true);
   }

   bool
   BackupRestorer::VersionIsRestorable(const String &archiveVersion, String &failureReason)
   {
      int archiveMajor = 0;
      int archiveMinor = 0;
      int archiveRevision = 0;

      if (!ParseVersion_(archiveVersion, archiveMajor, archiveMinor, archiveRevision))
      {
         // Accepted deliberately. A version we cannot parse is not evidence of a
         // newer schema - it is evidence of an unfamiliar build - and refusing every
         // archive whose provenance cannot be proved would make this the reason
         // somebody could not restore their mail.
         return true;
      }

      int runningMajor = 0;
      int runningMinor = 0;
      int runningRevision = 0;

      if (!ParseVersion_(Application::Instance()->GetVersionNumber(), runningMajor, runningMinor, runningRevision))
         return true;

      bool newer = false;

      if (archiveMajor != runningMajor)
         newer = archiveMajor > runningMajor;
      else if (archiveMinor != runningMinor)
         newer = archiveMinor > runningMinor;
      else
         newer = archiveRevision > runningRevision;

      if (!newer)
         return true;

      // Refused rather than warned, and there is deliberately no setting to
      // override it.
      //
      // A restore writes the archive's XML straight into this server's database
      // through the same Save path an administrator's edits use. A newer archive can
      // carry properties this build has no column for and objects it has no table
      // for; those are silently dropped, and what is left is written as if it were
      // complete. The administrator is then told the restore succeeded and is
      // holding a server that is missing settings nobody can enumerate. Upgrading
      // first is always available and always safe, so an override could only ever be
      // used to do the unsafe thing.
      failureReason = Formatter::Format("This backup was taken by hMailServer {0}, which is newer than the {1} running here. Restoring it would silently discard anything the newer version stores and this one does not. Upgrade this server to at least version {0} and restore again. Nothing has been changed on this server.",
         archiveVersion, Application::Instance()->GetVersionNumber());

      return false;
   }

   bool
   BackupRestorer::ReadIndex_(const String &archiveFile, String &failureReason)
   {
      if (archiveFile.IsEmpty())
      {
         failureReason = "No backup file was specified.";
         return false;
      }

      if (!FileUtilities::Exists(archiveFile))
      {
         failureReason = "The backup file " + archiveFile + " does not exist. Nothing has been changed on this server.";
         return false;
      }

      index_directory_ = Utilities::GetUniqueTempDirectory();

      if (!ExtractIndex_(archiveFile, index_directory_, failureReason))
         return false;

      String indexFile = FileUtilities::Combine(index_directory_, BACKUP_INDEX_FILE_NAME);
      String xml = FileUtilities::ReadCompleteTextFile(indexFile);

      if (xml.IsEmpty())
      {
         failureReason = "The backup index " + indexFile + " is empty, so the archive cannot be restored. Nothing has been changed on this server.";
         return false;
      }

      document_.Load(xml);

      backup_node_ = document_.GetChild(_T("Backup"));
      if (!backup_node_)
      {
         failureReason = "The backup index in " + archiveFile + " has no Backup element, so it is not a valid hMailServer backup. Nothing has been changed on this server.";
         return false;
      }

      // Every one of these was dereferenced without a check. GetChildAttr returns
      // null both for a missing child and for a missing attribute, and an index that
      // was truncated while it was being written parses into a Backup element with
      // no children at all.
      XNode *informationNode = backup_node_->GetChild(_T("BackupInformation"));
      if (!informationNode)
      {
         failureReason = "The backup index in " + archiveFile + " has no BackupInformation element, so what the archive contains cannot be established. Nothing has been changed on this server.";
         return false;
      }

      LPXAttr modeAttribute = informationNode->GetAttr(_T("Mode"));
      if (!modeAttribute)
      {
         failureReason = "The backup index in " + archiveFile + " does not record what was backed up (no Mode attribute), so it cannot safely be restored. Nothing has been changed on this server.";
         return false;
      }

      archive_contents_ = _ttoi(modeAttribute->value.c_str());

      if ((archive_contents_ & (Backup::BOSettings | Backup::BODomains | Backup::BOMessages)) == 0)
      {
         failureReason = Formatter::Format("The backup index in {0} says the archive contains nothing that can be restored (Mode={1}). Nothing has been changed on this server.",
            archiveFile, archive_contents_);
         return false;
      }

      // Absent in principle only - hMailServer has written it since 2010 - but an
      // absent version has to mean "unknown", not "version 0", or the check below
      // would treat the oldest possible archive as the newest.
      LPXAttr versionAttribute = informationNode->GetAttr(_T("Version"));
      if (versionAttribute)
         archive_version_ = versionAttribute->value;

      return true;
   }

   bool
   BackupRestorer::ReadArchiveIndex(const String &archiveFile, int &archiveContents, String &archiveVersion, String &failureReason)
   {
      // A local instance so that the temporary directory this creates is removed by
      // its destructor on every exit path, including the failure ones.
      BackupRestorer reader;

      if (!reader.ReadIndex_(archiveFile, failureReason))
         return false;

      archiveContents = reader.archive_contents_;
      archiveVersion = reader.archive_version_;

      return true;
   }

   bool
   BackupRestorer::Prepare(std::shared_ptr<Backup> backup, String &failureReason)
   {
      if (!backup)
      {
         failureReason = "No backup was supplied to restore.";
         return false;
      }

      String archiveFile = backup->GetBackupFile();

      if (!ReadIndex_(archiveFile, failureReason))
         return false;

      if (!VersionIsRestorable(archive_version_, failureReason))
         return false;

      restore_options_ = backup->GetRestoreOptions();

      const int selectable = Backup::BOSettings | Backup::BODomains | Backup::BOMessages;

      if ((restore_options_ & selectable) == 0)
      {
         failureReason = "The restore was started with nothing selected to restore. Choose settings, domains or messages and start it again.";
         return false;
      }

      // The subset check. Each of these is a way the server used to delete
      // everything in a category and then find it had nothing to put back, because
      // Collection::XMLLoad cannot tell "this collection was empty when the backup
      // was taken" from "this archive does not contain this collection at all".
      if ((restore_options_ & Backup::BODomains) && !(archive_contents_ & Backup::BODomains))
      {
         failureReason = "Restore refused: this backup does not contain any domains, so restoring domains from it would delete every domain, account and alias on this server and put nothing back. Clear the domains option, or use a backup that was taken with domains included. Nothing has been changed on this server.";
         return false;
      }

      if ((restore_options_ & Backup::BOSettings) && !(archive_contents_ & Backup::BOSettings))
      {
         failureReason = "Restore refused: this backup does not contain the server settings, so restoring settings from it would delete the SSL certificates, TCP/IP ports, IP ranges, global rules, blocked attachments and anti-spam lists and put nothing back. Clear the settings option, or use a backup that was taken with settings included. Nothing has been changed on this server.";
         return false;
      }

      if ((restore_options_ & Backup::BOMessages) && !(archive_contents_ & Backup::BOMessages))
      {
         failureReason = "Restore refused: this backup does not contain any messages. Clear the messages option, or use a backup that was taken with messages included. Nothing has been changed on this server.";
         return false;
      }

      // Messages live inside the domain tree, so the message store was only ever
      // restored as part of restoring domains. Asking for messages without domains
      // silently did nothing at all, which is the worst of the three possible
      // behaviours - the option looked as though it had worked.
      if ((restore_options_ & Backup::BOMessages) && !(restore_options_ & Backup::BODomains))
      {
         failureReason = "Restore refused: messages can only be restored together with the domains they belong to. Select domains as well, or clear the messages option. Nothing has been changed on this server.";
         return false;
      }

      // BackupMessagesDBOnly restores the message rows out of the domain XML and
      // never touches a file, so there is nothing to stage and no message store to
      // require inside the archive.
      if ((restore_options_ & Backup::BOMessages) && !IniFileSettings::Instance()->GetBackupMessagesDBOnly())
      {
         if (!StageMessageStore_(archiveFile, failureReason))
            return false;
      }

      return true;
   }

   bool
   BackupRestorer::StageMessageStore_(const String &archiveFile, String &failureReason)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Gets the message store onto local disk, ready to be copied into the data
   // directory, and fails the restore here if it cannot - while the server is still
   // intact. This work used to happen after every domain had been deleted, so a
   // temp volume with no room left, an archive that only extracts halfway, or an
   // archive with no message store in it all produced the same outcome: a server
   // with nothing on it.
   //---------------------------------------------------------------------------()
   {
      XNode *informationNode = backup_node_->GetChild(_T("BackupInformation"));

      // ReadIndex_ has already established this, but the null check is what makes
      // the dereference below safe to read at a glance rather than safe by argument.
      if (!informationNode)
      {
         failureReason = "The backup index has no BackupInformation element.";
         return false;
      }

      LPXAttr formatAttribute = informationNode->GetChildAttr(_T("DataFiles"), _T("Format"));

      if (!formatAttribute)
      {
         // THE null dereference. Mode said the archive contains messages, so the
         // caller was allowed to ask for them, but the DataFiles element that says
         // where they are is missing - which is what an index truncated after the
         // Mode attribute looks like.
         failureReason = "Restore refused: this backup claims to contain messages but its index does not say where they are (no DataFiles element), so the message store cannot be located. Nothing has been changed on this server.";
         return false;
      }

      String format = formatAttribute->value;

      if (format.CompareNoCase(_T("7Z")) == 0)
      {
         staging_directory_ = Utilities::GetUniqueTempDirectory();

         if (!FileUtilities::CreateDirectory(staging_directory_))
         {
            failureReason = "Restore refused: the temporary directory " + staging_directory_ + " could not be created, so the messages could not be extracted. Nothing has been changed on this server.";
            return false;
         }

         Compression compression;
         if (!compression.Uncompress(archiveFile, staging_directory_))
         {
            failureReason = "Restore refused: the messages could not be extracted from " + archiveFile + ". Nothing has been changed on this server.";
            return false;
         }

         staged_message_store_ = FileUtilities::Combine(staging_directory_, BACKUP_DATA_FOLDER_NAME);
      }
      else if (format.CompareNoCase(_T("Raw")) == 0)
      {
         LPXAttr folderAttribute = informationNode->GetChildAttr(_T("DataFiles"), _T("FolderName"));

         if (!folderAttribute)
         {
            failureReason = "Restore refused: this backup stores its messages uncompressed but its index does not say in which folder (no FolderName attribute). Nothing has been changed on this server.";
            return false;
         }

         // Beside the archive, not inside it, and therefore part of the backup.
         // Never staged and never deleted by this object.
         String archiveDirectory = FileUtilities::GetFilePath(archiveFile);
         staged_message_store_ = FileUtilities::Combine(archiveDirectory, folderAttribute->value);
      }
      else
      {
         failureReason = Formatter::Format("Restore refused: the backup index describes its message store in a format this version does not understand (Format=\"{0}\"). Nothing has been changed on this server.",
            format);
         return false;
      }

      // The check that used to run after the live data directory had already been
      // deleted, where its own message - "the existing data directory has not been
      // touched" - was no longer true.
      if (!FileUtilities::DirectoryExists(staged_message_store_))
      {
         failureReason = "Restore refused: the backup does not contain a message store (" + staged_message_store_ +
            " does not exist). Nothing has been changed on this server.";
         return false;
      }

      return true;
   }

   XNode *
   BackupRestorer::GetBackupNode()
   {
      return backup_node_;
   }

   int
   BackupRestorer::GetRestoreOptions() const
   {
      return restore_options_;
   }

   bool
   BackupRestorer::GetHasStagedMessageStore() const
   {
      return !staged_message_store_.IsEmpty();
   }

   String
   BackupRestorer::GetStagedMessageStore() const
   {
      return staged_message_store_;
   }

   void
   BackupRestorer::RetainStagedFiles()
   {
      retain_staged_files_ = true;
   }
}
