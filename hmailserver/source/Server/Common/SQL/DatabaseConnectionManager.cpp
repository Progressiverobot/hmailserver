// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include ".\databaseconnectionmanager.h"

#include "DALConnection.h"
#include "DALConnectionFactory.h"
#include "DatabaseSettings.h"
#include "DatabaseUnavailableMarker.h"

#include "ADORecordset.h"
#include "MySQLRecordset.h"
#include "PGRecordset.h"
#include "SQLCERecordset.h"
#include "MySQLInterface.h"

#include "SQLCommand.h"

#include "Prerequisites/PrerequisiteList.h"
#include "SQLScriptRunner.h"

#include "../Util/ServerStatus.h"
#include "../Util/OtelTracer.h"

#include <boost/chrono.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   DatabaseConnectionManager::DatabaseConnectionManager(void)
   {
   }

   DatabaseConnectionManager::~DatabaseConnectionManager(void)
   {
   }

   bool
   DatabaseConnectionManager::CreateConnections(String &sErrorMessage)
   {
      int iNoOfConnectionAttempts = IniFileSettings::Instance()->GetNumberOfDatabaseConnectionAttempts();
      int iDelayBetweenConnectionAttempt = IniFileSettings::Instance()->GetDBConnectionAttemptsDelay() * 1000;


      bool bConnectionOK = false;
      for (int iTry = 1; iTry <= iNoOfConnectionAttempts ; iTry++)
      {     
         DALConnection::ConnectionResult iResult = Connect_(sErrorMessage);

         switch (iResult)
         {
         case DALConnection::Connected:
            // Reset the error message. If we failed to connect the first time,
            // but succeeded the second, it's not really an error to care about.
            sErrorMessage = "";
            return true;
         case DALConnection::FatalError:
            // Skip out and report error.
            return false;
         case DALConnection::TemporaryFailure:
            // We failed to connect to the database server.
            // Pause a few seconds and then try again.
            Sleep(iDelayBetweenConnectionAttempt);
            break;
         }




      }  

      return false;
   }

   DALConnection::ConnectionResult
   DatabaseConnectionManager::Connect_(String &sErrorMessage)
   {
      int iNoOfConnections = IniFileSettings::Instance()->GetNumberOfDatabaseConnections();

      IniFileSettings *pIniFileSettings = IniFileSettings::Instance();

      String sProvider = pIniFileSettings->GetDatabaseProvider();
      String sServer = pIniFileSettings->GetDatabaseServer();
      String sUsername = pIniFileSettings->GetUsername();
      String sPassword = pIniFileSettings->GetPassword();
      String sDatabase = pIniFileSettings->GetDatabaseName();
      String sDatabaseDirectory = pIniFileSettings->GetDatabaseDirectory();
      String sDatabaseServerFailoverPartner = pIniFileSettings->GetDatabaseServerFailoverPartner();
      long lDBPort = pIniFileSettings->GetDatabasePort();

      HM::DatabaseSettings::SQLDBType dbType = IniFileSettings::Instance()->GetDatabaseType();

      std::shared_ptr<DatabaseSettings> pSettings = std::shared_ptr<DatabaseSettings> 
         (new DatabaseSettings(sProvider, sServer, sDatabase, sUsername, sPassword, sDatabaseDirectory, sDatabaseServerFailoverPartner, dbType, lDBPort));

      for (int i = 0; i < iNoOfConnections; i++)
      {
         std::shared_ptr<DALConnection> pConnection = DALConnectionFactory::CreateConnection(pSettings);
         DALConnection::ConnectionResult result = pConnection->Connect(sErrorMessage);

         if (result != DALConnection::Connected)
            return result;

         available_connections_.insert(pConnection);
      }

      // Fetch first connection
      auto iter = available_connections_.begin();
      if (!(*iter)->CheckServerVersion(sErrorMessage))
         return DALConnection::FatalError;

      (*iter)->OnConnected();

      return DALConnection::Connected;
   }

   void
   DatabaseConnectionManager::Disconnect()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      //auto iterConnection = available_connections_.begin();
      for(std::shared_ptr<DALConnection> pConnection : available_connections_)
      {
         pConnection->Disconnect();
      }
      available_connections_.clear();

      for(std::shared_ptr<DALConnection> pConnection : busy_connections_)
      {
         pConnection->Disconnect();
      }

      busy_connections_.clear();
   }
   
   bool 
   DatabaseConnectionManager::Execute(const SQLStatement &statement, __int64 *iInsertID, int iIgnoreErrors, String &sErrorMessage)
   {
      return Execute(statement.GetCommand(), iInsertID, iIgnoreErrors, sErrorMessage);
   }

   bool 
   DatabaseConnectionManager::Execute(const SQLCommand &command, __int64 *iInsertID, int iIgnoreErrors, String &sErrorMessage)
   {
      if (SimulateFailureFor_(command))
      {
         sErrorMessage = "Simulated database failure ([Settings] SimulateDatabaseFailureFor).";
         return false;
      }

      std::shared_ptr<DALConnection> pDALConn = GetConnection_();

      if (!pDALConn)
      {
         // Acquisition can legitimately fail (pool exhausted past its deadline).
         // GetConnection_ has already reported it.
         LOG_DEBUG("Aborting statement since no database connection could be acquired.");
         return false;
      }

      boost::chrono::steady_clock::time_point queryStart = boost::chrono::steady_clock::now();

      bool bResult = pDALConn->Execute(command, sErrorMessage, iInsertID, iIgnoreErrors);

      unsigned __int64 elapsedMicros = (unsigned __int64) boost::chrono::duration_cast<boost::chrono::microseconds>(
         boost::chrono::steady_clock::now() - queryStart).count();
      MeasureQuery_(command, elapsedMicros);

      ReleaseConnection_(pDALConn);

      return bResult;
   }

   std::shared_ptr<DALRecordset>
   DatabaseConnectionManager::OpenRecordset(const SQLStatement &statement)
   {
      return OpenRecordset(statement.GetCommand());
   }

   /*
      Test-only fault injection. True when [Settings] SimulateDatabaseFailureFor is set
      and this statement contains it, in which case the caller is answered exactly as it
      would be by a database that refused the statement.

      Why it exists: three sweeps in August 2026 fixed 51 places where the result of a
      database operation was discarded, and every defect found was in error handling
      that had never once executed - a `return true` where the delete had failed, a
      String passed through a variadic %s in the line that reports a failed restore, a
      UID counter read back after an unchecked increment. Those 51 fixes added 51 more
      branches that also never execute. Without a way to run them, the fixes are
      untested code of exactly the kind that was just found to be wrong.

      Why a substring of the statement and not a mode: the server has delivery threads,
      scheduled tasks and protocol sessions all issuing statements at once, so "fail the
      next statement" would fail whatever happened to be next. Naming the statement -
      "update hm_imapfolders set foldercurrentuid" - fails precisely one write and
      leaves everything else working, which is the only thing that makes this usable
      against a running server.

      Why it is safe: empty out of the box, so the cost is one IsEmpty() per statement
      and the behaviour is unchanged. It cannot be set remotely or over COM - only by
      editing hMailServer.INI and reinitialising - and Application::Reinitialize reports
      HM6119 for as long as it is set, because the failure mode of a facility like this
      is being quietly left on, not being turned on. SimulateSpoolWriteFailure and
      CrashSimulationMode are the same idea for the disk and the crash handler.
   */
   bool
   DatabaseConnectionManager::SimulateFailureFor_(const SQLCommand &command)
   {
      // The bool first, and it is the whole reason this is affordable: this runs before
      // every statement the server issues, and the String getters in IniFileSettings
      // return by value, so asking for the pattern here would put a heap copy on the
      // path of every query to support a facility that is off on every real server.
      if (!IniFileSettings::Instance()->GetSimulateDatabaseFailureEnabled())
         return false;

      const String pattern = IniFileSettings::Instance()->GetSimulateDatabaseFailureFor();

      if (pattern.IsEmpty())
         return false;

      return command.GetQueryString().Find(pattern) >= 0;
   }

   std::shared_ptr<DALRecordset> 
   DatabaseConnectionManager::OpenRecordset(const SQLCommand &command)
   {
      std::shared_ptr<DALRecordset> pRecordset;

      // Reads as well as writes: several of the defects this exists to test are on the
      // read side - ReadRecipients_ failing made a queued message look recipient-less,
      // and the delivery manager then deleted it.
      if (SimulateFailureFor_(command))
         return pRecordset;

      std::shared_ptr<DALConnection> pDALConn = GetConnection_();

      if (!pDALConn)
      {
         // Acquisition can legitimately fail (pool exhausted past its deadline).
         // GetConnection_ has already reported it.
         LOG_DEBUG("Aborting statement since no database connection could be acquired.");
         return pRecordset;
      }

      pRecordset = pDALConn->CreateRecordset();

      boost::chrono::steady_clock::time_point queryStart = boost::chrono::steady_clock::now();

      bool bOpened = pRecordset->Open(pDALConn, command);

      unsigned __int64 elapsedMicros = (unsigned __int64) boost::chrono::duration_cast<boost::chrono::microseconds>(
         boost::chrono::steady_clock::now() - queryStart).count();
      MeasureQuery_(command, elapsedMicros);

      if (!bOpened)
      {
         // A query that could not be run is also "the database did not answer",
         // not "the database answered with nothing" - a dropped connection or a
         // locked table reaches here rather than the acquisition failure above.
         DatabaseUnavailableMarker::Mark();

         pRecordset.reset();
      }

      ReleaseConnection_(pDALConn);

      return pRecordset;


   }

   void
   DatabaseConnectionManager::MeasureQuery_(const SQLCommand &command, unsigned __int64 microseconds)
   {
      int slowThresholdMs = IniFileSettings::Instance()->GetSlowQueryLogMilliseconds();
      bool wasSlow = slowThresholdMs > 0 && (microseconds / 1000) >= (unsigned __int64) slowThresholdMs;

      ServerStatus::Instance()->OnDatabaseQuery(microseconds, wasSlow);

      if (wasSlow)
      {
         String logLine;
         logLine.Format(_T("Slow database query (%I64u ms): %s"),
            microseconds / 1000, RedactSqlLiterals_(command.GetQueryString()).c_str());
         LOG_APPLICATION(logLine);
      }

      // Emit an OpenTelemetry span for the statement (redacted) when tracing is on.
      // Parents to the active protocol-command span on this thread when present.
      if (OtelTracer::Instance()->IsEnabled())
      {
         std::vector<OtelAttribute> attributes;

         OtelAttribute system;
         system.key = "db.system";
         switch (IniFileSettings::Instance()->GetDatabaseType())
         {
         case HM::DatabaseSettings::TypeMYSQLServer: system.value = "mysql"; break;
         case HM::DatabaseSettings::TypeMSSQLServer: system.value = "mssql"; break;
         case HM::DatabaseSettings::TypePGServer: system.value = "postgresql"; break;
         case HM::DatabaseSettings::TypeMSSQLCompactEdition: system.value = "sqlce"; break;
         default: system.value = "unknown"; break;
         }
         attributes.push_back(system);

         OtelAttribute statement;
         statement.key = "db.statement";
         statement.value = RedactSqlLiterals_(command.GetQueryString());
         attributes.push_back(statement);

         OtelTracer::Instance()->RecordCompletedSpan("db.query", OtelSpanKindClient, microseconds, attributes);
      }
   }

   String
   DatabaseConnectionManager::RedactSqlLiterals_(const String &sql)
   {
      // Replace the contents of every single-quoted string literal with a single
      // '?' so any secret inlined into the statement text is never logged. Handles
      // the SQL '' escaped quote and the MySQL \' backslash escape so a literal is
      // not closed prematurely. Over-masking on a malformed/unbalanced quote is
      // acceptable (it only ever removes more, never less).
      String result;
      int len = sql.GetLength();
      bool inString = false;
      bool maskedCurrent = false;

      for (int i = 0; i < len; i++)
      {
         TCHAR c = sql[i];

         if (!inString)
         {
            result += c;
            if (c == _T('\''))
            {
               inString = true;
               maskedCurrent = false;
            }
            continue;
         }

         // Inside a string literal.
         if (c == _T('\\') && i + 1 < len)
         {
            // Backslash escape (MySQL default): consume the escaped character too.
            i++;
            if (!maskedCurrent) { result += _T('?'); maskedCurrent = true; }
            continue;
         }

         if (c == _T('\''))
         {
            if (i + 1 < len && sql[i + 1] == _T('\''))
            {
               // Doubled '' escaped quote: still inside the literal.
               i++;
               if (!maskedCurrent) { result += _T('?'); maskedCurrent = true; }
               continue;
            }

            // Closing quote.
            if (!maskedCurrent)
               result += _T('?');
            result += _T('\'');
            inString = false;
            continue;
         }

         // Ordinary character inside the literal: emit one '?' for the whole run.
         if (!maskedCurrent) { result += _T('?'); maskedCurrent = true; }
      }

      return result;
   }

   void
   DatabaseConnectionManager::ReleaseConnection_(std::shared_ptr<DALConnection> pConnection)
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);

      auto iterConnection = busy_connections_.find(pConnection);
      if (iterConnection == busy_connections_.end())
      {
         assert(0);
         return;
      }

      busy_connections_.erase(iterConnection);

      // Locate an available connection
      available_connections_.insert(pConnection);

      // Wake one waiter (if any) in GetConnection_ now that a connection is free.
      connection_released_.notify_one();
   }

   int 
   DatabaseConnectionManager::GetCurrentDatabaseVersion()
   {
      SQLCommand command("select * from hm_dbversion");
      std::shared_ptr<DALRecordset> pRS = OpenRecordset(command);
      if (!pRS)
         return 0;

      int iRetVal = pRS->GetLongValue("value");

      return iRetVal;
   }

   std::shared_ptr<DALConnection>
   DatabaseConnectionManager::GetConnection_()
   {
      boost::unique_lock<boost::recursive_mutex> guard(mutex_);

      // The deadline bounds the whole acquisition, not each individual wait.
      // Without it, a pool exhausted by one stalled subsystem blocks every
      // unrelated caller - message saving, greylisting, authentication - for as
      // long as the stall lasts. 0 keeps the unbounded wait for anyone who wants
      // it.
      const int acquire_timeout_seconds = IniFileSettings::Instance()->GetDBConnectionAcquireTimeout();
      const bool bounded_wait = acquire_timeout_seconds > 0;

      boost::chrono::steady_clock::time_point deadline;
      if (bounded_wait)
      {
         deadline = boost::chrono::steady_clock::now() + boost::chrono::seconds(acquire_timeout_seconds);
      }

      // Loop until we find a free connection (re-checking after each wakeup).
      while (1)
      {
         // Locate an available connection
         auto iterConnection = available_connections_.begin();

         if (iterConnection != available_connections_.end())
         {
            // Remove the connection from free and add to busy
            std::shared_ptr<DALConnection> pConn = (*iterConnection);

            // Remove it from the list of available connections
            available_connections_.erase(iterConnection);

            busy_connections_.insert(pConn);
            return pConn;
         }

         if (busy_connections_.size() == 0 &&
            available_connections_.size() == 0)
         {
            // There's no available connections at all. Nothing to wait for.
            //
            // Marked for the same reason the timeout below is: this returns the
            // empty result that a caller cannot tell from "the query found
            // nothing", and a pool holding no connections whatsoever is about as
            // unavailable as a database gets. Without the mark, a recipient
            // lookup during the window between Disconnect() and the next
            // successful Connect() answers "no such user" and the sender is told
            // permanently that a valid mailbox does not exist. No error is
            // reported here - the failure to connect has already been reported
            // by whoever tried.
            DatabaseUnavailableMarker::Mark();

            std::shared_ptr<DALConnection> pEmpty;
            return pEmpty;
         }

         // All connections are busy. Wait until one is released instead of
         // polling. A timeout bounds the wait as a defensive backstop so a lost
         // notification can never wedge the caller indefinitely.
         boost::chrono::milliseconds wait_time(100);

         if (bounded_wait)
         {
            boost::chrono::steady_clock::time_point now = boost::chrono::steady_clock::now();

            if (now >= deadline)
            {
               // Hand back the same empty result the pool produces when it has no
               // connections at all, so callers take their existing failure path.
               String message;
               message.Format(_T("Timed out after %d seconds while waiting for a database connection. All %d pooled connections are in use. Increase [Database] NumberOfConnections, or investigate what is holding connections open."),
                  acquire_timeout_seconds, (int) busy_connections_.size());

               // Released before reporting. ErrorManager::ReportError writes to the
               // error log and, when an OnError event script is installed, runs it
               // synchronously with full COM access - and those COM objects read the
               // database, re-entering this function on this thread. The mutex is
               // recursive so the re-entry is admitted, but the pool is empty (that
               // is why we are here), so the inner call would wait for a connection
               // while still holding the outer lock, blocking every thread trying to
               // return one.
               guard.unlock();

               // Marked before reporting, and before returning the empty result:
               // callers see the same "nothing came back" they would see from a
               // query that legitimately found nothing, and this is the only thing
               // that lets them tell the two apart. Without it a recipient lookup
               // that timed out would answer "no such user" and the sender would
               // get a permanent rejection for a valid address.
               DatabaseUnavailableMarker::Mark();

               ErrorManager::Instance()->ReportError(ErrorManager::High, 5180, "DatabaseConnectionManager::GetConnection_", message);

               std::shared_ptr<DALConnection> pEmpty;
               return pEmpty;
            }

            boost::chrono::milliseconds remaining =
               boost::chrono::duration_cast<boost::chrono::milliseconds>(deadline - now);

            if (remaining < wait_time)
               wait_time = remaining;
         }

         connection_released_.wait_for(guard, wait_time);
      }
   }

   /*
      What a transaction opened here does and does not cover. Measured against
      the code on 13 August 2026; every claim below is about this tree, not about
      what the backends are capable of.

      SCOPE. The connection is taken out of the pool and handed back to the
      caller, and it stays checked out until Commit or Rollback returns it. Only
      statements run *on that object* are inside the transaction. Execute() and
      OpenRecordset() on this class take no connection: they borrow a different
      pooled connection per statement, so anything routed through them during the
      window is outside it. In practice the only caller that gets this right is
      the COM API - InterfaceDatabase keeps the connection in conn_ and sends
      ExecuteSQL / ExecuteSQLScript / EnsurePrerequisites straight to it. Nothing
      inside the server uses transactions at all, which is why the message-save
      and cascade-delete paths are not atomic and why "referential integrity in
      the schema" is still an open roadmap row.

      PER BACKEND:

      MS SQL Server (ADOConnection) - real. BEGIN/COMMIT/ROLLBACK TRANSACTION are
      issued as statements on the session, and MSSQL rolls back DDL as well as
      DML. Note that Connect() puts the session at READ UNCOMMITTED, so the
      transaction buys atomicity of its own writes and no read isolation
      whatsoever; a concurrent reader on another pooled connection sees the
      uncommitted rows.

      PostgreSQL (PGConnection) - real, and the strongest of the four: DDL is
      transactional, and the session is at the server default (READ COMMITTED)
      because nothing overrides it.

      MySQL/MariaDB (MySQLConnection) - conditional. BEGIN is issued only if
      every table in the database reported InnoDB when the connection was opened
      (LoadSupportsTransactions_); on a MyISAM database BeginTransaction returns
      true having done nothing, and it is Rollback that finally says so, with
      error 5104 telling the administrator to restore a backup. DDL is never
      transactional on MySQL - it commits implicitly - so a multi-statement
      upgrade step that fails half way leaves a half-changed schema whatever this
      returns.

      SQL Server Compact (SQLCEConnection) - DML only, and it is the installer
      default. See the comment on SQLCEConnection::BeginTransaction.

      So: a transaction here is worth having on MSSQL and PostgreSQL, is worth
      having for DML on SQL CE, is worth having on MySQL only for an all-InnoDB
      database, and is worth nothing at all for DDL on two of the four. Anything
      that wraps a cascade in one has to keep working when it silently buys
      nothing - and file deletions cannot be rolled back on any of them.
   */
   std::shared_ptr<DALConnection>
   DatabaseConnectionManager::BeginTransaction(String &sErrorMessage)
   {
      std::shared_ptr<DALConnection> pDALConnection = GetConnection_();

      // GetConnection_ returns empty when the pool could not hand one out.
      if (!pDALConnection)
      {
         sErrorMessage = "No database connection is available.";
         return pDALConnection;
      }

      if (!pDALConnection->BeginTransaction(sErrorMessage))
      {
         // Could not start the transaction. The connection must go back to the
         // pool - leaking it here permanently shrank the pool, and once it was
         // exhausted every SMTP, IMAP and POP3 operation blocked forever.
         ReleaseConnection_(pDALConnection);

         std::shared_ptr<DALConnection> pEmpty;
         return pEmpty;
      }

      return pDALConnection;
   }

   bool
   DatabaseConnectionManager::CommitTransaction(std::shared_ptr<DALConnection> pConnection, String &sErrorMessage)
   {
      bool bResult = pConnection->CommitTransaction(sErrorMessage);
      ReleaseConnection_(pConnection);

      return bResult;
   }

   bool
   DatabaseConnectionManager::RollbackTransaction(std::shared_ptr<DALConnection> pConnection, String &sErrorMessage)
   {
      bool bResult = pConnection->RollbackTransaction(sErrorMessage);
      ReleaseConnection_(pConnection);

      return bResult;
   }

   bool
   DatabaseConnectionManager::GetIsConnected()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      size_t iNoOfConnections = busy_connections_.size() + available_connections_.size();

      if (iNoOfConnections == 0)
         return false;

      return true;
   }

   int
   DatabaseConnectionManager::GetBusyConnectionCount()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      return (int) busy_connections_.size();
   }

   int
   DatabaseConnectionManager::GetAvailableConnectionCount()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      return (int) available_connections_.size();
   }

   bool
   DatabaseConnectionManager::ExecuteScript(const String &sFile, String &sErrorMessage)
   {
      std::shared_ptr<DALConnection> pConnection = GetConnection_();

      // GetConnection_ returns empty when the pool could not hand one out.
      if (!pConnection)
      {
         sErrorMessage = "No database connection is available.";
         return false;
      }

      SQLScriptRunner scriptRunner;
      bool result = scriptRunner.ExecuteScript(pConnection, sFile, sErrorMessage);

      ReleaseConnection_(pConnection);

      return result;
   }

   bool
   DatabaseConnectionManager::EnsuresPrerequisites(long DBVersion, String &sErrorMessage)
   {
      PrerequisiteList prerequisites;

      std::shared_ptr<DALConnection> pConnection = GetConnection_();

      // GetConnection_ returns empty when the pool could not hand one out.
      if (!pConnection)
      {
         sErrorMessage = "No database connection is available.";
         return false;
      }

      bool result = prerequisites.Ensure(pConnection, DBVersion, sErrorMessage);

      ReleaseConnection_(pConnection);

      return result;
   }
}