// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "DALConnection.h"
#include "MySQLInterface.h"
#include "ColumnPositionCache.h"

namespace HM
{
   

   class MySQLConnection : public DALConnection, public std::enable_shared_from_this<MySQLConnection>
   {
   public:

      enum Server
      {
         RequiredVersion = 40118
      };

	   MySQLConnection(std::shared_ptr<DatabaseSettings> pSettings);
	   virtual ~MySQLConnection();

      virtual ConnectionResult Connect(String &sErrorMessage);
      virtual bool Disconnect();
      virtual ExecutionResult TryExecute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID = 0, int iIgnoreErrors = 0); 
      virtual bool IsConnected() const;

      hm_MYSQL *GetConnection() const;

      ExecutionResult CheckError(hm_MYSQL *pSQL, const String &sAdditionalInfo, String &sOutputErrorMessage) const;

      virtual void OnConnected();

      virtual bool BeginTransaction(String &sErrorMessage);
      virtual bool CommitTransaction(String &sErrorMessage);
      virtual bool RollbackTransaction(String &sErrorMessage);

      /*
         The MySQL family has no client-side statement timeout either, and the two
         mysql_options values that look like one - MYSQL_OPT_READ_TIMEOUT and
         MYSQL_OPT_WRITE_TIMEOUT - are socket timeouts, not statement timeouts:
         they fire on a silent socket, so a server that is busily working on a
         long statement never trips them, and when they do fire the statement is
         still running on the server while the client believes the connection is
         gone. They are deliberately not used here.

         What exists is a server-side session variable, and it is spelled
         differently and measured differently by the two forks:

            MariaDB 10.1.1+     max_statement_time    SECONDS
            MySQL 5.7.8+        max_execution_time    MILLISECONDS
            MySQL 5.7.4-5.7.7   max_statement_time    MILLISECONDS

         That last row is why the fork is identified before the variable is
         chosen rather than the variable simply being probed for: MariaDB and
         early MySQL agree on the NAME and disagree on the UNIT, so a server told
         "30" meaning seconds would abort every statement after 30 milliseconds.
         Getting it wrong in the other direction is silent - an unknown system
         variable is an error, so nothing would be set and nothing would say so -
         and both are worse than the mechanism being absent, so LoadTimeoutMechanism_
         identifies the fork from VERSION(), then confirms the variable exists by
         setting it to 0 (no limit) before anything relies on it.

         The honest limit, stated because it is easy to assume otherwise: MySQL's
         max_execution_time applies only to read-only SELECT statements. On MySQL a
         blocked UPDATE or DELETE still holds its pooled connection. MariaDB's
         max_statement_time covers statements generally, and PostgreSQL's
         statement_timeout covers all of them.
      */
      virtual void SetTimeout(int seconds);

      virtual bool GetSupportsCommandParameters() const {return false; }
      ColumnPositionCache& GetColumnPositionCache() {return column_position_cache_;}

      virtual bool CheckServerVersion(String &errorMessage);

      virtual std::shared_ptr<DALRecordset> CreateRecordset();

      virtual void EscapeString(String &sInput);

      virtual std::shared_ptr<IMacroExpander> CreateMacroExpander();

   private:

      DALConnection::ExecutionResult GetErrorType_(hm_MYSQL *pSQL);

      void UpdatePassword_();
      void RunScriptFile_(const String &sFile) ;
      void RunCommand_(const String &sCommand) ;
      void LoadSupportsTransactions_(const String &database);
      void SetConnectionCharacterSet_();

      // Works out which session variable this server understands, and in which
      // unit, and proves it by setting it. Run once per connection, from Connect().
      void LoadTimeoutMechanism_();

      // True when "SET SESSION <variable> = 0" was accepted, i.e. the variable
      // exists on this server. 0 means "no limit" on every version that has any of
      // these variables, so the probe cannot leave a limit behind.
      bool TrySetTimeoutVariable_(const String &variableName, const String &value);

      hm_MYSQL *dbconn_;

      bool is_connected_;
      bool supports_transactions_;

      // Empty when this server has no statement-timeout variable at all, in which
      // case SetTimeout does nothing and said so once at connect time.
      String timeout_variable_;
      bool timeout_variable_is_seconds_;

      ColumnPositionCache column_position_cache_;

   };

}
