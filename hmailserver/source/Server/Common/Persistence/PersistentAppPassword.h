// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class AppPassword;
   enum PersistenceMode;

   class PersistentAppPassword
   {
   public:
      PersistentAppPassword(void);
      ~PersistentAppPassword(void);

      static bool SaveObject(std::shared_ptr<AppPassword> password, String &result, PersistenceMode mode);
      static bool DeleteObject(std::shared_ptr<AppPassword> password);

      // Every app password belonging to an account. Called when the account is
      // deleted: a credential that outlives the mailbox it opens is a credential
      // nobody will ever revoke, and the account id would eventually be reused.
      static bool DeleteByAccountID(__int64 accountID);

      // Records that this password has just authenticated something.
      //
      // Throttled, and that is the whole design of it. A POP3 client polling every
      // minute would otherwise turn a column nobody reads in real time into the
      // busiest write in the server - 1440 updates per day per client, each one a
      // transaction, purely to move a timestamp a few minutes. Writing at most once
      // an hour keeps the answer accurate enough for the question it exists to
      // answer ("has this credential been used at all, and roughly when?") at a
      // twenty-fourth of the cost. The read is from the row already in memory, so
      // the common case does no database work whatsoever.
      static void RecordUse(std::shared_ptr<AppPassword> password);

      // Whether any app password exists on this server at all.
      //
      // This is what keeps the feature free for everyone who does not use it. Without
      // it, every failed logon anywhere - and a mail server sees a great many, mostly
      // from people guessing - would run a SELECT against this table before answering
      // "no". The answer is cached after the first query and invalidated by every
      // write below, so the steady state is one atomic read.
      //
      // It is deliberately a server-wide answer rather than a per-account one: a
      // per-account cache would need invalidating from the account cache as well, and
      // the question being asked here is only "is this feature in use".
      static bool AnyConfigured();

      // Forces the next AnyConfigured() to ask the database again. Called by the
      // writes here; also called by the COM layer when it deletes on an account's
      // behalf.
      static void InvalidateExistenceCache();
   };
}
