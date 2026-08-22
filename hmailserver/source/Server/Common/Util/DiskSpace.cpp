// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "DiskSpace.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const unsigned __int64 BYTES_PER_MEGABYTE = 1024 * 1024;

      // How long one reading of the volume is trusted.
      //
      // The obvious flaw in caching this is that a cache which is too slow to
      // react lets the disk actually fill: between two readings the server keeps
      // saying "there is room" while there is not. Five seconds is chosen with
      // that stated plainly rather than pretended away -
      //
      //   * The cache CANNOT be the guarantee, at any TTL. The floor defaults to
      //     100 MB and the default maximum message size is 20 MB, so five
      //     concurrent senders can cross the floor between one reading and the
      //     next however short the interval is - and a single message that is
      //     accepted at 101 MB free can be larger than the 101 MB. The guarantee
      //     is the write-failure handling described in the header, which turns a
      //     write that actually fails into a temporary refusal. This class buys
      //     early refusal and an early warning, and that is all it buys.
      //
      //   * Given that, the TTL is chosen for cost rather than for safety. At
      //     five seconds a server accepting 1000 messages a minute performs 12
      //     GetDiskFreeSpaceEx calls a minute instead of 1000. On a local volume
      //     that call costs microseconds and the cache is barely worth having; on
      //     a UNC data directory it is a network round trip on the SMTP thread,
      //     which is the case the cache exists for.
      //
      //   * Five seconds also bounds the OTHER direction, which matters just as
      //     much and is easy to forget: after an administrator frees space, the
      //     server starts accepting mail again within five seconds rather than
      //     staying shut for a minute after the problem was fixed.
      const ULONGLONG DISK_SPACE_CACHE_TTL_MS = 5000;

      // Bound on the ancestor walk in GetFreeBytesAvailable. A directory path is
      // not sixty-four levels deep, and an unbounded loop over a path that keeps
      // failing for a reason we did not anticipate would hang an SMTP thread.
      const int MAX_ANCESTOR_LEVELS = 64;

      // Which band the volume is in. Only ever compared for equality - the point
      // is to announce a CHANGE, so that a server sitting just under the warning
      // line writes one log line rather than one an hour for ever.
      enum DiskSpaceBand
      {
         BandOk = 0,
         BandWarning = 1,
         BandBelowFloor = 2,
         BandUnknown = 3
      };

      enum VolumeReadResult
      {
         // A reading was taken.
         ReadOk = 0,
         // No data directory is configured, so there is nothing to read and
         // nothing to say about it.
         ReadNotConfigured = 1,
         // The volume could not be interrogated.
         ReadFailed = 2
      };

      struct DiskSpaceState
      {
         // Guards the cached reading.
         boost::mutex cache_mutex;
         ULONGLONG reading_tick = 0;
         bool reading_valid = false;
         VolumeReadResult reading_result = ReadOk;
         unsigned __int64 free_bytes = 0;
         unsigned __int64 total_bytes = 0;

         // Guards the last band announced. Starts at BandOk so that a healthy
         // server - which is what a stock, default-configured one is - says
         // nothing at all, and the first thing ever written about disk space is a
         // genuine change for the worse.
         boost::mutex band_mutex;
         DiskSpaceBand last_band = BandOk;
      };

      DiskSpaceState &State()
      {
         static DiskSpaceState state;
         return state;
      }

      // Percentage of the volume still free, for the administrator warning.
      // Returns 0 when the total is unknown, which reads as "0%" - the text
      // always carries the absolute figure as well, so nothing is lost.
      unsigned __int64 PercentFree_(unsigned __int64 freeBytes, unsigned __int64 totalBytes)
      {
         if (totalBytes == 0)
            return 0;

         return (freeBytes * 100) / totalBytes;
      }

      // Refreshes the cached reading when it has expired, or when forced.
      //
      // The volume is interrogated with the lock held, deliberately: a burst of
      // simultaneous MAIL FROMs collapses into ONE GetDiskFreeSpaceEx rather than
      // one per connection, which is the point of the cache. It does mean one
      // SMTP thread can wait on another thread's syscall - on a UNC data
      // directory, a network round trip - but every one of those threads is about
      // to write a message to that same share, so this is not a dependency they
      // did not already have.
      VolumeReadResult ReadVolume_(bool forceRefresh, unsigned __int64 &freeBytes, unsigned __int64 &totalBytes)
      {
         DiskSpaceState &state = State();

         boost::lock_guard<boost::mutex> guard(state.cache_mutex);

         ULONGLONG now = GetTickCount64();

         if (!forceRefresh && state.reading_valid && now >= state.reading_tick &&
             now - state.reading_tick < DISK_SPACE_CACHE_TTL_MS)
         {
            freeBytes = state.free_bytes;
            totalBytes = state.total_bytes;
            return state.reading_result;
         }

         state.free_bytes = 0;
         state.total_bytes = 0;

         String dataDirectory = IniFileSettings::Instance()->GetDataDirectory();

         if (dataDirectory.IsEmpty())
            state.reading_result = ReadNotConfigured;
         else if (DiskSpace::GetFreeBytesAvailable(dataDirectory, state.free_bytes, &state.total_bytes))
            state.reading_result = ReadOk;
         else
            state.reading_result = ReadFailed;

         state.reading_valid = true;
         state.reading_tick = now;

         freeBytes = state.free_bytes;
         totalBytes = state.total_bytes;

         return state.reading_result;
      }

      DiskSpaceBand Evaluate_(bool forceRefresh, unsigned __int64 &freeBytes, unsigned __int64 &totalBytes,
                              unsigned __int64 &floorBytes, unsigned __int64 &warnBytes)
      {
         floorBytes = DiskSpace::GetMinimumFreeBytes();
         warnBytes = DiskSpace::GetWarningThresholdBytes();

         freeBytes = 0;
         totalBytes = 0;

         switch (ReadVolume_(forceRefresh, freeBytes, totalBytes))
         {
         case ReadNotConfigured:
            return BandOk;
         case ReadFailed:
            return BandUnknown;
         default:
            break;
         }

         if (floorBytes > 0 && freeBytes < floorBytes)
            return BandBelowFloor;

         if (warnBytes > 0 && freeBytes < warnBytes)
            return BandWarning;

         return BandOk;
      }

      // Announces a band, once, on the transition into it.
      //
      // No lock is held while reporting: ErrorManager::ReportError fires the
      // OnError script event, which runs administrator code, and holding a mutex
      // that an SMTP thread needs across arbitrary script would be a deadlock
      // waiting to be written.
      void ReportBand_(DiskSpaceBand band, unsigned __int64 freeBytes, unsigned __int64 totalBytes,
                       unsigned __int64 floorBytes, unsigned __int64 warnBytes)
      {
         DiskSpaceState &state = State();

         DiskSpaceBand previous = BandOk;

         {
            boost::lock_guard<boost::mutex> guard(state.band_mutex);

            if (state.last_band == band)
               return;

            previous = state.last_band;
            state.last_band = band;
         }

         switch (band)
         {
         case BandBelowFloor:
            // ErrorManager and not LOG_APPLICATION: mail is now being refused,
            // which is genuinely wrong and is the one state here an administrator
            // has to be woken for. It cannot fire on a stock install - the floor
            // is 100 MB.
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6230, "DiskSpace::ReportBand_",
               Formatter::Format("Free disk space on the volume holding the message store has fallen to {0} MB, below the configured minimum of {1} MB ([Settings] MinimumFreeDiskSpaceMB in hMailServer.ini). New mail is being refused with a TEMPORARY failure until space is freed, so senders will retry rather than bounce.",
                  freeBytes / BYTES_PER_MEGABYTE, floorBytes / BYTES_PER_MEGABYTE));
            break;

         case BandWarning:
            // LOG_APPLICATION and not ErrorManager: nothing is wrong yet. Mail is
            // still being accepted and the server is working exactly as
            // configured; this is the line that is worth more than the one above,
            // because it arrives while there is still time to act on it.
            LOG_APPLICATION(Formatter::Format("Free disk space on the volume holding the message store is down to {0} MB ({1}% of the volume), below the warning threshold of {2} MB ([Settings] DiskSpaceWarningThresholdMB in hMailServer.ini). Mail is still being accepted; it will be refused with a temporary failure below {3} MB.",
               freeBytes / BYTES_PER_MEGABYTE, PercentFree_(freeBytes, totalBytes),
               warnBytes / BYTES_PER_MEGABYTE, floorBytes / BYTES_PER_MEGABYTE));
            break;

         case BandUnknown:
            // LOG_APPLICATION and not ErrorManager, and the reasoning is worth
            // recording because the other choice is tempting. Nothing about the
            // server's job has failed here: an OPTIONAL precondition could not be
            // evaluated, it fails open, and mail is accepted exactly as it was
            // before this check existed - and a write that actually fails is
            // still refused with a temporary failure by the path that has always
            // handled it. An ERROR record would add nothing an administrator can
            // act on that the accompanying write failures do not already say,
            // while being one more way for a diagnostic to fire on an
            // installation where nothing is wrong.
            LOG_APPLICATION(String(_T("Free disk space on the volume holding the message store could not be determined, so the free-space precondition is not being applied; mail is accepted as it was before it existed. Check that the data directory named in hMailServer.ini exists and that the service account can reach it.")));
            break;

         case BandOk:
         default:
            if (previous != BandOk)
            {
               LOG_APPLICATION(Formatter::Format("Free disk space on the volume holding the message store has recovered to {0} MB ({1}% of the volume). Mail is being accepted normally.",
                  freeBytes / BYTES_PER_MEGABYTE, PercentFree_(freeBytes, totalBytes)));
            }
            break;
         }
      }
   }

   bool
   DiskSpace::GetFreeBytesAvailable(const String &path, unsigned __int64 &freeBytesAvailable,
                                    unsigned __int64 *totalBytes)
   {
      freeBytesAvailable = 0;

      if (totalBytes != nullptr)
         *totalBytes = 0;

      if (path.IsEmpty())
         return false;

      String directory = path;

      // Strip trailing separators so the ancestor walk below has something to
      // cut, then put exactly one back for the call itself.
      while (directory.GetLength() > 1 && directory.EndsWith(_T("\\")))
         directory = directory.Left(directory.GetLength() - 1);

      for (int level = 0; level < MAX_ANCESTOR_LEVELS; level++)
      {
         // GetDiskFreeSpaceEx documents the trailing backslash as REQUIRED when
         // the name is a UNC share (\\server\share\) and it is harmless
         // everywhere else, so it is always added rather than conditionally.
         String query = directory;
         if (!query.EndsWith(_T("\\")))
            query += _T("\\");

         ULARGE_INTEGER availableToCaller;
         ULARGE_INTEGER totalOnVolume;
         availableToCaller.QuadPart = 0;
         totalOnVolume.QuadPart = 0;

         // The FIRST out parameter. The third is "free bytes on the volume",
         // which is a different number as soon as a disk quota applies to the
         // account the service runs as - and the number that decides whether THIS
         // process can write another byte is the first one.
         if (GetDiskFreeSpaceEx(query.c_str(), &availableToCaller, &totalOnVolume, nullptr))
         {
            freeBytesAvailable = availableToCaller.QuadPart;

            if (totalBytes != nullptr)
               *totalBytes = totalOnVolume.QuadPart;

            return true;
         }

         // Only "that path is not there" is worth walking up for. Anything else -
         // a drive with no media, access denied - is a real failure and is
         // reported as one by the caller rather than papered over by answering
         // for a different volume.
         DWORD lastError = GetLastError();
         if (lastError != ERROR_PATH_NOT_FOUND &&
             lastError != ERROR_FILE_NOT_FOUND &&
             lastError != ERROR_INVALID_NAME &&
             lastError != ERROR_BAD_NETPATH &&
             lastError != ERROR_BAD_NET_NAME &&
             lastError != ERROR_DIRECTORY)
            return false;

         int separator = directory.ReverseFind(_T('\\'));
         if (separator <= 0)
            return false;

         String parent = directory.Left(separator);

         // "C:" is not a directory; "C:\" is.
         if (parent.GetLength() == 2 && parent.GetAt(1) == _T(':'))
            parent += _T("\\");

         // \\server names no volume, and neither does anything above it, so the
         // walk stops at \\server\share rather than asking about the host.
         if (parent.StartsWith(_T("\\\\")) && parent.ReverseFind(_T('\\')) <= 1)
            return false;

         if (parent == directory)
            return false;

         directory = parent;
      }

      return false;
   }

   unsigned __int64
   DiskSpace::GetMinimumFreeBytes()
   {
      int megabytes = IniFileSettings::Instance()->GetMinimumFreeDiskSpaceMB();

      if (megabytes <= 0)
         return 0;

      return (unsigned __int64) megabytes * BYTES_PER_MEGABYTE;
   }

   unsigned __int64
   DiskSpace::GetWarningThresholdBytes()
   {
      int megabytes = IniFileSettings::Instance()->GetDiskSpaceWarningThresholdMB();

      if (megabytes <= 0)
         return 0;

      return (unsigned __int64) megabytes * BYTES_PER_MEGABYTE;
   }

   bool
   DiskSpace::DataDirectoryHasRoomForMail()
   {
      // Switched off. Nothing is read, nothing is reported, and the server
      // behaves exactly as every release before this one did.
      if (GetMinimumFreeBytes() == 0)
         return true;

      unsigned __int64 freeBytes = 0;
      unsigned __int64 totalBytes = 0;
      unsigned __int64 floorBytes = 0;
      unsigned __int64 warnBytes = 0;

      DiskSpaceBand band = Evaluate_(false, freeBytes, totalBytes, floorBytes, warnBytes);

      ReportBand_(band, freeBytes, totalBytes, floorBytes, warnBytes);

      // BandUnknown answers true. See the fail-open note in the header.
      return band != BandBelowFloor;
   }

   void
   DiskSpace::CheckAndReportDataDirectory()
   {
      unsigned __int64 freeBytes = 0;
      unsigned __int64 totalBytes = 0;
      unsigned __int64 floorBytes = 0;
      unsigned __int64 warnBytes = 0;

      // Forced: this runs at most once an hour, so a cached reading would be
      // answering a question about an hour ago.
      DiskSpaceBand band = Evaluate_(true, freeBytes, totalBytes, floorBytes, warnBytes);

      ReportBand_(band, freeBytes, totalBytes, floorBytes, warnBytes);
   }

   void
   DiskSpace::InvalidateCache()
   {
      DiskSpaceState &state = State();

      boost::lock_guard<boost::mutex> guard(state.cache_mutex);
      state.reading_valid = false;
   }

   DiskSpaceMonitorTask::DiskSpaceMonitorTask(void)
   {
   }

   DiskSpaceMonitorTask::~DiskSpaceMonitorTask(void)
   {
   }

   void
   DiskSpaceMonitorTask::DoWork()
   {
      DiskSpace::CheckAndReportDataDirectory();
   }
}
