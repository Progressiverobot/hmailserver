// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "SQLCEConnection.h"
#include "SQLCERecordset.h"
#include "Macros/SQLCEMacroExpander.h"
#include "DatabaseSettings.h"
#include "ADOInt64Helper.h"
#include "../Util/Time.h"
#include <sqlce_oledb.h>
#include <sqlce_err.h>
#include <sqlce_sync.h>

using namespace std;

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Source/description of a COM error as one line, for the transaction
      // functions below. err.Source()/err.Description() hand back BSTRs owned by
      // _bstr_t temporaries, so they are copied out before those go out of
      // scope, and a null one becomes an empty string rather than a fault.
      String DescribeComError(_com_error &err)
      {
         _bstr_t bstrSource(err.Source());
         _bstr_t bstrDescription(err.Description());

         LPCSTR lpcSource = bstrSource;
         LPCSTR lpcDescription = bstrDescription;

         String source;
         if (lpcSource != nullptr)
            source = lpcSource;

         String description;
         if (lpcDescription != nullptr)
            description = lpcDescription;

         String result;
         result.Format(_T("Source: %s, Description: %s"), source.c_str(), description.c_str());

         return result;
      }
   }

   SQLCEConnection::SQLCEConnection(std::shared_ptr<DatabaseSettings> pSettings) :
      DALConnection(pSettings)
   {
      HRESULT hr =cSQLCEConnection.CreateInstance(__uuidof(Connection));
      
      if ( !SUCCEEDED( hr ) )
      {
         assert(0);
      }  

      connected_ = false;
   }


   SQLCEConnection::~SQLCEConnection()
   {
   
      try
      {
         cSQLCEConnection = NULL;
      }
      catch(...)
      {
      
      } 
   
   }


   bool
   SQLCEConnection::Disconnect()
   {
      try
      {

         if (!connected_)
            return false; // --- already disconnected.

         if (cSQLCEConnection->State == 1)
            cSQLCEConnection->Close();
      }
      catch ( ... )
      {
         assert(0);
      }

      connected_ = false;

      return true;
   }

   

   String 
   SQLCEConnection::GetConnectionString_(const String &sDatabase, const String &sPassword) const
   {
      String sDatabaseFile = GetDatabaseFileName_(sDatabase);

      String sConnectionString;
      sConnectionString.Format(_T("Provider=Microsoft.SQLSERVER.CE.OLEDB.4.0;Data Source=%s;SSCE:Database Password=%s;SSCE:Max Database Size=4000;SSCE:Flush Interval=10;"), sDatabaseFile.c_str(), sPassword.c_str());

      return sConnectionString;
   }


   String 
   SQLCEConnection::GetDatabaseFileName_(const String &sShortName) const
   {
      String sDatabaseDirectory = database_settings_->GetDatabaseDirectory();
      String sDatabaseFile = sDatabaseDirectory + "\\" + sShortName + ".sdf";

      return sDatabaseFile;
   }

   bool 
   SQLCEConnection::GetRequiresUpgrade_(const String &sConnectionString)
   {
      BSTR sUsername = 0;
      BSTR sPassword = 0;
      BSTR bsConnection = sConnectionString.AllocSysString();
      
      bool bResult = false;

      try
      {
         cSQLCEConnection->Open( bsConnection, sUsername, sPassword, NULL );
         cSQLCEConnection->Close();
      }
      catch (_com_error &err)
      {
         if (ErrorManager::GetNativeErrorCode(err.ErrorInfo()) == SSCE_M_DBUPGRADENEEDED)
         {
            bResult = true;
         }
      }
      
      ::SysFreeString(bsConnection);

      return bResult;

   }

   DALConnection::ConnectionResult
   SQLCEConnection::Connect(String &sErrorMessage)
   {
      String sPassword = database_settings_->GetPassword();
      String sDatabase = database_settings_->GetDatabaseName();

      if (connected_)
         return Connected;
      
      String sConnectionString = GetConnectionString_(sDatabase, sPassword);
      
      /*
         Check if the database engine needs to be upgraded.
      */
      if (GetRequiresUpgrade_(sConnectionString))
      {
         if (!UpgradeDatabase_())
            return FatalError;
      }

      DALConnection::ConnectionResult result = DALConnection::FatalError;

      try
      {
         BSTR sUsername = 0;
         BSTR sPassword = 0;
         BSTR bsConnection = sConnectionString.AllocSysString();
         HRESULT hr = cSQLCEConnection->Open( bsConnection, sUsername, sPassword, NULL );

         if ( !SUCCEEDED( hr ) )
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5097, "SQLCEConnection::Connect", "Open failed");
            return TemporaryFailure;
         }   

         SysFreeString(bsConnection);


         connected_ = true;
         sErrorMessage = "";
         
         return DALConnection::Connected;
      }
      catch ( _com_error &err )
      {
         String sErrSource = err.Source();
         String sErrDesc =  err.Description();

         int iNativeErrorCode = ErrorManager::GetNativeErrorCode(err.ErrorInfo());
         sErrorMessage.Format(_T("Database connection error. Source: %s, Error: %d, Description: %s Check database settings in hMailServer.ini."), sErrSource.c_str(), iNativeErrorCode, sErrDesc.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5098, "SQLCEConnection::Connect", sErrorMessage);

         return TemporaryFailure;
      }
      catch( ... )
      {
         sErrorMessage = "ADO: Unknown database connection error";
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5099, "SQLCEConnection::Connect", "An unknown error occurred when connecting to database. Check your database settings in hMailServer.ini.");

         return TemporaryFailure;
      }

   }

   bool 
   SQLCEConnection::UpgradeDatabase_()
   {
      ISSCEEngine *pISSCEEngine = NULL;
      HRESULT hr = CoCreateInstance(CLSID_Engine, NULL, CLSCTX_INPROC_SERVER, IID_ISSCEEngine, (void**)&pISSCEEngine);
      if (FAILED(hr))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5122, "SQLCEConnection::UpgradeDatabase_()", "Failed to create instance of SQL CE Engine.");
         return false;
      }

      String sOldConnectionString = GetConnectionString_(database_settings_->GetDatabaseName(), database_settings_->GetPassword());
      String sNewConnectionString = GetConnectionString_(database_settings_->GetDatabaseName() + "_upgraded", database_settings_->GetPassword());

      HRESULT result = pISSCEEngine->UpgradeDatabase(sOldConnectionString.AllocSysString(), sNewConnectionString.AllocSysString());
      if (FAILED(hr))
      {
         String sErrorMessage;
         sErrorMessage.Format(_T("Failed to upgrade SQL CE database. HRESULT: %d"), hr);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5123, "SQLCEConnection::UpgradeDatabase_()", sErrorMessage);       

         return false;
      }

      pISSCEEngine->Release();
      
      String sCurrentDatabaseFile = GetDatabaseFileName_(database_settings_->GetDatabaseName());
      String sUpgradedDatabaseFile = GetDatabaseFileName_(database_settings_->GetDatabaseName() + "_upgraded");

      if (!FileUtilities::Move(sCurrentDatabaseFile, sCurrentDatabaseFile + ".backup_v52"))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5124, "SQLCEConnection::UpgradeDatabase_()", "Moving of current database file to backup location failed.");       
         return false;
      }

      if (!FileUtilities::Move(sUpgradedDatabaseFile, sCurrentDatabaseFile))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5125, "SQLCEConnection::UpgradeDatabase_()", "Moving of upgraded database file to current database file failed.");       
         return false;
      }

      return true;
   }

   _ConnectionPtr
   SQLCEConnection::GetConnection() const
   {
      return cSQLCEConnection;
   }

   DALConnection::ExecutionResult
   SQLCEConnection::TryExecute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID, int iIgnoreErrors)
   {
      if (!connected_)
         return DALConnection::DALConnectionProblem; // --- already disconnected.

      String queryString = command.GetQueryString();

      try
      {

         _CommandPtr adoCommand;
         adoCommand.CreateInstance(__uuidof(Command));

         InitializeCommandParameters(adoCommand, command, queryString);

         adoCommand->CommandType = adCmdText;
         adoCommand->CommandText = _bstr_t(queryString);
         adoCommand->ActiveConnection = cSQLCEConnection;
         adoCommand->NamedParameters = true;
         adoCommand->Execute(NULL, NULL, adCmdText);
         adoCommand->ActiveConnection = NULL;

         if (iInsertID > 0)
         {
            _RecordsetPtr pIdentityRS;
            BSTR bsIdentity;

            pIdentityRS.CreateInstance(__uuidof(Recordset));
            pIdentityRS->PutRefActiveConnection(cSQLCEConnection); 
            String sIdentitySQL = "SELECT @@IDENTITY AS IDENT";
            bsIdentity = sIdentitySQL.AllocSysString();

            HRESULT hr = pIdentityRS->Open( bsIdentity, vtMissing, adOpenForwardOnly, adLockUnspecified, -1);
                        
            *iInsertID = GetIdentityFromRS_(pIdentityRS);
            pIdentityRS->Close();
            pIdentityRS->PutRefActiveConnection(NULL); 

            ::SysFreeString( bsIdentity );
         }
      }
      catch ( _com_error &err )
      {
         ExecutionResult dbErr = GetErrorType_(_com_error::WCodeToHRESULT(err.WCode()));
         if (iIgnoreErrors > 0 && iIgnoreErrors & dbErr)
            return DALConnection::DALSuccess;

         // The marker means "this object may already exist". It must not also
         // discard a lost connection - see DALConnection::HasIgnoreErrorsMarker.
         // GetErrorType_ never classifies anything as DALConnectionProblem on
         // this backend today, so the test costs nothing here and stops the next
         // person who adds a mapping from re-opening the hole.
         if (dbErr != DALConnection::DALConnectionProblem &&
             HasIgnoreErrorsMarker(queryString))
            return DALConnection::DALSuccess;

         _bstr_t bstrSource( err.Source() );
         _bstr_t bstrDescription( err.Description() );

         LPCSTR lpcSource = bstrSource;
         String sErrSource = lpcSource;

         LPCSTR lpcDesc = bstrDescription;
         String sErrDesc = lpcDesc;

         sErrorMessage = "Source: SQLCEConnection::Execute(), Code: HM10044, Description: Error while executing SQL statement: \n";
         sErrorMessage += queryString;
         sErrorMessage += "\n";
         sErrorMessage += sErrSource;
         sErrorMessage += "\n";
         sErrorMessage += sErrDesc;

         return dbErr;
      }
      catch ( ... )
      {

         sErrorMessage = "Source: SQLCEConnection::Execute(), Code: HM10045, Description: Unknown error when executing SQL statement: \n";
         sErrorMessage.append(queryString);

         return DALConnection::DALUnknown;
      }

      return DALConnection::DALSuccess;
   }

   bool
   SQLCEConnection::IsConnected() const
   { 
      return connected_; 
   };

   __int64 
   SQLCEConnection::GetIdentityFromRS_(_RecordsetPtr pRS) const
   {
      try
      {
         if (pRS->GetadoEOF())
            return 0;
         
         _variant_t    vaField;
         FieldsPtr      pFields;
         FieldPtr       pField;
         HRESULT			hr;

         hr = pRS->get_Fields( &pFields );
         vaField = "IDENT";
         pFields->get_Item( vaField, &pField );

         vaField.Clear();
         if (pField)
         {
            pField->get_Value(&vaField);

            switch (vaField.vt)
            {
            case VT_NULL:
               return 0;
            case VT_I4:
               return vaField.intVal;
            default:
               if (vaField.decVal.sign == DECIMAL_NEG)
                  return -vaField.cyVal.int64;
               else
                  return vaField.cyVal.int64;
            }
         }

         return 0;
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5100, "SQLCEConnection::GetIdentityFromRS_", "Error while determening @@IDENTITY");
         throw;
      }

      return 0;
   }

   void 
   SQLCEConnection::OnConnected()
   {
      // Nothing to do here. Only used my MySQL.
   }

   DALConnection::ExecutionResult 
   SQLCEConnection::GetErrorType_(int iErrorCode)
   {
      switch (iErrorCode)
      {
      case 0:
         return DALConnection::DALSuccess;
      case 0x80040E2FL: // DB_E_INTEGRITYVIOLATION:
         return DALConnection::DALErrorInSQL;
      default:
         return DALConnection::DALUnknown;
      }
   }

   bool 
   SQLCEConnection::CreateDatabase(const String &sPath, const String &sFile, const String &sPassword, String &sErrorMessage)
   {
      String sFullPath = sPath + "\\" + sFile + ".sdf";

      if (FileUtilities::Exists(sFullPath))
      {
         sErrorMessage.Format(_T("Creation of SQL database failed. The database file, %s, already exists."), sFullPath.c_str());
         return false;
      }

      // Object declarations
      HRESULT            hr                   = NOERROR; 
      DBPROPSET          dbpropset[2]; 
      DBPROP             dbprop[1]; // Property array to initialize the provider.
      DBPROP             sscedbprop[1]; // Property array for SSCE security properties
      INT                i                    = 0;
      IDBDataSourceAdmin *pIDBDataSourceAdmin = NULL; 
      IUnknown           *pIUnknownSession    = NULL;

      // Create an instance of the OLE DB provider.
      hr = CoCreateInstance(CLSID_SQLSERVERCE, 0, CLSCTX_INPROC_SERVER,
         IID_IDBDataSourceAdmin, (void**)& pIDBDataSourceAdmin);
      if(FAILED(hr))
      {
         sErrorMessage.Format(_T("CoCreateInstance failed. Result: %d, Last error: %d"), hr, GetLastError());
         return false;
      }

      // Initialize property structures.
      VariantInit(&dbprop[0].vValue);
      for (int i = 0; i < sizeof(sscedbprop) / sizeof(sscedbprop[0]); i++)
      {
         VariantInit(&sscedbprop[i].vValue);
      }

      // Specify the property with name of the database.
      dbprop[0].dwPropertyID  = DBPROP_INIT_DATASOURCE;
      dbprop[0].dwOptions   = DBPROPOPTIONS_REQUIRED;
      dbprop[0].vValue.vt   = VT_BSTR;
      dbprop[0].vValue.bstrVal = SysAllocString(sFullPath); 
      if(NULL == dbprop[0].vValue.bstrVal)
      {
         hr = E_OUTOFMEMORY;
         sErrorMessage.Format(_T("SysAllocString failed. Out of memory"));
         return false;
      }


      // Specify the password.
      sscedbprop[0].dwPropertyID = DBPROP_SSCE_DBPASSWORD;
      sscedbprop[0].dwOptions = DBPROPOPTIONS_REQUIRED;
      sscedbprop[0].vValue.vt = VT_BSTR;
      sscedbprop[0].vValue.bstrVal = SysAllocString(sPassword);
      if(NULL == sscedbprop[0].vValue.bstrVal)
      {
         hr = E_OUTOFMEMORY;
         sErrorMessage.Format(_T("SysAllocString failed. Out of memory"));
         return false;
      }


      // Initialize the property sets.
      dbpropset[0].guidPropertySet = DBPROPSET_DBINIT;
      dbpropset[0].rgProperties  = dbprop;
      dbpropset[0].cProperties  = sizeof(dbprop)/sizeof(dbprop[0]);

      dbpropset[1].guidPropertySet = DBPROPSET_SSCE_DBINIT;
      dbpropset[1].rgProperties  = sscedbprop;
      dbpropset[1].cProperties  = sizeof(sscedbprop)/sizeof(sscedbprop[0]);

      // Create and initialize the database.
      hr = pIDBDataSourceAdmin->CreateDataSource(sizeof(dbpropset)/sizeof(dbpropset[0]),
         dbpropset, NULL, IID_IUnknown, &pIUnknownSession);

	   if(FAILED(hr)) 
      {
         sErrorMessage.Format(_T("CreateDataSource failed. Result: %d. Last error: %d"), hr, GetLastError());
         return false;
      }

      if (pIUnknownSession)
         pIUnknownSession->Release();

      if (pIDBDataSourceAdmin)
         pIDBDataSourceAdmin->Release();

      return true;
   }

   /*
      What a transaction is worth on SQL Server Compact, which is the installer
      default and therefore the case that matters most.

      BeginTrans/CommitTrans/RollbackTrans are accepted by the provider and are
      issued here, so DML inside a transaction is atomic in the ordinary sense.
      What is not covered is DDL: SQL CE commits schema changes as they are
      executed, so an upgrade step that ALTERs three tables and fails on the
      third cannot be rolled back to the state before the first. That is what
      the original comment on these three functions was reaching for, and it is
      why DBUpdater's SchemaVerification proves each marked step before the
      commit rather than trusting the commit to undo it.

      Separately, and true on every backend: DatabaseConnectionManager::Execute
      takes no connection, so it borrows a *different* pooled connection for each
      statement. A transaction opened here therefore covers only the statements
      routed onto this same connection object - which is exactly the COM API's
      IInterfaceDatabase.ExecuteSQL / ExecuteSQLScript / EnsurePrerequisites
      while a transaction is open, and nothing else. Server-internal work done
      through the object model during that window is outside it.

      What these three did *not* do was report failure. Each returned true
      unconditionally, so a commit that threw - a lost session, a provider error,
      a null connection object - was indistinguishable from one that had written
      the data. DBUpdater turns CommitTransaction() == S_OK into "the upgrade
      succeeded", so that lie became an installer exit code of 0.
   */
   bool
   SQLCEConnection::BeginTransaction(String &sErrorMessage)
   {
      try
      {
         cSQLCEConnection->BeginTrans();
         return true;
      }
      catch (_com_error &err)
      {
         sErrorMessage = "Failed to start a SQL Server Compact transaction: " + DescribeComError(err);
         LOG_APPLICATION(sErrorMessage);
         return false;
      }
      catch (...)
      {
         sErrorMessage = "Failed to start a SQL Server Compact transaction.";
         LOG_APPLICATION(sErrorMessage);
         return false;
      }
   }

   bool
   SQLCEConnection::CommitTransaction(String &sErrorMessage)
   {
      try
      {
         cSQLCEConnection->CommitTrans();
         return true;
      }
      catch (_com_error &err)
      {
         // The data is not committed. Saying so is the whole point: the caller
         // decides whether to retry or to leave the ERROR log in place, and it
         // cannot do either if this returns true.
         sErrorMessage = "Failed to commit a SQL Server Compact transaction. The changes it contained have not been saved. " + DescribeComError(err);
         LOG_APPLICATION(sErrorMessage);
         return false;
      }
      catch (...)
      {
         sErrorMessage = "Failed to commit a SQL Server Compact transaction. The changes it contained have not been saved.";
         LOG_APPLICATION(sErrorMessage);
         return false;
      }
   }

   bool
   SQLCEConnection::RollbackTransaction(String &sErrorMessage)
   {
      try
      {
         cSQLCEConnection->RollbackTrans();
         return true;
      }
      catch (_com_error &err)
      {
         sErrorMessage = "Failed to roll back a SQL Server Compact transaction. Data written inside it may still be present. " + DescribeComError(err);
         LOG_APPLICATION(sErrorMessage);
         return false;
      }
      catch (...)
      {
         sErrorMessage = "Failed to roll back a SQL Server Compact transaction. Data written inside it may still be present.";
         LOG_APPLICATION(sErrorMessage);
         return false;
      }
   }


   void 
   SQLCEConnection::SetTimeout(int seconds)
   {
      cSQLCEConnection->PutCommandTimeout(seconds);
   }

   bool 
   SQLCEConnection::CheckServerVersion(String &errorMessage)
   {
      return true;
   }

   std::shared_ptr<DALRecordset> 
   SQLCEConnection::CreateRecordset()
   {
      std::shared_ptr<SQLCERecordset> recordset = std::shared_ptr<SQLCERecordset>(new SQLCERecordset());
      return recordset;
   }

   std::shared_ptr<IMacroExpander> 
   SQLCEConnection::CreateMacroExpander()
   {
      std::shared_ptr<SQLCEMacroExpander> expander = std::shared_ptr<SQLCEMacroExpander>(new SQLCEMacroExpander());
      return expander;
   }

   void
   SQLCEConnection::EscapeString(String &sInput)
   {
      sInput.Replace(_T("'"), _T("''"));
   }

   void
   SQLCEConnection::InitializeCommandParameters(_CommandPtr &adoCommand, const SQLCommand &sqlCommand, String &queryString) const
   {
      for(const SQLParameter &parameter : sqlCommand.GetParameters())
      {
         String parameterName = parameter.GetName();

         if (parameter.GetType() == SQLParameter::ParamTypeInt32)
         {
            VARIANT integerType;
            integerType.vt = VT_I4;
            integerType.intVal = parameter.GetInt32Value();

            adoCommand->Parameters->Append(adoCommand->CreateParameter(_bstr_t(parameterName),adInteger,adParamInput, 4, integerType));
         }
         else if (parameter.GetType() == SQLParameter::ParamTypeInt64)
         {
            // Windows 2000 backwards compatibility:
            ADO64Helper::AddInt64Parameter(adoCommand, parameterName, parameter.GetInt64Value());
         }
         if (parameter.GetType() == SQLParameter::ParamTypeUnsignedInt32)
         {
            VARIANT integerType;
            integerType.vt = VT_UI4;
            integerType.intVal = parameter.GetUnsignedInt32Value();

            adoCommand->Parameters->Append(adoCommand->CreateParameter(_bstr_t(parameterName),adInteger,adParamInput, 4, integerType));
         }
         else if (parameter.GetType() == SQLParameter::ParamTypeString)
         {
            const String &value = parameter.GetStringValue();

            // _variant_t owns its BSTR for the lifetime of this scope; the previous
            // _bstr_t temporary was destroyed at the end of the assignment, leaving
            // the parameter bound to freed memory.
            _variant_t stringType(value.c_str());

            // The length must be higher than 0. Also, we set a fixed length on most queries
            // to prevent execution plan cache spamming. In the case where the data length is higher
            // than 8000, we spam the execution plan cache.
            int length = 0;

            if (value.GetLength() < 8000)
               length = 8000;
            else
               length = value.GetLength();

            adoCommand->Parameters->Append(adoCommand->CreateParameter(_bstr_t(parameterName),adWChar,adParamInput, length, stringType));
         }
      }
   }
}
