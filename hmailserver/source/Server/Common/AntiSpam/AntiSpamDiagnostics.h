// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // Says out loud, once in a while, that a spam check could not be carried out.
   //
   // Every DNS-backed check in this directory fails *open*: DNSBL, SURBL, SPF,
   // DKIM and DMARC all treat a lookup that did not complete as "nothing to
   // report" and let the message through. That is the right call per message -
   // refusing mail because our own resolver is sick would be worse - but it is
   // the wrong thing to do *silently*, and until now the only trace was a
   // LOG_DEBUG line, which is off on every production server. A resolver that
   // has stopped answering therefore looks exactly like a run of clean mail:
   // scores of zero, no rejections, no errors. That is how a broken resolver
   // survived long enough to be found by reading the code rather than the logs.
   //
   // This is deliberately not ErrorManager::ReportError. It is an operational
   // condition, not a defect in the server, and an entry in the ERROR log breaks
   // every regression fixture that follows it (PerformBasicSetup refuses to run
   // while an error log exists), which would make a DNS outage during a test run
   // look like a hundred unrelated failures.
   //
   // Header-only on purpose: no new translation unit, so no project file change.
   class AntiSpamDiagnostics
   {
   public:

      enum Check
      {
         CheckDkim = 0,
         CheckDmarc = 1,
         CheckSurbl = 2,
         CheckCount = 3
      };

      // Writes message to the application log at most once per check per
      // interval. A resolver outage affects every message that arrives, and the
      // operator needs to be told that the check stopped working, not to have
      // the log filled with one line per message.
      static void ReportCheckIncomplete(Check check, const String &message)
      {
         if (!ShouldReport_(check))
            return;

         LOG_APPLICATION(message);
      }

   private:

      static bool ShouldReport_(Check check)
      {
         const ULONGLONG intervalMilliseconds = 15 * 60 * 1000;

         if (check < 0 || check >= CheckCount)
            return false;

         // One slot per check. Zero means "not yet reported"; GetTickCount64 is
         // monotonic for the life of the process, so no wrap handling is needed.
         static volatile LONG64 lastReported[CheckCount] = { 0, 0, 0 };

         const ULONGLONG now = GetTickCount64();
         const LONG64 previous = lastReported[check];

         if (previous != 0 && (now - static_cast<ULONGLONG>(previous)) < intervalMilliseconds)
            return false;

         // Two threads hitting the same outage at the same moment must produce
         // one line, not two.
         return InterlockedCompareExchange64(&lastReported[check], static_cast<LONG64>(now), previous) == previous;
      }
   };
}
