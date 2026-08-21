// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include <boost/thread.hpp>

#include "../Persistence/PersistentMessageIndex.h"

namespace HM
{

   class MessageIndexer : public Singleton<MessageIndexer>
   {
   public:
      MessageIndexer();
      ~MessageIndexer(void);

      void Start();
      void Stop();

      void IndexNow();

   private:


      void WorkerFunc();
      void WorkerFuncInternal();

      void IndexMessages_();

      // One bounded pass of the full-text term index (hm_messageindexterms):
      // advances the backfill cursor by at most one batch, or heals a handful
      // of messages the cursor walked past. Answers true when there is nothing
      // left to do, so the caller knows whether to wait the full minute or
      // only pause briefly between passes.
      bool IndexFullTextBatch_();
      bool IndexFullTextMessages_(const std::vector<PersistentMessageIndex::IndexableMessage> &messages);

      boost::thread workerThread_;
	   int iIndexRunCount;

      // Counts loop iterations so the orphaned-terms sweep - a scan of the
      // whole terms table - runs on the full-index cadence rather than every
      // minute.
      int full_text_iteration_count_;

      // True while a reconciliation that found a full batch is still working
      // through its backlog, so it continues at backfill speed instead of one
      // batch per hourly gate.
      bool full_text_reconcile_active_;

      boost::recursive_mutex starterMutex_;

      Event index_now_;
   };
}
