// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include ".\databaseconnectionmanager.h"

#include "DALConnection.h"
#include "DALConnectionFactory.h"
#include "DatabaseSettings.h"

#include "ADORecordset.h"
#include "MySQLRecordset.h"
#include "PGRecordset.h"
#include "SQLCERecordset.h"
#include "MySQLInterface.h"

#include "SQLCommand.h"

#include "Prerequisites/PrerequisiteList.h"
#include "SQLScriptRunner.h"

#include "../Util/ServerStatus.h"

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
      std::shared_ptr<DALConnection> pDALConn = GetConnection_();

      if (!pDALConn)
      {

         assert(0);
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

   std::shared_ptr<DALRecordset> 
   DatabaseConnectionManager::OpenRecordset(const SQLCommand &command)
   {
      std::shared_ptr<DALRecordset> pRecordset;

      std::shared_ptr<DALConnection> pDALConn = GetConnection_();

      if (!pDALConn)
      {
         assert(0);
         return pRecordset;
      }

      pRecordset = pDALConn->CreateRecordset();

      boost::chrono::steady_clock::time_point queryStart = boost::chrono::steady_clock::now();

      bool bOpened = pRecordset->Open(pDALConn, command);

      unsigned __int64 elapsedMicros = (unsigned __int64) boost::chrono::duration_cast<boost::chrono::microseconds>(
         boost::chrono::steady_clock::now() - queryStart).count();
      MeasureQuery_(command, elapsedMicros);

      if (!bOpened)
         pRecordset.reset();

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
            std::shared_ptr<DALConnection> pEmpty;
            return pEmpty;
         }

         // All connections are busy. Wait until one is released instead of
         // polling. A timeout bounds the wait as a defensive backstop so a lost
         // notification can never wedge the caller indefinitely.
         connection_released_.wait_for(guard, boost::chrono::milliseconds(100));
      }
   }

   std::shared_ptr<DALConnection> 
   DatabaseConnectionManager::BeginTransaction(String &sErrorMessage)
   {
      std::shared_ptr<DALConnection> pDALConnection = GetConnection_();
      if (!pDALConnection->BeginTransaction(sErrorMessage))
      {
         // Could not start database transaction.
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

      bool result = prerequisites.Ensure(pConnection, DBVersion, sErrorMessage);

      ReleaseConnection_(pConnection);

      return result;
   }
}