// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "MySQLConnection.h"
#include "MySQLRecordset.h"
#include "DatabaseSettings.h"
#include "../Application/IniFileSettings.h"
#include "Macros/MySQLMacroExpander.h"
#include "..\Util\Unicode.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   MySQLConnection::MySQLConnection(std::shared_ptr<DatabaseSettings> pSettings) :
      DALConnection(pSettings)
   {
      is_connected_ = false;
      dbconn_ = 0;
      supports_transactions_ = false;
      timeout_variable_is_seconds_ = false;
   }

   MySQLConnection::~MySQLConnection()
   {
      try
      {
         if (dbconn_)
         {
            MySQLInterface::Instance()->p_mysql_close(dbconn_);
            dbconn_ = 0;
         }
      }
      catch (...)
      {

      }

   }

   DALConnection::ConnectionResult
   MySQLConnection::Connect(String &sErrorMessage)
   {
      if (!MySQLInterface::Instance()->IsLoaded())
      {
         // Load the MySQL interface.
         if (!MySQLInterface::Instance()->Load(sErrorMessage))
         {
            // Loading failed
            return FatalError;
         }
      }

      try
      {
         String sUsername = database_settings_->GetUsername();
         String sPassword = database_settings_->GetPassword();
         String sServer = database_settings_->GetServer();
         String sDatabase = database_settings_->GetDatabaseName();
         long lDBPort = database_settings_->GetPort();

         if (lDBPort == 0)
            lDBPort = 3306;



         dbconn_ = MySQLInterface::Instance()->p_mysql_init(NULL);

         // Point the client authentication-plugin directory at the "plugin" sub-folder
         // of the Bin directory, where the bundled MariaDB Connector/C auth plugins
         // ship (caching_sha2_password for MySQL 8.0+, client_ed25519 / auth_gssapi_client
         // / parsec for MariaDB, sha256_password, dialog, ...). This lets the one bundled
         // client authenticate against essentially any MySQL or MariaDB account type the
         // user has configured, instead of failing with "Authentication plugin '<x>'
         // cannot be loaded". The default auth plugin is intentionally NOT forced, so
         // the client negotiates whatever the account actually uses.
         if (MySQLInterface::Instance()->p_mysql_options != 0)
         {
            AnsiString sPluginDir = Unicode::ToANSI(MySQLInterface::Instance()->GetLibraryDirectory() + _T("\\plugin"));
            MySQLInterface::Instance()->p_mysql_options(dbconn_, HM_MYSQL_PLUGIN_DIR, sPluginDir.c_str());
         }

         // The bundled client is MariaDB Connector/C 3.4, which negotiates TLS and
         // refuses to continue against a server that has none: "SSL is required, but
         // the server does not support it". That is the right default and it is not
         // changed here - a database connection that quietly downgraded to plaintext
         // would be the worst kind of configuration drift. A server that genuinely has
         // no TLS is a choice written in the ini instead: [Database]
         // AllowUnencryptedConnection=1 asks the client to prefer TLS and fall back,
         // which is what the old libmysql did without asking. Certificate
         // verification is left at the client's default either way; upstream #559
         // switches both off unconditionally.
         if (IniFileSettings::Instance()->GetDatabaseAllowUnencryptedConnection() &&
             MySQLInterface::Instance()->p_mysql_options != 0)
         {
            char enforceTls = 0;
            MySQLInterface::Instance()->p_mysql_options(dbconn_, HM_MYSQL_OPT_SSL_ENFORCE, &enforceTls);
         }

         //MYSQL *pResult = mysql_real_connect(
         hm_MYSQL *pResult = MySQLInterface::Instance()->p_mysql_real_connect(
                     dbconn_,
                     Unicode::ToANSI(sServer),
                     Unicode::ToANSI(sUsername),
                     Unicode::ToANSI(sPassword),
                     Unicode::ToANSI(sDatabase), lDBPort, 0, 0);

         if (pResult == 0)
         {
            // From MySQL manual:
            //
            // Return Values:
            //
            // A MYSQL* connection handle if the connection was successful, NULL if the connection was
            // unsuccessful. For a successful connection, the return value is the same as the value
            // of the first parameter.

            const char *pError = MySQLInterface::Instance()->p_mysql_error(dbconn_);
            sErrorMessage = pError;

            // The bundled MariaDB Connector/C client ships the auth plugins for every
            // common MySQL/MariaDB account type, so "Authentication plugin '<x>' cannot
            // be loaded" should now only happen if the Bin\plugin folder is missing or
            // incomplete (a broken install). GSSAPI/SSPI "Windows authentication"
            // additionally requires the hMailServer service's Windows identity to match
            // the database account, which a service running as LocalSystem/NT SERVICE
            // normally will not. In either case, translate the raw client error into
            // actionable guidance instead of dead-ending the setup wizard.
            String sLowerError = pError;
            sLowerError.MakeLower();

            // The client's own wording, which says what happened but not what to do.
            if (sLowerError.Find(_T("ssl is required")) >= 0)
            {
               sErrorMessage += _T("\r\n\r\n"
                  "The database server did not offer TLS, and the bundled MariaDB Connector/C requires it by "
                  "default. Enable TLS on the database server - or, if it genuinely has none, set "
                  "AllowUnencryptedConnection=1 under [Database] in hMailServer.ini to let the client fall "
                  "back to an unencrypted connection.");
            }
            if (sLowerError.Find(_T("plugin")) >= 0 &&
                (sLowerError.Find(_T("cannot be loaded")) >= 0 || sLowerError.Find(_T("gssapi")) >= 0))
            {
               sErrorMessage += _T("\r\n\r\n"
                  "The database account uses an authentication method this hMailServer install could not "
                  "complete. If the message mentions a plugin that 'cannot be loaded', the Bin\\plugin "
                  "folder shipped with hMailServer is missing - reinstall/repair so the bundled MariaDB "
                  "Connector/C auth plugins are present. If it mentions GSSAPI/Windows authentication, the "
                  "hMailServer service's Windows identity must match the database account; the simplest fix "
                  "is to connect with a normal user name and password instead. To convert an account to "
                  "password authentication on the server:\r\n"
                  "    ALTER USER 'user'@'host' IDENTIFIED WITH mysql_native_password BY 'password';   -- MySQL\r\n"
                  "    ALTER USER 'user'@'host' IDENTIFIED VIA mysql_native_password USING PASSWORD('password');   -- MariaDB");
            }

            return TemporaryFailure;
         }

         if (CheckError(pResult, "mysql_real_connect()", sErrorMessage) != DALConnection::DALSuccess)
            return TemporaryFailure;

         SetConnectionCharacterSet_();

         if (!sDatabase.IsEmpty())
         {
            String switch_db_command = "use " + sDatabase;

            if (TryExecute(SQLCommand(switch_db_command), sErrorMessage, 0, 0) != DALConnection::DALSuccess)
            {
               return TemporaryFailure;
            }
         }

         LoadSupportsTransactions_(sDatabase);

         is_connected_ = true;

         // Both of these run statements, so they go after is_connected_. The
         // mechanism is worked out once per connection and then applied; the session
         // variable dies with the session, so Reconnect() - which calls Connect() -
         // is what puts it back after a dropped connection.
         LoadTimeoutMechanism_();
         ApplyDefaultTimeout();
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5008, "MySQLConnection::Connect", "An unhandled error occurred when connecting to the database.");
         return TemporaryFailure;
      }

      return Connected;
   }

   bool
   MySQLConnection::CheckServerVersion(String &errorMessage)
   {
      // check server version.
      int serverVersion = MySQLInterface::Instance()->p_mysql_get_server_version(dbconn_);
      if (serverVersion < RequiredVersion)
      {
         errorMessage = "hMailServer requires MySQL 4.1.18 or newer. If you are using the internal MySQL database, please upgrade to the latest 4.x version prior to upgrading to version 5 or later.";
         return false;
      }

      return true;
   }

   bool
   MySQLConnection::Disconnect()
   {
      if (dbconn_)
      {
         MySQLInterface::Instance()->p_mysql_close(dbconn_);
         dbconn_ = 0;
      }

      return true;
   }

   DALConnection::ExecutionResult
   MySQLConnection::TryExecute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID, int iIgnoreErrors)
   {
      String SQL = command.GetQueryString();
      try
      {
         // mysql_query-doc:
         // Zero if the query was successful. Non-zero if an error occurred.
         //
         AnsiString sQuery;
         if (!Unicode::WideToMultiByte(SQL, sQuery))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5105, "MySQLConnection::TryExecute", "Could not convert string into multi-byte.");
            return DALConnection::DALUnknown;
         }

         if (MySQLInterface::Instance()->p_mysql_query(dbconn_, sQuery))
         {
            // Classified first, because whether the marker may discard this
            // failure depends on what the failure is. CheckError is the only
            // thing on this backend that recognises a dropped connection (2006
            // "server has gone away" / 2013 "lost connection"); GetErrorType_
            // does not, so it cannot be used for that test. Its own error text
            // goes into a local so that a discarded failure does not leave a
            // message behind in the caller's buffer.
            String checkErrorMessage;
            DALConnection::ExecutionResult result = CheckError(dbconn_, SQL, checkErrorMessage);

            if (result != DALConnection::DALSuccess)
            {
               // "This object may already exist" - never "the server did not
               // hear the statement". See DALConnection::HasIgnoreErrorsMarker.
               bool ignoreByMarker = result != DALConnection::DALConnectionProblem &&
                                     HasIgnoreErrorsMarker(SQL);

               bool ignoreByCaller = iIgnoreErrors != 0 &&
                                     (GetErrorType_(dbconn_) & iIgnoreErrors) != 0;

               if (!ignoreByMarker && !ignoreByCaller)
               {
                  sErrorMessage = checkErrorMessage;
                  return result;
               }
            }
         }

         hm_MYSQL_RES *pRes = MySQLInterface::Instance()->p_mysql_store_result(dbconn_); // should always be called after mysql_query

         if (pRes)
            MySQLInterface::Instance()->p_mysql_free_result(pRes);

         // Fetch insert id.
         if (iInsertID > 0)
         {
            *iInsertID = MySQLInterface::Instance()->p_mysql_insert_id(dbconn_);
         }
      }
      catch (...)
      {
         sErrorMessage = "Source: MySQLConnection::TryExecute, Code: HM10048, Description: An unhandled error occurred while executing: " + SQL;
         return DALConnection::DALUnknown;
      }

      return DALConnection::DALSuccess;
   }

   bool
   MySQLConnection::IsConnected() const
   {
      return is_connected_;
   }

   hm_MYSQL*
   MySQLConnection::GetConnection() const
   {
      return dbconn_;
   }

   DALConnection::ExecutionResult
   MySQLConnection::GetErrorType_(hm_MYSQL *pSQL)
   {
      try
      {
         if (pSQL==NULL)
            return DALSuccess;

         int iErrNo = MySQLInterface::Instance()->p_mysql_errno(pSQL);

         switch (iErrNo)
         {
         case 0:
            return DALSuccess;
         case 1062: // ER_DUP_ENTRY - Message: Duplicate entry '%s' for key %d
            return DALErrorInSQL;
         default:
            return DALUnknown;
         }

         HM_ASSERT(0); // Should never get here
         return DALSuccess;
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 4373, "MySQLConnection::_GetErrorNumber", "An error occurred while trying to retrieve error code from MySQL.");
         return DALErrorInSQL;
      }

      return DALSuccess;

   }

   DALConnection::ExecutionResult
   MySQLConnection::CheckError(hm_MYSQL *pSQL, const String &sAdditionalInfo, String &sOutputErrorMessage) const
   {
      try
      {
         if (pSQL==NULL)
            return DALConnection::DALSuccess;

         const char *pError = MySQLInterface::Instance()->p_mysql_error(pSQL);
         if (!pError[0] != '\0')
            return DALConnection::DALSuccess;


         DALConnection::ExecutionResult result = DALConnection::DALUnknown;

         int errorCode = MySQLInterface::Instance()->p_mysql_errno(pSQL);
         switch (errorCode)
         {
         case 2006: // MySQL server has gone away
         case 2013: // Lost connection to MySQL server during query
            result = DALConnection::DALConnectionProblem;
            break;
         }


         AnsiString sMySqlErrorAnsi = pError;
         String sMySQLErrorUnicode = sMySqlErrorAnsi;

         String sErrorMessage;
         sErrorMessage.Format(_T("MySQL: %s (Additional info: %s)"), sMySQLErrorUnicode.c_str(), sAdditionalInfo.c_str());

         sOutputErrorMessage = sErrorMessage;

         return result;
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5009, "MySQLConnection::CheckError", "An unhandled error occurred while checking for errors.");
         return DALConnection::DALUnknown;
      }
   }

   void
   MySQLConnection::OnConnected()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // This would need refactoring some day. This is the place
   // where the internal MySQL database structure is managed.
   // The update of the data tables is taken care of by the
   // installation program, but the mysql.* tables are updated
   // here.
   //---------------------------------------------------------------------------()
   {
      // Check if the user is using the internal database. We don't rely
      // entirely on the [Database]->Internal setting in hMailServer.ini so
      // we check a few other properties as well.
      if (IniFileSettings::Instance()->GetDatabasePort() != 3307 ||
          IniFileSettings::Instance()->GetUsername().CompareNoCase(_T("root")) != 0 &&
          IniFileSettings::Instance()->GetUsername().CompareNoCase(_T("hmailserver")) != 0 &&
          IniFileSettings::Instance()->GetIsInternalDatabase())
      {
         // The user is not using the internal database.
         return;
      }

      // Remove dummy user created after installation.
      UpdatePassword_();

      // Run the scripts file
      String sScriptsFile = IniFileSettings::Instance()->GetDBScriptDirectory() + "\\Internal MySQL\\HMS4.3-MySQL4.1.18.sql";
      RunScriptFile_(sScriptsFile);

      RunCommand_("FLUSH PRIVILEGES");
   }

   void
   MySQLConnection::UpdatePassword_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Remoevs any user that lacks user name. Used to tighten security on the internal
   // database.
   //---------------------------------------------------------------------------()
   {
      // Remove the dummy user.
      RunCommand_("DELETE FROM mysql.user WHERE User = ''");
   }

   void
   MySQLConnection::RunScriptFile_(const String &sFile)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Runs a SQL script which contains commands separated with semicolons. This
   // function will always succeed, so should only be used for non-important
   // SQL epressions
   //---------------------------------------------------------------------------()
   {
#ifndef _DISABLE_MYSQL_AUTOUPGRADE
      String sContents = FileUtilities::ReadCompleteTextFile(sFile);

      std::vector<String> vecCommands = StringParser::SplitString(sContents, ";");

      auto iterCommand = vecCommands.begin();
      auto iterEnd = vecCommands.end();
      for (; iterCommand != iterEnd; iterCommand++)
      {
         String sSQL = (*iterCommand);

         sSQL.TrimLeft(_T("\r\n "));
         sSQL.TrimRight(_T("\r\n "));

         if (!sSQL.IsEmpty())
            RunCommand_(sSQL);
      }
#endif
   }

   void
   MySQLConnection::RunCommand_(const String &sCommand)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Runs a single SQL command without any error handling.
   //---------------------------------------------------------------------------()
   {
      String sError;

      TryExecute(SQLCommand(sCommand), sError, 0);
   }

   bool
   MySQLConnection::BeginTransaction(String &sErrorMessage)
   {
      if (supports_transactions_)
      {
         return TryExecute(SQLCommand("BEGIN"), sErrorMessage, 0)  == DALSuccess;
      }

      return true;
   }

   bool
   MySQLConnection::CommitTransaction(String &sErrorMessage)
   {
      if (supports_transactions_)
      {
         return TryExecute(SQLCommand("COMMIT"), sErrorMessage, 0)  == DALSuccess;
      }


      return true;
   }

   bool
   MySQLConnection::RollbackTransaction(String &sErrorMessage)
   {
      if (supports_transactions_)
      {
         return TryExecute(SQLCommand("ROLLBACK"), sErrorMessage, 0)  == DALSuccess;
      }
      else
      {
         sErrorMessage = "Rollback of MySQL statements failed. You may need to restore the latest database backup to ensure database integrity";
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5104, "MySQLConnection::RollbackTransaction", sErrorMessage);

         return false;
      }
   }

   void
   MySQLConnection::LoadSupportsTransactions_(const String &database)
   {
      supports_transactions_ = false;

      if (database.GetLength() == 0)
         return;

      MySQLRecordset rec;
      if (!rec.Open(shared_from_this(), SQLCommand("SHOW TABLE STATUS in " + database)))
         return;

      int tableCount = 0;

      while (!rec.IsEOF())
      {
         String sEngine = rec.GetStringValue("Engine");
         if (sEngine.CompareNoCase(_T("InnoDB")) != 0)
         {
            return;
         }

         tableCount++;

         rec.MoveNext();
      }

      if (tableCount > 0)
      {
         // Only InnoDB tables in this database. Enable transactions.
         supports_transactions_ = true;
      }
   }

   void
   MySQLConnection::SetConnectionCharacterSet_()
   {
      std::set<String> utf_character_sets;

      MySQLRecordset rec;
      if (!rec.Open(shared_from_this(), SQLCommand("SHOW CHARACTER SET LIKE 'UTF%'")))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5008, "MySQLConnection::LoadConnectionCharacterSet_", "Unable to find appropriate MySQL character set. Command SHOW CHARACTER SET LIKE 'UTF%' failed.");
         return;
      }


      while (!rec.IsEOF())
      {
         String character_set  = rec.GetStringValue("Charset");
         utf_character_sets.insert(character_set);
         rec.MoveNext();
      }

      String character_set_to_use;

      if (utf_character_sets.find("utf8mb4") != utf_character_sets.end())
         character_set_to_use = "utf8mb4";
      else if (utf_character_sets.find("utf8") != utf_character_sets.end())
         character_set_to_use = "utf8";
      else
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5008, "MySQLConnection::LoadConnectionCharacterSet_", "Unable to find appropriate MySQL character set.");
         return;
      }

      String error_message;
      AnsiString set_names_command = Formatter::Format("SET NAMES {0}", character_set_to_use);

      if (TryExecute(SQLCommand(set_names_command), error_message, 0, 0) != DALConnection::DALSuccess)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5008, "MySQLConnection::LoadConnectionCharacterSet_", set_names_command);
      }
   }

   bool
   MySQLConnection::TrySetTimeoutVariable_(const String &variableName, const String &value)
   {
      String command;
      command.Format(_T("SET SESSION %s = %s"), variableName.c_str(), value.c_str());

      String errorMessage;

      return TryExecute(SQLCommand(command), errorMessage, 0, 0) == DALConnection::DALSuccess;
   }

   void
   MySQLConnection::LoadTimeoutMechanism_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Decides which session variable this server uses to bound statement execution
   // time, and in which unit. See the comment on MySQLConnection::SetTimeout.
   //---------------------------------------------------------------------------()
   {
      timeout_variable_ = "";
      timeout_variable_is_seconds_ = false;

      // The fork is read from VERSION() rather than from
      // mysql_get_server_version, on purpose. MariaDB still announces itself in the
      // handshake as "5.5.5-<real version>-MariaDB" for the benefit of very old
      // clients, and a client library that parses that literally reports MariaDB
      // 10.6 as version 50505 - which would send this down the MySQL branch and pick
      // the wrong unit. VERSION() is answered by the server itself and carries no
      // such prefix.
      bool is_maria_db = false;

      MySQLRecordset rec;
      if (rec.Open(shared_from_this(), SQLCommand("SELECT VERSION() AS serverversion")) && !rec.IsEOF())
      {
         String version = rec.GetStringValue("serverversion");
         version.MakeLower();

         is_maria_db = version.Find(_T("mariadb")) >= 0;
      }
      else
      {
         // Without an answer there is no safe choice: the two forks disagree about
         // what "30" means for max_statement_time, so guessing is how every
         // statement ends up being aborted after 30 milliseconds. No mechanism is
         // the honest outcome.
         LOG_APPLICATION("The MySQL server version could not be read, so no statement timeout has been "
                         "set on this connection.");
         return;
      }

      if (is_maria_db)
      {
         if (TrySetTimeoutVariable_(_T("max_statement_time"), _T("0")))
         {
            timeout_variable_ = _T("max_statement_time");
            timeout_variable_is_seconds_ = true;
            return;
         }
      }
      else
      {
         if (TrySetTimeoutVariable_(_T("max_execution_time"), _T("0")))
         {
            timeout_variable_ = _T("max_execution_time");
            timeout_variable_is_seconds_ = false;
            return;
         }

         // MySQL 5.7.4 to 5.7.7 only. Milliseconds there too - this branch is never
         // reached on MariaDB, which is the fork where the same name means seconds.
         if (TrySetTimeoutVariable_(_T("max_statement_time"), _T("0")))
         {
            timeout_variable_ = _T("max_statement_time");
            timeout_variable_is_seconds_ = false;
            return;
         }
      }

      // Not an error: MySQL before 5.7.4 and MariaDB before 10.1.1 simply do not
      // have one, and hMailServer still supports them. Said once per connection so
      // that an administrator wondering why DatabaseStatementTimeout has no effect
      // can find out why.
      LOG_APPLICATION("This MySQL server has no statement-timeout variable (MariaDB 10.1.1 or MySQL 5.7.4 "
                      "and later have one), so DatabaseStatementTimeout does not apply to it.");
   }

   void
   MySQLConnection::SetTimeout(int seconds)
   {
      if (dbconn_ == 0 || !is_connected_ || timeout_variable_.IsEmpty())
         return;

      // 0 is "no limit" for both variables, so a caller asking for no timeout gets
      // the server's own behaviour rather than a very small one.
      __int64 value = 0;

      if (seconds > 0)
      {
         if (timeout_variable_is_seconds_)
         {
            value = seconds;
         }
         else
         {
            // Milliseconds, capped at what the variable can hold. MySQL's
            // max_execution_time is an unsigned 32-bit value; 1800 seconds - what
            // SQLScriptRunner asks for - is well inside it, and the cap is here for
            // a nonsense value in the ini rather than for anything this server does.
            const __int64 maximum_milliseconds = 4294967295LL;

            value = (__int64) seconds * 1000;

            if (value > maximum_milliseconds)
               value = maximum_milliseconds;
         }
      }

      String valueText;
      valueText.Format(_T("%I64d"), value);

      if (!TrySetTimeoutVariable_(timeout_variable_, valueText))
      {
         LOG_APPLICATION("The MySQL " + timeout_variable_ + " could not be set on this connection, so "
                         "statements on it are not time-limited.");
      }
   }

   std::shared_ptr<DALRecordset>
   MySQLConnection::CreateRecordset()
   {
      std::shared_ptr<MySQLRecordset> recordset = std::shared_ptr<MySQLRecordset>(new MySQLRecordset());
      return recordset;
   }

   void
   MySQLConnection::EscapeString(String &sInput)
   {
      sInput.Replace(_T("'"), _T("''"));
      sInput.Replace(_T("\\"), _T("\\\\"));
   }

   std::shared_ptr<IMacroExpander>
   MySQLConnection::CreateMacroExpander()
   {
      std::shared_ptr<MySQLMacroExpander> expander = std::shared_ptr<MySQLMacroExpander>(new MySQLMacroExpander());
      return expander;
   }
}