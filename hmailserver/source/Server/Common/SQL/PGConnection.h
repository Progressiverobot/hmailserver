// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include <libpq-fe.h>

#include "DALConnection.h"

namespace HM
{
   class PGConnection : public DALConnection
   {
   public:
	   PGConnection(std::shared_ptr<DatabaseSettings> pSettings);
	   virtual ~PGConnection();

      virtual ConnectionResult Connect(String &sErrorMessage);
      virtual bool Disconnect();
      virtual ExecutionResult TryExecute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID = 0, int iIgnoreErrors = 0); 
      virtual bool IsConnected() const;

      DALConnection::ExecutionResult CheckError(PGresult *pResult, const String &sAdditionalInfo, String &sOutputErrorMessage) const;
      PGconn *GetConnection() const;

      virtual bool GetSupportsCommandParameters() const {return false; }
      virtual void OnConnected();

      virtual bool BeginTransaction(String &sErrorMessage);
      virtual bool CommitTransaction(String &sErrorMessage);
      virtual bool RollbackTransaction(String &sErrorMessage);
      /*
         Deliberately does nothing, which means PostgreSQL has no statement
         timeout at all: a statement blocked on a lock holds its pooled
         connection until the server or the network gives up, and since the pool
         is fixed-size that is how the pool empties. MySQL is in the same
         position; only the two ADO backends implement this.

         It is not a one-line fix, which is why it is written down rather than
         done. The only caller is SQLScriptRunner, which raises the limit to 30
         minutes for a schema script and drops it to 30 seconds afterwards. The
         natural implementation here - "SET statement_timeout" - is a *session*
         setting on a pooled connection, so that 30 would then abort every
         subsequent statement on this connection that ran longer, for the life of
         the connection: a large delete during a domain removal, or a backup
         sweep, would start failing on a server that had merely run an upgrade
         script. Giving PostgreSQL and MySQL a real statement timeout means
         deciding what the server-wide limit should be and applying it at
         connect time, not borrowing SQLScriptRunner's.
      */
      virtual void SetTimeout(int seconds) {}

      virtual bool CheckServerVersion(String &errorMessage);

      virtual std::shared_ptr<DALRecordset> CreateRecordset();

      virtual void EscapeString(String &sInput);

      virtual std::shared_ptr<IMacroExpander> CreateMacroExpander();

   private:

  
      PGconn *dbconn_;

      bool is_connected_;
   };

}
