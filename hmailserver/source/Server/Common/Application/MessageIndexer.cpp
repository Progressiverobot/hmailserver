// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "MessageIndexer.h"

#include "../Application/ExceptionHandler.h"
#include "../BO/Message.h"
#include "../BO/MessageData.h"
#include "../BO/MessageMetaData.h"
#include "../MIME/MIME.h"
#include "../Util/Time.h"
#include "../Persistence/PersistentMessageMetaData.h"
#include "../Persistence/PersistentMessage.h"
#include "../Persistence/PersistentMessageIndex.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The reconciliation pass (heal delivered messages the cursor walked
      // past, or whose terms were removed after an edit) runs on this many
      // caught-up loop iterations - about hourly at the one-minute wait. It
      // exists for rare races, so it does not need to be fast, and its
      // anti-join over the message table is not free on a large store.
      const int FullTextReconcileIterations = 60;
   }

   MessageIndexer::MessageIndexer() :
      iIndexRunCount(0),
      full_text_iteration_count_(0),
      full_text_reconcile_active_(false)
   {

   }

   MessageIndexer::~MessageIndexer(void)
   {

   }

   void
   MessageIndexer::Start()
   {
      boost::lock_guard<boost::recursive_mutex> guard(starterMutex_);

      if (workerThread_.joinable())
      {
         if (!workerThread_.timed_join(boost::posix_time::milliseconds(1)))
         {
            // already started.
            return;
         }
      }
      // Start the indexer now.
      LOG_DEBUG("Starting message indexer...");

      std::function<void ()> func = std::bind( &MessageIndexer::WorkerFunc, this );
      workerThread_ = boost::thread(func);

   }

   void
   MessageIndexer::WorkerFunc()
   {
      boost::function<void()> func = boost::bind( &MessageIndexer::WorkerFuncInternal, this );
      ExceptionHandler::Run("MessageIndexer", func);
      LOG_DEBUG("Message indexer stopped.");
   }

   void
   MessageIndexer::WorkerFuncInternal()
   {
      LOG_DEBUG("Message indexer started...");

      PersistentMessageMetaData persistentMetaData;
      persistentMetaData.DeleteOrphanedItems();

      if (IniFileSettings::Instance()->GetIndexerFullTextEnabled())
         PersistentMessageIndex::DeleteOrphanedItems();

      while (true)
      {
         IndexMessages_();

         const bool fullTextCaughtUp = IndexFullTextBatch_();

         full_text_iteration_count_++;

         if (!fullTextCaughtUp)
         {
            // Backfill in progress. A short interruptible pause between
            // bounded passes is the throttle: each pass touches at most one
            // batch, so the database is never held for long, and a service
            // stop lands on either this sleep or the per-message interruption
            // point within a batch. The cursor makes the resume a resume.
            boost::this_thread::sleep_for(boost::chrono::milliseconds(250));
            continue;
         }

         if (IniFileSettings::Instance()->GetIndexerFullTextEnabled())
         {
            // Terms of deleted messages, on the full-index cadence. Stale
            // terms are dead rows, never wrong answers - a search only ever
            // sees messages still in the folder - so this is housekeeping,
            // not correctness, and can be slow.
            int fullMinutes = IniFileSettings::Instance()->GetIndexerFullMinutes();
            if (fullMinutes < 1)
               fullMinutes = 720;

            if ((full_text_iteration_count_ % fullMinutes) == 0)
               PersistentMessageIndex::DeleteOrphanedItems();
         }

         index_now_.WaitFor(boost::chrono::minutes(1));
      }

   }

   void
   MessageIndexer::IndexNow()
   {
      index_now_.Set();
   }

   void
   MessageIndexer::Stop()
   {
      if (workerThread_.joinable())
      {
         if (!workerThread_.timed_join(boost::posix_time::milliseconds(1)))
         {
            // thread is running. interrupt it.
            LOG_DEBUG("Stopping message indexer.");
            workerThread_.interrupt();
         }
      }
   }

   bool
   MessageIndexer::IndexFullTextBatch_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // One bounded pass of the full-text term index. Bounded is the contract:
   // at most one batch of messages is read and indexed per call, so however
   // large the store, no single pass can monopolise the database or the disk.
   // Answers true when there is nothing (more) to do right now.
   //
   // The cursor walk indexes delivered messages in id order and persists the
   // high-water mark after each batch, which is what makes a service stop in
   // the middle of a week-long backfill resume where it left off instead of
   // starting over. Id ranges rather than row counts, with a MIN() jump over
   // empty id space, so the walk needs no ORDER BY/TOP - which every engine
   // spells differently - and re-running a range is idempotent.
   //
   // Any database failure answers true WITHOUT advancing the cursor: the
   // caller then waits the full minute, which is the retry backoff, and
   // nothing is skipped.
   //---------------------------------------------------------------------------()
   {
      if (!IniFileSettings::Instance()->GetIndexerFullTextEnabled())
         return true;

      const int batchSize = IniFileSettings::Instance()->GetIndexerFullTextBatchSize();

      const __int64 frontier = PersistentMessageIndex::GetLatestDeliveredMessageId();
      const __int64 cursor = PersistentMessageIndex::GetBackfillCursor();

      if (cursor < frontier)
      {
         // Jump the cursor over id space with nothing delivered in it, so a
         // store whose ids start in the millions is not walked from zero.
         const __int64 firstAbove = PersistentMessageIndex::GetFirstMessageIdAbove(cursor);
         if (firstAbove == 0 || firstAbove > frontier)
         {
            PersistentMessageIndex::SetBackfillCursor(frontier);
            return true;
         }

         const __int64 low = firstAbove - 1;
         __int64 high = low + batchSize;
         if (high > frontier)
            high = frontier;

         std::vector<PersistentMessageIndex::IndexableMessage> messages;
         if (!PersistentMessageIndex::GetMessagesInRange(low, high, messages))
            return true;

         if (!IndexFullTextMessages_(messages))
            return true;

         if (!PersistentMessageIndex::SetBackfillCursor(high))
            return true;

         return high >= frontier;
      }

      // Caught up. Occasionally reconcile: a message the cursor passed before
      // its delivery committed, an interrupted batch, or terms deleted after
      // an edit all lack the complete-marker and are scanned on every search
      // until this finds them. Once a reconciliation finds a full batch it
      // stays active across passes rather than waiting for the next hourly
      // gate, so a real backlog heals at backfill speed.
      if (!full_text_reconcile_active_ && (full_text_iteration_count_ % FullTextReconcileIterations) != 0)
         return true;

      std::vector<PersistentMessageIndex::IndexableMessage> unindexed;
      if (!PersistentMessageIndex::GetUnindexedMessages(cursor, batchSize, unindexed))
         return true;

      if (unindexed.empty())
      {
         full_text_reconcile_active_ = false;
         return true;
      }

      if (!IndexFullTextMessages_(unindexed))
         return true;

      // A full batch means there may be more to heal; an emptier one means
      // this reconciliation is done.
      full_text_reconcile_active_ = (int) unindexed.size() >= batchSize;
      return !full_text_reconcile_active_;
   }

   bool
   MessageIndexer::IndexFullTextMessages_(const std::vector<PersistentMessageIndex::IndexableMessage> &messages)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Reads, tokenises and stores the terms for a batch of messages. The text
   // tokenised is EXACTLY the text the searcher scans - the raw header and the
   // body/HTML-body pair selected by MessageData's multipart walk, obtained
   // through the same MessageData the scan itself uses - because the index is
   // only allowed to say "cannot contain", and it can only say that about the
   // string the scan would actually search.
   //---------------------------------------------------------------------------()
   {
      const size_t maxTerms = (size_t) IniFileSettings::Instance()->GetIndexerFullTextMaxTokensPerMessage();

      for (const PersistentMessageIndex::IndexableMessage &messageToIndex : messages)
      {
         boost::this_thread::interruption_point();

         std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message);
         message->SetID(messageToIndex.MessageID);
         message->SetAccountID(messageToIndex.AccountID);
         message->SetPartialFileName(messageToIndex.FileName);

         std::set<String> terms;
         bool overflowed = false;

         std::shared_ptr<MessageData> messageData = std::shared_ptr<MessageData>(new MessageData());

         if (messageData->LoadFromMessage(messageToIndex.FileName, message))
         {
            // Header first; body and HTML body directly concatenated after
            // it, with nothing in between. SEARCH BODY scans GetBody() +
            // GetHTMLBody() as ONE string, so a token straddling that
            // junction is real and must exist in the index as the merged
            // token. Concatenation can only merge tokens, never split them,
            // and the searcher's needles are substring (LIKE) probes, so a
            // merged token still answers for its halves.
            // The text-bearing attachments as well, in the same concatenation the
            // BODY scan reads: the index may only ever exclude, so it has to know
            // about every piece of text the scan would search.
            String searchableText = messageData->GetHeader() + messageData->GetBody() + messageData->GetHTMLBody() + messageData->GetAttachmentText();

            PersistentMessageIndex::TokenizeForIndex(searchableText, maxTerms, terms, overflowed);
         }
         else
         {
            // Unreadable right now - locked by a scanner, missing, or over the
            // MIME parser's size cap. The overflow marker keeps it a candidate
            // for every search, which is exactly what a message the scan also
            // cannot read gets today.
            overflowed = true;
         }

         if (!PersistentMessageIndex::SaveTermsForMessage(messageToIndex.MessageID, messageToIndex.AccountID, terms, overflowed))
         {
            String errorMessage;
            errorMessage.Format(_T("Failed to save full-text index terms for message %I64d. The batch will be retried."),
               messageToIndex.MessageID);

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6260, "MessageIndexer::IndexFullTextMessages_", errorMessage);
            return false;
         }
      }

      return true;
   }

   void
   MessageIndexer::IndexMessages_()
   {
      // Default is 720
      int iIndexerFullMin = IniFileSettings::Instance()->GetIndexerFullMinutes();

      PersistentMessageMetaData persistentMetaData;
      // set quickIndex here so the loop will allow loading of an incomplete index
      bool bDoQuickIndex = true;
      if ((iIndexRunCount > iIndexerFullMin) || (iIndexRunCount == 0)){
        bDoQuickIndex = false;
        iIndexRunCount = 1;
      }

      while (true)
      {

         // added the boolean quickIndex to tell the funciton to use the quick index or the full index
         std::set<std::shared_ptr<PersistentMessageMetaData::MessageInfo> > messagesToIndex = persistentMetaData.GetMessagesToIndex(bDoQuickIndex);
         if (messagesToIndex.size() == 0)
         {
            LOG_DEBUG("No messages to index.");
            iIndexRunCount++;  //JDR
            // No messages to index found.
            return;
         }

         for(std::shared_ptr<PersistentMessageMetaData::MessageInfo> messageToIndex : messagesToIndex)
         {
            boost::this_thread::interruption_point();

            AnsiString headerText = PersistentMessage::LoadHeader(messageToIndex->FileName, false);

            MimeHeader header;
            header.Load(headerText, headerText.GetLength(), true);


            String dateString = header.GetUnicodeFieldValue("Date");
            DateTime date = Time::GetDateTimeFromMimeHeader(dateString);

            String from = header.GetUnicodeFieldValue("From");
            String subject = header.GetUnicodeFieldValue("Subject");
            String cc = header.GetUnicodeFieldValue("CC");
            String to = header.GetUnicodeFieldValue("TO");

            std::shared_ptr<MessageMetaData> metaData = std::shared_ptr<MessageMetaData>(new MessageMetaData);

            metaData->SetAccountID(messageToIndex->AccountID);
            metaData->SetFolderID(messageToIndex->FolderID);
            metaData->SetMessageID(messageToIndex->MessageID);

            metaData->SetDate(date);
            metaData->SetFrom(from);
            metaData->SetSubject(subject);
            metaData->SetCC(cc);
            metaData->SetTo(to);

            if (!persistentMetaData.SaveObject(metaData))
            {
               LOG_DEBUG("Error saving the index.")
               // Error saving. Abort now...
               return;
            }
         }
      }
      iIndexRunCount++;
   }
}
