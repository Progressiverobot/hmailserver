// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "DALConnection.h"

#include <boost/thread/recursive_mutex.hpp>
#include <boost/thread/condition_variable.hpp>

namespace HM
{
   class SQLCommand;

   class DatabaseConnectionManager
   {
   public:
      DatabaseConnectionManager(void);
      ~DatabaseConnectionManager(void);

      bool CreateConnections(String &sErrorMessage);
      
      void Disconnect();

      bool Execute(const SQLStatement &statement, __int64 *iInsertID = 0, int iIgnoreErrors = 0, String &sErrorMessage = String(_T("")));
      bool Execute(const SQLCommand &command, __int64 *iInsertID = 0, int iIgnoreErrors = 0, String &sErrorMessage = String(_T("")));
      
      std::shared_ptr<DALRecordset> OpenRecordset(const SQLStatement &statement);
      std::shared_ptr<DALRecordset> OpenRecordset(const SQLCommand &command);

      int GetCurrentDatabaseVersion();

      bool GetIsConnected();

      // Connection-pool observability (used by the metrics endpoint). Busy =
      // currently checked out by a caller, available = idle in the pool.
      int GetBusyConnectionCount();
      int GetAvailableConnectionCount();

      std::shared_ptr<DALConnection> BeginTransaction(String &sErrorMessage);
      bool CommitTransaction(std::shared_ptr<DALConnection> pConnection, String &sErrorMessage);
      bool RollbackTransaction(std::shared_ptr<DALConnection> pConnection, String &sErrorMessage);
      bool ExecuteScript(const String &sFile, String &sErrorMessage);

      bool EnsuresPrerequisites(long DBVersion, String &sErrorMessage);
   private:

      DALConnection::ConnectionResult Connect_(String &sErrorMessage);

      std::shared_ptr<DALConnection> GetConnection_();
      void ReleaseConnection_(std::shared_ptr<DALConnection> pConn);

      // Test-only fault injection; see the comment above the definition. Off unless
      // [Settings] SimulateDatabaseFailureFor names a substring of the statement.
      static bool SimulateFailureFor_(const SQLCommand &command);

      // Records a statement's execution latency into the observability metrics and,
      // when it exceeds [Settings] SlowQueryLogMilliseconds, logs it (literals
      // redacted). Backend-agnostic: called from the single Execute/OpenRecordset
      // chokepoint so every query is measured regardless of database engine.
      void MeasureQuery_(const SQLCommand &command, unsigned __int64 microseconds);

      // Returns the statement text with the contents of every single-quoted string
      // literal replaced by '?', so any value inlined into the SQL is never written
      // to the slow-query log.
      static String RedactSqlLiterals_(const String &sql);
 
      boost::recursive_mutex mutex_;
      
      std::set<std::shared_ptr<DALConnection> > busy_connections_;
      std::set<std::shared_ptr<DALConnection> > available_connections_;
      
      // Signalled whenever a connection is returned to the pool, so a waiter in
      // GetConnection_ can wake immediately instead of polling.
      boost::condition_variable_any connection_released_;

   };
}