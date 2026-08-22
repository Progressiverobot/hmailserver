// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../BO/ScheduledTask.h"

namespace HM
{
   // Free space on the volume that holds the message store, and the policy the
   // server applies to it.
   //
   // Nothing in this server used to own running out of disk. GetDiskFreeSpaceEx
   // appeared in exactly one place - the backup destination - and there was no
   // free-space precondition anywhere on the data directory. What there IS, and
   // this is worth being precise about because it decides what this class is for,
   // is correct handling of a write that FAILS: a spool write that fails during
   // DATA is turned into 451 4.3.0 (SMTPConnection::OnPreAcceptTransfer_ via
   // TransparentTransmissionBuffer::GetWriteFailed), a failed APPEND is turned
   // into NO [SERVERBUG] (IMAPCommandAppend::CompleteCurrentMessage_), and a
   // failed local-delivery save is reported and bounced. So a full disk did not
   // lose mail on those paths; it just did nothing about it in advance.
   //
   // This class is therefore the PRECONDITION and the WARNING, not the safety
   // net. The safety net is the write-failure handling above and it stays where
   // it is. Two consequences follow and both are deliberate:
   //
   //   * Everything here fails OPEN. If the volume cannot be interrogated the
   //     answer is "there is room", because refusing mail on the strength of a
   //     failed syscall would be a worse outcome than the thing being guarded
   //     against - and the write-failure path still catches the real case.
   //
   //   * The refusal is TEMPORARY on every protocol. A disk that is full now may
   //     not be in ten minutes, and a permanent rejection would destroy mail that
   //     a five-minute cleanup would have delivered.
   class DiskSpace
   {
   public:
      // Free bytes on the volume holding path.
      //
      // "Available to the caller", i.e. the FIRST out parameter of
      // GetDiskFreeSpaceEx and not the second. Under a disk quota the two differ,
      // and the one that decides whether this process can write another byte is
      // the first. The service runs as LocalSystem or as a dedicated service
      // account, so a quota against it is a real configuration rather than a
      // hypothetical one.
      //
      // path need not exist: the nearest existing ancestor directory is used, so
      // the check works before the data directory has been created and for a
      // per-account folder that has not been made yet. A UNC path is supported -
      // GetDiskFreeSpaceEx documents a trailing backslash as required for one.
      //
      // totalBytes, when supplied, receives the size of the volume. Only used for
      // the "n% free" text in the administrator warning; the decisions themselves
      // are absolute, see GetMinimumFreeBytes.
      static bool GetFreeBytesAvailable(const String &path, unsigned __int64 &freeBytesAvailable,
                                        unsigned __int64 *totalBytes = nullptr);

      // The configured floor in bytes. 0 means the check is disabled.
      static unsigned __int64 GetMinimumFreeBytes();

      // The configured warning threshold in bytes. 0 means no warning.
      static unsigned __int64 GetWarningThresholdBytes();

      // The precondition, for the paths that accept new mail. True when there is
      // room, when the check is switched off, and when free space cannot be
      // determined (see the fail-open note above).
      //
      // Answered from a short-lived cache rather than a syscall per message. The
      // first time it goes from true to false it reports HM6230, so an
      // administrator hears about it at the moment mail starts being refused
      // rather than at the next tick of the monitor task.
      static bool DataDirectoryHasRoomForMail();

      // The periodic administrator warning. Refreshes the reading, then reports
      // whichever band the volume is in - and only when the band CHANGES, so a
      // server that sits just under the warning line writes one log line and not
      // one per hour for ever.
      static void CheckAndReportDataDirectory();

      // Drops the cached reading. For self-tests and for a configuration reload.
      static void InvalidateCache();
   };

   // Runs CheckAndReportDataDirectory on the scheduler. Registered unconditionally
   // (see Application::CreateScheduledTasks_) because the warning is the half of
   // this feature that is worth the most: a server that says "1% free" in the log
   // at 03:00 is worth more than one that starts refusing mail at 09:00.
   class DiskSpaceMonitorTask : public ScheduledTask
   {
   public:
      DiskSpaceMonitorTask(void);
      ~DiskSpaceMonitorTask(void);

      virtual void DoWork();
   };
}
