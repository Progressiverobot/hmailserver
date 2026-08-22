// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class SQLCommand;

   //---------------------------------------------------------------------------()
   // The full-text term index behind IMAP SEARCH BODY and SEARCH TEXT.
   //
   // hm_messageindexterms holds one row per distinct normalised token per
   // delivered message; hm_messageindexstate holds the backfill cursor (the row
   // with misaccountid = 0). The index is a FILTER, never the answer: IMAP BODY
   // search is substring search ("ell" matches "hello"), which a term index
   // cannot answer, so the searcher only ever uses it to prove a message CANNOT
   // contain the text and skip loading it. Every message the index cannot rule
   // out is read and scanned exactly as before, so the results are identical
   // with the index on and off - only the cost changes.
   //
   // The soundness argument, which every rule below exists to preserve:
   //
   //   A message matches BODY "x" when ToLower(header-selected body text)
   //   contains ToLower(x). Any occurrence of a maximal ASCII-alphanumeric run
   //   of x lies inside one maximal token-character run of the message text,
   //   because ASCII alphanumerics are a subset of the token character class
   //   and both sides are lowered by the same String::ToLower the scan itself
   //   uses. So if some run of x (of at least the minimum length) is contained
   //   in NO stored term of a fully indexed, non-overflowed message, the
   //   message cannot contain x, and the scan can be skipped.
   //
   // The rules that keep that argument true:
   //
   //   - Tokens shorter than IndexTimeMinTokenLength (3, a constant - never the
   //     ini setting, because lowering the setting after indexing would promise
   //     answers the stored index cannot give) are dropped: they can only hide
   //     query runs shorter than 3, and those runs are never used to filter.
   //   - Tokens longer than the column (64) are stored as overlapping 64-char
   //     windows stepped by 40, so every substring of up to MaxQueryNeedleLength
   //     (16) characters of the original token lies whole inside some window.
   //     Query needles are therefore capped at 16 characters - a prefix of a
   //     longer run is still a substring of any text containing the run.
   //   - A message whose distinct terms exceed the cap, or whose file could not
   //     be loaded, gets the OverflowMarker term and no content terms: the
   //     searcher treats it as always-a-candidate, i.e. exactly today's scan.
   //   - The CompleteMarker term is written LAST, after every content term, so
   //     a message is only ever excludable once its terms are all committed. No
   //     marker = the message is scanned, which makes a half-indexed message, a
   //     not-yet-indexed message and a message whose terms were deleted for
   //     re-indexing all safe by the same rule.
   //
   // Marker terms start with '\x01', which the tokeniser can never emit (it
   // only emits ASCII alphanumerics and characters above 127), so they cannot
   // collide with content.
   //---------------------------------------------------------------------------()
   class PersistentMessageIndex
   {
   public:

      // Tokens shorter than this are never stored. A hard constant, not the ini
      // setting: see the class comment.
      static const int IndexTimeMinTokenLength = 3;

      // The column width of hm_messageindexterms.mitterm.
      static const int MaxTermLength = 64;

      // The longest LIKE needle the searcher may use. Bound by the window step:
      // windows of 64 stepped by 40 guarantee containment for substrings up to
      // 64 - 40 - a little slack for surrogate-pair-preserving boundary trims.
      static const int MaxQueryNeedleLength = 16;

      // A message that could not be indexed faithfully - too many distinct
      // terms, an over-long token explosion, or an unreadable file. Such a
      // message is always a search candidate.
      static const wchar_t *OverflowMarker();

      // Written last: its presence means every content term for the message is
      // committed, so the message may be excluded on the index's word.
      static const wchar_t *CompleteMarker();

      class IndexableMessage
      {
      public:
         __int64 MessageID = 0;
         __int64 AccountID = 0;
         String FileName;
      };

      // --- Tokenisation, shared by the indexer and the searcher. -------------

      // Distinct terms of the given content: lowered with String::ToLower (the
      // same folding ContainsNoCase applies during the scan), split on ASCII
      // non-alphanumerics (characters above 127 stay inside tokens), short
      // tokens dropped, long tokens stored as overlapping windows. Sets
      // overflowed - and stops - when the distinct count passes the cap.
      static void TokenizeForIndex(const String &content, size_t maxDistinctTerms, std::set<String> &terms, bool &overflowed);

      // The LIKE needles for one search text: maximal ASCII-alphanumeric runs
      // of the lowered text, at least minRunLength long, clipped to
      // MaxQueryNeedleLength, longest first, at most four. Empty means the
      // index cannot filter this text and the caller must scan everything.
      static void CreateQueryNeedles(const String &searchText, int minRunLength, std::vector<String> &needles);

      // --- Index maintenance (called from the MessageIndexer thread). --------

      // Replaces the message's rows: deletes what is there, inserts the content
      // terms, then the CompleteMarker (or just OverflowMarker + CompleteMarker
      // when overflowed - an overflowed message is always scanned, so content
      // terms for it would be dead rows).
      static bool SaveTermsForMessage(__int64 messageID, __int64 accountID, const std::set<String> &terms, bool overflowed);

      // Removes every term row for the message. Also what a caller uses to
      // force one message back to always-scanned (and eventual re-indexing by
      // the reconciliation pass) after its content changed.
      static bool DeleteForMessage(__int64 messageID);

      // Terms whose message no longer exists. Mirrors
      // PersistentMessageMetaData::DeleteOrphanedItems; staleness here is a
      // space problem and never a correctness one, because the searcher only
      // sees messages that are still in the folder being searched.
      static bool DeleteOrphanedItems();

      // Everything, terms and cursor. The backfill then starts over.
      static void Clear();

      // The backfill cursor (hm_messageindexstate, misaccountid = 0): every
      // delivered message at or below it has been OFFERED to the indexer. 0
      // when the backfill has never run.
      static __int64 GetBackfillCursor();
      static bool SetBackfillCursor(__int64 highestMessageID);

      // The smallest delivered message id above lowExclusive, or 0 when there
      // is none - lets the backfill jump sparse id space instead of walking it.
      static __int64 GetFirstMessageIdAbove(__int64 lowExclusive);

      // The highest DELIVERED message id - the backfill frontier. Not the
      // highest id overall: a still-queued row must stay ahead of the cursor
      // until it is delivered, or it would be skipped.
      static __int64 GetLatestDeliveredMessageId();

      // Delivered messages with lowExclusive < messageid <= highInclusive.
      static bool GetMessagesInRange(__int64 lowExclusive, __int64 highInclusive, std::vector<IndexableMessage> &result);

      // Delivered messages at or below the cursor without a CompleteMarker:
      // the ones the cursor walked past before they were committed as
      // delivered, the ones whose indexing was interrupted partway, and the
      // ones whose terms were deleted after an edit. At most maxCount. They
      // are merely unindexed (always scanned) until this heals them.
      static bool GetUnindexedMessages(__int64 highInclusive, int maxCount, std::vector<IndexableMessage> &result);

      // --- Search-side queries (account-scoped, so a search never crosses
      //     accounts; public-folder messages live under account id 0). --------

      // Messages of the account having at least one term that CONTAINS the
      // needle (mitterm LIKE '%needle%').
      static bool GetMessagesWithTermContaining(__int64 accountID, const String &needle, std::set<__int64> &result);

      // Messages of the account having exactly this term. Used for the markers.
      static bool GetMessagesWithTerm(__int64 accountID, const String &term, std::set<__int64> &result);

   private:

      static bool IsTokenChar_(wchar_t c);
      static bool IsAsciiAlnum_(wchar_t c);

      static bool InsertTerm_(__int64 messageID, __int64 accountID, const String &term);
      static bool ReadIndexableMessages_(SQLCommand &command, int maxCount, std::vector<IndexableMessage> &result);
      static bool ReadMessageIdSet_(SQLCommand &command, std::set<__int64> &result);
   };
}
