// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "BackupRetention.h"

#include "Logger.h"

#include "../Util/FileInfo.h"
#include "../Util/FileUtilities.h"

#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // "HMBackup " + "YYYY-MM-DD" + " " + "HHMMSS" + ".7z"
   //  0        8    9        18   19    20    25   26 28
   //
   // Generated in exactly one place, BackupExecuter::StartBackup, as
   //    sZipFile.Format(_T("%s\\HMBackup %s.7z"), destination, Time::GetCurrentDateTime() with its colons removed)
   // and Time::GetCurrentDateTime() is "%d-%.02d-%.02d %.02d:%.02d:%.02d" of the
   // local time. If that format is ever changed, this parser stops recognising
   // our own archives - and the visible symptom is not a crash but retention
   // silently doing nothing and BackupScheduleTask deciding on every service
   // start that no backup has ever been taken. Change the two together.
   const int ARCHIVE_NAME_LENGTH = 29;
   const int ARCHIVE_DATE_OFFSET = 9;
   const int ARCHIVE_TIME_OFFSET = 20;

   // How many of the newest archives the age rule is not allowed to touch. See
   // rule 3 in the header: two means a successful backup always leaves at least
   // one earlier generation behind, so a clock that has jumped forwards by years
   // cannot reduce the destination to the single archive it happens to be writing
   // at the time. ScheduledBackupKeepCount is deliberately not subject to this -
   // a count is not something a wrong clock can corrupt, so an administrator who
   // asks to keep exactly one archive gets exactly one.
   const size_t ARCHIVE_AGE_RULE_FLOOR = 2;

   static bool
   IsDigitAt_(const String &value, int offset)
   {
      // SafeGetAt, not GetAt: GetAt is std::basic_string::at and throws. The
      // length is checked before any of these calls, so this is defence against a
      // future edit to the offsets rather than against the current caller.
      wchar_t character = value.SafeGetAt((unsigned int) offset);

      return character >= L'0' && character <= L'9';
   }

   bool
   BackupRetention::ParseArchiveName(const String &fileName, DateTime &created)
   {
      if (fileName.GetLength() != ARCHIVE_NAME_LENGTH)
         return false;

      // StartsWith and EndsWith compare case-insensitively, which is what is
      // wanted here: the archive lives on a case-insensitive file system, and one
      // that came back from a third-party restore tool as "hmbackup ....7Z" is
      // still ours.
      if (!fileName.StartsWith(_T("HMBackup ")))
         return false;

      if (!fileName.EndsWith(_T(".7z")))
         return false;

      if (fileName.SafeGetAt((unsigned int) (ARCHIVE_DATE_OFFSET + 4)) != L'-')
         return false;

      if (fileName.SafeGetAt((unsigned int) (ARCHIVE_DATE_OFFSET + 7)) != L'-')
         return false;

      if (fileName.SafeGetAt((unsigned int) (ARCHIVE_TIME_OFFSET - 1)) != L' ')
         return false;

      // The digit positions inside "YYYY-MM-DD", relative to the date offset.
      const int digitOffsets[] = { 0, 1, 2, 3, 5, 6, 8, 9 };
      for (int index = 0; index < 8; index++)
      {
         if (!IsDigitAt_(fileName, ARCHIVE_DATE_OFFSET + digitOffsets[index]))
            return false;
      }

      for (int index = 0; index < 6; index++)
      {
         if (!IsDigitAt_(fileName, ARCHIVE_TIME_OFFSET + index))
            return false;
      }

      int year = _ttoi(fileName.Mid(ARCHIVE_DATE_OFFSET, 4).c_str());
      int month = _ttoi(fileName.Mid(ARCHIVE_DATE_OFFSET + 5, 2).c_str());
      int day = _ttoi(fileName.Mid(ARCHIVE_DATE_OFFSET + 8, 2).c_str());
      int hour = _ttoi(fileName.Mid(ARCHIVE_TIME_OFFSET, 2).c_str());
      int minute = _ttoi(fileName.Mid(ARCHIVE_TIME_OFFSET + 2, 2).c_str());
      int second = _ttoi(fileName.Mid(ARCHIVE_TIME_OFFSET + 4, 2).c_str());

      // Rejects 2026-02-30 and friends. A name whose digits are in the right
      // places but do not describe a real instant is not a name we wrote, and an
      // invalid DateTime compares as neither older nor newer than anything, which
      // would make it sort unpredictably against the real archives and so make
      // "the oldest" unpredictable too.
      DateTime parsed;
      if (parsed.SetDateTime(year, month, day, hour, minute, second) != DateTime::valid)
         return false;

      created = parsed;

      return true;
   }

   static bool
   IsOlder_(const BackupArchive &first, const BackupArchive &second)
   {
      // Written as three statements rather than one expression because DateTime's
      // comparison operators return BOOL, and returning a BOOL from a bool
      // function is exactly the int-to-bool conversion the build treats as an
      // error.
      if (first.created < second.created)
         return true;

      if (second.created < first.created)
         return false;

      // Identical to the second. Two of our archives cannot normally share a name
      // at all, but the ordering still has to be total and repeatable or std::sort
      // is entitled to do anything it likes with them, including reading out of
      // bounds.
      return first.file_name.compare(second.file_name) < 0;
   }

   std::vector<BackupArchive>
   BackupRetention::ListArchives(const String &directory)
   {
      std::vector<BackupArchive> result;

      if (directory.IsEmpty())
         return result;

      // ".*" and then filter with ParseArchiveName, rather than encoding the name
      // a second time as a regular expression. One description of what one of our
      // archives is called, in one place, is what makes the "never touch a file we
      // did not write" rule checkable - and a second, subtly different pattern
      // here is how that rule would come to be broken. The cost is one regular
      // expression match per entry in the top level of the destination, which
      // holds a handful of archives, the XML index and at most one DataBackup
      // folder; the enumeration is not recursive.
      std::vector<FileInfo> files = FileUtilities::GetFilesInDirectory(directory, _T(".*"));

      for (FileInfo &file : files)
      {
         BackupArchive archive;
         archive.file_name = file.GetName();

         if (!ParseArchiveName(archive.file_name, archive.created))
            continue;

         result.push_back(archive);
      }

      std::sort(result.begin(), result.end(), IsOlder_);

      return result;
   }

   void
   BackupRetention::Apply(const String &directory, const String &protectedFile, int keepCount, int maxAgeDays)
   {
      // Retention disabled, which is the shipped default. Deliberately not logged:
      // this runs after every successful backup on every installation, and a line
      // saying nothing happened would be in every backup log in the field.
      if (keepCount <= 0 && maxAgeDays <= 0)
         return;

      if (directory.IsEmpty())
         return;

      std::vector<BackupArchive> archives = ListArchives(directory);

      // One archive is the one this backup just wrote, so there is nothing older
      // to remove. Also the cheap form of rule 2.
      if (archives.size() <= 1)
         return;

      String protectedName = FileUtilities::GetFileNameFromFullPath(protectedFile);

      // How many of the oldest entries are over the count limit. Computed once,
      // from the full listing, rather than while deleting: if it were recomputed
      // as files disappeared, an archive that could not be deleted would move the
      // boundary and take an extra archive with it.
      size_t overCountThrough = 0;
      if (keepCount > 0 && archives.size() > (size_t) keepCount)
         overCountThrough = archives.size() - (size_t) keepCount;

      DateTime ageCutoff;
      if (maxAgeDays > 0)
      {
         DateTimeSpan window;
         window.SetDateTimeSpan(maxAgeDays, 0, 0, 0);

         ageCutoff = DateTime::GetCurrentTime() - window;
      }

      int deletedCount = 0;
      int failedCount = 0;
      String firstFailure;

      // "index + 1 < size", so the newest entry is never a candidate - rule 2.
      for (size_t index = 0; index + 1 < archives.size(); index++)
      {
         const BackupArchive &archive = archives[index];

         // Rule 1. The archive this backup produced is never deleted by the
         // retention pass that same backup triggered, whatever the policy makes of
         // its timestamp.
         if (protectedName.CompareNoCase(archive.file_name.c_str()) == 0)
            continue;

         bool overCount = index < overCountThrough;

         // Rule 3: the age rule stops short of the newest ARCHIVE_AGE_RULE_FLOOR
         // archives. The comparison is written as a ternary because DateTime's
         // operator< returns BOOL.
         bool ageRuleApplies = maxAgeDays > 0 && (index + ARCHIVE_AGE_RULE_FLOOR < archives.size());
         bool tooOld = ageRuleApplies && (archive.created < ageCutoff ? true : false);

         if (!overCount && !tooOld)
            continue;

         String fullPath = FileUtilities::Combine(directory, archive.file_name);

         if (FileUtilities::DeleteFile(fullPath))
         {
            deletedCount++;

            String line;
            line.Format(_T("Backup retention: deleted %s (%s)."),
               fullPath.c_str(),
               overCount ? _T("over the keep count") : _T("older than the retention window"));

            Logger::Instance()->LogBackup(line);
         }
         else
         {
            failedCount++;

            if (firstFailure.IsEmpty())
               firstFailure = fullPath;

            String line;
            line.Format(_T("Backup retention: could not delete %s."), fullPath.c_str());

            Logger::Instance()->LogBackup(line);
         }
      }

      if (deletedCount > 0)
      {
         String message;
         message.Format(_T("Backup retention: deleted %d old backup archive(s) from %s. %Iu archive(s) remain."),
            deletedCount, directory.c_str(), archives.size() - (size_t) deletedCount);

         LOG_APPLICATION(message);
      }

      if (failedCount > 0)
      {
         // Worth an entry in the error log rather than only a line in the backup
         // log. The backup itself succeeded, so nothing else will draw attention
         // to a destination that is now growing without bound until it fills up
         // and the backups start failing instead - by which point the useful
         // message is weeks old.
         //
         // Cannot fire on the shipped default configuration: it is only reachable
         // once ScheduledBackupKeepCount or ScheduledBackupMaxAgeDays has been
         // set, and both are absent in a default installation. (A directory that
         // happens to be *named* like one of our archives would also land here,
         // since DeleteFile cannot remove a directory. That is the correct
         // outcome - we do not delete directories in the destination - and the
         // message tells the administrator which path to look at.)
         String message;
         message.Format(_T("%d old backup archive(s) in %s could not be deleted, starting with %s. The backup itself completed successfully, but the retention policy is not being applied, so the destination will keep growing. Check that hMailServer's service account may delete files there and that no other process is holding the file open."),
            failedCount, directory.c_str(), firstFailure.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5885, "BackupRetention::Apply", message);
      }
   }
}
