// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include ".\PersistentMessageIndex.h"
#include ".\PersistentMessage.h"
#include "..\BO\Message.h"

#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Long tokens are stored as overlapping MaxTermLength windows stepped by
      // this. The containment guarantee needs
      //    step <= MaxTermLength - MaxQueryNeedleLength + 1  (= 49)
      // and 40 leaves slack for the surrogate-preserving boundary trims below.
      const int ChunkStep = 40;

      inline bool IsHighSurrogate(wchar_t c) { return c >= 0xD800 && c <= 0xDBFF; }
      inline bool IsLowSurrogate(wchar_t c)  { return c >= 0xDC00 && c <= 0xDFFF; }
   }

   const wchar_t *
   PersistentMessageIndex::OverflowMarker()
   {
      // '\x01' cannot be produced by the tokeniser, so no content term can
      // collide with a marker.
      return L"\x01" L"fto";
   }

   const wchar_t *
   PersistentMessageIndex::CompleteMarker()
   {
      return L"\x01" L"ftc";
   }

   bool
   PersistentMessageIndex::IsTokenChar_(wchar_t c)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The tokeniser's character class: split ONLY on ASCII non-alphanumerics.
   // Everything above 127 stays inside tokens. That choice is what makes the
   // filter sound: it is locale-independent, and it guarantees that every
   // maximal ASCII-alphanumeric run of any text - which is all the searcher
   // ever queries on - lies inside a single token, because the query class
   // (IsAsciiAlnum_) is a subset of this one. The cost is that unspaced CJK
   // text and Unicode-punctuated prose form long merged tokens; the window
   // chunking absorbs those.
   //---------------------------------------------------------------------------()
   {
      if (c >= L'0' && c <= L'9') return true;
      if (c >= L'a' && c <= L'z') return true;
      if (c >= L'A' && c <= L'Z') return true;
      return c > 127;
   }

   bool
   PersistentMessageIndex::IsAsciiAlnum_(wchar_t c)
   {
      if (c >= L'0' && c <= L'9') return true;
      if (c >= L'a' && c <= L'z') return true;
      if (c >= L'A' && c <= L'Z') return true;
      return false;
   }

   void
   PersistentMessageIndex::TokenizeForIndex(const String &content, size_t maxDistinctTerms, std::set<String> &terms, bool &overflowed)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The distinct terms of one message's searchable text. The content MUST be
   // the exact string the scan searches (header + body + HTML body, in that
   // order, with nothing inserted between body and HTML body - BODY searches
   // their direct concatenation, so a token straddling that junction is real
   // and must be indexed as the merged token it is).
   //
   // Lowered with String::ToLower - the very function ContainsNoCase applies
   // to both sides during the scan - so the index's idea of case-insensitivity
   // is the scan's, character for character, and no exotic case pair can make
   // the index exclude a message the scan would match.
   //---------------------------------------------------------------------------()
   {
      overflowed = false;

      String lowered = content;
      lowered.ToLower();

      const wchar_t *data = lowered.c_str();
      const size_t length = (size_t) lowered.GetLength();

      size_t tokenStart = 0;
      bool inToken = false;

      for (size_t i = 0; i <= length; i++)
      {
         const bool isTokenChar = (i < length) && IsTokenChar_(data[i]);

         if (isTokenChar)
         {
            if (!inToken)
            {
               inToken = true;
               tokenStart = i;
            }
            continue;
         }

         if (!inToken)
            continue;

         inToken = false;
         const size_t tokenEnd = i;
         const size_t tokenLength = tokenEnd - tokenStart;

         // Dropping short tokens is sound because the searcher never uses a
         // needle shorter than IndexTimeMinTokenLength: a needle of length n
         // can only occur inside a token of length >= n.
         if (tokenLength < (size_t) IndexTimeMinTokenLength)
            continue;

         if (tokenLength <= (size_t) MaxTermLength)
         {
            terms.insert(lowered.Mid((int) tokenStart, (int) tokenLength));
         }
         else
         {
            // Overlapping windows: any substring of up to MaxQueryNeedleLength
            // characters of this token lies whole inside one window, so a LIKE
            // over the windows finds everything a LIKE over the token would.
            for (size_t windowStart = tokenStart; ; windowStart += ChunkStep)
            {
               size_t start = windowStart;
               size_t end = windowStart + (size_t) MaxTermLength;
               if (end > tokenEnd)
                  end = tokenEnd;

               // Never split a surrogate pair across a window edge: a half
               // pair is not a valid string, and some engines mangle or
               // reject it. The step of 40 leaves room for these one-unit
               // trims without breaking the containment guarantee.
               if (start > tokenStart && IsLowSurrogate(data[start]))
                  start++;
               if (end < tokenEnd && end > start && IsHighSurrogate(data[end - 1]))
                  end--;

               if (end > start && (end - start) >= (size_t) IndexTimeMinTokenLength)
                  terms.insert(lowered.Mid((int) start, (int) (end - start)));

               if (windowStart + (size_t) MaxTermLength >= tokenEnd)
                  break;

               if (terms.size() > maxDistinctTerms)
                  break;
            }
         }

         if (terms.size() > maxDistinctTerms)
         {
            // Too rich to index faithfully (a giant HTML message, an inline
            // base64 image). It gets the overflow marker instead of terms and
            // is scanned on every search - exactly today's behaviour.
            overflowed = true;
            terms.clear();
            return;
         }
      }
   }

   void
   PersistentMessageIndex::CreateQueryNeedles(const String &searchText, int minRunLength, std::vector<String> &needles)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The LIKE needles for one search text. Only maximal ASCII-alphanumeric
   // runs are used: an ASCII run of the search text must, in any message
   // containing the text, lie inside a single token (the query class is a
   // subset of the token class), so "no stored term contains the run" proves
   // "the message does not contain the text". Runs containing characters above
   // 127 are simply not used - skipping a run only widens the candidate set.
   //
   // Needles are clipped to MaxQueryNeedleLength (a prefix of a run is still a
   // substring of any text containing the run) so the window chunking of long
   // tokens stays sufficient. Longest runs first - they are the most
   // selective - and at most four, since each costs one scan of the account's
   // terms.
   //---------------------------------------------------------------------------()
   {
      needles.clear();

      if (minRunLength < IndexTimeMinTokenLength)
         minRunLength = IndexTimeMinTokenLength;

      String lowered = searchText;
      lowered.ToLower();

      const wchar_t *data = lowered.c_str();
      const size_t length = (size_t) lowered.GetLength();

      std::set<String> distinctNeedles;

      size_t runStart = 0;
      bool inRun = false;

      for (size_t i = 0; i <= length; i++)
      {
         const bool isRunChar = (i < length) && IsAsciiAlnum_(data[i]);

         if (isRunChar)
         {
            if (!inRun)
            {
               inRun = true;
               runStart = i;
            }
            continue;
         }

         if (!inRun)
            continue;

         inRun = false;
         const size_t runLength = i - runStart;

         if (runLength < (size_t) minRunLength)
            continue;

         size_t needleLength = runLength;
         if (needleLength > (size_t) MaxQueryNeedleLength)
            needleLength = (size_t) MaxQueryNeedleLength;

         distinctNeedles.insert(lowered.Mid((int) runStart, (int) needleLength));
      }

      needles.assign(distinctNeedles.begin(), distinctNeedles.end());

      std::sort(needles.begin(), needles.end(),
         [](const String &first, const String &second)
         {
            if (first.GetLength() != second.GetLength())
               return first.GetLength() > second.GetLength();
            return first < second;
         });

      if (needles.size() > 4)
         needles.resize(4);
   }

   bool
   PersistentMessageIndex::InsertTerm_(__int64 messageID, __int64 accountID, const String &term)
   {
      SQLCommand command("insert into hm_messageindexterms (mitmessageid, mitaccountid, mitterm) values (@MESSAGEID, @ACCOUNTID, @TERM)");
      command.AddParameter("@MESSAGEID", messageID);
      command.AddParameter("@ACCOUNTID", (int) accountID);
      command.AddParameter("@TERM", term);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   PersistentMessageIndex::SaveTermsForMessage(__int64 messageID, __int64 accountID, const std::set<String> &terms, bool overflowed)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Replaces the message's index rows. The CompleteMarker goes in LAST: its
   // presence is the searcher's proof that every content term is committed, so
   // an interruption anywhere in here leaves a message that is merely scanned,
   // never one that is wrongly excluded. The delete first makes the whole
   // operation idempotent for the resumed backfill.
   //---------------------------------------------------------------------------()
   {
      if (!DeleteForMessage(messageID))
         return false;

      if (overflowed)
      {
         // An overflowed message is a candidate for every search, so content
         // terms for it would be rows nothing ever reads.
         if (!InsertTerm_(messageID, accountID, OverflowMarker()))
            return false;
      }
      else
      {
         for (const String &term : terms)
         {
            if (!InsertTerm_(messageID, accountID, term))
               return false;
         }
      }

      return InsertTerm_(messageID, accountID, CompleteMarker());
   }

   bool
   PersistentMessageIndex::DeleteForMessage(__int64 messageID)
   {
      SQLCommand command("delete from hm_messageindexterms where mitmessageid = @MESSAGEID");
      command.AddParameter("@MESSAGEID", messageID);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   PersistentMessageIndex::DeleteOrphanedItems()
   {
      SQLCommand command("delete from hm_messageindexterms where not exists (select messageid from hm_messages where messageid = mitmessageid)");

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   void
   PersistentMessageIndex::Clear()
   {
      SQLCommand deleteTerms("delete from hm_messageindexterms");
      Application::Instance()->GetDBManager()->Execute(deleteTerms);

      SQLCommand deleteState("delete from hm_messageindexstate");
      Application::Instance()->GetDBManager()->Execute(deleteState);
   }

   __int64
   PersistentMessageIndex::GetBackfillCursor()
   {
      SQLCommand command("select mishighwatermark from hm_messageindexstate where misaccountid = 0");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS || pRS->IsEOF())
         return 0;

      return pRS->GetInt64Value("mishighwatermark");
   }

   bool
   PersistentMessageIndex::SetBackfillCursor(__int64 highestMessageID)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Read-then-insert-or-update is race-free here because the indexer thread
   // is the only writer of the misaccountid = 0 row.
   //---------------------------------------------------------------------------()
   {
      SQLCommand existsCommand("select count(*) as c from hm_messageindexstate where misaccountid = 0");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(existsCommand);
      if (!pRS)
         return false;

      if (pRS->GetLongValue("c") == 0)
      {
         SQLCommand insertCommand("insert into hm_messageindexstate (misaccountid, mishighwatermark) values (0, @HWM)");
         insertCommand.AddParameter("@HWM", highestMessageID);
         return Application::Instance()->GetDBManager()->Execute(insertCommand);
      }

      SQLCommand updateCommand("update hm_messageindexstate set mishighwatermark = @HWM where misaccountid = 0");
      updateCommand.AddParameter("@HWM", highestMessageID);
      return Application::Instance()->GetDBManager()->Execute(updateCommand);
   }

   __int64
   PersistentMessageIndex::GetFirstMessageIdAbove(__int64 lowExclusive)
   {
      SQLCommand command("select min(messageid) as m from hm_messages where messagetype = 2 and messageid > @LOW");
      command.AddParameter("@LOW", lowExclusive);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS || pRS->IsEOF())
         return 0;

      return pRS->GetInt64Value("m");
   }

   bool
   PersistentMessageIndex::ReadIndexableMessages_(SQLCommand &command, int maxCount, std::vector<IndexableMessage> &result)
   {
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      while (!pRS->IsEOF())
      {
         if (maxCount > 0 && (int) result.size() >= maxCount)
            break;

         IndexableMessage info;
         info.MessageID = pRS->GetInt64Value("messageid");
         info.AccountID = pRS->GetLongValue("messageaccountid");

         String accountAddress = pRS->GetStringValue("accountaddress");
         String fileName = pRS->GetStringValue("messagefilename");

         // The same path construction the metadata indexer uses: a dummy
         // message carrying what GetFileName reads. An empty account address
         // (a public-folder message) selects the public-folder path.
         std::shared_ptr<Message> dummyMessage = std::shared_ptr<Message>(new Message);
         dummyMessage->SetID(info.MessageID);
         dummyMessage->SetAccountID(info.AccountID);
         dummyMessage->SetFolderID(pRS->GetLongValue("messagefolderid"));
         dummyMessage->SetPartialFileName(fileName);

         info.FileName = PersistentMessage::GetFileName(accountAddress, dummyMessage);

         result.push_back(info);
         pRS->MoveNext();
      }

      return true;
   }

   bool
   PersistentMessageIndex::GetMessagesInRange(__int64 lowExclusive, __int64 highInclusive, std::vector<IndexableMessage> &result)
   {
      SQLCommand command(
         "select messageid, messageaccountid, messagefolderid, messagefilename, accountaddress "
         "from hm_messages left join hm_accounts on hm_messages.messageaccountid = hm_accounts.accountid "
         "where hm_messages.messagetype = 2 and hm_messages.messageid > @LOW and hm_messages.messageid <= @HIGH");
      command.AddParameter("@LOW", lowExclusive);
      command.AddParameter("@HIGH", highInclusive);

      return ReadIndexableMessages_(command, 0, result);
   }

   bool
   PersistentMessageIndex::GetUnindexedMessages(__int64 highInclusive, int maxCount, std::vector<IndexableMessage> &result)
   {
      // Keyed on the missing CompleteMarker rather than on having no rows at
      // all, so a message whose indexing was interrupted partway - which has
      // rows but no marker, and is therefore scanned on every search - is
      // found and finished rather than left that way forever.
      SQLCommand command(
         "select messageid, messageaccountid, messagefolderid, messagefilename, accountaddress "
         "from hm_messages left join hm_accounts on hm_messages.messageaccountid = hm_accounts.accountid "
         "where hm_messages.messagetype = 2 and hm_messages.messageid <= @HIGH "
         "and not exists (select mitmessageid from hm_messageindexterms where mitmessageid = hm_messages.messageid and mitterm = @COMPLETE)");
      command.AddParameter("@HIGH", highInclusive);
      command.AddParameter("@COMPLETE", String(CompleteMarker()));

      return ReadIndexableMessages_(command, maxCount, result);
   }

   __int64
   PersistentMessageIndex::GetLatestDeliveredMessageId()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // The backfill frontier. Deliberately max over DELIVERED messages only:
   // advancing the cursor past a still-queued row whose id later flips to
   // delivered would leave that message waiting for the reconciliation pass
   // instead of the next batch.
   //---------------------------------------------------------------------------()
   {
      SQLCommand command("select max(messageid) as m from hm_messages where messagetype = 2");

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS || pRS->IsEOF())
         return 0;

      return pRS->GetInt64Value("m");
   }

   bool
   PersistentMessageIndex::ReadMessageIdSet_(SQLCommand &command, std::set<__int64> &result)
   {
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      while (!pRS->IsEOF())
      {
         result.insert(pRS->GetInt64Value("mitmessageid"));
         pRS->MoveNext();
      }

      return true;
   }

   bool
   PersistentMessageIndex::GetMessagesWithTermContaining(__int64 accountID, const String &needle, std::set<__int64> &result)
   {
      // The needle is ASCII-alphanumeric by construction (CreateQueryNeedles),
      // so it carries no LIKE metacharacters and needs no escaping.
      SQLCommand command("select distinct mitmessageid from hm_messageindexterms where mitaccountid = @ACCOUNTID and mitterm like @PATTERN");
      command.AddParameter("@ACCOUNTID", (int) accountID);
      command.AddParameter("@PATTERN", "%" + needle + "%");

      return ReadMessageIdSet_(command, result);
   }

   bool
   PersistentMessageIndex::GetMessagesWithTerm(__int64 accountID, const String &term, std::set<__int64> &result)
   {
      SQLCommand command("select mitmessageid from hm_messageindexterms where mitaccountid = @ACCOUNTID and mitterm = @TERM");
      command.AddParameter("@ACCOUNTID", (int) accountID);
      command.AddParameter("@TERM", term);

      return ReadMessageIdSet_(command, result);
   }
}
