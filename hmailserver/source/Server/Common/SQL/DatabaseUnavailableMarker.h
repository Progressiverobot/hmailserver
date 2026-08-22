// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // Records, for the current thread only, that a database operation failed
   // because the database was unavailable rather than because it had nothing to
   // return.
   //
   // Lookups deep in the object model report "not found" for both cases: an
   // account that does not exist and an account that could not be looked up
   // return the same empty result, through several layers that have no way to
   // carry a reason. That is fine until the answer is acted on - a recipient
   // reported as unknown is rejected with a permanent 550, so a database that is
   // briefly locked by a backup would tell the sending server that a perfectly
   // valid mailbox does not exist, and the mail would be bounced rather than
   // retried.
   //
   // Rather than change every signature between the pool and the protocol layer,
   // the pool marks the thread and the protocol layer asks. Both sides are a few
   // lines, and the marker is thread-local so concurrent sessions cannot see each
   // other's failures.
   //
   // Usage at the point of decision:
   //
   //    DatabaseUnavailableMarker::Scope scope;   // clears on entry
   //    ...do the lookup...
   //    if (!found && DatabaseUnavailableMarker::IsMarked())
   //       ... temporary failure ...
   //    else if (!found)
   //       ... genuinely not found ...
   class DatabaseUnavailableMarker
   {
   public:

      static void Mark();
      static bool IsMarked();
      static void Clear();

      // Clears the marker on construction so a decision is never made on a stale
      // failure from earlier work on the same thread, which is reused across
      // sessions by the thread pools.
      class Scope
      {
      public:
         Scope() { DatabaseUnavailableMarker::Clear(); }
         ~Scope() { DatabaseUnavailableMarker::Clear(); }
      };

   private:

      static thread_local bool unavailable_;
   };
}
