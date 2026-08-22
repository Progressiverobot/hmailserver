// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <vector>

namespace HM
{
   // The response-tracking store RFC 5230 4.7 requires: "the implementation MUST
   // keep track of the responses it has sent" so that a second message from the
   // same sender inside the suppression window does not get a second reply.
   //
   // This is not a nicety, it is the whole difference between vacation and a mail
   // loop. Two hMailServer users who are both away and who both mail each other
   // once produce exactly two replies with tracking and an unbounded exchange
   // without it, because every reply is itself a message that arrives at the other
   // account.
   //
   // FAIL CLOSED. Everything in here obeys one rule, and it is the rule an earlier
   // version of this class broke: a reply is permitted only when the store can
   // PROVE that no reply has already been sent for this key. "The record was not
   // found" is only such a proof when the whole store was read, understood, and
   // found not to contain it. Every other outcome - a store that cannot be read, a
   // line that cannot be parsed, a file whose format is not ours, a store already
   // holding as many live records as it may hold, a write that fails - means the
   // answer is unknown, and an unknown answer must suppress the reply. Getting this
   // backwards does not produce a missing feature, it produces a mail loop and a
   // backscatter source, which is the entire risk this feature carries.
   //
   // Concretely, and this is the part worth not re-inventing: there is deliberately
   // NO eviction path. The earlier version capped the store by dropping the records
   // with the nearest expiry when a new one arrived, which reads as a reasonable
   // trade until you follow it through - the sender whose record was dropped is now
   // a sender the store has forgotten, so their next message is answered again, and
   // the one after that, for as long as they keep writing. A cap enforced by
   // forgetting is not a cap on anything that matters. The cap here is enforced by
   // refusing to reply instead (see ClaimResponse), which costs an auto-reply and
   // cannot cost a loop.
   //
   // The key is (account, sender, handle):
   //
   //   account   the store lives in the account's own Sieve directory, so the
   //             recipient half of the key is the path and never appears in a
   //             record. It is the canonical account address, not the envelope
   //             recipient, on purpose: a user reached through an alias and through
   //             their own address is one person on one holiday, and keying on the
   //             envelope recipient would answer the same correspondent once per
   //             address they happened to use. Removing an account's Sieve data
   //             also removes its tracking data, which is the behaviour an
   //             administrator deleting a mailbox expects.
   //   sender    the envelope sender the reply would go to, lowercased. Per
   //             RFC 5230 4.7 the tracking is per-sender, not per-message.
   //   handle    ":handle" when the script gave one, otherwise the value
   //             DeriveHandle produces from the reason/subject/from/:mime
   //             arguments (RFC 5230 4.4). Two vacation commands that say the same
   //             thing therefore share a suppression window, and editing the reason
   //             text deliberately starts a fresh one.
   //
   // Records are stored on disk rather than in memory because a restart must not
   // reset the suppression windows. The in-memory multimap the account-level
   // auto-reply uses (SMTPVacationMessageCreator::CanSendVacationMessage_) loses
   // everything on restart, and a service that restarts nightly therefore
   // re-answers every correspondent every day; that is precisely the behaviour this
   // store exists to avoid.
   //
   // Persistence format: an ASCII header line, then one line per record,
   // "<expiry-unix-seconds> <key-hex>". The key is a SHA-256 hex digest rather than
   // the sender and handle in the clear. That is a deliberate injection defence,
   // not obfuscation: a ":handle" is arbitrary script text and a sender is
   // arbitrary attacker-supplied text, and either could otherwise contain a newline
   // and forge or delete records in this file. A fixed-width hex token cannot. It
   // also bounds the line length, which is what lets the record cap bound the file
   // size.
   class SieveVacationTracker : public Singleton<SieveVacationTracker>
   {
   public:
      SieveVacationTracker();

      // Atomically asks "is a response to this sender due, and if so record that it
      // is being sent". Returns true only when the caller should actually send.
      // intervalSeconds is the suppression window; a window of zero or less
      // (RFC 6131 ":seconds 0") means "respond every time", for which there is
      // nothing to record and nothing to check.
      //
      // The check and the record are one operation on purpose. Splitting them would
      // let two deliveries from the same sender arriving on two delivery threads
      // both see an empty store and both send.
      bool ClaimResponse(const String &accountAddress,
                         const String &sender,
                         const String &handle,
                         __int64 intervalSeconds);

      // Discards everything recorded for an account. Called when the account's
      // Sieve filtering is switched off, mirroring what
      // SMTPVacationMessageCreator::VacationMessageTurnedOff does for the
      // account-level auto-reply: a user who turns the feature off and on again
      // expects the people who wrote to them in the meantime to hear back. This is
      // the one sanctioned way to forget a response, and it needs a deliberate act
      // by the account's owner - a script that is off sends nothing, so there is no
      // window in which forgetting here can produce a second reply.
      void Forget(const String &accountAddress);

      // The implicit handle of RFC 5230 4.4: when a script gives no ":handle", the
      // suppression window is keyed on the content of the response, so that
      // changing the reason text starts a new window and two scripts saying the
      // same thing share one. Returned as a hex digest because it becomes part of a
      // record key.
      static String DeriveHandle(const String &reason,
                                 const String &subject,
                                 const String &fromAddress,
                                 bool mimeReason);

      // How many live records one account's store may hold. Exposed because it is
      // part of the contract rather than an implementation detail: an account with
      // more distinct correspondents than this inside one suppression window stops
      // getting auto-replies until records expire, and anybody reasoning about that
      // needs the number.
      static size_t MaxTrackedResponses();

   private:
      struct Record
      {
         __int64 expires = 0;
         AnsiString key;
      };

      static AnsiString KeyFor_(const String &sender, const String &handle);

      // Reads the store. Returns false when the store exists but its contents could
      // not be read in full and understood - the caller must then treat the answer
      // as unknown and refuse to reply, because a record it could not read may be
      // the very one that says "already answered".
      //
      // Expired records are dropped rather than returned, which is the pruning: it
      // happens on the path that is about to rewrite the file anyway, so there is
      // no separate sweep to schedule. prunedAny reports whether anything was
      // dropped, so a caller that is not going to add a record can still shrink the
      // file.
      static bool Load_(const String &path, __int64 now, std::vector<Record> &records, bool &prunedAny);

      // Writes the store. Returns false when the write failed, which the caller
      // must treat as "do not send". Refuses to write more records than
      // MaxTrackedResponses rather than silently dropping any: see the class
      // comment on why nothing in here may forget a record.
      static bool Save_(const String &path, const std::vector<Record> &records);

      // True for a 64-character lower-case hex string, the only shape KeyFor_
      // produces. A line whose key is any other shape was not written by this
      // class, and a store containing one is not a store whose absence of a record
      // proves anything.
      static bool IsWellFormedKey_(const AnsiString &key);

      // One lock for every account. Delivery is not hot enough for the contention
      // to matter (one short file read and write per auto-reply, and auto-replies
      // are rare by construction), and a per-account lock table would need its own
      // eviction policy to avoid becoming the unbounded structure this class exists
      // to avoid.
      boost::recursive_mutex mutex_;
   };
}
