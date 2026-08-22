// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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
         libpq has no per-statement timeout API, so the mechanism is the server
         side: statement_timeout, a GUC expressed in MILLISECONDS, which aborts
         any statement running longer with SQLSTATE 57014. 0 disables it, which
         is also PostgreSQL's own default.

         It is a SESSION setting, and this connection is pooled, so it does not
         belong to whichever caller set it last - which is the objection that
         kept this empty. That is answered by giving the server one limit,
         [Settings] DatabaseStatementTimeout, applying it at the end of Connect()
         so every pooled connection carries it from the moment it exists, and
         having SQLScriptRunner restore that same limit instead of the literal 30
         it used to restore. Reconnect() calls Connect(), so it survives a dropped
         connection too - which matters here more than on the ADO backends,
         because a GUC lives in the server-side session and dies with it.

         A failure to set it is logged and otherwise ignored: a connection that
         works is worth more than a timeout that does not, and a role without
         permission to SET statement_timeout is a configuration this server has
         no business refusing to run on.

         One caveat worth knowing before calling this from somewhere new: a plain
         SET inside an open transaction is TRANSACTION-local on PostgreSQL and
         reverts when that transaction ends. Neither of the two callers is inside
         one - Connect() runs before anything, and SQLScriptRunner is handed a
         connection from the pool and opens no transaction of its own - so the
         value is a session value in practice. A future caller inside a
         transaction would get a limit that quietly disappears at COMMIT.
      */
      virtual void SetTimeout(int seconds);

      virtual bool CheckServerVersion(String &errorMessage);

      virtual std::shared_ptr<DALRecordset> CreateRecordset();

      virtual void EscapeString(String &sInput);

      virtual std::shared_ptr<IMacroExpander> CreateMacroExpander();

   private:

  
      PGconn *dbconn_;

      bool is_connected_;
   };

}
