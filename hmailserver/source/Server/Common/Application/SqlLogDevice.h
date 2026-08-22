// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../Util/Event.h"

#include <deque>
#include <vector>

namespace HM
{
   class DatabaseConnectionManager;

   // The SQL logging device - eLogDevice::hLogDeviceSQL.
   //
   // WHY THIS EXISTS: the value has been declared in the IDL, settable through
   // COM (Settings.Logging.Device) and offered in the Control Panel's Logging
   // page for years, and nothing ever read it. An administrator could select
   // "SQL", get no error, and lose their logging entirely - the worst kind of
   // gap, because the setting appeared to work. This class is what the setting
   // now selects.
   //
   // THE THREE CONSTRAINTS THAT SHAPE THE WHOLE DESIGN:
   //
   //  1. A log write must never block a protocol thread. Enqueue() therefore
   //     takes one short mutex, copies the entry and returns. It never opens a
   //     connection, never waits on the pool and never touches the database. All
   //     database work happens on the single flush thread below.
   //
   //  2. A log write must never share a transaction with mail data. The flush
   //     thread borrows its own connection from the pool through
   //     DatabaseConnectionManager::Execute (auto-commit; BeginTransaction is
   //     never called), so a log insert can neither be rolled back by a failing
   //     message save nor hold a transaction open across one. It does share the
   //     connection *pool* with mail work: it holds one connection for the length
   //     of an insert, once a second. If every connection is busy the flush
   //     thread waits for one - bounded by [Settings] DBConnectionAcquireTimeout,
   //     60 seconds by default - and it is always the flush thread that waits,
   //     never a session.
   //
   //  3. Logging must survive the database being unavailable, which is precisely
   //     when the log matters most. Every failure path in here ends in the file
   //     device, not in a discarded entry: the device marks itself degraded,
   //     writes everything already queued to file, says so, and retries on a
   //     doubling timer (MinRetrySeconds, doubling per failed attempt, capped at
   //     MaxRetrySeconds). Nothing is dropped on the floor.
   //
   // WHY THE BUFFER IS BOUNDED AND WHAT "FULL" MEANS: an unbounded queue in front
   // of a slow database is a memory leak with a plausible excuse - a busy server
   // logging faster than the database accepts rows would grow it until the
   // process died, taking mail delivery with it. So the queue has a hard ceiling
   // (BufferSize). When it is reached, Enqueue() returns false and the caller
   // writes the line to the file exactly as it would with the file device
   // selected; the number of entries that took that route is counted and
   // reported in the application log. Discarding the entry instead - the other
   // obvious reading of "drop when full" - was rejected: silently losing log
   // lines is the failure this whole item exists to fix, and a file write costs
   // no more than it does for every installation that uses the file device.
   //
   // WHY hm_log AND NOT hm_serverlog: hMailServer's 1200-to-1400 database upgrade
   // created an hm_serverlog table for this device (MySQL and MS SQL only, and no
   // current CreateTables script creates it - CreateTablesMSSQL only drops it - so
   // an installation has one only if it once ran that upgrade script). Its columns
   // cannot carry what the Logger now produces: logremotehost is varchar(15),
   // which cannot hold an IPv6 address - on a strict MySQL that is a failed
   // insert, everywhere else a silently mislabelled peer - logtext is varchar(600)
   // on MS SQL, and there is no column for the session id, which is the field that
   // ties the lines of one conversation together. Widening the old table instead
   // would mean an ALTER on a table that only exists on some installations, so the
   // device writes its own and leaves any inherited hm_serverlog untouched.
   //
   // WHY THE TABLE IS CREATED ON DEMAND rather than by a schema upgrade: the
   // device is off by default, so requiring every installation to run DBUpdater
   // for a table most of them will never use would be a poor trade, and it keeps
   // the shipped schema - the thing DBUpdater has to reproduce on four backends -
   // unchanged. The create statements carry the DAL's [IGNORE-ERRORS] marker so
   // that a table which already exists costs two or three no-op statements per
   // activation and never reaches the error log. The consequence is that EnsureTable_
   // returning true does NOT prove the table exists: the marker swallows a
   // genuine failure (no permission, a row-size limit) just as happily. The first
   // INSERT is the real proof, and its failure is what degrades the device. That
   // is a better trade than a dialect-specific INFORMATION_SCHEMA probe, whose own
   // failure would be reported as an error before we could decide it was expected.
   //
   // WHY THE FLUSH THREAD IS NEVER JOINED: it is created the first time the SQL
   // device is selected and then lives for the life of the process, idling on a
   // one-second wait when the device is off. Stopping and restarting a thread
   // from whichever COM thread happens to change the setting means joining while
   // that thread may be inside a database call - either an unbounded wait on the
   // caller or a detached thread that can never be safely restarted. Flags are
   // cheaper and cannot deadlock. The thread refuses to touch the database unless
   // the server is in StateRunning, which keeps it away from the connection
   // manager while Application is building it up or tearing it down.
   class SqlLogDevice : public Singleton<SqlLogDevice>
   {
   public:

      SqlLogDevice();
      ~SqlLogDevice();

      enum Constants
      {
         // hm_log.logtext is varchar/nvarchar(3000). The width is set by SQL
         // Server Compact, which rejects a table whose defined row exceeds 8060
         // bytes and counts nvarchar in full: the other columns are 236 bytes
         // (8 + 8 + 4 + 40 + 40 + 4 + 4 + 128), so 3000 wide characters is 6236
         // and well inside it, while 4000 would be 8236 and the CREATE TABLE
         // would fail - silently, because of the [IGNORE-ERRORS] marker above.
         // Longer entries are truncated with a trailing ellipsis, the same
         // bargain the file device already makes via MaxLogLineLen.
         MaxLogTextLength = 3000,
         MaxRemoteHostLength = 64,
         MaxTokenLength = 20,

         // How long the flush thread sleeps between passes. Entries are visible
         // in the table within roughly this long; switching the device off
         // flushes immediately rather than waiting.
         FlushIntervalMilliseconds = 1000,

         // Entries that may wait in memory for the flush thread. 20,000 is a few
         // megabytes at the row limit above and about ten seconds of head-room at
         // the drain rate below, which is enough to ride out a slow statement or
         // a connection reconnect without spilling. Deliberately not a setting:
         // the only thing an operator could do with it is raise it until the
         // process runs out of memory, which is the failure the ceiling exists to
         // prevent. The lever that actually helps is logging less, or fixing the
         // database.
         BufferSize = 20000,

         // Rows per INSERT. 50 rows at the text limit above is a statement of about
         // 155,000 characters in the worst case, which stays inside even the 1 MB
         // max_allowed_packet that older MySQL versions default to, and well inside
         // the 1,000-row ceiling SQL Server places on a multi-row VALUES clause.
         BatchSize = 50,

         // Batches drained per pass, so the ceiling is 2,000 entries a second.
         // The cap stops one pass from running without end during a flood - the
         // rest waits in the buffer and is picked up a second later, or spills to
         // file if the buffer fills. On SQL Server Compact, which has no multi-row
         // VALUES clause and therefore takes one statement per row, the real
         // ceiling is far below this, and sustained heavy protocol logging on that
         // backend will spill to file. That is the design working, not failing.
         MaxBatchesPerPass = 40,

         // The retry schedule: MinRetrySeconds after the first failure, doubling
         // on each failed attempt, capped at MaxRetrySeconds. A successful insert
         // puts it back to the floor.
         MinRetrySeconds = 60,
         MaxRetrySeconds = 900,

         OverflowWarningIntervalSeconds = 60,
         RetentionIntervalMinutes = 60,

         // Guards the multiplication in the retention cut-off. 100 years of
         // retention is indistinguishable from "keep everything" and keeps
         // days * 24 * 60 inside an int.
         MaxRetentionDays = 36500
      };

      // Switches the device on or off. Called from Logger::SetLogDevice, which is
      // driven by the logdevice property - including the change event
      // PropertySet::Refresh() raises for every setting at startup.
      void SetEnabled(bool enabled);

      // Accepts one entry for asynchronous insertion.
      //
      // Returns false when the caller must write the entry somewhere else - the
      // device is off, the database is not currently usable, the buffer is full,
      // or the calling thread is the flush thread itself. Logger treats false as
      // "write it to the file", so a false return is a routed entry, never a lost
      // one.
      //
      // renderedLine is the entry already formatted for the file device; it is
      // carried alongside the fields so that a batch which cannot be inserted can
      // still be written to file byte-for-byte as it would have been.
      bool Enqueue(const String &category, int logType, long thread, int session,
                   const String &remoteHost, const String &time, const String &message,
                   const String &renderedLine);

   private:

      enum State
      {
         // Table not yet ensured for this activation. Entries are not accepted:
         // the queue must not fill with rows for a table that may not exist.
         StateStarting = 0,

         // Table ensured. Entries are accepted.
         StateReady = 1,

         // The database or the table let us down. Entries are not accepted (they
         // go to file at the call site) until the retry timer expires.
         StateDegraded = 2
      };

      // Why the flush thread may not talk to the database, because the two
      // reasons need opposite handling: one is a normal phase of the service
      // lifecycle and must stay silent, the other is a fault the operator has to
      // be told about.
      enum DatabaseAvailability
      {
         DatabaseUsable = 0,

         // Startup, shutdown, or a reinitialize in between.
         DatabaseNotRunning = 1,

         // The server is running but the pool has no connections.
         DatabaseUnavailable = 2
      };

      struct QueuedEntry
      {
         QueuedEntry() : log_type(0), thread(0), session(-1) {}

         String category;
         int log_type;          // Logger::LogType, kept as an int so this header
                                // does not have to include Logger.h - Logger.cpp
                                // includes this one.
         long thread;
         int session;
         String remote_host;
         String time;
         String message;
         String rendered_line;
      };

      void WorkerFunc_();

      // One iteration of the flush thread, run under an exception barrier.
      void RunPass_();

      // One pass of the flush thread while the device is enabled.
      void RunOnce_();

      // Final pass after the device is switched off: flush what is queued, then
      // report what the device managed to do.
      void FinalFlush_();

      // The server is not running, so the database is off limits for reasons that
      // are entirely normal. Puts the device back to Starting (unless it is
      // genuinely degraded, whose retry schedule must survive a restart) and
      // empties the queue to file. Deliberately silent: this is not a fault.
      void Suspend_();

      // Hands back its own shared_ptr to the manager so that Application
      // resetting its member mid-call cannot free it underneath us.
      DatabaseAvailability GetDatabase_(std::shared_ptr<DatabaseConnectionManager> &dbManager);

      bool EnsureTable_(std::shared_ptr<DatabaseConnectionManager> dbManager);
      bool InsertBatch_(std::shared_ptr<DatabaseConnectionManager> dbManager,
                        const std::vector<QueuedEntry> &batch);

      // Deletes rows older than [Settings] LogDeleteDays, the same window that
      // prunes the log *files* through LogRetentionTask, at most once an hour.
      // 0 (the default) keeps everything, matching what the file device does.
      void RunRetention_(std::shared_ptr<DatabaseConnectionManager> dbManager);

      std::vector<QueuedEntry> TakeBatch_();

      // Writes entries to the file device and counts them as having gone there.
      void SpillToFile_(const std::vector<QueuedEntry> &batch);

      // Empties the queue to the file device. Used whenever the database is not
      // usable, so accepted entries never sit in memory waiting for a recovery
      // that may not come.
      void DrainToFile_();

      // The table has been ensured: start accepting entries again.
      void MarkReady_();

      // Records a failure, reschedules the next attempt further out than the last
      // one, and says so. See the retry-schedule note in Constants.
      void Degrade_(const String &reason);

      void ReportOverflow_();

      State GetState_();
      bool ReadyForRetry_();

      // Multi-row VALUES: supported by MySQL, SQL Server and PostgreSQL, not by
      // SQL Server Compact, which gets one statement per row.
      static bool SupportsMultiRowInsert_();

      static std::vector<String> GetCreateStatements_();

      // "insert into hm_log (...) values ". Written in one place so the INSERT
      // and the CREATE TABLE column lists cannot drift apart unnoticed.
      static String GetInsertPrefix_();

      static String BuildValuesTuple_(const QueuedEntry &entry);
      static String LogTypeName_(int logType);

      // Truncates, strips control characters and escapes for inclusion as a
      // single-quoted SQL literal.
      static String SqlText_(const String &value, int maxLength);

      static String SqlDateTimeLiteral_(const String &time);
      static int MillisecondPart_(const String &time);

      // Set on the flush thread only. Any entry logged by that thread - a failed
      // insert reported by the DAL, a slow-query note, our own status lines -
      // must go straight to file: queueing it would let the device feed itself
      // faster than it can drain, and every failure would multiply.
      static thread_local bool in_flusher_thread_;

      // One mutex for the queue, the state and the counters. It is never held
      // across a database call or a file write, so the hot path (Enqueue) never
      // waits on I/O and the flush thread never blocks a protocol thread.
      boost::recursive_mutex mutex_;

      std::deque<QueuedEntry> queue_;

      bool enabled_;
      State state_;

      // Whether the "writing to the table" line has been emitted for the current
      // healthy spell, so recovery is announced exactly once rather than every
      // pass.
      bool ready_reported_;

      __int64 entries_written_;
      __int64 entries_to_file_;
      __int64 overflow_since_warning_;

      ULONGLONG next_retry_tick_;
      ULONGLONG next_overflow_warning_tick_;
      ULONGLONG next_retention_tick_;
      int retry_interval_seconds_;

      // Whether the flush thread exists. Kept separate from worker_.joinable() so
      // that the decision to create it, and the flag that says it was created,
      // are both made under the mutex.
      bool worker_created_;

      // Touched only by the flush thread, so it needs no synchronisation: it
      // exists so that switching the device off is noticed exactly once and the
      // final flush happens exactly once.
      bool worker_was_enabled_;

      Event wake_;
      boost::thread worker_;
   };
}
