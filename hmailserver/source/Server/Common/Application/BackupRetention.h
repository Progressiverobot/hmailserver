// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Util/VariantDateTime.h"

namespace HM
{
   // One backup archive that hMailServer itself wrote into a backup destination.
   // The timestamp comes from the file name rather than from the file system -
   // see BackupRetention::ParseArchiveName for why that distinction is the
   // difference between deleting the right file and deleting the wrong one.
   struct BackupArchive
   {
      String file_name;
      DateTime created;
   };

   // Deletes old backup archives from a backup destination according to the
   // retention settings in hMailServer.ini ([Settings] ScheduledBackupKeepCount
   // and ScheduledBackupMaxAgeDays). Both default to 0, which means "keep
   // everything": a default installation therefore never deletes a backup, which
   // is the only defensible default for a feature whose failure mode is losing
   // the only copy of the mail store.
   //
   // Retention exists because a backup schedule without one fills the
   // destination, and a destination with no free space produces a *failed*
   // backup - the schedule quietly stops working at the moment it is needed
   // most. It is deliberately kept out of the scheduling code so that it also
   // applies to a manual backup taken over COM: a policy that only bounded the
   // scheduled runs would stop bounding the directory as soon as somebody took a
   // backup by hand into the same place, which is exactly when the destination is
   // fullest.
   //
   // Four rules are enforced here rather than left to the caller, because each of
   // them is a way this class could destroy data if it were got wrong:
   //
   //  1. Nothing is deleted until the replacement backup has completed
   //     successfully. Apply() is called from the single point in
   //     BackupExecuter::StartBackup that every failure path has already
   //     returned past, and it is handed the name of the archive that run
   //     produced so that archive can never become a deletion candidate.
   //     Pruning first and writing afterwards is not retention, it is data loss:
   //     a destination with room for one more archive but not two would then have
   //     had yesterday's deleted and today's fail to write, leaving nothing.
   //
   //  2. The newest archive in the destination is never deleted, whatever the
   //     policy says. In normal operation the newest archive *is* the protected
   //     one, so this is belt-and-braces - but it is the guard that holds if
   //     Apply() is ever called from somewhere that has no archive to protect.
   //
   //  3. The age rule never touches the newest AGE_RULE_FLOOR archives (see the
   //     .cpp). "Older than N days" is a statement about the wall clock, and the
   //     wall clock is the one input here that can be wrong by years: a VM
   //     restored from a snapshot, a dead CMOS battery or a bad NTP step can all
   //     make every archive in the directory look ancient at once. A count is
   //     not affected by a wrong clock, so ScheduledBackupKeepCount is allowed to
   //     take the directory down to a single archive; ScheduledBackupMaxAgeDays
   //     is not.
   //
   //  4. Only files whose names we generate are ever considered. A backup
   //     destination is very often a directory in which an administrator also
   //     keeps their own copies, and this class never opens a file it is about to
   //     delete, so the name is the whole of the evidence. Same principle as
   //     LogRetentionTask only ever deleting hmailserver_*.log.
   class BackupRetention
   {
   public:

      // True when fileName is exactly one that BackupExecuter::StartBackup
      // generates - "HMBackup YYYY-MM-DD HHMMSS.7z" - and the embedded local
      // timestamp describes a real instant, which is then returned in created.
      //
      // The timestamp is read out of the name rather than from the file's
      // creation or modification time because file-system times do not survive
      // what administrators do to backups: copying an archive to another volume,
      // restoring it from a snapshot or letting a sync client re-create it all
      // reset the creation time to now. That would make the oldest backup in the
      // directory look like the newest one and hand the retention pass precisely
      // the wrong file to delete. The name is written once, by us, and never
      // changes afterwards.
      static bool ParseArchiveName(const String &fileName, DateTime &created);

      // Every archive in directory whose name we generated, oldest first.
      // Anything else - an administrator's own archives, the uncompressed
      // DataBackup folder, a half-finished download - does not match and is
      // therefore never a deletion candidate.
      static std::vector<BackupArchive> ListArchives(const String &directory);

      // Applies the retention policy to directory. protectedFile is the archive
      // the just-completed backup produced (a full path or a bare file name;
      // either is accepted) and is never deleted.
      //
      // Failing to delete an old archive is reported but does not fail the
      // backup: the backup itself succeeded, and turning a stale file that
      // something else has open into a backup failure would hide that.
      static void Apply(const String &directory, const String &protectedFile, int keepCount, int maxAgeDays);
   };
}
