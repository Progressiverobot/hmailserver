// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // Prunes the message archive.
   //
   // ArchiveDir is a raw filesystem copy of every message that passes through the
   // server - one file per message, under <archive>\<domain>\<user>\ for local
   // senders and <archive>\Inbound\ for everyone else. Nothing has ever removed
   // anything from it. On a busy server that is a directory that only grows, and the
   // administrator who eventually notices is the one whose disk filled up.
   //
   // ArchiveRetentionDays (0, the default, keeps everything) sets the window. Off by
   // default deliberately: an archive is frequently kept for a legal or contractual
   // reason, and a server that started deleting from one on upgrade would be
   // destroying exactly the evidence it was told to keep. Turning it on is a
   // decision, and it should be one somebody makes.
   class ArchiveRetentionTask : public ScheduledTask
   {
   public:
      ArchiveRetentionTask(void);
      ~ArchiveRetentionTask(void);

      virtual void DoWork();
   };
}
