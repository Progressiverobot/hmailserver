// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // The memory behind the first-contact note: who has written to an account
   // before. One row per account and sender address in hm_knownsenders (schema
   // 6045), which is the smallest thing that can answer the question.
   //
   // Why not the message index. hm_messages knows what arrived, but it does not
   // know who from: the sender is in the file, not in a column, so answering
   // "has this address written to me before" from it means opening every
   // message in the mailbox. A mailbox of forty thousand messages would do that
   // on every delivery. This table costs one indexed lookup instead.
   //
   // What it costs. Nothing at all unless the recipient's domain has the note
   // turned on. With it on: one SELECT per local recipient, then one UPDATE
   // when the sender is known, or one further SELECT and one INSERT the first
   // time a given address writes to a given account. The steady state is two
   // round trips per delivered copy.
   //
   // After a restore from backup. The table restores with the rest of the
   // database, so the memory is as of the backup: senders who first wrote after
   // it are forgotten and their next message is announced as a first contact
   // again. One redundant note each, and nothing is lost. A restore of the mail
   // store without the database has no memory at all, which is the same as a
   // fresh installation - see Result::NoMemoryYet.
   class PersistentKnownSender
   {
   public:

      enum Result
      {
         // The account has written-to-before history, and this address is not
         // in it. This is the note.
         FirstContact = 0,

         // This address has written to this account before.
         SeenBefore = 1,

         // The account has no history at all, so nothing can be unusual
         // relative to it. Recorded, not announced: announcing here would put a
         // first-contact note on the first message a brand-new mailbox ever
         // receives, which tells the reader nothing.
         NoMemoryYet = 2,

         // The lookup failed. Nothing is announced on a database error - a note
         // that appears because a query timed out is worse than no note.
         Unknown = 3
      };

      // Records that this address has written to this account, and says whether
      // it had before. Addresses are compared without case and stored folded
      // down, because a sender writing from Ada@ and ada@ is one correspondent.
      static Result Remember(__int64 accountID, const String &senderAddress);

      // Records the address without answering anything: what an account SENDS
      // to is somebody it knows, so a reply is not a first contact. Called on
      // submission for each recipient of a message an authenticated account
      // sent.
      static void RememberOutgoing(__int64 accountID, const String &recipientAddress);

      // Everything this account remembers, for the account deletion path and
      // for the tests.
      static bool DeleteByAccount(__int64 accountID);

      static int CountByAccount(__int64 accountID);
   };
}
