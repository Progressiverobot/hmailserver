// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#pragma once

#include <vector>

namespace HM
{
   // The seen-store behind the Sieve "duplicate" test (RFC 7352): has a message
   // with this identifier been delivered to this account before, inside the
   // expiry window - and if not, remember that this one now has been.
   //
   // Deliberately a sibling of SieveVacationTracker rather than a shared core,
   // for the same reason RuleGuard has two parallel regex functions: the two
   // stores share their file format, their key hashing and their injection
   // defences, but they FAIL IN OPPOSITE DIRECTIONS, and a shared implementation
   // would need a flag whose wrong value in one caller is a mail loop and in the
   // other is silently discarded mail. If you change one, look at the other.
   //
   // FAIL OPEN. Everything here obeys one rule, and it is the OPPOSITE of the
   // vacation tracker's: when the store cannot prove a message was seen before,
   // the answer is "not a duplicate". The common script is
   // "if duplicate { discard; }" (or fileinto a folder nobody reads), so a false
   // "duplicate" verdict destroys a legitimate message, while a false "new"
   // verdict delivers a copy twice - an annoyance. An unreadable store, an
   // unparseable line, a failed write, a store at its record cap: all of them
   // answer false. The one cost is that the message which HIT the unreadable
   // store is also not recorded, so its own real duplicate would later be missed;
   // that is the correct price for never guessing "duplicate".
   //
   // The cap is likewise enforced in the open direction: when the store already
   // holds MaxTrackedMessages live records, a new message is answered "not a
   // duplicate" and NOT recorded. Evicting the oldest record instead would let a
   // sender who controls their own Message-IDs roll every other correspondent's
   // records out of the window.
   //
   // The key is (account, source-tagged identifier, handle):
   //
   //   account     the store lives in the account's own Sieve directory, like the
   //               vacation store, and is removed with the account's Sieve data.
   //   identifier  what the script tests on: the Message-ID header by default, a
   //               named header's value under ":header", or a literal under
   //               ":uniqueid". The evaluator tags the identifier with its source
   //               before it reaches this class, because RFC 7352 3 requires that
   //               a ":uniqueid" value and a Message-ID that happen to be equal
   //               text do NOT collide.
   //   handle      ":handle" when the script gave one, "" otherwise - two
   //               duplicate tests with different handles track independently
   //               (RFC 7352 3.2).
   //
   // Persistence format: identical to the vacation store - an ASCII header line,
   // then "<expiry-unix-seconds> <key-hex>" per record, keys as SHA-256 hex so
   // attacker-supplied Message-IDs and handles cannot inject or forge lines and
   // the record cap bounds the file size.
   class SieveDuplicateTracker : public Singleton<SieveDuplicateTracker>
   {
   public:
      SieveDuplicateTracker();

      // Atomically asks "was this identifier seen inside its window", and when it
      // was NOT, records that it has now been. Returns true only for a proven
      // duplicate. windowSeconds is clamped to [1, MaxWindowSeconds];
      // refreshOnSeen is RFC 7352's ":last" - a seen record's expiry is pushed
      // out to now+window, so the window measures from the LAST occurrence
      // rather than the first.
      //
      // Check and record are one operation under one lock on purpose: two copies
      // of the same message arriving on two delivery threads must not both see an
      // empty store and both answer "new".
      bool CheckAndRecord(const String &accountAddress,
                          const String &identifier,
                          const String &handle,
                          __int64 windowSeconds,
                          bool refreshOnSeen);

      // How many live records one account's store may hold; part of the contract.
      // An account receiving more distinct identifiers than this inside one
      // window stops detecting new duplicates until records expire - detecting,
      // not delivering; delivery is never affected.
      static size_t MaxTrackedMessages();

   private:
      struct Record
      {
         __int64 expires = 0;
         AnsiString key;
      };

      static AnsiString KeyFor_(const String &identifier, const String &handle);
      static bool Load_(const String &path, __int64 now, std::vector<Record> &records, bool &prunedAny);
      static bool Save_(const String &path, const std::vector<Record> &records);
      static bool IsWellFormedKey_(const AnsiString &key);

      boost::recursive_mutex mutex_;
   };
}
