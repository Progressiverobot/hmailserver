// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class BlockedSender;

   // In-memory copy of the sender blacklist, consulted once per message
   // during the SMTP conversation. Modeled on WhiteListCache, with two
   // deliberate differences: the loaded flag is actually cleared after a
   // successful load (so the database is read once, not once per message),
   // and a failed load keeps the previous contents and retries on the next
   // message (stale entries beat an empty list that silently unblocks
   // every listed sender).
   class BlockedSenderCache
   {
   public:
      BlockedSenderCache(void);

      static void SetNeedRefresh();

      // Returns the entry that matches the given envelope sender, or an
      // empty pointer if none does. When several entries match (an address
      // entry and a domain entry covering the same sender), the one with
      // the highest score wins.
      std::shared_ptr<BlockedSender> GetBlockingEntry(const String &fromAddress);

   private:

      static void Refresh();
      static std::vector<std::shared_ptr<BlockedSender>> blockedSenderItems_;
      static bool needRefresh_;
   };
}
