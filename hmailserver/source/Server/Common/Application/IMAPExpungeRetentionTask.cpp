// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include "IMAPExpungeRetentionTask.h"

#include "IniFileSettings.h"

#include "../Persistence/PersistentIMAPFolder.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      /*
         Roughly how many records one DELETE is aimed at.

         Not a setting, because the number an administrator has an opinion about is
         how much history to keep, not how it is cut up. Sized so that a single
         statement stays comfortably inside the 30-second DatabaseStatementTimeout
         that PostgreSQL and MySQL now honour: a sweep that got itself cancelled
         every run would leave the table exactly as it found it and say nothing.
      */
      const int prune_batch_records = 2000;
   }

   IMAPExpungeRetentionTask::IMAPExpungeRetentionTask(void)
   {
   }

   IMAPExpungeRetentionTask::~IMAPExpungeRetentionTask(void)
   {
   }

   void
   IMAPExpungeRetentionTask::DoWork()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Cap each mailbox's QRESYNC expunge records at IMAPExpungeRetentionRecords, and
   // remove any belonging to folders that no longer exist.
   //---------------------------------------------------------------------------()
   {
      std::vector<std::pair<__int64, __int64>> recordCounts = PersistentIMAPFolder::GetExpungedRecordCounts();

      // Nothing has ever been expunged from any mailbox on this server. One GROUP BY
      // over an empty table is the whole cost of the task in that case.
      if (recordCounts.empty())
         return;

      // Records whose folder is gone go whatever the retention setting says. Nothing
      // can ever read one - the reader is only ever given the id of a folder a
      // session currently has selected - so "keep everything" cannot sensibly be
      // read as keeping those, and they are the residue of the failure HM6117
      // reports.
      __int64 orphansRemoved = PersistentIMAPFolder::DeleteOrphanedExpunged(recordCounts);

      if (orphansRemoved > 0)
      {
         LOG_APPLICATION(Formatter::Format("IMAP expunge-record retention: removed {0} record(s) belonging to "
                                           "folders that no longer exist.", orphansRemoved));
      }

      const int keepRecords = IniFileSettings::Instance()->GetIMAPExpungeRetentionRecords();

      // 0 keeps every record, which is the maximum-precision setting rather than a
      // disabled one: a client can then resynchronise from any mod-sequence and get
      // the compact answer. What it costs is a table that never stops growing, which
      // is why it is not the default.
      if (keepRecords <= 0)
         return;

      __int64 removed = 0;
      int foldersPruned = 0;

      for (const std::pair<__int64, __int64> &folder : recordCounts)
      {
         if (folder.second <= (__int64) keepRecords)
            continue;

         __int64 folderRemoved = PersistentIMAPFolder::PruneExpungedForFolder(folder.first, folder.second,
                                                                             keepRecords, prune_batch_records);

         if (folderRemoved > 0)
         {
            removed += folderRemoved;
            foldersPruned++;
         }
      }

      if (removed > 0)
      {
         LOG_APPLICATION(Formatter::Format("IMAP expunge-record retention: removed {0} record(s) from {1} "
                                           "mailbox(es), keeping the most recent {2} in each.",
                                           removed, foldersPruned, keepRecords));
      }
   }
}
