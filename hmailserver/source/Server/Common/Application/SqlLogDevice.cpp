// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com
// See SqlLogDevice.h for the design and the reasoning behind it.

#include "stdafx.h"

#include "SqlLogDevice.h"

#include "ExceptionHandler.h"
#include "IniFileSettings.h"
#include "Logger.h"

#include "../Util/ServerStatus.h"
#include "../Util/Time.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   thread_local bool SqlLogDevice::in_flusher_thread_ = false;

   SqlLogDevice::SqlLogDevice() :
      enabled_(false),
      state_(StateStarting),
      ready_reported_(false),
      entries_written_(0),
      entries_to_file_(0),
      overflow_since_warning_(0),
      next_retry_tick_(0),
      next_overflow_warning_tick_(0),
      next_retention_tick_(0),
      retry_interval_seconds_(0),
      worker_created_(false),
      worker_was_enabled_(false)
   {
   }

   SqlLogDevice::~SqlLogDevice()
   {
   }

   void
   SqlLogDevice::SetEnabled(bool enabled)
   {
      bool threadCreationFailed = false;

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         if (enabled == enabled_)
            return;

         enabled_ = enabled;

         if (enabled_)
         {
            // A fresh activation: the counters that the stop summary reports
            // describe this spell, not the sum of every spell since the service
            // started, and the table has to be ensured again because the
            // administrator may have been fixing the database in between.
            state_ = StateStarting;
            ready_reported_ = false;
            entries_written_ = 0;
            entries_to_file_ = 0;
            overflow_since_warning_ = 0;
            next_retry_tick_ = 0;
            next_retention_tick_ = 0;
            retry_interval_seconds_ = 0;

            if (!worker_created_)
            {
               // Created while the mutex is held, on purpose. The new thread's
               // first act is to take it, so it waits until this function returns;
               // a boost::thread constructor does not wait for the thread to
               // start, so there is nothing here to deadlock against. Creating it
               // outside the lock would let two threads that change the setting at
               // the same time each decide that no thread exists yet.
               try
               {
                  std::function<void()> func = std::bind(&SqlLogDevice::WorkerFunc_, this);
                  worker_ = boost::thread(func);
                  worker_created_ = true;
               }
               catch (...)
               {
                  // No thread, so nothing would ever drain the queue. Going back to
                  // disabled is what makes that safe: Enqueue keeps returning
                  // false and every entry is written to file exactly as it would be
                  // with the file device selected.
                  enabled_ = false;
                  threadCreationFailed = true;
               }
            }
         }
      }

      // Both outside the lock: Set() takes the event's own mutex, and logging
      // reaches the Logger, which takes its own and writes to a file.
      wake_.Set();

      if (threadCreationFailed)
      {
         LOG_APPLICATION(_T("SQL log device: the flush thread could not be started. Log entries are being written to file instead."));
      }
   }

   bool
   SqlLogDevice::Enqueue(const String &category, int logType, long thread, int session,
                         const String &remoteHost, const String &time, const String &message,
                         const String &renderedLine)
   {
      // Checked before the lock is taken, so an entry logged by the flush thread
      // while it holds the mutex cannot even contend for it.
      if (in_flusher_thread_)
         return false;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      if (!enabled_ || state_ != StateReady)
         return false;

      if (queue_.size() >= (size_t) BufferSize)
      {
         // Full. The caller writes the line to file; all we do is count it, so
         // the operator finds out from the application log rather than from a
         // gap in their records.
         overflow_since_warning_++;
         entries_to_file_++;
         return false;
      }

      QueuedEntry entry;
      entry.category = category;
      entry.log_type = logType;
      entry.thread = thread;
      entry.session = session;
      entry.remote_host = remoteHost;
      entry.time = time;
      entry.message = message;
      entry.rendered_line = renderedLine;

      queue_.push_back(entry);

      // Deliberately not signalling the flush thread here. Setting an event costs
      // a mutex and a condition-variable notify per logged line, which is more
      // than the queue push itself; the thread's one-second cadence bounds how
      // long an entry waits, and switching the device off flushes immediately.
      return true;
   }

   void
   SqlLogDevice::WorkerFunc_()
   {
      in_flusher_thread_ = true;

      while (true)
      {
         // The exception barrier is inside the loop rather than around it. Around
         // it, a single unexpected exception would end the flush thread for the
         // life of the process: Enqueue would keep accepting entries (the state is
         // still Ready) until the buffer filled, only then start diverting them to
         // file, and the buffer's worth already accepted would be stranded in
         // memory forever. Per pass, a failure costs one second and the device
         // carries on.
         boost::function<void()> func = boost::bind(&SqlLogDevice::RunPass_, this);
         ExceptionHandler::Run(_T("SqlLogDevice"), func);

         wake_.WaitFor(boost::chrono::milliseconds((int) FlushIntervalMilliseconds));
      }
   }

   void
   SqlLogDevice::RunPass_()
   {
      bool enabled = false;

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);
         enabled = enabled_;
      }

      if (enabled)
      {
         worker_was_enabled_ = true;
         RunOnce_();
      }
      else if (worker_was_enabled_)
      {
         // Just switched off. Entries accepted a moment ago are still queued and
         // must not evaporate because an administrator changed a setting.
         FinalFlush_();
         worker_was_enabled_ = false;
      }
   }

   void
   SqlLogDevice::RunOnce_()
   {
      // Nothing at all is attempted while the retry timer is running, and the gate
      // belongs here rather than beside the table check further down, which is
      // where it looks like it belongs. Below that check, the pool-unavailable path
      // is not gated at all: it would record a fresh failure on every one-second
      // pass, doubling the interval sixty times a minute and writing a line each
      // time to say so. Anything the protocol threads accepted before the device
      // degraded still has to reach the file, hence the drain.
      if (GetState_() == StateDegraded && !ReadyForRetry_())
      {
         DrainToFile_();
         return;
      }

      std::shared_ptr<DatabaseConnectionManager> dbManager;

      switch (GetDatabase_(dbManager))
      {
      case DatabaseNotRunning:
         // Startup, shutdown, or a reinitialize in between. Normal and transient,
         // and it must not be reported as degradation: the property change that
         // switches this device on arrives from PropertySet::Refresh() during
         // Configuration::Load, so every service start with SQL logging selected
         // would otherwise file an error before a single listener exists.
         Suspend_();
         return;

      case DatabaseUnavailable:
         // The server is running but the pool has nothing. Unlike the case above
         // this is a real fault, and the operator needs to be told that the log
         // device they selected is not the one being used.
         DrainToFile_();
         Degrade_(_T("the database connection pool has no connections"));
         return;

      case DatabaseUsable:
         break;
      }

      if (GetState_() != StateReady)
      {
         if (!EnsureTable_(dbManager))
         {
            DrainToFile_();
            Degrade_(_T("the statements that create table hm_log could not be run"));
            return;
         }

         MarkReady_();
      }

      for (int pass = 0; pass < MaxBatchesPerPass; pass++)
      {
         std::vector<QueuedEntry> batch = TakeBatch_();
         if (batch.empty())
            break;

         if (!InsertBatch_(dbManager, batch))
         {
            // The batch in hand plus anything queued behind it: all of it to
            // file, so the degraded window has no hole in it.
            SpillToFile_(batch);
            DrainToFile_();
            Degrade_(_T("a log insert failed"));
            return;
         }

         bool announce = false;

         {
            boost::lock_guard<boost::recursive_mutex> guard(mutex_);

            entries_written_ += (__int64) batch.size();

            // A completed insert is the only evidence that the whole path works -
            // see the note on [IGNORE-ERRORS] in the header for why the create
            // statements are not evidence of anything. So this is also where the
            // retry schedule goes back to its floor: the next outage starts its
            // backoff from MinRetrySeconds rather than continuing the schedule of
            // an outage that is over. An idle device keeps whatever interval it
            // last reached, which only means a device that failed, recovered and
            // failed again without logging anything in between waits longer than
            // the floor before its first retry.
            retry_interval_seconds_ = 0;

            if (!ready_reported_)
            {
               ready_reported_ = true;
               announce = true;
            }
         }

         if (announce)
         {
            LOG_APPLICATION(_T("SQL log device: writing log entries to table hm_log."));
         }
      }

      ReportOverflow_();
      RunRetention_(dbManager);
   }

   void
   SqlLogDevice::FinalFlush_()
   {
      std::shared_ptr<DatabaseConnectionManager> dbManager;

      if (GetState_() == StateReady && GetDatabase_(dbManager) == DatabaseUsable)
      {
         for (int pass = 0; pass < MaxBatchesPerPass; pass++)
         {
            std::vector<QueuedEntry> batch = TakeBatch_();
            if (batch.empty())
               break;

            if (!InsertBatch_(dbManager, batch))
            {
               SpillToFile_(batch);
               break;
            }

            boost::lock_guard<boost::recursive_mutex> guard(mutex_);
            entries_written_ += (__int64) batch.size();
         }
      }

      // Whatever is left - because the database was unusable, or because the
      // loop above gave up - still belongs in the log.
      DrainToFile_();
      ReportOverflow_();

      __int64 written = 0;
      __int64 toFile = 0;

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         written = entries_written_;
         toFile = entries_to_file_;

         state_ = StateStarting;
         ready_reported_ = false;
         next_retry_tick_ = 0;
         retry_interval_seconds_ = 0;
      }

      String message;
      message.Format(_T("SQL log device: stopped. %I64d entries written to table hm_log, %I64d written to file instead."),
         written, toFile);
      LOG_APPLICATION(message);
   }

   void
   SqlLogDevice::Suspend_()
   {
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         // Back to Starting so the table is re-checked once the server is up
         // again, but only from Ready. A device that degraded because of a genuine
         // failure keeps its state and its retry tick, so passing through a
         // restart does not reset the schedule and start attempting a database
         // that is still broken every second.
         if (state_ == StateReady)
         {
            state_ = StateStarting;
            ready_reported_ = false;
         }
      }

      DrainToFile_();
   }

   SqlLogDevice::DatabaseAvailability
   SqlLogDevice::GetDatabase_(std::shared_ptr<DatabaseConnectionManager> &dbManager)
   {
      // Only while the server is fully running. Between StateStopping and the
      // point where Application::ExitInstance releases the connection manager the
      // pool is being torn down, and startup is the same situation in reverse:
      // the property change that enables this device arrives from
      // PropertySet::Refresh() during Configuration::Load, long before any
      // listener exists. Logging in either window goes to file, which is where an
      // administrator looks for startup and shutdown problems anyway.
      if (ServerStatus::Instance()->GetState() != ServerStatus::StateRunning)
         return DatabaseNotRunning;

      // Copied, not referenced. Application::CloseDatabase() resets its member;
      // holding our own shared_ptr keeps the manager alive for the duration of
      // the call instead of having it freed underneath us mid-insert.
      dbManager = Application::Instance()->GetDBManager();

      if (!dbManager)
         return DatabaseUnavailable;

      if (!dbManager->GetIsConnected())
         return DatabaseUnavailable;

      return DatabaseUsable;
   }

   bool
   SqlLogDevice::EnsureTable_(std::shared_ptr<DatabaseConnectionManager> dbManager)
   {
      std::vector<String> statements = GetCreateStatements_();

      if (statements.empty())
      {
         // An unrecognised database type. Nothing sensible can be created, and
         // the caller degrades to file logging. This is also what keeps
         // RunRetention_ away from SQLStatement::GetCurrentTimestampPlusMinutes
         // with an unknown type, which would report HM5407 once an hour.
         return false;
      }

      for (const String &statement : statements)
      {
         SQLCommand command(statement);
         String errorMessage;

         // Every statement carries the [IGNORE-ERRORS] marker that all four
         // backends look for, so "already exists" is not an error and never
         // reaches the error log. A false return here therefore means the
         // statement could not be run at all - a lost connection, typically -
         // rather than that the table is missing; see the header.
         if (!dbManager->Execute(command, 0, 0, errorMessage))
            return false;
      }

      return true;
   }

   bool
   SqlLogDevice::InsertBatch_(std::shared_ptr<DatabaseConnectionManager> dbManager,
                              const std::vector<QueuedEntry> &batch)
   {
      if (SupportsMultiRowInsert_())
      {
         String sql = GetInsertPrefix_();

         bool first = true;
         for (const QueuedEntry &entry : batch)
         {
            if (!first)
               sql += _T(",");
            else
               first = false;

            sql += BuildValuesTuple_(entry);
         }

         SQLCommand command(sql);
         String errorMessage;

         return dbManager->Execute(command, 0, 0, errorMessage);
      }

      // SQL Server Compact does not accept multiple rows in one VALUES clause.
      // One statement per entry is slower - and it borrows a pooled connection
      // per statement, which is why BatchSize is modest - but it is still off the
      // protocol thread, which is what actually matters.
      for (const QueuedEntry &entry : batch)
      {
         String sql = GetInsertPrefix_();
         sql += BuildValuesTuple_(entry);

         SQLCommand command(sql);
         String errorMessage;

         if (!dbManager->Execute(command, 0, 0, errorMessage))
            return false;
      }

      return true;
   }

   void
   SqlLogDevice::RunRetention_(std::shared_ptr<DatabaseConnectionManager> dbManager)
   {
      // Reuses the setting that prunes the log files rather than inventing a
      // second one: an administrator who has decided how long they keep logs has
      // decided it for both devices. 0 - the default - keeps everything, which is
      // what the file device does today.
      int retentionDays = IniFileSettings::Instance()->GetLogDeleteDays();
      if (retentionDays <= 0)
         return;

      if (retentionDays > MaxRetentionDays)
         retentionDays = MaxRetentionDays;

      {
         // Under the lock because SetEnabled resets this tick from whichever
         // thread applied the settings change, and a 64-bit value is not written
         // atomically on the 32-bit build.
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         ULONGLONG now = ::GetTickCount64();
         if (next_retention_tick_ != 0 && now < next_retention_tick_)
            return;

         next_retention_tick_ = now + (ULONGLONG) RetentionIntervalMinutes * 60 * 1000;
      }

      String sql;
      sql.Format(_T("delete from hm_log where logtime < %s"),
         SQLStatement::GetCurrentTimestampPlusMinutes(-(retentionDays * 24 * 60)).c_str());

      SQLCommand command(sql);
      String errorMessage;

      // The result is deliberately not acted on. A retention sweep that fails is
      // a housekeeping problem, not a reason to stop logging, and the DAL has
      // already reported it.
      dbManager->Execute(command, 0, 0, errorMessage);
   }

   std::vector<SqlLogDevice::QueuedEntry>
   SqlLogDevice::TakeBatch_()
   {
      std::vector<QueuedEntry> batch;

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      size_t count = queue_.size();
      if (count > (size_t) BatchSize)
         count = (size_t) BatchSize;

      batch.reserve(count);

      for (size_t i = 0; i < count; i++)
      {
         batch.push_back(queue_.front());
         queue_.pop_front();
      }

      return batch;
   }

   void
   SqlLogDevice::SpillToFile_(const std::vector<QueuedEntry> &batch)
   {
      // Outside the lock: Logger takes its own mutex and touches the file system,
      // and holding ours across that would put a protocol thread's Enqueue behind
      // a disk write.
      for (const QueuedEntry &entry : batch)
      {
         Logger::Instance()->WriteLineToFile(entry.rendered_line, (Logger::LogType) entry.log_type);
      }

      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      entries_to_file_ += (__int64) batch.size();
   }

   void
   SqlLogDevice::DrainToFile_()
   {
      // Bounded per pass for the same reason the insert loop is: a flood must not
      // let one pass run without end. Anything still queued is picked up on the
      // next pass, one second later.
      for (int pass = 0; pass < MaxBatchesPerPass; pass++)
      {
         std::vector<QueuedEntry> batch = TakeBatch_();
         if (batch.empty())
            break;

         SpillToFile_(batch);
      }
   }

   void
   SqlLogDevice::MarkReady_()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      state_ = StateReady;

      // ready_reported_ is deliberately not touched here. The "writing log
      // entries to table hm_log" line belongs to the first insert that actually
      // succeeds, not to the moment the create statements ran without error -
      // those two are not the same thing, and only the first is worth telling
      // anybody about.
   }

   void
   SqlLogDevice::Degrade_(const String &reason)
   {
      int interval = 0;
      bool firstFailure = false;

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         firstFailure = (state_ != StateDegraded);

         state_ = StateDegraded;
         ready_reported_ = false;

         // The schedule is extended on EVERY failure, including a failed retry.
         // Doing it only on the transition into Degraded - the obvious way to
         // write "announce once" - leaves the retry tick in the past after the
         // first interval expires, so the device attempts the database again on
         // every one-second pass and the interval below is never reached. That is
         // the whole reason this function does its own transition bookkeeping
         // instead of returning early when it is already degraded.
         if (retry_interval_seconds_ <= 0)
            retry_interval_seconds_ = MinRetrySeconds;
         else
            retry_interval_seconds_ = retry_interval_seconds_ * 2;

         if (retry_interval_seconds_ > MaxRetrySeconds)
            retry_interval_seconds_ = MaxRetrySeconds;

         interval = retry_interval_seconds_;
         next_retry_tick_ = ::GetTickCount64() + (ULONGLONG) interval * 1000;
      }

      // Reported outside the lock. ErrorManager::ReportError writes to the error
      // log and, when an OnError script is installed, runs it synchronously with
      // full COM access - which reads the database and could re-enter this class.
      String message;
      message.Format(_T("SQL log device: %s. Log entries are being written to file instead; next attempt in %d second(s)."),
         reason.c_str(), interval);

      // One application-log line per attempt: because the interval is at least
      // MinRetrySeconds, that is at most one line a minute for as long as the
      // outage lasts, and it is the only place the operator can see the schedule
      // stretching out.
      LOG_APPLICATION(message);

      if (!firstFailure)
         return;

      // One error report per outage. HM5890 says, once, that the log device the
      // administrator selected is not the one being used. It cannot fire on a
      // default installation, because the default log device is not SQL.
      String reportedMessage;
      reportedMessage.Format(_T("The SQL log device is selected but cannot write to the database: %s. Logging has fallen back to files in the log directory."),
         reason.c_str());

      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5890, "SqlLogDevice::Degrade_", reportedMessage);
   }

   void
   SqlLogDevice::ReportOverflow_()
   {
      __int64 overflowed = 0;

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         if (overflow_since_warning_ <= 0)
            return;

         ULONGLONG now = ::GetTickCount64();
         if (next_overflow_warning_tick_ != 0 && now < next_overflow_warning_tick_)
            return;

         next_overflow_warning_tick_ = now + (ULONGLONG) OverflowWarningIntervalSeconds * 1000;

         overflowed = overflow_since_warning_;
         overflow_since_warning_ = 0;
      }

      String message;
      message.Format(_T("SQL log device: buffer full, %I64d log entries written to file instead. The database is not accepting rows as fast as they are being logged; turn off a log category, or use the file device."),
         overflowed);

      LOG_APPLICATION(message);
   }

   SqlLogDevice::State
   SqlLogDevice::GetState_()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      return state_;
   }

   bool
   SqlLogDevice::ReadyForRetry_()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      if (next_retry_tick_ == 0)
         return true;

      return ::GetTickCount64() >= next_retry_tick_;
   }

   bool
   SqlLogDevice::SupportsMultiRowInsert_()
   {
      switch (IniFileSettings::Instance()->GetDatabaseType())
      {
      case DatabaseSettings::TypeMYSQLServer:
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypePGServer:
         return true;
      case DatabaseSettings::TypeMSSQLCompactEdition:
      case DatabaseSettings::TypeUnknown:
      default:
         return false;
      }
   }

   std::vector<String>
   SqlLogDevice::GetCreateStatements_()
   {
      std::vector<String> statements;

      // The [IGNORE-ERRORS] marker has to survive into the statement text the DAL
      // executes, so it is written as a SQL comment: /* */ for MySQL and
      // PostgreSQL, and the --- form the existing MSSQL and MSSQLCE upgrade
      // scripts use.
      //
      // logtime is second precision on purpose. DATETIME on the MySQL versions
      // hMailServer has shipped with has no fractional part, so a value carrying
      // one would be rounded on one backend and stored on another - the same
      // query would order rows differently depending on the database. The
      // millisecond goes in its own integer column, which is lossless everywhere,
      // and logid gives a stable tie-break inside a millisecond regardless.
      //
      // The MSSQL and MSSQLCE shapes follow those scripts exactly - the column
      // list carries "identity (1, 1)" and the key arrives in a separate ALTER
      // TABLE - because that is the form known to work on SQL Server Compact,
      // where an inline PRIMARY KEY on an identity column is not something this
      // schema has ever relied on.
      switch (IniFileSettings::Instance()->GetDatabaseType())
      {
      case DatabaseSettings::TypeMYSQLServer:
         statements.push_back(
            _T("create table if not exists hm_log")
            _T("(")
            _T("   logid bigint auto_increment not null, primary key(`logid`),")
            _T("   logtime datetime not null,")
            _T("   logms int not null,")
            _T("   logtype varchar(20) not null,")
            _T("   logcategory varchar(20) not null,")
            _T("   logthread int not null,")
            _T("   logsession int not null,")
            _T("   logremoteip varchar(64) not null,")
            _T("   logtext varchar(3000) not null")
            _T(") DEFAULT CHARSET=utf8 /* [IGNORE-ERRORS] */"));

         statements.push_back(
            _T("create index idx_hm_log_logtime on hm_log (logtime) /* [IGNORE-ERRORS] */"));
         break;

      case DatabaseSettings::TypeMSSQLServer:
         statements.push_back(
            _T("create table hm_log")
            _T("(")
            _T("   logid bigint identity (1, 1) not null,")
            _T("   logtime datetime not null,")
            _T("   logms int not null,")
            _T("   logtype nvarchar(20) not null,")
            _T("   logcategory nvarchar(20) not null,")
            _T("   logthread int not null,")
            _T("   logsession int not null,")
            _T("   logremoteip nvarchar(64) not null,")
            _T("   logtext nvarchar(3000) not null")
            _T(") --- [IGNORE-ERRORS]"));

         statements.push_back(
            _T("ALTER TABLE hm_log ADD CONSTRAINT hm_log_pk PRIMARY KEY CLUSTERED (logid) --- [IGNORE-ERRORS]"));

         statements.push_back(
            _T("CREATE INDEX idx_hm_log_logtime ON hm_log (logtime) --- [IGNORE-ERRORS]"));
         break;

      case DatabaseSettings::TypePGServer:
         statements.push_back(
            _T("create table if not exists hm_log")
            _T("(")
            _T("   logid bigserial not null primary key,")
            _T("   logtime timestamp not null,")
            _T("   logms int not null,")
            _T("   logtype varchar(20) not null,")
            _T("   logcategory varchar(20) not null,")
            _T("   logthread int not null,")
            _T("   logsession int not null,")
            _T("   logremoteip varchar(64) not null,")
            _T("   logtext varchar(3000) not null")
            _T(") /* [IGNORE-ERRORS] */"));

         statements.push_back(
            _T("create index if not exists idx_hm_log_logtime on hm_log (logtime) /* [IGNORE-ERRORS] */"));
         break;

      case DatabaseSettings::TypeMSSQLCompactEdition:
         statements.push_back(
            _T("create table hm_log")
            _T("(")
            _T("   logid bigint identity (1, 1) not null,")
            _T("   logtime datetime not null,")
            _T("   logms int not null,")
            _T("   logtype nvarchar(20) not null,")
            _T("   logcategory nvarchar(20) not null,")
            _T("   logthread int not null,")
            _T("   logsession int not null,")
            _T("   logremoteip nvarchar(64) not null,")
            _T("   logtext nvarchar(3000) not null")
            _T(") --- [IGNORE-ERRORS]"));

         statements.push_back(
            _T("ALTER TABLE hm_log ADD CONSTRAINT hm_log_pk PRIMARY KEY CLUSTERED (logid) --- [IGNORE-ERRORS]"));

         statements.push_back(
            _T("CREATE INDEX idx_hm_log_logtime ON hm_log (logtime) --- [IGNORE-ERRORS]"));
         break;

      case DatabaseSettings::TypeUnknown:
      default:
         break;
      }

      return statements;
   }

   String
   SqlLogDevice::GetInsertPrefix_()
   {
      return _T("insert into hm_log (logtime, logms, logtype, logcategory, logthread, logsession, logremoteip, logtext) values ");
   }

   String
   SqlLogDevice::BuildValuesTuple_(const QueuedEntry &entry)
   {
      // Values are escaped and inlined rather than passed as SQLCommand
      // parameters. Two backends inline parameters themselves through
      // SQLStatement::GenerateFromCommand, which substitutes by name with a
      // whole-string Replace - so a fifty-row statement carrying @logtext1 to
      // @logtext50 would have @logtext1 rewritten inside @logtext10 as well, and
      // the batch would land with the wrong text in the wrong rows. Inlining is
      // one mechanism on all four backends and SqlText_ below is the one place
      // that has to be right.
      String tuple;
      tuple.Format(_T("('%s',%d,'%s','%s',%d,%d,'%s','%s')"),
         SqlDateTimeLiteral_(entry.time).c_str(),
         MillisecondPart_(entry.time),
         SqlText_(LogTypeName_(entry.log_type), MaxTokenLength).c_str(),
         SqlText_(entry.category, MaxTokenLength).c_str(),
         (int) entry.thread,
         entry.session,
         SqlText_(entry.remote_host, MaxRemoteHostLength).c_str(),
         SqlText_(entry.message, MaxLogTextLength).c_str());

      return tuple;
   }

   String
   SqlLogDevice::LogTypeName_(int logType)
   {
      // The file the entry would have gone to, kept as a name rather than the
      // enumeration's number so a query stays readable after the enumeration is
      // extended. AWStats, Backup and Events never reach this device - the Logger
      // keeps those three on file because external tools parse them with fixed
      // field lists - but the mapping is complete so an unexpected caller cannot
      // produce an unlabelled row.
      switch (logType)
      {
      case Logger::Normal:
         return _T("NORMAL");
      case Logger::Error:
         return _T("ERROR");
      case Logger::AWStats:
         return _T("AWSTATS");
      case Logger::Backup:
         return _T("BACKUP");
      case Logger::Events:
         return _T("EVENTS");
      case Logger::IMAP:
         return _T("IMAP");
      case Logger::POP3:
         return _T("POP3");
      case Logger::SMTP:
         return _T("SMTP");
      }

      return _T("UNKNOWN");
   }

   String
   SqlLogDevice::SqlText_(const String &value, int maxLength)
   {
      String result = value;

      // Control characters first. A NUL would truncate the statement when it is
      // converted to the driver's narrow encoding - silently, and after the
      // opening quote, which turns a log line into a syntax error - and a bare CR
      // or LF inside a literal only makes the statement harder to read in a query
      // log. The default file rendering has already turned CRLF pairs into "[nl]"
      // where that matters.
      for (int i = 0; i < result.GetLength(); i++)
      {
         if (result[i] < _T(' '))
            result[i] = _T(' ');
      }

      if (result.GetLength() > maxLength)
      {
         // Marked rather than silently clipped, so nobody reads a truncated line
         // as a complete one. maxLength is never smaller than MaxTokenLength, so
         // there is always room for the ellipsis.
         result = result.Mid(0, maxLength - 3) + _T("...");
      }

      return SQLStatement::Escape(result);
   }

   String
   SqlLogDevice::SqlDateTimeLiteral_(const String &time)
   {
      // "yyyy-MM-dd HH:mm:ss.fff" -> "yyyy-MM-dd HH:mm:ss", which every supported
      // backend accepts as a datetime literal - it is the same shape
      // SQLStatement::AddColumnDate produces for every other table. Validated
      // rather than trusted: a malformed literal would fail the whole batch and
      // degrade the device, so a value that is not recognisably a timestamp is
      // replaced with the current time instead.
      if (time.GetLength() >= 19 &&
          time[4] == _T('-') && time[7] == _T('-') && time[10] == _T(' ') &&
          time[13] == _T(':') && time[16] == _T(':'))
      {
         return time.Mid(0, 19);
      }

      return Time::GetCurrentDateTime();
   }

   int
   SqlLogDevice::MillisecondPart_(const String &time)
   {
      if (time.GetLength() >= 23 && time[19] == _T('.'))
         return _ttoi(time.Mid(20, 3).c_str());

      return 0;
   }
}
