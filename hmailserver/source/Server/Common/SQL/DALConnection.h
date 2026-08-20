// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class DatabaseSettings;
   class MacroExpander;
   class IMacroExpander;

   class DALConnection  
   {
   public:
	   DALConnection(std::shared_ptr<DatabaseSettings> pDatabaseSettings);
	   virtual ~DALConnection();

      enum ConnectionResult
      {
         Connected = 1,
         TemporaryFailure = 2,
         FatalError = 3
      };

      enum ExecutionResult
      {
         DALSuccess = 0,
         DALErrorInSQL = 1,
         DALConnectionProblem = 2,
         DALUnknown = 3
      };

      bool Execute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID = 0, int iIgnoreErrors = 0);

      bool Reconnect(String &sErrorMessage);

      // True when the statement carries the DAL's [IGNORE-ERRORS] marker in a
      // position where it means what it is meant to mean.
      //
      // (Line comments rather than a block comment on purpose: this text has to
      // name the SQL comment forms the marker is written in, one of which ends a
      // C block comment, so a /* */ block here closes itself twenty lines early
      // and the rest of the prose is compiled as code.)
      //
      // The marker exists so that re-running an upgrade step whose column, table
      // or index already exists is harmless on the three backends whose DDL has
      // no IF NOT EXISTS form (MSSQL, SQL CE, MySQL; the PostgreSQL scripts use
      // IF NOT EXISTS and carry no marker). Every use of it in the tree - six
      // upgrade scripts and SqlLogDevice's create statements - writes it inside a
      // trailing SQL comment, either a "---" line comment or a slash-star block.
      //
      // It used to be looked for with a plain substring search over the whole
      // statement, which meant a *value* carrying that text switched error
      // handling off for the statement it appeared in. hm_message_metadata and
      // the SQL log device both write attacker-supplied text - a subject line, a
      // logged protocol command - so that was reachable from outside, and what it
      // bought the attacker was one silently discarded write.
      //
      // This scan therefore skips over single-quoted string literals (handling
      // both the doubled '' and the MySQL backslash escape, so a literal is not
      // closed early) and honours the marker only where it appears inside a
      // comment. A value can reach neither.
      //
      // What it does NOT decide is which errors are then discarded; see the
      // callers. A lost connection is never discarded, because "the statement was
      // already applied" and "the statement never reached the server" are not the
      // same thing, and it was the second one masquerading as the first that
      // stamped schema version 6006 onto databases that had not got the column.
      static bool HasIgnoreErrorsMarker(const String &sql);

      virtual ConnectionResult Connect(String &sErrorMessage) = 0;
      virtual bool Disconnect() = 0;
      virtual ExecutionResult TryExecute(const SQLCommand &command, String &sErrorMessage, __int64 *iInsertID = 0, int iIgnoreErrors = 0) = 0;
      virtual bool IsConnected() const = 0;
      virtual void OnConnected() = 0;

      virtual bool BeginTransaction(String &sErrorMessage) = 0;
      virtual bool CommitTransaction(String &sErrorMessage) = 0;
      virtual bool RollbackTransaction(String &sErrorMessage) = 0;
      virtual void SetTimeout(int seconds) = 0;

      // Puts the connection back on the server-wide statement timeout, [Settings]
      // DatabaseStatementTimeout.
      //
      // There has to be a "back to normal" for the same reason SQLScriptRunner has a
      // scope guard: the timeout is a property of a POOLED connection on all four
      // backends - ADO's CommandTimeout, and a session GUC/variable on PostgreSQL and
      // MySQL - so whatever the last caller left it at is what the next unrelated
      // statement inherits. SQLScriptRunner used to restore the literal 30, which was
      // right only because 30 happens to be ADO's own default; with the value now
      // configurable, and with PostgreSQL and MySQL having no default of their own,
      // it has to come from one place.
      void ApplyDefaultTimeout();

      void SetTryCount(int iTryCount) {try_count_ = iTryCount; }
      

      virtual bool GetSupportsCommandParameters() const = 0;

	   virtual bool CheckServerVersion(String &errorMessage) = 0;

      // Implemented by all four backends and called by none of them. The
      // escaping that actually runs is the static SQLStatement::Escape, which
      // decides what to double from IniFileSettings rather than from the
      // connection - the same setting DALConnectionFactory used to pick the
      // backend, so the two cannot disagree. Kept because removing a pure
      // virtual touches every backend for no behaviour; do not reach for it
      // thinking it is the escaping path.
      virtual void EscapeString(String &sInput) = 0;

      virtual std::shared_ptr<DALRecordset> CreateRecordset() = 0;
      virtual std::shared_ptr<IMacroExpander> CreateMacroExpander() = 0;

      std::shared_ptr<DatabaseSettings> GetSettings() {return database_settings_; }

   protected:

      std::shared_ptr<DatabaseSettings> database_settings_;

   private:

      int try_count_;
   };

}
