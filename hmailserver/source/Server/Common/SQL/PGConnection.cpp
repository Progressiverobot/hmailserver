// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "PGConnection.h"
#include "PGRecordset.h"
#include "DatabaseSettings.h"
#include "..\Util\Unicode.h"
#include "Macros/PGSQLMacroExpander.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PGConnection::PGConnection(std::shared_ptr<DatabaseSettings> pSettings) :
      DALConnection(pSettings),
      dbconn_(nullptr)
   {
      is_connected_ = false;
   }

   PGConnection::~PGConnection()
   {
      try
      {
         if (dbconn_)
         {
            PQfinish(dbconn_);
            dbconn_ = 0;
         }
      }
      catch (...)
      {

      }
        
   }

   DALConnection::ConnectionResult
   PGConnection::Connect(String &sErrorMessage)
   {
      try
      {

         String sUsername = database_settings_->GetUsername();
         String sPassword = database_settings_->GetPassword();
         String sServer = database_settings_->GetServer();
         String sDatabase = database_settings_->GetDatabaseName();
         long lDBPort = database_settings_->GetPort();
        
         String sConnectionString;
         sConnectionString.Format(_T("host='%s' port='%d' user='%s' password='%s'"), sServer.c_str(), lDBPort, sUsername.c_str(), sPassword.c_str());

         if (sDatabase.IsEmpty())
            sConnectionString += " dbname='postgres'";
         else
            sConnectionString += " dbname='" + sDatabase + "'";

         dbconn_ = PQconnectdb(Unicode::ToANSI(sConnectionString));

        
         if (PQstatus(dbconn_) != CONNECTION_OK)
         {
            sErrorMessage = PQerrorMessage(dbconn_);
            return TemporaryFailure;
         }

         is_connected_ = true;
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5085, "PGConnection::Connect", "An unhandled error occurred when connecting to the database");

         return TemporaryFailure;
      }
          
      return Connected;
   }

   bool
   PGConnection::Disconnect()
   {
      if (dbconn_)
      {
         PQfinish(dbconn_);
         dbconn_ = 0;
      }

      // Was left true, so IsConnected() went on claiming a connection that had
      // been finished. Connect() resets it on the way back up.
      is_connected_ = false;

      return true;
   }

   DALConnection::ExecutionResult
   PGConnection::TryExecute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID, int iIgnoreErrors) 
   {
      String SQL = command.GetQueryString();

      try
      {
         // PG_query-doc:
         // Zero if the query was successful. Non-zero if an error occurred.
         // 
         AnsiString sQuery;
         if (!Unicode::WideToMultiByte(SQL, sQuery))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5106, "PGConnection::TryExecute", "Could not convert string into multi-byte.");
            return DALConnection::DALUnknown;
         }

         // Cleared before the statement runs, the way ADOConnection does it. A
         // plain INSERT only comes back with a row when SQLStatement::GetCommand
         // appended a RETURNING clause, so without this the caller kept whatever
         // it happened to be holding - on the shared "insert then read the id"
         // path that is another row's identity.
         if (iInsertID != nullptr)
            *iInsertID = 0;

         PGresult *pResult = PQexec(dbconn_, sQuery);

         // Classified before the marker is consulted, because a marked statement
         // may discard "this object already exists" but never "the server never
         // heard the statement" - see DALConnection::HasIgnoreErrorsMarker. The
         // error text goes into a local so a discarded failure leaves nothing in
         // the caller's buffer.
         String checkErrorMessage;
         DALConnection::ExecutionResult result = CheckError(pResult, SQL, checkErrorMessage);

         if (result != DALSuccess)
         {
            bool ignoreByMarker = result != DALConnection::DALConnectionProblem &&
                                  HasIgnoreErrorsMarker(SQL);

            if (!ignoreByMarker)
            {
               sErrorMessage = checkErrorMessage;

               if (pResult != 0)
                  PQclear(pResult);

               return result;
            }
         }

         ExecStatusType iExecResult = PQresultStatus(pResult);

         // Check if a value has been returned. Will only occur if we've
         // inserted a value.
         if (iInsertID != nullptr && iExecResult == PGRES_TUPLES_OK)
         {
            // pick the ID from the first row.
            char *pRetVal = PQgetvalue(pResult, 0, 0);
            *iInsertID = pRetVal ? _atoi64(pRetVal) : 0;
         }

         if (pResult != 0)
            PQclear(pResult);
        
      }
      catch (...)
      {
         sErrorMessage = "Source: PGConnection::TryExecute, Code: HM5084, Description: An unhanded error occurred while executing: " + SQL;
         return DALConnection::DALUnknown;
      }

      return DALConnection::DALSuccess;
   }

   bool
   PGConnection::IsConnected() const
   {
      return is_connected_;
   }

   PGconn*
   PGConnection::GetConnection() const
   {
      return dbconn_;
   }

   DALConnection::ExecutionResult
   PGConnection::CheckError(PGresult *pResult, const String &sAdditionalInfo, String &sOutputErrorMessage) const
   {
      try
      {
         String sErrorMsg = "";

         DALConnection::ExecutionResult result = DALConnection::DALUnknown;
         
         if (pResult)
         {  
            ExecStatusType iExecResult = PQresultStatus(pResult);

            if (iExecResult == PGRES_COMMAND_OK || iExecResult == PGRES_TUPLES_OK) 
            {
               result = DALConnection::DALSuccess;
               return result;
            }
            else if (iExecResult == PGRES_FATAL_ERROR)
            {
               result = DALConnection::DALErrorInSQL;
            }

            // Retrieve error message
            sErrorMsg  = PQresultErrorMessage(pResult);

            /*
               A connection that died while the statement was in flight does not
               come back as a null result: libpq builds a PGRES_FATAL_ERROR
               result ("server closed the connection unexpectedly") and marks the
               connection bad. Classified on the result status alone that is
               DALErrorInSQL, which DALConnection::Execute and DALRecordset::Open
               both treat as final - so the statement was reported failed and
               never retried, and it took a *second* statement (which libpq
               refuses outright, giving the null result below) before anything
               reconnected. On MySQL the same event is recognised, through error
               codes 2006/2013, and the statement is retried; PostgreSQL was the
               one backend that dropped it.

               PQstatus is what separates the two: a statement the server
               rejected leaves the connection CONNECTION_OK, a statement that
               never got an answer does not.
            */
            if (result != DALConnection::DALSuccess && PQstatus(dbconn_) != CONNECTION_OK)
               result = DALConnection::DALConnectionProblem;
         }
         else
         {
            sErrorMsg  = PQerrorMessage(dbconn_);

            if (sErrorMsg.IsEmpty())
               sErrorMsg = "Unknown error. Error structure not initialized.";

            result = DALConnection::DALConnectionProblem;
         }

         String sErrorMessage;
         sErrorMessage.Format(_T("Postgres: %s (Additional info: %s)"), sErrorMsg.c_str(), sAdditionalInfo);

         sOutputErrorMessage = sErrorMessage;

         return result;
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(HM::ErrorManager::High, 5083, "PGConnection::CheckError", "An unhandled error occurred while checking for errors.");

         return DALConnection::DALUnknown;
      }
   }

   void 
   PGConnection::OnConnected()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Not much going on here...
   //---------------------------------------------------------------------------()
   {
      
   }

   bool 
   PGConnection::BeginTransaction(String &sErrorMessage)
   {
      return TryExecute(SQLCommand("BEGIN TRANSACTION"), sErrorMessage, 0, 0) == DALSuccess;
   }

   bool 
   PGConnection::CommitTransaction(String &sErrorMessage)
   {
      return TryExecute(SQLCommand("COMMIT TRANSACTION"), sErrorMessage, 0, 0) == DALSuccess;
   }

   bool 
   PGConnection::RollbackTransaction(String &sErrorMessage)
   {
      return TryExecute(SQLCommand("ROLLBACK TRANSACTION"), sErrorMessage, 0, 0) == DALSuccess;
   }

   bool 
   PGConnection::CheckServerVersion(String &errorMessage)
   {

      return true;
   }

   std::shared_ptr<DALRecordset> 
   PGConnection::CreateRecordset()
   {
      std::shared_ptr<PGRecordset> recordset = std::shared_ptr<PGRecordset>(new PGRecordset());
      return recordset;
   }

   void
   PGConnection::EscapeString(String &sInput)
   {
      sInput.Replace(_T("'"), _T("''"));
      sInput.Replace(_T("\\"), _T("\\\\"));
   }

   std::shared_ptr<IMacroExpander> 
   PGConnection::CreateMacroExpander()
   {
      std::shared_ptr<PGSQLMacroExpander> expander = std::shared_ptr<PGSQLMacroExpander>(new PGSQLMacroExpander());
      return expander;
   }

}